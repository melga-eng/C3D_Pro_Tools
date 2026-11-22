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

        // A cable (top 10.0, 0.1 high) over 0-100, an encasement around it over 40-60 (top 10.2, 0.5 high, bottom 9.7).
        private static DuctSegment[] CableInEncasement() => new[]
        {
            new DuctSegment("Cable", 0, 10.0, 100, 10.0) { Height = 0.1 },
            new DuctSegment("Enc", 40, 10.2, 60, 10.2) { Height = 0.5 },
        };

        [Fact]
        public void Encasement_FollowedByDefault()
        {
            var r = TopProfileMath.Compute(CableInEncasement(), 0, 100);
            // A vertical step at each end of the encasement: cable top and encasement top, 0.1 mm apart.
            var pts = r.Points.Select(p => (System.Math.Round(p.Station, 4), System.Math.Round(p.Elevation, 3))).ToList();
            Assert.Equal(new List<(double, double)> { (0, 10.0), (40, 10.0), (40.0001, 10.2), (60, 10.2), (60.0001, 10.0), (100, 10.0) }, pts);
            Assert.Equal(2, r.EncasementSteps);
            Assert.Contains(r.Warnings, w => w.StartsWith("Encasement followed: Enc around Cable"));
            Assert.Equal(0, r.EncasementsLeftOut);
        }

        [Fact]
        public void Encasement_InSeveralPieces_StepsOnlyAtTheRunEnds()
        {
            // Three encasement pieces 40-50-55-60 around two cable pieces split at 50: steps at 40 and 60 only.
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("Cable1", 0, 10.0, 50, 10.0) { Height = 0.1 },
                new DuctSegment("Cable2", 50, 10.0, 100, 10.0) { Height = 0.1 },
                new DuctSegment("Enc1", 40, 10.2, 50, 10.2) { Height = 0.5 },
                new DuctSegment("Enc2", 50, 10.2, 55, 10.2) { Height = 0.5 },
                new DuctSegment("Enc3", 55, 10.2, 60, 10.2) { Height = 0.5 },
            }, 0, 100);
            Assert.Equal(2, r.EncasementSteps);
            Assert.All(r.Points.Where(p => p.Station > 40.001 && p.Station < 59.999), p => Assert.Equal(10.2, p.Elevation, 3));
        }

        [Fact]
        public void Encasement_LeftOutWhenFollowingTheInside()
        {
            var r = TopProfileMath.Compute(CableInEncasement(), 0, 100, insideEncasement: true);
            Assert.All(r.Points, p => Assert.Equal(10.0, p.Elevation, 3));
            Assert.Equal(1, r.EncasementsLeftOut);
        }

        [Fact]
        public void Encasement_WithNothingInsideStillCounts()
        {
            // The cable stops at 50; the encasement goes on to 60 with nothing in it.
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("Cable", 0, 10.0, 50, 10.0) { Height = 0.1 },
                new DuctSegment("Enc", 40, 10.2, 60, 10.2) { Height = 0.5 },
            }, 0, 100, insideEncasement: true);
            Assert.Equal(10.0, Pts(r).First(p => p.s == 50).z);
            Assert.Equal(10.2, Pts(r).Last().z);
        }

        [Fact]
        public void BiggerPipeBesideTheCableInPlan_IsNotAnEncasement()
        {
            // Same depth band, but 0.5 m apart in plan: the bigger pipe is not around the cable.
            var cable = new DuctSegment("Cable", 0, 10.0, 100, 10.0) { Height = 0.1, Width = 0.1, Offset = 0.25 };
            var duct = new DuctSegment("Duct", 40, 10.03, 60, 10.03) { Height = 0.16, Width = 0.16, Offset = -0.25 };
            Assert.False(TopProfileMath.Holds(duct, cable, 50));
            var r = TopProfileMath.Compute(new[] { cable, duct }, 0, 100, insideEncasement: true);
            Assert.Equal(0, r.EncasementsLeftOut);
            Assert.Equal(10.03, TopProfileMath.ElevationAt(r.Points, 50).Value, 3);
        }

        [Fact]
        public void CableLeavingTheEncasement_TopLeavesItThere()
        {
            // Cable top falls 10.0 -> 9.5 over 0-100 (0.1 high), encasement 10.2 level (0.5 high, bottom 9.7): the cable's
            // bottom (9.9 - 0.005 s) leaves the encasement's (9.7) at sta 40. Inside mode follows the cable up to there.
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("Cable", 0, 10.0, 100, 9.5) { Height = 0.1 },
                new DuctSegment("Enc", 0, 10.2, 100, 10.2) { Height = 0.5 },
            }, 0, 100, insideEncasement: true);
            Assert.Equal(9.9, TopProfileMath.ElevationAt(r.Points, 20).Value, 3);
            Assert.Contains(r.Points, p => System.Math.Abs(p.Station - 40) < 0.01);
        }

        [Fact]
        public void SmallEncasementRise_StillAStepNotARamp()
        {
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("Cable", 0, 10.0, 100, 10.0) { Height = 0.1 },
                new DuctSegment("Enc", 40, 10.015, 60, 10.015) { Height = 0.5 },
            }, 0, 100);
            Assert.Equal(2, r.EncasementSteps);
            Assert.Equal(10.0, TopProfileMath.ElevationAt(r.Points, 20).Value, 4);
        }

        [Fact]
        public void PipeBesideAnother_IsNotAnEncasement()
        {
            // A taller pipe lying higher (its bottom above the cable's): not around it, so the TOP follows it in both modes.
            var r = TopProfileMath.Compute(new[]
            {
                new DuctSegment("Cable", 0, 10.0, 100, 10.0) { Height = 0.1 },
                new DuctSegment("Duct", 40, 10.8, 60, 10.8) { Height = 0.5 },
            }, 0, 100, insideEncasement: true);
            Assert.Equal(10.8, Pts(r).First(p => p.s == 40).z);
            Assert.Equal(0, r.EncasementsLeftOut);
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
