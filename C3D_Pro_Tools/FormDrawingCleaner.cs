using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// Disambiguation Aliases
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    /// <summary>
    /// C3D Drawing Cleaner: chooses what to strip from a drawing and hands it to <see cref="DrawingCleanup"/>. With "save a
    /// cleaned copy" (the default) the drawing on screen is never changed.
    /// </summary>
    public partial class FormDrawingCleaner : Form
    {
        private int _corridorCount = 0;
        private int _assemblyCount = 0;
        private int _surfaceCount = 0;
        private int _alignmentCount = 0;
        private int _cogoPointCount = 0;
        private int _featureLineCount = 0;
        private int _parcelCatchmentCount = 0;
        private int _civilLabelTableCount = 0;
        private int _annotationCount = 0;
        private int _blockCount = 0;
        private int _xrefCount = 0;
        private int _cadGeometryCount = 0;
        private int _sheetCount = 0;

        /// <summary>List text → network name, for the networks shown in the two lists.</summary>
        private readonly Dictionary<string, string> _gravityNetMap = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _pressureNetMap = new Dictionary<string, string>();

        private static readonly (string text, XrefHandling value)[] XrefChoices =
        {
            ("Keep attached", XrefHandling.Keep),
            ("Detach unreferenced", XrefHandling.DetachUnreferenced),
            ("Detach all", XrefHandling.DetachAll),
            ("Bind (insert)", XrefHandling.Bind)
        };

        private static readonly (string text, DataReferenceHandling value)[] DataReferenceChoices =
        {
            ("Leave as they are", DataReferenceHandling.Leave),
            ("Strip (delete references)", DataReferenceHandling.Strip)
        };

        public FormDrawingCleaner()
        {
            InitializeComponent();
            foreach (var c in XrefChoices) cbXrefs.Items.Add(c.text);
            foreach (var c in DataReferenceChoices) cbDataRefs.Items.Add(c.text);
            // As before: with blocks ticked the xrefs went too.
            cbXrefs.SelectedIndex = chkBlocksXrefs.Checked ? 2 : 0;
            cbDataRefs.SelectedIndex = 0;
            InitializeDefaultSavePath();
            ScanDrawingInventory();
        }

        private void InitializeDefaultSavePath()
        {
            string activeDwg = AcadApp.DocumentManager.MdiActiveDocument?.Name;
            if (!string.IsNullOrEmpty(activeDwg))
            {
                // A never-saved drawing ("Drawing1.dwg") has no folder; fall back to Documents instead of AutoCAD's working directory.
                string dir = Path.GetDirectoryName(activeDwg);
                if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(activeDwg))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string name = Path.GetFileNameWithoutExtension(activeDwg);
                txtSavePath.Text = Path.Combine(dir, $"{name}_Cleaned.dwg");
            }
        }

        private void chkSaveCopy_CheckedChanged(object sender, EventArgs e)
        {
            txtSavePath.Enabled = chkSaveCopy.Checked;
            btnBrowse.Enabled = chkSaveCopy.Checked;
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                FileName = Path.GetFileName(txtSavePath.Text)
            })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                    txtSavePath.Text = sfd.FileName;
            }
        }

        private void btnPresetNetworksOnly_Click(object sender, EventArgs e) => ApplyPreset(networksOnly: true);
        private void btnSelectAll_Click(object sender, EventArgs e) => ApplyPreset(checkAll: true);
        private void btnClearAll_Click(object sender, EventArgs e) => ApplyPreset(checkAll: false);
        private void btnCancel_Click(object sender, EventArgs e) => this.Close();

        /// <summary>
        /// Step 1: Scans and classifies every single object in ModelSpace and Database so 0 objects go undetected.
        /// </summary>
        private void ScanDrawingInventory()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument?.Database;
            if (doc == null || db == null) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                _surfaceCount = doc.GetSurfaceIds().Count;
                _alignmentCount = doc.GetAlignmentIds().Count;
                _corridorCount = doc.CorridorCollection.Count;
                _assemblyCount = doc.AssemblyCollection.Count;
                _cogoPointCount = 0;
                _featureLineCount = 0;
                _parcelCatchmentCount = 0;
                _civilLabelTableCount = 0;
                _annotationCount = 0;
                _blockCount = 0;
                _xrefCount = 0;
                _cadGeometryCount = 0;

                // Scan ModelSpace and classify EVERY entity
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    DBObject obj;
                    try { obj = tr.GetObject(id, OpenMode.ForRead); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (obj == null || obj.IsErased) continue;

                    switch (DrawingCleanup.KindOf(obj))
                    {
                        case DrawingCleanup.Kind.LabelOrTable: _civilLabelTableCount++; break;
                        // Corridors, assemblies, surfaces and alignments are counted from their collections.
                        case DrawingCleanup.Kind.AlignmentOrView: if (!(obj is Alignment)) _alignmentCount++; break;
                        case DrawingCleanup.Kind.CogoPoint: _cogoPointCount++; break;
                        case DrawingCleanup.Kind.FeatureLineOrGrading: _featureLineCount++; break;
                        case DrawingCleanup.Kind.ParcelOrCatchment: _parcelCatchmentCount++; break;
                        case DrawingCleanup.Kind.Annotation: _annotationCount++; break;
                        case DrawingCleanup.Kind.Block: _blockCount++; break;
                        // Catches all lines, polylines, hatches, 3D solids, meshes, proxies, etc.
                        case DrawingCleanup.Kind.CadGeometry: _cadGeometryCount++; break;
                    }
                }

                // Also count site-based parcels if not already in ModelSpace count
                foreach (ObjectId siteId in doc.GetSiteIds())
                {
                    if (tr.GetObject(siteId, OpenMode.ForRead) is Site site)
                        _parcelCatchmentCount += site.GetParcelIds().Count;
                }

                // Xref definitions in the block table
                foreach (ObjectId btrId in bt)
                {
                    if (tr.GetObject(btrId, OpenMode.ForRead) is BlockTableRecord btr && btr.IsFromExternalReference)
                        _xrefCount++;
                }

                // Count Paper Space Layouts
                _sheetCount = 0;
                DBDictionary layoutDict = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                if (layoutDict != null)
                {
                    foreach (DBDictionaryEntry entry in layoutDict)
                    {
                        if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                            _sheetCount++;
                    }
                }

                // Gravity Networks
                _gravityNetMap.Clear();
                clbGravityNets.Items.Clear();
                foreach (ObjectId netId in doc.GetPipeNetworkIds())
                {
                    if (tr.GetObject(netId, OpenMode.ForRead) is Network net)
                    {
                        string display = $"{net.Name} ({net.GetPipeIds().Count} pipes, {net.GetStructureIds().Count} structures)";
                        _gravityNetMap[display] = net.Name;
                        clbGravityNets.Items.Add(display, false); // Default unchecked = KEEP
                    }
                }

                // Pressure Networks
                _pressureNetMap.Clear();
                clbPressureNets.Items.Clear();
                foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
                {
                    if (tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork pnet)
                    {
                        string display = $"{pnet.Name} ({pnet.GetPipeIds().Count} pipes, {pnet.GetFittingIds().Count} fittings)";
                        _pressureNetMap[display] = pnet.Name;
                        clbPressureNets.Items.Add(display, false); // Default unchecked = KEEP
                    }
                }

                tr.Commit();
            }

            chkCorridors.Text = $"Corridors ({_corridorCount}) & Assemblies ({_assemblyCount})";
            chkSurfaces.Text = $"Surfaces (TIN / Grid / Volume) [{_surfaceCount}]";
            chkAlignments.Text = $"Alignments, Profiles, Views & Frames [{_alignmentCount}]";
            chkCogoPoints.Text = $"COGO Points & Point Groups [{_cogoPointCount}]";
            chkFeatureLines.Text = $"Feature Lines, Gradings & Sites [{_featureLineCount}]";
            chkParcels.Text = $"Parcels & Catchments [{_parcelCatchmentCount}]";
            chkLabelsTables.Text = $"All Civil 3D Labels & C3D Tables [{_civilLabelTableCount}]";
            chkAnnotations.Text = $"CAD Texts, Dimensions, Leaders & Tables [{_annotationCount}]";
            chkBlocksXrefs.Text = $"Blocks, Images & PDFs [{_blockCount}]";
            chkCadGeometry.Text = $"All 2D/3D CAD Lines, Hatches & Solids [{_cadGeometryCount}]";
            chkSheets.Text = $"Paper Space Layout Sheets [{_sheetCount}]";
            lblXrefs.Text = $"Xrefs [{_xrefCount}]:";
        }

        private void ApplyPreset(bool? networksOnly = null, bool? checkAll = null)
        {
            if (networksOnly == true)
            {
                chkCorridors.Checked = true;
                chkSurfaces.Checked = true;
                chkAlignments.Checked = true;
                chkCogoPoints.Checked = true;
                chkFeatureLines.Checked = true;
                chkParcels.Checked = true;
                chkLabelsTables.Checked = true;
                chkAnnotations.Checked = true;
                chkBlocksXrefs.Checked = true;
                chkCadGeometry.Checked = true;
                chkSheets.Checked = true;
                chkDeepPurge.Checked = true;
                cbXrefs.SelectedIndex = 2;

                // Uncheck all networks so they are KEPT
                for (int i = 0; i < clbGravityNets.Items.Count; i++) clbGravityNets.SetItemChecked(i, false);
                for (int i = 0; i < clbPressureNets.Items.Count; i++) clbPressureNets.SetItemChecked(i, false);
            }
            else if (checkAll.HasValue)
            {
                bool val = checkAll.Value;
                chkCorridors.Checked = val;
                chkSurfaces.Checked = val;
                chkAlignments.Checked = val;
                chkCogoPoints.Checked = val;
                chkFeatureLines.Checked = val;
                chkParcels.Checked = val;
                chkLabelsTables.Checked = val;
                chkAnnotations.Checked = val;
                chkBlocksXrefs.Checked = val;
                chkCadGeometry.Checked = val;
                chkSheets.Checked = val;
                chkDeepPurge.Checked = val;
                cbXrefs.SelectedIndex = val ? 2 : 0;

                for (int i = 0; i < clbGravityNets.Items.Count; i++) clbGravityNets.SetItemChecked(i, val);
                for (int i = 0; i < clbPressureNets.Items.Count; i++) clbPressureNets.SetItemChecked(i, val);
            }
        }

        private CleanupOptions ReadOptions()
        {
            CleanupOptions o = new CleanupOptions
            {
                Corridors = chkCorridors.Checked, Surfaces = chkSurfaces.Checked, Alignments = chkAlignments.Checked,
                CogoPoints = chkCogoPoints.Checked, FeatureLines = chkFeatureLines.Checked, Parcels = chkParcels.Checked,
                LabelsTables = chkLabelsTables.Checked, Annotations = chkAnnotations.Checked, Blocks = chkBlocksXrefs.Checked,
                CadGeometry = chkCadGeometry.Checked, Sheets = chkSheets.Checked, DeepPurge = chkDeepPurge.Checked,
                Xrefs = XrefChoices[Math.Max(0, cbXrefs.SelectedIndex)].value,
                DataReferences = DataReferenceChoices[Math.Max(0, cbDataRefs.SelectedIndex)].value
            };
            foreach (object item in clbGravityNets.CheckedItems)
                if (_gravityNetMap.TryGetValue(item.ToString(), out string name)) o.GravityNetworks.Add(name);
            foreach (object item in clbPressureNets.CheckedItems)
                if (_pressureNetMap.TryGetValue(item.ToString(), out string name)) o.PressureNetworks.Add(name);
            return o;
        }

        private void BtnExecute_Click(object sender, EventArgs e)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            string targetPath = txtSavePath.Text.Trim();
            if (chkSaveCopy.Checked && string.IsNullOrWhiteSpace(targetPath))
            {
                MessageBox.Show("Please choose a valid destination file path for the saved copy.");
                return;
            }

            DialogResult confirm = MessageBox.Show(
                "Ready to execute C3D Drawing Cleaner:\n\n" +
                "1. Delete all selected Civil 3D & AutoCAD elements\n" +
                "2. Clean Paper Space layout sheets & purge unused definitions\n" +
                (chkSaveCopy.Checked
                    ? $"3. Save the cleaned result as:\n{targetPath}\n\nThe drawing on screen is not changed: the cleaning is done on a copy of it (unsaved changes included)."
                    : "3. WARNING: Modifying active drawing directly without saving a copy!"),
                "Confirm C3D Drawing Cleaner",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            CleanupOptions options = ReadOptions();
            CleanupReport report;
            try
            {
                Cursor = Cursors.WaitCursor;
                report = chkSaveCopy.Checked ? DrawingCleanup.ExportCleanCopy(doc, targetPath, options) : DrawingCleanup.CleanInPlace(doc, options);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    (chkSaveCopy.Checked ? "No cleaned copy was saved, and the drawing on screen is unchanged.\n\n" : "The cleanup stopped; what it had not committed was undone.\n\n") + ex.Message,
                    "C3D Drawing Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally { Cursor = Cursors.Default; }

            if (!chkSaveCopy.Checked) ed.Regen();
            // The full list of objects that could not be deleted, by handle, for checking in the drawing.
            foreach (string line in report.Failures) ed.WriteMessage($"\n[C3D Drawing Cleaner] Not deleted: {line}");
            foreach (string note in report.Notes) ed.WriteMessage($"\n[C3D Drawing Cleaner] {note}");
            ed.WriteMessage($"\n--- C3D Drawing Cleaner Complete: Deleted {report.Deleted} objects, {report.SheetsRemoved} sheets, and purged {report.Purged} unused definitions/styles. ---\n");

            string xrefs = report.XrefsBound > 0 ? $"\n• Xrefs bound: {report.XrefsBound}" : report.XrefsDetached > 0 ? $"\n• Xrefs detached: {report.XrefsDetached}" : "";
            string failures = report.Failures.Count > 0
                ? $"\n\n{report.Failures.Count} object(s) could not be deleted ({report.FailureSummary()}); their handles are listed on the command line."
                : "";
            string notes = report.Notes.Count > 0 ? "\n\n" + string.Join("\n", report.Notes.Take(8)) + (report.Notes.Count > 8 ? "\n…" : "") : "";
            MessageBox.Show(
                $"Drawing Cleanup Complete!\n\n" +
                $"• Deleted Objects & Labels: {report.Deleted}\n" +
                $"• Removed Layout Sheets: {report.SheetsRemoved}\n" +
                $"• Purged Definitions & Layers: {report.Purged}" + xrefs +
                (chkSaveCopy.Checked ? $"\n\nSaved to:\n{targetPath}\n\nThe drawing on screen was not changed." : "") +
                failures + notes,
                "C3D Drawing Cleaner",
                MessageBoxButtons.OK,
                report.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
