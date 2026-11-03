using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Font = System.Drawing.Font;

namespace C3DProTools
{
    /// <summary>What to create for a run of parts: the alignment, its surface profiles, the profile view and the part labels.</summary>
    internal sealed class ProfileSetup
    {
        public string AlignmentName;
        public ObjectId AlignmentStyleId, AlignmentLabelSetId, LayerId;
        public double StartStation;
        public List<KeyValuePair<string, ObjectId>> Surfaces = new List<KeyValuePair<string, ObjectId>>();
        public ObjectId ProfileStyleId, ProfileLabelSetId;
        public ObjectId ProfileViewStyleId, BandSetId;
        public double ElevationMargin;
        public List<ObjectId> PipeLabels = new List<ObjectId>();
        public List<ObjectId> PressurePipeLabels = new List<ObjectId>();
        public ObjectId StructureLabel, FittingLabel, AppurtenanceLabel;
    }

    /// <summary>Collects the <see cref="ProfileSetup"/>, pre-filled from (and saved back to) <see cref="PartsToProfileSettings"/>.</summary>
    internal sealed class FormProfileSetup : Form
    {
        private const string NoneItem = "<None>";

        private sealed class NamedId
        {
            public string Name;
            public ObjectId Id;
            public override string ToString() => Name;
        }

        private readonly PartsToProfileSettings _settings;
        private readonly TextBox txtName = new TextBox();
        private readonly TextBox txtStartStation = new TextBox();
        private readonly TextBox txtMargin = new TextBox();
        private readonly CheckedListBox clbSurfaces = new CheckedListBox();
        private ComboBox cbAlignmentStyle, cbAlignmentLabelSet, cbLayer, cbProfileStyle, cbProfileLabelSet, cbViewStyle, cbBandSet;
        private ComboBox cbStructureLabel, cbFittingLabel, cbAppurtenanceLabel;
        private readonly ComboBox[] cbPipeLabels = new ComboBox[3];
        private readonly ComboBox[] cbPressureLabels = new ComboBox[3];
        private readonly NumericUpDown nudPipeLabels = new NumericUpDown();
        private readonly NumericUpDown nudPressureLabels = new NumericUpDown();

        public ProfileSetup Result { get; private set; }

