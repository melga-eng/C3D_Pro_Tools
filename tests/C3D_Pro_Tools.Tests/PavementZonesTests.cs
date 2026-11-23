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

            // A 1 m hole 4 m away is within the band (review case).
            bool Holed(double x, double y) => Surface(x, y) && (x - 54) * (x - 54) + y * y > 1;
            Assert.False(PavementZones.InsideBy(Holed, 50, 0, 16.5));
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
            var zones = new[] { taxiway, road };
            Assert.Null(PavementZones.CheckType("A", taxiway, zones));
            Assert.True(PavementZones.CheckType("B", taxiway, zones).Value.fails);
            Assert.False(PavementZones.CheckType("A", road, zones).Value.fails);
            Assert.Null(PavementZones.CheckType(null, taxiway, zones));
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
        public void ExistingDuctTypesStillRecognized(string name, string size, string expected)
            => Assert.Equal(expected, PavementZones.DuctTypeOf(name, size, null));

        [Theory]
        [InlineData("ICT-105-2B-01", "", "", "B")]
        [InlineData("", "", "ICT Duct_Reinf.\nType-3A_6x4", "A")]
        [InlineData("IC-124-IC3A-06", "ICT 6x4 Reinforcement", "Prop ICT Ductbank (6x4 110 mm TYPE-3B)", "A")]
        [InlineData("ICT-125-IC2B-01", "ICT 6x4 Reinforcement", "", "B")]
        [InlineData("IC-PIP-TDB-NCRD-71", "ICT 6X4 WAY", "Prop ICT Ductbank (6x4 110 mm TYPE-3B)", "B")]
        [InlineData("", "", "Prop ICT Ductbank (6x4 110mm Type-3A- Under Taxiway)", "A")]
        [InlineData("", "", "ICT Micro Duct Type-1C", "C")]
        [InlineData("", "ICT 6x4 Reinforcement", "", "A")]
        [InlineData("IC-124-IC3B-06", "790 x 1110 mm ICT Duct - 3A _6x4", "", "B")]
        [InlineData("IC-PIP-TDB-NCRD-77", "790 x 1110 mm ICT Duct - 3A _6x4", "", "A")]
        [InlineData("", "ICT Duct - 2B", "", "B")]
        [InlineData("IF-PIP-MVDCT-T1-0189", "1,500.00 X 1,700.00 Medium Voltage Trench - Type 01", "", "T1")]
        [InlineData("", "500.00 X 1,700.00 Medium Voltage Trench - Type 04", "", "T4")]
        [InlineData("IF-PIP-MVDCT-T1-0192", "Medium Voltage Trench - Type 02", "", "T1")]
        [InlineData("IF-PIP-ROAD-CROSSING-DUCT-049", "Medium Voltage Trench - Type 02", "", "T2")]
        [InlineData("", "MV 5x3 way Duct Bank (Type-1 & 2)", "MV 5\u0445 3 WAY DUCT BANK TYPE-1", "T1")]
        [InlineData("", "", "MV 5x 3 WAY DUCT BANK TYPE-1", "T1")]
        [InlineData("", "", "TYPE - 04 - 5 X 1 Duct Bank (Under Road Crossing)", "T4")]
        [InlineData("", "", "MV Trench Type 03", "T3")]
        [InlineData("", "", "MV Reinforced Duct Bank Type-2", "T2")]
        [InlineData("", "", "T1", "T1")]
        [InlineData("IF-PIP-MVDCT-T1-0189", "", "", "T1")]
        [InlineData("", "MV 5x3 way Duct Bank (Type-1 & 2)", "", null)]
        [InlineData("", "MV Duct Bank Type-1 & Type-2", "", null)]
        [InlineData("", "MV Duct Bank Type-1 / 2", "", null)]
        [InlineData("", "MV Duct Bank Type-1 or Type-2", "", null)]
        [InlineData("", "MV Duct Bank Type-1; Type-2", "", null)]
        [InlineData("", "MV Direct Buried 3C x 500 mm2 Cable", "", null)]
        [InlineData("MV Direct Buried 3C x 500 mm2 Cable", "", "", null)]
        [InlineData("", "", "MV Direct Buried 3C x 500 mm2 Cable", null)]
        [InlineData("", "", "Pipe Type 01", null)]
        [InlineData(null, null, null, null)]
        public void DuctTypesPreferNameThenDescriptionThenSize(string name, string size, string description, string expected)
            => Assert.Equal(expected, PavementZones.DuctTypeOf(name, size, description));

        [Theory]
        [InlineData("A, T1, T3", "t1", true)]
        [InlineData("A T1\tT3\nT4", "T4", true)]
        [InlineData("T1,T3", "T1", true)]
        [InlineData("T1,T3", "T2", false)]
        [InlineData("T1,T3", "T", false)]
        [InlineData("T1,T3", "T11", false)]
        [InlineData("", "C", true)]
        public void AcceptedCodesAreWholeCodesOrAnyWhenEmpty(string accepted, string type, bool expected)
            => Assert.Equal(expected, PavementZones.AcceptsType(new PavementZone { DuctType = accepted }, type));

        [Fact]
        public void MixedTypesFollowCoverStrengthAndSkipTypesOutsideTheRules()
        {
            var taxiway = new PavementZone { Name = "Taxiway", MinCover = 1.5, DuctType = "A, T1, T3" };
            var road = new PavementZone { Name = "Road", MinCover = 0.64, DuctType = "B T2 T4" };
            var unpaved = new PavementZone { Name = "Unpaved", MinCover = 0.6, DuctType = "B,T2,T4" };
            var zones = new[] { taxiway, road, unpaved };
            foreach (string type in new[] { "A", "T1", "T3" })
            {
                Assert.Null(PavementZones.CheckType(type, taxiway, zones));
                var note = PavementZones.CheckType(type, road, zones).Value;
                Assert.False(note.fails);
                Assert.Contains("stronger than needed", note.text);
                Assert.False(PavementZones.CheckType(type, unpaved, zones).Value.fails);
            }
            foreach (string type in new[] { "B", "T2", "T4" })
            {
                Assert.Null(PavementZones.CheckType(type, road, zones));
                var fail = PavementZones.CheckType(type, taxiway, zones).Value;
                Assert.True(fail.fails);
                Assert.Contains($"needs duct type A, T1, T3 (is {type})", fail.text);
            }
            foreach (string type in new[] { "C", "T9", null })
            {
                Assert.False(PavementZones.IsTypeChecked(type, zones));
                Assert.Null(PavementZones.CheckType(type, taxiway, zones));
            }
            Assert.Null(PavementZones.CheckType("T1", new PavementZone(), zones));
        }

        [Fact]
        public void AZoneThatAcceptsAnyTypeDoesNotBringMicroDuctsIntoTheCheck()
        {
            // Unpaved accepts "any": a 1C micro duct under the taxiway is still not checked, as before.
            var taxiway = new PavementZone { Name = "Taxiway", MinCover = 1.5, DuctType = "A" };
            var road = new PavementZone { Name = "Road", MinCover = 0.64, DuctType = "B" };
            var unpaved = new PavementZone { Name = "Unpaved", MinCover = 0.6, DuctType = "" };
            var zones = new[] { taxiway, road, unpaved };
            Assert.False(PavementZones.IsTypeChecked("C", zones));
            Assert.Null(PavementZones.CheckType("C", taxiway, zones));
            Assert.True(PavementZones.CheckType("B", taxiway, zones).Value.fails);
        }

        [Fact]
        public void ATypeIsStrongerOnlyWhenAnAcceptingZoneAsksMoreCover()
        {
            var first = new PavementZone { MinCover = 1.5, DuctType = "A" };
            var second = new PavementZone { MinCover = 1.5, DuctType = "B" };
            var zones = new[] { first, second };
            Assert.True(PavementZones.CheckType("A", second, zones).Value.fails);
            first.MinCover = 1.4;
            Assert.True(PavementZones.CheckType("A", second, zones).Value.fails);
            first.MinCover = 1.6;
            Assert.False(PavementZones.CheckType("A", second, zones).Value.fails);
        }

        [Theory]
        [InlineData(0, "A", "B")]
        [InlineData(1, "T1,T3", "T2,T4")]
        [InlineData(2, "A,T1,T3", "B,T2,T4")]
        public void PresetSetsAllTiedDeepestZonesAndKeepsCoverAndEdges(int index, string under, string elsewhere)
        {
            var zones = new[]
            {
                new PavementZone { Surface = "Road", MinCover = 0.64, DuctType = "old" },
                new PavementZone { Surface = "Taxiway", MinCover = 1.5, MaxCover = 2.4, EdgeBand = 16.5 },
                new PavementZone { Surface = "Other pavement", MinCover = 1.5 },
            };
            var unpaved = new PavementZone { MinCover = 0.6 };
            PavementDuctTypePreset.All[index].Apply(zones, unpaved);
            Assert.Equal(elsewhere, zones[0].DuctType);
            Assert.Equal(under, zones[1].DuctType);
            Assert.Equal(under, zones[2].DuctType);
            Assert.Equal(elsewhere, unpaved.DuctType);
            Assert.Equal(new[] { 0.64, 1.5, 1.5 }, Array.ConvertAll(zones, z => z.MinCover));
            Assert.Equal(0.6, unpaved.MinCover);
            Assert.Equal(2.4, zones[1].MaxCover);
            Assert.Equal(16.5, zones[1].EdgeBand);
            Assert.Equal("Taxiway", zones[1].Surface);
            PavementDuctTypePreset.All[index].Apply(Array.Empty<PavementZone>(), unpaved);
            Assert.Equal(elsewhere, unpaved.DuctType);
        }

        [Theory]
        [InlineData("A")]
        [InlineData("A, T1, T3")]
        public void SavedDuctTypeKeyKeepsSingleAndMultipleCodes(string type)
        {
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
            string json = System.Text.Json.JsonSerializer.Serialize(new PavementZone { DuctType = type }, options);
            Assert.Contains("\"ductType\":", json);
            Assert.Equal(type, System.Text.Json.JsonSerializer.Deserialize<PavementZone>(json, options).DuctType);
        }
    }
}
