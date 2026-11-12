using System;
using System.Collections.Generic;
using C3DProTools;
using Xunit;

namespace C3D_Pro_Tools.Tests
{
    public class PavementZonesTests
    {
        [Fact]
        public void EdgeBandLeavesTheShoulderOut()
        {
            // A taxiway surface 0..100 m across (shoulders included); 16.5 m inside each edge is not taxiway for the cover check.
            bool Surface(double x, double y) => x >= 0 && x <= 100 && y >= -1000 && y <= 1000;
            Assert.False(PavementZones.InsideBy(Surface, 10, 0, 16.5));
            Assert.False(PavementZones.InsideBy(Surface, 16.2, 0, 16.5));
            Assert.True(PavementZones.InsideBy(Surface, 16.8, 0, 16.5));
            Assert.True(PavementZones.InsideBy(Surface, 50, 0, 16.5));
            Assert.False(PavementZones.InsideBy(Surface, 84, 0, 16.5));
            Assert.False(PavementZones.InsideBy(Surface, -5, 0, 16.5));
            Assert.True(PavementZones.InsideBy(Surface, 5, 0, 0));
        }

        [Fact]
        public void FindsEachEdgeToTheTolerance()
        {
            // Taxiway (1) from 20.37 to 55.81, road (2) from 55.81 to 70.12, unpaved elsewhere.
            int Kind(double s) => s >= 20.37 && s < 55.81 ? 1 : s >= 55.81 && s < 70.12 ? 2 : 0;
            List<PavementRun> runs = PavementZones.Runs(0, 100, 1.0, Kind);
            Assert.Equal(4, runs.Count);
            Assert.Equal(0, runs[0].Kind);
            Assert.Equal(1, runs[1].Kind);
            Assert.Equal(2, runs[2].Kind);
            Assert.Equal(0, runs[3].Kind);
            Assert.InRange(runs[1].S0, 20.36, 20.38);
            Assert.InRange(runs[1].S1, 55.80, 55.82);
            Assert.InRange(runs[2].S1, 70.11, 70.13);
            Assert.Equal(0, runs[0].S0);
            Assert.Equal(100, runs[3].S1);
        }

        [Fact]
        public void AllOneKindIsOneRun()
        {
            List<PavementRun> runs = PavementZones.Runs(5, 42.5, 1.0, _ => 1);
            Assert.Single(runs);
            Assert.Equal((5.0, 42.5, 1), (runs[0].S0, runs[0].S1, runs[0].Kind));
        }

        [Fact]
        public void EdgeInTheLastPartialStepIsFound()
        {
            List<PavementRun> runs = PavementZones.Runs(0, 10.5, 1.0, s => s < 10.27 ? 0 : 1);
            Assert.Equal(2, runs.Count);
            Assert.InRange(runs[1].S0, 10.26, 10.28);
        }

        [Fact]
        public void FirstZoneWinsWhereTheyOverlap()
        {
            var inside = new List<Func<double, double, bool>> { (x, y) => x < 10, (x, y) => x < 20 };
            Assert.Equal(1, PavementZones.KindAt(inside, 5, 0));
            Assert.Equal(2, PavementZones.KindAt(inside, 15, 0));
            Assert.Equal(0, PavementZones.KindAt(inside, 25, 0));
        }

        [Fact]
        public void PavedStretchesReachTheStationsEitherSide()
        {
            double[] s = { 0, 1, 2, 3, 4, 5, 6 };
            int[] k = { 0, 1, 1, 0, 2, 2, 2 };
            List<PavementRun> runs = PavementZones.PavedStretches(s, k);
            Assert.Equal(2, runs.Count);
            Assert.Equal((0.0, 3.0, 1), (runs[0].S0, runs[0].S1, runs[0].Kind));
            Assert.Equal((3.0, 6.0, 2), (runs[1].S0, runs[1].S1, runs[1].Kind));
            Assert.Equal(new HashSet<int> { 0, 1 }, PavementZones.KindsBetween(s, k, 1, 3));
            Assert.Equal(new HashSet<int> { 2 }, PavementZones.KindsBetween(s, k, 4, 6));
        }

        [Fact]
        public void PavedStretchEdgesAreFoundBetweenStationsWhenTheKindCanBeAsked()
        {
            double[] s = { 0, 1, 2 };
            int[] k = { 0, 1, 0 };
            // Taxiway from 0.4 to 1.7: the stretch must not take in the unpaved stations 0 and 2.
            List<PavementRun> runs = PavementZones.PavedStretches(s, k, st => st >= 0.4 && st < 1.7 ? 1 : 0);
            Assert.Single(runs);
            Assert.InRange(runs[0].S0, 0.39, 0.41);
            Assert.InRange(runs[0].S1, 1.69, 1.71);
        }

        [Fact]
        public void TypeBUnderTaxiwayFailsTypeAUnderRoadIsOnlyANote()
        {
            var taxiway = new PavementZone { Name = "Taxiway Pavement", MinCover = 1.5, DuctType = "A" };
            var road = new PavementZone { Name = "Road Pavement", MinCover = 0.64, DuctType = "B" };
            Assert.Null(PavementZones.CheckType("A", taxiway));
            Assert.True(PavementZones.CheckType("B", taxiway).Value.fails);
            Assert.False(PavementZones.CheckType("A", road).Value.fails);
            Assert.Null(PavementZones.CheckType(null, taxiway));
        }

        [Fact]
        public void ZoneMaxSkipsTheGeneralMaximumWhenTheZoneIsDeeper()
        {
            Assert.True(double.IsNaN(PavementZones.ZoneMax(new PavementZone { MinCover = 1.5 }, 1.5)));
            Assert.Equal(0, PavementZones.ZoneMax(new PavementZone { MinCover = 0.64 }, 1.5));
            Assert.Equal(2.4, PavementZones.ZoneMax(new PavementZone { MinCover = 1.5, MaxCover = 2.4 }, 1.5));
        }

        [Theory]
        [InlineData("IC-124-IC3A-06", "ICT 6x4 way Reinforcement", "A")]
        [InlineData("IC-124-IC3B-09", "ICT 6x4 way", "B")]
        [InlineData("ICT-124-IC1B-22", "", "B")]
        [InlineData("IC-124-IC1C-21", "ICT 1x1 way 50mm", "C")]
        [InlineData("IC-PIP-TDB-NCRD-77", "ICT 6x4 way Reinforcement", "A")]
        [InlineData("IC-PIP-TDB-NCRD-77", "ICT 6x4 way", null)]
        public void DuctTypeFromNameThenSize(string name, string size, string expected)
            => Assert.Equal(expected, PavementZones.DuctTypeOf(name, size));
    }
}
