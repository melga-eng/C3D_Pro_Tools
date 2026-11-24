using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.SheetManagerRun))]
#endif

namespace C3DProTools
{
    /// <summary>
    /// C3DPROSHEETS: Sheet Manager with no window, through the same code as its window.
    ///   Report  every sheet as Sheet Manager sees it and the issues its checks find (reads only)
    ///   Rename  old=new pairs: layout, drawing number and the match lines on every sheet that point at it
    ///   Copy    a sheet as a new layout after it: its number set, its match lines cleared (it stays in the source's group)
    ///   Group   sheet=group pairs: puts each sheet in a group (sheet= with nothing after takes it out); Sort puts each group together
    /// Each change is one transaction (one UNDO). After a change the sheets involved are read back and reported.
    /// </summary>
    public sealed class SheetManagerRun
    {
        [CommandMethod("C3DPRO", "C3DPROSHEETS", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var kw = new PromptKeywordOptions("\nSheets") { AllowNone = true };
            kw.Keywords.Add("Report"); kw.Keywords.Add("Rename"); kw.Keywords.Add("Copy"); kw.Keywords.Add("Group");
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Report" : kr.StringResult;
            string arg = "";
            if (mode != "Report")
            {
                PromptResult ar = ed.GetString(new PromptStringOptions(mode == "Rename" ? "\nRenames (old=new, comma separated): " : mode == "Group" ? "\nGroups (sheet=group, comma separated; Sort to put each group together): " : "\nCopy (sheet=new name): ") { AllowSpaces = true });
                if (ar.Status != PromptStatus.OK) return;
                arg = ar.StringResult.Trim();
            }
            ed.WriteMessage("\n" + RunReport.Write(Execute(doc, mode, arg), "C3DPROSHEETS", mode) + "\n");
        }

        internal static string Execute(Document doc, string mode, string arg)
        {
            SheetProfile p = SheetManagerSettings.Load().Profile;
            var log = new StringBuilder();
            log.AppendLine($"C3DPROSHEETS {mode} on {Path.GetFileName(doc.Name)}" + (arg.Length > 0 ? $": {arg}" : ""));
            try
            {
                using (doc.LockDocument())
                {
                    // Every entry must be old=new: a malformed one stops the whole request before anything changes.
                    string[] entries = arg.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                    bool sort = mode == "Group" && entries.Contains("Sort");
                    entries = entries.Where(x => x != "Sort").ToArray();
                    // A group may be empty (sheet=): takes the sheet out of its group.
                    string bad = entries.FirstOrDefault(x => x.Split('=').Length != 2 || x.Split('=').Take(mode == "Group" ? 1 : 2).Any(part => part.Trim().Length == 0));
                    if (bad != null) { log.AppendLine($"C3DPROSHEETS failed: \"{bad}\" is not old=new; nothing was changed."); return log.ToString(); }
                    var pairs = entries.Select(x => x.Split('=')).Select(x => (from: x[0].Trim(), to: x[1].Trim())).ToList();
                    if (mode != "Report" && pairs.Count == 0 && !sort) { log.AppendLine($"C3DPROSHEETS failed: nothing to {mode.ToLowerInvariant()} in \"{arg}\"."); return log.ToString(); }
                    var touched = new List<string>();
                    if (mode != "Report")
                        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            List<SheetRecord> sheets = SheetManager.Read(tr, doc.Database, p);
                            foreach ((string from, _) in pairs.Where(x => !sheets.Any(s => s.Layout == x.from)))
                                throw new System.InvalidOperationException($"no sheet is named \"{from}\"");
                            if (mode == "Rename")
                            {
                                var renames = pairs.ToDictionary(x => sheets.First(s => s.Layout == x.from), x => x.to);
                                foreach (string line in SheetManager.Rename(tr, doc.Database, sheets, renames, p)) log.AppendLine("Renamed " + line);
                                touched.AddRange(pairs.Select(x => x.to));
                                // The sheets whose match lines pointed at a renamed one.
                                touched.AddRange(sheets.Where(s => s.Attributes.Any(a => pairs.Any(x => a.Value.Contains(x.to)))).Select(s => s.Layout));
                            }
                            else if (mode == "Group")
                            {
                                foreach ((string sheet, string group) in pairs)
                                {
                                    SheetGroups.Set(tr, sheets.First(s => s.Layout == sheet).LayoutId, group);
                                    log.AppendLine($"Group of {sheet}: \"{SheetGroupNames.Clean(group)}\"");
                                    touched.Add(sheet);
                                }
                                if (sort)
                                {
                                    List<SheetRecord> now = SheetManager.Read(tr, doc.Database, p);
                                    SheetManager.Reorder(tr, SheetGroupNames.Together(now.Select(s => s.Group).ToList()).Select(i => now[i]).ToList());
                                    log.AppendLine("Tabs sorted: each group together.");
                                }
                            }
                            else
                                foreach ((string from, string to) in pairs)
                                {
                                    string problem = SheetManager.NameProblem(to, sheets);
                                    if (problem != null) throw new System.InvalidOperationException($"{to}: {problem}");
                                    SheetRecord copy = SheetManager.Duplicate(tr, doc.Database, sheets.First(s => s.Layout == from), to, p);
                                    log.AppendLine($"Copied {from} as {copy.Layout}");
                                    touched.Add(from); touched.Add(to);
                                }
                            tr.Commit();
                        }

                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        List<SheetRecord> sheets = SheetManager.Read(tr, doc.Database, p);
                        int parts = sheets.Count(s => s.IsPart), withTile = sheets.Count(s => s.Tile.Length > 0);
                        log.AppendLine($"{sheets.Count} sheets read: {parts} tile-part plan sheets, {withTile} with a tile, {sheets.Count(s => !s.TitleBlockId.IsNull)} with a title block.");
                        foreach (SheetRecord s in sheets.Where(s => touched.Count == 0 || touched.Contains(s.Layout)))
                            log.AppendLine($"  {s.Layout}: tab {s.Tab}, group \"{s.Group}\", tile-part \"{s.TilePart}\" ({(s.TileSource.Length > 0 ? s.TileSource : "not found")}), " +
                                           $"number \"{s.Attr(SheetManager.NumberTagOf(s, p))}\" ({SheetManager.NumberTagOf(s, p)}), title \"{s.Attr(p.SheetTitleTag)}\", " +
                                           $"match R \"{s.Attr(p.MatchRightTag)}\" L \"{s.Attr(p.MatchLeftTag)}\" U \"{s.Attr(p.MatchUpTag)}\" D \"{s.Attr(p.MatchDownTag)}\", " +
                                           $"scale {s.ViewportScale} twist {s.Twist * 180 / System.Math.PI:0.###}°, key plan texts {s.KeyTexts.Count}" +
                                           (touched.Count > 0 ? $"\n    attributes: " + string.Join("; ", s.Attributes.Select(a => $"{a.Key}={(a.Value.Length > 40 ? a.Value.Substring(0, 40) + "…" : a.Value)}")) : ""));
                        List<SheetIssue> issues = SheetManager.Check(tr, sheets, p);
                        log.AppendLine($"Checks: {issues.Count(i => !i.IsNote)} problems ({issues.Count(i => !i.IsNote && i.Fix != null)} fixable), {issues.Count(i => i.IsNote)} notes.");
                        foreach (SheetIssue i in issues)
                            log.AppendLine($"  {(i.IsNote ? "note" : "problem")} {i.Layout}: {i.Text}" + (i.FixText != null ? $" (fix: {i.FixText})" : ""));
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROSHEETS failed", ex);
                log.AppendLine($"C3DPROSHEETS failed: {ex.Message}");
            }
            return log.ToString();
        }
    }
}
