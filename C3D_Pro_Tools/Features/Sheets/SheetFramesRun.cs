using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(C3DProTools.Features.Sheets.SheetFramesRun))]

// C3DPROVPFRAMES (ribbon Sheet Frames): each sheet's viewports drawn in model space as the area they show, named with the sheet's
// tile and part and its layout name, on Z-Zz_10_20_95-M-SheetFrames (not plotted); or those frames removed. Drawing a sheet
// again replaces its frames. One UNDO. (Your request, 2026-11-19.)
namespace C3DProTools.Features.Sheets
{
    public sealed class SheetFramesRun
    {
        [CommandMethod("C3DPRO", "C3DPROVPFRAMES", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var kw = new PromptKeywordOptions("\nSheet frames in model space") { AllowNone = true };
            kw.Keywords.Add("Draw"); kw.Keywords.Add("Remove");
            kw.Keywords.Default = "Draw";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            bool draw = kr.StringResult != "Remove";
            PromptResult nr = ed.GetString(new PromptStringOptions("\nSheets (layout names, wildcards; Enter for all) <all>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            string names = nr.StringResult.Trim();
            try
            {
                int n;
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    List<SheetInfo> sheets = AnnotSheets.Read(tr, doc.Database).Where(s => names.Length == 0 || NamePatterns.Matches(names, s.Layout)).ToList();
                    if (sheets.Count == 0) { ed.WriteMessage($"\nC3DPROVPFRAMES: no sheet matches \"{names}\".\n"); return; }
                    n = draw ? AnnotFrame.FramesToModel(tr, doc.Database, sheets, TilePartOf(tr, doc.Database))
                             : AnnotFrame.RemoveFrames(tr, doc.Database, names.Length == 0 ? null : sheets);
                    tr.Commit();
                }
                ViewHelper.Regen(doc);
                ed.WriteMessage(draw ? $"\nC3DPROVPFRAMES: drew {n} viewport frame(s) in model space on {AnnotFrame.SheetFramesLayer} (not plotted).\n"
                                     : $"\nC3DPROVPFRAMES: removed {n} frame object(s).\n");
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROVPFRAMES failed", ex);
                ed.WriteMessage($"\nC3DPROVPFRAMES failed: {ex.Message}. Nothing was changed.\n");
            }
        }

        /// <summary>Each sheet's tile and part as Sheet Manager reads them ("123-C"; a profile sheet's tile alone), by layout name.</summary>
        internal static Dictionary<string, string> TilePartOf(Transaction tr, Database db)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (SheetRecord s in SheetManager.Read(tr, db, SheetManagerSettings.Load().Profile))
                    if (s.Tile.Length > 0) map[s.Layout] = s.Part.Length > 0 ? $"{s.Tile}-{s.Part}" : $"Tile {s.Tile}";
            }
            catch (Exception ex) { C3DProLogger.Warn("Sheet frames: the sheets' tiles could not be read", ex); }
            return map;
        }
    }
}
