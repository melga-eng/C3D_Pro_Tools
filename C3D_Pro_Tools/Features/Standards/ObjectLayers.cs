using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// Civil 3D objects on their layer: each object (alignments, profiles, profile views, surfaces, labels, parts drawn in
    /// profile views…) belongs on the base layer of its kind in the drawing's own object-layer settings, one layer per kind
    /// (no "_name"). Objects drawn in model and on sheets only; layers frozen in a viewport are left with their objects.
    /// Also: the settings themselves, when they give each new object a layer of its own ("_*").
    /// </summary>
    internal static class ObjectLayers
    {
        public static void Check(Transaction tr, Database db, List<StandardFinding> found)
        {
            SettingsObjectLayers settings;
            try { settings = CivilDocument.GetCivilDocument(db).Settings.DrawingSettings.ObjectLayerSettings; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return; }

            // The base layer of each kind in this drawing ("0" or empty: the drawing sets none, nothing to check).
            var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var suffixed = new List<SettingsObjectLayerType>();
            foreach (SettingsObjectLayerType t in Enum.GetValues(typeof(SettingsObjectLayerType)))
            {
                try
                {
                    SettingsObjectLayer s = settings.GetObjectLayerSetting(t);
                    string name = s.LayerName ?? "";
                    if (name.Length == 0 || name == "0") continue;
                    bases[t.ToString()] = name;
                    if (s.Modifier != ObjectLayerModifierType.None) suffixed.Add(t);
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            if (bases.Count == 0) return;

            var template = new HashSet<string>(LayerAudit.TemplateLayers(LayerAudit.TemplatePath()), StringComparer.OrdinalIgnoreCase);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<(string setting, string kind, bool sure), List<(ObjectId id, string layer)>>();
            int leftFrozen = 0;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var entities = new List<(ObjectId id, Entity e)>();
            foreach (ObjectId bid in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (!btr.IsLayout) continue;
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (e is Viewport vp)
                    {
                        try { foreach (ObjectId f in vp.GetFrozenLayers()) if (tr.GetObject(f, OpenMode.ForRead) is LayerTableRecord l) frozen.Add(l.Name); }
                        catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        continue;
                    }
                    if (id.ObjectClass.DxfName.StartsWith("AECC", StringComparison.OrdinalIgnoreCase)) entities.Add((id, e));
                }
            }
            foreach ((ObjectId id, Entity e) in entities)
            {
                var kind = ObjectLayerRules.KindOf(id.ObjectClass.DxfName);
                if (kind == null || !bases.TryGetValue(kind.Value.setting, out string baseLayer)) continue;
                string layer = e.Layer;
                if (layer.Equals(baseLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (frozen.Contains(layer)) { leftFrozen++; continue; }
                bool sure = ObjectLayerRules.IsSure(layer, baseLayer, LayerSuggest.IsStandard(layer, template));
                var key = (kind.Value.setting, kind.Value.kind, sure);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(ObjectId, string)>();
                list.Add((id, layer));
            }

            foreach (var g in groups.OrderByDescending(x => x.Key.sure).ThenByDescending(x => x.Value.Count))
            {
                string baseLayer = bases[g.Key.setting];
                List<(ObjectId id, string layer)> items = g.Value;
                // Also a base layer that is off or frozen everywhere: the objects would vanish from every view (review finding).
                bool hidden = lt.Has(baseLayer) && tr.GetObject(lt[baseLayer], OpenMode.ForRead) is LayerTableRecord bl && (bl.IsFrozen || bl.IsOff);
                if (frozen.Contains(baseLayer) || hidden)
                {
                    // Moving onto a layer frozen in a viewport would make the objects vanish from that sheet: listed, no fix.
                    found.Add(new StandardFinding
                    {
                        Area = "Civil 3D objects", Rule = "Object layers",
                        Text = $"{items.Count} {g.Key.kind} are not on {baseLayer}, but {baseLayer} is {(hidden ? "off or frozen" : "frozen in a viewport")}: not moved (they would disappear).",
                        Items = items.GroupBy(x => x.layer).OrderByDescending(x => x.Count()).Select(x => $"{x.Count()} on {x.Key}").ToList(),
                    });
                    continue;
                }
                found.Add(new StandardFinding
                {
                    Area = "Civil 3D objects", Rule = "Object layers",
                    Text = $"{items.Count} {g.Key.kind} are not on {baseLayer}" + (g.Key.sure ? "." : " (they are on other KSIA layers: tick after a look)."),
                    Items = items.GroupBy(x => x.layer).OrderByDescending(x => x.Count()).Select(x => $"{x.Count()} on {x.Key}").ToList(),
                    Tick = g.Key.sure,
                    FixText = $"move them to {baseLayer}",
                    Fix = (t, d, log) => MoveTo(t, d, baseLayer, items, log),
                });
            }
            if (leftFrozen > 0)
                found.Add(new StandardFinding { Area = "Civil 3D objects", Rule = "Object layers", IsNote = true, Text = $"{leftFrozen} Civil 3D objects on layers frozen in a viewport are left as they are." });

            // New objects: the drawing's settings give each one a layer of its own ("_*"); one layer per kind means no modifier.
            List<SettingsObjectLayerType> toClear = suffixed.ToList();
            if (toClear.Count > 0)
                found.Add(new StandardFinding
                {
                    Area = "Civil 3D objects", Rule = "Object layer settings",
                    Text = $"For {toClear.Count} kinds of object, Civil 3D puts each new object on a layer of its own (base layer + \"_\" + its name).",
                    Items = toClear.Select(t => $"{t}: {bases[t.ToString()]}_*").ToList(),
                    FixText = "new objects on the base layer of their kind (no suffix)",
                    Fix = (t, d, log) =>
                    {
                        SettingsObjectLayers s = CivilDocument.GetCivilDocument(d).Settings.DrawingSettings.ObjectLayerSettings;
                        foreach (SettingsObjectLayerType k in toClear)
                        {
                            SettingsObjectLayer one = s.GetObjectLayerSetting(k);
                            one.Modifier = ObjectLayerModifierType.None;
                            one.ModifierValue = "";
                        }
                        log.AppendLine($"  {toClear.Count} object-layer settings: no suffix (new objects go on the base layer of their kind)");
                    }
                });
        }

        /// <summary>
        /// The objects to <paramref name="baseLayer"/>: from the drawing, else the KSIA template, else made with the properties
        /// of the layer most of them are on. Locked source layers are unlocked for the move and locked again.
        /// </summary>
        private static void MoveTo(Transaction tr, Database db, string baseLayer, List<(ObjectId id, string layer)> items, StringBuilder log)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(baseLayer))
            {
                bool fromTemplate = LayerAudit.TemplateLayers(LayerAudit.TemplatePath()).Contains(baseLayer, StringComparer.OrdinalIgnoreCase);
                if (fromTemplate) LayerAudit.EnsureTemplateLayer(tr, db, baseLayer, log);
                else
                {
                    string like = items.GroupBy(x => x.layer).OrderByDescending(x => x.Count()).First().Key;
                    var model = (LayerTableRecord)tr.GetObject(lt[like], OpenMode.ForRead);
                    var made = new LayerTableRecord { Name = baseLayer, Color = model.Color, LinetypeObjectId = model.LinetypeObjectId, LineWeight = model.LineWeight, IsPlottable = model.IsPlottable };
                    lt.UpgradeOpen();
                    lt.Add(made);
                    tr.AddNewlyCreatedDBObject(made, true);
                    log.AppendLine($"  layer {baseLayer} made (colour, linetype and weight of {like})");
                }
            }
            var unlocked = new List<LayerTableRecord>();
            foreach (string layer in items.Select(x => x.layer).Distinct(StringComparer.OrdinalIgnoreCase))
                if (lt.Has(layer) && tr.GetObject(lt[layer], OpenMode.ForRead) is LayerTableRecord l && l.IsLocked)
                {
                    l.UpgradeOpen();
                    l.IsLocked = false;
                    unlocked.Add(l);
                }
            int moved = 0;
            var failed = new List<string>();
            foreach ((ObjectId id, string _) in items)
            {
                try { ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Layer = baseLayer; moved++; }
                catch (Exception ex) { failed.Add(ex.Message); }
            }
            foreach (LayerTableRecord l in unlocked) l.IsLocked = true;
            log.AppendLine($"  {moved} object(s) to {baseLayer}");
            if (failed.Count > 0) throw new InvalidOperationException($"{failed.Count} not moved ({string.Join(", ", failed.Distinct().Take(3))})");
        }
    }
}
