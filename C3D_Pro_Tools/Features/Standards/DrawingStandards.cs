using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(C3DProTools.Features.Standards.DrawingStandards))]

// Drawing Standards (KSIA CAD standard, 2026-11-18): the drawing checked against the project's CAD rules: units, coordinate
// system, objects on 0 / Defpoints, layer names and line weights, text fonts, dimension styles and layers, viewport scales,
// xref paths, sheets (Sheet Manager's checks plus the title block date and scale formats), unused items. Read only, except two
// safe fixes: model units set to metres, xref paths made relative to the drawing. One UNDO.
namespace C3DProTools.Features.Standards
{
    internal sealed class StandardFinding
    {
        public string Area, Rule, Text;
        public List<string> Items = new List<string>();
        public string FixText;
        public Action<Transaction, Database, StringBuilder> Fix;
        public bool IsNote;
    }

    public sealed class DrawingStandards
    {
        [CommandMethod("C3DPRO", "DrawingStandards", CommandFlags.Modal)]
        public void Window()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (var form = new FormDrawingStandards(doc)) AcApp.ShowModalDialog(form);
        }

        [CommandMethod("C3DPRO", "C3DPROSTANDARDS", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var kw = new PromptKeywordOptions("\nDrawing standards") { AllowNone = true };
            kw.Keywords.Add("Check"); kw.Keywords.Add("Fix");
            kw.Keywords.Default = "Check";
            PromptResult kr = doc.Editor.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Check" : kr.StringResult;
            var log = new StringBuilder();
            try
            {
                List<StandardFinding> found = Check(doc);
                Describe(found, log);
                if (mode == "Fix") Fix(doc, found.Where(f => f.Fix != null).ToList(), log);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROSTANDARDS failed", ex);
                log.AppendLine($"C3DPROSTANDARDS failed: {ex.Message}. Nothing was changed.");
            }
            doc.Editor.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROSTANDARDS", mode) + "\n");
        }

        internal static void Describe(List<StandardFinding> found, StringBuilder log)
        {
            int problems = found.Count(f => !f.IsNote);
            log.AppendLine($"Drawing standards: {problems} problems ({found.Count(f => f.Fix != null)} fixable), {found.Count(f => f.IsNote)} notes.");
            foreach (StandardFinding f in found)
            {
                log.AppendLine($"{(f.IsNote ? "note" : "problem")} [{f.Area}, {f.Rule}] {f.Text}{(f.Fix != null ? " (fix: " + f.FixText + ")" : "")}");
                foreach (string i in f.Items.Take(40)) log.AppendLine("  " + i);
                if (f.Items.Count > 40) log.AppendLine($"  ... {f.Items.Count - 40} more");
            }
        }

        internal static void Fix(Document doc, List<StandardFinding> chosen, StringBuilder log)
        {
            int fixedCount = 0, failedCount = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (StandardFinding f in chosen.Where(x => x.Fix != null))
                {
                    try { f.Fix(tr, doc.Database, log); fixedCount++; }
                    catch (Exception ex) { failedCount++; log.AppendLine($"  not fixed: {f.Text}: {ex.Message}"); }
                }
                tr.Commit();
            }
            // Counted as done only when the whole fix went through; a fix that stopped part way is listed (review finding).
            log.AppendLine($"Fixed: {fixedCount} item(s){(failedCount > 0 ? $", {failedCount} not fixed or only in part (listed above)" : "")} (one UNDO).");
        }

        private static string Safe(Func<string> get) { try { return get() ?? ""; } catch (Exception) { return ""; } }

