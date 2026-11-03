using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace C3DProTools
{
    internal sealed class CoverRow
    {
        public ProfilePipe Pipe;
        public double MinCover, MaxCover;
        public string Status;
        public bool Failed;
    }

    /// <summary>Measures cover (to the chosen reference) and slope along each pipe's whole length and flags the limits it breaks.</summary>
    internal static class CoverEvaluation
    {
        // Results within a millimetre of a limit count as meeting it (levels are searched on a 2.5 cm grid).
        private const double Tolerance = 0.001;

        public static List<CoverRow> Evaluate(CoverRoute route, List<ProfilePipe> pipes, CoverOptions o)
        {
            List<CoverRow> rows = new List<CoverRow>();
            foreach (ProfilePipe p in pipes)
            {
                RouteSection sec = route.Sections[p.Section];
                double off = CoverRoute.ReferenceOffset(o.Reference, p.Source.InnerHeight, p.Source.OuterHeight);
                double min = double.MaxValue, max = double.MinValue;
                List<string> issues = new List<string>();
                // The least clear distance to each crossing pipe along this pipe (a skewed or parallel one spans several stations).
                Dictionary<RouteCrossing, double> leastClear = new Dictionary<RouteCrossing, double>();
                for (int r = sec.FirstRecord; r <= sec.LastRecord; r++)
                {
                    RouteRecord rec = route.Records[r];
                    if (rec.S < p.S0 - 1e-6 || rec.S > p.S1 + 1e-6) continue;
                    double cover = rec.Ground - (p.ZAt(rec.S) + off);
                    min = Math.Min(min, cover);
                    max = Math.Max(max, cover);

                    if (o.CrossingClearance <= 0) continue;
                    foreach (CrossingLevel level in rec.Crossings)
                    {
                        // Clear distance outside to outside, whichever side the pipe passes.
                        double half = p.Source.OuterHeight / 2.0, z = p.ZAt(rec.S);
                        double clear = Math.Max(level.Bottom - (z + half), (z - half) - level.Top);
                        if (!leastClear.TryGetValue(level.Crossing, out double least) || clear < least) leastClear[level.Crossing] = clear;
                    }
                }
                foreach (KeyValuePair<RouteCrossing, double> kv in leastClear)
                {
                    if (kv.Value < o.CrossingClearance - Tolerance)
                        issues.Add(kv.Value < 0 ? $"Clashes with {kv.Key.Name}" : $"Only {kv.Value:0.00} m clear of {kv.Key.Name}");
                }

                if (min < o.MinCover - Tolerance) issues.Add($"Cover below min ({min:0.00} m)");
                if (max > o.MaxCover + Tolerance) issues.Add($"Cover above max ({max:0.00} m)");
                double slope = p.Slope;
                if (o.Mode == CoverMode.Gravity)
                {
                    if (slope > -o.MinSlope + 1e-6) issues.Add(slope > 1e-6 ? "Adverse slope" : "Slope below min");
                }
                else if (o.MinSlope > 0 && Math.Abs(slope) < o.MinSlope - 1e-6)
                {
                    issues.Add("Slope below min");
                }
                if (Math.Abs(slope) > o.MaxSlope + 1e-6) issues.Add("Slope above max");

                rows.Add(new CoverRow
                {
                    Pipe = p,
                    MinCover = min == double.MaxValue ? double.NaN : min,
                    MaxCover = max == double.MinValue ? double.NaN : max,
                    Status = issues.Count == 0 ? "OK" : string.Join("; ", issues),
                    Failed = issues.Count > 0
                });
            }
            return rows;
        }

        public static string ToCsv(List<CoverRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Pipe,Station From,Station To,Length (m),Slope (%),Min Cover (m),Max Cover (m),Status");
            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (CoverRow row in rows)
            {
                ProfilePipe p = row.Pipe;
                sb.AppendLine(string.Join(",",
                    Quote(p.Name), p.S0.ToString("0.000", c), p.S1.ToString("0.000", c), p.Length.ToString("0.000", c),
                    (p.Slope * 100).ToString("0.000", c), row.MinCover.ToString("0.000", c), row.MaxCover.ToString("0.000", c), Quote(row.Status)));
            }
            return sb.ToString();
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
