namespace C3DProTools.Tests
{
    /// <summary>Rims to surface: the rim follows the ground plus the height asked, unless that makes no sense for the structure.</summary>
    public class RimRulesTests
    {
        private const double None = double.NaN;

        [Fact]
        public void RimMovesToTheSurface()
        {
            (RimCheck check, double rim) = RimRules.Check(101, 100.25, 0, 97, 98.5);
            Assert.Equal(RimCheck.Adjust, check);
            Assert.Equal(100.25, rim, 6);
        }

        [Fact]
        public void HeightAboveTheSurfaceIsAdded_AndMayBeNegative()
        {
            Assert.Equal(100.35, RimRules.Check(101, 100.25, 0.10, 97, 98.5).newRim, 6);
            Assert.Equal(100.20, RimRules.Check(101, 100.25, -0.05, 97, 98.5).newRim, 6);
        }

        [Fact]
        public void OutsideTheSurface_HasNoLevel()
        {
            (RimCheck check, double rim) = RimRules.Check(101, None, 0, 97, 98.5);
            Assert.Equal(RimCheck.OffSurface, check);
            Assert.True(double.IsNaN(rim));
        }

        [Fact]
        public void WithinAMillimetre_AlreadyMatches()
        {
            Assert.Equal(RimCheck.AlreadyMatches, RimRules.Check(100.2505, 100.25, 0, 97, 98.5).check);
            Assert.Equal(RimCheck.Adjust, RimRules.Check(100.252, 100.25, 0, 97, 98.5).check);
        }

        [Fact]
        public void AtOrUnderTheSump_IsRefused()
        {
            Assert.Equal(RimCheck.BelowSump, RimRules.Check(101, 97, 0, 97, None).check);
            Assert.Equal(RimCheck.BelowSump, RimRules.Check(101, 96, 0, 97, 98.5).check);
        }

        [Fact]
        public void UnderAPipeTop_IsFlagged_ButAPipeTopAtTheRimIsFine()
        {
            Assert.Equal(RimCheck.BelowPipe, RimRules.Check(101, 98.4, 0, 97, 98.5).check);
            Assert.Equal(RimCheck.Adjust, RimRules.Check(101, 98.5, 0, 97, 98.5).check);
        }

        [Fact]
        public void NoConnectedPipes_OrUnknownSump_DoNotBlock()
        {
            Assert.Equal(RimCheck.Adjust, RimRules.Check(101, 100, 0, None, None).check);
        }

        [Fact]
        public void ARimAlreadyOnTheSurface_IsNotReportedAsAProblem()
        {
            // A pipe standing above the ground was there before this tool: nothing would change.
            Assert.Equal(RimCheck.AlreadyMatches, RimRules.Check(98.4, 98.4, 0, 97, 98.5).check);
        }
    }
}
