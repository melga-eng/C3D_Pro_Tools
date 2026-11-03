using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace C3DProTools
{
    internal sealed class CoverRow
    {
        public ProfilePipe Pipe;
        public double MinCover, MaxCover;
        public string Status;
        public bool Failed;
        /// <summary>Things to act on that are not failures: a backdrop manhole, a protection slab, a pipe alongside.</summary>
        public readonly List<string> Flags = new List<string>();
    }

    /// <summary>
    /// Measures cover (to the chosen reference) and slope along each pipe's whole length and flags the limits it breaks,
    /// with the design rules: junction matching and drops at manholes, crossing clearances (protection slab band), and
    /// for dry utilities the bend radius, the bend budget and length of each pull.
    /// </summary>
    internal static class CoverEvaluation
    {
        // Levels are exact to the millimetre on the solver's grid; only floating point noise is forgiven. (Up to 1.1.0 a
        // millimetre short of a limit counted as meeting it, which let 0.599 m pass a 0.600 m rule.)
        private const double Tolerance = 1e-6;

        public static List<CoverRow> Evaluate(CoverRoute route, List<ProfilePipe> pipes, CoverOptions o)
        {
            CoverRules rules = o.Rules ?? new CoverRules();
            List<CoverRow> rows = new List<CoverRow>();
            foreach (ProfilePipe p in pipes)
            {
                RouteSection sec = route.Sections[p.Section];
                double off = CoverRoute.ReferenceOffset(o.Reference, p.Source.InnerHeight, p.Source.OuterHeight);
                double min = double.MaxValue, max = double.MinValue;
                List<string> issues = new List<string>();
                CoverRow row = new CoverRow { Pipe = p };
                // The least clear distance to each crossing pipe along this pipe (a skewed one spans several stations).
                Dictionary<RouteCrossing, double> leastClear = new Dictionary<RouteCrossing, double>();
                (double shortfall, string zone, double required) worstCover = (0, null, 0);
                for (int r = sec.FirstRecord; r <= sec.LastRecord; r++)
                {
                    RouteRecord rec = route.Records[r];
                    if (rec.S < p.S0 - 1e-6 || rec.S > p.S1 + 1e-6) continue;
                    double cover = rec.Ground - (p.ZAt(rec.S) + off);
                    min = Math.Min(min, cover);
                    max = Math.Max(max, cover);
                    CoverZone zone = route.ZoneAt(rec.S);
                    double required = zone?.MinCover ?? o.MinCover;
                    if (required - cover > worstCover.shortfall + Tolerance) worstCover = (required - cover, zone?.Label, required);

                    foreach (CrossingLevel level in rec.Crossings)
                    {
                        if (level.Crossing.ClearanceOr(o.CrossingClearance) <= 0) continue;
                        // Clear distance outside to outside, whichever side the pipe passes.
                        double half = p.Source.OuterHeight / 2.0, z = p.ZAt(rec.S);
                        double clear = Math.Max(level.Bottom - (z + half), (z - half) - level.Top);
                        if (!leastClear.TryGetValue(level.Crossing, out double least) || clear < least) leastClear[level.Crossing] = clear;
                    }
                }
                foreach (KeyValuePair<RouteCrossing, double> kv in leastClear)
                {
                    double full = kv.Key.ClearanceOr(o.CrossingClearance), slab = rules.ProtectionMinClearance;
                    if (kv.Value >= full - Tolerance) continue;
                    if (kv.Value < 0) issues.Add($"Clashes with {kv.Key.Name}");
                    else if (slab > 0 && kv.Value >= slab - Tolerance)
                        row.Flags.Add($"Protection slab / HDD at {kv.Key.Name}: {kv.Value:0.00} m clear (full clearance {full:0.00} m)");
                    else issues.Add($"Only {kv.Value:0.00} m clear of {kv.Key.Name} (needs {full:0.00} m)");
                }

                if (worstCover.zone != null) issues.Add($"Cover below the {worstCover.zone} minimum ({worstCover.required - worstCover.shortfall:0.000} m < {worstCover.required:0.00} m)");
                else if (min < o.MinCover - Tolerance) issues.Add($"Cover below min ({min:0.00} m)");
                if (max > o.MaxCover + Tolerance) issues.Add($"Cover above max ({max:0.00} m)");

                (double minSlope, double maxSlope) = rules.SlopesFor(sec.InnerHeight, o.MinSlope, o.MaxSlope);
                double slope = p.Slope;
                if (o.Mode == CoverMode.Gravity)
                {
                    if (slope > -minSlope + 1e-6) issues.Add(slope > 1e-6 ? "Adverse slope" : "Slope below min");
                }
                else if (minSlope > 0 && Math.Abs(slope) < minSlope - 1e-6)
                {
                    issues.Add("Slope below min");
                }
                if (Math.Abs(slope) > maxSlope + 1e-6) issues.Add("Slope above max");

                row.MinCover = min == double.MaxValue ? double.NaN : min;
                row.MaxCover = max == double.MinValue ? double.NaN : max;
                row.Status = string.Join("; ", issues);
                row.Failed = issues.Count > 0;
                rows.Add(row);
            }

            CheckJoints(route, rows, o, rules);
            CheckPulls(route, rows, o, rules);
            foreach (ProximityWarning w in route.Proximities)
            {
                string flag = $"{w.Name} runs alongside {w.MinEdgeDistance:0.00} m away (needs {w.Required:0.00} m) from {w.S0:0.0} to {w.S1:0.0}";
                foreach (CoverRow row in rows) if (row.Pipe.S0 <= w.S1 + 1e-6 && row.Pipe.S1 >= w.S0 - 1e-6) row.Flags.Add(flag);
            }

            foreach (CoverRow row in rows)
            {
                string flags = string.Join("; ", row.Flags.Distinct());
                row.Status = row.Status.Length == 0 ? (flags.Length == 0 ? "OK" : "OK; " + flags) : (flags.Length == 0 ? row.Status : row.Status + "; " + flags);
            }
            return rows;
        }

        /// <summary>
        /// Between consecutive pipes: at a manhole the junction rule and the drops (backdrop flagged); at a plain joint of
        /// a dry utility the minimum bend radius.
        /// </summary>
        private static void CheckJoints(CoverRoute route, List<CoverRow> rows, CoverOptions o, CoverRules rules)
        {
            JunctionMatchRule rule = rules.JunctionFor(o.Mode);
            for (int i = 1; i < rows.Count; i++)
            {
                ProfilePipe a = rows[i - 1].Pipe, b = rows[i].Pipe;
                RouteSection secB = route.Sections[b.Section];
                bool manhole = a.Section != b.Section && secB.AfterManhole;
                if (manhole)
                {
                    double dIn = a.Source.InnerHeight, dOut = b.Source.InnerHeight;
                    if (rule == JunctionMatchRule.None)
                    {
                        double step = rules.FreeStepFor(o.Mode), change = (b.Z0 - dOut / 2.0) - (a.Z1 - dIn / 2.0);
                        if (Math.Abs(change) > rules.FreeStepTolerance(o.Mode) + 1e-6) rows[i].Issue($"Invert steps {change:+0.00;-0.00} m through the structure, more than ±{step:0.00} m");
                        continue;
                    }
                    double matched = CoverRules.MaxOutletCentre(rule, a.Z1, dIn, dOut);
                    double minDrop = rules.MinDropFor(o.Mode, secB.DeflectionIn);
                    if (b.Z0 > matched - minDrop + 1e-6)
                        rows[i].Issue(minDrop > 0 && b.Z0 <= matched + 1e-6
                            ? $"Drop at the manhole is less than the minimum {minDrop:0.00} m"
                            : $"Outlet above the inlet ({Describe(rule)})");
                    double drop = (a.Z1 - dIn / 2.0) - (b.Z0 - dOut / 2.0);
                    double belowMatched = matched - minDrop - b.Z0;
                    if (rules.Backdrop != BackdropMode.UnrestrictedDrop && belowMatched > o.MaxDrop + 1e-6)
                    {
                        if (rules.Backdrop == BackdropMode.CostedBackdropWithFlag && drop >= rules.MinBackdropHeight - 1e-6 && drop <= rules.MaxBackdropDrop + 1e-6)
                            rows[i].Flags.Add($"Backdrop manhole required (drop = {drop:0.00} m)");
                        else rows[i].Issue($"Drop at the manhole {drop:0.00} m is more than allowed");
                    }
                }
                else
                {
                    double radius = rules.BendRadiusFor(o.Mode, secB.InnerHeight);
                    if (radius <= 0) continue;
                    double allowed = Math.Min(a.Length, b.Length) / radius;
                    double change = Math.Abs(b.Slope - a.Slope);
                    if (change > allowed + 1e-6) rows[i].Issue($"Bend sharper than the {radius:0.0} m minimum radius");
                }
            }
        }

        /// <summary>Dry utilities: each pull's length and total bending (plan + profile) against the limits.</summary>
        private static void CheckPulls(CoverRoute route, List<CoverRow> rows, CoverOptions o, CoverRules rules)
        {
            double budget = rules.BendBudgetFor(o.Mode), maxSpan = rules.MaxSpanFor(o.Mode);
            if (budget <= 0 && maxSpan <= 0) return;
            foreach ((int first, int last, double length, double planTurn) in route.Spans())
            {
                List<CoverRow> pull = rows.Where(r => r.Pipe.Section >= first && r.Pipe.Section <= last).ToList();
                if (pull.Count == 0) continue;
                if (maxSpan > 0 && length > maxSpan + 1e-6)
                    foreach (CoverRow r in pull) r.Issue($"Pull {length:0.0} m between structures is longer than {maxSpan:0.0} m");
                if (budget <= 0) continue;
                double vertical = 0;
                for (int i = 1; i < pull.Count; i++)
                    vertical += Math.Abs(Math.Atan(pull[i].Pipe.Slope) - Math.Atan(pull[i - 1].Pipe.Slope)) * 180.0 / Math.PI;
                if (planTurn + vertical > budget + 1e-6)
                    foreach (CoverRow r in pull) r.Issue($"Pull bends {planTurn + vertical:0}° in all (plan {planTurn:0}°), more than {budget:0}°");
            }
        }

        private static void Issue(this CoverRow row, string issue)
        {
            row.Status = row.Status.Length == 0 ? issue : row.Status + "; " + issue;
            row.Failed = true;
        }

        private static string Describe(JunctionMatchRule rule)
        {
            switch (rule)
            {
                case JunctionMatchRule.CrownToCrown: return "crowns";
                case JunctionMatchRule.InvertToInvert: return "inverts";
                case JunctionMatchRule.Match08Depth: return "0.8 depth";
                default: return "crowns and inverts";
            }
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
