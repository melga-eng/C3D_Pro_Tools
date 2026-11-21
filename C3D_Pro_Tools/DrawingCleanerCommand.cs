using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.DrawingCleanerCommand))]
#endif

namespace C3DProTools
{
    public class DrawingCleanerCommand
    {
        [CommandMethod("C3DDrawingCleaner")]
        [CommandMethod("CleanSubmittalDrawing")]
        public void RunDrawingCleaner()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                using (FormDrawingCleaner form = new FormDrawingCleaner())
                {
                    AcadUiApp.ShowModalDialog(form);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nError running C3D Drawing Cleaner: {ex.Message}");
            }
        }

        /// <summary>
        /// C3DPROCLEAN: the Drawing Cleaner with no window, preset "Networks only" (every pipe and pressure network kept,
        /// everything else removed, one empty sheet left, xrefs detached, purged). Always saves a copy; the open drawing is
        /// only read, and the command checks that it still has all its layouts afterwards.
        /// </summary>
        [CommandMethod("C3DPRO", "C3DPROCLEAN", CommandFlags.Modal)]
        public void RunNoDialog()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            PromptResult pr = ed.GetString(new PromptStringOptions("\nSave the cleaned copy (Networks only) as: ") { AllowSpaces = true });
            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult)) return;
            string target = pr.StringResult.Trim().Trim('"');

            var o = new CleanupOptions
            {
                Corridors = true, Surfaces = true, Alignments = true, CogoPoints = true, FeatureLines = true, Parcels = true,
                LabelsTables = true, Annotations = true, Blocks = true, CadGeometry = true, Sheets = true, DeepPurge = true,
                Xrefs = XrefHandling.DetachAll,
            };
            var log = new StringBuilder();
            log.AppendLine($"C3DPROCLEAN Networks only: {Path.GetFileName(doc.Name)} to {target}");
            int layoutsBefore = LayoutCount(doc.Database);
            try
            {
                CleanupReport r = DrawingCleanup.ExportCleanCopy(doc, target, o);
                log.AppendLine($"Cleaned copy saved: {r.Deleted} objects deleted, {r.SheetsRemoved} sheets removed, {r.Purged} unused definitions purged" +
                               (r.Failures.Count > 0 ? $"; {r.Failures.Count} not deleted ({r.FailureSummary()})" : ""));
                foreach (string n in r.Notes) log.AppendLine("  " + n);
                foreach (string f in r.Failures.Take(20)) log.AppendLine("  not deleted " + f);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROCLEAN failed", ex);
                log.AppendLine($"C3DPROCLEAN failed: {ex.Message}");
            }
            int layoutsAfter = LayoutCount(doc.Database);
            log.AppendLine(layoutsAfter == layoutsBefore
                ? $"Open drawing unchanged: {layoutsAfter} layouts before and after."
                : $"Open drawing CHANGED: {layoutsBefore} layouts before, {layoutsAfter} after.");
            ed.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROCLEAN", "NetworksOnly") + "\n");
        }

        private static int LayoutCount(Database db)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                return ((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Count;
        }
    }
}
