using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Labels;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

[assembly: CommandClass(typeof(C3DProTools.Features.CadExport.CadExportRun))]

// C3DPROCADRUN: makes the CAD sheets from the drawing open in a background Civil 3D (accoreconsole), which is always a copy:
//   1. every Civil 3D label exploded as its sheet shows it: plan labels with the model view turned like the plan viewport,
//      profile labels with no turn (the two-step explode done by hand);
//   2. the drawing exported to plain AutoCAD 2018 (EXPORTTOAUTOCAD);
//   3. one DWG per chosen layout, from the export: the whole model, that layout only and current, everything unused purged.
// It refuses to run in a Civil 3D with a window: the explode would change the user's drawing. C3DPROCAD starts it.
namespace C3DProTools.Features.CadExport
{
    internal sealed class CadExportSettings
    {
        /// <summary>Exact layout names (the window sends them); wildcards work too. Separated by |.</summary>
        public List<string> Layouts { get; set; } = new List<string>();
        public string Folder { get; set; } = "";
    }

    public sealed class CadExportRun
    {
        public const string Command = "C3DPROCADRUN";

        /// <summary>True in the background Civil 3D (accoreconsole), where the drawing is a copy.</summary>
        internal static bool InBackground =>
            string.Equals(Process.GetCurrentProcess().ProcessName, "accoreconsole", StringComparison.OrdinalIgnoreCase);

        [CommandMethod("C3DPRO", Command, CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            if (!InBackground)
            {
                ed.WriteMessage($"\n{Command} runs only in the background on a copy of the drawing (it explodes every label). Use C3DPROCAD.\n");
                return;
            }
            // Two steps, each in its own background Civil 3D: EXPORTTOAUTOCAD writes no file in the session that exploded the
            // labels (tile 124 test, 2026-11-16), so the explode is saved (_QSAVE in the script) and the export opens it fresh.
            var kw = new PromptKeywordOptions("\nStep") { AllowNone = false };
            kw.Keywords.Add("Explode");
            kw.Keywords.Add("Export");
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK) return;
            // Names and folder may come as @file (UTF-8): a script is ASCII, and a non-English name would turn into ? (a wildcard).
            PromptResult lr = ed.GetString(new PromptStringOptions("\nLayouts (| between names, wildcards allowed, or @file): ") { AllowSpaces = true });
            if (lr.Status != PromptStatus.OK) return;
            var s = new CadExportSettings
            {
                Layouts = FromFile(lr.StringResult).Split('|', '\n', '\r').Select(n => n.Trim()).Where(n => n.Length > 0).ToList(),
            };
            if (kr.StringResult == "Export")
            {
                PromptResult fr = ed.GetString(new PromptStringOptions("\nOutput folder (or @file): ") { AllowSpaces = true });
                if (fr.Status != PromptStatus.OK) return;
                s.Folder = FromFile(fr.StringResult).Trim().Trim('"');
            }
            // The background console moves TEMP into its own folder, so the window names the report file it will read.
            PromptResult rr = ed.GetString(new PromptStringOptions("\nReport file <none>: ") { AllowSpaces = true });
            if (rr.Status != PromptStatus.OK) return;
            string report = kr.StringResult == "Export" ? Export(doc, s) : Explode(doc, s.Layouts);
            string reportFile = rr.StringResult.Trim().Trim('"');
            if (reportFile.Length > 0)
                try { File.AppendAllText(reportFile, report); }
                catch (System.Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn($"{Command}: the report could not be written", ex); }
            ed.WriteMessage("\n" + RunReport.Write(report, Command, kr.StringResult) + "\n");
        }

        /// <summary>
        /// Whether a typed or sent name picks a layout: the exact name when a layout has it (a sheet name may hold * or ?),
        /// otherwise as wildcards.
        /// </summary>
        internal static bool Picks(string pattern, string layout, IReadOnlyCollection<string> all) =>
            all.Contains(pattern, StringComparer.OrdinalIgnoreCase)
                ? string.Equals(pattern, layout, StringComparison.OrdinalIgnoreCase)
                : NamePatterns.Matches(pattern, layout);

