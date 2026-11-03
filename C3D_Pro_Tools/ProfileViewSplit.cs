using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Label = Autodesk.Civil.DatabaseServices.Label;

namespace C3DProTools
{
    internal sealed class SplitOptions
    {
        public double Length = 450, Round = 10, Overlap, MinLast = 100;
        /// <summary>True: the parts go one under another below the source; false: in a row to its right.</summary>
        public bool Below = true;
        public double Gap = 30;
        public string NamePattern = "{view} - P{n}";
        public bool TrimParts = true, CopyLabels = true, FitElevation = true;
        public double Margin = 1.0;
        /// <summary>Each part's elevation range is rounded outward to whole steps of this (m).</summary>
        public double DatumIncrement = 1.0;
        /// <summary>The number {SheetNo} takes in the first part's name.</summary>
        public int FirstSheetNo = 1;
        /// <summary>Lines at the stations where the alignment enters and leaves closed polylines on these layers (empty = none).</summary>
        public string ZoneLayers = "";
        public string ZoneLabel = "Paved";
        public bool DeleteSource;
        public ProfileRoleRules Rules = new ProfileRoleRules();
        /// <summary>The sheet viewport's size in model units (0 = no viewport to check against).</summary>
        public double ViewportWidth, ViewportHeight;
        // Sheets
        public bool Sheets;
        public string SheetTemplate = "", FirstSheetName = "";
        public bool NumberPartTitle = true;
    }

    internal sealed class SplitPart
    {
        public int N;
        public double S0, S1, Low, High;
        public string Name = "", Sheet = "";
        /// <summary>Size of the new view in model units (graph plus title and bands), and whether it fits the viewport.</summary>
        public double Width, Height;
        public bool Fits = true;
        public ObjectId ViewId;
        /// <summary>The new view's outline once placed (graph, title and bands).</summary>
        public Extents3d Box;
    }

    /// <summary>
    /// Breaks a long profile view into views of a set length (or the length that fits a sheet's viewport). Each part is a copy
    /// of the view, so it keeps its style, bands, band profiles, profiles and drawn parts; then it gets its station range,
    /// its own elevation range, only the parts that lie in it, the source's part labels, and optionally a profile sheet.
    /// </summary>
    internal static class ProfileViewSplit
    {
        private const double StationTolerance = 0.5;

        /// <summary>Where the view's graph and the space around it (title, axes, bands) lie, in model units.</summary>
        internal sealed class Frame
        {
            public double Horizontal = 1, Vertical = 1;
            public double Left, Right, Top, Bottom;
            public Point3d Origin;
        }

        public static Frame Measure(ProfileView v)
        {
            Frame f = new Frame();
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            double s0 = v.StationStart, s1 = v.StationEnd, e0 = v.ElevationMin, e1 = v.ElevationMax;
            if (!v.FindXYAtStationAndElevation(s0, e0, ref x0, ref y0) || !v.FindXYAtStationAndElevation(s1, e1, ref x1, ref y1))
                throw new InvalidOperationException($"The geometry of the view {v.Name} could not be read.");
            f.Origin = new Point3d(x0, y0, 0);
            f.Horizontal = s1 > s0 ? (x1 - x0) / (s1 - s0) : 1;
            f.Vertical = e1 > e0 ? (y1 - y0) / (e1 - e0) : 1;
            Extents3d ext = v.GeometricExtents;
            f.Left = x0 - ext.MinPoint.X; f.Right = ext.MaxPoint.X - x1;
            f.Bottom = y0 - ext.MinPoint.Y; f.Top = ext.MaxPoint.Y - y1;
            return f;
        }

        /// <summary>The longest station length whose view fits the viewport width, rounded down to <paramref name="round"/>.</summary>
        public static double LengthForWidth(Frame f, double viewportWidth, double round)
        {
            double usable = viewportWidth * 0.98 - f.Left - f.Right;
            double length = usable / Math.Max(1e-9, f.Horizontal);
            if (round > 0) length = Math.Floor(length / round) * round;
            return Math.Max(0, length);
        }

