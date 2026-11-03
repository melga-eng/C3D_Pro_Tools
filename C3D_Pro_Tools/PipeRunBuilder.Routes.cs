using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    /// <summary>
    /// Route finding over the read pipes: which pipe continues which at every joint, so parallel pipes running side by side
    /// through the same manholes (cable ducts) stay separate routes instead of being mixed.
    /// </summary>
    internal sealed partial class PipeRunBuilder
    {
        // At a joint with more than two pipes, two ends only continue each other when the route turns by less than this.
        private const double MaxContinuationTurn = 100.0 * Math.PI / 180.0;
        // Two pipes meeting alone at a joint continue each other unless they double back (parallel pipes ending in one manhole).
        private const double MaxTurn = 160.0 * Math.PI / 180.0;
        // A sideways shift of this much between two ends costs as much as a one-radian turn (keeps parallel ducts apart).
        private const double LateralScale = 0.3;
        // Two pipes that both flow into (or both out of) a joint are a poor continuation on a gravity network.
        private const double FlowPenalty = 1.5;
        // Path search: every pipe costs one, plus this times how badly it continues the previous one.
        private const double TurnWeight = 3.0;

        /// <summary>Plan direction pointing out of the pipe at this end (the tangent for curved pipes), unit length or zero.</summary>
        private Vector2d Outward(int end)
        {
            RunPipe pipe = _pipes[PipeOf(end)];
            Point3d e = EndPoint(end);
            Vector2d outward;
            if (pipe.Arc.HasValue)
            {
                Point2d c = pipe.Arc.Value.Center;
                Vector2d radial = new Vector2d(e.X - c.X, e.Y - c.Y);
                outward = new Vector2d(-radial.Y, radial.X);
                // Of the two tangent directions, outward is the one pointing away from the arc's middle.
                Vector2d fromMid = new Vector2d(e.X - pipe.Arc.Value.Mid.X, e.Y - pipe.Arc.Value.Mid.Y);
                if (outward.DotProduct(fromMid) < 0) outward = outward.Negate();
            }
            else
            {
                Point3d other = IsStart(end) ? pipe.End : pipe.Start;
                outward = new Vector2d(e.X - other.X, e.Y - other.Y);
            }
            return outward.Length < 1e-9 ? new Vector2d(0, 0) : outward.GetNormal();
        }

        /// <summary>The turn (radians, 0 = straight on) from arriving through end <paramref name="a"/> to leaving through end <paramref name="b"/>.</summary>
        private double Turn(int a, int b)
        {
            Vector2d da = Outward(a), db = Outward(b);
            if (da.Length < 1e-9 || db.Length < 1e-9) return Math.PI / 2;
            return da.GetAngleTo(db.Negate());
        }

        /// <summary>How badly end <paramref name="b"/> continues end <paramref name="a"/>: the turn, the sideways shift and, on gravity networks, the flow.</summary>
        private double ContinuationCost(int a, int b, bool respectFlow)
        {
            double cost = Turn(a, b);
            Point3d pa = EndPoint(a), pb = EndPoint(b);
            Vector2d gap = new Vector2d(pb.X - pa.X, pb.Y - pa.Y);
            Vector2d da = Outward(a), db = Outward(b);
            double lateral = (Math.Abs(da.X * gap.Y - da.Y * gap.X) + Math.Abs(db.X * gap.Y - db.Y * gap.X)) / 2.0;
            cost += lateral / LateralScale;
            // A pipe's end is where it flows in; its start is where it flows out.
            if (respectFlow && IsStart(a) == IsStart(b)) cost += FlowPenalty;
            return cost;
        }

        /// <summary>The joint (node index) a step passes at its entry or exit end.</summary>
        public int NodeAt(RunStep s, bool atEntry) => _endNode[EndKey(s.Pipe, atEntry ? s.Forward : !s.Forward)];

        public Point3d NodePoint(int node) => _nodes[node].Point;

        /// <summary>
        /// Splits the read pipes into routes: at every joint each pipe end is paired with the end that continues it best
        /// (straightest, least sideways shift, and on gravity networks one flowing in with one flowing out). Ends left
        /// unpaired, such as a branch joining a main line, end their route there.
        /// </summary>
        public List<List<RunStep>> Decompose(bool respectFlow)
        {
            int endCount = 2 * _pipes.Count;
            int[] partner = new int[endCount];
            for (int i = 0; i < endCount; i++) partner[i] = -1;

            foreach (Node node in _nodes)
            {
                List<int> ends = node.Ends;
                if (ends.Count < 2) continue;
                if (ends.Count == 2)
                {
                    if (PipeOf(ends[0]) != PipeOf(ends[1]) && Turn(ends[0], ends[1]) <= MaxTurn) { partner[ends[0]] = ends[1]; partner[ends[1]] = ends[0]; }
                    continue;
                }
                List<(double cost, int a, int b)> pairs = new List<(double, int, int)>();
                for (int i = 0; i < ends.Count; i++)
                    for (int j = i + 1; j < ends.Count; j++)
                    {
                        if (PipeOf(ends[i]) == PipeOf(ends[j]) || Turn(ends[i], ends[j]) > MaxContinuationTurn) continue;
                        pairs.Add((ContinuationCost(ends[i], ends[j], respectFlow), ends[i], ends[j]));
                    }
                pairs.Sort((x, y) => x.cost.CompareTo(y.cost));
                foreach ((double _, int a, int b) in pairs)
                {
                    if (partner[a] >= 0 || partner[b] >= 0) continue;
                    partner[a] = b;
                    partner[b] = a;
                }
            }

            bool[] visited = new bool[_pipes.Count];
            List<List<RunStep>> routes = new List<List<RunStep>>();
            List<RunStep> Walk(int firstEnd)
            {
                List<RunStep> steps = new List<RunStep>();
                RunStep step = new RunStep { Pipe = PipeOf(firstEnd), Forward = IsStart(firstEnd) };
                while (!visited[step.Pipe])
                {
                    visited[step.Pipe] = true;
                    steps.Add(step);
                    int next = partner[EndKey(step.Pipe, !step.Forward)];
                    if (next < 0) break;
                    step = new RunStep { Pipe = PipeOf(next), Forward = IsStart(next) };
                }
                return steps;
            }

            // Routes start at unpaired ends; whatever is left is a loop, cut open at its first pipe.
            for (int p = 0; p < _pipes.Count; p++)
            {
                if (visited[p]) continue;
                if (partner[EndKey(p, true)] < 0) routes.Add(Walk(EndKey(p, true)));
                else if (partner[EndKey(p, false)] < 0) routes.Add(Walk(EndKey(p, false)));
            }
            for (int p = 0; p < _pipes.Count; p++)
                if (!visited[p]) routes.Add(Walk(EndKey(p, true)));
            return routes;
        }

        /// <summary>
        /// The route from <paramref name="startPipe"/> to <paramref name="endPipe"/> that continues straightest at every joint,
        /// so it stays on one of several parallel pipes. Null when they are not connected.
        /// </summary>
        public List<RunStep> FindStraightestPath(int startPipe, int endPipe, bool respectFlow)
        {
            if (startPipe < 0 || endPipe < 0) return null;
            if (startPipe == endPipe) return new List<RunStep> { new RunStep { Pipe = startPipe, Forward = true } };

            // A state is a pipe travelled in one direction: 2 * pipe + (forward ? 1 : 0).
            int states = 2 * _pipes.Count;
            double[] dist = new double[states];
            int[] previous = new int[states];
            for (int i = 0; i < states; i++) { dist[i] = double.PositiveInfinity; previous[i] = -1; }
            PriorityQueue<int, double> queue = new PriorityQueue<int, double>();
            foreach (bool forward in new[] { true, false })
            {
                int s = 2 * startPipe + (forward ? 1 : 0);
                dist[s] = 1;
                queue.Enqueue(s, 1);
            }

            int goal = -1;
            while (queue.TryDequeue(out int state, out double d))
            {
                if (d > dist[state]) continue;
                int pipe = state / 2;
                bool forward = state % 2 == 1;
                if (pipe == endPipe) { goal = state; break; }
                int exit = EndKey(pipe, !forward);
                foreach (int e in _nodes[_endNode[exit]].Ends)
                {
                    int next = PipeOf(e);
                    if (next == pipe) continue;
                    int nextState = 2 * next + (IsStart(e) ? 1 : 0);
                    double nd = d + 1 + TurnWeight * ContinuationCost(exit, e, respectFlow);
                    if (nd >= dist[nextState]) continue;
                    dist[nextState] = nd;
                    previous[nextState] = state;
                    queue.Enqueue(nextState, nd);
                }
            }
            if (goal < 0) return null;

            List<RunStep> path = new List<RunStep>();
            for (int s = goal; s >= 0; s = previous[s]) path.Add(new RunStep { Pipe = s / 2, Forward = s % 2 == 1 });
            path.Reverse();
            return path;
        }
    }
}
