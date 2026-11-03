using System.Collections.Generic;
using System.Linq;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>Refinements of the Stage 3A rules: the dry pit step, the encasement envelope, zone arcs and the gravity slope preset.</summary>
    public class RuleRefinementTests
    {
        // ---------------- A. Dry pit step ----------------

        /// <summary>Two dry pulls meeting at a pit where the ground falls 1.5 m.</summary>
        private static CoverRoute CliffAtPit() =>
            Routes.Build(new[] { new SectionSpec { Length = 20 }, new SectionSpec { Length = 20, ManholeBefore = true } }, s => s < 20 ? 100.0 : 98.5);

        private static double StepAt(CoverSolution solution)
        {
            ProfilePipe a = solution.Pipes.FindLast(p => p.Section == 0), b = solution.Pipes.Find(p => p.Section == 1);
            return b.Z0 - a.Z1;
        }

        [Fact]
        public void DryFreeJunction_StepThroughThePit_IsBoundedBothWays()
        {
            CoverRoute route = CliffAtPit();
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 5.0;
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            Assert.True(Math.Abs(StepAt(solution)) <= 0.30 + 1e-9, $"step {StepAt(solution):0.000}");
            Assert.All(CoverEvaluation.Evaluate(route, solution.Pipes, o), r => Assert.False(r.Failed, r.Status));
        }

        [Fact]
        public void DryFreeJunction_ZeroStep_IsFlush()
        {
            CoverRoute route = CliffAtPit();
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 5.0;
            o.Rules.DryMaxStep = 0;
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            Assert.Equal(0.0, StepAt(solution), 9);
        }

        [Fact]
        public void DryFreeJunction_AJumpThroughOnePit_IsAnIssue()
        {
            CoverRoute route = CliffAtPit();
            CoverOptions o = Routes.Options(CoverMode.Dry);
            List<ProfilePipe> pipes = new List<ProfilePipe>
            {
                new ProfilePipe { Section = 0, S0 = 0, S1 = 20, Z0 = 99.0, Z1 = 99.0, Name = "A", Source = route.Pipes[0] },
                new ProfilePipe { Section = 1, S0 = 20, S1 = 40, Z0 = 97.5, Z1 = 97.5, Name = "B", Source = route.Pipes[1] }
            };
            List<CoverRow> rows = CoverEvaluation.Evaluate(route, pipes, o);
            Assert.True(rows[1].Failed);
            Assert.Contains("Invert steps -1.50 m through the structure, more than ±0.30 m", rows[1].Status);

            // Gravity routes keep their own rules; a free gravity junction is not limited by the dry step.
            Assert.True(double.IsPositiveInfinity(new CoverRules().FreeStepFor(CoverMode.Gravity)));
        }

        // ---------------- B. Encasement envelope ----------------

        [Fact]
        public void Encasement_IsPartOfTheRoutesOutside()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20, Inner = 0.10, Outer = 0.12 } }, s => 100.0);
            route.ApplyEncasement(0.10);
            Assert.Equal(0.32, route.Sections[0].OuterHeight, 9);
            Assert.Equal(0.32, route.Sections[0].PlanWidth, 9);
            Assert.Equal(0.32, route.Pipes[0].OuterHeight, 9);

            // Cover is to the top of the encasement: 0.60 m from the ground to it.
            CoverOptions o = Routes.Options(CoverMode.Dry);
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            Assert.All(solution.Pipes, p => Assert.True(100.0 - (Math.Max(p.Z0, p.Z1) + 0.16) >= 0.60 - 1e-9));
        }

        [Fact]
        public void Encasement_ClearanceIsTheSpecificationsNumber_FaceToFace()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 40, Inner = 0.10, Outer = 0.12 } }, s => 100.0);
            route.ApplyEncasement(0.10);
            RouteCrossing c = Routes.AddCrossing(route, "Water", 20, 99.10, 98.90);
            c.Clearance = 0.30;
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 3.0;
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            double z = solution.Pipes.Find(p => p.S0 <= 20 && p.S1 >= 20).ZAt(20);
            // Below it: the encasement's top (centre + 0.16) is 0.30 m under the pipe's bottom.
            Assert.True(98.90 - (z + 0.16) >= 0.30 - 1e-9, $"gap {98.90 - (z + 0.16):0.000}");

            // A pipe 0.25 m clear of the encasement is reported against 0.30 m, not 0.30 m + the encasement.
            ProfilePipe tooClose = new ProfilePipe { Section = 0, S0 = 0, S1 = 40, Z0 = 98.49, Z1 = 98.49, Name = "P", Source = route.Pipes[0] };
            CoverRow row = CoverEvaluation.Evaluate(route, new List<ProfilePipe> { tooClose }, o)[0];
            Assert.True(row.Failed);
            Assert.Contains("0.30", row.Status);
            Assert.DoesNotContain("0.40", row.Status);
        }

        [Fact]
        public void Encasement_WidensThePlanEnvelope_ForCrossingBands()
        {
            // A pipe 0.30 m from the route's centreline, alongside: clear of a 0.12 m conduit, but inside 0.10 m of encasement
            // on each side (half width 0.16) plus its own half width 0.15.
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 40, Inner = 0.10, Outer = 0.12 } }, s => 100.0);
            List<(double X, double Y)> plan = route.Records.ConvertAll(r => (r.S, 0.0));
            List<(double X, double Y, double Z)> path = new List<(double X, double Y, double Z)> { (10, 0.30, 99.0), (30, 0.30, 99.0) };

            CoverRoute bare = Routes.Build(new[] { new SectionSpec { Length = 40, Inner = 0.10, Outer = 0.12 } }, s => 100.0);
            bare.MarkCrossingBands(plan, path, 0.15, 0.15, 0.0, new List<RouteCrossing>(), () => new RouteCrossing { Name = "W" });
            Assert.Empty(bare.Crossings);

            route.ApplyEncasement(0.10);
            route.MarkCrossingBands(plan, path, 0.15, 0.15, 0.0, new List<RouteCrossing>(), () => new RouteCrossing { Name = "W" });
            Assert.Single(route.Crossings);
        }

        // ---------------- C. Zone arcs ----------------

        [Fact]
        public void ZoneOutline_ArcEdges_AreFollowedWithinFiftyMillimetres()
        {
            // A 20 × 20 square whose top edge bulges up into a half circle of radius 10 (centre (10, 20)).
            var vertices = new List<(double X, double Y, double Bulge)> { (0, 0, 0), (20, 0, 0), (20, 20, 1.0), (0, 20, 0) };
            List<(double X, double Y)> outline = PlanGeometry.Densify(vertices, true, 0.05);
            // The half circle takes 16 chords (15 points between its ends) to stay within 50 mm.
            Assert.Equal(4 + 15, outline.Count);
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                if (a.Y < 20 - 1e-9 || b.Y < 20 - 1e-9) continue;
                double mx = (a.X + b.X) / 2 - 10, my = (a.Y + b.Y) / 2 - 20;
                Assert.True(10 - Math.Sqrt(mx * mx + my * my) <= 0.05 + 1e-9);
            }

            // A route crossing the bulge near its top is in the zone; with the chord alone it would not be.
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            List<(double X, double Y)> plan = route.Records.ConvertAll(r => (r.S, 29.0));
            route.AddZones(plan, new[] { outline }, 0.64, "Paved");
            Assert.Single(route.Zones);
            CoverRoute chordOnly = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            chordOnly.AddZones(plan, new[] { vertices.ConvertAll(v => (v.X, v.Y)) }, 0.64, "Paved");
            Assert.Empty(chordOnly.Zones);
        }

        [Fact]
        public void ArcPoints_BulgeSign_TurnsTheRightWay()
        {
            // From (0,0) to (2,0): bulge +1 is a counter-clockwise half circle, below the chord; −1 above it.
            Assert.True(PlanGeometry.ArcPoints(0, 0, 2, 0, 1.0).Take(3).All(p => p.Y < 0));
            Assert.True(PlanGeometry.ArcPoints(0, 0, 2, 0, -1.0).Take(3).All(p => p.Y > 0));
            Assert.Equal((2.0, 0.0), PlanGeometry.ArcPoints(0, 0, 2, 0, 0.5).Last());
            Assert.Single(PlanGeometry.ArcPoints(0, 0, 2, 0, 0));
        }

        // ---------------- D. Gravity slope preset ----------------

        [Fact]
        public void GravityPreset_HasTheTenRows_AndDrivesTheSlopes()
        {
            List<SlopeRow> preset = CoverRules.GravityPresetV075();
            Assert.Equal(new double[] { 150, 200, 250, 300, 400, 500, 600, 800, 1000, 1200 }, preset.Select(r => r.DiameterMm));
            Assert.Equal(new[] { 0.65, 0.45, 0.35, 0.28, 0.20, 0.15, 0.12, 0.09, 0.07, 0.06 }, preset.Select(r => r.MinSlopePercent));
            Assert.Equal(new[] { 10.0, 8.0, 8.0, 6.0, 5.0, 4.0, 3.5, 3.0, 2.5, 2.0 }, preset.Select(r => r.MaxSlopePercent));

            CoverRules rules = new CoverRules { Slopes = SlopeSource.Table, SlopeTable = preset };
            (double min, double max) = rules.SlopesFor(0.300, 0.003, 0.10);
            Assert.Equal(0.0028, min, 9);
            Assert.Equal(0.06, max, 9);
            // DN225 takes the DN200 row.
            Assert.Equal(0.0045, rules.SlopesFor(0.225, 0.003, 0.10).min, 9);
            // Not applied unless loaded.
            Assert.Equal(SlopeSource.Flat, new CoverRules().Slopes);
            Assert.Empty(new CoverRules().SlopeTable);
        }
    }
}
