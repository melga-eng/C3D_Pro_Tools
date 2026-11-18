using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

// Network layers (KSIA, 2026-11-18): pipes and structures are exported to Navisworks by layer, so each part goes on the layer
// of its type (read from its description); their labels go on the drawing's labelling layer of their kind, without the
// per-network suffix (tile layers frozen per viewport are left alone). Check lists what is wrong; Fix moves it (one UNDO),
// stops Civil 3D making per-network layers, points the networks' layers for new parts at the base layers and deletes the
// per-network layers left empty. Rules are learned from the drawing and saved in it.
namespace C3DProTools.Features.Standards
{
    public sealed class LayerAudit
    {
        private const string RulesKey = "C3DPRO_LAYER_RULES";

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
                if (mode == "Fix") Fix(doc, scan, findings.Where(f => f.To != null).ToList(), new FixOptions(), log);
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
            public List<string> AllNetworks = new List<string>();
            public List<LayerItem> Items = new List<LayerItem>();
            public List<string> PartBases = new List<string>(), LabelBases = new List<string>();
            public string PipeBase, StructureBase, PipeLabelBase, StructureLabelBase;
            public Dictionary<string, string> Learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Saved over learned: what the check and the fix use.</summary>
            public Dictionary<string, string> Rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Layers = new List<string>();
            /// <summary>Layers frozen in some viewport (the tile label layers): labels on them stay.</summary>
            public List<string> FrozenInViewports = new List<string>();

            public List<LayerFinding> Check() => LayerRules.Check(Items, Rules, PartBases, LabelBases, FrozenInViewports);
        }

        private static string Base(SettingsObjectLayers s, SettingsObjectLayerType t)
        {
            try { return s.GetObjectLayerSetting(t).LayerName; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return null; }
        }

