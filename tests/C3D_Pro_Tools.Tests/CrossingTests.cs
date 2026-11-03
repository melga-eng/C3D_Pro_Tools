using System.Collections.Generic;
using System.Linq;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>
    /// Crossings the route must clear: several at one station, shallow-skew crossings (once dropped below 15°) and pipes
    /// running alongside within reach in plan.
    /// </summary>
    public class CrossingTests
    {
        /// <summary>A straight route along +X from the origin; record plan positions are (station, 0).</summary>
        private static (CoverRoute route, List<(double X, double Y)> plan) StraightRoute(double length = 100)
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = length } }, s => 100.0);
            return (route, route.Records.Select(r => (r.S, 0.0)).ToList());
        }

        private static List<(double X, double Y, double Z)> Line(double x0, double y0, double x1, double y1, double z) =>
            new List<(double, double, double)> { (x0, y0, z), (x1, y1, z) };

        [Fact]
        public void Closest_ProjectsOntoTheNearestSegment()
        {
            var path = new List<(double X, double Y, double Z)> { (0, 0, 10), (10, 0, 20), (10, 10, 20) };
            (double d, double z, double f) = CrossingGeometry.Closest(path, 4, 3);
            Assert.Equal(3.0, d, 9);
            Assert.Equal(14.0, z, 9);
            Assert.Equal(0.2, f, 9);
        }

        [Fact]
        public void TwoCrossingsAtOneStation_AreBothCleared()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 60 } }, s => 100.0);
            // A deep pipe, then a shallower one at the same station: the route has to go under both.
            Routes.AddCrossing(route, "Deep", 30, 98.2, 98.0);
            Routes.AddCrossing(route, "Shallow", 30, 99.1, 98.9);
            Assert.Equal(2, route.Records.Single(r => Math.Abs(r.S - 30) < 1e-9).Crossings.Count);

            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.CrossingClearance = 0.3;
            CoverSolution solution = new CoverSolver(route, o).Solve();
            List<CoverRow> rows = CoverEvaluation.Evaluate(route, solution.Pipes, o);

            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            ProfilePipe p = solution.Pipes.Find(x => x.S0 <= 30 && x.S1 >= 30);
            Assert.True(98.0 - (p.ZAt(30) + 0.18) >= 0.3 - 1e-6, "clear of the deep pipe below");
        }

        [Fact]
        public void ShallowSkewCrossing_ClaimsEveryStationWhereTheOutsidesOverlap()
        {
            (CoverRoute route, var plan) = StraightRoute();
            double a = 10 * Math.PI / 180;
            var path = Line(50 - 30 * Math.Cos(a), -30 * Math.Sin(a), 50 + 30 * Math.Cos(a), 30 * Math.Sin(a), 98.5);
            List<RouteCrossing> ofThisPipe = new List<RouteCrossing>();

            route.MarkCrossingBands(plan, path, 0.25, 0.25, 0.0, ofThisPipe, () => new RouteCrossing { Name = "Skew" });

            // Reach 0.18 + 0.25 = 0.43 m across; at 10° that is |s - 50| < 0.43 / sin 10° = 2.48 m along the route.
            RouteCrossing c = Assert.Single(route.Crossings);
            Assert.Equal(50.0, c.S, 9);
            int[] marked = route.Records.Where(r => r.Crossings.Count > 0).Select(r => (int)Math.Round(r.S)).ToArray();
            Assert.Equal(new[] { 48, 49, 50, 51, 52 }, marked);
            Assert.All(route.Records.Where(r => r.Crossings.Count > 0), r => Assert.Equal(98.75, r.Crossings[0].Top, 9));
        }

        [Fact]
        public void PipeAlongside_WithinReach_IsClearedOverItsWholeLength()
        {
            (CoverRoute route, var plan) = StraightRoute();
            var path = Line(20, 0.3, 80, 0.3, 99.0);

            route.MarkCrossingBands(plan, path, 0.15, 0.15, 0.0, new List<RouteCrossing>(), () => new RouteCrossing { Name = "Alongside" });

            Assert.Single(route.Crossings);
            int[] marked = route.Records.Where(r => r.Crossings.Count > 0).Select(r => (int)Math.Round(r.S)).ToArray();
            // Its two ends are left out (a pipe ending by the route is a branch), the 58 stations between are claimed.
            Assert.Equal(Enumerable.Range(21, 59), marked);
        }

        [Fact]
        public void PipeAlongside_OutOfReach_IsIgnoredUnlessAHorizontalClearanceAsksForIt()
        {
            (CoverRoute route, var plan) = StraightRoute();
            var path = Line(20, 1.0, 80, 1.0, 99.0);

            route.MarkCrossingBands(plan, path, 0.15, 0.15, 0.0, new List<RouteCrossing>(), () => new RouteCrossing());
            Assert.Empty(route.Crossings);

            route.MarkCrossingBands(plan, path, 0.15, 0.15, 0.8, new List<RouteCrossing>(), () => new RouteCrossing());
            Assert.Single(route.Crossings);
        }

        [Fact]
        public void CrossingAlreadyFound_IsReusedForItsBand()
        {
            (CoverRoute route, var plan) = StraightRoute();
            RouteCrossing found = new RouteCrossing { Name = "X", S = 50, Top = 98.75, Bottom = 98.25 };
            route.Crossings.Add(found);
            var path = Line(50, -20, 50, 20, 98.5);

            route.MarkCrossingBands(plan, path, 0.25, 0.25, 0.0, new List<RouteCrossing> { found }, () => new RouteCrossing { Name = "new" });

            Assert.Same(found, Assert.Single(route.Crossings));
            Assert.Contains(route.Records, r => r.Crossings.Any(l => l.Crossing == found));
        }

        [Fact]
        public void ShallowSkewBand_IsClearedAtEveryStationOfIt()
        {
            (CoverRoute route, var plan) = StraightRoute(60);
            double a = 10 * Math.PI / 180;
            // Sloping, so its level differs across the band.
            var path = new List<(double X, double Y, double Z)> { (30 - 20 * Math.Cos(a), -20 * Math.Sin(a), 99.3), (30 + 20 * Math.Cos(a), 20 * Math.Sin(a), 98.7) };
            route.MarkCrossingBands(plan, path, 0.2, 0.2, 0.0, new List<RouteCrossing>(), () => new RouteCrossing { Name = "Skew" });

            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.CrossingClearance = 0.3;
            CoverSolution solution = new CoverSolver(route, o).Solve();
            List<CoverRow> rows = CoverEvaluation.Evaluate(route, solution.Pipes, o);

            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            foreach (RouteRecord rec in route.Records.Where(r => r.Crossings.Count > 0))
            {
                ProfilePipe p = solution.Pipes.Find(x => x.S0 <= rec.S + 1e-9 && x.S1 >= rec.S - 1e-9);
                double top = p.ZAt(rec.S) + 0.18;
                Assert.True(rec.Crossings[0].Bottom - top >= 0.3 - 1e-6, $"at {rec.S:0.0}: {rec.Crossings[0].Bottom - top:0.000} m clear");
            }
        }
    }
}
