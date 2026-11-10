using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
using CivilLabel = Autodesk.Civil.DatabaseServices.Label;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

// The sheet scene's Civil 3D side (docs/sheet-production-contract.md, sections 2–4): one read-only pass over the drawing,
// turned into plain values. It reuses the existing readers: Sheet Manager for tiles and parts, AnnotSheets for viewports,
// ProfileViewMatch for profile roles and drawn parts, ProfilePartDetection for crossings. Gravity networks only in phase 1.
namespace C3DProTools.Features.Sheets.Civil3D
{
    /// <summary>What the scene reader needs from the project settings.</summary>
    internal sealed class SheetSceneOptions
    {
        /// <summary>Regular expressions tried on a pipe's description, then its layer; the first group is its type code ("IC-3B").</summary>
        public List<string> TypeCodePatterns { get; set; } = new List<string> { @"Type:\s*([A-Za-z0-9-]+)" };
        /// <summary>Spacing of the samples of surface profiles (m).</summary>
        public double SurfaceSampleM { get; set; } = 1.0;
        /// <summary>Spacing of the samples along vertical curves of layout profiles (m); tangents give their ends only.</summary>
        public double CurveSampleM { get; set; } = 1.0;
        /// <summary>How far from an alignment a crossing pipe is looked for (m), as Parts to Profile does.</summary>
        public double CrossingToleranceM { get; set; } = 0.5;
    }

    internal sealed class SheetSceneCivil3D : ISheetScene
    {
        private readonly SheetSceneOptions _options;
        private readonly SheetProfile _sheetProfile;
        private readonly ProfileRoleRules _roleRules;

        public SheetSceneCivil3D(SheetSceneOptions options = null, SheetProfile sheetProfile = null, ProfileRoleRules roleRules = null)
        {
            _options = options ?? new SheetSceneOptions();
            _sheetProfile = sheetProfile ?? SheetManagerSettings.Load().Profile;
            _roleRules = roleRules ?? ProfileViewToolsSettings.Load().Rules ?? new ProfileRoleRules();
        }

        public SheetScene Read(SheetSceneRequest request, CancellationToken ct)
        {
            Document doc = DrawingRefs3D.Find(request?.Drawing);
            if (doc == null)
                return new SheetScene { Drawing = request?.Drawing, Issues = new[] { OperationIssue.Error(SheetCodes.PreviewWrongDrawing, "That drawing is not open any more. Open it and try again.") } };
            Database db = doc.Database;
            if (ConduitCivil3DAccess.Metres(db.Insunits) != 1.0)
                return new SheetScene { Drawing = request.Drawing, Issues = new[] { OperationIssue.Error(SheetCodes.UnitsNotMetres, "Sheet production works in metre drawings only. This drawing's units are not metres.") } };

            Reader r = new Reader(_options, _sheetProfile, _roleRules, ct);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SheetScene scene = r.Read(tr, db, CivilDocument.GetCivilDocument(db), request);
                tr.Commit();
                return scene with { Drawing = request.Drawing };
            }
        }

        /// <summary>One read: the state it gathers on the way.</summary>
        private sealed class Reader
        {
            private readonly SheetSceneOptions _o;
            private readonly SheetProfile _sp;
            private readonly ProfileRoleRules _rules;
            private readonly CancellationToken _ct;
            private readonly List<OperationIssue> _issues = new List<OperationIssue>();
            private readonly List<Regex> _typeCode;
            private readonly Dictionary<ObjectId, string> _networkOfPart = new Dictionary<ObjectId, string>();

            public Reader(SheetSceneOptions o, SheetProfile sp, ProfileRoleRules rules, CancellationToken ct)
            {
                _o = o; _sp = sp; _rules = rules; _ct = ct;
                _typeCode = (o.TypeCodePatterns ?? new List<string>()).Select(p => { try { return new Regex(p, RegexOptions.IgnoreCase); } catch (ArgumentException) { return null; } })
                    .Where(rx => rx != null).ToList();
            }

