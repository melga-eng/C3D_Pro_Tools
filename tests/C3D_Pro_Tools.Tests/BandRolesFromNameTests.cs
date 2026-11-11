namespace C3DProTools.Tests
{
    /// <summary>A data band's profiles told by its style name (0 none, 1 existing, 2 proposed, 3 top).</summary>
    public class BandRolesFromNameTests
    {
        [Theory]
        [InlineData("KSIA_JCB_Existing Levels", 1, 1)]
        [InlineData("KSIA_JCB_Proposed Levels", 2, 2)]
        [InlineData("KSIA_TOP OF DUCT (m)", 3, 3)]
        [InlineData("KSIA_Level Difference", 2, 3)]
        [InlineData("KSIA_Level Difference_NGL", 1, 3)]
        [InlineData("NGL Elevations", 1, 1)]
        [InlineData("FGL", 2, 2)]
        [InlineData("Depth of Cover", 2, 3)]
        public void KnownNames(string name, int p1, int p2) => Assert.Equal((p1, p2), ProfileViewMath.BandRolesFromName(name));

        [Theory]
        [InlineData("KSIA_JCB_Chainage")]
        [InlineData("KSIA_Title")]
        [InlineData("Existing and Proposed Elevations")]
        [InlineData("Stoppage")]   // "TOP" only as a whole word
        [InlineData("")]
        public void OtherNames_AreLeftAsRecorded(string name) => Assert.Null(ProfileViewMath.BandRolesFromName(name));
    }
}