        /// <summary>Every check, read only; each finding carries its fix when it has a safe one.</summary>
        internal static List<StandardFinding> Check(Document doc)
        {
            var found = new List<StandardFinding>();
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Units(db, found);
                CoordinateSystem(db, found);
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var layers = lt.Cast<ObjectId>().Select(id => (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToList();
                Objects(tr, db, found);
                Layers(layers, found);
                Fonts(tr, db, found);
                Viewports(tr, db, found);
                Xrefs(tr, db, doc.Name, found);
                Sheets(tr, db, found);
                Unused(tr, db, found);
                tr.Commit();
            }
            return found;
        }

        private static void Units(Database db, List<StandardFinding> found)
        {
            if (db.Insunits == UnitsValue.Meters) return;
            found.Add(new StandardFinding
            {
                Area = "Drawing", Rule = "STD 5.2", Text = $"Model units are {db.Insunits}, not metres (INSUNITS 6).", FixText = "set the units to metres",
                Fix = (tr, d, log) => { d.Insunits = UnitsValue.Meters; log.AppendLine("  units set to metres"); }
            });
        }

        private static void CoordinateSystem(Database db, List<StandardFinding> found)
        {
            string code = "";
            try { code = CivilDocument.GetCivilDocument(db).Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? ""; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (!code.Equals(StandardRules.CoordinateSystem, StringComparison.OrdinalIgnoreCase))
                found.Add(new StandardFinding { Area = "Drawing", Rule = "STD 5.1, GIR 8.3", Text = $"Coordinate system is \"{(code.Length == 0 ? "none" : code)}\", not {StandardRules.CoordinateSystem} (Drawing Settings > Units and Zone)." });
        }

        /// <summary>Objects on 0 or Defpoints (QA 1.9 / 2.9), and dimensions: KSIA style, on a dimensioning (D) layer (STD 7).</summary>
        private static void Objects(Transaction tr, Database db, List<StandardFinding> found)
        {
            var onZero = new Dictionary<string, int>();
            var dimStyle = new Dictionary<string, int>();
            var dimLayer = new Dictionary<string, int>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (!btr.IsLayout) continue;
                string space = btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "model" : "sheets";
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    // Viewports, and Civil 3D's own network objects (the network, its part connectors), which always sit on 0.
                    if (e is Viewport || Regex.IsMatch(id.ObjectClass.DxfName, "^AECC_(NETWORK|PRESSURE_NETWORK|NETWORK_PART_CONNECTOR|GRAPH)", RegexOptions.IgnoreCase)) continue;
                    if (e.Layer == "0" || e.Layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
                    {
                        string k = $"{e.Layer}: {id.ObjectClass.DxfName} ({space})";
                        onZero[k] = onZero.TryGetValue(k, out int n) ? n + 1 : 1;
                    }
                    if (e is Dimension d)
                    {
                        string style = Safe(() => d.DimensionStyleName);
                        if (!style.StartsWith("KSI", StringComparison.OrdinalIgnoreCase)) dimStyle[style] = dimStyle.TryGetValue(style, out int n) ? n + 1 : 1;
                        string[] f = d.Layer.Split('-');
                        if (!(f.Length >= 4 && f[2] == "D")) dimLayer[d.Layer] = dimLayer.TryGetValue(d.Layer, out int m) ? m + 1 : 1;
                    }
                }
            }
            if (onZero.Count > 0)
                found.Add(new StandardFinding { Area = "Objects", Rule = "QA 1.9 / 2.9", Text = $"{onZero.Values.Sum()} objects on layer 0 or Defpoints.", Items = onZero.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}").ToList() });
            if (dimStyle.Count > 0)
                found.Add(new StandardFinding { Area = "Dimensions", Rule = "STD 7", Text = $"{dimStyle.Values.Sum()} dimensions use a style that is not a KSIA one (KSI_Standard / KSIA_DIM).", Items = dimStyle.OrderByDescending(x => x.Value).Select(x => $"{x.Value} on style {x.Key}").ToList() });
            if (dimLayer.Count > 0)
                found.Add(new StandardFinding { Area = "Dimensions", Rule = "STD 7", Text = $"{dimLayer.Values.Sum()} dimensions are not on a dimensioning layer (presentation D).", Items = dimLayer.OrderByDescending(x => x.Value).Select(x => $"{x.Value} on {x.Key}").ToList() });
        }

        /// <summary>Layer names in the KSIA form (STD 5.6) and line weights in the allowed set (STD 8); xref layers are the other drawing's.</summary>
        private static void Layers(List<LayerTableRecord> layers, List<StandardFinding> found)
        {
            var own = layers.Where(l => !l.IsDependent && l.Name != "0" && !l.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> badNames = own.Where(l => !LayerRules.IsStandardName(l.Name)).Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (badNames.Count > 0)
                found.Add(new StandardFinding { Area = "Layers", Rule = "STD 5.6", Text = $"{badNames.Count} of {own.Count} layers are not named Role-Uniclass code-Presentation-Description.", Items = badNames });
            var weights = own.Where(l => !StandardRules.LineWeightAllowed((int)l.LineWeight)).GroupBy(l => (int)l.LineWeight).OrderBy(g => g.Key).ToList();
            if (weights.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Layers", Rule = "STD 8", IsNote = true,
                    Text = $"{weights.Sum(g => g.Count())} layers have a line weight that is not an allowed plotted width (0.05, 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 mm). The KSIA template has the same; reported only.",
                    Items = weights.Select(g => $"{g.Key / 100.0:0.00} mm: {g.Count()} layers, e.g. {string.Join(", ", g.Take(3).Select(l => l.Name))}").ToList()
                });
        }

        /// <summary>Text styles in use whose font is not Arial Narrow (STD 6).</summary>
        private static void Fonts(Transaction tr, Database db, List<StandardFinding> found)
        {
            var styles = new ObjectIdCollection();
            var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in st) styles.Add(id);
            var unused = new ObjectIdCollection();
            foreach (ObjectId id in styles) unused.Add(id);
            db.Purge(unused);
            var unusedSet = new HashSet<ObjectId>(unused.Cast<ObjectId>());
            var bad = new List<string>();
            foreach (ObjectId id in styles)
            {
                if (unusedSet.Contains(id) || !(tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord s) || s.IsShapeFile || s.IsDependent || s.Name.Length == 0) continue;
                string face = Safe(() => s.Font.TypeFace), file = Safe(() => s.FileName);
                if (!StandardRules.FontAllowed(face, file)) bad.Add($"{s.Name}: {(face.Length > 0 ? face : file)}");
            }
            if (bad.Count > 0)
                found.Add(new StandardFinding { Area = "Text", Rule = "STD 6", Text = $"{bad.Count} text styles in use are not Arial Narrow.", Items = bad });
        }

        /// <summary>Sheet viewports at a scale of the allowed list (STD 9).</summary>
        private static void Viewports(Transaction tr, Database db, List<StandardFinding> found)
        {
            var bad = new List<string>();
            var notPreferred = new List<string>();
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry le in layouts)
            {
                if (!(tr.GetObject(le.Value, OpenMode.ForRead) is Layout lay) || lay.ModelType) continue;
                var space = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                bool first = true;
                foreach (ObjectId id in space)
                {
                    if (!id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(Viewport))) || !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                    // The sheet's own viewport: the first in the layout (Number is -1 on layouts not shown).
                    if (first || vp.Number == 1) { first = false; continue; }
                    if (vp.CustomScale <= 0) continue;
                    double n = 1000.0 / vp.CustomScale;  // model in metres, sheet in millimetres (as Sheet Manager)
                    string state = StandardRules.ScaleState(n);
                    string line = $"{lay.LayoutName}: viewport at 1:{n:0.##}";
                    if (state == "not allowed") bad.Add(line); else if (state == "not preferred") notPreferred.Add(line);
                }
            }
            // A note: a plan viewport may be scaled to fit its sheet part on purpose (as Sheet Manager keeps it); the scale written on the sheet is checked below.
            if (bad.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "STD 9", IsNote = true, Text = $"{bad.Count} viewports are at a scale that is not in the allowed list (kept when scaled to fit on purpose).", Items = bad });
            if (notPreferred.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "STD 9", IsNote = true, Text = $"{notPreferred.Count} viewports are at 1:125 or 1:150 (not preferred).", Items = notPreferred });
        }

        /// <summary>Xrefs: relative paths (STD 11, 14.2), found.</summary>
        private static void Xrefs(Transaction tr, Database db, string drawing, List<StandardFinding> found)
        {
            var absolute = new List<(ObjectId id, string name, string path, string rel)>();
            var missing = new List<string>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;
                string path = btr.PathName ?? "";
                if (!btr.IsResolved) missing.Add($"{btr.Name}: {path}");
                if (!StandardRules.IsRelativePath(path))
                    absolute.Add((id, btr.Name, path, File.Exists(drawing) ? StandardRules.RelativeTo(drawing, path) : null));
            }
            if (missing.Count > 0) found.Add(new StandardFinding { Area = "Xrefs", Rule = "STD 11", IsNote = true, Text = $"{missing.Count} xrefs are not loaded or not found.", Items = missing });
            var fixable = absolute.Where(a => a.rel != null).ToList();
            if (fixable.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Xrefs", Rule = "STD 11, 14.2", Text = $"{fixable.Count} xrefs use a full path, not a relative one.", Items = fixable.Select(a => $"{a.name}: {a.path} -> {a.rel}").ToList(),
                    FixText = "make the paths relative",
                    Fix = (t, d, log) =>
                    {
                        var failed = new List<string>();
                        foreach (var a in fixable)
                        {
                            try { ((BlockTableRecord)t.GetObject(a.id, OpenMode.ForWrite)).PathName = a.rel; log.AppendLine($"  xref {a.name}: {a.rel}"); }
                            catch (Exception ex) { failed.Add($"{a.name} ({ex.Message})"); }
                        }
                        if (failed.Count > 0) throw new InvalidOperationException($"{failed.Count} of {fixable.Count} xref paths not changed: {string.Join(", ", failed)}");
                    }
                });
            var other = absolute.Where(a => a.rel == null).ToList();
            if (other.Count > 0)
                found.Add(new StandardFinding { Area = "Xrefs", Rule = "STD 11, 14.2", Text = $"{other.Count} xrefs use a full path on another drive (or the drawing is not saved): move them into the project folder.", Items = other.Select(a => $"{a.name}: {a.path}").ToList() });
        }

        /// <summary>Sheet Manager's checks, and the title block formats: dates DD/MM/YYYY, scale 1:n / NTS (EIR Appendix E).</summary>
        private static void Sheets(Transaction tr, Database db, List<StandardFinding> found)
        {
            List<SheetRecord> sheets;
            SheetProfile p;
            try { p = SheetManagerSettings.Load().Profile; sheets = SheetManager.Read(tr, db, p); }
            catch (Exception ex)
            {
                // Said in the report: no sheet problems listed must not read as sheets checked (review finding).
                C3DProLogger.Warn("Drawing Standards: the sheets could not be read", ex);
                found.Add(new StandardFinding { Area = "Sheets", Rule = "Sheet Manager", Text = $"The sheets could not be read ({ex.Message}); they were not checked." });
                return;
            }
            if (sheets.Count == 0) return;
            List<SheetIssue> issues;
            try { issues = SheetManager.Check(tr, sheets, p); }
            catch (Exception ex)
            {
                C3DProLogger.Warn("Drawing Standards: the sheet checks failed", ex);
                found.Add(new StandardFinding { Area = "Sheets", Rule = "Sheet Manager", Text = $"The sheet checks failed ({ex.Message}); title block formats below were still checked." });
                issues = new List<SheetIssue>();
            }
            List<SheetIssue> problems = issues.Where(i => !i.IsNote).ToList();
            if (problems.Count > 0)
                found.Add(new StandardFinding { Area = "Sheets", Rule = "STD 5.4, Sheet Manager", Text = $"{problems.Count} sheet problems (Sheet Manager fixes most of them).", Items = problems.Select(i => $"{(i.Layout.Length > 0 ? i.Layout + ": " : "")}{i.Text}").ToList() });
            var dates = new List<string>();
            var scales = new List<string>();
            foreach (SheetRecord s in sheets.Where(x => !x.TitleBlockId.IsNull))
                foreach (KeyValuePair<string, string> a in s.Attributes)
                {
                    string v = (a.Value ?? "").Trim();
                    if (v.Length == 0 || v.StartsWith("DD", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(a.Key, "DATE", RegexOptions.IgnoreCase) && !StandardRules.DateOk(v)) dates.Add($"{s.Layout}: {a.Key} = {v}");
                    if (Regex.IsMatch(a.Key, "^SCALE$", RegexOptions.IgnoreCase) && !StandardRules.ScaleTextOk(v)) scales.Add($"{s.Layout}: {a.Key} = {v}");
                }
            if (dates.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "EIR App. E", Text = $"{dates.Count} title block dates are not written DD/MM/YYYY.", Items = dates });
            if (scales.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "STD 9, EIR App. E", Text = $"{scales.Count} title block scales are not 1:n of the allowed list, NTS or As Shown.", Items = scales });
        }

        /// <summary>Unused layers, blocks, styles and linetypes, to purge before issue (STD 13).</summary>
        private static void Unused(Transaction tr, Database db, List<StandardFinding> found)
        {
            var counts = new List<string>();
            int total = 0;
            void Count(ObjectId table, string what)
            {
                var ids = new ObjectIdCollection();
                foreach (ObjectId id in (SymbolTable)tr.GetObject(table, OpenMode.ForRead))
                    if (tr.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r && !r.IsDependent && !(r is BlockTableRecord b && (b.IsLayout || b.IsFromExternalReference))) ids.Add(id);
                db.Purge(ids);
                if (ids.Count > 0) { counts.Add($"{ids.Count} {what}"); total += ids.Count; }
            }
            Count(db.LayerTableId, "layers");
            Count(db.BlockTableId, "blocks");
            Count(db.TextStyleTableId, "text styles");
            Count(db.DimStyleTableId, "dimension styles");
            Count(db.LinetypeTableId, "linetypes");
            if (total > 0)
                found.Add(new StandardFinding { Area = "Drawing", Rule = "STD 13", IsNote = true, Text = $"{total} unused items to purge before issue (Drawing Cleaner does it on a copy).", Items = counts });
        }
    }
}
