using System.Collections.Generic;

namespace C3DProTools.Tests
{
    /// <summary>Profile view arithmetic: station text, datum rounding, naming patterns and zone stations.</summary>
    public class ProfileViewMathTests
    {
        [Theory]
        [InlineData(0, 0, "0+000")]
        [InlineData(450, 0, "0+450")]
        [InlineData(1250, 0, "1+250")]
        [InlineData(-50, 0, "-0+050")]
        [InlineData(-1050.5, 2, "-1+050.50")]
        [InlineData(50, 2, "0+050.00")]
        [InlineData(999.96, 1, "1+000.0")]
        [InlineData(999.6, 0, "1+000")]
        [InlineData(-0.0001, 0, "0+000")]
        public void Station_FormatsSignsAndRounding(double s, int decimals, string expected) =>
            Assert.Equal(expected, ProfileViewMath.Station(s, decimals));

        [Theory]
        [InlineData(93.4, 97.2, 1.0, 1.0, 92, 99)]
        [InlineData(93.4, 97.2, 1.0, 2.0, 92, 100)]
        [InlineData(93.4, 97.2, 1.0, 5.0, 90, 100)]
        [InlineData(95.0, 95.0, 0.0, 5.0, 95, 100)]
        [InlineData(-3.2, 1.1, 0.5, 2.0, -4, 2)]
        public void DatumRange_RoundsOutwardToTheIncrement(double lo, double hi, double margin, double inc, double low, double high)
        {
            (double l, double h) = ProfileViewMath.DatumRange(lo, hi, margin, inc);
            Assert.Equal(low, l, 9);
            Assert.Equal(high, h, 9);
        }

        [Fact]
        public void Name_FillsTokens_WithFormats_AndKeepsUnknownOnes()
        {
            var values = ProfileViewMath.SplitTokens("PV-1", "Main Road", 3, "0+900", "1+350", 12);
            Assert.Equal("PROFILE SHEET 014 - PART - 3", ProfileViewMath.Name("PROFILE SHEET {SheetNo:000} - PART - {PartIndex}", values));
            Assert.Equal("Main Road - 0+900 to 1+350", ProfileViewMath.Name("{AlignmentName} - {StartSta} to {EndSta}", values));
            Assert.Equal("PV-1 - P3", ProfileViewMath.Name("{view} - P{n}", values));
            Assert.Equal("pv-1 {Unknown}", ProfileViewMath.Name("pv-1 {Unknown}", values));
            Assert.Equal("Sheet 14", ProfileViewMath.Name("Sheet {sheetno}", values));
        }

        [Fact]
        public void Name_OldAndNewTokensMix()
        {
            var values = ProfileViewMath.SplitTokens("PV", "Main", 1, ProfileViewMath.Station(0), ProfileViewMath.Station(450), 5);
            Assert.Equal("Main - 0+000 to 0+450 (sheet 5)", ProfileViewMath.Name("{AlignmentName} - {from} to {to} (sheet {SheetNo})", values));
        }

        [Fact]
        public void ZoneRanges_FindEntryAndExitToAMillimetre()
        {
            // A straight path along y = 0 through two zones: x 20.25 to 35.5, and 60 to the end (80).
            var zones = new List<IReadOnlyList<(double X, double Y)>>
            {
                new List<(double X, double Y)> { (20.25, -5), (35.5, -5), (35.5, 5), (20.25, 5) },
                new List<(double X, double Y)> { (60, -5), (90, -5), (90, 5), (60, 5) }
            };
            List<(double s0, double s1)> ranges = ProfileViewMath.ZoneRanges(0, 80, 2.0, s => (s, 0.0), zones);
            Assert.Equal(2, ranges.Count);
            Assert.Equal(20.25, ranges[0].s0, 3);
            Assert.Equal(35.5, ranges[0].s1, 3);
            Assert.Equal(60, ranges[1].s0, 3);
            Assert.Equal(80, ranges[1].s1, 9);
        }
    }
}
