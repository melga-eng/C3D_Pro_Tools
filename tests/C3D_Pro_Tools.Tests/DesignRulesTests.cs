using System.Collections.Generic;
using System.Linq;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>The Stage 3 design rules: junctions, drops and backdrops, slopes by size, cover zones, dry utility bends and pulls, clearances.</summary>
    public class DesignRulesTests
    {
        private static (CoverSolution solution, List<CoverRow> rows) Solve(CoverRoute route, CoverOptions o)
        {
            CoverSolution solution = new CoverSolver(route, o).Solve();
            Assert.NotNull(solution);
            return (solution, CoverEvaluation.Evaluate(route, solution.Pipes, o));
        }

        // ---------------- Junction rules (formulas) ----------------

        [Fact]
        public void MaxOutletCentre_FollowsEachRule()
        {
            // Inlet centreline 100.0, DN300 in, DN600 out: invert in 99.85, crown in 100.15.
            Assert.Equal(99.85 + 0.30, CoverRules.MaxOutletCentre(JunctionMatchRule.InvertToInvert, 100.0, 0.3, 0.6), 9);
            Assert.Equal(100.15 - 0.30, CoverRules.MaxOutletCentre(JunctionMatchRule.CrownToCrown, 100.0, 0.3, 0.6), 9);
            Assert.Equal(99.85 + 0.8 * (0.3 - 0.6) + 0.30, CoverRules.MaxOutletCentre(JunctionMatchRule.Match08Depth, 100.0, 0.3, 0.6), 9);
            Assert.Equal(99.85, CoverRules.MaxOutletCentre(JunctionMatchRule.StrictCrownAndInvert, 100.0, 0.3, 0.6), 9);
            Assert.True(double.IsPositiveInfinity(CoverRules.MaxOutletCentre(JunctionMatchRule.None, 100.0, 0.3, 0.6)));
        }

        [Fact]
        public void MinInletCentre_IsTheInverseOfTheRule()
        {
            foreach (JunctionMatchRule rule in new[] { JunctionMatchRule.CrownToCrown, JunctionMatchRule.Match08Depth, JunctionMatchRule.InvertToInvert, JunctionMatchRule.StrictCrownAndInvert })
            {
                double zIn = CoverRules.MinInletCentre(rule, 97.5, 0.3, 0.6, 0.03);
                Assert.Equal(97.5, CoverRules.MaxOutletCentre(rule, zIn, 0.3, 0.6) - 0.03, 9);
            }
            Assert.True(double.IsNaN(CoverRules.MinInletCentre(JunctionMatchRule.None, 97.5, 0.3, 0.6, 0)));
        }

        [Fact]
        public void MinDrop_ByTurn_AndNoneForDryUtilities()
        {
            CoverRules r = new CoverRules();
            Assert.Equal(0.02, r.MinDropFor(CoverMode.Gravity, 0), 9);
            Assert.Equal(0.02, r.MinDropFor(CoverMode.Gravity, 15), 9);
            Assert.Equal(0.03, r.MinDropFor(CoverMode.Gravity, 30), 9);
            Assert.Equal(0.05, r.MinDropFor(CoverMode.Gravity, 90), 9);
            Assert.Equal(0.0, r.MinDropFor(CoverMode.Dry, 90), 9);
            Assert.Equal(JunctionMatchRule.None, r.JunctionFor(CoverMode.Dry));
        }

        [Fact]
        public void Gravity_ManholeOnATurn_DropsAtLeastTheTurnsMinimum()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 50 }, new SectionSpec { Length = 50, ManholeBefore = true } }, s => 100.0 - 0.01 * s);
            route.Sections[1].DeflectionIn = 60;
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            ProfilePipe inlet = solution.Pipes.FindLast(p => p.Section == 0), outlet = solution.Pipes.Find(p => p.Section == 1);
            Assert.True(inlet.Z1 - outlet.Z0 >= 0.05 - 1e-9, $"drop {inlet.Z1 - outlet.Z0:0.000}");
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
        }

        // ---------------- Backdrops ----------------

        /// <summary>
        /// The inlet starts fixed at 99.00 and can fall at most 10 % over its 10 m (to 98.00); 2 m past the manhole the
        /// outlet must pass 0.30 m under a pipe whose bottom is at 96.20, so the outlet starts at 95.92 at most. A standard
        /// drop (0.02 + 0.50 m) cannot reach that; a backdrop (1 to 6 m) can.
        /// </summary>
        private static CoverRoute ObstacleRoute()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 10 }, new SectionSpec { Length = 30, ManholeBefore = true } }, s => 100.0, startZ: 99.0);
            Routes.AddCrossing(route, "Culvert", 12, 96.5, 96.2);
            return route;
        }

        private static CoverOptions ObstacleOptions(BackdropMode mode)
        {
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            o.MaxCover = 5.0;
            o.StartFixed = true;
            o.CrossingClearance = 0.3;
            o.Rules.Backdrop = mode;
            return o;
        }

        [Fact]
        public void Backdrop_Disallowed_NoLayout()
        {
            Assert.Null(new CoverSolver(ObstacleRoute(), ObstacleOptions(BackdropMode.Disallow)).Solve());
        }

        [Fact]
        public void Backdrop_Costed_IsUsedOnlyWhenNeededAndFlagged()
        {
            CoverOptions o = ObstacleOptions(BackdropMode.CostedBackdropWithFlag);
            (CoverSolution solution, List<CoverRow> rows) = Solve(ObstacleRoute(), o);
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.True(solution.UsedBackdrops);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            CoverRow outlet = rows.First(r => r.Pipe.Section == 1);
            Assert.Contains(outlet.Flags, f => f.StartsWith("Backdrop manhole required (drop = "));
            ProfilePipe inPipe = solution.Pipes.FindLast(p => p.Section == 0), outPipe = solution.Pipes.Find(p => p.Section == 1);
            double drop = inPipe.Z1 - outPipe.Z0;
            Assert.InRange(drop, 1.0 - 1e-9, 6.0 + 1e-9);

            // Where a standard drop does, no backdrop is used.
            (CoverSolution gentle, _) = Solve(Routes.Build(new[] { new SectionSpec { Length = 40 }, new SectionSpec { Length = 40, ManholeBefore = true } }, s => 100.0 - 0.01 * s), o);
            Assert.False(gentle.UsedBackdrops);
        }

        [Fact]
        public void Backdrop_Unrestricted_AnyDropAndNoFlag()
        {
            (CoverSolution solution, List<CoverRow> rows) = Solve(ObstacleRoute(), ObstacleOptions(BackdropMode.UnrestrictedDrop));
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.All(rows, r => Assert.Empty(r.Flags));
        }

        // ---------------- Slopes by size ----------------

        [Fact]
        public void SlopeTable_RowOfTheLargestDiameterNotAbovethePipe()
        {
            CoverRules r = new CoverRules
            {
                Slopes = SlopeSource.Table,
                SlopeTable = new List<SlopeRow>
                {
                    new SlopeRow { DiameterMm = 150, MinSlopePercent = 1.0 },
                    new SlopeRow { DiameterMm = 300, MinSlopePercent = 0.5, MaxSlopePercent = 8 },
                    new SlopeRow { DiameterMm = 600, MinSlopePercent = 0.2 }
                }
            };
            Assert.Equal((0.010, 0.10), r.SlopesFor(0.100, 0.003, 0.10));
            Assert.Equal((0.005, 0.08), r.SlopesFor(0.300, 0.003, 0.10));
            Assert.Equal((0.005, 0.08), r.SlopesFor(0.450, 0.003, 0.10));
            Assert.Equal((0.002, 0.10), r.SlopesFor(0.900, 0.003, 0.10));
            Assert.Equal(1.0 / 300, new CoverRules { Slopes = SlopeSource.OneInDiameter }.SlopesFor(0.300, 0.001, 0.10).min, 12);
        }

        [Fact]
        public void SlopeTable_IsKeptByTheSolver()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 120, Inner = 0.3, Outer = 0.36 } }, s => 100.0 - 0.004 * s);
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            o.MaxCover = 4.0;
            o.Rules.Slopes = SlopeSource.Table;
            o.Rules.SlopeTable = new List<SlopeRow> { new SlopeRow { DiameterMm = 300, MinSlopePercent = 0.8 } };
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);
            Assert.All(solution.Pipes, p => Assert.True(p.Slope <= -0.008 + 1e-9, $"{p.Slope:0.0000}"));
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
        }

        // ---------------- Cover zones ----------------

        [Fact]
        public void Zones_FromPolygons_CoverTheirStretchWithMargin()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 100 } }, s => 100.0);
            var plan = route.Records.Select(r => (r.S, 0.0)).ToList();
            var paving = new List<(double X, double Y)> { (40.5, -5), (60.5, -5), (60.5, 5), (40.5, 5) };
            route.AddZones(plan, new[] { paving }, 0.64, "Paved");
            CoverZone z = Assert.Single(route.Zones);
            Assert.Equal(40.0, z.S0, 9);
            Assert.Equal(61.0, z.S1, 9);
            Assert.Equal(0.64, route.MinCoverAt(50, 0.6), 9);
            Assert.Equal(0.6, route.MinCoverAt(70, 0.6), 9);
        }

        [Fact]
        public void Zones_AreKeptByTheSolverAndChecked()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 100 } }, s => 100.0);
            route.Zones.Add(new CoverZone { S0 = 40, S1 = 60, MinCover = 1.0, Label = "Taxiway" });
            CoverOptions o = Routes.Options(CoverMode.Dry);
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);
            foreach (RouteRecord rec in route.Records.Where(r => r.S >= 40 && r.S <= 60))
            {
                ProfilePipe p = solution.Pipes.Find(x => x.S0 <= rec.S + 1e-9 && x.S1 >= rec.S - 1e-9);
                Assert.True(rec.Ground - (p.ZAt(rec.S) + 0.18) >= 1.0 + 0.002 - 1e-9);
            }
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));

            // A flat pipe at 0.70 m cover breaks the taxiway's 1.00 m.
            ProfilePipe flat = new ProfilePipe { Section = 0, S0 = 0, S1 = 100, Z0 = 99.12, Z1 = 99.12, Name = "P", Source = route.Pipes[0] };
            CoverRow row = CoverEvaluation.Evaluate(route, new List<ProfilePipe> { flat }, o)[0];
            Assert.True(row.Failed);
            Assert.Contains("below the Taxiway minimum", row.Status);
        }

        // ---------------- Dry utilities: bend radius, bend budget, pull length ----------------

        [Fact]
        public void BendRadius_EveryJointWithinIt()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 150 } }, s => 100.0 + 1.0 * Math.Sin(s / 12.0));
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 3.0;
            o.MinLength = 6;
            o.Rules.MinBendRadius = 400;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);
            for (int i = 1; i < solution.Pipes.Count; i++)
            {
                ProfilePipe a = solution.Pipes[i - 1], b = solution.Pipes[i];
                Assert.True(Math.Abs(b.Slope - a.Slope) <= Math.Min(a.Length, b.Length) / 400 + 1e-9);
            }
            Assert.DoesNotContain(rows, r => r.Status.Contains("minimum radius"));
        }

        [Fact]
        public void BendBudget_TotalVerticalBendingOfAPullWithinIt()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 150 } }, s => 100.0 + 1.0 * Math.Sin(s / 12.0));
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 3.0;
            o.Rules.BendBudgetDeg = 6;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);
            double turned = 0;
            for (int i = 1; i < solution.Pipes.Count; i++)
                turned += Math.Abs(Math.Atan(solution.Pipes[i].Slope) - Math.Atan(solution.Pipes[i - 1].Slope)) * 180 / Math.PI;
            Assert.True(turned <= 6 + 1e-6, $"{turned:0.00}°");
            Assert.DoesNotContain(rows, r => r.Status.Contains("Pull bends"));
        }

        [Fact]
        public void Spans_SumPlanTurningBetweenStructures()
        {
            CoverRoute route = Routes.Build(new[]
            {
                new SectionSpec { Length = 30 }, new SectionSpec { Length = 30 }, new SectionSpec { Length = 30, ManholeBefore = true }
            }, s => 100.0);
            route.Sections[1].DeflectionIn = 20;
            route.Sections[2].DeflectionIn = 45;  // at the manhole: not part of either pull
            route.Sections[2].PlanSweep = 10;
            var spans = route.Spans();
            Assert.Equal(2, spans.Count);
            Assert.Equal((0, 1, 60.0, 20.0), spans[0]);
            Assert.Equal((2, 2, 30.0, 10.0), spans[1]);
        }

        [Fact]
        public void PullLongerThanTheMaximum_IsFlagged()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 200 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            (_, List<CoverRow> rows) = Solve(route, o);
            Assert.All(rows, r => Assert.Contains("Pull 200.0 m between structures is longer than 150.0 m", r.Status));
            o.Rules.MaxStructureSpan = 0;
            (_, rows) = Solve(route, o);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
        }

        // ---------------- Clearances ----------------

        [Theory]
        [InlineData("ICT-01 Duct Bank", false, (int)UtilityClass.DryIct)]
        [InlineData("MV Power", false, (int)UtilityClass.DryPower)]
        [InlineData("Foul Sewer", false, (int)UtilityClass.WetGravity)]
        [InlineData("Potable Water Main", true, (int)UtilityClass.WetPressure)]
        [InlineData("Network - (3)", true, (int)UtilityClass.WetPressure)]
        [InlineData("Network - (3)", false, (int)UtilityClass.Unknown)]
        public void Classify_ByNetworkName(string name, bool pressure, int expected)
        {
            Assert.Equal((UtilityClass)expected, new CoverRules().Classify(name, pressure));
        }

        [Fact]
        public void ClearanceMatrix_DryToDryCloserThanWet()
        {
            CoverRules r = new CoverRules();
            Assert.Equal(0.30, r.ClearanceBetween(UtilityClass.DryIct, UtilityClass.DryPower, 0.1), 9);
            Assert.Equal(0.50, r.ClearanceBetween(UtilityClass.DryIct, UtilityClass.WetGravity, 0.1), 9);
            Assert.Equal(0.50, r.ClearanceBetween(UtilityClass.WetPressure, UtilityClass.WetGravity, 0.1), 9);
            Assert.Equal(0.50, r.ClearanceBetween(UtilityClass.DryPower, UtilityClass.Unknown, 0.1), 9);
            r.UseClearanceMatrix = false;
            Assert.Equal(0.1, r.ClearanceBetween(UtilityClass.DryIct, UtilityClass.DryIct, 0.1), 9);
        }

        [Fact]
        public void ProtectionSlab_UsedOnlyWhenTheFullClearanceCannotBeKept_AndFlagged()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 60 } }, s => 100.0);
            // A shallow water main: passing under with the full 0.50 m breaks the 1.65 m maximum cover, over it the minimum.
            RouteCrossing c = Routes.AddCrossing(route, "Water", 30.5, 99.0, 98.7);
            c.Clearance = 0.50;
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 1.65;
            o.Rules.ProtectionMinClearance = 0.30;
            (CoverSolution solution, List<CoverRow> rows) = Solve(route, o);

            Assert.True(solution.UsedProtectionClearance);
            Assert.Equal(0.0, solution.Relaxation, 6);
            Assert.All(rows, r => Assert.False(r.Failed, r.Status));
            Assert.Contains(rows, r => r.Flags.Any(f => f.StartsWith("Protection slab / HDD at Water")));

            o.Rules.ProtectionMinClearance = 0;
            (CoverSolution strict, _) = Solve(route, o);
            Assert.False(strict.UsedProtectionClearance);
            Assert.True(strict.Relaxation > 0.001, "without the slab option the cover limits have to give");
        }
    }
}
