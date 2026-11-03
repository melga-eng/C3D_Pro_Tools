using System.Collections.Generic;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>
    /// Baseline for the Profile &amp; Cover Designer solver. The invariant tests must keep passing through every change; the
    /// snapshots pin today's exact layouts so a rule change shows up as a reviewed diff, never silently.
    /// </summary>
    public class CoverSolverTests
    {
        private static (CoverSolution solution, List<CoverRow> rows) Solve(CoverRoute route, CoverOptions o)
        {
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            return (solution, CoverEvaluation.Evaluate(route, solution.Pipes, o));
        }

        private static void AssertContinuous(CoverRoute route, CoverSolution solution)
        {
            Assert.Equal(0.0, solution.Pipes[0].S0, 6);
            Assert.Equal(route.Length, solution.Pipes[solution.Pipes.Count - 1].S1, 6);
            for (int i = 1; i < solution.Pipes.Count; i++)
                Assert.Equal(solution.Pipes[i - 1].S1, solution.Pipes[i].S0, 6);
        }

        [Fact]
        public void Dry_FlatGround_OnePipeAtShallowestGridLevel()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 100 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            Assert.Single(solution.Pipes);
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            // Shallowest: outside top at 0.6 m cover is centre 100 - 0.6 - 0.18 = 99.22, rounded down to the 25 mm grid.
            ProfilePipe p = solution.Pipes[0];
            Assert.Equal(99.200, p.Z0, 6);
            Assert.Equal(99.200, p.Z1, 6);
        }

        [Fact]
        public void Dry_UndulatingGround_EveryPipeWithinCoverAndSlope()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 120 } }, s => 100.0 + 1.2 * Math.Sin(s / 15.0));
            CoverOptions o = Routes.Options(CoverMode.Dry);
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            AssertContinuous(route, solution);
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            Assert.All(solution.Pipes, p => Assert.True(p.Length >= o.MinLength - 1e-6, $"pipe {p.S0}-{p.S1} shorter than the minimum"));
            Snapshot.Match("dry_undulating", Routes.Describe(solution, rows));
        }

        [Fact]
        public void Gravity_FallingGround_AllPipesFallAtLeastMinSlope()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 80 }, new SectionSpec { Length = 60, ManholeBefore = true } },
                s => 100.0 - 0.02 * s + 0.4 * Math.Sin(s / 9.0));
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            AssertContinuous(route, solution);
            Assert.All(solution.Pipes, p => Assert.True(p.Slope <= -o.MinSlope + 1e-9, $"pipe {p.S0}-{p.S1} slope {p.Slope}"));
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            Snapshot.Match("gravity_falling", Routes.Describe(solution, rows));
        }

        [Fact]
        public void Gravity_AtManhole_OutletInvertNeverAboveInletAndDropLimited()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 50 }, new SectionSpec { Length = 50, ManholeBefore = true } }, s => 100.0 - 0.01 * s);
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            (CoverSolution solution, _) = Solve(route, o);

            ProfilePipe inlet = solution.Pipes.FindLast(p => p.Section == 0), outlet = solution.Pipes.Find(p => p.Section == 1);
            double invertIn = inlet.Z1 - 0.3 / 2, invertOut = outlet.Z0 - 0.3 / 2;
            Assert.True(invertOut <= invertIn + 1e-9);
            Assert.True(invertIn - invertOut <= o.MaxDrop + 1e-9);
        }

        private static CoverRoute SizeIncreaseRoute() => Routes.Build(new[]
        {
            new SectionSpec { Length = 40, Inner = 0.3, Outer = 0.36 },
            new SectionSpec { Length = 40, Inner = 0.6, Outer = 0.72, ManholeBefore = true }
        }, s => 100.0 - 0.01 * s);

        private static CoverOptions SizeIncreaseOptions(JunctionMatchRule rule)
        {
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            // Cover to the invert, so the outlet's own cover limit does not bind before the junction rule does.
            o.Reference = CoverReference.Invert;
            o.MinCover = 0.6; o.MaxCover = 4.0;
            o.Rules.GravityJunction = rule;
            return o;
        }

        /// <summary>Soffit (crown) matching is the gravity default: a larger outlet drops so its crown is not above the inlet's.</summary>
        [Fact]
        public void Gravity_SizeIncreaseAtManhole_CrownsMatchByDefault()
        {
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            Assert.Equal(JunctionMatchRule.CrownToCrown, o.Rules.GravityJunction);
            o = SizeIncreaseOptions(o.Rules.GravityJunction);
            (CoverSolution solution, List<CoverRow> rows) = Solve(SizeIncreaseRoute(), o);

            ProfilePipe inlet = solution.Pipes.FindLast(p => p.Section == 0), outlet = solution.Pipes.Find(p => p.Section == 1);
            double crownIn = inlet.Z1 + 0.15, crownOut = outlet.Z0 + 0.30;
            // The 0.02 m minimum drop of a straight-through manhole applies on top.
            Assert.True(crownOut <= crownIn - 0.02 + 1e-9, $"outlet crown {crownOut:0.000} vs inlet crown {crownIn:0.000}");
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
        }

        /// <summary>The old behaviour stays available: with invert matching a larger outlet's crown sits above the inlet's.</summary>
        [Fact]
        public void Gravity_SizeIncreaseAtManhole_InvertMatchingWhenChosen()
        {
            (CoverSolution solution, _) = Solve(SizeIncreaseRoute(), SizeIncreaseOptions(JunctionMatchRule.InvertToInvert));
            ProfilePipe inlet = solution.Pipes.FindLast(p => p.Section == 0), outlet = solution.Pipes.Find(p => p.Section == 1);
            Assert.True(outlet.Z0 - 0.30 <= inlet.Z1 - 0.15 - 0.02 + 1e-9);
            Assert.True(outlet.Z0 + 0.30 > inlet.Z1 + 0.15 + 1e-6);
        }

        [Theory]
        [InlineData((int)JunctionMatchRule.CrownToCrown)]
        [InlineData((int)JunctionMatchRule.Match08Depth)]
        [InlineData((int)JunctionMatchRule.InvertToInvert)]
        [InlineData((int)JunctionMatchRule.StrictCrownAndInvert)]
        public void Gravity_EveryJunctionRule_IsMetAtTheManhole(int ruleValue)
        {
            JunctionMatchRule rule = (JunctionMatchRule)ruleValue;
            (CoverSolution solution, List<CoverRow> rows) = Solve(SizeIncreaseRoute(), SizeIncreaseOptions(rule));
            ProfilePipe inlet = solution.Pipes.FindLast(p => p.Section == 0), outlet = solution.Pipes.Find(p => p.Section == 1);
            Assert.True(outlet.Z0 <= CoverRules.MaxOutletCentre(rule, inlet.Z1, 0.3, 0.6) - 0.02 + 1e-9);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
        }

        [Fact]
        public void Crossing_ClearanceIsKept()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 60 } }, s => 100.0);
            // A 0.5 m pipe crossing at 30.5 m with its top 0.9 m below ground: the route cannot pass over it within cover.
            Routes.AddCrossing(route, "X1", 30.5, 99.1, 98.6);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.CrossingClearance = 0.3;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            ProfilePipe p = solution.Pipes.Find(x => x.S0 <= 30.5 && x.S1 >= 30.5);
            double top = p.ZAt(30.5) + 0.18;
            Assert.True(98.6 - top >= 0.3 - 1e-6, $"clear distance {98.6 - top:0.000} m");
            Assert.True(route.Crossings[0].PassBelow);
            Snapshot.Match("dry_crossing", Routes.Describe(solution, rows));
        }

        [Fact]
        public void NoLayoutWithinLimits_LimitsWidenLocallyAndPipesAreFlagged()
        {
            // A 3 m deep, 6 m wide trench in the ground with a 2% slope limit: no layout meets the cover there.
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 60 } }, s => Math.Abs(s - 30) < 3 ? 97.0 : 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxSlope = 0.02;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            Assert.True(solution.Relaxation > 0.001);
            Assert.Contains(rows, r => r.Failed);
            AssertContinuous(route, solution);
        }

        [Fact]
        public void FixedStartAndEnd_AreKeptExactly()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 40 } }, s => 100.0, startZ: 98.613);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.StartFixed = o.EndFixed = true;
            (CoverSolution solution, _) = Solve(route, o);

            Assert.Equal(98.613, solution.Pipes[0].Z0, 9);
            Assert.Equal(98.613, solution.Pipes[solution.Pipes.Count - 1].Z1, 9);
        }

        /// <summary>
        /// A fine slope-change limit with steep slopes allowed needs thousands of slope buckets (0.6 / 0.001 = 600 here,
        /// 2400 with the 0.0005 case). The state key used to hold 10 bits, so every bucket past 1022 merged into one.
        /// </summary>
        [Theory]
        [InlineData(0.002)]
        [InlineData(0.0005)]
        public void FineSlopeChangeLimit_WithSteepSlopes_IsMetAtEveryJoint(double maxChange)
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 150 } }, s => 100.0 + 0.6 * Math.Sin(s / 20.0) + 0.02 * s);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxSlope = 0.30;
            o.MaxSlopeChange = maxChange;
            o.MinLength = 6;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            AssertContinuous(route, solution);
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            for (int i = 1; i < solution.Pipes.Count; i++)
                Assert.True(Math.Abs(solution.Pipes[i].Slope - solution.Pipes[i - 1].Slope) <= maxChange + 1e-9,
                    $"joint at {solution.Pipes[i].S0:0.0}: slope change {Math.Abs(solution.Pipes[i].Slope - solution.Pipes[i - 1].Slope):0.00000}");
        }

        [Fact]
        public void SlopeBuckets_StayDistinctAcrossTheWholeSlopeRange()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxSlope = 0.30;
            o.MaxSlopeChange = 0.0005;
            CoverSolver solver = new CoverSolver(route, o);

            // Bucket width 0.00025: -30 % .. +30 % needs 2400 buckets, each slope apart by more than one width in its own.
            int[] buckets = { solver.Bucket(-0.30), solver.Bucket(-0.05), solver.Bucket(0.0), solver.Bucket(0.10), solver.Bucket(0.30) };
            Assert.Equal(buckets.Length, buckets.Distinct().Count());
            Assert.True(solver.Bucket(0.10) > solver.Bucket(0.0));
            Assert.NotEqual(solver.Bucket(0.30), solver.Bucket(double.NaN));
        }

        [Fact]
        public void MaxLength_SplitsLongStraightSections()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 100 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxLength = 30;
            (CoverSolution solution, _) = Solve(route, o);

            Assert.True(solution.Pipes.Count >= 4);
            Assert.All(solution.Pipes, p => Assert.True(p.Length <= 30 + 1e-6));
        }
    }
}
