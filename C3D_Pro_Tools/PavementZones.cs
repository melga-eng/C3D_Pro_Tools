using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    /// <summary>One pavement kind: where its surface covers the ground, ducts need this cover and duct type.</summary>
    internal sealed class PavementZone
    {
        /// <summary>The surface that covers exactly the paved area (a data reference is fine).</summary>
        public string Surface { get; set; } = "";
        public string Name { get; set; } = "";
        public double MinCover { get; set; }
        /// <summary>Most cover here (m); 0 = the general maximum, or no maximum when this zone's minimum is at or above it.</summary>
        public double MaxCover { get; set; }
        /// <summary>The duct type letter required here ("A" = reinforced); empty = any.</summary>
        public string DuctType { get; set; } = "";
        /// <summary>Station-elevation label styles for its edges in profile views; empty = the pavement section's defaults.</summary>
        public string StartLabelStyle { get; set; } = "";
        public string EndLabelStyle { get; set; } = "";
    }

    /// <summary>A stretch of an alignment or route in one pavement kind (0 = unpaved, i = zones[i - 1]).</summary>
    internal readonly struct PavementRun
    {
        public readonly double S0, S1;
        public readonly int Kind;
        public PavementRun(double s0, double s1, int kind) { S0 = s0; S1 = s1; Kind = kind; }
        public override string ToString() => $"{Kind}: {S0:0.###}-{S1:0.###}";
    }

    /// <summary>Pure geometry of pavement zones along a station range (no Autodesk types), so it is unit tested.</summary>
    internal static class PavementZones
    {
        /// <summary>
        /// The runs of each kind between <paramref name="s0"/> and <paramref name="s1"/>: sampled every <paramref name="step"/>,
        /// each change of kind then found to within <paramref name="tolerance"/> by halving. <paramref name="kindAt"/> gives the
        /// kind at a station (it decides which pavement wins where two overlap). A run narrower than a step can be missed.
        /// </summary>
        public static List<PavementRun> Runs(double s0, double s1, double step, Func<double, int> kindAt, double tolerance = 0.01)
        {
            var runs = new List<PavementRun>();
            if (s1 < s0 || kindAt == null) return runs;
            step = step > 0 ? step : 1.0;
            double start = s0, prevS = s0;
            int kind = kindAt(s0);
            for (double s = s0 + step; ; s += step)
            {
                double at = Math.Min(s, s1);
                int k = kindAt(at);
                if (k != kind)
                {
                    // Halve the step between the last station of the old kind and the first of the new one.
                    double a = prevS, b = at;
                    while (b - a > tolerance)
                    {
                        double m = (a + b) / 2;
                        if (kindAt(m) == kind) a = m; else b = m;
                    }
                    double edge = (a + b) / 2;
                    runs.Add(new PavementRun(start, edge, kind));
                    start = edge;
                    kind = kindAt(b);
                }
                prevS = at;
                if (at >= s1) break;
            }
            runs.Add(new PavementRun(start, s1, kind));
            return runs;
        }

        /// <summary>
        /// The paved stretches of a route checked at these stations (in order), each from the last station before it to the first
        /// after it, so its edges are always inside (as cover zones from polygons are). Unpaved stretches are left out.
        /// </summary>
        public static List<PavementRun> PavedStretches(IReadOnlyList<double> stations, IReadOnlyList<int> kinds, Func<double, int> kindAt = null, double tolerance = 0.01)
        {
            var list = new List<PavementRun>();
            int n = Math.Min(stations.Count, kinds.Count);
            // Where the kind changes between two checked stations: halved down to the tolerance when the kind can be asked
            // anywhere, else the outer station (the stretch then reaches the stations either side).
            double Edge(int inside, int outside)
            {
                if (kindAt == null) return stations[outside];
                double a = stations[inside], b = stations[outside];
                int k = kinds[inside];
                while (Math.Abs(b - a) > tolerance)
                {
                    double m = (a + b) / 2;
                    if (kindAt(m) == k) a = m; else b = m;
                }
                return (a + b) / 2;
            }
            int i = 0;
            while (i < n)
            {
                if (kinds[i] <= 0) { i++; continue; }
                int j = i;
                while (j + 1 < n && kinds[j + 1] == kinds[i]) j++;
                double s0 = i > 0 ? Edge(i, i - 1) : stations[0], s1 = j + 1 < n ? Edge(j, j + 1) : stations[n - 1];
                list.Add(new PavementRun(s0, s1, kinds[i]));
                i = j + 1;
            }
            return list;
        }

        /// <summary>The kinds met between two stations (both included), from the route's checked stations.</summary>
        public static HashSet<int> KindsBetween(IReadOnlyList<double> stations, IReadOnlyList<int> kinds, double s0, double s1)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < Math.Min(stations.Count, kinds.Count); i++)
                if (stations[i] >= s0 - 1e-6 && stations[i] <= s1 + 1e-6) set.Add(kinds[i]);
            return set;
        }

        /// <summary>The kind at a point: the first zone (in priority order) whose test says the point is inside it, else 0.</summary>
        public static int KindAt(IReadOnlyList<Func<double, double, bool>> inside, double x, double y)
        {
            for (int i = 0; i < inside.Count; i++)
                if (inside[i] != null && inside[i](x, y)) return i + 1;
            return 0;
        }

        /// <summary>The cover zone maximum for a pavement: its own, else the general one, else none when its minimum is not below the general maximum.</summary>
        public static double ZoneMax(PavementZone z, double generalMax) =>
            z.MaxCover > 0 ? z.MaxCover : z.MinCover >= generalMax - 1e-9 ? double.NaN : 0;

        /// <summary>
        /// A duct's type against the strictest pavement it passes under (the one asking most cover): null when it fits, else
        /// (fails, text). A reinforced (A) duct where B is asked is stronger than needed: a note, not a failure.
        /// </summary>
        public static (bool fails, string text)? CheckType(string type, PavementZone strictest)
        {
            if (strictest == null || string.IsNullOrEmpty(strictest.DuctType) || string.IsNullOrEmpty(type)) return null;
            if (string.Equals(type, strictest.DuctType, StringComparison.OrdinalIgnoreCase)) return null;
            bool fails = !string.Equals(type, "A", StringComparison.OrdinalIgnoreCase);
            return (fails, fails ? $"Under {strictest.Name}: needs duct type {strictest.DuctType} (is {type})"
                                 : $"type {type} where {strictest.Name} asks {strictest.DuctType} (stronger than needed)");
        }

        private static readonly Regex TypeInName = new Regex(@"IC-?\d([A-Z])(?![A-Z])", RegexOptions.IgnoreCase);

        /// <summary>
        /// The duct type letter of a duct: from its name ("IC-124-IC3A-06" → A), else from its size ("Reinforcement" → A), else
        /// null (unknown: not checked).
        /// </summary>
        public static string DuctTypeOf(string name, string size)
        {
            Match m = TypeInName.Match(name ?? "");
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            if ((size ?? "").IndexOf("Reinforc", StringComparison.OrdinalIgnoreCase) >= 0) return "A";
            return null;
        }
    }
}
