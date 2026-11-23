using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace C3DProTools.Features.Standards
{
    /// <summary>One row of the layer names table: a whole non-standard layer, or one kind of object on layer 0 / Defpoints.</summary>
    internal sealed class LayerMapRow
    {
        public string Layer = "";
        /// <summary>Null for a whole layer; for layer 0 / Defpoints, the kind of object (DXF name) and where it is (model / sheets).</summary>
        public string Kind, Space;
        public int Objects;
        public bool FrozenInViewport, UsedInBlocks;
        public string Target = "", Why = "";
        public bool Tick;
        /// <summary>Remembered key: the layer, or "layer|kind|space" for a group on layer 0 / Defpoints.</summary>
        public string Key => Kind == null ? Layer : $"{Layer}|{Kind}|{Space}";
        public string What => Kind == null ? Layer : $"{Layer}: {Kind} ({Space})";
    }

    /// <summary>
    /// Layer names to the KSIA form: every own layer whose name is not standard, with a suggested layer (bound xref layers back to
    /// their own name; old names to a template layer), and the objects on layer 0 / Defpoints by kind. Only objects drawn in model
    /// and on sheets are moved (not inside blocks). Layers frozen in a viewport are left as they are, with their objects (your rule,
    /// 2026-11-23). Choices are remembered for the next drawing.
    /// </summary>
    internal static class LayerMap
    {
        /// <summary>Drawing tests point this at their own file, so a test never reads or writes the user's choices.</summary>
        public const string TestFileVariable = "C3DPRO_LAYERMAP_FILE";

        private static string FilePath => Environment.GetEnvironmentVariable(TestFileVariable) is string t && t.Length > 0 ? t
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "LayerMap.json");

        public static Dictionary<string, string> Remembered()
        {
            try
            {
                if (File.Exists(FilePath))
                    return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("Layer names: the remembered choices could not be read", ex); }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Best effort: the choices applied, kept for the next drawing (a blank target forgets the row).</summary>
        public static void Remember(IEnumerable<LayerMapRow> rows)
        {
            try
            {
                Dictionary<string, string> map = Remembered();
                foreach (LayerMapRow r in rows) { if (string.IsNullOrWhiteSpace(r.Target)) map.Remove(r.Key); else map[r.Key] = r.Target.Trim(); }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("Layer names: the choices could not be remembered", ex); }
        }

        /// <summary>The table for the drawing: remembered choices ticked, sure suggestions ticked, the rest left for you.</summary>
        public static List<LayerMapRow> Scan(Transaction tr, Database db, List<string> template, Dictionary<string, string> remembered)
        {
            var templateSet = new HashSet<string>(template, StringComparer.OrdinalIgnoreCase);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var layers = lt.Cast<ObjectId>().Select(id => (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToList();
            var names = layers.Select(l => l.Name).ToList();

            // Objects in model and on sheets per layer (layer 0 / Defpoints by kind); layers used inside blocks; layers frozen in viewports.
            var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var zero = new Dictionary<(string layer, string kind, string space), int>();
            var inBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frozen = new HashSet<ObjectId>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (btr.IsFromExternalReference || btr.IsDependent) continue;
                string space = btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "model" : "sheets";
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!btr.IsLayout) { inBlocks.Add(e.Layer); continue; }
                    if (e is Viewport vp)
                    {
                        try { foreach (ObjectId f in vp.GetFrozenLayers()) frozen.Add(f); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        continue;
                    }
                    count[e.Layer] = count.TryGetValue(e.Layer, out int n) ? n + 1 : 1;
                    if (IsZero(e.Layer) && !Regex.IsMatch(id.ObjectClass.DxfName, "^AECC_(NETWORK|PRESSURE_NETWORK|NETWORK_PART_CONNECTOR|GRAPH)", RegexOptions.IgnoreCase))
                    {
                        var k = (e.Layer, id.ObjectClass.DxfName, space);
                        zero[k] = zero.TryGetValue(k, out int z) ? z + 1 : 1;
                    }
                }
            }

            var rows = new List<LayerMapRow>();
            foreach (LayerTableRecord l in layers)
            {
                if (l.IsDependent || IsZero(l.Name) || LayerSuggest.IsStandard(l.Name, templateSet)) continue;
                var row = new LayerMapRow { Layer = l.Name, Objects = count.TryGetValue(l.Name, out int n) ? n : 0, FrozenInViewport = frozen.Contains(l.ObjectId), UsedInBlocks = inBlocks.Contains(l.Name) };
                if (remembered.TryGetValue(row.Key, out string chosen)) { row.Target = chosen; row.Tick = true; row.Why = "your earlier choice"; }
                else
                {
                    LayerSuggest.Suggestion s = LayerSuggest.Suggest(l.Name, templateSet, names);
                    row.Target = s?.Target ?? ""; row.Tick = s != null && s.Sure && !string.IsNullOrEmpty(s.Target); row.Why = s?.Why ?? "";
                }
                if (row.FrozenInViewport) { row.Tick = false; row.Why = "frozen in a viewport: left as it is"; }
                rows.Add(row);
            }
            foreach (KeyValuePair<(string layer, string kind, string space), int> g in zero.OrderByDescending(x => x.Value))
            {
                var row = new LayerMapRow { Layer = g.Key.layer, Kind = g.Key.kind, Space = g.Key.space, Objects = g.Value };
                row.FrozenInViewport = lt.Has(g.Key.layer) && frozen.Contains(lt[g.Key.layer]);
                if (remembered.TryGetValue(row.Key, out string chosen)) { row.Target = chosen; row.Tick = true; row.Why = "your earlier choice"; }
                else row.Why = "on " + g.Key.layer + ": you choose";
                // Layer 0 / Defpoints frozen in a viewport: its objects stay too (review finding).
                if (row.FrozenInViewport) { row.Tick = false; row.Why = "frozen in a viewport: left as it is"; }
                rows.Add(row);
            }
            return rows;
        }

        private static bool IsZero(string layer) => layer == "0" || layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Moves the objects of each ticked row (model and sheets only) to its target, one transaction (one UNDO). The target comes
        /// from the drawing, else from the KSIA template; a whole layer whose target is in neither is renamed to it. An emptied old
        /// layer is deleted; one still used inside blocks is kept. Rows frozen in a viewport are never touched.
        /// </summary>
        public static string Apply(Document doc, List<LayerMapRow> chosen)
        {
            var log = new StringBuilder();
            Database db = doc.Database;
            int moved = 0, renamed = 0, deleted = 0, failed = 0;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                // The entities of model and sheets once, by layer.
                var byLayer = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
                var frozen = new HashSet<ObjectId>();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                    if (!btr.IsLayout) continue;
                    foreach (ObjectId id in btr)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                        if (e is Viewport vp)
                        {
                            try { foreach (ObjectId f in vp.GetFrozenLayers()) frozen.Add(f); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                            continue;
                        }
                        if (!byLayer.TryGetValue(e.Layer, out List<ObjectId> list)) byLayer[e.Layer] = list = new List<ObjectId>();
                        list.Add(id);
                    }
                }
                List<LayerMapRow> todo = chosen.Where(x => x.Tick && !x.FrozenInViewport && !string.IsNullOrWhiteSpace(x.Target)).ToList();
                // A layer that is both a target and moved / renamed itself in the same run: its rows are left (review finding).
                var sources = new HashSet<string>(todo.Where(x => x.Kind == null).Select(x => x.Layer), StringComparer.OrdinalIgnoreCase);
                var targets = new HashSet<string>(todo.Select(x => x.Target.Trim()), StringComparer.OrdinalIgnoreCase);
                foreach (LayerMapRow r in todo)
                {
                    string target = r.Target.Trim();
                    if (target.Equals(r.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sources.Contains(target) || (r.Kind == null && targets.Contains(r.Layer)))
                    {
                        failed++;
                        log.AppendLine($"  {r.What}: not changed ({(sources.Contains(target) ? target + " is itself moved in this run" : r.Layer + " is also a target in this run")}; apply it on its own)");
                        continue;
                    }
                    if (lt.Has(target) && frozen.Contains(lt[target]))
                    {
                        failed++;
                        log.AppendLine($"  {r.What}: not changed ({target} is frozen in a viewport: its objects would disappear there)");
                        continue;
                    }
                    // One row at a time inside its own nested transaction: a failure leaves that row as it was.
                    using (Transaction one = tr.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            if (!lt.Has(r.Layer)) throw new InvalidOperationException("the layer is no longer in the drawing");
                            var from = (LayerTableRecord)one.GetObject(lt[r.Layer], OpenMode.ForWrite);
                            bool wasLocked = from.IsLocked;
                            if (r.Kind == null && !lt.Has(target) && !LayerAudit.TemplateLayers(LayerAudit.TemplatePath()).Contains(target, StringComparer.OrdinalIgnoreCase))
                            {
                                // Not in the drawing or the template: the layer takes the new name (its objects, blocks and settings go with it).
                                SymbolUtilityServices.ValidateSymbolName(target, false);
                                from.Name = target;
                                renamed++;
                                log.AppendLine($"  {r.What}: renamed to {target}");
                                one.Commit();
                                continue;
                            }
                            ObjectId to = lt.Has(target) ? lt[target] : LayerAudit.EnsureTemplateLayer(one, db, target, log);
                            string toName = ((LayerTableRecord)one.GetObject(to, OpenMode.ForRead)).Name;
                            if (wasLocked) from.IsLocked = false;
                            int n = 0;
                            foreach (ObjectId id in byLayer.TryGetValue(r.Layer, out List<ObjectId> ids) ? ids : new List<ObjectId>())
                            {
                                if (r.Kind != null && (!id.ObjectClass.DxfName.Equals(r.Kind, StringComparison.OrdinalIgnoreCase) || SpaceOf(one, id) != r.Space)) continue;
                                ((Entity)one.GetObject(id, OpenMode.ForWrite)).Layer = toName;
                                n++;
                            }
                            if (wasLocked) from.IsLocked = true;
                            moved += n;
                            string kept = "";
                            if (r.Kind == null)
                            {
                                var still = new ObjectIdCollection { from.ObjectId };
                                db.Purge(still);
                                if (still.Count == 1 && db.Clayer != from.ObjectId) { from.Erase(); deleted++; kept = "; old layer deleted"; }
                                else kept = r.UsedInBlocks ? "; old layer kept (used inside blocks)" : "; old layer kept (still in use)";
                            }
                            log.AppendLine($"  {r.What}: {n} object(s) to {toName}{kept}");
                            one.Commit();
                        }
                        catch (Exception ex)
                        {
                            one.Abort();
                            failed++;
                            log.AppendLine($"  {r.What}: not changed ({ex.Message})");
                        }
                    }
                }
                tr.Commit();
            }
            return $"Layer names: {moved} object(s) moved, {renamed} layer(s) renamed, {deleted} old layer(s) deleted, {failed} row(s) failed. UNDO reverses it." +
                   Environment.NewLine + log;
        }

        private static string SpaceOf(Transaction tr, ObjectId id) =>
            tr.GetObject(id, OpenMode.ForRead) is Entity e && tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord b &&
            b.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "model" : "sheets";
    }
}
