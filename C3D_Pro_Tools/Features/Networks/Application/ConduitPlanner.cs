using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using C3DProTools.Features.Networks.Contracts;

// The Generate Conduits planner: from plain drawing values to the per-encasement plan the preview shows and Apply makes.
// No Autodesk types, so every rule (ends, runs, footprints, wall trimming, mitres, offset arcs, fit) is tested offline.
namespace C3DProTools.Features.Networks.Application
{
    /// <summary>Plan geometry shared by the planner and the adapter (so Apply builds exactly what was planned).</summary>
    internal static class ConduitGeometry
    {
        /// <summary>
        /// The plan centre and radius of the arc from <paramref name="s"/> to <paramref name="e"/> with this bulge (tan of a
        /// quarter of the sweep; + counter-clockwise). The centre lies left of the chord for a positive bulge.
        /// </summary>
        public static (double X, double Y, double Radius) ArcCentre(Point3 s, Point3 e, double bulge)
        {
            double dx = e.X - s.X, dy = e.Y - s.Y, c = Math.Sqrt(dx * dx + dy * dy);
            double mx = (s.X + e.X) / 2, my = (s.Y + e.Y) / 2, lx = -dy / c, ly = dx / c;
            double off = c * (1 - bulge * bulge) / (4 * bulge);
            return (mx + lx * off, my + ly * off, c * (1 + bulge * bulge) / (4 * Math.Abs(bulge)));
        }

        /// <summary>True for a clockwise arc (negative bulge), as Civil 3D's curve geometry takes it.</summary>
        public static bool Clockwise(double bulge) => bulge < 0;

        /// <summary>The swept angle (radians, positive) of an arc with this bulge.</summary>
        public static double Sweep(double bulge) => 4 * Math.Atan(Math.Abs(bulge));
    }

    internal static class ConduitPlanner
    {
        /// <summary>An offset arc may not shrink below this radius (m); equals NetworkEditOps.MinArcRadius.</summary>
        public const double MinArcRadiusM = 0.05;
        private const double GridCell = 0.05;

        private sealed class Enc
        {
            public EncasementRead Read;
            public bool Curved;
            public int Run;
            public bool Reversed;
            public bool Visited;
            /// <summary>The paired neighbour at each end (index 0 = start, 1 = end), and which of its ends.</summary>
            public readonly Enc[] Neighbour = new Enc[2];
            public readonly PipeEnd[] NeighbourEnd = new PipeEnd[2];
            public readonly bool[] Unpaired = new bool[2];
            public Point3 Point(int end) => end == 0 ? Read.Snapshot.Start : Read.Snapshot.End;
            public DrawingObjectRef? Connected(int end) => end == 0 ? Read.Snapshot.StartStructure : Read.Snapshot.EndStructure;
            /// <summary>The parameter (0 or 1) of this encasement's end that is paired with <paramref name="other"/>.</summary>
            public double JointEndOf(Enc other) => Neighbour[0] == other ? 0 : 1;
        }

        /// <summary>Plans every encasement read. Returns the items (request order) and request-level issues.</summary>
        public static (List<ConduitSourcePlan> Items, List<OperationIssue> Issues) Plan(ConduitRequest request, ConduitDrawingRead read, CancellationToken cancel)
        {
            List<OperationIssue> issues = new List<OperationIssue>(read.Issues);
            ConduitEndRules rules = request.Ends ?? new ConduitEndRules();
            ConduitSection section = request.EffectiveSection;
            List<Enc> encs = read.Encasements.Where(e => e?.Snapshot != null).Select(e => new Enc { Read = e, Curved = e.Snapshot.Curved }).ToList();
            Dictionary<DrawingObjectRef, StructureRead> structures = read.Structures.GroupBy(s => s.Structure).ToDictionary(g => g.Key, g => g.First());

            PairJoints(encs);
            OrientRuns(encs);

            List<ConduitSourcePlan> items = new List<ConduitSourcePlan>();
            foreach (Enc enc in encs)
            {
                if (cancel.IsCancellationRequested) break;
                items.Add(PlanOne(enc, request, rules, section, structures));
            }
            return (items, issues);
        }

        // ---------- Joints and runs ----------