            private void Warn(string what, Exception ex, DBObject obj = null)
            {
                C3DProLogger.Warn("Sheet scene: " + what, ex);
                _issues.Add(OperationIssue.Warning(SheetCodes.SceneReadFailed, what + " It is left out of the scene.", obj: obj == null ? null : DrawingRefs3D.Ref(obj.ObjectId)));
            }

            public SheetScene Read(Transaction tr, Database db, CivilDocument civil, SheetSceneRequest request)
            {
                HashSet<string> wanted = new HashSet<string>(request.Layouts, StringComparer.OrdinalIgnoreCase);

                List<SheetViewport> viewports = new List<SheetViewport>();
                List<NetworkSnapshot> networks = ReadNetworks(tr, civil);
                List<ProfileViewSnapshot> views = new List<ProfileViewSnapshot>();
                List<CrossingSnapshot> crossings = new List<CrossingSnapshot>();
                List<AlignmentSnapshot> alignments = ReadAlignments(tr, db, civil, views, crossings);
                _ct.ThrowIfCancellationRequested();

                // A role no profile matches usually means a stale surface name in the role settings (Profile View Tools).
                foreach ((SceneProfileRole role, string surface) in new[] { (SceneProfileRole.Existing, _rules.ExistingSurface), (SceneProfileRole.Proposed, _rules.ProposedSurface) })
                    if (alignments.Count > 0 && !alignments.Any(a => a.Profiles.Any(p => p.Role == role)))
                        _issues.Add(OperationIssue.Warning(SheetCodes.SceneRoleUnmatched, string.IsNullOrWhiteSpace(surface)
                            ? $"No {role.ToString().ToLowerInvariant()} ground surface is set, so no profile has that role. Set it in Profile View Tools."
                            : $"No profile was sampled from the {role.ToString().ToLowerInvariant()} surface \"{surface}\". Check its name in Profile View Tools; the drawing's surfaces may be named differently."));

                List<SheetSnapshot> sheets = ReadSheets(tr, db, wanted, views, viewports);
                List<LabelSnapshot> labels = ReadLabels(tr, db, viewports);

                return new SheetScene
                {
                    Sheets = sheets, Networks = networks, Alignments = alignments, ProfileViews = views, Crossings = crossings,
                    Labels = labels, BuildRecords = BuildXData.ReadRecords(tr, db), Issues = _issues
                };
            }

            // ---- Sheets and viewports --------------------------------------------------------------------------------------

