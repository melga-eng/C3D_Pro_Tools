using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.Features.Sheets.Civil3D.SheetCommands))]

namespace C3DProTools.Features.Sheets.Civil3D
{
    /// <summary>Sheet production commands. Phase 1 so far: a read-only report of what the scene reader sees.</summary>
    public class SheetCommands
    {
        /// <summary>
        /// Reads the sheet scene of the active drawing and writes a summary to the command line and to
        /// %TEMP%\C3DProSceneReport.txt, with the content fingerprints of the label, view and dimension styles. Changes nothing.
        /// </summary>
        [CommandMethod("C3DProSceneReport")]
        public void SceneReport()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                DateTime t0 = DateTime.Now;
                SheetScene scene = new SheetSceneCivil3D().Read(new SheetSceneRequest { Drawing = DrawingRefs3D.Identity(doc) }, CancellationToken.None);
                string report = Report(scene, doc, DateTime.Now - t0);
                string file = Path.Combine(Path.GetTempPath(), "C3DProSceneReport.txt");
                File.WriteAllText(file, report);
                doc.Editor.WriteMessage("\n" + string.Join("\n", report.Split('\n').Take(40)) + $"\n… full report: {file}\n");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("Scene report failed", ex);
                doc.Editor.WriteMessage($"\nScene report failed: {ex.Message}\n");
            }
        }

        private static string Report(SheetScene s, Document doc, TimeSpan took)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine($"Sheet scene of {doc.Name} (contract {SheetContract.Version}), read in {took.TotalSeconds:0.0} s");
            b.AppendLine($"Sheets: {s.Sheets.Count} (plan {s.Sheets.Count(x => x.Kind == SheetKind.Plan)}, profile {s.Sheets.Count(x => x.Kind == SheetKind.Profile)}, other {s.Sheets.Count(x => x.Kind == SheetKind.Other)})");
            b.AppendLine($"Networks: {s.Networks.Count}; pipes {s.Networks.Sum(n => n.Pipes.Count)} ({s.Networks.Sum(n => n.Pipes.Count(p => p.TypeCode.Length > 0))} with a type code); structures {s.Networks.Sum(n => n.Structures.Count)}");
            b.AppendLine($"Alignments: {s.Alignments.Count}; profiles {s.Alignments.Sum(a => a.Profiles.Count)} (top {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Top))}, proposed {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Proposed))}, existing {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Existing))})");
            b.AppendLine($"Profile views: {s.ProfileViews.Count}; crossings {s.Crossings.Count} ({s.Crossings.Count(c => c.ShownInView)} drawn in their view)");
            b.AppendLine($"Labels: {s.Labels.Count} ({s.Labels.Count(l => l.IsToolLabel)} tool labels, {s.Labels.Count(l => l.Layout.Length > 0)} on a sheet); build records {s.BuildRecords.Count}");
            b.AppendLine($"Issues: {s.Issues.Count}");
            foreach (OperationIssue i in s.Issues.Take(20)) b.AppendLine($"  {i.Severity} {i.Code} {i.Object}: {i.Message}");

            b.AppendLine();
            b.AppendLine("Sheets (layout, kind, tile-part, viewports as scale / twist / locked):");
            foreach (SheetSnapshot sh in s.Sheets.OrderBy(x => x.TabOrder))
                b.AppendLine($"  {sh.Layout}  {sh.Kind}  {(sh.Tile.Length > 0 ? sh.Tile + "-" + sh.Part : "")}  "
                    + string.Join("; ", sh.Viewports.Select(v => $"1:{1000 / v.PaperMmPerModelM:0} / {v.TwistDeg:0.#}° / {(v.Locked ? "locked" : "unlocked")}")));

            b.AppendLine();
            b.AppendLine("Profile views (name, stations, exaggeration, parts own/other, crossings):");
            foreach (ProfileViewSnapshot v in s.ProfileViews.Take(60))
                b.AppendLine($"  {v.Name}  {v.StartStationM:0.00}–{v.EndStationM:0.00}  ×{v.VerticalExaggeration:0.#}  {v.DrawnParts.Count(p => p.IsOwnNetwork)}/{v.DrawnParts.Count(p => !p.IsOwnNetwork)}  {s.Crossings.Count(c => c.ProfileView == v.Ref)}");

            b.AppendLine();
            b.AppendLine("Style fingerprints (label, profile view and dimension styles):");
            foreach (string line in Fingerprints(doc)) b.AppendLine("  " + line);
            return b.ToString();
        }

        /// <summary>The fingerprints of the drawing's styles, to check live that an edited style gets a new one.</summary>
        private static IEnumerable<string> Fingerprints(Document doc)
        {
            List<string> lines = new List<string>();
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                void Add(string kind, ObjectId id)
                {
                    if (id.IsNull || id.IsErased) return;
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string name = (o as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name ?? (o as SymbolTableRecord)?.Name ?? o.GetType().Name;
                    lines.Add($"{kind}  {name}  {StyleFingerprint.Of(tr, o)}");
                }
                try { foreach (ObjectId id in civil.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles) Add("pipe label", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { foreach (ObjectId id in civil.Styles.LabelStyles.StructureLabelStyles.LabelStyles) Add("structure label", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { foreach (ObjectId id in civil.Styles.ProfileViewStyles) Add("profile view style", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                DimStyleTable dims = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dims) Add("dim style", id);
                tr.Commit();
            }
            return lines;
        }
    }
}