        public FormProfileSetup(string defaultAlignmentName, PartsToProfileSettings settings)
        {
            _settings = settings;
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;

            GroupBox gbAlignment = Group("Alignment", 10, 10, 430, 178);
            GroupBox gbSurfaces = Group("Surface Profiles (shown in the profile view)", 450, 10, 430, 178);
            GroupBox gbView = Group("Profile View", 10, 196, 870, 58);
            GroupBox gbLabels = Group("Part Labels in Profile", 10, 262, 870, 172);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // --- Alignment ---
                RowLabel(gbAlignment, "Name:", 25);
                txtName.Location = new Point(125, 22); txtName.Size = new Size(290, 22); txtName.Text = defaultAlignmentName;
                gbAlignment.Controls.Add(txtName);
                cbAlignmentStyle = Combo(gbAlignment, "Style:", 53, ReadNamed(tr, doc.Styles.AlignmentStyles), settings.AlignmentStyle, false);
                cbAlignmentLabelSet = Combo(gbAlignment, "Label set:", 81, ReadNamed(tr, doc.Styles.LabelSetStyles.AlignmentLabelSetStyles), settings.AlignmentLabelSet, false);
                cbLayer = Combo(gbAlignment, "Layer:", 109, ReadLayers(tr, db), settings.Layer ?? CurrentLayerName(tr, db), false);
                RowLabel(gbAlignment, "Start station:", 140);
                txtStartStation.Location = new Point(125, 137); txtStartStation.Size = new Size(100, 22);
                txtStartStation.Text = settings.StartStation.ToString("0.###", CultureInfo.InvariantCulture);
                gbAlignment.Controls.Add(txtStartStation);

                // --- Surfaces ---
                clbSurfaces.Location = new Point(10, 22); clbSurfaces.Size = new Size(410, 82); clbSurfaces.CheckOnClick = true; clbSurfaces.IntegralHeight = false;
                foreach (NamedId surface in ReadSurfaces(tr, doc))
                    clbSurfaces.Items.Add(surface, settings.Surfaces.Contains(surface.Name));
                gbSurfaces.Controls.Add(clbSurfaces);
                cbProfileStyle = Combo(gbSurfaces, "Profile style:", 112, ReadNamed(tr, doc.Styles.ProfileStyles), settings.ProfileStyle, false);
                cbProfileLabelSet = Combo(gbSurfaces, "Profile label set:", 140, ReadNamed(tr, doc.Styles.LabelSetStyles.ProfileLabelSetStyles), settings.ProfileLabelSet, false);

                // --- Profile view ---
                Label lblViewStyle = new Label { Text = "Style:", AutoSize = true, Location = new Point(10, 27) };
                gbView.Controls.Add(lblViewStyle);
                cbViewStyle = ComboAt(gbView, 55, 24, 300, ReadNamed(tr, doc.Styles.ProfileViewStyles), settings.ProfileViewStyle, false);
                gbView.Controls.Add(new Label { Text = "Band set:", AutoSize = true, Location = new Point(370, 27) });
                cbBandSet = ComboAt(gbView, 435, 24, 250, ReadNamed(tr, doc.Styles.ProfileViewBandSetStyles), settings.BandSet, false);
                gbView.Controls.Add(new Label { Text = "Elev. margin (m):", AutoSize = true, Location = new Point(700, 27) });
                txtMargin.Location = new Point(805, 24); txtMargin.Size = new Size(55, 22);
                txtMargin.Text = settings.ElevationMargin.ToString("0.##", CultureInfo.InvariantCulture);
                gbView.Controls.Add(txtMargin);

                // --- Labels ---
                LabelStylesRoot labels = doc.Styles.LabelStyles;
                List<NamedId> pipeStyles = ReadNamed(tr, labels.PipeLabelStyles.PlanProfileLabelStyles);
                List<NamedId> structureStyles = ReadNamed(tr, labels.StructureLabelStyles.LabelStyles);
                List<NamedId> pressurePipeStyles = ReadNamed(tr, LabelStylesRootPressurePipesExtension.GetPressurePipeLabelStyles(labels).PlanProfileLabelStyles);
                List<NamedId> fittingStyles = ReadNamed(tr, LabelStylesRootPressurePipesExtension.GetPressureFittingLabelStyles(labels).LabelStyles);
                List<NamedId> appurtenanceStyles = ReadNamed(tr, LabelStylesRootPressurePipesExtension.GetPressureAppurtenanceLabelStyles(labels).LabelStyles);

                PipeLabelRow(gbLabels, "Gravity pipes:", 25, nudPipeLabels, cbPipeLabels, pipeStyles, settings.PipeLabelCount, settings.PipeLabels);
                cbStructureLabel = LabelCombo(gbLabels, "Structures:", 55, structureStyles, settings.StructureLabel);
                PipeLabelRow(gbLabels, "Pressure pipes:", 85, nudPressureLabels, cbPressureLabels, pressurePipeStyles, settings.PressurePipeLabelCount, settings.PressurePipeLabels);
                cbFittingLabel = LabelCombo(gbLabels, "Fittings:", 115, fittingStyles, settings.FittingLabel);
                cbAppurtenanceLabel = LabelCombo(gbLabels, "Appurtenances:", 143, appurtenanceStyles, settings.AppurtenanceLabel);

                tr.Commit();
            }

            Button btnCreate = new Button { Text = "Pick Insertion Point && Create", Location = new Point(560, 444), Size = new Size(210, 34) };
            btnCreate.Font = new Font(btnCreate.Font, FontStyle.Bold);
            btnCreate.Click += BtnCreate_Click;
            Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(780, 444), Size = new Size(100, 34) };

            Controls.AddRange(new Control[] { gbAlignment, gbSurfaces, gbView, gbLabels, btnCreate, btnCancel });
            AcceptButton = btnCreate;
            CancelButton = btnCancel;