        /// <summary>A prompt answer, or the UTF-8 text of the file it names after @.</summary>
        private static string FromFile(string answer)
        {
            string a = (answer ?? "").Trim();
            return a.StartsWith("@") ? File.ReadAllText(a.Substring(1).Trim('"'), Encoding.UTF8).Trim() : a;
        }

        /// <summary>
        /// Step 1: every Civil 3D label exploded as the chosen sheets show it (only their viewports decide a label's turn). The
        /// open drawing must be a disposable copy.
        /// </summary>
        internal static string Explode(Document doc, List<string> layouts)
        {
            var log = new StringBuilder();
            log.AppendLine($"{Command} on {Path.GetFileName(doc.Name)}");
            var clock = Stopwatch.StartNew();
            try
            {
                using (doc.LockDocument()) ExplodeLabels(doc, layouts, log);
                log.AppendLine($"  labels done after {clock.Elapsed.TotalSeconds:0} s");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error($"{Command} Explode failed", ex);
                log.AppendLine($"{Command} Explode failed: {ex.Message}");
            }
            return log.ToString();
        }

        /// <summary>Step 2, on the saved explode: EXPORTTOAUTOCAD, then one DWG per chosen layout; returns the report.</summary>
        internal static string Export(Document doc, CadExportSettings s)
        {
            var log = new StringBuilder();
            var clock = Stopwatch.StartNew();
            string work = Path.Combine(Path.GetTempPath(), "c3dpro-cad-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                if (string.IsNullOrWhiteSpace(s.Folder)) { log.AppendLine("Failed: no output folder."); return log.ToString(); }
                Directory.CreateDirectory(s.Folder);
                Directory.CreateDirectory(work);

                List<string> all = LayoutNames(doc.Database);
                List<string> chosen = all.Where(n => s.Layouts.Any(p => Picks(p, n, all))).ToList();
                foreach (string missing in s.Layouts.Where(p => !all.Any(n => Picks(p, n, all))))
                    log.AppendLine($"Not found: no layout is named \"{missing}\".");
                if (chosen.Count == 0) { log.AppendLine("Failed: none of the chosen layouts is in the drawing."); return log.ToString(); }

                using (doc.LockDocument())
                {
                    string exported = Path.Combine(work, "export.dwg");
                    // Exported from Model: from a sheet, EXPORTTOAUTOCAD stalls in the background at _MSPACE (tile 124 and
                    // 000501 copies, 2026-11-16).
                    if (!string.Equals(LayoutManager.Current.CurrentLayout, "Model", StringComparison.OrdinalIgnoreCase))
                        LayoutManager.Current.CurrentLayout = "Model";
                    ExportToAutoCad(doc, exported);
                    if (!File.Exists(exported)) { log.AppendLine("Failed: EXPORTTOAUTOCAD wrote no file."); return log.ToString(); }
                    log.AppendLine($"  exported to AutoCAD 2018 after {clock.Elapsed.TotalSeconds:0} s ({new FileInfo(exported).Length / 1048576} MB)");

                    int saved = 0;
                    foreach (string layout in chosen)
                    {
                        string target = Path.Combine(s.Folder, SheetTwist.FileName(layout));
                        try
                        {
                            int purged = Split(exported, layout, target);
                            saved++;
                            log.AppendLine($"Saved {Path.GetFileName(target)} ({purged} unused items purged)");
                        }
                        catch (System.Exception ex)
                        {
                            C3DProLogger.Error($"{Command}: {layout} could not be saved", ex);
                            log.AppendLine($"Not saved {layout}: {ex.Message}");
                        }
                    }
                    log.AppendLine($"{saved} of {chosen.Count} CAD sheets saved in {s.Folder} ({clock.Elapsed.TotalMinutes:0.0} min).");
                }
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error($"{Command} failed", ex);
                log.AppendLine($"{Command} failed: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (System.Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn($"{Command}: the work folder could not be removed", ex); }
            }
            return log.ToString();
        }

        /// <summary>Layout names in tab order, Model left out.</summary>
        internal static List<string> LayoutNames(Database db)
        {
            var list = new List<(int, string)>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                {
                    var l = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                    if (!l.ModelType) list.Add((l.TabOrder, l.LayoutName));
                }
                tr.Commit();
            }
            return list.OrderBy(t => t.Item1).Select(t => t.Item2).ToList();
        }

