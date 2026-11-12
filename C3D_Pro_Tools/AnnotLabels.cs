using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using CLabel = Autodesk.Civil.DatabaseServices.Label;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>A network label in the drawing: what it labels, where it is anchored and where its text sits.</summary>
    internal sealed class LabelInfo
    {
        public ObjectId Id;
        public LabelKind Kind;
        public string Style = "";
        /// <summary>The pipe or structure labelled (for profile labels: the model part behind the profile view part).</summary>
        public ObjectId PartId;
        /// <summary>The profile view of a profile label, and the part's representation in it.</summary>
        public ObjectId ViewId, ViewPartId;
        public Point3d Anchor, Location;
        public bool Dragged, Locked;
        public bool IsProfile => Kind == LabelKind.ProfilePipe || Kind == LabelKind.ProfileStructure;
        public bool IsPipe => Kind == LabelKind.PlanPipe || Kind == LabelKind.ProfilePipe;
    }

    /// <summary>One label of a label set: its kind, style and where along the part it goes.</summary>
    internal sealed class LabelSetItem
    {
        public LabelKind Kind { get; set; }
        public string Style { get; set; } = "";
        /// <summary>Position along a pipe (0 = start, 1 = end).</summary>
        public double Ratio { get; set; } = 0.5;
        public bool Dragged { get; set; }
        public override string ToString() => $"{KindText(Kind)}: {Style}" + (Kind == LabelKind.PlanPipe || Kind == LabelKind.ProfilePipe ? $" at {Ratio:0.##}" : "");

        public static string KindText(LabelKind k) =>
            k == LabelKind.PlanPipe ? "Plan pipe" : k == LabelKind.PlanStructure ? "Plan structure" : k == LabelKind.ProfilePipe ? "Profile pipe" : "Profile structure";
    }

    /// <summary>A named set of labels to put on parts, captured from an example part.</summary>
    internal sealed class LabelSet
    {
        public string Name { get; set; } = "";
        public List<LabelSetItem> Items { get; set; } = new List<LabelSetItem>();
        public override string ToString() => Name;
    }

    internal enum ExistingLabels { AddMissing, SkipLabelled, Replace }

    /// <summary>
    /// Breaks an entity apart in memory (nothing in the drawing changes), going into nested blocks. Every piece and collection
    /// is freed when the walk ends, whatever happens during it, so a visitor must not keep a piece.
    /// </summary>
    internal static class ExplodedPieces
    {
        /// <param name="blockDepth">How many levels of blocks to go into; a block deeper than that is handed over as it is.</param>
        public static void ForEach(Entity entity, int blockDepth, Action<DBObject> visit) => Walk(entity, 0, blockDepth, visit);

        private static void Walk(Entity entity, int depth, int blockDepth, Action<DBObject> visit)
        {
            using (DBObjectCollection parts = new DBObjectCollection())
            {
                try { entity.Explode(parts); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                // Listed first, so the pieces not reached yet are freed too if a visit throws.
                List<DBObject> pieces = parts.Cast<DBObject>().ToList();
                try
                {
                    foreach (DBObject piece in pieces)
                    {
                        if (piece is BlockReference br && depth < blockDepth) Walk(br, depth + 1, blockDepth, visit);
                        else visit(piece);
                    }
                }
                finally
                {
                    foreach (DBObject piece in pieces) try { piece.Dispose(); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
                }
            }
        }
    }

    /// <summary>Finding, measuring, locking and creating network labels.</summary>
    internal static class AnnotLabels
    {
        public const string RegApp = "C3DPRO_ANNOT";

        public static LabelKind? KindOf(DBObject obj) =>
            obj is PipeProfileLabel ? LabelKind.ProfilePipe : obj is StructureProfileLabel ? LabelKind.ProfileStructure
            : obj is PipeLabel ? LabelKind.PlanPipe : obj is StructureLabel ? LabelKind.PlanStructure : (LabelKind?)null;

        /// <summary>Every gravity network label in model space, with its part and anchor.</summary>
        public static List<LabelInfo> Scan(Transaction tr, Database db)
        {
            List<LabelInfo> list = new List<LabelInfo>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                string cls = id.ObjectClass.Name;
                if (!(cls.Contains("PipeLabel") || cls.Contains("StructureLabel") || cls.Contains("ProfileLabel"))) continue;
                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); } catch (Exception) { continue; }
                LabelKind? kind = KindOf(obj);
                if (kind == null || !(obj is FeatureLabel fl)) continue;
                LabelInfo info = Read(tr, fl, kind.Value);
                if (info != null) list.Add(info);
            }
            return list;
        }

        private static LabelInfo Read(Transaction tr, FeatureLabel fl, LabelKind kind)
        {
            LabelInfo info = new LabelInfo { Id = fl.ObjectId, Kind = kind };
            try { info.Style = fl.StyleName; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { info.Location = fl.LabelLocation; } catch (Exception) { return null; }
            try { info.Anchor = fl.AnchorInfo.Location; } catch (Exception) { info.Anchor = info.Location; }
            try { info.Dragged = fl.Dragged; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            ObjectId feature = ObjectId.Null;
            try { feature = fl.FeatureId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (info.IsProfile)
            {
                try { info.ViewId = fl.ViewId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (!feature.IsNull && tr.GetObject(feature, OpenMode.ForRead) is ProfileViewPart vp) { info.PartId = vp.ModelPartId; info.ViewPartId = feature; }
                else info.PartId = feature;
            }
            else info.PartId = feature;
            info.Locked = IsLocked(fl);
            return info;
        }

        /// <summary>
        /// The corners of the label's text and frame (not its leader) in model coordinates, or null when the label cannot be
        /// measured (it is broken apart in memory; nothing in the drawing changes).
        /// </summary>
        public static List<Point3d> Measure(Entity label) => Measure(label, out _, out _);

        public static List<Point3d> Measure(Entity label, out double textHeight) => Measure(label, out textHeight, out _);

        /// <summary>As <see cref="Measure(Entity)"/>, with the height of the label's first text in the model (0 when it has none).</summary>
        /// <param name="exact">False when only the label's extents could be read (they include its leader).</param>
        public static List<Point3d> Measure(Entity label, out double textHeight, out bool exact)
        {
            exact = true;
            List<Point3d> pts = new List<Point3d>();
            double firstText = 0;
            textHeight = 0;
            ExplodedPieces.ForEach(label, 3, o =>
            {
                switch (o)
                {
                    case Leader _:
                    case MLeader _:
                        break;
                    case MText mt:
                        if (firstText == 0 && !string.IsNullOrWhiteSpace(mt.Text)) firstText = mt.TextHeight;
                        List<Point3d> corners = MTextCorners(mt);
                        if (corners.Count > 0) pts.AddRange(corners); else AddExtents(mt, pts);
                        break;
                    case Line l:
                        pts.Add(l.StartPoint); pts.Add(l.EndPoint);
                        break;
                    case Polyline pl:
                        for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint3dAt(i));
                        break;
                    case Entity e:
                        AddExtents(e, pts);
                        break;
                }
            });
            textHeight = firstText;
            if (pts.Count >= 2) return pts;
            // Labels Civil 3D will not break apart (profile structure labels, for one): their extents, leader included.
            pts.Clear();
            exact = false;
            AddExtents(label, pts);
            return pts.Count >= 2 ? pts : null;
        }

        /// <summary>
        /// The plotted height (paper units) of a label style's first text. Civil 3D shows a label at its plotted size in every
        /// viewport, so this over the label's text height in the model gives how big the label appears on paper.
        /// </summary>
        public static double StyleTextHeight(Transaction tr, ObjectId styleId, double paperPerUnit)
        {
            try
            {
                Autodesk.Civil.DatabaseServices.Styles.LabelStyle style = (Autodesk.Civil.DatabaseServices.Styles.LabelStyle)tr.GetObject(styleId, OpenMode.ForRead);
                ObjectIdCollection comps = style.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text);
                foreach (ObjectId c in comps)
                {
                    double h = ((Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent)tr.GetObject(c, OpenMode.ForRead)).Text.Height.Value;
                    if (h > 0) return h * paperPerUnit;
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return 0;
        }

        /// <summary>Paper units (mm, or inches for imperial drawings) per drawing unit, for plotted heights stored in drawing units.</summary>
        public static double PaperPerUnit(Database db)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters: return 1;
                case UnitsValue.Centimeters: return 10;
                case UnitsValue.Feet: return 12;
                case UnitsValue.Inches: return 1;
                default: return 1000;
            }
        }

        private static void AddExtents(Entity e, List<Point3d> pts)
        {
            try
            {
                Extents3d x = e.GeometricExtents;
                pts.Add(x.MinPoint); pts.Add(x.MaxPoint);
                pts.Add(new Point3d(x.MinPoint.X, x.MaxPoint.Y, 0)); pts.Add(new Point3d(x.MaxPoint.X, x.MinPoint.Y, 0));
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        // ---------------- Locking hand-placed labels ----------------

        public static bool IsLocked(DBObject obj)
        {
            try
            {
                ResultBuffer rb = obj.GetXDataForApplication(RegApp);
                return rb != null && rb.AsArray().Any(v => v.Value is string s && s == "locked");
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Background masking on or off for labels' text: on the text components of the label styles the Civil 3D labels use (a
        /// style is shared: every label of that style changes), and on the text of multileaders directly. Returns the styles and
        /// multileaders changed.
        /// </summary>
        public static (int styles, int leaders) SetMask(Transaction tr, IEnumerable<ObjectId> ids, bool mask)
        {
            HashSet<ObjectId> styles = new HashSet<ObjectId>();
            int leaders = 0;
            foreach (ObjectId id in ids)
            {
                if (id.IsErased) continue;
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is CLabel label)
                {
                    try { if (!label.StyleId.IsNull) styles.Add(label.StyleId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                else if (obj is MLeader ml && ml.ContentType == ContentType.MTextContent)
                {
                    ml.UpgradeOpen();
                    MText text = ml.MText;
                    text.BackgroundFill = mask;
                    if (mask) { text.UseBackgroundColor = true; text.BackgroundScaleFactor = 1.5; }
                    ml.MText = text;
                    leaders++;
                }
            }
            int changed = 0;
            foreach (ObjectId sid in styles)
            {
                if (!(tr.GetObject(sid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.LabelStyle style)) continue;
                bool any = false;
                foreach (ObjectId c in style.GetComponents(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType.Text))
                    if (tr.GetObject(c, OpenMode.ForWrite) is Autodesk.Civil.DatabaseServices.Styles.LabelStyleTextComponent t && t.Border.BackgroundMask.Value != mask)
                    {
                        t.Border.BackgroundMask.Value = mask;
                        any = true;
                    }
                if (any) changed++;
            }
            return (changed, leaders);
        }

        /// <summary>
        /// The corners of an MText as written, turned with it: its bounding points, and the box of its actual width and height
        /// round its insertion point (right- or centre-attached text often runs past the width it was given).
        /// </summary>
        public static List<Point3d> MTextCorners(MText mt)
        {
            List<Point3d> pts = new List<Point3d>();
            try { pts.AddRange(mt.GetBoundingPoints().Cast<Point3d>()); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                double w = mt.ActualWidth, h = mt.ActualHeight;
                if (w > 0 && h > 0)
                {
                    Vector3d x = mt.Direction.GetNormal(), y = mt.Normal.CrossProduct(x).GetNormal();
                    string a = mt.Attachment.ToString();
                    double x0 = a.EndsWith("Right") ? -w : a.EndsWith("Center") || a.EndsWith("Mid") ? -w / 2 : 0;
                    double y0 = a.StartsWith("Top") ? -h : a.StartsWith("Middle") ? -h / 2 : 0;
                    foreach ((double dx, double dy) in new[] { (x0, y0), (x0 + w, y0), (x0 + w, y0 + h), (x0, y0 + h) })
                        pts.Add(mt.Location + x * dx + y * dy);
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return pts;
        }

        /// <summary>The pipe and structure labels shown in the sheets' viewports (by their anchor or their text), plan and / or profile.</summary>
        public static List<LabelInfo> OnSheets(IEnumerable<LabelInfo> labels, IEnumerable<SheetInfo> sheets, bool plan, bool profile)
        {
            List<SheetViewport> views = sheets.SelectMany(s => s.Viewports).ToList();
            return labels.Where(l => (l.IsProfile ? profile : plan) && views.Any(v => v.Sees(l.Anchor) || v.Sees(l.Location))).ToList();
        }

        /// <summary>
        /// Puts labels back where their style places them (Civil 3D's Reset Label, for many at once): dragged labels lose their
        /// leader and offset. Locked labels are left alone unless <paramref name="includeLocked"/>. Returns how many were reset,
        /// were locked, and failed.
        /// </summary>
        public static (int reset, int locked, int failed) ResetLocations(Transaction tr, IEnumerable<LabelInfo> labels, bool includeLocked)
        {
            int reset = 0, locked = 0, failed = 0;
            foreach (LabelInfo l in labels)
            {
                if (l.Locked && !includeLocked) { locked++; continue; }
                try
                {
                    if (!(tr.GetObject(l.Id, OpenMode.ForWrite) is CLabel label)) continue;
                    label.ResetLocation();
                    reset++;
                }
                catch (Exception ex)
                {
                    failed++;
                    C3DProLogger.Warn($"Reset labels: label {l.Id.Handle} could not be reset", ex);
                }
            }
            return (reset, locked, failed);
        }

        public static void SetLocked(Transaction tr, Database db, IEnumerable<ObjectId> ids, bool locked)
        {
            RegAppTable table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!table.Has(RegApp))
            {
                table.UpgradeOpen();
                RegAppTableRecord rec = new RegAppTableRecord { Name = RegApp };
                table.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
            foreach (ObjectId id in ids)
            {
                if (!(tr.GetObject(id, OpenMode.ForWrite) is CLabel label)) continue;
                label.XData = locked
                    ? new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp), new TypedValue((int)DxfCode.ExtendedDataAsciiString, "locked"))
                    : new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp));
            }
        }

        // ---------------- Label sets ----------------

        /// <summary>The labels on the example parts, as a label set (plan and profile, one entry per distinct style and place).</summary>
        public static LabelSet Capture(List<LabelInfo> all, IEnumerable<ObjectId> partIds, Transaction tr)
        {
            HashSet<ObjectId> parts = new HashSet<ObjectId>(partIds);
            LabelSet set = new LabelSet();
            foreach (LabelInfo l in all.Where(l => parts.Contains(l.PartId)))
            {
                double ratio = 0.5;
                if (l.IsPipe && tr.GetObject(l.Id, OpenMode.ForRead) is DBObject o)
                {
                    try { ratio = o is PipeLabel pl ? pl.Ratio : o is PipeProfileLabel ppl ? ppl.Ratio : 0.5; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                // Positions near an end count as that end (labels "at the end" are a little inside it).
                if (l.IsPipe) ratio = ratio < 0.15 ? 0 : ratio > 0.85 ? 1 : Math.Round(ratio, 2);
                if (set.Items.Any(i => i.Kind == l.Kind && i.Style == l.Style && Math.Abs(i.Ratio - ratio) < 0.05)) continue;
                set.Items.Add(new LabelSetItem { Kind = l.Kind, Style = l.Style, Ratio = l.IsPipe ? ratio : 0.5, Dragged = l.Dragged });
            }
            set.Items = set.Items.OrderBy(i => i.Kind).ThenBy(i => i.Ratio).ToList();
            return set;
        }

        /// <summary>The names of the drawing's label styles for a kind of label (pipe styles serve plan and profile alike).</summary>
        public static List<string> StyleNames(Transaction tr, CivilDocument doc, LabelKind kind)
        {
            List<string> names = new List<string>();
            try
            {
                IEnumerable<ObjectId> styles = kind == LabelKind.PlanPipe || kind == LabelKind.ProfilePipe
                    ? doc.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles.Cast<ObjectId>()
                    : doc.Styles.LabelStyles.StructureLabelStyles.LabelStyles.Cast<ObjectId>();
                foreach (ObjectId id in styles)
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.LabelStyle s) names.Add(s.Name);
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>A label style of the kind by name, or null.</summary>
        public static ObjectId StyleId(Transaction tr, CivilDocument doc, LabelKind kind, string name)
        {
            IEnumerable<ObjectId> styles;
            try
            {
                styles = kind == LabelKind.PlanPipe || kind == LabelKind.ProfilePipe
                    ? doc.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles.Cast<ObjectId>()
                    : doc.Styles.LabelStyles.StructureLabelStyles.LabelStyles.Cast<ObjectId>();
            }
            catch (Exception) { return ObjectId.Null; }
            foreach (ObjectId id in styles)
                if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.LabelStyle s && s.Name == name) return id;
            return ObjectId.Null;
        }

        internal sealed class ApplyResult
        {
            public int Created, Removed, Skipped, Failed;
            public readonly List<ObjectId> NewIds = new List<ObjectId>();
            public readonly List<string> Errors = new List<string>();
        }

        /// <summary>
        /// Puts the set's labels on the parts: plan labels on each part, profile labels in every profile view showing it.
        /// Existing labels are kept and only missing ones added, or parts already labelled are skipped, or their labels replaced.
        /// </summary>
        public static ApplyResult Apply(Transaction tr, CivilDocument doc, LabelSet set, IEnumerable<ObjectId> partIds, List<LabelInfo> existing, ExistingLabels mode)
        {
            ApplyResult r = new ApplyResult();
            Dictionary<string, ObjectId> styles = new Dictionary<string, ObjectId>();
            ObjectId Style(LabelSetItem i)
            {
                string key = i.Kind + "|" + i.Style;
                if (!styles.TryGetValue(key, out ObjectId id)) styles[key] = id = StyleId(tr, doc, i.Kind, i.Style);
                return id;
            }
            ILookup<ObjectId, LabelInfo> byPart = existing.ToLookup(l => l.PartId);
            Dictionary<ObjectId, ObjectId> viewParts = null;

            foreach (ObjectId partId in partIds)
            {
                DBObject part = tr.GetObject(partId, OpenMode.ForRead);
                bool pipe = part is Pipe;
                if (!pipe && !(part is Structure)) continue;
                List<LabelInfo> have = byPart[partId].ToList();
                List<LabelSetItem> items = set.Items.Where(i => pipe ? i.Kind == LabelKind.PlanPipe || i.Kind == LabelKind.ProfilePipe : i.Kind == LabelKind.PlanStructure || i.Kind == LabelKind.ProfileStructure).ToList();
                if (items.Count == 0) continue;
                if (mode == ExistingLabels.SkipLabelled && have.Count > 0) { r.Skipped++; continue; }
                if (mode == ExistingLabels.Replace)
                {
                    List<LabelSetItem> missing = items.Where(i => Style(i).IsNull).ToList();
                    if (missing.Count > 0)
                    {
                        foreach (LabelSetItem item in missing)
                        {
                            r.Failed++;
                            r.Errors.Add($"{((Part)part).Name}: Label style \"{item.Style}\" is not in this drawing; existing labels kept.");
                        }
                        continue;
                    }
                    foreach (LabelInfo l in have.Where(l => items.Any(i => i.Kind == l.Kind)))
                    {
                        try { tr.GetObject(l.Id, OpenMode.ForWrite).Erase(); r.Removed++; } catch (Exception ex) { r.Errors.Add(ex.Message); }
                    }
                    have.Clear();
                }

                ObjectIdCollection views = new ObjectIdCollection();
                if (items.Any(i => i.Kind == LabelKind.ProfilePipe || i.Kind == LabelKind.ProfileStructure))
                    try { views = ((Part)part).GetProfileViewsDisplayingMe(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

                // Each existing label can satisfy one requested position only.
                List<AnnotLabelPosition> positions = have.Select(l =>
                {
                    double? ratio = null;
                    if (l.IsPipe)
                    {
                        try
                        {
                            DBObject label = tr.GetObject(l.Id, OpenMode.ForRead);
                            ratio = label is PipeLabel pl ? pl.Ratio : label is PipeProfileLabel ppl ? ppl.Ratio : (double?)null;
                        }
                        catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    }
                    return new AnnotLabelPosition(l.Kind, l.Style, l.ViewId.Handle.ToString(), ratio);
                }).ToList();
                foreach (LabelSetItem item in items)
                {
                    ObjectId styleId = Style(item);
                    if (styleId.IsNull) { r.Errors.Add($"Label style \"{item.Style}\" is not in this drawing."); r.Failed++; continue; }
                    bool profile = item.Kind == LabelKind.ProfilePipe || item.Kind == LabelKind.ProfileStructure;
                    IEnumerable<ObjectId> targets = profile ? views.Cast<ObjectId>().Where(v => !v.IsNull).Distinct() : new[] { ObjectId.Null };
                    foreach (ObjectId viewId in targets)
                    {
                        // Position matters only when the set has several labels of this style (two end labels, say); the
                        // only one of its style counts wherever it sits, since Arrange may have slid it along the pipe.
                        bool several = items.Count(i => i.Kind == item.Kind && i.Style == item.Style) > 1;
                        if (AnnotLabelRules.TakeExisting(positions, new AnnotLabelPosition(item.Kind, item.Style,
                            viewId.Handle.ToString(), several ? Math.Max(0, Math.Min(1, item.Ratio)) : (double?)null)))
                        { r.Skipped++; continue; }
                        try
                        {
                            ObjectId viewPart = ObjectId.Null;
                            if (profile)
                            {
                                if (viewParts == null) viewParts = ViewParts(tr, part.Database);
                                if (!viewParts.TryGetValue(partId, out viewPart)) { r.Failed++; r.Errors.Add($"{((Part)part).Name}: Civil 3D has no profile representation of it."); continue; }
                            }
                            ObjectId id = Create(part, item, styleId, viewId, viewPart);
                            if (id.IsNull) { r.Failed++; continue; }
                            r.Created++;
                            r.NewIds.Add(id);
                        }
                        catch (Exception ex) { r.Failed++; r.Errors.Add($"{(part is Part p ? p.Name : "")}: {ex.Message}"); }
                    }
                }
            }
            return r;
        }

        private static ObjectId Create(DBObject part, LabelSetItem item, ObjectId styleId, ObjectId viewId, ObjectId viewPart)
        {
            switch (item.Kind)
            {
                case LabelKind.PlanPipe:
                    return PipeLabel.Create(part.ObjectId, Math.Max(0, Math.Min(1, item.Ratio)), styleId);
                case LabelKind.PlanStructure:
                    return StructureLabel.Create(part.ObjectId, styleId, ((Structure)part).Location);
                case LabelKind.ProfilePipe:
                    return PipeProfileLabel.Create(viewPart, viewId, Math.Max(0, Math.Min(1, item.Ratio)), styleId);
                default:
                    return StructureProfileLabel.Create(viewId, viewPart, styleId);
            }
        }

        /// <summary>
        /// Each part's profile representation: one model space entity per part, shared by every profile view that draws it
        /// (labels take it together with the view).
        /// </summary>
        internal static Dictionary<ObjectId, ObjectId> ViewParts(Transaction tr, Database db)
        {
            Dictionary<ObjectId, ObjectId> map = new Dictionary<ObjectId, ObjectId>();
            Autodesk.AutoCAD.Runtime.RXClass partClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileViewPart));
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!id.ObjectClass.IsDerivedFrom(partClass)) continue;
                try { map[((ProfileViewPart)tr.GetObject(id, OpenMode.ForRead)).ModelPartId] = id; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return map;
        }    }
}
