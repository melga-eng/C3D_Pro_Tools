using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace C3DProTools.Tests.Helpers
{
    /// <summary>One straight section of a test route: its length, pipe size and whether a manhole sits at its start.</summary>
    internal sealed class SectionSpec
    {
        public double Length;
        public double Inner = 0.3, Outer = 0.36;
        public bool ManholeBefore;
    }

    /// <summary>
    /// Builds a <see cref="CoverRoute"/> without a drawing, the way CoverRoute.Build lays out records: a record every
    /// <c>spacing</c> metres plus one at each section end; breaks allowed only on interior records.
    /// </summary>
    internal static class Routes
    {
        public static CoverRoute Build(IList<SectionSpec> specs, Func<double, double> ground, double spacing = 1.0, double startZ = double.NaN)
        {
            CoverRoute route = new CoverRoute();
            double s = 0;
            for (int i = 0; i < specs.Count; i++)
            {
                SectionSpec spec = specs[i];
                RouteSection sec = new RouteSection
                {
                    Index = i, S0 = s, S1 = s + spec.Length, InnerHeight = spec.Inner, OuterHeight = spec.Outer, AfterManhole = i > 0 && spec.ManholeBefore
                };
                double z0 = double.IsNaN(startZ) ? ground(sec.S0) - 1.5 : startZ, z1 = double.IsNaN(startZ) ? ground(sec.S1) - 1.5 : startZ;
                RoutePipe pipe = new RoutePipe
                {
                    Name = $"P{i + 1}", Forward = true, Section = i, S0 = sec.S0, S1 = sec.S1, Z0 = z0, Z1 = z1, InnerHeight = spec.Inner, OuterHeight = spec.Outer
                };
                sec.Pipes.Add(pipe);
                route.Pipes.Add(pipe);
                route.Sections.Add(sec);

                sec.FirstRecord = route.Records.Count;
                int count = Math.Max(1, (int)Math.Ceiling(sec.Length / spacing - 1e-9));
                for (int k = 0; k <= count; k++)
                {
                    double st = k == count ? sec.S1 : sec.S0 + k * spacing;
                    route.Records.Add(new RouteRecord { S = st, Ground = ground(st), Section = i, CanBreak = k > 0 && k < count });
                }
                sec.LastRecord = route.Records.Count - 1;
                s = sec.S1;
            }
            return route;
        }

        /// <summary>Gives a crossing pipe its own record, as CoverRoute.FindCrossings does.</summary>
        public static RouteCrossing AddCrossing(CoverRoute route, string name, double s, double top, double bottom)
        {
            RouteCrossing c = new RouteCrossing { Name = name, S = s, Top = top, Bottom = bottom };
            route.Crossings.Add(c);
            RouteSection sec = route.Sections.Find(x => s >= x.S0 && s <= x.S1);
            int at = sec.FirstRecord;
            while (at <= sec.LastRecord && route.Records[at].S < s - 1e-6) at++;
            if (Math.Abs(route.Records[at].S - s) <= 1e-6) { route.Records[at].AddCrossing(c, top, bottom); return c; }
            RouteRecord before = route.Records[at - 1], after = route.Records[at];
            double t = (s - before.S) / (after.S - before.S);
            route.Records.Insert(at, new RouteRecord { S = s, Section = sec.Index, Ground = before.Ground + (after.Ground - before.Ground) * t });
            route.Records[at].AddCrossing(c, top, bottom);
            foreach (RouteSection other in route.Sections) if (other.Index > sec.Index) { other.FirstRecord++; other.LastRecord++; }
            sec.LastRecord++;
            return c;
        }

        public static CoverOptions Options(CoverMode mode) => new CoverOptions
        {
            Mode = mode,
            Reference = CoverReference.OutsideTop,
            MinCover = 0.6, MaxCover = 2.5, MinLength = 2.0,
            MinSlope = mode == CoverMode.Gravity ? 0.003 : 0.0, MaxSlope = 0.10, MaxSlopeChange = 0.0,
            MaxDrop = 0.5, MaxLength = 0.0, CrossingClearance = 0.0, PreferGentleBends = false
        };

        /// <summary>A stable text form of a solution for snapshots: one line per pipe.</summary>
        public static string Describe(CoverSolution solution, List<CoverRow> rows)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.Append("relaxation ").Append(solution.Relaxation.ToString("0.000", c)).Append('\n');
            for (int i = 0; i < solution.Pipes.Count; i++)
            {
                ProfilePipe p = solution.Pipes[i];
                CoverRow r = rows[i];
                sb.Append(string.Format(c, "sec {0} s {1:0.000}-{2:0.000} z {3:0.000}-{4:0.000} slope {5:0.0000} cover {6:0.000}-{7:0.000} {8}\n",
                    p.Section, p.S0, p.S1, p.Z0, p.Z1, p.Slope, r.MinCover, r.MaxCover, r.Status));
            }
            return sb.ToString();
        }
    }
}
