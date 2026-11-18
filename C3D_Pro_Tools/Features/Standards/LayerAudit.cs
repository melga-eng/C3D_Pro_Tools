using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

[assembly: CommandClass(typeof(C3DProTools.Features.Standards.LayerAudit))]

// Network layers (KSIA, 2026-11-18): pipes, structures and pressure parts are exported to Navisworks by layer, so each part
// goes on the layer of its type (read from its description); their labels go on the drawing's labelling layer of their kind,
// without the per-network suffix (labels on layers frozen in a viewport, the tile layers, are left alone). A type the drawing
// does not show gets a KSIA template layer suggested (service, kind, existing or proposed), which the user ticks. Check lists
// what is wrong; Fix moves it (one UNDO), stops Civil 3D making per-network layers, points the networks' layers for new parts
// at the base layers, copies missing layers from the template and deletes the per-network layers left empty.
namespace C3DProTools.Features.Standards
{
    public sealed class LayerAudit
    {
        private const string RulesKey = "C3DPRO_LAYER_RULES";
        internal const string DefaultTemplate = @"D:\Pinnacle Working Files\_General Files\00-KSIA Project Document\06.02-cad and civil3d standads\CAD_STD\KSIA C3D Template.dwg";
        private static readonly Regex Clash = new Regex(@"cla?sh|\bcash\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The window (ribbon). C3DPROLAYERS is the same on the command line.</summary>
        [CommandMethod("C3DPRO", "NetworkLayers", CommandFlags.Modal)]
        public void Window()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (var form = new FormLayerAudit(doc)) AcApp.ShowModalDialog(form);
        }

