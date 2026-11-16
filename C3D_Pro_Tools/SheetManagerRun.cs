using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.SheetManagerRun))]

namespace C3DProTools
{
    /// <summary>
    /// C3DPROSHEETS: Sheet Manager's reading and checks with no window. Lists every sheet as Sheet Manager sees it (tile and
    /// part and where they came from, title block, sheet title, scale, twist, key plan) and the issues its checks find.
    /// Reads only; nothing in the drawing changes.
    /// </summary>
    public sealed class SheetManagerRun
    {
        [CommandMethod("C3DPRO", "C3DPROSHEETS", CommandFlags.Modal)]
        public void Report()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            SheetProfile p = SheetManagerSettings.Load().Profile;
            var log = new StringBuilder();
            log.AppendLine($"C3DPROSHEETS Report on {Path.GetFileName(doc.Name)}");
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    List<SheetRecord> sheets = SheetManager.Read(tr, doc.Database, p);
                    int parts = sheets.Count(s => s.IsPart), withTile = sheets.Count(s => s.Tile.Length > 0);
                    log.AppendLine($"{sheets.Count} sheets read: {parts} tile-part plan sheets, {withTile} with a tile, {sheets.Count(s => !s.TitleBlockId.IsNull)} with a title block.");
                    foreach (SheetRecord s in sheets)
                        log.AppendLine($"  {s.Layout}: tile-part \"{s.TilePart}\" ({(s.TileSource.Length > 0 ? s.TileSource : "not found")}), " +
                                       $"title block {(s.TitleBlockName.Length > 0 ? s.TitleBlockName : "none")}, title \"{s.Attr(p.SheetTitleTag)}\", " +
                                       $"scale {s.ViewportScale} twist {s.Twist * 180 / System.Math.PI:0.###}°, key plan texts {s.KeyTexts.Count}, " +
                                       $"{(s.Locked ? "locked" : "unlocked")}");
                    List<SheetIssue> issues = SheetManager.Check(tr, sheets, p);
                    log.AppendLine($"Checks: {issues.Count(i => !i.IsNote)} problems ({issues.Count(i => !i.IsNote && i.Fix != null)} fixable), {issues.Count(i => i.IsNote)} notes.");
                    foreach (SheetIssue i in issues)
                        log.AppendLine($"  {(i.IsNote ? "note" : "problem")} {i.Layout}: {i.Text}" + (i.FixText != null ? $" (fix: {i.FixText})" : ""));
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROSHEETS failed", ex);
                log.AppendLine($"C3DPROSHEETS failed: {ex.Message}");
            }
            doc.Editor.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROSHEETS", "Report") + "\n");
        }
    }
}
