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

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.Features.Standards.DrawingStandards))]
#endif

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
        /// <summary>A fix that comes ticked (false: shown with its fix, for you to tick after a look).</summary>
        public bool Tick = true;
    }

    public sealed class DrawingStandards
    {
        /// <summary>The KSIA template's dimension layer.</summary>
        internal const string DimLayer = "Z-Zz_20_20-D-Dim";

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
            kw.Keywords.Add("Check"); kw.Keywords.Add("Fix"); kw.Keywords.Add("Layers");
            kw.Keywords.Default = "Check";
            PromptResult kr = doc.Editor.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Check" : kr.StringResult;
            var log = new StringBuilder();
            try
            {
                if (mode == "Layers")
                {
                    // Layer names: the remembered choices and the sure suggestions (as ticked in the Layer names window).
                    List<LayerMapRow> rows = ScanLayers(doc);
                    log.AppendLine($"Layer names: {rows.Count(r => r.Kind == null)} layers not in the KSIA form, {rows.Count(r => r.Kind != null)} kinds of objects on 0 / Defpoints; " +
                                   $"{rows.Count(r => r.Tick)} ticked (your earlier choices and sure suggestions), {rows.Count(r => r.FrozenInViewport)} frozen in a viewport (left).");
                    foreach (LayerMapRow r in rows) log.AppendLine($"  {(r.Tick ? "[x]" : "[ ]")} {r.What} ({r.Objects}) -> {(r.Target.Length > 0 ? r.Target : "-")}  {r.Why}");
                    log.AppendLine(LayerMap.Apply(doc, rows.Where(r => r.Tick).ToList()));
                }
                else
                {
                    List<StandardFinding> found = Check(doc);
                    Describe(found, log);
                    if (mode == "Fix") Fix(doc, found.Where(f => f.Fix != null && f.Tick).ToList(), log);
                }
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROSTANDARDS failed", ex);
                log.AppendLine($"C3DPROSTANDARDS failed: {ex.Message}. Nothing was changed.");
            }
            doc.Editor.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROSTANDARDS", mode) + "\n");
        }

        /// <summary>The layer names table for the drawing (KSIA template layers, remembered choices, suggestions).</summary>
        internal static List<LayerMapRow> ScanLayers(Document doc)
        {
            List<string> template = LayerAudit.TemplateLayers(LayerAudit.TemplatePath());
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                List<LayerMapRow> rows = LayerMap.Scan(tr, doc.Database, template, LayerMap.Remembered());
                tr.Commit();
                return rows;
            }
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
                ObjectLayers.Check(tr, db, found);
                Objects(tr, db, found);
                Layers(layers, found);
                Names(tr, db, found);
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
            var dimIds = new List<ObjectId>();
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
                        if (!(f.Length >= 4 && f[2] == "D")) { dimLayer[d.Layer] = dimLayer.TryGetValue(d.Layer, out int m) ? m + 1 : 1; dimIds.Add(id); }
                    }
                }
            }
            if (onZero.Count > 0)
                found.Add(new StandardFinding { Area = "Objects", Rule = "QA 1.9 / 2.9", Text = $"{onZero.Values.Sum()} objects on layer 0 or Defpoints.", Items = onZero.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}").ToList() });
            if (dimStyle.Count > 0)
                found.Add(new StandardFinding { Area = "Dimensions", Rule = "STD 7", Text = $"{dimStyle.Values.Sum()} dimensions use a style that is not a KSIA one (KSI_Standard / KSIA_DIM).", Items = dimStyle.OrderByDescending(x => x.Value).Select(x => $"{x.Value} on style {x.Key}").ToList() });
            if (dimLayer.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Dimensions", Rule = "STD 7", Text = $"{dimLayer.Values.Sum()} dimensions are not on a dimensioning layer (presentation D).",
                    Items = dimLayer.OrderByDescending(x => x.Value).Select(x => $"{x.Value} on {x.Key}").ToList(),
                    // The KSIA template's dimension layer (your choice, 2026-11-18). Profile DIMs finds its own DIMs by their tag, not
                    // their layer, so moving them does not make it draw them again.
                    FixText = $"move them to {DimLayer}",
                    Fix = (t, d, log) =>
                    {
                        string layer = t.GetObject(LayerAudit.EnsureTemplateLayer(t, d, DimLayer, log), OpenMode.ForRead) is LayerTableRecord l ? l.Name : DimLayer;
                        int moved = 0;
                        var locked = new List<string>();
                        foreach (ObjectId id in dimIds)
                        {
                            try { ((Entity)t.GetObject(id, OpenMode.ForWrite)).Layer = layer; moved++; }
                            catch (Exception ex) { locked.Add(ex.Message.Contains("eOnLockedLayer") ? "on a locked layer" : ex.Message); }
                        }
                        log.AppendLine($"  {moved} dimensions moved to {layer}");
                        if (locked.Count > 0) throw new InvalidOperationException($"{locked.Count} dimensions not moved ({string.Join(", ", locked.Distinct())})");
                    }
                });
        }

        /// <summary>Layer names in the KSIA form (STD 5.6) and line weights in the allowed set (STD 8); xref layers are the other drawing's.</summary>
        private static void Layers(List<LayerTableRecord> layers, List<StandardFinding> found)
        {
            var own = layers.Where(l => !l.IsDependent && l.Name != "0" && !l.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)).ToList();
            // KSIA template layers count as standard, and an underscore inside a section is correct (your rule, 2026-11-23).
            var template = new HashSet<string>(LayerAudit.TemplateLayers(LayerAudit.TemplatePath()), StringComparer.OrdinalIgnoreCase);
            List<string> badNames = own.Where(l => !LayerSuggest.IsStandard(l.Name, template)).Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (badNames.Count > 0)
                found.Add(new StandardFinding { Area = "Layers", Rule = "STD 5.6", Text = $"{badNames.Count} of {own.Count} layers are not named Role-Uniclass code-Presentation-Description (Layer names window: pick a KSIA layer for each).", Items = badNames });
            var weights = own.Where(l => !StandardRules.LineWeightAllowed((int)l.LineWeight)).GroupBy(l => (int)l.LineWeight).OrderBy(g => g.Key).ToList();
            if (weights.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Layers", Rule = "STD 8", IsNote = true,
                    Text = $"{weights.Sum(g => g.Count())} layers have a line weight that is not an allowed plotted width (0.05, 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 mm). The KSIA template has the same; reported only.",
                    Items = weights.Select(g => $"{g.Key / 100.0:0.00} mm: {g.Count()} layers, e.g. {string.Join(", ", g.Take(3).Select(l => l.Name))}").ToList()
                });
        }

        /// <summary>
        /// Block and style names with another company's prefix (JAC, JCB, JCP) or an xref drawing's name: KSIA_ instead (your
        /// rule, 2026-11-24). Xref blocks, layouts and anonymous blocks keep their names; a new name already taken is listed, not used.
        /// </summary>
        private static void Names(Transaction tr, Database db, List<StandardFinding> found)
        {
            var renames = new List<(ObjectId id, string what, string from, string to)>();
            var clashes = new List<string>();
            void Table(ObjectId tableId, string what)
            {
                var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r) || r.IsDependent) continue;
                    if (r is BlockTableRecord b && (b.IsLayout || b.IsFromExternalReference || b.IsAnonymous)) continue;
                    string to = NamePrefix.Proposed(r.Name);
                    if (to == null) continue;
                    if (table.Has(to)) { clashes.Add($"{what} {r.Name}: {to} is already taken"); continue; }
                    renames.Add((id, what, r.Name, to));
                }
            }
            void Dictionary(ObjectId dictId, string what)
            {
                if (dictId.IsNull) return;
                var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                {
                    string to = NamePrefix.Proposed(e.Key);
                    if (to == null) continue;
                    if (dict.Contains(to)) { clashes.Add($"{what} {e.Key}: {to} is already taken"); continue; }
                    renames.Add((e.Value, what, e.Key, to));
                }
            }
            Table(db.BlockTableId, "block");
            Table(db.TextStyleTableId, "text style");
            Table(db.DimStyleTableId, "dimension style");
            Dictionary(db.MLeaderStyleDictionaryId, "multileader style");
            Dictionary(db.TableStyleDictionaryId, "table style");
            // AutoCAD's own internal copies (A$C…) keep their names; two names that would both become the same KSIA_ name are
            // listed for you, not renamed (drawing test 462124: several bound "…$0$block 2").
            renames.RemoveAll(r => r.to.StartsWith(NamePrefix.Ksia + "A$C", StringComparison.OrdinalIgnoreCase));
            foreach (var dup in renames.GroupBy(r => (r.what, r.to.ToUpperInvariant())).Where(g => g.Count() > 1).ToList())
            {
                clashes.Add($"{dup.Key.what}s {string.Join(", ", dup.Select(r => r.from))}: all would become {dup.First().to}");
                renames.RemoveAll(r => dup.Contains(r));
            }
            if (renames.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Names", Rule = "KSIA_ prefix",
                    Text = $"{renames.Count} block and style names carry another company's prefix or an xref drawing's name (KSIA_ instead).",
                    Items = renames.Select(r => $"{r.what} {r.from} -> {r.to}").ToList(),
                    FixText = "rename them with KSIA_",
                    Fix = (t, d, log) =>
                    {
                        int done = 0;
                        var failed = new List<string>();
                        foreach (var r in renames)
                        {
                            try
                            {
                                DBObject o = t.GetObject(r.id, OpenMode.ForWrite);
                                if (o is SymbolTableRecord s) s.Name = r.to;
                                else
                                {
                                    // Dictionary entries (multileader and table styles) are renamed in their dictionary.
                                    var dict = (DBDictionary)t.GetObject(o.OwnerId, OpenMode.ForWrite);
                                    dict.SetName(r.from, r.to);
                                }
                                done++;
                            }
                            catch (Exception ex) { failed.Add($"{r.from} ({ex.Message})"); }
                        }
                        log.AppendLine($"  {done} names changed to KSIA_");
                        if (failed.Count > 0) throw new InvalidOperationException($"{failed.Count} not renamed: {string.Join(", ", failed.Take(5))}");
                    }
                });
            if (clashes.Count > 0)
                found.Add(new StandardFinding { Area = "Names", Rule = "KSIA_ prefix", Text = $"{clashes.Count} names would take a KSIA_ name that is already used: merge them by hand.", Items = clashes });
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
            // Revision letter by stage (your rule, 2026-11-24): N# on IFC drawings, C# on SDR (shop drawings).
            var revisions = new List<string>();
            foreach (SheetRecord s in sheets.Where(x => !x.TitleBlockId.IsNull))
            {
                // The tags set in Sheet Manager's profile (review finding), else the usual names.
                string Attr(string tag, string fallback) =>
                    !string.IsNullOrWhiteSpace(tag) && s.HasAttr(tag) ? s.Attr(tag) : s.Attributes.FirstOrDefault(a => Regex.IsMatch(a.Key, fallback, RegexOptions.IgnoreCase)).Value;
                string stage = Attr(p.StageTag, "^STAGE$");
                string rev = Attr(p.RevisionTag, "^REV(_?NO|ISION)?$");
                string letter = NamePrefix.RevisionLetter(stage);
                if (letter != null && !string.IsNullOrWhiteSpace(rev) && !NamePrefix.RevisionFits(rev, letter))
                    revisions.Add($"{s.Layout}: stage \"{stage}\", revision {rev} (should be {letter}1, {letter}2…)");
            }
            if (revisions.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "Revision by stage", Text = $"{revisions.Count} sheets have a revision that does not match their stage (N for IFC, C for SDR).", Items = revisions });

            // North arrow and scale bar on every sheet (CRS: "provide the scale bar and north orientation"): a block named NORTH… / SCALE….
            var noNorth = new List<string>();
            var noScale = new List<string>();
            foreach (SheetRecord s in sheets)
            {
                if (!(tr.GetObject(s.LayoutId, OpenMode.ForRead) is Layout lay)) continue;
                // The blocks on the sheet and the blocks inside them (a north arrow often sits inside the title block); only those
                // that plot: not on a layer that is off or frozen (review finding). Layer 0 inside a block takes the block's layer.
                bool Shown(BlockReference br) =>
                    !(tr.GetObject(br.LayerId, OpenMode.ForRead) is LayerTableRecord l) || (!l.IsOff && !l.IsFrozen && l.IsPlottable);
                var names = new List<string>();
                var seen = new HashSet<ObjectId>();
                void Collect(ObjectId spaceId, int depth)
                {
                    if (depth > 4 || !seen.Add(spaceId)) return;
                    foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
                        if (id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(BlockReference))) && tr.GetObject(id, OpenMode.ForRead) is BlockReference br && Shown(br))
                        {
                            var def = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                            names.Add(def.Name);
                            if (!def.IsFromExternalReference) Collect(br.BlockTableRecord, depth + 1);
                        }
                }
                Collect(lay.BlockTableRecordId, 0);
                if (!names.Any(n => Regex.IsMatch(n, "NORTH", RegexOptions.IgnoreCase))) noNorth.Add(s.Layout);
                if (!names.Any(n => Regex.IsMatch(n, "SCALE", RegexOptions.IgnoreCase))) noScale.Add(s.Layout);
            }
            if (noNorth.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "CRS: north orientation", Text = $"{noNorth.Count} sheets have no north arrow (no block named NORTH…).", Items = noNorth });
            if (noScale.Count > 0) found.Add(new StandardFinding { Area = "Sheets", Rule = "CRS: scale bar", Text = $"{noScale.Count} sheets have no scale bar (no block named SCALE…).", Items = noScale });

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