        /// <summary>
        /// Pairs encasement ends that meet with no structure: two ends of different encasements within the joint tolerance (plan
        /// and level), in any direction. An end matched by more than one other end is not paired (a tee): it keeps its end.
        /// Ends are indexed on a grid, so this is linear in the number of encasements.
        /// </summary>
        private static void PairJoints(List<Enc> encs)
        {
            double tol = ConduitLimits.JointToleranceM;
            Dictionary<(long, long), List<(Enc enc, int end)>> grid = new Dictionary<(long, long), List<(Enc, int)>>();
            (long, long) Cell(Point3 p) => ((long)Math.Floor(p.X / GridCell), (long)Math.Floor(p.Y / GridCell));
            List<(Enc enc, int end)> loose = new List<(Enc, int)>();
            foreach (Enc e in encs)
            {
                if (e.Read.Unsupported != null) continue;
                for (int end = 0; end < 2; end++)
                {
                    if (e.Connected(end).HasValue) continue;
                    loose.Add((e, end));
                    (long, long) k = Cell(e.Point(end));
                    if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<(Enc, int)>();
                    list.Add((e, end));
                }
            }
            List<(Enc enc, int end)> Matches(Enc e, int end)
            {
                Point3 p = e.Point(end);
                (long cx, long cy) = Cell(p);
                List<(Enc, int)> found = new List<(Enc, int)>();
                for (long i = cx - 1; i <= cx + 1; i++)
                    for (long j = cy - 1; j <= cy + 1; j++)
                        if (grid.TryGetValue((i, j), out var list))
                            foreach ((Enc o, int oe) in list)
                                if (o != e && o.Point(oe).PlanDistanceTo(p) <= tol && Math.Abs(o.Point(oe).Z - p.Z) <= tol) found.Add((o, oe));
                return found;
            }
            foreach ((Enc e, int end) in loose)
            {
                List<(Enc enc, int end)> m = Matches(e, end);
                if (m.Count == 0) continue;
                if (m.Count == 1 && Matches(m[0].enc, m[0].end).Count == 1)
                {
                    e.Neighbour[end] = m[0].enc;
                    e.NeighbourEnd[end] = m[0].end == 0 ? PipeEnd.Start : PipeEnd.End;
                }
                else e.Unpaired[end] = true;
            }
        }

        /// <summary>
        /// Runs are chains of paired encasements. Each takes the direction of its first chosen member; a member meeting its
        /// neighbour end-to-end or start-to-start runs the other way (<see cref="Enc.Reversed"/>), so column 1 stays on the
        /// run's left across every joint. Runs restart at structures (phase one).
        /// </summary>
        private static void OrientRuns(List<Enc> encs)
        {
            int run = 0;
            foreach (Enc first in encs)
            {
                if (first.Visited) continue;
                run++;
                first.Visited = true;
                first.Run = run;
                Queue<Enc> queue = new Queue<Enc>();
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    Enc e = queue.Dequeue();
                    for (int end = 0; end < 2; end++)
                    {
                        Enc n = e.Neighbour[end];
                        if (n == null || n.Visited) continue;
                        bool sameKind = (end == 0) == (e.NeighbourEnd[end] == PipeEnd.Start);
                        n.Reversed = e.Reversed ^ sameKind;
                        n.Run = run;
                        n.Visited = true;
                        queue.Enqueue(n);
                    }
                }
            }
        }

        // ---------- One encasement ----------

