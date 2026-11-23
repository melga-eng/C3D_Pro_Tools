using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>Accepted duct type codes, separated by commas or whitespace ("A" or "T1,T3"); empty = any.</summary>
        public string DuctType { get; set; } = "";
        /// <summary>
        /// Width (m) along the inside of the surface's edge where this zone's cover and duct type are not required (a taxiway
        /// surface that includes its 10.5 m shoulder and 6 m blast pad: 16.5). The cover check takes the next zone or unpaved
        /// there; the pavement edge labels in profile views stay at the surface edge.
        /// </summary>
        public double EdgeBand { get; set; }
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
        /// Inside the area and at least <paramref name="band"/> from its edge (outer edge or a hole): the point and every point on
        /// rings 2 m apart out to that radius (every 11.25°) are inside, the outer ring first. Along a straight edge it is off by
        /// at most 0.5 % of the band; a hole or notch smaller than about 3 m across within the band can be missed.
        /// </summary>
        public static bool InsideBy(Func<double, double, bool> inside, double x, double y, double band)
        {
            if (!inside(x, y)) return false;
            if (band <= 0) return true;
            int rings = Math.Max(1, (int)Math.Ceiling(band / 2.0));
            for (int k = rings; k >= 1; k--)
            {
                double r = band * k / rings;
                for (int i = 0; i < 32; i++)
                {
                    double a = i * Math.PI / 16;
                    if (!inside(x + r * Math.Cos(a), y + r * Math.Sin(a))) return false;
                }
            }
            return true;
        }

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
        /// (fails, text). A type accepted by a zone asking more cover is stronger than needed: a note, not a failure.
        /// </summary>
        public static (bool fails, string text)? CheckType(string type, PavementZone strictest, IReadOnlyList<PavementZone> zones)
        {
            if (strictest == null || !IsTypeChecked(type, zones) || AcceptsType(strictest, type)) return null;
            bool fails = !zones.Any(z => z.MinCover > strictest.MinCover && Lists(z, type));
            return (fails, fails ? $"Under {strictest.Name}: needs duct type {strictest.DuctType} (is {type})"
                                 : $"type {type} where {strictest.Name} asks {strictest.DuctType} (stronger than needed)");
        }

        public static bool AcceptsType(PavementZone zone, string type) =>
            string.IsNullOrWhiteSpace(zone.DuctType) || Regex.Split(zone.DuctType.Trim(), @"[,\s]+")
                .Any(code => string.Equals(code, type?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>A zone names this type in its list (an empty list, "any", names none).</summary>
        private static bool Lists(PavementZone zone, string type) => !string.IsNullOrWhiteSpace(zone.DuctType) && AcceptsType(zone, type);

        /// <summary>
        /// Only types some zone names are checked: types outside the rules (ICT 1C micro ducts, untyped cables) are left out, even
        /// where a zone accepts "any" (review: an empty list must not bring every type into the check).
        /// </summary>
        public static bool IsTypeChecked(string type, IReadOnlyList<PavementZone> zones) =>
            !string.IsNullOrWhiteSpace(type) && zones.Any(z => Lists(z, type));

        private static readonly Regex TypeInName = new Regex(@"IC-?\d([A-Z])(?![A-Z])", RegexOptions.IgnoreCase);
        private static readonly Regex LetterType = new Regex(@"(?<![A-Z0-9])\d([ABC])(?![A-Z0-9])", RegexOptions.IgnoreCase);
        private static readonly Regex NumberType = new Regex(@"\b(?:TYPE\s*[-_:]?\s*|T)0*(\d+)(?![A-Z0-9])", RegexOptions.IgnoreCase);
        private static readonly Regex MultipleTypes = new Regex(@"\b(?:TYPE\s*[-_:]?\s*|T)\d+\s*(?:&|/|,|\+|AND\b|OR\b)\s*(?:(?:TYPE\s*[-_:]?\s*|T))?\d+\b", RegexOptions.IgnoreCase);
        private static readonly Regex MvContext = new Regex(@"\b(?:MV|MEDIUM[\s-]+VOLTAGE|TRENCH|DUCT[\s_-]*BANK)\b", RegexOptions.IgnoreCase);
        private static readonly Regex MvName = new Regex(@"-T(\d)-", RegexOptions.IgnoreCase);

        /// <summary>
        /// A type from the first unambiguous source: description, size, then name. ICT gives a letter; MV gives T + number.
        /// </summary>
        public static string DuctTypeOf(string name, string size, string description)
        {
            string FromText(string text)
            {
                text ??= "";
                Match letter = LetterType.Match(text);
                // "3C x 500 mm2 Cable" describes cable cores, not an ICT micro-duct type.
                if (letter.Success && !Regex.IsMatch(text, @"\bCABLE\b", RegexOptions.IgnoreCase)) return letter.Groups[1].Value.ToUpperInvariant();
                if (MultipleTypes.IsMatch(text)) return null;
                var codes = NumberType.Matches(text).Cast<Match>()
                    .Where(m => MvContext.IsMatch(text) || m.Value.StartsWith("T", StringComparison.OrdinalIgnoreCase) && !m.Value.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase))
                    .Select(m => "T" + m.Groups[1].Value).Distinct().ToList();
                if (codes.Count > 1) return null;
                if (codes.Count == 1) return codes[0];
                // ICT "Reinforcement" sizes are type A; an MV text with its own numbered type is read above first (review finding).
                return text.IndexOf("Reinforc", StringComparison.OrdinalIgnoreCase) >= 0 ? "A" : null;
            }
            // The name first: on the project the type is written in the name (ICT "IC-125-IC2B-01" → B, MV "IF-PIP-MVDCT-T1-0189" → T1),
            // as the old check did (ICT drawing test, 2026-11-23: four "2B" ducts whose part says A stay B). The part's description
            // and size only when the name carries no type.
            Match m = TypeInName.Match(name ?? "");
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            m = Regex.Match(name ?? "", @"(?:^|-)\d([A-Z])(?=-|$)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            m = MvName.Match(name ?? "");
            if (m.Success) return "T" + m.Groups[1].Value;
            return FromText(description) ?? FromText(size);
        }
    }

    /// <summary>Templates for accepted types only: cover, surfaces and edge bands stay as configured.</summary>
    internal sealed class PavementDuctTypePreset
    {
        public static readonly IReadOnlyList<PavementDuctTypePreset> All = Array.AsReadOnly(new[]
        {
            new PavementDuctTypePreset("ICT (A under pavement, B elsewhere)", "A", "B"),
            new PavementDuctTypePreset("MV (T1,T3 under pavement, T2,T4 elsewhere)", "T1,T3", "T2,T4"),
            new PavementDuctTypePreset("ICT + MV (A,T1,T3 under pavement; B,T2,T4 elsewhere)", "A,T1,T3", "B,T2,T4"),
        });

        private readonly string _name, _underPavement, _elsewhere;
        private PavementDuctTypePreset(string name, string underPavement, string elsewhere)
        { _name = name; _underPavement = underPavement; _elsewhere = elsewhere; }
        public override string ToString() => _name;

        public void Apply(IReadOnlyList<PavementZone> zones, PavementZone unpaved)
        {
            double largest = zones.Count > 0 ? zones.Max(z => z.MinCover) : 0;
            foreach (PavementZone zone in zones)
                zone.DuctType = zone.MinCover == largest ? _underPavement : _elsewhere;
            unpaved.DuctType = _elsewhere;
        }
    }
}
