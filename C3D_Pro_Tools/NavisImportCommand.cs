using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.NavisLink;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.NavisImportCommand))]

namespace C3DProTools
{
    public class NavisImportCommand
    {
        /// <summary>Opens Navisworks Import. The window is modeless and reads the live Navisworks selection over the local link.</summary>
        [CommandMethod("ImportNavisPipe")]
        public void RunImport()
        {
            try
            {
                FormNavisImport.ShowTool();
            }
            catch (System.Exception ex)
            {
                AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nNavisworks Import error: {ex.Message}");
            }
        }

        /// <summary>
        /// Navisworks Import with no window: a saved selection (the window keeps the last one) imported with the window's
        /// settings and the destination asked here. Every ticked row is imported; one UNDO.
        /// </summary>
        [CommandMethod("C3DPRO", "C3DPRONAVIS", CommandFlags.Modal)]
        public void RunSaved()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            PromptResult fr = ed.GetString(new PromptStringOptions($"\nSaved Navisworks selection <{NavisImportSession.LastSelectionPath}>: ") { AllowSpaces = true });
            if (fr.Status != PromptStatus.OK) return;
            string file = fr.StringResult.Trim().Trim('"');
            if (file.Length == 0) file = NavisImportSession.LastSelectionPath;

            var session = new NavisImportSession();
            var kw = new PromptKeywordOptions("\nPut parts in") { AllowNone = true };
            kw.Keywords.Add("Auto"); kw.Keywords.Add("One"); kw.Keywords.Add("New");
            kw.Keywords.Default = "Auto";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Auto" : kr.StringResult;
            session.Settings.Destination = mode == "One" ? 1 : mode == "New" ? 2 : 0;
            if (mode == "One")
            {
                PromptResult nr = ed.GetString(new PromptStringOptions($"\nNetwork <{session.Settings.ChosenNetwork}>: ") { AllowSpaces = true });
                if (nr.Status != PromptStatus.OK) return;
                if (nr.StringResult.Trim().Length > 0) session.Settings.ChosenNetwork = nr.StringResult.Trim();
            }

            var ck = new PromptKeywordOptions("\nConnect pipes to the structures they end in") { AllowNone = true };
            ck.Keywords.Add("Yes"); ck.Keywords.Add("No");
            ck.Keywords.Default = session.Settings.ConnectToStructures ? "Yes" : "No";
            PromptResult cr = ed.GetKeywords(ck);
            if (cr.Status != PromptStatus.OK && cr.Status != PromptStatus.None) return;
            if (!string.IsNullOrEmpty(cr.StringResult)) session.Settings.ConnectToStructures = cr.StringResult == "Yes";

            var log = new StringBuilder();
            try
            {
                NavisResponse response = NavisImportSession.LoadSelection(file);
                // The saved selection is in Navisworks coordinates: the window's shift and turn apply, as when it was read.
                session.Transform = new NavisTransform
                {
                    Dx = session.Settings.TransformDx, Dy = session.Settings.TransformDy, Dz = session.Settings.TransformDz, RotationDeg = session.Settings.TransformRotationDeg
                };
                if (!session.Transform.IsIdentity) log.AppendLine($"Shift from the window's settings: X {session.Transform.Dx}, Y {session.Transform.Dy}, Z {session.Transform.Dz}, turn {session.Transform.RotationDeg} deg");
                session.Load(response);
                session.Bind(doc, out _);
                NavisImportOptions o = session.Options(ObjectId.Null);
                var rows = session.Rows.Where(r => session.Ticked.Contains(r)).ToList();
                log.AppendLine($"C3DPRONAVIS {mode}: {rows.Count} row(s) from {System.IO.Path.GetFileName(response.Document)}");
                ImportReport report = NavisImporter.Import(doc, rows, o);
                if (report.Error.Length > 0) log.AppendLine("C3DPRONAVIS failed: " + report.Error + ". Nothing was changed.");
                foreach (string n in report.Networks) log.AppendLine("Network: " + n);
                foreach (string sz in report.AddedSizes) log.AppendLine("Size added: " + sz);
                foreach (var kv in report.Rows)
                {
                    report.Destinations.TryGetValue(kv.Key, out RowDestination d);
                    log.AppendLine($"  {kv.Key.Name}: {kv.Key.Action} -> {(d == null ? "-" : (d.Existing ? "existing " : "new ") + d.Network)}: {kv.Value.Status}" +
                                   (kv.Value.Family.Length > 0 ? $" ({kv.Value.Family} · {kv.Value.Size})" : "") + (kv.Value.Detail.Length > 0 ? " " + kv.Value.Detail : ""));
                }
                log.AppendLine(Connections(doc, report));
                log.AppendLine($"Imported {report.Rows.Values.Count(r => r.Created.Count > 0 && !r.Failed && !r.InDrawing && r.Status != "Already imported")}, " +
                               $"in drawing {report.Rows.Values.Count(r => r.InDrawing)}, failed {report.Rows.Values.Count(r => r.Failed)}.");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPRONAVIS failed", ex);
                log.AppendLine($"C3DPRONAVIS failed: {ex.Message}. Nothing was changed.");
            }
            ed.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPRONAVIS", mode) + "\n");
        }

        /// <summary>The imported pipes' ends: connected to an imported structure, to one already in the drawing, or open.</summary>
        private static string Connections(Document doc, ImportReport report)
        {
            var imported = new System.Collections.Generic.HashSet<ObjectId>(report.Rows.Values.SelectMany(r => r.Created));
            int toImported = 0, toDrawing = 0, open = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in imported)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                    foreach (ObjectId s in new[] { p.StartStructureId, p.EndStructureId })
                    {
                        if (s.IsNull) open++;
                        else if (imported.Contains(s)) toImported++;
                        else toDrawing++;
                    }
                }
                tr.Commit();
            }
            return $"Pipe ends: {toDrawing} connected to structures already in the drawing, {toImported} to imported structures, {open} open.";
        }
    }
}