        private static ConduitSourcePlan PlanOne(Enc enc, ConduitRequest request, ConduitEndRules rules, ConduitSection section, Dictionary<DrawingObjectRef, StructureRead> structures)
        {
            EncasementRead r = enc.Read;
            EncasementSnapshot snap = r.Snapshot;
            List<OperationIssue> issues = new List<OperationIssue>();
            ConduitFitCheck fit = ConduitValidation.CheckSection(request.Template, section, snap.OuterWidthM, snap.OuterHeightM, r.Part?.OutsideDiameterM ?? 0);
            ConduitEndPlan[] ends = new ConduitEndPlan[2];
            Dictionary<int, StructureRead> endStructure = new Dictionary<int, StructureRead>();

            if (r.Unsupported != null)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementUnsupported, $"{r.Name}: {r.Unsupported}; it is left out.", null, r.Encasement));
            if (r.Part == null)
                issues.Add(r.PartIssue ?? OperationIssue.Error(ConduitIssueCodes.PartUnavailable, $"{r.Name}: the conduit part could not be found for its network; it is left out.", null, r.Encasement));
            else
            {
                issues.AddRange(ConduitValidation.FitIssues(fit, request.AllowConduitsOutsideEncasement, r.Name, r.Encasement));
                // A family-less part is made by copying a straight pipe; it cannot follow an arc.
                if (enc.Curved && r.Part.Template.HasValue && string.IsNullOrEmpty(r.Part.FamilyKey))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementUnsupported, $"{r.Name} is curved, and a conduit part with no family can only be copied straight; it is left out.", null, r.Encasement));
            }

            for (int end = 0; end < 2; end++)
            {
                PipeEnd which = end == 0 ? PipeEnd.Start : PipeEnd.End;
                ends[end] = ResolveEnd(enc, end, which, request, rules, structures, issues, out StructureRead s);
                if (s != null) endStructure[end] = s;
            }

            List<PlannedConduit> conduits = new List<PlannedConduit>();
            if (!issues.Any(i => i.Severity == IssueSeverity.Error))
                conduits = enc.Curved ? CurvedConduits(enc, request, section, issues) : StraightConduits(enc, ends, endStructure, request, section, issues);
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) conduits.Clear();

            return new ConduitSourcePlan(r.Encasement, r.Name, snap, enc.Run, enc.Reversed, ends[0], ends[1], fit, r.Destination, r.Part, conduits) { Issues = issues };
        }

        private static ConduitEndPlan ResolveEnd(Enc enc, int end, PipeEnd which, ConduitRequest request, ConduitEndRules rules,
            Dictionary<DrawingObjectRef, StructureRead> structures, List<OperationIssue> issues, out StructureRead used)
        {
            used = null;
            EncasementRead r = enc.Read;
            string endName = which == PipeEnd.Start ? "start" : "end";
            StructureMatch? foundBy = null;
            List<StructureSnapshot> candidates = null;

            if (enc.Connected(end) is DrawingObjectRef sid)
            {
                if (structures.TryGetValue(sid, out used)) foundBy = StructureMatch.Connected;
                else
                {
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.FootprintUnknown, $"{r.Name}: the structure at its {endName} could not be read; it is left out.", null, r.Encasement));
                    return new ConduitEndPlan(which, ConduitEndKind.Free);
                }
            }
            else if (enc.Neighbour[end] is Enc n)
            {
                JointTreatment joint = rules.AtJoint;
                if ((enc.Curved || n.Curved) && joint == JointTreatment.Mitre)
                {
                    joint = JointTreatment.EncasementEnd;
                    issues.Add(OperationIssue.Info(ConduitIssueCodes.CurvedJointNotMitred, $"{r.Name}: the joint at its {endName} has a curved encasement, so it is not mitred; both sides keep their ends.", null, r.Encasement));
                }
                return new ConduitEndPlan(which, ConduitEndKind.Joint, Neighbour: n.Read.Encasement, Joint: joint);
            }
            else
            {
                if (enc.Unpaired[end])
                    issues.Add(OperationIssue.Info(ConduitIssueCodes.JointNotPaired, $"{r.Name}: more than two chosen encasements meet at its {endName}; that joint is not mitred.", null, r.Encasement));
                if (rules.MatchUnconnectedEnds && r.Unsupported == null)
                {
                    Point3 p = enc.Point(end);
                    // Only structures whose footprint is known can be matched by position (an unknown one has no face to measure to).
                    List<(StructureRead read, StructureSnapshot snap)> near = structures.Values
                        .Select(s => (s, Footprint(s, rules)))
                        .Where(x => x.Item2.Source != FootprintSource.Unknown && DistanceToOuterFace(x.Item2, p) <= rules.StructureSearchM)
                        .ToList();
                    StructureChoice pick = rules.StructureChoices.FirstOrDefault(c => c != null && c.Encasement == r.Encasement && c.End == which);
                    if (near.Count == 1 && pick == null) { used = near[0].read; }
                    else if (near.Count > 0 && pick != null)
                    {
                        used = near.FirstOrDefault(x => x.read.Structure == pick.Structure).read;
                        if (used == null)
                        {
                            issues.Add(OperationIssue.Error(ConduitIssueCodes.StructureAmbiguous, $"{r.Name}: the structure picked for its {endName} is not one of those found there; pick again.", null, r.Encasement));
                            return new ConduitEndPlan(which, ConduitEndKind.Structure) { Candidates = near.Select(x => x.snap).ToList() };
                        }
                    }
                    else if (near.Count > 1)
                    {
                        candidates = near.Select(x => x.snap).ToList();
                        issues.Add(OperationIssue.Error(ConduitIssueCodes.StructureAmbiguous, $"{r.Name}: {near.Count} structures are near its {endName} ({string.Join(", ", near.Select(x => x.read.Name))}); pick the one it ends at.", null, r.Encasement));
                        return new ConduitEndPlan(which, ConduitEndKind.Structure) { Candidates = candidates };
                    }
                    if (used != null)
                    {
                        StructureSnapshot fs = Footprint(used, rules);
                        foundBy = DistanceToOuterFace(fs, p) <= 0 ? StructureMatch.InsideOuterFace : StructureMatch.WithinSearchDistance;
                        issues.Add(OperationIssue.Info(ConduitIssueCodes.StructureMatchedByPosition, $"{r.Name}: its {endName} is not connected; it is taken to end at {used.Name} ({(foundBy == StructureMatch.InsideOuterFace ? "inside it" : "near it")}).", null, r.Encasement));
                    }
                }
                if (used == null) return new ConduitEndPlan(which, ConduitEndKind.Free);
            }

            StructureSnapshot structure = Footprint(used, rules);
            (StructureEndTreatment treatment, bool connect, bool overridden) = ConduitValidation.ResolveStructureEnd(rules, r.Encasement, which);
            if (enc.Curved && (treatment != StructureEndTreatment.EncasementEnd || connect))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.CurvedEndUnsupported, $"{r.Name} is curved: at its {endName} ({used.Name}) conduits can only keep the encasement's end, without connection, in this version; set that end to keep its end, or leave it out.", null, r.Encasement));
            if (structure.Source == FootprintSource.Unknown && (treatment == StructureEndTreatment.InnerWall || treatment == StructureEndTreatment.OuterWall))
            {
                string size = string.IsNullOrWhiteSpace(used.PartSizeName) ? used.Name : $"\"{used.PartSizeName}\"";
                if (rules.WhenFootprintUnknown == UnknownFootprintPolicy.LeaveOut)
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.FootprintUnknown, $"{r.Name}: the inner size of {used.Name} at its {endName} is not known (its part reports none). Give the verified inner size for {size}; until then it is left out.", null, r.Encasement));
                else
                {
                    treatment = StructureEndTreatment.EncasementEnd;
                    issues.Add(OperationIssue.Warning(ConduitIssueCodes.FootprintUnknown, $"{r.Name}: the inner size of {used.Name} is not known, so its conduits keep the encasement's {endName} there.", null, r.Encasement));
                }
            }
            // A connection the user asked for is required: it is never dropped quietly. When it cannot be made, the encasement
            // is blocked until the user changes the destination (or part) or turns the connection off for that end.
            if (connect && (r.Destination == null || r.Destination.CreatedAtApply || r.Destination.Network != used.Network))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.ConnectionImpossible, $"{r.Name}: its conduits would be in network {r.Destination?.Name ?? "?"}, not {used.Name}'s, so the connection asked for at its {endName} cannot be made. Put the conduits in the encasement's network (a part with no family stays in the network it is copied from), or turn connection off for that end; it is left out until then.", null, r.Encasement));
            return new ConduitEndPlan(which, ConduitEndKind.Structure, structure, foundBy, treatment, connect, Overridden: overridden);
        }

        // ---------- Footprints ----------

        /// <summary>A structure's footprint: the user's verified box for its part, else the part's own inner size, else unknown (never the drawn extents).</summary>
        internal static StructureSnapshot Footprint(StructureRead s, ConduitEndRules rules)
        {
            StructureInnerBox box = ConduitValidation.InnerBoxFor(rules, s.PartFamilyName, s.PartSizeName);
            if (box != null)
                return new StructureSnapshot(s.Structure, s.Name, s.PartSizeName, s.Location, s.RotationRad, FootprintSource.UserBox, box.Round,
                    box.InnerWidthM, box.Round ? box.InnerWidthM : box.InnerLengthM, Math.Max(0, s.WallM));
            bool known = s.InnerWidthM > 0 && (s.Round || s.InnerLengthM > 0);
            return known
                ? new StructureSnapshot(s.Structure, s.Name, s.PartSizeName, s.Location, s.RotationRad, FootprintSource.PartSize, s.Round, s.InnerWidthM, s.Round ? s.InnerWidthM : s.InnerLengthM, Math.Max(0, s.WallM))
                : new StructureSnapshot(s.Structure, s.Name, s.PartSizeName, s.Location, s.RotationRad, FootprintSource.Unknown, s.Round, 0, 0, Math.Max(0, s.WallM));
        }

        private static (double x, double y) Local(StructureSnapshot s, double px, double py)
        {
            double dx = px - s.Location.X, dy = py - s.Location.Y, c = Math.Cos(-s.RotationRad), n = Math.Sin(-s.RotationRad);
            return (dx * c - dy * n, dx * n + dy * c);
        }

        /// <summary>Plan distance from a point to the structure's outer face; 0 inside. Uses the actual round or rotated shape.</summary>
        internal static double DistanceToOuterFace(StructureSnapshot s, Point3 p)
        {
            (double x, double y) = Local(s, p.X, p.Y);
            if (s.Round) return Math.Max(0, Math.Sqrt(x * x + y * y) - (s.InnerWidthM / 2 + s.WallM));
            double dx = Math.Max(0, Math.Abs(x) - (s.InnerWidthM / 2 + s.WallM)), dy = Math.Max(0, Math.Abs(y) - (s.InnerLengthM / 2 + s.WallM));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Where the line from <paramref name="from"/> towards <paramref name="to"/> (plan) first enters the structure's face, as
        /// a parameter along from→to; null when it misses the face or starts inside it. Inner face, or outer with the wall.
        /// </summary>
        internal static double? FaceEntry(StructureSnapshot s, bool outer, (double x, double y) from, (double x, double y) to)
        {
            double grow = outer ? s.WallM : 0;
            (double ax, double ay) = Local(s, from.x, from.y);
            (double bx, double by) = Local(s, to.x, to.y);
            double dx = bx - ax, dy = by - ay;
            if (dx * dx + dy * dy < 1e-18) return null;
            double t;
            if (s.Round)
            {
                double r = s.InnerWidthM / 2 + grow, A = dx * dx + dy * dy, B = 2 * (ax * dx + ay * dy), C = ax * ax + ay * ay - r * r;
                double disc = B * B - 4 * A * C;
                if (disc < 0) return null;
                t = (-B - Math.Sqrt(disc)) / (2 * A);
            }
            else
            {
                double hx = s.InnerWidthM / 2 + grow, hy = s.InnerLengthM / 2 + grow, enter = double.NegativeInfinity, exit = double.PositiveInfinity;
                foreach ((double p0, double dp, double h) in new[] { (ax, dx, hx), (ay, dy, hy) })
                {
                    if (Math.Abs(dp) < 1e-12) { if (Math.Abs(p0) > h) return null; continue; }
                    double t1 = (-h - p0) / dp, t2 = (h - p0) / dp;
                    enter = Math.Max(enter, Math.Min(t1, t2));
                    exit = Math.Min(exit, Math.Max(t1, t2));
                }
                if (enter > exit) return null;
                t = enter;
            }
            return t <= 1e-9 ? null : t;
        }

        // ---------- Conduits ----------

        private static List<PlannedConduit> StraightConduits(Enc enc, ConduitEndPlan[] ends, Dictionary<int, StructureRead> endStructure, ConduitRequest request,
            ConduitSection section, List<OperationIssue> issues)
        {
            EncasementSnapshot snap = enc.Read.Snapshot;
            Point3 S = snap.Start, E = snap.End;
            double dx = E.X - S.X, dy = E.Y - S.Y, L = Math.Sqrt(dx * dx + dy * dy);
            List<PlannedConduit> list = new List<PlannedConduit>();
            if (L < ConduitLimits.MinConduitLengthM)
            {
                issues.Add(OperationIssue.Error(ConduitIssueCodes.ConduitTooShort, $"{enc.Read.Name} has no length in plan; it is left out.", null, enc.Read.Encasement));
                return list;
            }
            double ux = dx / L, uy = dy / L, nx = uy, ny = -ux; // n = right of the drawing direction
            IReadOnlyList<SectionCentre> centres = ConduitValidation.SectionCentres(request.Template.Arrangement, section, snap.OuterHeightM);
            foreach (SectionCentre c in centres)
            {
                double h = enc.Reversed ? -c.AcrossM : c.AcrossM;
                (double x, double y) s0 = (S.X + nx * h, S.Y + ny * h), e0 = (E.X + nx * h, E.Y + ny * h);
                double Grade(double t) => S.Z + (E.Z - S.Z) * t + c.UpM;
                EndPoint start = MakeEnd(enc, 0, ends[0], s0, e0, L, ux, uy, c, section, request, Grade);
                EndPoint end = MakeEnd(enc, 1, ends[1], s0, e0, L, ux, uy, c, section, request, Grade);
                foreach (EndPoint p in new[] { start, end })
                    if (p.Problem != null)
                    {
                        issues.Add(p.Problem);
                        return new List<PlannedConduit>();
                    }
                if ((end.T - start.T) * L < ConduitLimits.MinConduitLengthM)
                {
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.ConduitTooShort, $"{enc.Read.Name}: conduit {c.Column}-{c.Row} would be shorter than {ConduitLimits.MinConduitLengthM} m after its ends are made; it is left out.", null, enc.Read.Encasement));
                    return new List<PlannedConduit>();
                }
                Point3 At(EndPoint p) => new Point3(s0.x + (e0.x - s0.x) * p.T, s0.y + (e0.y - s0.y) * p.T, p.Z);
                list.Add(new PlannedConduit(c.Column, c.Row, c.AcrossM, c.UpM, At(start), At(end), 0, Math.Round((end.T - start.T) * L, 9)));
            }
            return list;
        }

        /// <summary>Where one conduit's end is made: parameter along the encasement (0 = start, 1 = end), level, or the problem that blocks it.</summary>
        private readonly record struct EndPoint(double T, double Z, OperationIssue Problem = null);

        private static EndPoint MakeEnd(Enc enc, int end, ConduitEndPlan plan, (double x, double y) s0, (double x, double y) e0, double L, double ux, double uy,
            SectionCentre c, ConduitSection section, ConduitRequest request, Func<double, double> grade)
        {
            double t0 = end;
            string endName = end == 0 ? "start" : "end";
            switch (plan.Kind)
            {
                case ConduitEndKind.Joint when plan.Joint == JointTreatment.Mitre && enc.Neighbour[end] is Enc n:
                {
                    // The neighbour's line for the same column (its offset follows its own direction in the run).
                    EncasementSnapshot ns = n.Read.Snapshot;
                    double ndx = ns.End.X - ns.Start.X, ndy = ns.End.Y - ns.Start.Y, nl = Math.Sqrt(ndx * ndx + ndy * ndy);
                    if (nl < 1e-9) return new EndPoint(t0, grade(t0));
                    double nux = ndx / nl, nuy = ndy / nl, hn = n.Reversed ? -c.AcrossM : c.AcrossM;
                    double qx = ns.Start.X + nuy * hn, qy = ns.Start.Y - nux * hn;
                    double cross = ux * nuy - uy * nux;
                    double t = t0, tn;
                    if (Math.Abs(cross) < 1e-9)
                        tn = n.JointEndOf(enc); // collinear: the offset ends already meet
                    else
                    {
                        t = ((qx - s0.x) * nuy - (qy - s0.y) * nux) / cross / L;
                        double mx = s0.x + (e0.x - s0.x) * t, my = s0.y + (e0.y - s0.y) * t;
                        tn = ((mx - qx) * nux + (my - qy) * nuy) / nl;
                    }
                    // Both conduits end at one level: the mean of their own grades at the mitre. Each side computes the same
                    // mean from the same two values, so the ends meet in X, Y and Z. A row at another height on the other side
                    // (bottom-referenced in encasements of different heights), or grades too far apart, cannot meet: flagged.
                    IReadOnlyList<SectionCentre> other = ConduitValidation.SectionCentres(request.Template.Arrangement, section, ns.OuterHeightM);
                    double upN = other.First(o => o.Column == c.Column && o.Row == c.Row).UpM;
                    double own = grade(t), theirs = ns.Start.Z + (ns.End.Z - ns.Start.Z) * tn + upN;
                    double half = Math.Abs(own - theirs) / 2;
                    if (half > ConduitLimits.MitreLevelToleranceM)
                        return new EndPoint(t, own, OperationIssue.Error(ConduitIssueCodes.MitreLevelMismatch,
                            $"{enc.Read.Name}: at the mitre with {n.Read.Name} (its {endName}), conduit {c.Column}-{c.Row} is {Math.Round(Math.Abs(own - theirs) * 1000)} mm off its partner in level, more than the {Math.Round(2 * ConduitLimits.MitreLevelToleranceM * 1000)} mm a shared end can absorb. Check the two encasements' levels and placement there, or keep encasement ends at joints; it is left out.",
                            null, enc.Read.Encasement));
                    return new EndPoint(t, (own + theirs) / 2);
                }
                case ConduitEndKind.Structure when plan.Structure != null && plan.Treatment.HasValue:
                {
                    StructureSnapshot s = plan.Structure;
                    switch (plan.Treatment.Value)
                    {
                        case StructureEndTreatment.StructureCentre:
                        {
                            double t = ((s.Location.X - s0.x) * ux + (s.Location.Y - s0.y) * uy) / L;
                            return new EndPoint(t, grade(t));
                        }
                        case StructureEndTreatment.InnerWall:
                        case StructureEndTreatment.OuterWall:
                        {
                            bool outer = plan.Treatment == StructureEndTreatment.OuterWall;
                            // From the far end towards this one: the first face crossing is where this end stops (trim or extend).
                            (double x, double y) from = end == 0 ? e0 : s0, to = end == 0 ? s0 : e0;
                            double? f = FaceEntry(s, outer, from, to);
                            if (f == null)
                                return new EndPoint(t0, grade(t0), OperationIssue.Error(ConduitIssueCodes.WallMissed,
                                    $"{enc.Read.Name}: conduit {c.Column}-{c.Row} passes beside {s.Name} at its {endName} and never meets its {(outer ? "outer" : "inner")} wall. Change the layout or side shift, or set that end to keep the encasement's end; it is left out.",
                                    null, enc.Read.Encasement));
                            double t = end == 0 ? 1 - f.Value : f.Value;
                            return new EndPoint(t, grade(t));
                        }
                        default:
                            return new EndPoint(t0, grade(t0));
                    }
                }
                default:
                    return new EndPoint(t0, grade(t0));
            }
        }

        private static List<PlannedConduit> CurvedConduits(Enc enc, ConduitRequest request, ConduitSection section, List<OperationIssue> issues)
        {
            EncasementSnapshot snap = enc.Read.Snapshot;
            List<PlannedConduit> list = new List<PlannedConduit>();
            double b = snap.Bulge;
            if (!double.IsFinite(b) || b == 0)
            {
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementUnsupported, $"{enc.Read.Name}: its arc could not be read; it is left out.", null, enc.Read.Encasement));
                return list;
            }
            (double cx, double cy, double r) = ConduitGeometry.ArcCentre(snap.Start, snap.End, b);
            double sweep = ConduitGeometry.Sweep(b);
            foreach (SectionCentre c in ConduitValidation.SectionCentres(request.Template.Arrangement, section, snap.OuterHeightM))
            {
                double h = enc.Reversed ? -c.AcrossM : c.AcrossM;
                // Right of travel is outward on a counter-clockwise arc and inward on a clockwise one.
                double r2 = r + Math.Sign(b) * h;
                if (r2 < MinArcRadiusM)
                {
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.ConduitTooShort, $"{enc.Read.Name}: conduit {c.Column}-{c.Row} would shrink its arc below {MinArcRadiusM} m radius; it is left out.", null, enc.Read.Encasement));
                    return new List<PlannedConduit>();
                }
                double k = r2 / r;
                Point3 P(Point3 q) => new Point3(cx + (q.X - cx) * k, cy + (q.Y - cy) * k, q.Z + c.UpM);
                list.Add(new PlannedConduit(c.Column, c.Row, c.AcrossM, c.UpM, P(snap.Start), P(snap.End), b, Math.Round(r2 * sweep, 9)));
            }
            return list;
        }
    }
}