        public static List<SplitPart> Plan(Transaction tr, ProfileView v, SplitOptions o, ProfileViewMatch.Surfaces surfaces)
        {
            if (o.Length <= 0) throw new InvalidOperationException("The part length must be positive.");
            double s0 = v.StationStart, s1 = v.StationEnd;
            List<double> ends = new List<double>();
            for (int k = 1; ; k++)
            {
                double end = s0 + k * o.Length;
                if (o.Round > 0) end = Math.Floor(end / o.Round) * o.Round;
                if (end >= s1 - 1e-6) { ends.Add(s1); break; }
                if (ends.Count > 0 && end <= ends[ends.Count - 1] + 1e-6) end = ends[ends.Count - 1] + o.Length;
                ends.Add(end);
                if (ends.Count > 1000) throw new InvalidOperationException("That makes more than 1000 parts.");
            }
            // A short last part is joined to the one before it.
            if (ends.Count > 1 && s1 - ends[ends.Count - 2] < o.MinLast) ends.RemoveAt(ends.Count - 2);

            Frame f = Measure(v);
            Alignment alignment = tr.GetObject(v.AlignmentId, OpenMode.ForRead) as Alignment;
            string alignmentName = alignment?.Name ?? "";
            Func<double, string> station = StationText(alignment);
            List<ObjectId> roles = RoleProfiles(tr, v, o.Rules, surfaces);
            ProfileViewMatch.ViewPartIndex index = surfaces.Parts;
            List<SplitPart> parts = new List<SplitPart>();
            double start = s0;
            for (int i = 0; i < ends.Count; i++)
            {
                SplitPart p = new SplitPart { N = i + 1, S0 = i == 0 ? s0 : Math.Max(s0, start - o.Overlap), S1 = ends[i] };
                p.Name = Name(o.NamePattern, v.Name, p.N, p.S0, p.S1, alignmentName, o.FirstSheetNo, station);
                if (o.FitElevation && ProfileViewMatch.FitRange(tr, v, roles, p.S0, p.S1, out double lo, out double hi, index))
                    (p.Low, p.High) = ProfileViewMath.DatumRange(lo, hi, o.Margin, o.DatumIncrement);
                else { p.Low = v.ElevationMin; p.High = v.ElevationMax; }
                p.Width = (p.S1 - p.S0) * f.Horizontal + f.Left + f.Right;
                p.Height = (p.High - p.Low) * f.Vertical + f.Top + f.Bottom;
                p.Fits = o.ViewportWidth <= 0 || (p.Width <= o.ViewportWidth + 1e-6 && p.Height <= o.ViewportHeight + 1e-6);
                parts.Add(p);
                start = ends[i];
            }
            return parts;
        }

        /// <summary>The role profiles (or, with none found, every profile drawn) whose levels the parts' elevation ranges cover.</summary>
        private static List<ObjectId> RoleProfiles(Transaction tr, ProfileView v, ProfileRoleRules rules, ProfileViewMatch.Surfaces surfaces)
        {
            List<ViewProfile> all = ProfileViewMatch.Classify(tr, v, rules, surfaces);
            List<ObjectId> roles = all.Where(p => p.Chosen).Select(p => p.Id).ToList();
            return roles.Count > 0 ? roles : all.Where(p => p.Drawn).Select(p => p.Id).ToList();
        }

        /// <summary>
        /// A part's name from the pattern: {view} {n} {from} {to}, or {ViewName} {PartIndex} {StartSta} {EndSta} {AlignmentName}
        /// {SheetNo}, each with an optional format ({SheetNo:000}).
        /// </summary>
        public static string Name(string pattern, string view, int n, double s0, double s1, string alignment = "", int firstSheet = 1, Func<double, string> station = null)
        {
            station = station ?? Station;
            return ProfileViewMath.Name(string.IsNullOrWhiteSpace(pattern) ? "{view} - P{n}" : pattern,
                ProfileViewMath.SplitTokens(view, alignment, n, station(s0), station(s1), firstSheet));
        }

        /// <summary>Station text with the sign in front (-0+050), whole metres.</summary>
        public static string Station(double s) => ProfileViewMath.Station(s);