        /// <summary>
        /// The ICT networks by default: a name with "ICT", or a part on an "IC-" layer; never a clash copy of other services
        /// ("…-CLASH", "…-Cash-1": names in the KSIA drawings), which the user leaves out for now (2026-11-18).
        /// </summary>
        internal static bool IsIct(Network n, Transaction tr)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"cla?sh|\bcash\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
            if (n.Name.IndexOf("ICT", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (ObjectId id in n.GetPipeIds().Cast<ObjectId>().Concat(n.GetStructureIds().Cast<ObjectId>()).Take(200))
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e && e.Layer.StartsWith("IC-", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Reads the networks named (wildcards; empty: the ICT ones, or those of the last fix), their parts and labels.</summary>
        internal static Scan Read(Document doc, string names, IEnumerable<string> exact = null)
        {
            var scan = new Scan();
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(db);
                SettingsObjectLayers ol = civil.Settings.DrawingSettings.ObjectLayerSettings;
                scan.PipeBase = Base(ol, SettingsObjectLayerType.Pipe);
                scan.StructureBase = Base(ol, SettingsObjectLayerType.Structure);
                scan.PipeLabelBase = Base(ol, SettingsObjectLayerType.PipeLabeling);
                scan.StructureLabelBase = Base(ol, SettingsObjectLayerType.StructureLabeling);
                scan.PartBases = new[] { scan.PipeBase, scan.StructureBase }.Where(b => !string.IsNullOrEmpty(b)).ToList();
                scan.LabelBases = new[] { scan.PipeLabelBase, scan.StructureLabelBase }.Where(b => !string.IsNullOrEmpty(b)).ToList();

                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                    if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord l && !l.IsDependent) scan.Layers.Add(l.Name);
                scan.Layers.Sort(StringComparer.OrdinalIgnoreCase);
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
                scan.FrozenInViewports = frozen.ToList();

                var exactSet = exact == null ? null : new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase);
                var parts = new Dictionary<ObjectId, LayerItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId nid in civil.GetPipeNetworkIds())
                {
                    if (!(tr.GetObject(nid, OpenMode.ForRead) is Network n)) continue;
                    bool dead = false;
                    try { dead = n.IsReferenceObject; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (dead) continue;  // a data-shortcut reference is the source drawing's to fix
                    if (seen.Add(n.Name)) scan.AllNetworks.Add(n.Name);
                    bool chosen = exactSet != null ? exactSet.Contains(n.Name) : names.Length > 0 ? NamePatterns.Matches(names, n.Name) : IsIct(n, tr);
                    if (!chosen) continue;
                    scan.Networks.Add(n.Name);
                    foreach (ObjectId id in n.GetPipeIds().Cast<ObjectId>().Concat(n.GetStructureIds().Cast<ObjectId>()))
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Part p)) continue;
                        var it = new LayerItem
                        {
                            Handle = id.Handle.ToString(), Kind = p is Pipe ? "pipe" : "structure", Network = n.Name, Layer = p.Layer,
                            Name = Safe(() => p.Name), Description = Safe(() => p.Description), Size = Safe(() => p.PartSizeName)
                        };
                        parts[id] = it;
                        scan.Items.Add(it);
                    }
                }
                // Labels of those parts (plan and profile), wherever they sit in model space.
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(LabelBase)))) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is LabelBase lb)) continue;
                    ObjectId f = ObjectId.Null;
                    try { f = lb.FeatureId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (f.IsNull || !parts.TryGetValue(f, out LayerItem part)) continue;
                    bool structure = part.Kind == "structure";
                    scan.Items.Add(new LayerItem
                    {
                        Handle = id.Handle.ToString(), IsLabel = true, Kind = structure ? "structure label" : "pipe label", Network = part.Network,
                        Name = part.Name, Description = id.ObjectClass.DxfName, Layer = lb.Layer, LabelBase = structure ? scan.StructureLabelBase : scan.PipeLabelBase
                    });
                }
                tr.Commit();
            }
            scan.Learned = LayerRules.Learn(scan.Items, scan.PartBases);
            scan.Saved = LayerRules.Read(ReadSaved(db));
            foreach (var kv in scan.Learned) scan.Rules[kv.Key] = kv.Value;
            foreach (var kv in scan.Saved) scan.Rules[kv.Key] = kv.Value;
            return scan;
        }

        private static string Safe(Func<string> get) { try { return get() ?? ""; } catch (Exception) { return ""; } }

        internal static void Describe(Scan scan, List<LayerFinding> findings, StringBuilder log)
        {
            int parts = scan.Items.Count(i => !i.IsLabel), labels = scan.Items.Count - parts;
            log.AppendLine($"Networks: {string.Join(", ", scan.Networks)}; {parts} parts, {labels} labels; {scan.Rules.Count} type rules ({scan.Saved.Count} saved in the drawing).");
            foreach (var g in findings.GroupBy(f => (f.Item.IsLabel ? "label" : "part") + ": " + f.Why + (f.To == null ? "" : " -> " + f.To)).OrderByDescending(g => g.Count()))
                log.AppendLine($"{g.Count()} {g.Key}");
            if (findings.Count == 0) log.AppendLine("Every part is on its type's layer and every label on its labelling layer.");
            foreach (LayerFinding f in findings.Take(500))
                log.AppendLine($"  {f.Item.Kind} {f.Item.Name} [{f.Item.Network}] on {f.Item.Layer}" + (f.To == null ? $": no layer set for type \"{f.Key}\"" : $" -> {f.To}"));
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

        /// <summary>The layer, made when missing (properties of <paramref name="like"/> when given).</summary>
        private static ObjectId EnsureLayer(Transaction tr, Database db, string name, string like)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            var l = new LayerTableRecord { Name = name };
            if (!string.IsNullOrEmpty(like) && lt.Has(like) && tr.GetObject(lt[like], OpenMode.ForRead) is LayerTableRecord src)
            {
                l.Color = src.Color; l.LinetypeObjectId = src.LinetypeObjectId; l.LineWeight = src.LineWeight; l.IsPlottable = src.IsPlottable;
            }
            lt.UpgradeOpen();
            ObjectId id = lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
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
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (LayerFinding f in chosen.Where(x => x.To != null))
                {
                    try
                    {
                        ObjectId id = db.GetObjectId(false, new Handle(Convert.ToInt64(f.Item.Handle, 16)), 0);
                        var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        EnsureLayer(tr, db, f.To, f.Item.Layer);
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
                    foreach (SettingsObjectLayerType t in new[] { SettingsObjectLayerType.Pipe, SettingsObjectLayerType.Structure, SettingsObjectLayerType.PipeLabeling, SettingsObjectLayerType.StructureLabeling })
                    {
                        try
                        {
                            SettingsObjectLayer s = ol.GetObjectLayerSetting(t);
                            if (s.Modifier != ObjectLayerModifierType.None) { s.Modifier = ObjectLayerModifierType.None; log.AppendLine($"  new {t} objects: layer {s.LayerName}, no per-network suffix"); }
                        }
                        catch (Exception ex) { log.AppendLine($"  the {t} layer setting could not be changed: {ex.Message}"); }
                    }
                    // The networks' own layers for new parts: the base layer instead of a per-network one.
                    foreach (ObjectId nid in civil.GetPipeNetworkIds())
                    {
                        if (!(tr.GetObject(nid, OpenMode.ForRead) is Network n) || !scan.Networks.Contains(n.Name)) continue;
                        try
                        {
                            string pl = n.PipePlanLayerName, sl = n.StructurePlanLayerName;
                            if (LayerRules.IsSuffixLayer(pl, scan.PartBases) && !string.IsNullOrEmpty(scan.PipeBase))
                            { n.UpgradeOpen(); n.PipePlanLayerId = EnsureLayer(tr, db, scan.PipeBase, pl); emptied.Add(pl); }
                            if (LayerRules.IsSuffixLayer(sl, scan.PartBases) && !string.IsNullOrEmpty(scan.StructureBase))
                            { n.UpgradeOpen(); n.StructurePlanLayerId = EnsureLayer(tr, db, scan.StructureBase, sl); emptied.Add(sl); }
                        }
                        catch (Exception ex) { log.AppendLine($"  network {n.Name}: its layers for new parts could not be changed: {ex.Message}"); }
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
