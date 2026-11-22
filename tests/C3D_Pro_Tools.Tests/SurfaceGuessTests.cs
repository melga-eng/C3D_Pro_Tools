using C3DProTools.Features.ProfileTool;
using Xunit;

namespace C3DProTools.Tests
{
    public class SurfaceGuessTests
    {
        private static readonly string[] Mv = { "CE_PROP-CONT-NCRD", "COMBINED NGL TILL 21-06-2026 AND NGL NOT SUBMITED", "PROPOSED_SURFACE" };
        private static readonly string[] Ict = { "KSIA_Existing-Surface", "KSIA_Proposed-Surface", "KSIA_ROAD-AIRSIDE", "KSIA_TAXIWAY-PAVEMENT", "TOP ALL MEDEVAC" };

        [Fact]
        public void The_MV_drawing_gets_its_own_surfaces_instead_of_the_ICT_names() =>
            Assert.Equal(("COMBINED NGL TILL 21-06-2026 AND NGL NOT SUBMITED", "PROPOSED_SURFACE"),
                (SurfaceGuess.Pick(true, "KSIA_Existing-Surface", Mv), SurfaceGuess.Pick(false, "KSIA_Proposed-Surface", Mv)));

        [Theory]
        [InlineData("KSIA_Proposed_Surface")]
        [InlineData("KSIA_Proposed-Surface")]
        [InlineData("PROPOSED_SURFACE")]
        public void Each_spelling_of_the_proposed_surface_is_found(string name) =>
            Assert.Equal(name, SurfaceGuess.Pick(false, "PROPOSED_SURFACE", new[] { "CE_PROP-CONT-NCRD", "KSIA_Existing-Surface", name }));

        [Fact]
        public void A_saved_name_the_drawing_has_is_kept() =>
            Assert.Equal("KSIA_Proposed-Surface", SurfaceGuess.Pick(false, "ksia_proposed-surface", Ict));

        [Fact]
        public void The_ICT_drawing_is_read_right_without_saved_names() =>
            Assert.Equal(("KSIA_Existing-Surface", "KSIA_Proposed-Surface"), (SurfaceGuess.Pick(true, "", Ict), SurfaceGuess.Pick(false, null, Ict)));

        [Fact]
        public void No_surface_says_what_it_is_gives_none() =>
            Assert.Null(SurfaceGuess.Pick(true, "Ground 1", new[] { "Surface1", "TIN A" }));

        [Fact]
        public void The_surface_taken_for_one_role_is_not_offered_for_the_other() =>
            Assert.Null(SurfaceGuess.Pick(false, "", new[] { "FGL" }, taken: "FGL"));
    }
}
