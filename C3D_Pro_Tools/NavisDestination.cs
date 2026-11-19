using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    /// <summary>Where imported network parts go: networks already in the drawing when they match, one network you choose, or new ones.</summary>
    internal enum DestinationMode { Auto, OneNetwork, NewNetworks }

    /// <summary>A structure already in the drawing, in metres (plain data, read by the importer).</summary>
    internal sealed class DrawingStructure
    {
        public string Name = "";
        public double X, Y, Rotation, Rim, Sump;
        public bool Circular;
        /// <summary>Plan size: diameter (circular), or length along <see cref="Rotation"/> and width across it.</summary>
        public double Diameter, Length, Width;
    }

    /// <summary>A pipe already in the drawing: its centreline ends, in metres.</summary>
    internal sealed class DrawingPipe
    {
        public string Name = "";
        public V3 Start, End;
    }

    /// <summary>A pipe network already in the drawing. Only an editable one (not a data-shortcut reference) takes new parts.</summary>
    internal sealed class DrawingNetwork
    {
        public string Name = "";
        public bool Editable = true;
        public string PartsList = "";
        public List<DrawingStructure> Structures = new List<DrawingStructure>();
        public List<DrawingPipe> Pipes = new List<DrawingPipe>();
    }

    /// <summary>The network one row goes to, and why.</summary>
    internal sealed class RowDestination
    {
        public string Network = "";
        /// <summary>The network is already in the drawing (its parts list takes any new sizes).</summary>
        public bool Existing;
        public string Reason = "";
        /// <summary>The same part is already drawn in that network (same place): it is left out by default.</summary>
        public bool InDrawing;
        public string InDrawingPart = "";
    }

    /// <summary>
    /// Decides the destination network of each import row. Auto: a group of rows (the new-network grouping) joins a drawing
    /// network that has the same name as its source network, or whose structures its pipe ends meet; otherwise it becomes a
    /// new network. Rows already drawn in their destination are marked. No AutoCAD types, so it is tested on its own.
    /// </summary>
    internal static class NavisDestination
    {
        /// <summary>A pipe end within this much (metres) of a structure's outline counts as meeting it.</summary>
        public const double EndMargin = 0.10;
        /// <summary>A part within this much (metres) of an existing one in the same network is that part.</summary>
        public const double SameTolerance = 0.10;

        public static Dictionary<ImportCandidate, RowDestination> Decide(
            IList<ImportCandidate> rows, IList<DrawingNetwork> networks, DestinationMode mode, string chosenNetwork,
            Func<ImportCandidate, string> newNetworkName, IDictionary<ImportCandidate, string> overrides = null)
        {
            var result = new Dictionary<ImportCandidate, RowDestination>();
            List<DrawingNetwork> editable = networks.Where(n => n.Editable).ToList();
            DrawingNetwork Find(string name) => string.IsNullOrEmpty(name) ? null : editable.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            RowDestination To(string name, string reason)
            {
                DrawingNetwork n = Find(name);
                return n != null ? new RowDestination { Network = n.Name, Existing = true, Reason = reason } : new RowDestination { Network = name, Reason = "new network" };
            }

            List<ImportCandidate> network = rows.Where(r => r.Action == ImportKind.Pipe || r.Action == ImportKind.Box || r.Action == ImportKind.Structure).ToList();
            var auto = new List<ImportCandidate>();
            foreach (ImportCandidate r in network)
            {
                if (overrides != null && overrides.TryGetValue(r, out string own) && !string.IsNullOrEmpty(own)) result[r] = To(own, "chosen by you");
                else if (mode == DestinationMode.OneNetwork && Find(chosenNetwork) != null) result[r] = To(chosenNetwork, "the network you chose");
                else if (mode == DestinationMode.Auto) auto.Add(r);
                else
                {
                    string name = newNetworkName(r);
                    result[r] = To(name, "earlier import of this name");
                }
            }

            foreach (IGrouping<string, ImportCandidate> group in auto.GroupBy(newNetworkName, StringComparer.OrdinalIgnoreCase))
            {
                // 1. The source network has the name of a drawing network (a model exported from this project).
                DrawingNetwork byName = group.Select(r => Find(r.Network)).FirstOrDefault(n => n != null);
                if (byName != null) { foreach (ImportCandidate r in group) result[r] = To(byName.Name, "same network name"); continue; }

                // 2. Each linked run of parts (pipes meeting each other or the group's structures) whose pipe ends meet the
                //    structures of a drawing network, or whose structures stand where that network's do, joins it. Parts far
                //    from it in the same group are not dragged along.
                foreach (List<ImportCandidate> run in Runs(group.ToList()))
                {
                    DrawingNetwork best = null;
                    int bestHits = 0;
                    foreach (DrawingNetwork n in editable)
                    {
                        int hits = Hits(run, n);
                        if (hits > bestHits) { best = n; bestHits = hits; }
                    }
                    // 3. Otherwise a network of the import's own name (an earlier import), else a new network.
                    foreach (ImportCandidate r in run)
                        result[r] = best != null ? To(best.Name, $"{bestHits} connection(s) to its structures") : To(group.Key, "earlier import of this name");
                }
            }

            foreach (KeyValuePair<ImportCandidate, RowDestination> kv in result)
            {
                if (!kv.Value.Existing) continue;
                string same = SamePart(kv.Key, Find(kv.Value.Network));
                if (same != null) { kv.Value.InDrawing = true; kv.Value.InDrawingPart = same; }
            }
            return result;
        }

        /// <summary>The rows split into linked runs: pipes whose ends meet, and pipes ending in a structure of the rows.</summary>
        internal static List<List<ImportCandidate>> Runs(List<ImportCandidate> rows)
        {
            int[] parent = Enumerable.Range(0, rows.Count).ToArray();
            int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                    if (Linked(rows[i], rows[j])) parent[Root(i)] = Root(j);
            return Enumerable.Range(0, rows.Count).GroupBy(Root).Select(g => g.Select(i => rows[i]).ToList()).ToList();
        }

        private static bool Linked(ImportCandidate a, ImportCandidate b)
        {
            bool sa = a.Action == ImportKind.Structure, sb = b.Action == ImportKind.Structure;
            if (sa && sb) return false;
            if (sa || sb)
            {
                ImportCandidate st = sa ? a : b, pipe = sa ? b : a;
                double reach = Math.Max(st.StructDiameter, Math.Max(st.StructWidth, st.StructLength)) / 2 + EndMargin;
                return Ends(pipe).Any(e => Plan(e.X, e.Y, st.X, st.Y) <= reach);
            }
            return Ends(a).Any(e => Ends(b).Any(f => Dist(e, f) <= 2 * EndMargin));
        }

        /// <summary>How many of the group's pipe ends meet the network's structures, plus its structures standing on one of them.</summary>
        internal static int Hits(IEnumerable<ImportCandidate> group, DrawingNetwork n)
        {
            int hits = 0;
            foreach (ImportCandidate r in group)
            {
                // A part already drawn there is the strongest sign.
                if (SamePart(r, n) != null) { hits += 2; continue; }
                if (r.Action == ImportKind.Structure) continue;
                // A pipe end in one of its structures, or meeting one of its pipes' ends (a duct carried on).
                foreach (V3 end in Ends(r))
                    if (StructureAt(n, end) != null || n.Pipes.Any(p => Dist(p.Start, end) <= 2 * EndMargin || Dist(p.End, end) <= 2 * EndMargin)) hits++;
            }
            return hits;
        }

        /// <summary>The drawing structure whose outline (plus <see cref="EndMargin"/>) holds the point, between its sump and rim.</summary>
        internal static DrawingStructure StructureAt(DrawingNetwork n, V3 p)
        {
            foreach (DrawingStructure s in n.Structures)
                if (Holds(s, p, EndMargin)) return s;
            return null;
        }

        internal static bool Holds(DrawingStructure s, V3 p, double margin)
        {
            if (p.Z < Math.Min(s.Sump, s.Rim) - margin || p.Z > Math.Max(s.Sump, s.Rim) + margin) return false;
            double x = p.X - s.X, y = p.Y - s.Y;
            if (s.Circular) return Math.Sqrt(x * x + y * y) <= s.Diameter / 2 + margin;
            double along = x * Math.Cos(s.Rotation) + y * Math.Sin(s.Rotation), across = -x * Math.Sin(s.Rotation) + y * Math.Cos(s.Rotation);
            return Math.Abs(along) <= s.Length / 2 + margin && Math.Abs(across) <= s.Width / 2 + margin;
        }

        /// <summary>The name of the part of <paramref name="n"/> that is this row (same place), or null.</summary>
        internal static string SamePart(ImportCandidate r, DrawingNetwork n)
        {
            if (n == null) return null;
            if (r.Action == ImportKind.Structure)
                return n.Structures.FirstOrDefault(s => Plan(s.X, s.Y, r.X, r.Y) <= SameTolerance)?.Name;
            // Every barrel must already be drawn.
            string first = null;
            foreach ((V3 a, V3 b) in BarrelEnds(r))
            {
                DrawingPipe p = n.Pipes.FirstOrDefault(d => (Dist(d.Start, a) <= SameTolerance && Dist(d.End, b) <= SameTolerance)
                                                         || (Dist(d.Start, b) <= SameTolerance && Dist(d.End, a) <= SameTolerance));
                if (p == null) return null;
                first = first ?? p.Name;
            }
            return first;
        }

        private static IEnumerable<V3> Ends(ImportCandidate r)
        {
            foreach ((V3 a, V3 b) in BarrelEnds(r)) { yield return a; yield return b; }
        }

        private static IEnumerable<(V3, V3)> BarrelEnds(ImportCandidate r)
        {
            if (r.Barrels.Count == 0) { yield return (r.Start, r.End); yield break; }
            foreach ((double u, double w) in r.Barrels)
            {
                V3 off = r.BarrelAxis * u + r.BarrelUp * w;
                yield return (r.Start + off, r.End + off);
            }
        }

        private static double Plan(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

        private static double Dist(V3 a, V3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }
}