            private List<SheetSnapshot> ReadSheets(Transaction tr, Database db, HashSet<string> wanted, List<ProfileViewSnapshot> views, List<SheetViewport> allViewports)
            {
                Dictionary<string, SheetRecord> records = SheetManager.Read(tr, db, _sp).ToDictionary(s => s.Layout, StringComparer.OrdinalIgnoreCase);
                List<(Extents3d box, DrawingObjectRef view)> viewBoxes = new List<(Extents3d, DrawingObjectRef)>();
                foreach (ProfileViewSnapshot v in views)
                    if (DrawingRefs3D.TryResolve(db, v.Ref, out ObjectId id) && tr.GetObject(id, OpenMode.ForRead) is Entity e)
                        try { viewBoxes.Add((e.GeometricExtents, v.Ref)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

                List<SheetSnapshot> sheets = new List<SheetSnapshot>();
                foreach (SheetInfo info in AnnotSheets.Read(tr, db))
                {
                    if (wanted.Count > 0 && !wanted.Contains(info.Layout)) continue;
                    _ct.ThrowIfCancellationRequested();
                    records.TryGetValue(info.Layout, out SheetRecord rec);
                    List<ViewportSnapshot> vps = new List<ViewportSnapshot>();
                    bool showsProfileView = false;
                    foreach (SheetViewport v in info.Viewports)
                    {
                        allViewports.Add(v);
                        bool locked = false;
                        try { locked = ((Viewport)tr.GetObject(v.Id, OpenMode.ForRead)).Locked; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        List<Point2> boundary = v.Outline.Select(p => new Point3d(p.X, p.Y, 0).TransformBy(v.PaperToModel)).Select(m => new Point2(m.X, m.Y)).ToList();
                        vps.Add(new ViewportSnapshot(DrawingRefs3D.Ref(v.Id), v.Scale, v.Twist * 180 / Math.PI, boundary,
                            new Rect2(v.PaperBox.MinPoint.X, v.PaperBox.MinPoint.Y, v.PaperBox.MaxPoint.X, v.PaperBox.MaxPoint.Y), locked));
                        showsProfileView |= viewBoxes.Any(b => Overlaps(v.ModelBox, b.box));
                    }
                    bool isPart = rec != null && rec.IsPart;
                    SheetKind kind = isPart ? SheetKind.Plan : showsProfileView ? SheetKind.Profile : SheetKind.Other;
                    sheets.Add(new SheetSnapshot(info.Layout, info.TabOrder, kind, isPart ? rec.Tile : "", isPart ? rec.Part : "", info.Layout, vps));
                }
                return sheets;
            }

            private static bool Overlaps(Extents2d a, Extents3d b) =>
                a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X && a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y;

            // ---- Networks ----------------------------------------------------------------------------------------------

            private string TypeCodeOf(Pipe pipe)
            {
                string description = "", layer = "";
                try { description = pipe.Description ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { layer = pipe.Layer ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                foreach (Regex rx in _typeCode)
                    foreach (string source in new[] { description, layer })
                        if (rx.Match(source) is Match m && m.Success) return (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();
                return "";
            }

            private List<NetworkSnapshot> ReadNetworks(Transaction tr, CivilDocument civil)
            {
                List<NetworkSnapshot> networks = new List<NetworkSnapshot>();
                foreach (ObjectId netId in civil.GetPipeNetworkIds())
                {
                    _ct.ThrowIfCancellationRequested();
                    if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                    List<ScenePipe> pipes = new List<ScenePipe>();
                    List<SceneStructure> structures = new List<SceneStructure>();
                    foreach (ObjectId id in net.GetPipeIds())
                    {
                        if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                        try
                        {
                            pipes.Add(new ScenePipe(DrawingRefs3D.Ref(id), p.Name, net.Name, TypeCodeOf(p), DrawingRefs3D.Point(p.StartPoint), DrawingRefs3D.Point(p.EndPoint),
                                CoverRoute.OuterWidthOf(p), CoverRoute.OuterHeightOf(p)));
                            _networkOfPart[id] = net.Name;
                        }
                        catch (Exception ex) { Warn($"Pipe {p.Name} could not be read.", ex, p); }
                    }
                    foreach (ObjectId id in net.GetStructureIds())
                    {
                        if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Structure s)) continue;
                        try
                        {
                            string type = "";
                            try { type = s.PartDescription ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                            structures.Add(new SceneStructure(DrawingRefs3D.Ref(id), s.Name, net.Name, type, DrawingRefs3D.Point(s.Position)));
                            _networkOfPart[id] = net.Name;
                        }
                        catch (Exception ex) { Warn($"Structure {s.Name} could not be read.", ex, s); }
                    }
                    networks.Add(new NetworkSnapshot(DrawingRefs3D.Ref(netId), net.Name, pipes, structures));
                }
                return networks;
            }

            // ---- Alignments, profiles, profile views, crossings --------------------------------------------------------

            private static IEnumerable<ObjectId> AllAlignmentIds(Transaction tr, CivilDocument civil)
            {
                foreach (ObjectId id in civil.GetAlignmentIds()) yield return id;
                foreach (ObjectId siteId in civil.GetSiteIds())
                {
                    if (!(tr.GetObject(siteId, OpenMode.ForRead) is Site site)) continue;
                    foreach (ObjectId id in site.GetAlignmentIds()) yield return id;
                }
            }

            private List<AlignmentSnapshot> ReadAlignments(Transaction tr, Database db, CivilDocument civil, List<ProfileViewSnapshot> views, List<CrossingSnapshot> crossings)
            {
                List<AlignmentSnapshot> list = new List<AlignmentSnapshot>();
                ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, civil);
                List<ObjectId> networkIds = civil.GetPipeNetworkIds().Cast<ObjectId>().ToList();
                foreach (ObjectId alId in AllAlignmentIds(tr, civil).Distinct())
                {
                    _ct.ThrowIfCancellationRequested();
                    if (alId.IsErased || !(tr.GetObject(alId, OpenMode.ForRead) is Alignment al)) continue;
                    try
                    {
                        List<SceneProfile> profiles = new List<SceneProfile>();
                        foreach (ObjectId pid in al.GetProfileIds())
                            if (!pid.IsErased && tr.GetObject(pid, OpenMode.ForRead) is Profile p) profiles.Add(ReadProfile(p, al, surfaces));

                        List<DrawingObjectRef> viewRefs = new List<DrawingObjectRef>();
                        ObjectIdCollection viewIds = al.GetProfileViewIds();
                        List<ObjectId> viewIdList = viewIds.Cast<ObjectId>().ToList();
                        List<PlanCrossing> detected = viewIdList.Count == 0 ? new List<PlanCrossing>() : FindCrossings(tr, al, networkIds);
                        foreach (ObjectId vid in viewIdList)
                        {
                            if (vid.IsErased || !(tr.GetObject(vid, OpenMode.ForRead) is ProfileView pv)) continue;
                            ProfileViewSnapshot snap = ReadView(tr, pv, al, surfaces);
                            if (snap == null) continue;
                            views.Add(snap);
                            viewRefs.Add(snap.Ref);
                            crossings.AddRange(ReadCrossings(tr, al, snap, detected));
                        }
                        list.Add(new AlignmentSnapshot(DrawingRefs3D.Ref(alId), al.Name, al.StartingStation, al.EndingStation, profiles, viewRefs));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Warn($"Alignment {al.Name} could not be read.", ex, al); }
                }
                return list;
            }

            private SceneProfile ReadProfile(Profile p, Alignment al, ProfileViewMatch.Surfaces surfaces)
            {
                SceneProfileRole role = SceneProfileRole.Other;
                ProfileType type = ProfileType.FG;
                try { type = p.ProfileType; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                bool surface = type == ProfileType.EG;
                if (surface)
                {
                    string source = surfaces.SourceOf(p, al);
                    if (source.Length > 0 && source.Equals(_rules.ExistingSurface, StringComparison.OrdinalIgnoreCase)) role = SceneProfileRole.Existing;
                    else if (source.Length > 0 && source.Equals(_rules.ProposedSurface, StringComparison.OrdinalIgnoreCase)) role = SceneProfileRole.Proposed;
                }
                else if (ProfileViewMatch.IsTopName(p.Name, _rules)) role = SceneProfileRole.Top;
                return new SceneProfile(DrawingRefs3D.Ref(p.ObjectId), p.Name, role, SampleRuns(p, surface));
            }

            /// <summary>
            /// The profile as station/elevation runs. Layout profiles: the ends of every tangent and samples along vertical curves,
            /// so grade breaks stay exact vertices. Surface profiles: samples at a fixed spacing. A station without an elevation
            /// (a gap in the surface) ends the run.
            /// </summary>
            private List<List<StationElevation>> SampleRuns(Profile p, bool surface)
            {
                SortedSet<double> stations = new SortedSet<double>();
                double s0 = p.StartingStation, s1 = p.EndingStation;
                stations.Add(s0); stations.Add(s1);
                if (surface)
                {
                    double step = Math.Max(0.1, _o.SurfaceSampleM);
                    for (double s = s0 + step; s < s1; s += step) stations.Add(Math.Round(s, 6));
                }
                else
                {
                    try
                    {
                        foreach (ProfileEntity e in p.Entities)
                        {
                            stations.Add(e.StartStation); stations.Add(e.EndStation);
                            if (e.EntityType == ProfileEntityType.Tangent) continue;
                            double step = Math.Max(0.1, _o.CurveSampleM);
                            for (double s = e.StartStation + step; s < e.EndStation; s += step) stations.Add(s);
                        }
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }

                List<List<StationElevation>> runs = new List<List<StationElevation>>();
                List<StationElevation> run = null;
                double last = double.NaN;
                foreach (double s in stations)
                {
                    if (s < s0 - 1e-9 || s > s1 + 1e-9 || (!double.IsNaN(last) && s - last < 1e-6)) continue;
                    double z;
                    try { z = p.ElevationAt(s); }
                    catch (Exception) { run = null; continue; }    // no elevation here: a gap
                    if (!double.IsFinite(z)) { run = null; continue; }
                    if (run == null) { run = new List<StationElevation>(); runs.Add(run); }
                    run.Add(new StationElevation(s, z));
                    last = s;
                }
                return runs.Where(r => r.Count >= 2).ToList();
            }

            private ProfileViewSnapshot ReadView(Transaction tr, ProfileView pv, Alignment al, ProfileViewMatch.Surfaces surfaces)
            {
                try
                {
                    double exaggeration = 1;
                    try
                    {
                        if (tr.GetObject(pv.StyleId, OpenMode.ForRead) is ProfileViewStyle st) exaggeration = st.GraphStyle.VerticalExaggeration;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

                    List<(ObjectId id, ViewPart part, string network, double length)> parts = new List<(ObjectId, ViewPart, string, double)>();
                    foreach (ObjectId partId in ProfileViewMatch.PartsInView(tr, pv, surfaces.Parts).Distinct())
                    {
                        if (partId.IsErased) continue;
                        DBObject obj = tr.GetObject(partId, OpenMode.ForRead);
                        if (obj is Pipe pipe && Station(al, pipe.StartPoint, out double a) && Station(al, pipe.EndPoint, out double b))
                        {
                            double h = CoverRoute.OuterHeightOf(pipe) / 2;
                            parts.Add((partId, new ViewPart(DrawingRefs3D.Ref(partId), ViewPartKind.Pipe, false, a, b,
                                pipe.StartPoint.Z + h, pipe.StartPoint.Z - h, pipe.EndPoint.Z + h, pipe.EndPoint.Z - h),
                                _networkOfPart.TryGetValue(partId, out string n) ? n : "", Math.Abs(b - a)));
                        }
                        else if (obj is Structure s && Faces(al, s, out double f0, out double f1))
                        {
                            double rim = s.RimElevation, floor = s.SumpElevation;
                            parts.Add((partId, new ViewPart(DrawingRefs3D.Ref(partId), ViewPartKind.Structure, false, f0, f1, rim, floor, rim, floor),
                                _networkOfPart.TryGetValue(partId, out string n) ? n : "", 0));
                        }
                    }
                    // The view's own network: the one with the longest pipes drawn in it (the duct); the others are crossings.
                    string own = parts.Where(x => x.part.Kind == ViewPartKind.Pipe && x.network.Length > 0).GroupBy(x => x.network)
                        .OrderByDescending(g => g.Sum(x => x.length)).Select(g => g.Key).FirstOrDefault() ?? "";
                    List<ViewPart> drawn = parts.Select(x => x.part with { IsOwnNetwork = own.Length > 0 && x.network == own }).ToList();

                    return new ProfileViewSnapshot(DrawingRefs3D.Ref(pv.ObjectId), pv.Name, DrawingRefs3D.Ref(al.ObjectId),
                        pv.StationStart, pv.StationEnd, pv.ElevationMin, pv.ElevationMax, "", drawn, exaggeration);
                }
                catch (Exception ex) { Warn($"Profile view {pv.Name} could not be read.", ex, pv); return null; }
            }

            /// <summary>A pipe crossing an alignment in plan: where, and the pipe's centre level and half outer height there.</summary>
            private sealed record PlanCrossing(ObjectId Id, string Network, string Name, double Station, double CentreZ, double HalfHeightM);

            /// <summary>
            /// Every point where a gravity pipe crosses the alignment in plan, tested on each pipe's own path. This does not use
            /// ProfilePartDetection's categories: a pipe with one sample near the alignment is "Partial" there and would be missed.
            /// </summary>
            private List<PlanCrossing> FindCrossings(Transaction tr, Alignment al, List<ObjectId> networkIds)
            {
                List<PlanCrossing> found = new List<PlanCrossing>();
                Extents3d reach;
                try { reach = al.GeometricExtents; } catch (Exception) { return found; }
                double pad = Math.Max(1.0, _o.CrossingToleranceM);
                foreach (ObjectId netId in networkIds)
                {
                    if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                    foreach (ObjectId id in net.GetPipeIds())
                    {
                        if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe)) continue;
                        try
                        {
                            Extents3d box = pipe.GeometricExtents;
                            if (box.MaxPoint.X < reach.MinPoint.X - pad || box.MinPoint.X > reach.MaxPoint.X + pad
                                || box.MaxPoint.Y < reach.MinPoint.Y - pad || box.MinPoint.Y > reach.MaxPoint.Y + pad) continue;
                            ArcInfo? arc = null;
                            if (pipe.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(pipe, out ArcInfo a)) arc = a;
                            List<Point3d> path = PipeArcGeometry.SamplePath(pipe.StartPoint, pipe.EndPoint, arc, 1.0, 2000);
                            double half = CoverRoute.OuterHeightOf(pipe) / 2;
                            foreach ((double station, double z) in CrossingPoints(al, path, _o.CrossingToleranceM))
                                found.Add(new PlanCrossing(id, net.Name, pipe.Name, station, z, half));
                        }
                        catch (Exception ex) { Warn($"Pipe {pipe.Name} could not be tested for crossings.", ex, pipe); }
                    }
                }
                return found;
            }

            private List<CrossingSnapshot> ReadCrossings(Transaction tr, Alignment al, ProfileViewSnapshot view, List<PlanCrossing> crossings)
            {
                HashSet<string> drawn = new HashSet<string>(view.DrawnParts.Select(p => p.Part.Handle));
                string own = view.DrawnParts.Where(p => p.IsOwnNetwork).Select(p => DrawingRefs3D.TryResolve(al.Database, p.Part, out ObjectId id) && _networkOfPart.TryGetValue(id, out string n) ? n : null)
                    .FirstOrDefault(n => n != null) ?? "";
                List<CrossingSnapshot> list = new List<CrossingSnapshot>();
                foreach (PlanCrossing c in crossings)
                {
                    if (c.Network == own) continue;
                    if (c.Station < view.StartStationM - 1e-6 || c.Station > view.EndStationM + 1e-6) continue;
                    DrawingObjectRef r = DrawingRefs3D.Ref(c.Id);
                    list.Add(new CrossingSnapshot(view.Ref, r, c.Network, c.Name, c.Station, c.CentreZ + c.HalfHeightM, c.CentreZ - c.HalfHeightM, drawn.Contains(r.Handle)));
                }
                return list;
            }

            private static bool Station(Alignment al, Point3d p, out double station)
            {
                station = 0;
                double offset = 0;
                try { al.StationOffset(p.X, p.Y, ref station, ref offset); return true; }
                catch (Exception) { return false; }
            }

            /// <summary>The structure's outer faces as stations: its plan box corners projected on the alignment.</summary>
            private static bool Faces(Alignment al, Structure s, out double f0, out double f1)
            {
                f0 = double.MaxValue; f1 = double.MinValue;
                Extents3d box;
                try { box = s.GeometricExtents; } catch (Exception) { return false; }
                foreach (Point3d c in new[] { box.MinPoint, new Point3d(box.MaxPoint.X, box.MinPoint.Y, 0), box.MaxPoint, new Point3d(box.MinPoint.X, box.MaxPoint.Y, 0) })
                    if (Station(al, c, out double st)) { f0 = Math.Min(f0, st); f1 = Math.Max(f1, st); }
                return f1 >= f0;
            }

            /// <summary>
            /// Where a path crosses the alignment in plan: each change of side (the sign of the offset) between two samples,
            /// interpolated, and kept only when the interpolated point lies within <paramref name="tolerance"/> of the alignment
            /// (a side change far from it, past an alignment end or across a tight bend, is not a crossing). Samples exactly on
            /// the alignment take no side: a path that touches and turns back does not cross.
            /// </summary>
            private static List<(double station, double z)> CrossingPoints(Alignment al, List<Point3d> path, double tolerance)
            {
                List<(double, double)> points = new List<(double, double)>();
                int lastSide = 0;
                double lastSta = 0, lastOff = 0;
                Point3d lastP = default;
                foreach (Point3d p in path)
                {
                    double sta = 0, off = 0;
                    try { al.StationOffset(p.X, p.Y, ref sta, ref off); } catch (Exception) { lastSide = 0; continue; }
                    int side = off > 1e-9 ? 1 : off < -1e-9 ? -1 : 0;
                    if (side == 0) continue;
                    if (lastSide != 0 && side != lastSide)
                    {
                        double t = Math.Abs(lastOff) / (Math.Abs(lastOff) + Math.Abs(off));
                        Point3d x = lastP + (p - lastP) * t;
                        double s2 = 0, o2 = 0;
                        try { al.StationOffset(x.X, x.Y, ref s2, ref o2); } catch (Exception) { o2 = double.MaxValue; }
                        if (Math.Abs(o2) <= tolerance) points.Add((s2, x.Z));
                    }
                    lastSide = side; lastSta = sta; lastOff = off; lastP = p;
                }
                return points;
            }

            // ---- Labels ------------------------------------------------------------------------------------------------

            private List<LabelSnapshot> ReadLabels(Transaction tr, Database db, List<SheetViewport> viewports)
            {
                List<LabelSnapshot> labels = new List<LabelSnapshot>();
                List<SheetViewport> byTab = viewports.OrderBy(v => v.TabOrder).ToList();
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased || !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(CivilLabel)))) continue;
                    _ct.ThrowIfCancellationRequested();
                    if (!(tr.GetObject(id, OpenMode.ForRead) is CivilLabel label)) continue;
                    try
                    {
                        Point3d at = label.LabelLocation;
                        double? ratio = null;
                        if (label is PipeLabel pl) try { ratio = pl.Ratio; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        // RotationAngle throws on labels that cannot rotate (most network labels): read it only when it exists.
                        double rotation = label.CanRotate ? label.RotationAngle : 0.0;
                        RecordedPlacement current = new RecordedPlacement(label.StyleName, label.Dragged, at.X, at.Y, ratio, rotation);
                        BuildTag tag = BuildXData.Decode(label.GetXDataForApplication(BuildTag.XDataApplication));
                        string layout = byTab.FirstOrDefault(v => v.Sees(at))?.Layout ?? "";
                        // A label whose part is gone stays in the scene (Target = default): it is still an obstacle, and a pinned
                        // one must be reported, not dropped (section 4, sheets.label.orphanPinned).
                        ObjectId target = ModelTarget(tr, label.FeatureId);
                        LabelSnapshot snap = new LabelSnapshot(DrawingRefs3D.Ref(id), target.IsNull ? default : DrawingRefs3D.Ref(target), layout, tag, current);
                        labels.Add(snap);
                        if (target.IsNull && snap.IsPinned)
                            _issues.Add(OperationIssue.Warning(SheetCodes.LabelOrphanPinned,
                                "A pinned label's part no longer exists. The label is kept; delete or release it if it is not needed.", obj: snap.Ref) with { Layout = layout.Length > 0 ? layout : null });
                    }
                    catch (Exception ex) { Warn("A label could not be read.", ex, label); }
                }
                return labels;
            }

            /// <summary>
            /// The model part (or alignment, profile…) a label belongs to. A profile label's feature is the part's representation
            /// in the view (a ProfileViewPart / ProfileViewPressurePart), which leads to the model part, as AnnotLabels.Read does.
            /// </summary>
            private static ObjectId ModelTarget(Transaction tr, ObjectId feature)
            {
                if (feature.IsNull || feature.IsErased) return ObjectId.Null;
                try
                {
                    DBObject obj = tr.GetObject(feature, OpenMode.ForRead);
                    if (obj is ProfileViewPart vp) return vp.ModelPartId;
                    if (obj is ProfileViewPressurePart pp) return pp.ModelPartId;
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                return feature;
            }
        }
    }
}
