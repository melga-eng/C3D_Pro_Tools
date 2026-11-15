using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace C3DProTools
{
    internal enum RunStartMode
    {
        /// <summary>Stationing starts at the first pipe the user picked, at the end nearest the pick.</summary>
        FirstPipePicked,
        /// <summary>Stationing starts at the end of the run with the higher invert.</summary>
        Upstream,
        /// <summary>Stationing follows the flow direction (start -> end) of most pipes in the run.</summary>
        MajorityFlow
    }

    internal enum RunVertexKind { RunEnd, Joint, ManholeCenter, PipeEnd, GapMidpoint }

    internal sealed class RunVertex
    {
        public Point3d Point;
        /// <summary>Set when the segment arriving at this vertex is an arc (a curved pipe): the arc's midpoint.</summary>
        public Point2d? ArcMid;
        public RunVertexKind Kind;
    }

    /// <summary>A selected gravity or pressure pipe, copied out of the database.</summary>
    internal sealed class RunPipe
    {
        public ObjectId Id;
        public string Name;
        public string NetworkName;
        public bool IsPressure;
        public Point3d Start, End;
        public ArcInfo? Arc;
        public double StartInvert, EndInvert;
        /// <summary>Structure / fitting / appurtenance the pipe end is attached to in Civil 3D, if any.</summary>
        public ObjectId StartHub, EndHub;
        public List<Point3d> Path;
    }

    /// <summary>A structure, fitting or appurtenance that pipe ends can meet at.</summary>
    internal sealed class RunHub
    {
        public ObjectId Id;
        public Point3d Center;
        public Point2d Min, Max;
    }

    internal struct RunStep
    {
        public int Pipe;
        /// <summary>True when the run travels the pipe from its start to its end.</summary>
        public bool Forward;
    }

    /// <summary>A junction the run reached where several selected pipes continue; the user picks one.</summary>
    internal sealed class RunBranch
    {
        public Point3d Point;
        public List<int> Candidates;
        public List<RunStep> RunSoFar;
    }

    internal sealed class PipeRun
    {
        public List<RunStep> Steps;
        public List<RunVertex> Vertices;
        public List<int> UnusedPipes;
    }

    /// <summary>
    /// Orders selected pipes into a single run by geometry (flow direction is ignored) and computes the alignment PIs:
    /// <list type="bullet">
    /// <item>Pipes meeting at a manhole / fitting (attached to it, or with the end physically inside it): one PI at
    /// its centre when both pipes' axes, extended past the wall, pass through the centre; otherwise a PI at each pipe end.</item>
    /// <item>Pipe ends meeting without a manhole: a PI at the shared point, or at the midpoint of a gap up to the limit.</item>
    /// </list>
    /// </summary>
    internal sealed partial class PipeRunBuilder
    {
        public const double CoincidentTolerance = 0.001;
        // A pipe end on a manhole wall still counts as inside it.
        private const double HubEdgeTolerance = 0.01;
        // Hubs farther than this from every selected pipe are not read at all.
        private const double HubSearchMargin = 20.0;
        // A PI on a straight run is dropped when it lies this close to the line through its neighbours.
        private const double CollinearTolerance = 0.005;

        private readonly double _gapLimit;
        private readonly double _centerTolerance;
        private readonly List<RunPipe> _pipes = new List<RunPipe>();
        private readonly Dictionary<ObjectId, RunHub> _hubs = new Dictionary<ObjectId, RunHub>();
        private readonly List<ObjectId> _selectedHubIds = new List<ObjectId>();
        private int[] _endNode;
        private readonly List<Node> _nodes = new List<Node>();

        private sealed class Node
        {
            public RunHub Hub;
            public readonly List<int> Ends = new List<int>();
            public Point3d Point;
        }

        public IReadOnlyList<RunPipe> Pipes => _pipes;
        /// <summary>Structures, fittings and appurtenances that were in the selection (drawn in the profile view too).</summary>
        public IReadOnlyList<ObjectId> SelectedHubIds => _selectedHubIds;

        private PipeRunBuilder(double gapLimit, double centerTolerance)
        {
            _gapLimit = gapLimit;
            _centerTolerance = centerTolerance;
        }

        // Pipe ends are numbered 2 * pipe (start) and 2 * pipe + 1 (end).
        private static int EndKey(int pipe, bool atStart) => 2 * pipe + (atStart ? 0 : 1);
        private static int PipeOf(int end) => end / 2;
        private static bool IsStart(int end) => end % 2 == 0;
        private Point3d EndPoint(int end) => IsStart(end) ? _pipes[PipeOf(end)].Start : _pipes[PipeOf(end)].End;

        public int IndexOf(ObjectId pipeId) => _pipes.FindIndex(p => p.Id == pipeId);

        public static PipeRunBuilder Read(Transaction tr, IEnumerable<ObjectId> selection, double gapLimit, double centerTolerance)
        {
            PipeRunBuilder b = new PipeRunBuilder(gapLimit, centerTolerance);
            HashSet<ObjectId> gravityNets = new HashSet<ObjectId>();
            HashSet<ObjectId> pressureNets = new HashSet<ObjectId>();
            HashSet<ObjectId> seen = new HashSet<ObjectId>();

            foreach (ObjectId id in selection)
            {
                if (!seen.Add(id)) continue;
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is Pipe pipe)
                {
                    ArcInfo? arc = null;
                    if (pipe.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(pipe, out ArcInfo a)) arc = a;
                    double half = pipe.InnerHeight / 2.0;
                    b._pipes.Add(new RunPipe
                    {
                        Id = id, Name = pipe.Name, NetworkName = pipe.NetworkName, Start = pipe.StartPoint, End = pipe.EndPoint, Arc = arc,
                        StartInvert = pipe.StartPoint.Z - half, EndInvert = pipe.EndPoint.Z - half,
                        StartHub = pipe.StartStructureId, EndHub = pipe.EndStructureId,
                        Path = PipeArcGeometry.SamplePath(pipe.StartPoint, pipe.EndPoint, arc, 2.0, 60)
                    });
                    gravityNets.Add(pipe.NetworkId);
                }
                else if (obj is PressurePipe pp)
                {
                    ArcInfo? arc = null;
                    if (pp.IsCurve && PipeArcGeometry.TryGetPressureArc(pp, out ArcInfo a)) arc = a;
                    double half = pp.InnerDiameter / 2.0;
                    b._pipes.Add(new RunPipe
                    {
                        Id = id, Name = pp.Name, NetworkName = pp.NetworkName, IsPressure = true, Start = pp.StartPoint, End = pp.EndPoint, Arc = arc,
                        StartInvert = pp.StartPoint.Z - half, EndInvert = pp.EndPoint.Z - half,
                        StartHub = PressureHubId(tr, pp.StartPartId), EndHub = PressureHubId(tr, pp.EndPartId),
                        Path = PipeArcGeometry.SamplePath(pp.StartPoint, pp.EndPoint, arc, 2.0, 60)
                    });
                    pressureNets.Add(pp.NetworkId);
                }
                else if (obj is Structure || obj is PressureFitting || obj is PressureAppurtenance)
                {
                    b._selectedHubIds.Add(id);
                }
            }

            // Every structure / fitting near the selection can be a meeting point, selected or not, attached or not.
            Extents3d? reach = b.SelectionExtents();
            foreach (ObjectId netId in gravityNets)
            {
                if (tr.GetObject(netId, OpenMode.ForRead) is Network net)
                    foreach (ObjectId stId in net.GetStructureIds().Live()) b.ReadHub(tr, stId, reach);
            }
            foreach (ObjectId netId in pressureNets)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                foreach (ObjectId fId in net.GetFittingIds()) b.ReadHub(tr, fId, reach);
                foreach (ObjectId aId in net.GetAppurtenanceIds()) b.ReadHub(tr, aId, reach);
            }
            foreach (RunPipe p in b._pipes)
            {
                // Attached hubs always count, even if they fell outside the search area.
                if (!p.StartHub.IsNull) b.ReadHub(tr, p.StartHub, null);
                if (!p.EndHub.IsNull) b.ReadHub(tr, p.EndHub, null);
            }

            b.BuildNodes();
            return b;
        }

        private static ObjectId PressureHubId(Transaction tr, ObjectId partId)
        {
            if (partId.IsNull) return ObjectId.Null;
            DBObject obj = tr.GetObject(partId, OpenMode.ForRead);
            return obj is PressureFitting || obj is PressureAppurtenance ? partId : ObjectId.Null;
        }

        private Extents3d? SelectionExtents()
        {
            Extents3d? ext = null;
            foreach (RunPipe p in _pipes)
            {
                foreach (Point3d pt in p.Path)
                {
                    Extents3d e = ext ?? new Extents3d(pt, pt);
                    e.AddPoint(pt);
                    ext = e;
                }
            }
            return ext;
        }

        private void ReadHub(Transaction tr, ObjectId id, Extents3d? reach)
        {
            if (_hubs.ContainsKey(id)) return;
            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
            Point3d center;
            if (obj is Structure st) center = st.Location;
            else if (obj is PressureFitting f) center = f.Location;
            else if (obj is PressureAppurtenance a) center = a.Location;
            else return;

            if (reach.HasValue && (center.X < reach.Value.MinPoint.X - HubSearchMargin || center.X > reach.Value.MaxPoint.X + HubSearchMargin ||
                                   center.Y < reach.Value.MinPoint.Y - HubSearchMargin || center.Y > reach.Value.MaxPoint.Y + HubSearchMargin))
                return;

            RunHub hub = new RunHub { Id = id, Center = center, Min = new Point2d(center.X, center.Y), Max = new Point2d(center.X, center.Y) };
            try
            {
                Extents3d ext = ((Entity)obj).GeometricExtents;
                hub.Min = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                hub.Max = new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            _hubs[id] = hub;
        }

        /// <summary>The hub a pipe end meets: the one it is attached to, else the nearest one whose plan footprint holds the end.</summary>
        private RunHub HubOf(int end)
        {
            RunPipe pipe = _pipes[PipeOf(end)];
            ObjectId attached = IsStart(end) ? pipe.StartHub : pipe.EndHub;
            if (!attached.IsNull && _hubs.TryGetValue(attached, out RunHub hub)) return hub;

            Point3d p = EndPoint(end);
            RunHub best = null;
            double bestDist = double.MaxValue;
            foreach (RunHub h in _hubs.Values)
            {
                if (p.X < h.Min.X - HubEdgeTolerance || p.X > h.Max.X + HubEdgeTolerance ||
                    p.Y < h.Min.Y - HubEdgeTolerance || p.Y > h.Max.Y + HubEdgeTolerance) continue;
                double d = PlanDistance(p, h.Center);
                if (d < bestDist) { bestDist = d; best = h; }
            }
            return best;
        }

        private void BuildNodes()
        {
            int endCount = 2 * _pipes.Count;
            RunHub[] hubOfEnd = new RunHub[endCount];
            int[] parent = new int[endCount];
            for (int i = 0; i < endCount; i++) { parent[i] = i; hubOfEnd[i] = HubOf(i); }

            for (int i = 0; i < endCount; i++)
            {
                for (int j = i + 1; j < endCount; j++)
                {
                    if (PipeOf(i) == PipeOf(j)) continue;
                    if (hubOfEnd[i] != null || hubOfEnd[j] != null)
                    {
                        // Ends at a manhole join only through it; the gap rule is for pipes without one.
                        if (hubOfEnd[i] == hubOfEnd[j]) Union(parent, i, j);
                    }
                    else if (PlanDistance(EndPoint(i), EndPoint(j)) <= _gapLimit)
                    {
                        Union(parent, i, j);
                    }
                }
            }

            Dictionary<int, Node> byRoot = new Dictionary<int, Node>();
            _endNode = new int[endCount];
            for (int i = 0; i < endCount; i++)
            {
                int root = Find(parent, i);
                if (!byRoot.TryGetValue(root, out Node node))
                {
                    node = new Node { Hub = hubOfEnd[i] };
                    byRoot[root] = node;
                    _nodes.Add(node);
                }
                node.Ends.Add(i);
                _endNode[i] = _nodes.IndexOf(node);
            }
            foreach (Node node in _nodes)
            {
                if (node.Hub != null) { node.Point = node.Hub.Center; continue; }
                double x = 0, y = 0, z = 0;
                foreach (int e in node.Ends) { Point3d p = EndPoint(e); x += p.X; y += p.Y; z += p.Z; }
                node.Point = new Point3d(x / node.Ends.Count, y / node.Ends.Count, z / node.Ends.Count);
            }
        }

        private int OtherPipesAt(int end)
        {
            int count = 0;
            foreach (int e in _nodes[_endNode[end]].Ends) if (PipeOf(e) != PipeOf(end)) count++;
            return count;
        }

        /// <summary>
        /// Start for <see cref="RunStartMode.FirstPipePicked"/>: the picked pipe's free end when exactly one end is free
        /// (the run continues from the other), otherwise the end nearest the pick point.
        /// </summary>
        public RunStep StartFromPick(int pipe, Point3d pick)
        {
            bool startFree = OtherPipesAt(EndKey(pipe, true)) == 0;
            bool endFree = OtherPipesAt(EndKey(pipe, false)) == 0;
            bool atStart = startFree != endFree
                ? startFree
                : PlanDistance(pick, _pipes[pipe].Start) <= PlanDistance(pick, _pipes[pipe].End);
            return new RunStep { Pipe = pipe, Forward = atStart };
        }

        /// <summary>Start for the other modes: the first selected pipe with a free end (or the first pipe, if all are joined in a loop).</summary>
        public RunStep LeafStart()
        {
            for (int p = 0; p < _pipes.Count; p++)
            {
                if (OtherPipesAt(EndKey(p, true)) == 0) return new RunStep { Pipe = p, Forward = true };
                if (OtherPipesAt(EndKey(p, false)) == 0) return new RunStep { Pipe = p, Forward = false };
            }
            return new RunStep { Pipe = 0, Forward = true };
        }

        /// <summary>
        /// Walks from <paramref name="start"/> through the joints. At a branch, <paramref name="chooseBranch"/> returns the
        /// candidate pipe to continue along, -1 to end the run there, or -2 to cancel (the walk then returns null).
        /// </summary>
        public List<RunStep> Walk(RunStep start, Func<RunBranch, int> chooseBranch)
        {
            bool[] used = new bool[_pipes.Count];
            List<RunStep> steps = new List<RunStep>();
            RunStep step = start;

            while (true)
            {
                used[step.Pipe] = true;
                steps.Add(step);

                int exitEnd = EndKey(step.Pipe, !step.Forward);
                Node node = _nodes[_endNode[exitEnd]];
                List<int> candidateEnds = new List<int>();
                foreach (int e in node.Ends)
                {
                    if (!used[PipeOf(e)] && !candidateEnds.Exists(c => PipeOf(c) == PipeOf(e))) candidateEnds.Add(e);
                }
                if (candidateEnds.Count == 0) break;

                int chosenEnd = candidateEnds[0];
                if (candidateEnds.Count > 1)
                {
                    List<int> candidatePipes = candidateEnds.ConvertAll(PipeOf);
                    int choice = chooseBranch(new RunBranch { Point = node.Point, Candidates = candidatePipes, RunSoFar = steps });
                    if (choice == -2) return null;
                    if (choice < 0) break;
                    chosenEnd = candidateEnds[candidatePipes.IndexOf(choice)];
                }
                // Entering a pipe at its start end means travelling it forward.
                step = new RunStep { Pipe = PipeOf(chosenEnd), Forward = IsStart(chosenEnd) };
            }
            return steps;
        }

        /// <summary>
        /// The run with the fewest pipes from <paramref name="startPipe"/> to <paramref name="endPipe"/> through the
        /// joints, or null when they are not connected. The start pipe's direction is whichever reaches the end pipe.
        /// </summary>
        public List<RunStep> FindPath(int startPipe, int endPipe)
        {
            if (startPipe == endPipe) return new List<RunStep> { new RunStep { Pipe = startPipe, Forward = true } };

            // A state is a pipe travelled in one direction: 2 * pipe + (forward ? 1 : 0).
            int[] previous = new int[2 * _pipes.Count];
            for (int i = 0; i < previous.Length; i++) previous[i] = -2;
            Queue<int> queue = new Queue<int>();
            foreach (bool forward in new[] { true, false })
            {
                int s = 2 * startPipe + (forward ? 1 : 0);
                previous[s] = -1;
                queue.Enqueue(s);
            }

            while (queue.Count > 0)
            {
                int state = queue.Dequeue();
                int pipe = state / 2;
                bool forward = state % 2 == 1;
                foreach (int e in _nodes[_endNode[EndKey(pipe, !forward)]].Ends)
                {
                    int next = PipeOf(e);
                    if (next == pipe) continue;
                    int nextState = 2 * next + (IsStart(e) ? 1 : 0);
                    if (previous[nextState] != -2) continue;
                    previous[nextState] = state;
                    if (next == endPipe)
                    {
                        List<RunStep> path = new List<RunStep>();
                        for (int s = nextState; s != -1; s = previous[s]) path.Add(new RunStep { Pipe = s / 2, Forward = s % 2 == 1 });
                        path.Reverse();
                        return path;
                    }
                    queue.Enqueue(nextState);
                }
            }
            return null;
        }

        /// <summary>The structure / fitting the run passes through at a step's entry (or exit) end, or null for a plain pipe joint.</summary>
        public RunHub HubAt(RunStep s, bool atEntry) => _nodes[_endNode[EndKey(s.Pipe, atEntry ? s.Forward : !s.Forward)]].Hub;

        public double EntryInvert(RunStep s) => s.Forward ? _pipes[s.Pipe].StartInvert : _pipes[s.Pipe].EndInvert;
        public double ExitInvert(RunStep s) => s.Forward ? _pipes[s.Pipe].EndInvert : _pipes[s.Pipe].StartInvert;

        public static List<RunStep> Reverse(List<RunStep> steps)
        {
            List<RunStep> reversed = new List<RunStep>(steps.Count);
            for (int i = steps.Count - 1; i >= 0; i--) reversed.Add(new RunStep { Pipe = steps[i].Pipe, Forward = !steps[i].Forward });
            return reversed;
        }

        /// <summary>Gravity pipes of the run grouped by whether they point along it, for the flow-direction check.</summary>
        public FlowRun FlowRunFor(List<RunStep> steps)
        {
            FlowRun run = new FlowRun();
            foreach (RunStep s in steps)
            {
                RunPipe p = _pipes[s.Pipe];
                if (p.IsPressure) continue;
                (s.Forward ? run.WithSeed : run.AgainstSeed).Add(p.Id);
                run.SeedDrop += EntryInvert(s) - ExitInvert(s);
            }
            return run;
        }

        public PipeRun BuildRun(List<RunStep> steps)
        {
            List<RunVertex> v = new List<RunVertex>();
            void Add(Point3d p, RunVertexKind kind, Point2d? arcMid = null)
            {
                if (v.Count > 0 && PlanDistance(v[v.Count - 1].Point, p) <= CoincidentTolerance) return;
                v.Add(new RunVertex { Point = p, Kind = kind, ArcMid = arcMid });
            }

            for (int i = 0; i < steps.Count; i++)
            {
                RunStep s = steps[i];
                RunPipe pipe = _pipes[s.Pipe];
                int entry = EndKey(s.Pipe, s.Forward), exit = EndKey(s.Pipe, !s.Forward);
                Point3d entryPt = EndPoint(entry), exitPt = EndPoint(exit);

                if (i == 0)
                {
                    RunHub hub = _nodes[_endNode[entry]].Hub;
                    if (hub != null && AxisThroughCenter(entry, hub.Center)) Add(hub.Center, RunVertexKind.ManholeCenter);
                    else Add(entryPt, RunVertexKind.RunEnd);
                }

                if (pipe.Arc.HasValue)
                {
                    // An arc segment must start and end on the pipe itself.
                    Add(entryPt, RunVertexKind.PipeEnd);
                    Add(exitPt, RunVertexKind.PipeEnd, pipe.Arc.Value.Mid);
                }

                Node node = _nodes[_endNode[exit]];
                if (i == steps.Count - 1)
                {
                    if (node.Hub != null && AxisThroughCenter(exit, node.Hub.Center)) Add(node.Hub.Center, RunVertexKind.ManholeCenter);
                    else Add(exitPt, RunVertexKind.RunEnd);
                    continue;
                }

                RunStep next = steps[i + 1];
                int nextEntry = EndKey(next.Pipe, next.Forward);
                Point3d nextPt = EndPoint(nextEntry);
                if (node.Hub != null)
                {
                    if (AxisThroughCenter(exit, node.Hub.Center) && AxisThroughCenter(nextEntry, node.Hub.Center))
                    {
                        Add(node.Hub.Center, RunVertexKind.ManholeCenter);
                    }
                    else
                    {
                        Add(exitPt, RunVertexKind.PipeEnd);
                        Add(nextPt, RunVertexKind.PipeEnd);
                    }
                }
                else if (PlanDistance(exitPt, nextPt) <= CoincidentTolerance)
                {
                    Add(exitPt, RunVertexKind.Joint);
                }
                else
                {
                    Add(new Point3d((exitPt.X + nextPt.X) / 2.0, (exitPt.Y + nextPt.Y) / 2.0, (exitPt.Z + nextPt.Z) / 2.0), RunVertexKind.GapMidpoint);
                }
            }

            RemoveCollinear(v);

            HashSet<int> usedPipes = new HashSet<int>();
            foreach (RunStep s in steps) usedPipes.Add(s.Pipe);
            List<int> unused = new List<int>();
            for (int p = 0; p < _pipes.Count; p++) if (!usedPipes.Contains(p)) unused.Add(p);

            return new PipeRun { Steps = steps, Vertices = v, UnusedPipes = unused };
        }

        /// <summary>
        /// True when the pipe's axis at this end, extended outward past the end (e.g. from a manhole wall), passes within
        /// the centre tolerance of <paramref name="center"/>. Curved pipes use their tangent at the end.
        /// </summary>
        private bool AxisThroughCenter(int end, Point3d center)
        {
            Point3d e = EndPoint(end);
            Vector2d toCenter = new Vector2d(center.X - e.X, center.Y - e.Y);
            if (toCenter.Length <= _centerTolerance) return true;

            Vector2d outward = Outward(end);
            if (outward.Length < 1e-9) return false;

            double along = outward.DotProduct(toCenter);
            double across = Math.Abs(outward.X * toCenter.Y - outward.Y * toCenter.X);
            return along >= -_centerTolerance && across <= _centerTolerance;
        }

        private static void RemoveCollinear(List<RunVertex> v)
        {
            int i = 1;
            while (i < v.Count - 1)
            {
                if (v[i].ArcMid == null && v[i + 1].ArcMid == null && IsBetweenOnLine(v[i - 1].Point, v[i].Point, v[i + 1].Point))
                    v.RemoveAt(i);
                else
                    i++;
            }
        }

        private static bool IsBetweenOnLine(Point3d a, Point3d p, Point3d b)
        {
            Vector2d ab = new Vector2d(b.X - a.X, b.Y - a.Y);
            Vector2d ap = new Vector2d(p.X - a.X, p.Y - a.Y);
            double len = ab.Length;
            if (len < 1e-9) return false;
            double along = ab.DotProduct(ap) / len;
            double across = Math.Abs(ab.X * ap.Y - ab.Y * ap.X) / len;
            return across <= CollinearTolerance && along > 0 && along < len;
        }

        /// <summary>A 2D polyline through the run's PIs (arcs as bulges), in stationing order.</summary>
        public static Polyline ToPolyline(PipeRun run)
        {
            Polyline pl = new Polyline();
            for (int i = 0; i < run.Vertices.Count; i++)
                pl.AddVertexAt(i, new Point2d(run.Vertices[i].Point.X, run.Vertices[i].Point.Y), 0, 0, 0);
            for (int i = 1; i < run.Vertices.Count; i++)
            {
                if (!run.Vertices[i].ArcMid.HasValue) continue;
                Point3d s = run.Vertices[i - 1].Point, e = run.Vertices[i].Point;
                Point2d m = run.Vertices[i].ArcMid.Value;
                // Bulge = 2 * sagitta / chord, positive for counter-clockwise arcs (which bulge to the right of the chord).
                double cx = e.X - s.X, cy = e.Y - s.Y;
                double cross = cx * (m.Y - s.Y) - cy * (m.X - s.X);
                double chordSq = cx * cx + cy * cy;
                if (chordSq > 1e-12) pl.SetBulgeAt(i - 1, -2.0 * cross / chordSq);
            }
            return pl;
        }

        private static double PlanDistance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        private static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);
    }
}