            Text = "Parts to Profile - Create Alignment && Profile View";
            ClientSize = new Size(890, 488);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
        }

        private void BtnCreate_Click(object sender, EventArgs e)
        {
            string name = txtName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("Please enter an alignment name.", "Missing Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!TryRead(txtStartStation, out double startStation) || !TryRead(txtMargin, out double margin) || margin < 0)
            {
                MessageBox.Show("Start station and elevation margin must be valid numbers.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Selected(cbAlignmentStyle) == null || Selected(cbAlignmentLabelSet) == null || Selected(cbLayer) == null ||
                Selected(cbViewStyle) == null || Selected(cbBandSet) == null ||
                (clbSurfaces.CheckedItems.Count > 0 && (Selected(cbProfileStyle) == null || Selected(cbProfileLabelSet) == null)))
            {
                MessageBox.Show("This drawing is missing a style that is needed (alignment, label set, layer, profile view, band set or profile style).",
                    "Missing Style", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ProfileSetup r = new ProfileSetup
            {
                AlignmentName = name,
                AlignmentStyleId = Selected(cbAlignmentStyle).Id,
                AlignmentLabelSetId = Selected(cbAlignmentLabelSet).Id,
                LayerId = Selected(cbLayer).Id,
                StartStation = startStation,
                ProfileStyleId = Selected(cbProfileStyle)?.Id ?? ObjectId.Null,
                ProfileLabelSetId = Selected(cbProfileLabelSet)?.Id ?? ObjectId.Null,
                ProfileViewStyleId = Selected(cbViewStyle).Id,
                BandSetId = Selected(cbBandSet).Id,
                ElevationMargin = margin,
                StructureLabel = Selected(cbStructureLabel)?.Id ?? ObjectId.Null,
                FittingLabel = Selected(cbFittingLabel)?.Id ?? ObjectId.Null,
                AppurtenanceLabel = Selected(cbAppurtenanceLabel)?.Id ?? ObjectId.Null
            };
            foreach (NamedId surface in clbSurfaces.CheckedItems) r.Surfaces.Add(new KeyValuePair<string, ObjectId>(surface.Name, surface.Id));
            for (int i = 0; i < (int)nudPipeLabels.Value; i++) if (Selected(cbPipeLabels[i]) is NamedId n) r.PipeLabels.Add(n.Id);
            for (int i = 0; i < (int)nudPressureLabels.Value; i++) if (Selected(cbPressureLabels[i]) is NamedId n) r.PressurePipeLabels.Add(n.Id);
            Result = r;

            _settings.AlignmentStyle = cbAlignmentStyle.Text;
            _settings.AlignmentLabelSet = cbAlignmentLabelSet.Text;
            _settings.Layer = cbLayer.Text;
            _settings.StartStation = startStation;
            _settings.Surfaces = r.Surfaces.ConvertAll(s => s.Key);
            _settings.ProfileStyle = cbProfileStyle.Text;
            _settings.ProfileLabelSet = cbProfileLabelSet.Text;
            _settings.ProfileViewStyle = cbViewStyle.Text;
            _settings.BandSet = cbBandSet.Text;
            _settings.ElevationMargin = margin;
            _settings.PipeLabelCount = (int)nudPipeLabels.Value;
            _settings.PipeLabels = Array.ConvertAll(cbPipeLabels, cb => cb.Text).ToList();
            _settings.StructureLabel = cbStructureLabel.Text;
            _settings.PressurePipeLabelCount = (int)nudPressureLabels.Value;
            _settings.PressurePipeLabels = Array.ConvertAll(cbPressureLabels, cb => cb.Text).ToList();
            _settings.FittingLabel = cbFittingLabel.Text;
            _settings.AppurtenanceLabel = cbAppurtenanceLabel.Text;
            _settings.Save();

            DialogResult = DialogResult.OK;
        }

        private static bool TryRead(TextBox box, out double value) =>
            double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        /// <summary>The chosen item, or null for "&lt;None&gt;" / an empty list.</summary>
        private static NamedId Selected(ComboBox box) => box.SelectedItem as NamedId;

        private static GroupBox Group(string text, int x, int y, int w, int h) =>
            new GroupBox { Text = text, Location = new Point(x, y), Size = new Size(w, h) };

        private static void RowLabel(Control parent, string text, int y) =>
            parent.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(10, y) });

        private static ComboBox Combo(Control parent, string label, int y, List<NamedId> items, string preferred, bool allowNone)
        {
            RowLabel(parent, label, y + 3);
            return ComboAt(parent, 125, y, 290, items, preferred, allowNone);
        }

        private static ComboBox ComboAt(Control parent, int x, int y, int w, List<NamedId> items, string preferred, bool allowNone)
        {
            ComboBox box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Size = new Size(w, 24) };
            if (allowNone) box.Items.Add(NoneItem);
            foreach (NamedId item in items) box.Items.Add(item);

            int index = -1;
            if (!string.IsNullOrEmpty(preferred))
            {
                for (int i = 0; i < box.Items.Count; i++) if (box.Items[i].ToString() == preferred) { index = i; break; }
            }
            // With no remembered choice, default to the first real style rather than "<None>".
            if (index < 0) index = box.Items.Count > (allowNone ? 1 : 0) ? (allowNone ? 1 : 0) : (box.Items.Count > 0 ? 0 : -1);
            box.SelectedIndex = index;
            parent.Controls.Add(box);
            return box;
        }

        private static ComboBox LabelCombo(Control parent, string label, int y, List<NamedId> styles, string preferred)
        {
            RowLabel(parent, label, y + 3);
            return ComboAt(parent, 135, y, 270, styles, preferred, true);
        }

        private static void PipeLabelRow(Control parent, string label, int y, NumericUpDown count, ComboBox[] boxes,
            List<NamedId> styles, int savedCount, List<string> savedStyles)
        {
            RowLabel(parent, label, y + 3);
            count.Location = new Point(135, y); count.Size = new Size(45, 22);
            count.Minimum = 0; count.Maximum = 3; count.Value = Math.Max(0, Math.Min(3, savedCount));
            parent.Controls.Add(count);
            for (int i = 0; i < boxes.Length; i++)
                boxes[i] = ComboAt(parent, 190 + i * 225, y, 215, styles, i < savedStyles.Count ? savedStyles[i] : null, false);

            void UpdateEnabled() { for (int i = 0; i < boxes.Length; i++) boxes[i].Enabled = i < count.Value; }
            count.ValueChanged += (s, e) => UpdateEnabled();
            UpdateEnabled();
        }

        /// <summary>Names of the styles in a Civil 3D style collection (every such collection enumerates ObjectIds).</summary>
        private static List<NamedId> ReadNamed(Transaction tr, IEnumerable collection)
        {
            List<NamedId> items = new List<NamedId>();
            foreach (object o in collection)
            {
                if (!(o is ObjectId id) || !Readable(tr, id, out DBObject obj)) continue;
                string name = obj is StyleBase style ? style.Name : obj.GetType().GetProperty("Name")?.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(name)) items.Add(new NamedId { Name = name, Id = id });
            }
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return items;
        }

        private static List<NamedId> ReadSurfaces(Transaction tr, CivilDocument doc)
        {
            List<NamedId> items = new List<NamedId>();
            foreach (ObjectId id in doc.GetSurfaceIds())
            {
                if (Readable(tr, id, out DBObject obj) && obj is Autodesk.Civil.DatabaseServices.Surface surface)
                    items.Add(new NamedId { Name = surface.Name, Id = id });
            }
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return items;
        }

        private static List<NamedId> ReadLayers(Transaction tr, Database db)
        {
            List<NamedId> items = new List<NamedId>();
            LayerTable layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in layers)
            {
                if (Readable(tr, id, out DBObject obj) && obj is LayerTableRecord layer && !layer.IsDependent)
                    items.Add(new NamedId { Name = layer.Name, Id = id });
            }
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return items;
        }

        /// <summary>
        /// Opens an entry of a style / layer / surface list, skipping erased or unreadable ones: a collection can still hold
        /// an entry whose object was erased (undone or deleted in the session), and opening it throws ePermanentlyErased.
        /// </summary>
        private static bool Readable(Transaction tr, ObjectId id, out DBObject obj)
        {
            obj = null;
            if (id.IsNull || id.IsErased || !id.IsValid) return false;
            try { obj = tr.GetObject(id, OpenMode.ForRead); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
            return obj != null;
        }

        private static string CurrentLayerName(Transaction tr, Database db) =>
            ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
    }
}
