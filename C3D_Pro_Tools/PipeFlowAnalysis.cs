using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    internal enum PipeEndIssueKind
    {
        /// <summary>Two pipe ends are close (under <see cref="PipeFlowAnalysis.GapLimit"/>) but not touching.</summary>
        Gap,
        /// <summary>Two pipe ends touch in plan but their inverts differ.</summary>
        LevelMismatch
    }

    internal sealed class PipeEndIssue
    {
        public PipeEndIssueKind Kind;
        public ObjectId PipeA, PipeB;
        public string LabelA, LabelB;
        public double PlanDistance;
        public double InvertDifference;
        public Point3d Location;
    }

    /// <summary>
    /// A chain of pipes joined end to end, two at a time. Branch junctions (three or more pipe ends) split runs,
    /// since several pipes meeting there may flow in different directions on purpose.
    /// </summary>
    internal sealed class FlowRun
    {
        /// <summary>Pipes that point the same way as the run's first pipe.</summary>
        public readonly List<ObjectId> WithSeed = new List<ObjectId>();
        /// <summary>Pipes that point against the run's first pipe.</summary>
        public readonly List<ObjectId> AgainstSeed = new List<ObjectId>();
        /// <summary>Total invert drop along the run when travelled in the seed direction (positive = downhill).</summary>
        public double SeedDrop;

        public bool IsMixed => WithSeed.Count > 0 && AgainstSeed.Count > 0;
        public List<ObjectId> Majority => WithSeed.Count >= AgainstSeed.Count ? WithSeed : AgainstSeed;
        public List<ObjectId> Minority => WithSeed.Count >= AgainstSeed.Count ? AgainstSeed : WithSeed;
        public List<ObjectId> Uphill => SeedDrop >= 0 ? AgainstSeed : WithSeed;
    }

    internal sealed class PipeFlowReport
    {
        public readonly List<PipeEndIssue> Issues = new List<PipeEndIssue>();
        public readonly List<FlowRun> Runs = new List<FlowRun>();
        public bool HasMixedDirections => Runs.Exists(r => r.IsMixed);
    }

    /// <summary>Checks gravity pipes for gaps and level mismatches at their ends, and for mixed flow directions.</summary>
    internal static class PipeFlowAnalysis
    {
        /// <summary>Plan distance under which two pipe ends count as the same point (drawing units, meters).</summary>
        public const double CoincidentTolerance = 0.001;
        /// <summary>Invert difference under which two touching ends count as level.</summary>
        public const double LevelTolerance = 0.001;
        /// <summary>Ends closer than this but not touching are reported as gaps; farther apart they are taken as unconnected.</summary>
        public const double GapLimit = 1.0;

        private sealed class PipeEnd
        {
            public int Pipe;
            public bool IsStart;
            public Point3d Point;
            public double Invert;
            public ObjectId StructureId;
            public int Node;
        }

        public static PipeFlowReport Analyze(Transaction tr, IEnumerable<ObjectId> pipeIds)
        {
            List<ObjectId> ids = new List<ObjectId>();
            List<string> names = new List<string>();
            List<PipeEnd> ends = new List<PipeEnd>();

            foreach (ObjectId id in pipeIds)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe)) continue;
                int index = ids.Count;
                ids.Add(id);
                names.Add(pipe.Name);

                // Pipe points are on the centreline, so the invert sits half the inner height below them.
                double halfHeight = pipe.InnerHeight / 2.0;
                ends.Add(new PipeEnd { Pipe = index, IsStart = true, Point = pipe.StartPoint, Invert = pipe.StartPoint.Z - halfHeight, StructureId = pipe.StartStructureId });
                ends.Add(new PipeEnd { Pipe = index, IsStart = false, Point = pipe.EndPoint, Invert = pipe.EndPoint.Z - halfHeight, StructureId = pipe.EndStructureId });
            }

            PipeFlowReport report = new PipeFlowReport();
            int[] parent = new int[ends.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            // Ends join when they share a structure or touch in plan; touching ends are also checked for level.
            for (int i = 0; i < ends.Count; i++)
            {
                PipeEnd a = ends[i];
                for (int j = i + 1; j < ends.Count; j++)
                {
                    PipeEnd b = ends[j];
                    if (a.Pipe == b.Pipe) continue;

                    if (!a.StructureId.IsNull && a.StructureId == b.StructureId)
                    {
                        Union(parent, i, j);
                        continue;
                    }

                    double dist = PlanDistance(a.Point, b.Point);
                    if (dist <= CoincidentTolerance)
                    {
                        Union(parent, i, j);
                        double dz = Math.Abs(a.Invert - b.Invert);
                        if (dz > LevelTolerance)
                            report.Issues.Add(MakeIssue(PipeEndIssueKind.LevelMismatch, a, b, dist, dz, ids, names));
                    }
                    else if (dist < GapLimit)
                    {
                        report.Issues.Add(MakeIssue(PipeEndIssueKind.Gap, a, b, dist, Math.Abs(a.Invert - b.Invert), ids, names));
                    }
                }
            }

            Dictionary<int, List<PipeEnd>> nodes = new Dictionary<int, List<PipeEnd>>();
            for (int i = 0; i < ends.Count; i++)
            {
                PipeEnd end = ends[i];
                end.Node = Find(parent, i);
                if (!nodes.TryGetValue(end.Node, out List<PipeEnd> list)) nodes[end.Node] = list = new List<PipeEnd>();
                list.Add(end);
            }

            BuildRuns(report, ids, ends, nodes);
            return report;
        }

        /// <summary>
        /// Walks each chain of pipes through two-pipe junctions. Across such a junction a consistent chain meets
        /// end-to-start; meeting end-to-end or start-to-start means one of the two pipes points the other way.
        /// </summary>
        private static void BuildRuns(PipeFlowReport report, List<ObjectId> ids, List<PipeEnd> ends, Dictionary<int, List<PipeEnd>> nodes)
        {
            bool?[] withSeed = new bool?[ids.Count];
            for (int seed = 0; seed < ids.Count; seed++)
            {
                if (withSeed[seed].HasValue) continue;

                FlowRun run = new FlowRun();
                Queue<int> queue = new Queue<int>();
                withSeed[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int p = queue.Dequeue();
                    bool same = withSeed[p].Value;
                    PipeEnd start = ends[2 * p], end = ends[2 * p + 1];
                    (same ? run.WithSeed : run.AgainstSeed).Add(ids[p]);
                    run.SeedDrop += same ? start.Invert - end.Invert : end.Invert - start.Invert;

                    foreach (PipeEnd mine in new[] { start, end })
                    {
                        List<PipeEnd> atNode = nodes[mine.Node];
                        if (atNode.Count != 2) continue;

                        PipeEnd other = atNode[0] == mine ? atNode[1] : atNode[0];
                        if (withSeed[other.Pipe].HasValue) continue;

                        // Opposite end types (end meets start) keep the direction; matching types flip it.
                        withSeed[other.Pipe] = mine.IsStart != other.IsStart ? same : !same;
                        queue.Enqueue(other.Pipe);
                    }
                }
                report.Runs.Add(run);
            }
        }

        /// <summary>Swaps a pipe's start and end (keeping both 3D points, so levels stay put) and its structure connections.</summary>
        public static void ReversePipe(Pipe pipe)
        {
            Point3d origStart = pipe.StartPoint;
            Point3d origEnd = pipe.EndPoint;
            ObjectId origStartSt = pipe.StartStructureId;
            ObjectId origEndSt = pipe.EndStructureId;

            // Civil 3D refuses to connect an end that is already connected, so free both ends before swapping.
            if (!origStartSt.IsNull) pipe.Disconnect(ConnectorPositionType.Start);
            if (!origEndSt.IsNull) pipe.Disconnect(ConnectorPositionType.End);

            pipe.StartPoint = origEnd;
            pipe.EndPoint = origStart;

            if (!origEndSt.IsNull) NetworkEditOps.ConnectEnd(pipe, ConnectorPositionType.Start, origEndSt);
            if (!origStartSt.IsNull) NetworkEditOps.ConnectEnd(pipe, ConnectorPositionType.End, origStartSt);

            // Connecting can snap an end to the structure; keep the swapped 3D points exactly.
            if (!pipe.StartPoint.IsEqualTo(origEnd)) pipe.StartPoint = origEnd;
            if (!pipe.EndPoint.IsEqualTo(origStart)) pipe.EndPoint = origStart;
        }

        private static PipeEndIssue MakeIssue(PipeEndIssueKind kind, PipeEnd a, PipeEnd b, double dist, double dz, List<ObjectId> ids, List<string> names)
        {
            return new PipeEndIssue
            {
                Kind = kind,
                PipeA = ids[a.Pipe],
                PipeB = ids[b.Pipe],
                LabelA = $"{names[a.Pipe]} ({(a.IsStart ? "Start" : "End")})",
                LabelB = $"{names[b.Pipe]} ({(b.IsStart ? "Start" : "End")})",
                PlanDistance = dist,
                InvertDifference = dz,
                Location = new Point3d((a.Point.X + b.Point.X) / 2.0, (a.Point.Y + b.Point.Y) / 2.0, (a.Point.Z + b.Point.Z) / 2.0)
            };
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