        /// <summary>
        /// Station text for an alignment: Civil 3D's own (station equations and the drawing's station format) when it has station
        /// equations, else <see cref="Station"/>.
        /// </summary>
        public static Func<double, string> StationText(Alignment alignment)
        {
            bool equations = false;
            try { equations = alignment != null && alignment.StationEquations.Count > 0; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (!equations) return Station;
            return s =>
            {
                try { return alignment.GetStationStringWithEquations(s); }
                catch (Exception) { return Station(s); }
            };
        }

        /// <summary>Creates the parts. Each step commits on its own (the view geometry is read back between them).</summary>
        public static List<SplitPart> Apply(Database db, CivilDocument doc, ObjectId sourceId, List<SplitPart> parts, SplitOptions o, List<string> log)
        {
            Frame f;
            Extents3d sourceExt;
            string sourceName;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ProfileView src = (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead);
                f = Measure(src);
                sourceExt = src.GeometricExtents;
                sourceName = src.Name;
                foreach (ObjectId id in ProfileViewMatch.AllViews(tr, db)) names.Add(((ProfileView)tr.GetObject(id, OpenMode.ForRead)).Name);
                tr.Commit();
            }

            List<ObjectId> sourceParts;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                sourceParts = ProfileViewMatch.PartsInView(tr, (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead));
                tr.Commit();
            }
            double nextTop = sourceExt.MinPoint.Y - o.Gap, nextLeft = sourceExt.MaxPoint.X + o.Gap;
            foreach (SplitPart p in parts)
            {
                // Where the part's graph goes: below (or right of) the source and the parts before it.
                double graphH = (p.High - p.Low) * f.Vertical, graphW = (p.S1 - p.S0) * f.Horizontal;
                Point3d target = o.Below ? new Point3d(f.Origin.X, nextTop - f.Top - graphH, 0) : new Point3d(nextLeft + f.Left, f.Origin.Y, 0);
                p.Box = new Extents3d(new Point3d(target.X - f.Left, target.Y - f.Bottom, 0), new Point3d(target.X + graphW + f.Right, target.Y + graphH + f.Top, 0));
                if (o.Below) nextTop = target.Y - f.Bottom - o.Gap;
                else nextLeft = target.X + graphW + f.Right + o.Gap;

                // 1. A new view of the same alignment (a copied view would share the source's part representations).
                string name = p.Name;
                for (int i = 2; names.Contains(name); i++) name = $"{p.Name} ({i})";
                p.Name = name;
                names.Add(name);
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileView src = (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead);
                    ObjectId bandSet = ObjectId.Null;
                    foreach (ObjectId id in doc.Styles.ProfileViewBandSetStyles) { bandSet = id; break; }
                    p.ViewId = ProfileView.Create(src.AlignmentId, target, name, bandSet, src.StyleId);
                    ProfileView v = (ProfileView)tr.GetObject(p.ViewId, OpenMode.ForWrite);
                    try { v.Layer = src.Layer; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    SetStations(v, p.S0, p.S1);
                    v.ElevationRangeMode = ElevationRangeType.UserSpecified;
                    if (p.Low < v.ElevationMax) { v.ElevationMin = p.Low; v.ElevationMax = p.High; }
                    else { v.ElevationMax = p.High; v.ElevationMin = p.Low; }
                    CopyBands(tr, src, v, log);
                    CopyProfileOverrides(src, v);
                    tr.Commit();
                }
                // 2. Its graph's corner moved onto the planned point (the insertion point need not be the graph's corner).
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileView v = (ProfileView)tr.GetObject(p.ViewId, OpenMode.ForWrite);
                    double x = 0, y = 0;
                    if (v.FindXYAtStationAndElevation(v.StationStart, v.ElevationMin, ref x, ref y) && new Point3d(x, y, 0).DistanceTo(target) > 1e-6)
                        v.TransformBy(Matrix3d.Displacement(target - new Point3d(x, y, 0)));
                    tr.Commit();
                }
                // 3. The source's parts that lie in its stations (or all of them), with their overrides.
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileView src = (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead);
                    Alignment al = (Alignment)tr.GetObject(src.AlignmentId, OpenMode.ForRead);
                    int failed = 0;
                    foreach (ObjectId partId in sourceParts)
                    {
                        if (o.TrimParts && !PartInRange(tr, al, partId, p.S0, p.S1)) continue;
                        try
                        {
                            DBObject obj = tr.GetObject(partId, OpenMode.ForWrite);
                            if (obj is Part gp) gp.AddToProfileView(p.ViewId);
                            else if (obj is PressurePart pp) pp.AddToProfileView(p.ViewId);
                        }
                        catch (Exception) { failed++; }
                    }
                    if (failed > 0) log.Add($"{p.Name}: {failed} part(s) could not be drawn in it.");
                    tr.Commit();
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    CopyPartOverrides(tr, (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead), (ProfileView)tr.GetObject(p.ViewId, OpenMode.ForWrite));
                    tr.Commit();
                }
                if (o.CopyLabels)
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        CopyLabels(tr, sourceId, p, log);
                        tr.Commit();
                    }
            }

            if (!string.IsNullOrWhiteSpace(o.ZoneLayers))
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    List<IReadOnlyList<(double X, double Y)>> zones = ProfileViewZones.ReadZones(tr, db, o.ZoneLayers);
                    if (zones.Count == 0) log.Add($"Cover zones: no closed polylines on {o.ZoneLayers}.");
                    int marked = 0;
                    foreach (SplitPart p in parts.Where(x => !x.ViewId.IsNull))
                        try { marked += ProfileViewZones.Mark(tr, db, p.ViewId, zones, o.ZoneLabel); }
                        catch (Exception ex) { log.Add($"{p.Name}: cover zones: {ex.Message}"); }
                    if (zones.Count > 0) log.Add($"Cover zones: {marked} zone edge(s) marked.");
                    tr.Commit();
                }

            if (o.Sheets) CreateSheets(db, parts, o, f, log);

            if (o.DeleteSource)
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    try { tr.GetObject(sourceId, OpenMode.ForWrite).Erase(); log.Add($"The source view {sourceName} was deleted."); }
                    catch (Exception ex) { log.Add($"The source view could not be deleted: {ex.Message}"); }
                    tr.Commit();
                }
            return parts;
        }

        /// <summary>The source's bands, in order, with their settings and profiles (the same alignment, so the same profiles).</summary>
        private static void CopyBands(Transaction tr, ProfileView src, ProfileView v, List<string> log)
        {
            foreach (bool top in new[] { false, true })
            {
                ProfileViewBandItemCollection items = new ProfileViewBandItemCollection(v.ObjectId, top ? BandLocationType.Top : BandLocationType.Bottom);
                List<ProfileViewBandItem> from = (top ? src.Bands.GetTopBandItems() : src.Bands.GetBottomBandItems()).ToList();
                foreach (ProfileViewBandItem b in from)
                    try { items.Add(b.BandStyleId); }
                    catch (Exception ex) { log.Add($"{v.Name}: band \"{ProfileViewMatch.StyleName(tr, b.BandStyleId)}\": {ex.Message}"); }
                List<ProfileViewBandItem> to = items.ToList();
                for (int i = 0; i < Math.Min(from.Count, to.Count); i++)
                {
                    ProfileViewBandItem a = from[i], b = to[i];
                    try { b.Gap = a.Gap; b.Weeding = a.Weeding; b.ShowLabels = a.ShowLabels; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { b.LabelAtStartStation = a.LabelAtStartStation; b.LabelAtEndStation = a.LabelAtEndStation; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { b.MajorInterval = a.MajorInterval; b.MinorInterval = a.MinorInterval; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { if (!a.Profile1Id.IsNull) b.Profile1Id = a.Profile1Id; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { if (!a.Profile2Id.IsNull) b.Profile2Id = a.Profile2Id; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                try { if (top) v.Bands.SetTopBandItems(items); else v.Bands.SetBottomBandItems(items); }
                catch (Exception ex) { log.Add($"{v.Name}: setting the bands failed: {ex.Message}"); }
            }
            try { v.Bands.MatchIncrementToGridIntervals = src.Bands.MatchIncrementToGridIntervals; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        /// <summary>Which profiles the source shows, and any style it overrides them with.</summary>
        private static void CopyProfileOverrides(ProfileView src, ProfileView v)
        {
            Dictionary<ObjectId, ProfileOverride> from = new Dictionary<ObjectId, ProfileOverride>();
            foreach (ProfileOverride o in src.GraphOverrides) try { from[o.ProfileId] = o; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            foreach (ProfileOverride o in v.GraphOverrides)
            {
                if (!from.TryGetValue(o.ProfileId, out ProfileOverride a)) continue;
                try { o.Draw = a.Draw; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { if (a.UseOverrideStyle) { o.OverrideStyleId = a.OverrideStyleId; o.UseOverrideStyle = true; } else o.UseOverrideStyle = false; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
        }

        /// <summary>The source's pipe and structure overrides (shown, style) on the same parts in the new view.</summary>
        private static void CopyPartOverrides(Transaction tr, ProfileView src, ProfileView v)
        {
            ObjectId Model(ObjectId id) { try { return tr.GetObject(id, OpenMode.ForRead) is ProfileViewPart vp ? vp.ModelPartId : id; } catch (Exception) { return id; } }
            Dictionary<ObjectId, (bool draw, ObjectId style)> pipes = new Dictionary<ObjectId, (bool, ObjectId)>(), structures = new Dictionary<ObjectId, (bool, ObjectId)>();
            try { foreach (PipeOverride o in src.PipeOverrides) pipes[Model(o.PipeId)] = (o.Draw, o.OverrideStyleId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { foreach (StructureOverride o in src.StructureOverrides) structures[Model(o.StructId)] = (o.Draw, o.OverrideStyleId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                foreach (PipeOverride o in v.PipeOverrides)
                    if (pipes.TryGetValue(Model(o.PipeId), out var a)) { try { o.Draw = a.draw; if (!a.style.IsNull) o.OverrideStyleId = a.style; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                foreach (StructureOverride o in v.StructureOverrides)
                    if (structures.TryGetValue(Model(o.StructId), out var a)) { try { o.Draw = a.draw; if (!a.style.IsNull) o.OverrideStyleId = a.style; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        private static void SetStations(ProfileView v, double s0, double s1)
        {
            v.StationRangeMode = StationRangeType.UserSpecified;
            // The view rejects a start past its current end (and an end before its start).
            if (s0 < v.StationEnd) { v.StationStart = s0; v.StationEnd = s1; }
            else { v.StationEnd = s1; v.StationStart = s0; }
        }

        private static bool PartInRange(Transaction tr, Alignment al, ObjectId partId, double s0, double s1)
        {
            DBObject obj = tr.GetObject(partId, OpenMode.ForRead);
            List<Point3d> points = new List<Point3d>();
            if (obj is Pipe pipe) { points.Add(pipe.StartPoint); points.Add(pipe.EndPoint); }
            else if (obj is PressurePipe pp) { points.Add(pp.StartPoint); points.Add(pp.EndPoint); }
            else if (obj is Structure st) points.Add(st.Location);
            else if (obj is Entity e) { try { Extents3d x = e.GeometricExtents; points.Add(x.MinPoint + (x.MaxPoint - x.MinPoint) / 2); } catch (Exception) { return true; } }
            List<double> stations = new List<double>();
            foreach (Point3d pt in points)
            {
                try { double st = 0, off = 0; al.StationOffset(pt.X, pt.Y, ref st, ref off); stations.Add(st); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            if (stations.Count == 0) return true;
            return stations.Max() >= s0 - StationTolerance && stations.Min() <= s1 + StationTolerance;
        }

        // =====================================================================================
        // Labels
        // =====================================================================================

        /// <summary>
        /// The source view's labels on the parts the new view shows, with the same style and placement. Labels of projected
        /// objects and the view's own labels have no .NET API to be made and are counted in the log instead.
        /// </summary>
        private static void CopyLabels(Transaction tr, ObjectId sourceId, SplitPart p, List<string> log)
        {
            ProfileView src = (ProfileView)tr.GetObject(sourceId, OpenMode.ForRead);
            ProfileView v = (ProfileView)tr.GetObject(p.ViewId, OpenMode.ForRead);
            Dictionary<ObjectId, ObjectId> gravityParts = GravityViewParts(tr, v), sourceGravity = GravityViewParts(tr, src);
            HashSet<ObjectId> shown = new HashSet<ObjectId>(ProfileViewMatch.PartsInView(tr, v));
            Dictionary<ObjectId, ObjectId> pressureParts = PressureViewParts(tr, v);
            int made = 0, failed = 0;

            void Copy(ObjectId labelId, Func<Label, ObjectId> create)
            {
                if (!(tr.GetObject(labelId, OpenMode.ForRead) is Label from)) return;
                try
                {
                    ObjectId newId = create(from);
                    if (newId.IsNull) return;
                    CopyPlacement(tr, from, newId);
                    made++;
                }
                catch (Exception) { failed++; }
            }

            // Gravity pipes and structures: labels sit on each view's own representation of the part.
            foreach (ObjectId id in SafeIds(() => src.GetAvailablePipeProfileLabelIds()))
                Copy(id, l => ModelOf(tr, l.FeatureId) is ObjectId pipe && shown.Contains(pipe)
                    ? PipeProfileLabel.Create(gravityParts.TryGetValue(pipe, out ObjectId target) ? target : pipe, v.ObjectId, (l as PipeProfileLabel)?.Ratio ?? 0.5, l.StyleId) : ObjectId.Null);
            foreach (ObjectId id in SafeIds(() => src.GetAvailableStructureProfileLabelIds()))
                Copy(id, l => ModelOf(tr, l.FeatureId) is ObjectId st && shown.Contains(st)
                    ? StructureProfileLabel.Create(v.ObjectId, gravityParts.TryGetValue(st, out ObjectId target) ? target : st, l.StyleId) : ObjectId.Null);
            foreach (KeyValuePair<ObjectId, ObjectId> sp in sourceGravity)
            {
                if (!gravityParts.TryGetValue(sp.Key, out ObjectId target) || !(tr.GetObject(sp.Key, OpenMode.ForRead) is Pipe)) continue;
                foreach (ObjectId id in SafeIds(() => CrossingPipeProfileLabel.GetAvailableLabelIds(sp.Value, sourceId)))
                    Copy(id, l => CrossingPipeProfileLabel.Create(target, v.ObjectId, l.StyleId) is ObjectIdCollection c && c.Count > 0 ? c[0] : ObjectId.Null);
            }
            // Pressure parts: labels hang on each view's own representation of the part.
            foreach (KeyValuePair<ObjectId, ObjectId> sp in PressureViewParts(tr, src))
            {
                if (!pressureParts.TryGetValue(sp.Key, out ObjectId target)) continue;
                DBObject model = tr.GetObject(sp.Key, OpenMode.ForRead);
                if (model is PressurePipe)
                {
                    foreach (ObjectId id in SafeIds(() => PressurePipeProfileLabel.GetAvailableLabelIds(sp.Value, sourceId)))
                        Copy(id, l => PressurePipeProfileLabel.Create(target, v.ObjectId, (l as PressurePipeProfileLabel)?.Ratio ?? 0.5, l.StyleId));
                    foreach (ObjectId id in SafeIds(() => CrossingPressurePipeProfileLabel.GetAvailableLabelIds(sp.Value, sourceId)))
                        Copy(id, l => CrossingPressurePipeProfileLabel.Create(target, v.ObjectId, 0.5, l.StyleId) is ObjectIdCollection c && c.Count > 0 ? c[0] : ObjectId.Null);
                }
                else if (model is PressureFitting)
                    foreach (ObjectId id in SafeIds(() => PressureFittingProfileLabel.GetAvailableLabelIds(sp.Value, sourceId)))
                        Copy(id, l => PressureFittingProfileLabel.Create(target, v.ObjectId, 0.5, Vector3d.XAxis, l.StyleId));
                else if (model is PressureAppurtenance)
                    foreach (ObjectId id in SafeIds(() => PressureAppurtenanceProfileLabel.GetAvailableLabelIds(sp.Value, sourceId)))
                        Copy(id, l => PressureAppurtenanceProfileLabel.Create(target, v.ObjectId, 0.5, Vector3d.XAxis, l.StyleId));
            }

            int projections = CountProjectionLabels(tr, src, p.S0, p.S1);
            int viewLabels = SafeIds(() => src.GetProfileViewLabelIds()).Count;
            log.Add($"{p.Name}: {made} part label(s) copied" + (failed > 0 ? $", {failed} could not be" : "") + "." +
                    (projections > 0 ? $" {projections} projected object label(s) lie in its stations: project those objects with Civil 3D's Project Objects to Profile View." : "") +
                    (viewLabels > 0 && p.N == 1 ? $" The source has {viewLabels} label(s) of its own (station / depth), which are not copied." : ""));
        }

        private static ObjectIdCollection SafeIds(Func<ObjectIdCollection> get)
        {
            try { return get() ?? new ObjectIdCollection(); } catch (Exception) { return new ObjectIdCollection(); }
        }

        /// <summary>The model part behind a label's feature: the part itself, or the model part of its view representation.</summary>
        private static object ModelOf(Transaction tr, ObjectId featureId)
        {
            if (featureId.IsNull || featureId.IsErased) return null;
            DBObject obj = tr.GetObject(featureId, OpenMode.ForRead);
            if (obj is ProfileViewPart vp) return vp.ModelPartId;
            if (obj is Part) return featureId;
            return null;
        }

        /// <summary>Model gravity pipe or structure → its representation in the view.</summary>
        private static Dictionary<ObjectId, ObjectId> GravityViewParts(Transaction tr, ProfileView v)
        {
            Dictionary<ObjectId, ObjectId> map = new Dictionary<ObjectId, ObjectId>();
            void Add(ObjectId id)
            {
                if (id.IsNull || id.IsErased) return;
                try { if (tr.GetObject(id, OpenMode.ForRead) is ProfileViewPart vp && !vp.ModelPartId.IsNull) map[vp.ModelPartId] = id; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            try { foreach (PipeOverride o in v.PipeOverrides) Add(o.PipeId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { foreach (StructureOverride o in v.StructureOverrides) Add(o.StructId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return map;
        }

        /// <summary>Model pressure part → its representation in the view.</summary>
        private static Dictionary<ObjectId, ObjectId> PressureViewParts(Transaction tr, ProfileView v)
        {
            Dictionary<ObjectId, ObjectId> map = new Dictionary<ObjectId, ObjectId>();
            foreach (ObjectId id in SafeIds(() => v.GetPressureNetworkPartsInGraph()))
                if (tr.GetObject(id, OpenMode.ForRead, false, true) is ProfileViewPressurePart vp && !vp.ModelPartId.IsNull) map[vp.ModelPartId] = id;
            return map;
        }

        private static void CopyPlacement(Transaction tr, Label from, ObjectId toId)
        {
            if (!(tr.GetObject(toId, OpenMode.ForWrite) is Label to)) return;
            try { if (to.StyleId != from.StyleId) to.StyleId = from.StyleId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { if (from.AllowsFlipping) to.Flipped = from.Flipped; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { if (from.AllowsReversing) to.Reversed = from.Reversed; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { if (from.AllowsDimensionAnchors) { to.DimensionAnchorOption = from.DimensionAnchorOption; to.DimensionAnchorValue = from.DimensionAnchorValue; } } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            // Dragged: the same offset from its anchor (LabelLocation in place of the obsolete DraggedOffset setter).
            try { if (from.Dragged) to.LabelLocation = to.AnchorInfo.Location + (from.LabelLocation - from.AnchorInfo.Location); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        private static int CountProjectionLabels(Transaction tr, ProfileView src, double s0, double s1)
        {
            int n = 0;
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(src.OwnerId, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.ObjectClass.DxfName.IndexOf("PROFILEPROJECTIONLABEL", StringComparison.OrdinalIgnoreCase) < 0 &&
                    id.ObjectClass.Name != "AeccDbProfileProjectionLabel") continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is ProfileProjectionLabel l) || l.ViewId != src.ObjectId) continue;
                try
                {
                    Extents3d x = l.GeometricExtents;
                    double st = 0, el = 0;
                    if (src.FindStationAndElevationAtXY((x.MinPoint.X + x.MaxPoint.X) / 2, x.MinPoint.Y, ref st, ref el) && st >= s0 && st <= s1) n++;
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return n;
        }

        // =====================================================================================
        // Sheets
        // =====================================================================================

        /// <summary>
        /// A profile sheet per part, copied from the template sheet: numbered on from the first name, its viewport centred on the
        /// part at the template's scale, and the "PART n" in its title set to the part's number.
        /// </summary>
        private static void CreateSheets(Database db, List<SplitPart> parts, SplitOptions o, Frame f, List<string> log)
        {
            SheetProfile profile = SheetManagerSettings.Load().Profile ?? new SheetProfile();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<SheetRecord> sheets = SheetManager.Read(tr, db, profile);
                SheetRecord template = sheets.FirstOrDefault(s => s.Layout.Equals(o.SheetTemplate, StringComparison.OrdinalIgnoreCase));
                if (template == null) { log.Add($"Sheets: the template sheet \"{o.SheetTemplate}\" is not in this drawing; no sheets were made."); tr.Commit(); return; }
                List<string> taken = new List<string>();
                string next = string.IsNullOrWhiteSpace(o.FirstSheetName) ? template.Layout : o.FirstSheetName.Trim();
                long number = SheetManager.NumberOf(next, profile);
                SheetRecord previous = template;
                foreach (SplitPart p in parts)
                {
                    string name = sheets.Any(s => s.Layout.Equals(next, StringComparison.OrdinalIgnoreCase)) || taken.Contains(next)
                        ? SheetManager.NextFreeName(next, Math.Max(0, number), sheets, taken, profile) : next;
                    try
                    {
                        SheetRecord copy = SheetManager.Duplicate(tr, db, previous, name, profile);
                        taken.Add(name);
                        p.Sheet = name;
                        CentreViewport(tr, copy, p.Box);
                        if (o.NumberPartTitle) NumberTitle(tr, copy, p.N);
                        sheets.Add(copy);
                        previous = copy;
                    }
                    catch (Exception ex) { log.Add($"Sheets: making \"{name}\" failed: {ex.Message}"); }
                    number = SheetManager.NumberOf(name, profile) + 1;
                    next = number > 0 ? SheetManager.WithNumber(name, number, profile) : name + " (2)";
                }
                tr.Commit();
            }
        }

        private static void CentreViewport(Transaction tr, SheetRecord sheet, Extents3d x)
        {
            if (sheet.ViewportId.IsNull) return;
            Point3d c = x.MinPoint + (x.MaxPoint - x.MinPoint) / 2;
            Viewport vp = (Viewport)tr.GetObject(sheet.ViewportId, OpenMode.ForWrite);
            bool locked = vp.Locked;
            if (locked) vp.Locked = false;
            // A plan viewport with no twist: its view centre is the model point less the view target.
            vp.ViewCenter = new Point2d(c.X - vp.ViewTarget.X, c.Y - vp.ViewTarget.Y);
            if (locked) vp.Locked = true;
        }

        private static readonly Regex PartNumber = new Regex(@"(PART\s*-?\s*)(\d+)", RegexOptions.IgnoreCase);

        private static void NumberTitle(Transaction tr, SheetRecord sheet, int n)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> a in sheet.Attributes)
                if (PartNumber.IsMatch(a.Value ?? "")) values[a.Key] = PartNumber.Replace(a.Value, m => m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture), 1);
            if (values.Count > 0) SheetManager.SetAttributes(tr, sheet, values);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(sheet.BlockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is DBText t && PartNumber.IsMatch(t.TextString)) { t.UpgradeOpen(); t.TextString = PartNumber.Replace(t.TextString, m => m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture), 1); }
                else if (obj is MText m && PartNumber.IsMatch(m.Text)) { m.UpgradeOpen(); m.Contents = PartNumber.Replace(m.Contents, x => x.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture), 1); }
            }
        }
    }
}
