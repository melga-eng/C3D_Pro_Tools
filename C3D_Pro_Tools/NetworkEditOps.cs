using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Linq;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>Which face of a structure pipe ends are fitted to.</summary>
    internal enum StructureFace { InnerWall, OuterWall, Center }

    /// <summary>
    /// A structure's plan footprint from its own size: a box (width along the structure's X axis, the direction its rotation
    /// gives, and length along Y; the same convention Navisworks Import places structures with) or a circle, at the inner or
    /// outer face of its walls. A box of the user's size (<see cref="Box"/>) follows the same convention.
    /// </summary>
    internal sealed class StructureFootprint
    {
        public ObjectId Id;
        public string Name;
        public Point2d Center;
        public double Rotation;
        public bool Round;
        /// <summary>Inner half sizes (box) or inner radius (round, in HalfX), and the wall thickness.</summary>
        public double HalfX, HalfY, Wall;
        public Point2d ExtentsMin, ExtentsMax;

        public static StructureFootprint Of(Structure st)
        {
            StructureFootprint fp = new StructureFootprint { Id = st.ObjectId, Name = st.Name, Center = new Point2d(st.Location.X, st.Location.Y), Rotation = st.Rotation };
            try { fp.Round = st.BoundingShape == BoundingShapeType.Cylinder; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { fp.Wall = Math.Max(0, st.WallThickness); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                double w = st.InnerDiameterOrWidth, l = fp.Round ? w : st.InnerLength;
                fp.HalfX = Math.Max(0, w / 2.0);
                fp.HalfY = Math.Max(0, l / 2.0);
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                Extents3d e = st.GeometricExtents;
                fp.ExtentsMin = new Point2d(e.MinPoint.X, e.MinPoint.Y);
                fp.ExtentsMax = new Point2d(e.MaxPoint.X, e.MaxPoint.Y);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { fp.ExtentsMin = fp.ExtentsMax = fp.Center; }
            // A structure with no readable size still occupies its drawn extents.
            if (fp.HalfX <= 0 || fp.HalfY <= 0)
            {
                fp.HalfX = Math.Max(0.05, (fp.ExtentsMax.X - fp.ExtentsMin.X) / 2.0);
                fp.HalfY = Math.Max(0.05, (fp.ExtentsMax.Y - fp.ExtentsMin.Y) / 2.0);
                fp.Rotation = 0;
            }
            return fp;
        }

        /// <summary>
        /// A box of the user's size instead of the part's: centred on the structure and turned with it, its width along the
        /// structure's own X axis and its length along Y, as the part's own size lies (<see cref="Of"/>), so a box of the
        /// structure's own length × width is its inner wall face. (Releases up to 1.1.0 had the two the other way round.)
        /// </summary>
        public static StructureFootprint Box(Structure st, double length, double width)
        {
            Point2d center = new Point2d(st.Location.X, st.Location.Y);
            return new StructureFootprint
            {
                Id = st.ObjectId, Name = st.Name, Center = center, Rotation = st.Rotation,
                HalfX = Math.Max(0.01, width / 2.0), HalfY = Math.Max(0.01, length / 2.0),
                // No drawn extents: the box alone decides what is inside it.
                ExtentsMin = center, ExtentsMax = center
            };
        }

        /// <summary>The footprint's outline (at the face given) as a closed polyline, e.g. to check a box in the drawing.</summary>
        public Polyline Outline(StructureFace face, double elevation)
        {
            double grow = face == StructureFace.OuterWall ? Wall : 0.0;
            Polyline pl = new Polyline();
            if (Round)
            {
                double r = HalfX + grow;
                pl.AddVertexAt(0, new Point2d(Center.X - r, Center.Y), 1, 0, 0);
                pl.AddVertexAt(1, new Point2d(Center.X + r, Center.Y), 1, 0, 0);
            }
            else
            {
                double hx = HalfX + grow, hy = HalfY + grow, c = Math.Cos(Rotation), s = Math.Sin(Rotation);
                (double x, double y)[] corners = { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) };
                for (int i = 0; i < 4; i++)
                    pl.AddVertexAt(i, new Point2d(Center.X + corners[i].x * c - corners[i].y * s, Center.Y + corners[i].x * s + corners[i].y * c), 0, 0, 0);
            }
            pl.Closed = true;
            pl.Elevation = elevation;
            return pl;
        }

        public static List<StructureFootprint> OfNetwork(Transaction tr, ObjectId networkId)
        {
            List<StructureFootprint> list = new List<StructureFootprint>();
            if (!(tr.GetObject(networkId, OpenMode.ForRead) is Network network)) return list;
            foreach (ObjectId id in network.GetStructureIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Structure st) list.Add(Of(st));
            return list;
        }

        private Vector2d Local(Point2d p)
        {
            double dx = p.X - Center.X, dy = p.Y - Center.Y, c = Math.Cos(-Rotation), s = Math.Sin(-Rotation);
            return new Vector2d(dx * c - dy * s, dx * s + dy * c);
        }

        /// <summary>True when the point lies inside the outer face (plus a margin) or the drawn extents.</summary>
        public bool Contains(Point3d p, double margin = 0.01)
        {
            if (p.X >= ExtentsMin.X - margin && p.X <= ExtentsMax.X + margin && p.Y >= ExtentsMin.Y - margin && p.Y <= ExtentsMax.Y + margin) return true;
            Vector2d l = Local(new Point2d(p.X, p.Y));
            if (Round) return l.Length <= HalfX + Wall + margin;
            return Math.Abs(l.X) <= HalfX + Wall + margin && Math.Abs(l.Y) <= HalfY + Wall + margin;
        }

        /// <summary>
        /// Where the line from <paramref name="otherEnd"/> through <paramref name="end"/> first meets the chosen face (trimming an
        /// end inside the structure, or extending one that stops short), then <paramref name="inset"/> further in. The end comes
        /// back unchanged when the line misses the structure.
        /// </summary>
        public Point3d Fit(Point3d end, Point3d otherEnd, StructureFace face, double inset = 0)
        {
            if (face == StructureFace.Center) return new Point3d(Center.X, Center.Y, end.Z);
            double grow = face == StructureFace.OuterWall ? Wall : 0.0;
            Vector2d a = Local(new Point2d(otherEnd.X, otherEnd.Y)), b = Local(new Point2d(end.X, end.Y));
            Vector2d d = b - a;
            if (d.Length < 1e-9) return end;
            double t;
            if (Round)
            {
                double r = HalfX + grow, A = d.DotProduct(d), B = 2 * a.DotProduct(d), C = a.DotProduct(a) - r * r;
                double disc = B * B - 4 * A * C;
                if (disc < 0) return end;
                t = (-B - Math.Sqrt(disc)) / (2 * A);
            }
            else
            {
                double hx = HalfX + grow, hy = HalfY + grow, tEnter = double.NegativeInfinity, tExit = double.PositiveInfinity;
                foreach ((double p0, double dp, double h) in new[] { (a.X, d.X, hx), (a.Y, d.Y, hy) })
                {
                    if (Math.Abs(dp) < 1e-12) { if (Math.Abs(p0) > h) return end; continue; }
                    double t1 = (-h - p0) / dp, t2 = (h - p0) / dp;
                    tEnter = Math.Max(tEnter, Math.Min(t1, t2));
                    tExit = Math.Min(tExit, Math.Max(t1, t2));
                }
                if (tEnter > tExit) return end;
                t = tEnter;
            }
            if (t <= 1e-6) return end;
            t += inset / d.Length;
            return new Point3d(otherEnd.X + (end.X - otherEnd.X) * t, otherEnd.Y + (end.Y - otherEnd.Y) * t, end.Z);
        }
    }

    /// <summary>The Network Editor's operations on existing pipes. Each runs inside the caller's transaction.</summary>
    internal static class NetworkEditOps
    {
        // Smallest radius an offset arc may shrink to before the copy is skipped (m).
        private const double MinArcRadius = 0.05;
        private const double MinPipeLength = 0.05;

        internal sealed class Counts
        {
            public int Done, Created, Skipped, Failed, Structures;
            /// <summary>Copy ends connected to a structure.</summary>
            public int Joined;
            /// <summary>New pipes made by copying a pipe in the drawing (its part has no family to build from).</summary>
            public int Copied;
            /// <summary>Networks that kept family-less copies which could not be moved to the requested network.</summary>
            public readonly HashSet<string> StayedIn = new HashSet<string>();
            /// <summary>Pipe ends placed where Civil 3D would not keep them connected to their structure.</summary>
            public int Unconnected;
            public readonly List<ObjectId> NewIds = new List<ObjectId>();
            /// <summary>Parts list changes made on the way (families and sizes added, networks switched to another list).</summary>
            public readonly List<string> Log = new List<string>();
            /// <summary>The counts as they are now, to put back when an operation is rolled back.</summary>
            internal (int done, int created, int skipped, int structures, int joined, int copied, int unconnected, int newIds) Snapshot() =>
                (Done, Created, Skipped, Structures, Joined, Copied, Unconnected, NewIds.Count);

            internal void Restore((int done, int created, int skipped, int structures, int joined, int copied, int unconnected, int newIds) s)
            {
                (Done, Created, Skipped, Structures, Joined, Copied, Unconnected) = (s.done, s.created, s.skipped, s.structures, s.joined, s.copied, s.unconnected);
                if (NewIds.Count > s.newIds) NewIds.RemoveRange(s.newIds, NewIds.Count - s.newIds);
            }

            private readonly Dictionary<ObjectId, (ObjectId family, ObjectId size)> _resolved = new Dictionary<ObjectId, (ObjectId, ObjectId)>();

            /// <summary>The chosen part in a network's parts list (added there, or the network switched, once per network).</summary>
            public (ObjectId family, ObjectId size) PartFor(Transaction tr, ObjectId networkId, PartPick pick)
            {
                if (!_resolved.TryGetValue(networkId, out var ids)) _resolved[networkId] = ids = PartsListTools.Resolve(tr, networkId, ObjectId.Null, pick, Log);
                return ids;
            }
        }

        // =====================================================================================
        // Split
        // =====================================================================================

        internal sealed class SplitPlan
        {
            public ObjectId PipeId;
            public string Name;
            public bool Curved;
            /// <summary>Split points, ordered from the pipe's start; each lies on the pipe (Z on its centreline).</summary>
            public readonly List<Point3d> Points = new List<Point3d>();
            public double Length;
        }

        /// <summary>Plan length and the point at a plan distance from the start, for straight and curved pipes.</summary>
        private static (double length, Func<double, Point3d> at) Measure(Pipe pipe)
        {
            Point3d s = pipe.StartPoint, e = pipe.EndPoint;
            if (pipe.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(pipe, out ArcInfo arc))
            {
                List<Point3d> path = PipeArcGeometry.SamplePath(s, e, arc, 0.05, 20000);
                List<double> along = new List<double> { 0 };
                for (int i = 1; i < path.Count; i++) along.Add(along[i - 1] + new Point2d(path[i - 1].X, path[i - 1].Y).GetDistanceTo(new Point2d(path[i].X, path[i].Y)));
                double total = along[along.Count - 1];
                return (total, d =>
                {
                    int i = along.BinarySearch(d);
                    if (i < 0) i = Math.Max(1, Math.Min(along.Count - 1, ~i));
                    if (i == 0) i = 1;
                    double t = (d - along[i - 1]) / Math.Max(1e-12, along[i] - along[i - 1]);
                    Point3d p = path[i - 1] + (path[i] - path[i - 1]) * t;
                    return new Point3d(p.X, p.Y, s.Z + (e.Z - s.Z) * d / Math.Max(1e-9, total));
                });
            }
            double length = new Point2d(s.X, s.Y).GetDistanceTo(new Point2d(e.X, e.Y));
            return (length, d => s + (e - s) * (d / Math.Max(1e-9, length)));
        }

        /// <summary>Splits into equal parts, or every <paramref name="every"/> m from the start (the last piece takes the rest).</summary>
        public static List<SplitPlan> PlanSplit(Transaction tr, IEnumerable<ObjectId> pipeIds, int parts, double every)
        {
            List<SplitPlan> plans = new List<SplitPlan>();
            foreach (ObjectId id in pipeIds)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe)) continue;
                (double length, Func<double, Point3d> at) = Measure(pipe);
                SplitPlan plan = new SplitPlan { PipeId = id, Name = pipe.Name, Curved = pipe.SubEntityType == PipeSubEntityType.Curved, Length = length };
                if (every > 0)
                {
                    for (double d = every; d < length - MinPipeLength; d += every) plan.Points.Add(at(d));
                }
                else
                {
                    for (int i = 1; i < parts; i++) plan.Points.Add(at(length * i / parts));
                }
                if (plan.Points.Count > 0) plans.Add(plan);
            }
            return plans;
        }

        /// <summary>Splits the pipe nearest each picked point (within <paramref name="tolerance"/> in plan) at that point.</summary>
        public static List<SplitPlan> PlanSplitAtPoints(Transaction tr, IEnumerable<ObjectId> candidatePipes, List<Point3d> picks, double tolerance, out int missed)
        {
            missed = 0;
            Dictionary<ObjectId, SplitPlan> byPipe = new Dictionary<ObjectId, SplitPlan>();
            List<(Pipe pipe, double length, Func<double, Point3d> at)> pipes = candidatePipes
                .Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe).Where(p => p != null)
                .Select(p => { var m = Measure(p); return (p, m.length, m.at); }).ToList();
            foreach (Point3d pick in picks)
            {
                Pipe best = null;
                double bestDist = tolerance, bestAlong = 0, bestLength = 0;
                Func<double, Point3d> bestAt = null;
                foreach ((Pipe pipe, double length, Func<double, Point3d> at) in pipes)
                {
                    // Pipes nowhere near the pick are skipped before the fine search (whole networks can be thousands of pipes).
                    Point3d ps = pipe.StartPoint, pe = pipe.EndPoint, pm = at(length / 2.0);
                    double reach = Math.Max(new Point2d(ps.X, ps.Y).GetDistanceTo(new Point2d(pm.X, pm.Y)), new Point2d(pe.X, pe.Y).GetDistanceTo(new Point2d(pm.X, pm.Y)));
                    if (new Point2d(pick.X, pick.Y).GetDistanceTo(new Point2d(pm.X, pm.Y)) > reach + tolerance) continue;
                    // The nearest point on the pipe, searched along it (curved pipes included).
                    int n = Math.Max(2, Math.Min(4000, (int)(length / 0.05)));
                    for (int i = 0; i <= n; i++)
                    {
                        double d = length * i / n;
                        Point3d p = at(d);
                        double dist = new Point2d(p.X, p.Y).GetDistanceTo(new Point2d(pick.X, pick.Y));
                        if (dist < bestDist) { bestDist = dist; best = pipe; bestAlong = d; bestLength = length; bestAt = at; }
                    }
                }
                if (best == null || bestAlong < MinPipeLength || bestAlong > bestLength - MinPipeLength) { missed++; continue; }
                if (!byPipe.TryGetValue(best.ObjectId, out SplitPlan plan))
                    byPipe[best.ObjectId] = plan = new SplitPlan { PipeId = best.ObjectId, Name = best.Name, Curved = best.SubEntityType == PipeSubEntityType.Curved, Length = bestLength };
                plan.Points.Add(bestAt(bestAlong));
            }
            foreach (SplitPlan plan in byPipe.Values)
            {
                Pipe pipe = (Pipe)tr.GetObject(plan.PipeId, OpenMode.ForRead);
                Point3d s = pipe.StartPoint;
                plan.Points.Sort((a, b) => new Point2d(s.X, s.Y).GetDistanceTo(new Point2d(a.X, a.Y)).CompareTo(new Point2d(s.X, s.Y).GetDistanceTo(new Point2d(b.X, b.Y))));
            }
            return byPipe.Values.ToList();
        }

        /// <summary>
        /// Splits each pipe at its points: straight pipes keep their first piece and get copies (same family, size, style and
        /// layer, on the same 3D line) for the rest, the original end connection moving to the last piece; curved pipes are
        /// broken by Civil 3D. With a structure family, a structure joins the pieces at every split point.
        /// </summary>
        public static Counts ApplySplit(Transaction tr, List<SplitPlan> plans, PartPick structure, Editor ed)
        {
            Counts c = new Counts();
            foreach (SplitPlan plan in plans)
            {
                // Read here; written only inside the operation's own transaction, so a split that fails leaves the pipe as it was.
                Network network = (Network)tr.GetObject(((Pipe)tr.GetObject(plan.PipeId, OpenMode.ForRead)).NetworkId, OpenMode.ForRead);
                ObjectId structureFamilyId = ObjectId.Null, structureSizeId = ObjectId.Null;
                if (structure != null)
                {
                    (structureFamilyId, structureSizeId) = c.PartFor(tr, network.ObjectId, structure);
                    if (structureSizeId.IsNull) ed.WriteMessage($"\n[Warning] {structure.Label} could not be put in the parts list of network {network.Name}; {plan.Name} is split without structures.");
                }
                bool addStructures = !structureFamilyId.IsNull && !structureSizeId.IsNull;
                ObjectId networkId = network.ObjectId, surfaceId = network.ReferenceSurfaceId;
                try
                {
                    Atomic(tr, c, t =>
                    {
                        Pipe pipe = (Pipe)t.GetObject(plan.PipeId, OpenMode.ForWrite);
                        Network net = (Network)t.GetObject(networkId, OpenMode.ForWrite);
                        Autodesk.Civil.DatabaseServices.Surface surface = surfaceId.IsNull ? null : t.GetObject(surfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                        if (plan.Curved) { SplitCurved(t, net, pipe, plan, addStructures, structureFamilyId, structureSizeId, surface, c); return; }

                        ObjectId familyId = pipe.PartFamilyId, sizeId = PipePartCatalog.SizeIdOf(t, pipe);
                        Point3d start = pipe.StartPoint, end = pipe.EndPoint;
                        ObjectId endStructure = pipe.EndStructureId;
                        if (!endStructure.IsNull) pipe.Disconnect(ConnectorPositionType.End);
                        pipe.EndPoint = plan.Points[0];
                        if (!pipe.StartPoint.IsEqualTo(start)) pipe.StartPoint = start;

                        Pipe previous = pipe;
                        for (int i = 0; i < plan.Points.Count; i++)
                        {
                            Point3d a = plan.Points[i], b = i + 1 < plan.Points.Count ? plan.Points[i + 1] : end;
                            ObjectId newId = ObjectId.Null;
                            using (LineSegment3d line = new LineSegment3d(a, b)) net.AddLinePipe(familyId, sizeId, line, ref newId, false);
                            Pipe piece = (Pipe)t.GetObject(newId, OpenMode.ForWrite);
                            piece.StyleId = pipe.StyleId;
                            piece.Layer = pipe.Layer;
                            c.Created++;
                            c.NewIds.Add(newId);
                            if (addStructures) JoinAtStructure(t, net, previous, piece, a, Math.Atan2(end.Y - start.Y, end.X - start.X), structureFamilyId, structureSizeId, surface, c, ed);
                            previous = piece;
                        }
                        if (!endStructure.IsNull) ConnectOrThrow(previous, ConnectorPositionType.End, endStructure);
                        c.Done++;
                    });
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Splitting pipe <{plan.Name}>: {ex.Message} The pipe is left as it was.");
                    c.Failed++;
                }
            }
            return c;
        }

        /// <summary>
        /// Runs one operation in its own nested transaction: kept when it succeeds; undone, with its counts, when it throws (the
        /// exception is passed on). The operation opens what it changes through the transaction it is given.
        /// </summary>
        private static void Atomic(Transaction tr, Counts c, Action<Transaction> operation)
        {
            var before = c.Snapshot();
            using (Transaction t = tr.TransactionManager.StartTransaction())
            {
                try
                {
                    operation(t);
                    t.Commit();
                }
                catch
                {
                    t.Abort();
                    c.Restore(before);
                    throw;
                }
            }
        }

        private static void SplitCurved(Transaction tr, Network network, Pipe pipe, SplitPlan plan, bool addStructures, ObjectId familyId, ObjectId sizeId,
            Autodesk.Civil.DatabaseServices.Surface surface, Counts c)
        {
            // Break from the far end back, so each break point stays on the original pipe.
            ObjectId current = pipe.ObjectId;
            for (int i = plan.Points.Count - 1; i >= 0; i--)
            {
                Point3d p = plan.Points[i];
                ObjectId structureId = ObjectId.Null;
                if (addStructures)
                {
                    network.AddStructure(familyId, sizeId, p, 0, ref structureId, false);
                    c.Structures++;
                }
                ObjectId newId = ObjectId.Null;
                network.BreakPipe(current, p, structureId, ref newId);
                if (!newId.IsNull) { c.Created++; c.NewIds.Add(newId); }
                if (!structureId.IsNull) SetRimAndSump(tr, structureId, surface, p.Z - pipe.InnerHeight / 2.0);
            }
            c.Done++;
        }

        private static void JoinAtStructure(Transaction tr, Network network, Pipe upstream, Pipe downstream, Point3d at, double rotation,
            ObjectId familyId, ObjectId sizeId, Autodesk.Civil.DatabaseServices.Surface surface, Counts c, Editor ed)
        {
            ObjectId structureId = ObjectId.Null;
            network.AddStructure(familyId, sizeId, at, rotation, ref structureId, false);
            c.Structures++;
            ConnectOrThrow(upstream, ConnectorPositionType.End, structureId);
            ConnectOrThrow(downstream, ConnectorPositionType.Start, structureId);
            SetRimAndSump(tr, structureId, surface, Math.Min(upstream.EndPoint.Z - upstream.InnerHeight / 2.0, downstream.StartPoint.Z - downstream.InnerHeight / 2.0));
        }

        private static void SetRimAndSump(Transaction tr, ObjectId structureId, Autodesk.Civil.DatabaseServices.Surface surface, double lowestInvert)
        {
            Structure st = (Structure)tr.GetObject(structureId, OpenMode.ForWrite);
            if (surface != null)
            {
                double g = CoverRoute.GroundAt(surface, st.Location.X, st.Location.Y);
                if (!double.IsNaN(g)) try { st.RimElevation = g; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            try { st.ControlSumpBy = StructureControlSumpType.ByElevation; st.SumpElevation = lowestInvert; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        // =====================================================================================
        // Parallel copies (offsets, duct bank conduit arrays)
        // =====================================================================================

        internal sealed class ParallelOptions
        {
            /// <summary>Plan offsets (+ = right of the pipe's direction) and vertical offsets (+ = up), in m; every combination is copied.</summary>
            public List<double> Horizontal = new List<double>(), Vertical = new List<double> { 0 };
            /// <summary>True: copies go into the source pipe's network; false: into one new network per type.</summary>
            public bool SameNetwork = true;
            public string NewNetworkName = "Parallel copies";
            public ObjectId PartsListId;
            /// <summary>Gravity copies use this part (put in the target network's parts list); null = the source pipe's own.</summary>
            public PartPick Part;
            /// <summary>Straight copies end at the source pipe's structures (their walls), instead of running to its ends.</summary>
            public bool FitToStructures;
            public StructureFace Face = StructureFace.InnerWall;
            /// <summary>When both are positive, copies end at a box of this length × width on each structure instead of its own size.</summary>
            public double BoxLength, BoxWidth;
            /// <summary>Conduits: a copy is made at the source position too (the source is the encasement, not one of the pipes).</summary>
            public bool IncludeSource;
            /// <summary>Conduits: vertical offsets are measured up from the source pipe's outside bottom instead of its centreline.</summary>
            public bool FromBottom;
            /// <summary>
            /// Straight pipes chosen together that meet end to end with no structure between them: their copies meet at the mitre
            /// of the two offset lines (one continuous offset run, no wedge or gap at the deflection).
            /// </summary>
            public bool Mitre = true;
            /// <summary>Copies in the source pipe's network whose end lies inside one of its structures are connected to that structure.</summary>
            public bool ConnectToStructures = true;
        }

        /// <summary>Plan distance (m) within which two pipe ends count as one joint.</summary>
        private const double JointTolerance = 0.01;

        /// <summary>
        /// For each chosen straight gravity pipe, the far end of the chosen pipe joined to its start / end with no structure there
        /// (in the same direction of travel), for mitred copies.
        /// </summary>
        private static (Dictionary<ObjectId, Point3d> before, Dictionary<ObjectId, Point3d> after) FreeJoints(Transaction tr, List<ObjectId> pipeIds)
        {
            var before = new Dictionary<ObjectId, Point3d>();
            var after = new Dictionary<ObjectId, Point3d>();
            List<Pipe> straight = pipeIds.Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                .Where(p => p != null && p.SubEntityType == PipeSubEntityType.Straight).ToList();
            bool Same(Point3d a, Point3d b) => new Point2d(a.X, a.Y).GetDistanceTo(new Point2d(b.X, b.Y)) <= JointTolerance && Math.Abs(a.Z - b.Z) <= JointTolerance;
            foreach (Pipe p in straight)
                foreach (Pipe q in straight)
                {
                    if (p == q || !p.EndStructureId.IsNull || !q.StartStructureId.IsNull || !Same(p.EndPoint, q.StartPoint)) continue;
                    if (!after.ContainsKey(p.ObjectId) && !before.ContainsKey(q.ObjectId)) { after[p.ObjectId] = q.EndPoint; before[q.ObjectId] = p.StartPoint; }
                }
            return (before, after);
        }

        /// <summary>The point offset <paramref name="h"/> (+ = right) at the mitre of the legs a→joint→b, at level <paramref name="z"/>.</summary>
        public static Point3d MitreAt(Point3d a, Point3d joint, Point3d b, double h, double z)
        {
            (double X, double Y) m = PlanGeometry.OffsetRun(new List<(double X, double Y)> { (a.X, a.Y), (joint.X, joint.Y), (b.X, b.Y) }, h)[1];
            return new Point3d(m.X, m.Y, z);
        }

        public static Counts ApplyParallel(Transaction tr, CivilDocument doc, Database db, List<ObjectId> pipeIds, ParallelOptions o, Editor ed)
        {
            Counts c = new Counts();
            ObjectId newGravity = ObjectId.Null, newPressure = ObjectId.Null;
            List<(double h, double v)> offsets = new List<(double, double)>();
            foreach (double h in o.Horizontal) foreach (double v in o.Vertical) if (o.IncludeSource || o.FromBottom || Math.Abs(h) > 1e-9 || Math.Abs(v) > 1e-9) offsets.Add((h, v));
            Dictionary<ObjectId, StructureFootprint> footprints = new Dictionary<ObjectId, StructureFootprint>();
            StructureFootprint FootprintOf(ObjectId id)
            {
                if (id.IsNull) return null;
                if (!footprints.TryGetValue(id, out StructureFootprint fp) && tr.GetObject(id, OpenMode.ForRead) is Structure st)
                    footprints[id] = fp = o.BoxLength > 0 && o.BoxWidth > 0 ? StructureFootprint.Box(st, o.BoxLength, o.BoxWidth) : StructureFootprint.Of(st);
                return fp;
            }
            // The structures' own sizes: Civil 3D keeps a pipe end connected only inside them.
            Dictionary<ObjectId, StructureFootprint> own = new Dictionary<ObjectId, StructureFootprint>();
            StructureFootprint OwnFootprint(ObjectId id)
            {
                if (id.IsNull) return null;
                if (!own.TryGetValue(id, out StructureFootprint fp) && tr.GetObject(id, OpenMode.ForRead) is Structure st) own[id] = fp = StructureFootprint.Of(st);
                return fp;
            }
            (Dictionary<ObjectId, Point3d> before, Dictionary<ObjectId, Point3d> after) = o.Mitre
                ? FreeJoints(tr, pipeIds) : (new Dictionary<ObjectId, Point3d>(), new Dictionary<ObjectId, Point3d>());

            foreach (ObjectId id in pipeIds)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is Pipe src)
                {
                    if (src.SubEntityType == PipeSubEntityType.Segmented || src.SubEntityType == PipeSubEntityType.Flex)
                    {
                        ed.WriteMessage($"\n[Skipped] Pipe <{src.Name}> is a {src.SubEntityType} pipe; only straight and curved pipes can be copied.");
                        c.Skipped += offsets.Count;
                        continue;
                    }
                    bool curved = src.SubEntityType == PipeSubEntityType.Curved;
                    ArcInfo arc = default;
                    if (curved && !PipeArcGeometry.TryGetGravityArc(src, out arc)) { ed.WriteMessage($"\n[Failed] Curved pipe <{src.Name}>: its arc could not be read."); c.Failed += offsets.Count; continue; }

                    ObjectId targetId = src.NetworkId;
                    if (!o.SameNetwork)
                    {
                        if (newGravity.IsNull)
                        {
                            string name = o.NewNetworkName;
                            newGravity = Network.Create(doc, ref name);
                            Network created = (Network)tr.GetObject(newGravity, OpenMode.ForWrite);
                            Network srcNet = (Network)tr.GetObject(src.NetworkId, OpenMode.ForRead);
                            try { created.PartsListId = o.PartsListId.IsNull ? srcNet.PartsListId : o.PartsListId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                            try { created.ReferenceSurfaceId = srcNet.ReferenceSurfaceId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        }
                        targetId = newGravity;
                    }
                    Network target = (Network)tr.GetObject(targetId, OpenMode.ForWrite);
                    ObjectId familyId = src.PartFamilyId, sizeId = PipePartCatalog.SizeIdOf(tr, src);
                    // Parts with no family (duct banks, conduits) are copied from a pipe in the drawing instead of built.
                    Pipe template = o.Part == null ? src
                        : !o.Part.TemplateId.IsNull && !o.Part.TemplateId.IsErased ? tr.GetObject(o.Part.TemplateId, OpenMode.ForRead) as Pipe : null;
                    if (o.Part != null)
                    {
                        (familyId, sizeId) = c.PartFor(tr, targetId, o.Part);
                        if (sizeId.IsNull && template == null) { ed.WriteMessage($"\n[Skipped] Pipe <{src.Name}>: {o.Part.Label} could not be put in the parts list of network {target.Name}."); c.Skipped += offsets.Count; continue; }
                    }
                    bool build = PartCopy.IsFamily(tr, familyId) && !sizeId.IsNull;
                    if (!build && curved)
                    {
                        ed.WriteMessage($"\n[Skipped] Curved pipe <{src.Name}>: the part has no family, and only straight copies can be made by copying a pipe.");
                        c.Skipped += offsets.Count; continue;
                    }

                    Point3d s = src.StartPoint, e = src.EndPoint;
                    if (o.FromBottom)
                    {
                        // The pipe's points are on its centreline; the offsets start at its outside bottom.
                        Vector3d down = new Vector3d(0, 0, -CoverRoute.OuterHeightOf(src) / 2.0);
                        s += down; e += down;
                    }
                    StructureFootprint startFp = o.FitToStructures && !curved ? FootprintOf(src.StartStructureId) : null;
                    StructureFootprint endFp = o.FitToStructures && !curved ? FootprintOf(src.EndStructureId) : null;
                    foreach ((double h, double v) in offsets)
                    {
                        try
                        {
                            ObjectId newId = ObjectId.Null;
                            if (curved)
                            {
                                if (!TryOffsetArc(s, e, arc, h, out Point3d ns, out Point3d ne, out Point2d center, out bool clockwise))
                                {
                                    ed.WriteMessage($"\n[Skipped] Curved pipe <{src.Name}>: an offset of {h:0.###} m collapses its radius ({arc.Radius:0.###} m)."); c.Skipped++; continue;
                                }
                                using (PipeCurveGeometry g = new PipeCurveGeometry(new Point3d(ns.X, ns.Y, ns.Z + v), center, new Point3d(ne.X, ne.Y, ne.Z + v), clockwise))
                                    newId = target.AddCurvePipe(familyId, sizeId, g, false);
                            }
                            else
                            {
                                if (!TryOffsetLine(s, e, h, out Point3d ns, out Point3d ne)) { ed.WriteMessage($"\n[Skipped] Pipe <{src.Name}> has no length in plan."); c.Skipped++; continue; }
                                // Joined end to end with another chosen pipe (no structure): both copies end at the mitre.
                                if (before.TryGetValue(id, out Point3d prev)) ns = MitreAt(prev, s, e, h, s.Z);
                                if (after.TryGetValue(id, out Point3d next)) ne = MitreAt(s, e, next, h, e.Z);
                                // Each copy meets the wall where its own line does (round manholes are nearer at an offset).
                                Point3d fs = startFp != null ? startFp.Fit(ns, ne, o.Face) : ns, fe = endFp != null ? endFp.Fit(ne, ns, o.Face) : ne;
                                KeepSlope(ns, ne, ref fs, ref fe);
                                ns = fs; ne = fe;
                                newId = PartCopy.Pipe(tr, target, build ? familyId : ObjectId.Null, sizeId, template,
                                    new Point3d(ns.X, ns.Y, ns.Z + v), new Point3d(ne.X, ne.Y, ne.Z + v), out bool copiedTemplate, stayIfNotMovable: true);
                                if (copiedTemplate) c.Copied++;
                            }
                            if (newId.IsNull) throw new InvalidOperationException("Civil 3D did not return the new pipe.");
                            Pipe copy = (Pipe)tr.GetObject(newId, OpenMode.ForWrite);
                            if (copy.NetworkId != targetId && c.StayedIn.Add(copy.NetworkName))
                                ed.WriteMessage($"\n[Note] {copy.PartSizeName} has no part family, so its copies stay in network {copy.NetworkName} (Civil 3D cannot move such parts to {target.Name}).");
                            // A copied template keeps its own style and layer; a built part follows the source pipe.
                            if (build || o.Part == null)
                            {
                                if (o.Part == null) copy.StyleId = src.StyleId;
                                copy.Layer = src.Layer;
                            }
                            c.Created++;
                            c.NewIds.Add(newId);
                            // In the source's network, an end inside one of its structures is connected there.
                            if (o.ConnectToStructures && !curved && copy.NetworkId == src.NetworkId)
                                foreach ((ObjectId sid, bool atStart) in new[] { (src.StartStructureId, true), (src.EndStructureId, false) })
                                {
                                    StructureFootprint fp = OwnFootprint(sid);
                                    if (fp == null || !fp.Contains(atStart ? copy.StartPoint : copy.EndPoint, 0.0)) continue;
                                    CoverApply.Connect(copy, true, atStart, sid, ed);
                                    c.Joined++;
                                }
                        }
                        catch (Exception ex) { ed.WriteMessage($"\n[Error] Copying pipe <{src.Name}>: {ex.Message}"); c.Failed++; }
                    }
                    c.Done++;
                }
                else if (obj is PressurePipe pp)
                {
                    if (pp.IsVerticalCurve) { ed.WriteMessage($"\n[Skipped] Pressure pipe <{pp.Name}> is a vertical curve."); c.Skipped += offsets.Count; continue; }
                    PressurePartSize size = pp.GetPartSize();
                    if (size == null) { ed.WriteMessage($"\n[Skipped] Pressure pipe <{pp.Name}>: its size could not be read."); c.Skipped += offsets.Count; continue; }
                    ArcInfo arc = default;
                    if (pp.IsCurve && !PipeArcGeometry.TryGetPressureArc(pp, out arc)) { c.Failed += offsets.Count; continue; }

                    ObjectId targetId = pp.NetworkId;
                    if (!o.SameNetwork)
                    {
                        if (newPressure.IsNull)
                        {
                            newPressure = PressurePipeNetwork.Create(db, UniquePressureNetworkName(doc, tr, o.NewNetworkName));
                            if (tr.GetObject(pp.NetworkId, OpenMode.ForRead) is PressurePipeNetwork srcNet && tr.GetObject(newPressure, OpenMode.ForWrite) is PressurePipeNetwork created)
                                try { created.PartsListId = srcNet.PartsListId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        }
                        targetId = newPressure;
                    }
                    PressurePipeNetwork target = (PressurePipeNetwork)tr.GetObject(targetId, OpenMode.ForWrite);
                    foreach ((double h, double v) in offsets)
                    {
                        try
                        {
                            ObjectId newId;
                            if (pp.IsCurve)
                            {
                                if (!TryOffsetArc(pp.StartPoint, pp.EndPoint, arc, h, out Point3d ns, out Point3d ne, out Point2d center, out bool clockwise)) { c.Skipped++; continue; }
                                using (PipeCurveGeometry g = new PipeCurveGeometry(new Point3d(ns.X, ns.Y, ns.Z + v), center, new Point3d(ne.X, ne.Y, ne.Z + v), clockwise))
                                    newId = target.AddCurvePipe(g, size);
                            }
                            else
                            {
                                if (!TryOffsetLine(pp.StartPoint, pp.EndPoint, h, out Point3d ns, out Point3d ne)) { c.Skipped++; continue; }
                                using (LineSegment3d line = new LineSegment3d(new Point3d(ns.X, ns.Y, ns.Z + v), new Point3d(ne.X, ne.Y, ne.Z + v)))
                                    newId = target.AddLinePipe(line, size);
                            }
                            if (newId.IsNull) throw new InvalidOperationException("Civil 3D did not return the new pressure pipe.");
                            if (tr.GetObject(newId, OpenMode.ForWrite) is PressurePipe copy) { copy.StyleId = pp.StyleId; copy.Layer = pp.Layer; }
                            c.Created++;
                            c.NewIds.Add(newId);
                        }
                        catch (Exception ex) { ed.WriteMessage($"\n[Error] Copying pressure pipe <{pp.Name}>: {ex.Message}"); c.Failed++; }
                    }
                    c.Done++;
                }
            }
            return c;
        }

        private static string UniquePressureNetworkName(CivilDocument doc, Transaction tr, string baseName)
        {
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in doc.GetPressurePipeNetworkIds())
                if (tr.GetObject(id, OpenMode.ForRead) is PressurePipeNetwork net) existing.Add(net.Name);
            string name = baseName;
            for (int i = 1; existing.Contains(name); i++) name = $"{baseName} ({i})";
            return name;
        }

        /// <summary>Concentric offset of a plan arc (+ = right of travel); each end keeps its elevation.</summary>
        public static bool TryOffsetArc(Point3d start, Point3d end, ArcInfo arc, double offset, out Point3d newStart, out Point3d newEnd, out Point2d center, out bool clockwise)
        {
            newStart = newEnd = Point3d.Origin;
            center = arc.Center;
            Point2d s = new Point2d(start.X, start.Y), e = new Point2d(end.X, end.Y);
            clockwise = PipeArcGeometry.IsClockwise(s, arc.Mid, e);
            // Right of travel is away from the centre on a counter-clockwise arc and toward it on a clockwise one.
            double radius = arc.Radius + (clockwise ? -offset : offset);
            if (radius < MinArcRadius) return false;
            Vector2d rs = (s - center).GetNormal(), re = (e - center).GetNormal();
            newStart = new Point3d(center.X + rs.X * radius, center.Y + rs.Y * radius, start.Z);
            newEnd = new Point3d(center.X + re.X * radius, center.Y + re.Y * radius, end.Z);
            return true;
        }

        public static bool TryOffsetLine(Point3d start, Point3d end, double offset, out Point3d newStart, out Point3d newEnd)
        {
            newStart = start; newEnd = end;
            Vector2d dir = new Vector2d(end.X - start.X, end.Y - start.Y);
            if (dir.Length < 1e-6) return false;
            dir = dir.GetNormal();
            // (dir.Y, -dir.X) is the plan normal to the right of travel.
            Vector3d right = new Vector3d(dir.Y, -dir.X, 0.0);
            newStart = start + right * offset;
            newEnd = end + right * offset;
            return true;
        }

        /// <summary>
        /// Re-levels moved pipe ends so the pipe keeps its slope: the new start takes the level of the original line at its
        /// position along the pipe, and the end follows at the original slope over the new length.
        /// </summary>
        public static void KeepSlope(Point3d origStart, Point3d origEnd, ref Point3d newStart, ref Point3d newEnd)
        {
            Vector2d axis = new Vector2d(origEnd.X - origStart.X, origEnd.Y - origStart.Y);
            double length = axis.Length;
            if (length < 1e-9) return;
            double slope = (origEnd.Z - origStart.Z) / length;
            double along = axis.GetNormal().DotProduct(new Vector2d(newStart.X - origStart.X, newStart.Y - origStart.Y));
            double startZ = origStart.Z + slope * along;
            double newLength = new Point2d(newStart.X, newStart.Y).GetDistanceTo(new Point2d(newEnd.X, newEnd.Y));
            newStart = new Point3d(newStart.X, newStart.Y, startZ);
            newEnd = new Point3d(newEnd.X, newEnd.Y, startZ + slope * newLength);
        }

        // =====================================================================================
        // Fit pipe ends to structures
        // =====================================================================================

        // Fitted ends sit this far inside the face: Civil 3D drops the connection of an end lying on a structure's outline.
        public const double FitInset = 0.002;

        /// <summary>
        /// The new ends of each pipe at its structures (trimmed or extended to the chosen face of each structure's own size, or to
        /// a box of the given length × width when both are positive, keeping the slope), including loose ends lying inside a
        /// structure that would be connected to it. Read-only.
        /// </summary>
        public static List<ManholeAdjustment> PlanFit(Transaction tr, List<ObjectId> pipeIds, StructureFace face, bool connectLooseEnds, double boxLength = 0, double boxWidth = 0)
        {
            List<ManholeAdjustment> plan = new List<ManholeAdjustment>();
            Dictionary<ObjectId, List<StructureFootprint>> byNetwork = new Dictionary<ObjectId, List<StructureFootprint>>();
            // The faces ends are fitted to: the structures' own (as for finding loose ends), or the user's boxes.
            Dictionary<ObjectId, StructureFootprint> boxes = new Dictionary<ObjectId, StructureFootprint>();
            bool useBox = boxLength > 0 && boxWidth > 0;
            StructureFootprint FitOf(StructureFootprint own)
            {
                if (own == null || !useBox) return own;
                if (!boxes.TryGetValue(own.Id, out StructureFootprint box)) boxes[own.Id] = box = StructureFootprint.Box((Structure)tr.GetObject(own.Id, OpenMode.ForRead), boxLength, boxWidth);
                return box;
            }
            if (useBox) face = StructureFace.InnerWall;
            foreach (ObjectId id in pipeIds)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe) || pipe.SubEntityType != PipeSubEntityType.Straight) continue;
                if (!byNetwork.TryGetValue(pipe.NetworkId, out List<StructureFootprint> footprints)) byNetwork[pipe.NetworkId] = footprints = StructureFootprint.OfNetwork(tr, pipe.NetworkId);
                ManholeAdjustment a = new ManholeAdjustment
                {
                    PipeId = id, Name = pipe.Name, OrigStart = pipe.StartPoint, OrigEnd = pipe.EndPoint, NewStart = pipe.StartPoint, NewEnd = pipe.EndPoint,
                    StartStructure = pipe.StartStructureId, EndStructure = pipe.EndStructureId
                };
                plan.Add(a);

                if (connectLooseEnds)
                {
                    if (a.StartStructure.IsNull && Nearest(footprints, a.OrigStart, a.EndStructure) is StructureFootprint s) { a.StartStructure = s.Id; a.ConnectStart = true; }
                    if (a.EndStructure.IsNull && Nearest(footprints, a.OrigEnd, a.StartStructure) is StructureFootprint e) { a.EndStructure = e.Id; a.ConnectEnd = true; }
                }
                if (a.StartStructure.IsNull && a.EndStructure.IsNull) { a.Note = "Not at a structure"; continue; }

                StructureFootprint startFp = FitOf(footprints.Find(f => f.Id == a.StartStructure)), endFp = FitOf(footprints.Find(f => f.Id == a.EndStructure));
                a.StartStructureName = startFp?.Name ?? "";
                a.EndStructureName = endFp?.Name ?? "";
                Point3d p1 = startFp != null ? startFp.Fit(a.OrigStart, a.OrigEnd, face, FitInset) : a.OrigStart;
                Point3d p2 = endFp != null ? endFp.Fit(a.OrigEnd, a.OrigStart, face, FitInset) : a.OrigEnd;
                KeepSlope(a.OrigStart, a.OrigEnd, ref p1, ref p2);
                if (p1.DistanceTo(p2) <= MinPipeLength) { a.Note = "Would be shorter than 5 cm"; continue; }
                a.NewStart = p1;
                a.NewEnd = p2;
                a.Actionable = true;
                // Within a few millimetres of the fitted point an end counts as in place (the inset alone is no reason to move it).
                bool moves = !p1.IsEqualTo(a.OrigStart, new Tolerance(0.003, 0.003)) || !p2.IsEqualTo(a.OrigEnd, new Tolerance(0.003, 0.003));
                a.Note = (a.ConnectStart || a.ConnectEnd) ? (moves ? "Connect loose end + fit" : "Connect loose end") : (moves ? "Fit" : "Already in place");
            }
            return plan;
        }

        /// <summary>
        /// Draws the outline ends are fitted to (the box given, or each structure's own face) as a closed polyline on a layer of its
        /// own, so it can be checked against the structures and erased afterwards. Returns how many were drawn.
        /// </summary>
        public static int DrawFitOutlines(Transaction tr, Database db, IEnumerable<ObjectId> structureIds, StructureFace face, double boxLength, double boxWidth, string layerName)
        {
            LayerTable layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId layerId;
            if (layers.Has(layerName)) layerId = layers[layerName];
            else
            {
                layers.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord { Name = layerName, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1), IsPlottable = false };
                layerId = layers.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            int drawn = 0;
            bool useBox = boxLength > 0 && boxWidth > 0;
            foreach (ObjectId id in structureIds.Distinct())
            {
                if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Structure st)) continue;
                StructureFootprint fp = useBox ? StructureFootprint.Box(st, boxLength, boxWidth) : StructureFootprint.Of(st);
                if (face == StructureFace.Center) continue;
                Polyline pl = fp.Outline(useBox ? StructureFace.InnerWall : face, st.Location.Z);
                pl.LayerId = layerId;
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                drawn++;
            }
            return drawn;
        }

        private static StructureFootprint Nearest(List<StructureFootprint> footprints, Point3d end, ObjectId notThis)
        {
            StructureFootprint best = null;
            double bestDist = double.MaxValue;
            foreach (StructureFootprint fp in footprints)
            {
                if (fp.Id == notThis || !fp.Contains(end)) continue;
                double d = fp.Center.GetDistanceTo(new Point2d(end.X, end.Y));
                if (d < bestDist) { bestDist = d; best = fp; }
            }
            return best;
        }

        /// <summary>
        /// Moves each chosen pipe's ends to their fitted points and keeps (or makes) their structure connections. Civil 3D only keeps
        /// an end connected while it lies inside the structure's own size; an end fitted beyond it (e.g. to a larger box) keeps its
        /// fitted point and is left unconnected, counted in <see cref="Counts.Unconnected"/> and named on the command line.
        /// </summary>
        public static Counts ApplyFit(Transaction tr, List<ManholeAdjustment> chosen, Editor ed)
        {
            Counts c = new Counts();
            Tolerance same = new Tolerance(1e-6, 1e-6);
            foreach (ManholeAdjustment a in chosen)
            {
                Pipe pipe = (Pipe)tr.GetObject(a.PipeId, OpenMode.ForWrite);
                // One pipe that cannot be adjusted is put back and reported; it must not undo every other pipe.
                try
                {
                    // A loose end is connected first (Civil 3D then pulls it to the structure's centre; the fitted point follows).
                    if (a.ConnectStart && TryConnect(pipe, ConnectorPositionType.Start, a.StartStructure)) c.Structures++;
                    if (a.ConnectEnd && TryConnect(pipe, ConnectorPositionType.End, a.EndStructure)) c.Structures++;
                    // Moving an end can drop its connection, and connecting it again pulls it back to the centre: two rounds of
                    // placing and reconnecting settle every end that Civil 3D accepts at its fitted point.
                    for (int round = 0; round < 2; round++)
                    {
                        if (!pipe.StartPoint.IsEqualTo(a.NewStart, same)) pipe.StartPoint = a.NewStart;
                        if (!pipe.EndPoint.IsEqualTo(a.NewEnd, same)) pipe.EndPoint = a.NewEnd;
                        // Civil 3D refuses to connect an end that is already connected, so only an end that lost its structure is reconnected.
                        bool lostStart = !a.StartStructure.IsNull && pipe.StartStructureId != a.StartStructure;
                        bool lostEnd = !a.EndStructure.IsNull && pipe.EndStructureId != a.EndStructure;
                        if (!lostStart && !lostEnd) break;
                        if (lostStart) TryConnect(pipe, ConnectorPositionType.Start, a.StartStructure);
                        if (lostEnd) TryConnect(pipe, ConnectorPositionType.End, a.EndStructure);
                    }
                    // The fitted points win over a connection Civil 3D will only keep elsewhere.
                    if (!pipe.StartPoint.IsEqualTo(a.NewStart, same)) pipe.StartPoint = a.NewStart;
                    if (!pipe.EndPoint.IsEqualTo(a.NewEnd, same)) pipe.EndPoint = a.NewEnd;
                    foreach ((ObjectId want, ObjectId now, string which, string structure) in new[]
                    {
                        (a.StartStructure, pipe.StartStructureId, "start", a.StartStructureName), (a.EndStructure, pipe.EndStructureId, "end", a.EndStructureName)
                    })
                    {
                        if (want.IsNull || now == want) continue;
                        c.Unconnected++;
                        ed.WriteMessage($"\n[Warning] Pipe <{a.Name}>: its {which} is fitted but left unconnected; it lies outside {structure}'s own size, where Civil 3D keeps no connection.");
                    }
                    c.Done++;
                }
                catch (Exception ex)
                {
                    try { pipe.StartPoint = a.OrigStart; pipe.EndPoint = a.OrigEnd; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    ed.WriteMessage($"\n[Error] Fitting pipe <{a.Name}>: {ex.Message}");
                    c.Failed++;
                }
            }
            return c;
        }

        /// <summary>
        /// Connects a pipe end to a structure. Civil 3D often reports "Fail to connect to the structure" for a connection it did
        /// make (typically after the end was moved), so only a connection that is really missing counts as a failure.
        /// </summary>
        internal static void ConnectEnd(Pipe pipe, ConnectorPositionType position, ObjectId structureId)
        {
            try { pipe.ConnectToStructure(position, structureId, false); }
            catch (Exception)
            {
                ObjectId now = position == ConnectorPositionType.Start ? pipe.StartStructureId : pipe.EndStructureId;
                if (now != structureId) throw;
            }
        }

        /// <summary>
        /// Connects a pipe end to a structure, keeps both pipe ends where they were, and throws when the end is not connected
        /// afterwards: inside <see cref="Atomic"/>, a connection that fails undoes the whole operation instead of leaving the
        /// pipe disconnected.
        /// </summary>
        internal static void ConnectOrThrow(Pipe pipe, ConnectorPositionType position, ObjectId structureId)
        {
            Point3d start = pipe.StartPoint, end = pipe.EndPoint;
            ConnectEnd(pipe, position, structureId);
            if (!pipe.StartPoint.IsEqualTo(start)) pipe.StartPoint = start;
            if (!pipe.EndPoint.IsEqualTo(end)) pipe.EndPoint = end;
            ObjectId now = position == ConnectorPositionType.Start ? pipe.StartStructureId : pipe.EndStructureId;
            if (now != structureId)
                throw new InvalidOperationException($"Pipe <{pipe.Name}> did not stay connected at its {(position == ConnectorPositionType.Start ? "start" : "end")}.");
        }

        private static bool TryConnect(Pipe pipe, ConnectorPositionType position, ObjectId structureId)
        {
            try { ConnectEnd(pipe, position, structureId); return true; }
            catch (Exception) { return false; }
        }

        // =====================================================================================
        // Pressure to gravity
        // =====================================================================================

        // A fitting centre counts as on the pipe's axis within this distance (m); a longer extension means it is not this pipe's fitting.
        private const double FittingAxisTolerance = 0.05, MaxFittingExtension = 5.0;

        public static Counts ApplyPressureToGravity(Transaction tr, CivilDocument doc, List<ObjectId> ids, string networkName, PartPick part, Editor ed)
        {
            Counts c = new Counts();
            Network target = null;
            ObjectId familyId = ObjectId.Null, sizeId = ObjectId.Null;
            foreach (ObjectId id in ids)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is PressurePipe pp)) continue;
                if (target == null)
                {
                    string name = string.IsNullOrWhiteSpace(networkName) ? "Gravity from pressure" : networkName.Trim();
                    ObjectId netId = Network.Create(doc, ref name);
                    target = (Network)tr.GetObject(netId, OpenMode.ForWrite);
                    target.PartsListId = part.PartsListId;
                    (familyId, sizeId) = c.PartFor(tr, netId, part);
                    if (sizeId.IsNull) { ed.WriteMessage($"\n[Error] {part.Label} could not be put in the parts list."); c.Failed = ids.Count; return c; }
                }
                try
                {
                    ObjectId newId = ObjectId.Null;
                    if (pp.IsCurve)
                    {
                        PipeCurveGeometry geom = null;
                        try { geom = pp.CurveGeometry; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        if (geom == null) geom = new PipeCurveGeometry(pp.StartPoint, pp.EndPoint, pp.Bulge);
                        try { newId = target.AddCurvePipe(familyId, sizeId, geom, false); }
                        finally { geom.Dispose(); }
                    }
                    else
                    {
                        // Close the gap a fitting leaves: each end runs on to the fitting's centre when that lies on the pipe's line.
                        Point3d start = ExtendToFitting(tr, pp.StartPartId, pp.StartPoint, pp.EndPoint);
                        Point3d end = ExtendToFitting(tr, pp.EndPartId, pp.EndPoint, pp.StartPoint);
                        using (LineSegment3d line = new LineSegment3d(start, end)) target.AddLinePipe(familyId, sizeId, line, ref newId, false);
                    }
                    if (newId.IsNull) throw new InvalidOperationException("Civil 3D did not return the new pipe.");
                    c.Created++;
                    c.NewIds.Add(newId);
                }
                catch (Exception ex) { ed.WriteMessage($"\n[Error] Converting pressure pipe <{pp.Name}>: {ex.Message}"); c.Failed++; }
            }
            return c;
        }

        private static Point3d ExtendToFitting(Transaction tr, ObjectId partId, Point3d end, Point3d otherEnd)
        {
            if (partId.IsNull) return end;
            DBObject obj = tr.GetObject(partId, OpenMode.ForRead);
            Point3d center;
            if (obj is PressureFitting f) center = f.Location;
            else if (obj is PressureAppurtenance a) center = a.Location;
            else return end;
            Vector3d axis = end - otherEnd;
            if (axis.Length < 1e-9) return end;
            Vector3d u = axis.GetNormal(), toCenter = center - end;
            double along = toCenter.DotProduct(u), across = (toCenter - u * along).Length;
            return along < -FittingAxisTolerance || along > MaxFittingExtension || across > FittingAxisTolerance ? end : center;
        }

        // =====================================================================================
        // Merge and swap
        // =====================================================================================

        // Pipes merge only when they meet within this distance and turn less than this.
        private const double MergeGap = 0.01, MergeAngle = 0.5 * Math.PI / 180.0;

        internal sealed class MergePlan
        {
            public List<ObjectId> Pipes = new List<ObjectId>();
            public string Names;
            public double Length;
        }

        /// <summary>
        /// Chains of selected straight pipes of the same family and size that meet end to end without a structure between
        /// them and run on in a straight line; each chain becomes one pipe.
        /// </summary>
        public static List<MergePlan> PlanMerge(Transaction tr, List<ObjectId> pipeIds)
        {
            List<Pipe> pipes = pipeIds.Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                .Where(p => p != null && p.SubEntityType == PipeSubEntityType.Straight).ToList();
            Dictionary<Pipe, Pipe> next = new Dictionary<Pipe, Pipe>(), previous = new Dictionary<Pipe, Pipe>();
            foreach (Pipe a in pipes)
                foreach (Pipe b in pipes)
                {
                    if (a == b || next.ContainsKey(a) || previous.ContainsKey(b)) continue;
                    if (a.NetworkId != b.NetworkId || a.PartFamilyId != b.PartFamilyId || a.PartSizeName != b.PartSizeName) continue;
                    if (!a.EndStructureId.IsNull || !b.StartStructureId.IsNull) continue;
                    if (new Point2d(a.EndPoint.X, a.EndPoint.Y).GetDistanceTo(new Point2d(b.StartPoint.X, b.StartPoint.Y)) > MergeGap) continue;
                    Vector2d da = new Vector2d(a.EndPoint.X - a.StartPoint.X, a.EndPoint.Y - a.StartPoint.Y), db = new Vector2d(b.EndPoint.X - b.StartPoint.X, b.EndPoint.Y - b.StartPoint.Y);
                    if (da.Length < 1e-9 || db.Length < 1e-9 || da.GetAngleTo(db) > MergeAngle) continue;
                    next[a] = b;
                    previous[b] = a;
                }
            List<MergePlan> plans = new List<MergePlan>();
            foreach (Pipe head in pipes.Where(p => next.ContainsKey(p) && !previous.ContainsKey(p)))
            {
                MergePlan plan = new MergePlan();
                List<string> names = new List<string>();
                for (Pipe p = head; p != null; p = next.TryGetValue(p, out Pipe n) ? n : null)
                {
                    plan.Pipes.Add(p.ObjectId);
                    names.Add(p.Name);
                    plan.Length += new Point2d(p.StartPoint.X, p.StartPoint.Y).GetDistanceTo(new Point2d(p.EndPoint.X, p.EndPoint.Y));
                }
                plan.Names = string.Join(" + ", names);
                plans.Add(plan);
            }
            return plans;
        }

        public static Counts ApplyMerge(Transaction tr, List<MergePlan> plans, Editor ed)
        {
            Counts c = new Counts();
            foreach (MergePlan plan in plans)
            {
                try
                {
                    // In its own transaction: a merge that fails puts the erased pipes back.
                    Atomic(tr, c, t =>
                    {
                        Pipe first = (Pipe)t.GetObject(plan.Pipes[0], OpenMode.ForWrite);
                        Pipe last = (Pipe)t.GetObject(plan.Pipes[plan.Pipes.Count - 1], OpenMode.ForWrite);
                        Point3d start = first.StartPoint, end = last.EndPoint;
                        ObjectId endStructure = last.EndStructureId;
                        if (!endStructure.IsNull) last.Disconnect(ConnectorPositionType.End);
                        for (int i = 1; i < plan.Pipes.Count; i++) { t.GetObject(plan.Pipes[i], OpenMode.ForWrite).Erase(); c.Skipped++; }
                        first.EndPoint = end;
                        if (!first.StartPoint.IsEqualTo(start)) first.StartPoint = start;
                        if (!endStructure.IsNull) ConnectOrThrow(first, ConnectorPositionType.End, endStructure);
                        c.Done++;
                    });
                }
                catch (Exception ex) { ed.WriteMessage($"\n[Error] Merging {plan.Names}: {ex.Message} The pipes are left as they were."); c.Failed++; }
            }
            return c;
        }

    }
}