        [CommandMethod("C3DPRO", "C3DPROLAYERS", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var kw = new PromptKeywordOptions("\nNetwork layers") { AllowNone = true };
            kw.Keywords.Add("Check"); kw.Keywords.Add("Fix"); kw.Keywords.Add("Data");
            kw.Keywords.Default = "Check";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Check" : kr.StringResult;
            if (mode == "Data")
            {
                PromptResult fr = ed.GetString(new PromptStringOptions("\nReport file <%TEMP%\\C3DPROLAYERS-Data.txt>: ") { AllowSpaces = true });
                if (fr.Status != PromptStatus.OK) return;
                string file = fr.StringResult.Trim().Trim('"');
                if (file.Length == 0) file = RunReport.File("C3DPROLAYERS", "Data");
                try { File.WriteAllText(file, LayerDataReport.Report(doc), new UTF8Encoding(false)); ed.WriteMessage($"\nC3DPROLAYERS Data written: {file}\n"); }
                catch (Exception ex) { C3DProLogger.Error("C3DPROLAYERS Data failed", ex); ed.WriteMessage($"\nC3DPROLAYERS failed: {ex.Message}\n"); }
                return;
            }
            PromptResult nr = ed.GetString(new PromptStringOptions("\nNetworks (wildcards; Enter for the ICT networks) <ICT>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            var log = new StringBuilder();
            try
            {
                Scan scan = Read(doc, nr.StringResult.Trim());
                List<LayerFinding> findings = scan.Check();
                Describe(scan, findings, log);
                // A template suggestion is never applied without the user: the command line skips it.
                if (mode == "Fix") Fix(doc, scan, findings.Where(f => f.To != null && !f.Suggested).ToList(), new FixOptions(), log);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROLAYERS failed", ex);
                log.AppendLine($"C3DPROLAYERS failed: {ex.Message}. Nothing was changed.");
            }
            ed.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROLAYERS", mode) + "\n");
        }

        internal sealed class FixOptions
        {
            public bool StopPerNetworkLayers = true;
            public bool DeleteEmptyLayers = true;
        }

        /// <summary>What the check read: the chosen networks' parts and labels, the drawing's base layers, the rules in force.</summary>
        internal sealed class Scan
        {
            public List<string> Networks = new List<string>();
            /// <summary>Every network, with its group: "ICT", "other" or "clash" (a clash copy of another service).</summary>
            public List<(string name, string group)> AllNetworks = new List<(string, string)>();
            public List<LayerItem> Items = new List<LayerItem>();
            public List<string> PartBases = new List<string>(), LabelBases = new List<string>();
            /// <summary>The drawing's object layer (no suffix) per kind: pipe, structure, pressure pipe, fitting, appurtenance, and "… label".</summary>
            public Dictionary<string, string> BaseOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Saved over learned: what the check and the fix use.</summary>
            public Dictionary<string, string> Rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Template layers offered for types with no rule.</summary>
            public Dictionary<string, string> Suggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Layers = new List<string>();
            public string TemplatePath;
            public List<string> TemplateLayers = new List<string>();
            /// <summary>Layers frozen in some viewport (the tile label layers): labels on them stay.</summary>
            public List<string> FrozenInViewports = new List<string>();

            public List<LayerFinding> Check() => LayerRules.Check(Items, Rules, PartBases, LabelBases, FrozenInViewports, Suggestions);
        }

        private static string Base(SettingsObjectLayers s, SettingsObjectLayerType t)
        {
            try { return s.GetObjectLayerSetting(t).LayerName; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return null; }
        }

        // Kind -> (part layer setting, label layer setting).
        private static readonly (string kind, SettingsObjectLayerType part, SettingsObjectLayerType label)[] Kinds =
        {
            ("pipe", SettingsObjectLayerType.Pipe, SettingsObjectLayerType.PipeLabeling),
            ("structure", SettingsObjectLayerType.Structure, SettingsObjectLayerType.StructureLabeling),
            ("pressure pipe", SettingsObjectLayerType.PressurePipe, SettingsObjectLayerType.PressurePipeLabeling),
            ("fitting", SettingsObjectLayerType.Fitting, SettingsObjectLayerType.FittingLabeling),
            ("appurtenance", SettingsObjectLayerType.Appurtenance, SettingsObjectLayerType.AppurtenanceLabeling),
        };

        internal static string GroupOf(string name, bool ictParts)
        {
            if (Clash.IsMatch(name)) return "clash";
            return name.IndexOf("ICT", StringComparison.OrdinalIgnoreCase) >= 0 || ictParts ? "ICT" : "other";
        }

        private static bool HasIctLayer(IEnumerable<ObjectId> ids, Transaction tr) =>
            ids.Take(200).Any(id => tr.GetObject(id, OpenMode.ForRead) is Entity e && e.Layer.StartsWith("IC-", StringComparison.OrdinalIgnoreCase));

        /// <summary>Reads the networks named (wildcards; empty: the ICT ones; or exactly <paramref name="exact"/>), their parts and labels.</summary>
        internal static Scan Read(Document doc, string names, IEnumerable<string> exact = null)
        {
            var scan = new Scan();
            Database db = doc.Database;
            var exactSet = exact == null ? null : new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase);
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(db);
                SettingsObjectLayers ol = civil.Settings.DrawingSettings.ObjectLayerSettings;
                foreach (var k in Kinds)
                {
                    scan.BaseOf[k.kind] = Base(ol, k.part);
                    scan.BaseOf[k.kind + " label"] = Base(ol, k.label);
                }
                scan.PartBases = Kinds.Select(k => scan.BaseOf[k.kind]).Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                scan.LabelBases = Kinds.Select(k => scan.BaseOf[k.kind + " label"]).Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                    if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord l && !l.IsDependent) scan.Layers.Add(l.Name);
                scan.Layers.Sort(StringComparer.OrdinalIgnoreCase);
                scan.FrozenInViewports = FrozenInViewports(tr, db);

                var parts = new Dictionary<ObjectId, LayerItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Network(string name, bool dead, List<(ObjectId id, string kind)> ids)
                {
                    if (dead) return;  // a data-shortcut reference is the source drawing's to fix
                    string group = GroupOf(name, HasIctLayer(ids.Select(x => x.id), tr));
                    if (seen.Add(name)) scan.AllNetworks.Add((name, group));
                    bool chosen = exactSet != null ? exactSet.Contains(name) : names.Length > 0 ? NamePatterns.Matches(names, name) : group == "ICT";
                    if (!chosen) return;
                    if (!scan.Networks.Contains(name)) scan.Networks.Add(name);
                    foreach ((ObjectId id, string kind) in ids)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                        var it = new LayerItem { Handle = id.Handle.ToString(), Kind = kind, Network = name, Layer = e.Layer };
                        if (e is Part p) { it.Name = Safe(() => p.Name); it.Description = Safe(() => p.Description); it.Size = Safe(() => p.PartSizeName); }
                        else if (e is PressurePart pp) { it.Name = Safe(() => pp.Name); it.Description = Safe(() => pp.PartDescription); it.Size = Safe(() => pp.PartFamilyName); }
                        parts[id] = it;
                        scan.Items.Add(it);
                    }
                }
                foreach (ObjectId nid in civil.GetPipeNetworkIds())
                    if (tr.GetObject(nid, OpenMode.ForRead) is Network n)
                        Network(n.Name, IsReference(n), n.GetPipeIds().Cast<ObjectId>().Select(i => (i, "pipe"))
                                                         .Concat(n.GetStructureIds().Cast<ObjectId>().Select(i => (i, "structure"))).ToList());
                foreach (ObjectId nid in civil.GetPressurePipeNetworkIds())
                    if (tr.GetObject(nid, OpenMode.ForRead) is PressurePipeNetwork pn)
                        Network(pn.Name, IsReference(pn), pn.GetPipeIds().Cast<ObjectId>().Select(i => (i, "pressure pipe"))
                                                           .Concat(pn.GetFittingIds().Cast<ObjectId>().Select(i => (i, "fitting")))
                                                           .Concat(pn.GetAppurtenanceIds().Cast<ObjectId>().Select(i => (i, "appurtenance"))).ToList());

                // Labels of those parts (plan and profile), wherever they sit in model space.
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(LabelBase)))) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is LabelBase lb)) continue;
                    ObjectId f = ObjectId.Null;
                    try { f = lb.FeatureId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (f.IsNull) continue;
                    // A profile label points at the part as drawn in the profile view: its model part is the one (review finding).
                    if (!parts.ContainsKey(f))
                    {
                        try
                        {
                            Autodesk.AutoCAD.DatabaseServices.DBObject pv = tr.GetObject(f, OpenMode.ForRead);
                            if (pv is ProfileViewPart pvp) f = pvp.ModelPartId;
                            else if (pv is ProfileViewPressurePart pvpp) f = pvpp.ModelPartId;
                        }
                        catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    }
                    if (!parts.TryGetValue(f, out LayerItem part)) continue;
                    scan.Items.Add(new LayerItem
                    {
                        Handle = id.Handle.ToString(), IsLabel = true, Kind = part.Kind + " label", Network = part.Network,
                        Name = part.Name, Description = id.ObjectClass.DxfName, Layer = lb.Layer, LabelBase = scan.BaseOf[part.Kind + " label"]
                    });
                }
                tr.Commit();
            }
            scan.Learned = LayerRules.Learn(scan.Items, scan.PartBases);
            scan.Saved = LayerRules.Read(ReadSaved(db));
            foreach (var kv in scan.Learned) scan.Rules[kv.Key] = kv.Value;
            foreach (var kv in scan.Saved) scan.Rules[kv.Key] = kv.Value;

            // Template layers for the types the drawing does not show.
            scan.TemplatePath = TemplatePath();
            scan.TemplateLayers = TemplateLayers(scan.TemplatePath);
            if (scan.TemplateLayers.Count > 0)
                foreach (var g in scan.Items.Where(i => !i.IsLabel).GroupBy(LayerRules.ItemKey, StringComparer.OrdinalIgnoreCase))
                {
                    if (g.Key.Length == 0 || scan.Rules.ContainsKey(g.Key) || scan.Rules.ContainsKey(LayerRules.BareKey(g.Key))) continue;
                    LayerItem first = g.GroupBy(i => i.Network).OrderByDescending(x => x.Count()).First().First();
                    string s = LayerRules.Suggest(first.Network, first.Description, first.Size, first.Kind == "structure", scan.TemplateLayers);
                    if (s != null) scan.Suggestions[g.Key] = s;
                }
            return scan;
        }

        private static bool IsReference(Autodesk.AutoCAD.DatabaseServices.DBObject o)
        {
            try { return o is Autodesk.Civil.DatabaseServices.Entity ce && ce.IsReferenceObject; } catch (Exception) { return false; }
        }

        private static List<string> FrozenInViewports(Transaction tr, Database db)
        {
            var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry le in layouts)
            {
                if (!(tr.GetObject(le.Value, OpenMode.ForRead) is Layout lay) || lay.ModelType) continue;
                var space = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId vid in space)
                {
                    if (!vid.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(Viewport))) || !(tr.GetObject(vid, OpenMode.ForRead) is Viewport vp)) continue;
                    try { foreach (ObjectId fl in vp.GetFrozenLayers()) if (tr.GetObject(fl, OpenMode.ForRead) is LayerTableRecord l) frozen.Add(l.Name); }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }
            return frozen.ToList();
        }

        private static string Safe(Func<string> get) { try { return get() ?? ""; } catch (Exception) { return ""; } }

        // ---- KSIA template: path remembered per user, layer names read once per file version ----

        private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NetworkLayers.json");

        internal static string TemplatePath()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string p = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.GetProperty("template").GetString();
                    if (!string.IsNullOrEmpty(p)) return p;
                }
            }
            catch (Exception probe) when (probe is IOException || probe is JsonException || probe is KeyNotFoundException || probe is UnauthorizedAccessException) { }
            return DefaultTemplate;
        }

        internal static void SaveTemplatePath(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Dictionary<string, string> { ["template"] = path }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("Network Layers: the template path could not be remembered", ex); }
        }

        private static string _cachedKey;
        private static List<string> _cachedLayers = new List<string>();

        internal static List<string> TemplateLayers(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<string>();
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (key == _cachedKey) return _cachedLayers;
            var names = new List<string>();
            try
            {
                using (var tdb = new Database(false, true))
                {
                    tdb.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                    using (Transaction tr = tdb.TransactionManager.StartOpenCloseTransaction())
                    {
                        var lt = (LayerTable)tr.GetObject(tdb.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId id in lt) if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord l && !l.IsDependent) names.Add(l.Name);
                        tr.Commit();
                    }
                }
            }
            catch (Exception ex) { C3DProLogger.Warn("Network Layers: the template could not be read", ex); return new List<string>(); }
            _cachedKey = key; _cachedLayers = names;
            return names;
        }

        internal static void Describe(Scan scan, List<LayerFinding> findings, StringBuilder log)
        {
            int parts = scan.Items.Count(i => !i.IsLabel), labels = scan.Items.Count - parts;
            log.AppendLine($"Networks: {string.Join(", ", scan.Networks)}; {parts} parts, {labels} labels; {scan.Rules.Count} type rules ({scan.Saved.Count} saved in the drawing)" +
                           (scan.TemplateLayers.Count > 0 ? $"; {scan.Suggestions.Count} template suggestions" : "; template not found: " + scan.TemplatePath) + ".");
            foreach (var g in findings.GroupBy(f => (f.Item.IsLabel ? "label" : "part") + ": " + f.Why + (f.To == null ? "" : " -> " + f.To)).OrderByDescending(g => g.Count()))
                log.AppendLine($"{g.Count()} {g.Key}");
            if (findings.Count == 0) log.AppendLine("Every part is on its type's layer and every label on its labelling layer.");
            foreach (LayerFinding f in findings.Take(500))
                log.AppendLine($"  {f.Item.Kind} {f.Item.Name} [{f.Item.Network}] on {f.Item.Layer}" +
                               (f.To == null ? $": no layer set for type \"{f.Key}\"" : $" -> {f.To}" + (f.Suggested ? " (suggested)" : "")));
        }

        internal static string ReadSaved(Database db)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(RulesKey) || !(tr.GetObject(nod.GetAt(RulesKey), OpenMode.ForRead) is Xrecord x) || x.Data == null) return "";
                var sb = new StringBuilder();
                foreach (TypedValue v in x.Data) if (v.TypeCode == (short)DxfCode.Text) sb.Append(v.Value as string);
                tr.Commit();
                return sb.ToString();
            }
        }

        private static void SaveRules(Transaction tr, Database db, string text)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            var values = new List<TypedValue>();
            for (int i = 0; i < text.Length; i += 1000) values.Add(new TypedValue((short)DxfCode.Text, text.Substring(i, Math.Min(1000, text.Length - i))));
            if (values.Count == 0) values.Add(new TypedValue((short)DxfCode.Text, ""));
            if (nod.Contains(RulesKey)) ((Xrecord)tr.GetObject(nod.GetAt(RulesKey), OpenMode.ForWrite)).Data = new ResultBuffer(values.ToArray());
            else
            {
                nod.UpgradeOpen();
                var x = new Xrecord { Data = new ResultBuffer(values.ToArray()) };
                nod.SetAt(RulesKey, x);
                tr.AddNewlyCreatedDBObject(x, true);
            }
        }

        /// <summary>A KSIA template layer in the drawing (copied from the template when missing), for other tools.</summary>
        internal static ObjectId EnsureTemplateLayer(Transaction tr, Database db, string name, StringBuilder log)
        {
            var scan = new Scan { TemplatePath = TemplatePath() };
            scan.TemplateLayers = TemplateLayers(scan.TemplatePath);
            // Not made up when the template cannot be read: a default layer would plot differently (review finding).
            if (!((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Has(name) && !scan.TemplateLayers.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"layer {name} is not in the drawing and the KSIA template ({scan.TemplatePath}) could not give it");
            return EnsureLayer(tr, db, name, null, scan, log);
        }

        /// <summary>
        /// The layer, made when missing: copied from the KSIA template when it has it (colour, linetype, line weight, plot),
        /// else with the properties of <paramref name="like"/>.
        /// </summary>
        private static ObjectId EnsureLayer(Transaction tr, Database db, string name, string like, Scan scan, StringBuilder log)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            if (scan.TemplateLayers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using (var tdb = new Database(false, true))
                    {
                        tdb.ReadDwgFile(scan.TemplatePath, FileOpenMode.OpenForReadAndAllShare, true, null);
                        ObjectId src;
                        using (Transaction ttr = tdb.TransactionManager.StartOpenCloseTransaction())
                        {
                            src = ((LayerTable)ttr.GetObject(tdb.LayerTableId, OpenMode.ForRead))[name];
                            ttr.Commit();
                        }
                        var map = new IdMapping();
                        tdb.WblockCloneObjects(new ObjectIdCollection { src }, db.LayerTableId, map, DuplicateRecordCloning.Ignore, false);
                    }
                    lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(name)) { log.AppendLine($"  layer {name} copied from the KSIA template"); return lt[name]; }
                }
                catch (Exception ex) { C3DProLogger.Warn($"Network Layers: {name} could not be copied from the template", ex); }
                // A template layer that could not be copied is not made up from another layer (review finding): the move fails.
                throw new InvalidOperationException($"layer {name} could not be copied from the KSIA template");
            }
            var l = new LayerTableRecord { Name = name };
            if (!string.IsNullOrEmpty(like) && lt.Has(like) && tr.GetObject(lt[like], OpenMode.ForRead) is LayerTableRecord from)
            {
                l.Color = from.Color; l.LinetypeObjectId = from.LinetypeObjectId; l.LineWeight = from.LineWeight; l.IsPlottable = from.IsPlottable;
            }
            lt.UpgradeOpen();
            ObjectId id = lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
            log.AppendLine($"  layer {name} made (not in the drawing{(scan.TemplateLayers.Count > 0 ? " nor the template" : "")})");
            return id;
        }

        /// <summary>
        /// Moves each finding's object to its layer, saves the rules, and (options) stops per-network layers and deletes those left
        /// empty. One transaction, one UNDO; an object that cannot be moved (a locked layer) is listed and the rest go on.
        /// </summary>
        internal static void Fix(Document doc, Scan scan, List<LayerFinding> chosen, FixOptions opt, StringBuilder log)
        {
            Database db = doc.Database;
            int moved = 0, failed = 0, deleted = 0;
            var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A suggestion the user ticked becomes the rule of its type.
            foreach (LayerFinding f in chosen.Where(x => x.Suggested && x.Key != null && x.To != null))
                if (!scan.Rules.ContainsKey(f.Key)) scan.Rules[f.Key] = f.To;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (LayerFinding f in chosen.Where(x => x.To != null))
                {
                    try
                    {
                        ObjectId id = db.GetObjectId(false, new Handle(Convert.ToInt64(f.Item.Handle, 16)), 0);
                        var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        EnsureLayer(tr, db, f.To, f.Item.Layer, scan, log);
                        string from = e.Layer;
                        e.Layer = f.To;
                        moved++;
                        if (LayerRules.IsSuffixLayer(from, scan.PartBases.Concat(scan.LabelBases))) emptied.Add(from);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.AppendLine($"  not moved: {f.Item.Kind} {f.Item.Name} on {f.Item.Layer}: {(ex.Message.Contains("eOnLockedLayer") ? "its layer is locked" : ex.Message)}");
                    }
                }
                SaveRules(tr, db, LayerRules.Write(scan.Rules));

                if (opt.StopPerNetworkLayers)
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(db);
                    SettingsObjectLayers ol = civil.Settings.DrawingSettings.ObjectLayerSettings;
                    // Only the kinds this run touched: a drawing with no pressure network keeps its pressure settings.
                    var kinds = new HashSet<string>(scan.Items.Select(i => i.IsLabel ? i.Kind.Replace(" label", "") : i.Kind));
                    foreach (var k in Kinds.Where(k => kinds.Contains(k.kind)))
                        foreach (SettingsObjectLayerType t in new[] { k.part, k.label })
                        {
                            try
                            {
                                SettingsObjectLayer s = ol.GetObjectLayerSetting(t);
                                if (s.Modifier != ObjectLayerModifierType.None) { s.Modifier = ObjectLayerModifierType.None; log.AppendLine($"  new {t} objects: layer {s.LayerName}, no per-network suffix"); }
                            }
                            catch (Exception ex) { log.AppendLine($"  the {t} layer setting could not be changed: {ex.Message}"); }
                        }
                    // The networks' own layers for new parts: the base layer instead of a per-network one.
                    void Repoint(string network, string current, string kind, Action<ObjectId> set)
                    {
                        string b = scan.BaseOf.TryGetValue(kind, out string v) ? v : null;
                        if (string.IsNullOrEmpty(b) || !LayerRules.IsSuffixLayer(current, scan.PartBases)) return;
                        try { set(EnsureLayer(tr, db, b, current, scan, log)); emptied.Add(current); }
                        catch (Exception ex) { log.AppendLine($"  network {network}: its {kind} layer for new parts could not be changed: {ex.Message}"); }
                    }
                    foreach (ObjectId nid in civil.GetPipeNetworkIds())
                    {
                        if (!(tr.GetObject(nid, OpenMode.ForRead) is Network n) || !scan.Networks.Contains(n.Name) || IsReference(n)) continue;
                        Repoint(n.Name, Safe(() => n.PipePlanLayerName), "pipe", id => { n.UpgradeOpen(); n.PipePlanLayerId = id; });
                        Repoint(n.Name, Safe(() => n.StructurePlanLayerName), "structure", id => { n.UpgradeOpen(); n.StructurePlanLayerId = id; });
                    }
                    foreach (ObjectId nid in civil.GetPressurePipeNetworkIds())
                    {
                        if (!(tr.GetObject(nid, OpenMode.ForRead) is PressurePipeNetwork n) || !scan.Networks.Contains(n.Name) || IsReference(n)) continue;
                        Repoint(n.Name, Safe(() => n.PipePlanLayerName), "pressure pipe", id => { n.UpgradeOpen(); n.PipePlanLayerId = id; });
                        Repoint(n.Name, Safe(() => n.FittingPlanLayerName), "fitting", id => { n.UpgradeOpen(); n.FittingPlanLayerId = id; });
                        Repoint(n.Name, Safe(() => n.AppurtenancePlanLayerName), "appurtenance", id => { n.UpgradeOpen(); n.AppurtenancePlanLayerId = id; });
                    }
                }

                if (opt.DeleteEmptyLayers && emptied.Count > 0)
                {
                    // Only per-network layers this fix emptied, and only those nothing else uses (Purge decides).
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var ids = new ObjectIdCollection();
                    foreach (string name in emptied) if (lt.Has(name) && name != "0") ids.Add(lt[name]);
                    db.Purge(ids);
                    foreach (ObjectId id in ids)
                    {
                        try { tr.GetObject(id, OpenMode.ForWrite).Erase(); deleted++; }
                        catch (Exception ex) { C3DProLogger.Warn("C3DPROLAYERS: an empty layer could not be deleted", ex); }
                    }
                }
                tr.Commit();
            }
            log.AppendLine($"Fixed: {moved} objects moved to their layers{(failed > 0 ? $", {failed} not moved (listed)" : "")}; {deleted} per-network layers deleted (empty); rules saved in the drawing.");
        }
    }
}
