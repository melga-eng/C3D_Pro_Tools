namespace C3DProTools.ProfileTool
{
    /// <summary>One duct along the alignment: top-of-duct elevation at its two drawn ends (already trimmed at structures).</summary>
    public sealed class DuctSegment
    {
        public string Name;
        public double S0, Z0, S1, Z1;

        public DuctSegment(string name, double s0, double z0, double s1, double z1)
        {
            Name = name;
            if (s1 < s0) { (s0, s1) = (s1, s0); (z0, z1) = (z1, z0); }
            S0 = s0; Z0 = z0; S1 = s1; Z1 = z1;
        }

        public double Length => S1 - S0;
        public bool Covers(double s, double eps) => s >= S0 - eps && s <= S1 + eps;
        public double ZAt(double s) => Length <= 1e-9 ? Math.Max(Z0, Z1) : Z0 + (Z1 - Z0) * (Math.Clamp(s, S0, S1) - S0) / Length;
    }

    public readonly struct StationElevation
    {
        public readonly double Station, Elevation;
        public StationElevation(double s, double z) { Station = s; Elevation = z; }
        public override string ToString() => $"{Station:F3}  {Elevation:F3}";
    }

    public sealed class TopFitResult
    {
        public List<StationElevation> Points = new();
        public List<string> Warnings = new();
    }

    /// <summary>
    /// Pure logic, no Autodesk API. Builds a TOP profile from duct tops: the upper envelope of every duct
    /// (a PVI at every duct end and wherever two overlapping ducts cross), straight across the gaps at manholes,
    /// and flat from the alignment start to the first duct end and from the last duct end to the alignment end.
    /// </summary>
    public static class TopProfileMath
    {
        public const double StationEps = 0.001;
        private const double StepTolerance = 0.02;
        /// <summary>Longer than any manhole gap between duct ends (structures here are ~4.6 m outer).</summary>
        public const double MaxGap = 8.0;
        private const double OverlapReport = 0.05;

        public static TopFitResult Compute(IEnumerable<DuctSegment> ducts, double alignStart, double alignEnd)
        {
            var result = new TopFitResult();
            var segs = ducts.Where(d => d.Length > StationEps && d.S1 > alignStart && d.S0 < alignEnd).ToList();
            if (segs.Count == 0) return result;

            // Candidate stations: every duct end, plus crossings of overlapping ducts.
            var stations = new List<double>();
            foreach (var d in segs) { stations.Add(Math.Max(d.S0, alignStart)); stations.Add(Math.Min(d.S1, alignEnd)); }
            for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
            {
                var a = segs[i]; var b = segs[j];
                double lo = Math.Max(a.S0, b.S0), hi = Math.Min(a.S1, b.S1);
                if (hi - lo <= StationEps) continue;
                if (hi - lo > OverlapReport)
                    result.Warnings.Add($"Ducts overlap: {a.Name} and {b.Name} (sta {lo:F2} to {hi:F2}); TOP follows the higher one.");
                double dLo = a.ZAt(lo) - b.ZAt(lo), dHi = a.ZAt(hi) - b.ZAt(hi);
                if (Math.Sign(dLo) != Math.Sign(dHi) && Math.Abs(dLo - dHi) > 1e-12)
                    stations.Add(lo + (hi - lo) * dLo / (dLo - dHi));
            }
            stations.Sort();

            var pts = new List<StationElevation>();
            foreach (double s in stations)
            {
                if (pts.Count > 0 && s - pts[^1].Station < StationEps) continue;
                var covering = segs.Where(d => d.Covers(s, StationEps)).ToList();
                if (covering.Count == 0) continue;
                double z = covering.Max(d => d.ZAt(s));

                // A duct ending where another starts at a different height: a profile cannot step vertically.
                var touching = covering.Where(d => Math.Abs(d.S0 - s) < StationEps || Math.Abs(d.S1 - s) < StationEps).ToList();
                if (touching.Count > 1)
                {
                    double zMin = touching.Min(d => d.ZAt(s));
                    if (z - zMin > StepTolerance)
                        result.Warnings.Add($"Step of {z - zMin:F3} at sta {s:F2} ({string.Join(", ", touching.Select(d => d.Name))}); TOP uses the higher end.");
                }
                pts.Add(new StationElevation(s, z));
            }
            if (pts.Count == 0) return result;

            // Gaps longer than a manhole usually mean a duct is missing (not shown in the view, or off the alignment).
            var covered = segs.Select(d => (lo: Math.Max(d.S0, alignStart), hi: Math.Min(d.S1, alignEnd))).OrderBy(c => c.lo).ToList();
            double reach = covered[0].hi;
            foreach (var c in covered.Skip(1))
            {
                if (c.lo - reach > MaxGap)
                    result.Warnings.Add($"No duct from sta {reach:F2} to {c.lo:F2} ({c.lo - reach:F2}); TOP runs straight across. A duct may be missing from the view or off the alignment.");
                reach = Math.Max(reach, c.hi);
            }

            // Flat over the start and end structures: same elevation as the first/last duct end.
            if (pts[0].Station - alignStart > StationEps) pts.Insert(0, new StationElevation(alignStart, pts[0].Elevation));
            if (alignEnd - pts[^1].Station > StationEps) pts.Add(new StationElevation(alignEnd, pts[^1].Elevation));

            result.Points = pts;
            return result;
        }

        /// <summary>Elevation of a polyline profile at a station, or null outside it.</summary>
        public static double? ElevationAt(IReadOnlyList<StationElevation> pts, double s)
        {
            if (pts.Count == 0 || s < pts[0].Station - StationEps || s > pts[^1].Station + StationEps) return null;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                if (s <= b.Station + StationEps)
                {
                    double len = b.Station - a.Station;
                    if (len <= 1e-9) return b.Elevation;
                    double t = Math.Clamp((s - a.Station) / len, 0, 1);
                    return a.Elevation + (b.Elevation - a.Elevation) * t;
                }
            }
            return pts[^1].Elevation;
        }
    }
}