        /// <summary>The model windows of every sheet viewport in the drawing (the overall paper viewport of each layout left out).</summary>
        private static List<ViewportWindow> Windows(Database db, Transaction tr)
        {
            var windows = new List<ViewportWindow>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                var layout = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (layout.ModelType) continue;
                var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var vps = new List<Viewport>();
                foreach (ObjectId id in space)
                    if (id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(Viewport))) && tr.GetObject(id, OpenMode.ForRead) is Viewport vp) vps.Add(vp);
                // The overall paper viewport is the first one created in the layout.
                ObjectIdCollection listed = layout.GetViewports();
                ObjectId overall = listed != null && listed.Count > 0 ? listed[0] : vps.OrderBy(v => v.Handle.Value).Select(v => v.ObjectId).FirstOrDefault();
                foreach (Viewport vp in vps.Where(v => v.ObjectId != overall))
                    windows.Add(ViewportWindow.Of(layout.LayoutName, vp.Width, vp.Height, vp.ViewCenter.X, vp.ViewCenter.Y, vp.ViewHeight,
                        vp.ViewTarget.X, vp.ViewTarget.Y, vp.TwistAngle));
            }
            return windows;
        }

        /// <summary>
        /// Every Civil 3D label (single labels and label groups) exploded in model space, grouped by the turn it is exploded at,
        /// with the model view turned to it so Civil 3D draws each label as its sheet shows it. Locked layers are unlocked for
        /// the explode and locked again.
        /// </summary>
        private static void ExplodeLabels(Document doc, List<string> layouts, StringBuilder log)
        {
            Database db = doc.Database;
            var groups = new Dictionary<double, List<ObjectId>>();
            var locked = new List<ObjectId>();
            double planTwist;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<string> names = LayoutNames(db);
                List<ViewportWindow> windows = Windows(db, tr)
                    .Where(w => layouts.Count == 0 || layouts.Any(p => Picks(p, w.Layout, names))).ToList();
                planTwist = SheetTwist.PlanTwist(windows);
                var profileViews = new List<Extents3d>();
                var labels = new List<(ObjectId Id, string Kind)>();
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                // By the managed type of each object: the RXClass of LabelBase is a Civil 3D base shared with pipes and structures.
                foreach (ObjectId id in ms)
                {
                    if (!id.ObjectClass.DxfName.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase)) continue;
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (o is LabelBase) labels.Add((id, id.ObjectClass.DxfName));
                    else if (o is ProfileView pv)
                        try { profileViews.Add(pv.GeometricExtents); }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                foreach ((ObjectId id, string kind) in labels)
                {
                    var label = (Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(id, OpenMode.ForRead);
                    double x = 0, y = 0;
                    bool inView = false;
                    try
                    {
                        Extents3d ext = label.GeometricExtents;
                        x = (ext.MinPoint.X + ext.MaxPoint.X) / 2;
                        y = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2;
                        inView = profileViews.Any(v => x >= v.MinPoint.X && x <= v.MaxPoint.X && y >= v.MinPoint.Y && y <= v.MaxPoint.Y);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    double twist = SheetTwist.Normal(SheetTwist.For(x, y, SheetTwist.IsProfileKind(kind), inView, windows, planTwist));
                    double key = Math.Round(twist, 6);
                    if (!groups.TryGetValue(key, out List<ObjectId> g)) groups[key] = g = new List<ObjectId>();
                    g.Add(id);
                }
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in layers)
                {
                    var layer = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (layer.IsLocked) { layer.UpgradeOpen(); layer.IsLocked = false; locked.Add(lid); }
                }
                tr.Commit();
            }
            log.AppendLine($"  plan twist {Degrees(planTwist):0.###}° (model text reads at {Degrees(SheetTwist.Normal(-planTwist)):0.###}°); {locked.Count} locked layers unlocked for the explode");

            // The EXPLODE command, as done by hand: it draws each label in the current (turned) view. Entity.Explode does not
            // (tile 124 test, 2026-11-16: plan text came out at 0° with the view turned 59.379°).
            object qaflags = AcApp.GetSystemVariable("QAFLAGS");
            using (var turn = new ViewTurn(doc))
            {
                try
                {
                    // Bit 1: EXPLODE takes a whole selection set from a script, not only its first object.
                    AcApp.SetSystemVariable("QAFLAGS", (short)1);
                    foreach (KeyValuePair<double, List<ObjectId>> g in groups.OrderByDescending(p => p.Value.Count))
                    {
                        turn.Turn(g.Key);
                        long before = LastHandle(db);
                        doc.Editor.Command("_.EXPLODE", SelectionSet.FromObjectIds(g.Value.ToArray()), "");
                        int failed = g.Value.Count(id => !id.IsErased);
                        string sample = FirstTextAngle(db, before);
                        int flattened = FlattenNewBlocks(db, before);
                        if (flattened > 0) log.AppendLine($"  {flattened} label blocks exploded to text and lines");
                        log.AppendLine($"Labels exploded at view turn {Degrees(g.Key):0.###}°: {g.Value.Count - failed}" + (failed > 0 ? $", {failed} not exploded" : "") +
                                       (sample != null ? $" (first text reads at {sample}°)" : ""));
                        foreach (ObjectId id in g.Value.Where(id => !id.IsErased).Take(5))
                            log.AppendLine($"  label {id.Handle} ({id.ObjectClass.DxfName}) not exploded");
                    }
                }
                finally { AcApp.SetSystemVariable("QAFLAGS", qaflags); }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId lid in locked) ((LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite)).IsLocked = true;
                tr.Commit();
            }
        }

        /// <summary>
        /// The anonymous blocks the EXPLODE command made of the labels, exploded again to plain text, lines and leaders (as in the
        /// submitted CAD sheets). Plain geometry now, so the view turn no longer matters. EXPORTTOAUTOCAD stalled on the label
        /// blocks in the background (tile 124 test, 2026-11-16). Up to three levels of anonymous blocks.
        /// </summary>
        private static int FlattenNewBlocks(Database db, long since)
        {
            int count = 0;
            for (int level = 0; level < 3; level++)
            {
                int thisLevel = 0;
                long next = db.Handseed.Value;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    var refs = new List<ObjectId>();
                    foreach (ObjectId id in ms)
                        if (id.Handle.Value >= since && id.ObjectClass.DxfName == "INSERT") refs.Add(id);
                    foreach (ObjectId id in refs)
                    {
                        var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                        if (!((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).IsAnonymous) continue;
                        try
                        {
                            var parts = new DBObjectCollection();
                            br.Explode(parts);
                            foreach (DBObject o in parts)
                            {
                                if (o is Autodesk.AutoCAD.DatabaseServices.Entity part) { ms.AppendEntity(part); tr.AddNewlyCreatedDBObject(part, true); }
                                else o.Dispose();
                            }
                            br.UpgradeOpen();
                            br.Erase();
                            thisLevel++;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) { C3DProLogger.Warn($"{Command}: label block {id.Handle} not exploded", ex); }
                    }
                    tr.Commit();
                }
                count += thisLevel;
                if (thisLevel == 0) break;
                since = next;
            }
            return count;
        }

        /// <summary>The handle the next new object gets: everything made after this point has a handle at least this.</summary>
        private static long LastHandle(Database db) => db.Handseed.Value;

        /// <summary>The model angle of the first text among the model objects made since <paramref name="since"/>, for the report.</summary>
        private static string FirstTextAngle(Database db, long since)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                    if (id.Handle.Value >= since && tr.GetObject(id, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Entity e)
                    {
                        string a = TextAngle(e, tr);
                        if (a != null) return a;
                    }
                return null;
            }
        }

        /// <summary>The model angle of the first text in an exploded part (looking inside a block it made), for the report.</summary>
        private static string TextAngle(Autodesk.AutoCAD.DatabaseServices.Entity part, Transaction tr)
        {
            if (part is MText m) return Degrees(m.Rotation).ToString("0.##");
            if (part is DBText t) return Degrees(t.Rotation).ToString("0.##");
            if (part is BlockReference br && br.BlockTableRecord.IsValid)
            {
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                foreach (ObjectId id in def)
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    double? a = o is MText mt ? mt.Rotation : o is DBText dt ? dt.Rotation : (double?)null;
                    if (a.HasValue) return Degrees(SheetTwist.Normal(a.Value + br.Rotation)).ToString("0.##");
                }
            }
            return null;
        }

        private static double Degrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>EXPORTTOAUTOCAD to the given file, AutoCAD 2018 format.</summary>
        private static void ExportToAutoCad(Document doc, string file)
        {
            // Xrefs are not bound: binding an unloaded xref from the copy fails (eWrongDatabase on the user's 000501 drawing,
            // 2026-11-16) and EXPORTTOAUTOCAD then writes no file. The sheets carry no xrefs.
            doc.Editor.Command("_.-EXPORTTOAUTOCAD", "_Format", "2018", "_Bind", "_No", "", file);
        }

        /// <summary>
        /// One CAD sheet: the export read again, every other layout deleted, this one current, unused items purged, saved as
        /// AutoCAD 2018. Returns how many unused items were purged.
        /// </summary>
        private static int Split(string exported, string layout, string target)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(exported, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                HostApplicationServices.WorkingDatabase = db;
                try
                {
                    LayoutManager lm = LayoutManager.Current;
                    lm.CurrentLayout = layout;
                    foreach (string other in LayoutNames(db).Where(n => !string.Equals(n, layout, StringComparison.OrdinalIgnoreCase)))
                        lm.DeleteLayout(other);
                    int purged = Purge(db);
                    db.SaveAs(target, DwgVersion.AC1032);
                    HostApplicationServices.WorkingDatabase = previous;
                    OpenOnSheet(target);
                    return purged;
                }
                finally { HostApplicationServices.WorkingDatabase = previous; }
            }
        }

        /// <summary>
        /// The saved sheet set to open on its one layout (Model tab off). The layout made current in the split's side database
        /// does not stick (102059 opened on Model, 2026-11-16), and TILEMODE set there crashed the background Civil 3D, so the
        /// file is read again on its own and saved once more.
        /// </summary>
        private static void OpenOnSheet(string file)
        {
            string temp = file + ".c3dpro.tmp";
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                db.TileMode = false;
                db.SaveAs(temp, DwgVersion.AC1032);
            }
            File.Copy(temp, file, true);
            File.Delete(temp);
        }

        /// <summary>PURGE all: unused blocks, layers, linetypes, text, dimension, multileader, table and multiline styles, until none is left.</summary>
        private static int Purge(Database db)
        {
            int total = 0;
            for (int pass = 0; pass < 20; pass++)
            {
                var ids = new ObjectIdCollection();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId table in new[] { db.BlockTableId, db.LayerTableId, db.LinetypeTableId, db.TextStyleTableId, db.DimStyleTableId, db.RegAppTableId })
                        foreach (ObjectId id in (SymbolTable)tr.GetObject(table, OpenMode.ForRead)) ids.Add(id);
                    foreach (ObjectId dictId in new[] { db.MLeaderStyleDictionaryId, db.TableStyleDictionaryId, db.MLStyleDictionaryId })
                        if (dictId.IsValid)
                            foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead)) ids.Add(e.Value);
                    db.Purge(ids);
                    foreach (ObjectId id in ids)
                        try { tr.GetObject(id, OpenMode.ForWrite).Erase(); }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) { C3DProLogger.Warn($"{Command}: {id.ObjectClass.DxfName} {id.Handle} could not be purged", ex); }
                    tr.Commit();
                }
                if (ids.Count == 0) break;
                total += ids.Count;
            }
            return total;
        }
    }
}
