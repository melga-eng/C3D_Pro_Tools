using System.Collections.Generic;
using System.Linq;
using C3DProTools.ProfileTool;

namespace C3DProTools.Tests
{
    /// <summary>TOP profile from duct tops: flat ends at the alignment ends, straight across manholes, upper envelope, warnings.</summary>
    public class TopProfileMathTests
    {
        private static List<(double s, double z)> Pts(TopFitResult r) => r.Points.Select(p => (System.Math.Round(p.Station, 3), System.Math.Round(p.Elevation, 3))).ToList();

        [Fact]
        public void TwoDucts_FlatEndsAndStraightAcrossTheManhole()
        {
            // Alignment 0-100, manholes at 0, 50, 100; duct ends 2.3 m off each structure centre.
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("A", 2.3, 10.0, 47.7, 9.5),
                new DuctSegment("B", 52.3, 9.4, 97.7, 9.0),
            }, 0, 100);
            Assert.Equal(new List<(double, double)> { (0, 10.0), (2.3, 10.0), (47.7, 9.5), (52.3, 9.4), (97.7, 9.0), (100, 9.0) }, Pts(r));
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void ReversedDuct_IsNormalised()
        {
            var r = TopProfileMath.Compute(new[] { new DuctSegment("A", 40, 8, 10, 9) }, 0, 50);
            Assert.Equal(new List<(double, double)> { (0, 9), (10, 9), (40, 8), (50, 8) }, Pts(r));
        }

        [Fact]
        public void OverlappingDucts_FollowTheHigherAndAddTheCrossing()
        {
            // A falls 10 -> 8 over 0-20, B rises 8 -> 10 over 0-20 (fully overlapping): they cross at 10.
            var r = TopProfileMath.Compute(new[] { new DuctSegment("A", 0, 10, 20, 8), new DuctSegment("B", 0, 8, 20, 10) }, 0, 20);
            Assert.Equal(new List<(double, double)> { (0, 10), (10, 9), (20, 10) }, Pts(r));
            Assert.Contains(r.Warnings, w => w.Contains("overlap"));
        }

        [Fact]
        public void StepAtAJoint_UsesTheHigherEndAndWarns()
        {
            var r = TopProfileMath.Compute(new[] { new DuctSegment("A", 0, 10, 10, 10), new DuctSegment("B", 10, 9.9, 20, 9.9) }, 0, 20);
            Assert.Equal(10, r.Points.Single(p => System.Math.Abs(p.Station - 10) < 1e-6).Elevation, 6);
            Assert.Contains(r.Warnings, w => w.StartsWith("Step"));
        }

        [Fact]
        public void LongGap_Warns()
        {
            var r = TopProfileMath.Compute(new[] { new DuctSegment("A", 0, 10, 10, 10), new DuctSegment("B", 30, 10, 40, 10) }, 0, 40);
            Assert.Contains(r.Warnings, w => w.StartsWith("No duct from sta 10.00 to 30.00"));
        }

        [Fact]
        public void DuctsOutsideTheAlignment_AreIgnored()
        {
            Assert.Empty(TopProfileMath.Compute(new[] { new DuctSegment("A", 200, 10, 210, 10) }, 0, 100).Points);
        }

        [Fact]
        public void DuctPastTheAlignmentEnd_IsClipped()
        {
            var r = TopProfileMath.Compute(new[] { new DuctSegment("A", 5, 10, 120, 10) }, 0, 100);
            Assert.Equal(100, r.Points[^1].Station, 6);
            Assert.Equal(0, r.Points[0].Station, 6);
        }

        [Fact]
        public void ElevationAt_InterpolatesAndIsNullOutside()
        {
            var pts = new List<StationElevation> { new(0, 10), new(10, 12) };
            Assert.Equal(11, TopProfileMath.ElevationAt(pts, 5).Value, 6);
            Assert.Null(TopProfileMath.ElevationAt(pts, 11));
        }
    }
}
