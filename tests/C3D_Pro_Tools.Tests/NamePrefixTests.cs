using C3DProTools.Features.Standards;
using Xunit;

namespace C3DProTools.Tests
{
    /// <summary>Name prefixes (KSIA_), bound xref layers (XREF_) and revision letters, from the IFC drawing 462124-N3.</summary>
    public class NamePrefixTests
    {
        [Theory]
        [InlineData("JAC-SCALE-DYN", "KSIA_SCALE-DYN")]
        [InlineData("JCB-TTB_KSIAATTRIBUTES_A1", "KSIA_TTB_KSIAATTRIBUTES_A1")]
        [InlineData("JCB-TMP_DWG_Title_A1", "KSIA_TMP_DWG_Title_A1")]
        [InlineData("JCP_Section Marker", "KSIA_Section Marker")]
        [InlineData("NCRD_ICT Network_N2 (AGC Modified)$0$Manhole Type C", "KSIA_Manhole Type C")]
        [InlineData("SW00650052000D-C00002-JCB-ZZ-DWG-EL-XX-462124 Legend", "KSIA_Legend")]
        [InlineData("Plan$0$JAC-North Arrow", "KSIA_North Arrow")]
        [InlineData("KSIA-North Arrow$0$x", "KSIA_x")]
        public void ForeignPrefixesBecomeKsia(string name, string expected) => Assert.Equal(expected, NamePrefix.Proposed(name));

        [Theory]
        [InlineData("KSIA_SCALE-DYN")]
        [InlineData("LEGEND BLOCK")]
        [InlineData("Health & Safety block")]
        [InlineData("CutSheetPlan")]
        [InlineData("JACKET")]                       // a word, not the JAC prefix
        [InlineData("SW00650052000D-C00002-JCB-ZZ-DWG-EL-XX-462124")] // a drawing number alone (an xref): not renamed
        public void OtherNamesStay(string name) => Assert.Null(NamePrefix.Proposed(name));

        [Theory]
        [InlineData("NCRD_ICT Network_N2 (AGC Modified)$0$C-ANNO-DIM", "XREF_C-ANNO-DIM")]
        [InlineData("A$0$B$1$O-Ss_50_35_06_14-M-IctManholeTypeC", "XREF_O-Ss_50_35_06_14-M-IctManholeTypeC")]
        [InlineData("C-Ss_55-M-Pipe", null)]
        public void BoundXrefLayers(string layer, string expected) => Assert.Equal(expected, NamePrefix.XrefLayerName(layer));

        [Theory]
        [InlineData("JCB-TTB_KSIAATTRIBUTES_A1", "KSIA_TTB_KSIAATTRIBUTES_A1", true)]
        [InlineData("JCB-TTB_KSIAATTRIBUTES_A1", "JCB-TTB_KSIAATTRIBUTES_A1", true)]
        [InlineData("JCB-TTB_KSIAATTRIBUTES_A1", "KSIA_TTB_OTHER", false)]
        public void SavedNamesStillFindTheBlock(string a, string b, bool same) => Assert.Equal(same, NamePrefix.SameIgnoringPrefix(a, b));

        [Theory]
        [InlineData("Design Status (IFC)", "N3", true)]
        [InlineData("Design Status (IFC)", "C1", false)]
        [InlineData("SDR - Shop Drawing", "C1", true)]
        [InlineData("SDR", "N2", false)]
        [InlineData("Preliminary", "P1", true)]   // neither stage: not checked
        public void RevisionLetterFollowsTheStage(string stage, string rev, bool fits) => Assert.Equal(fits, NamePrefix.RevisionFits(rev, NamePrefix.RevisionLetter(stage)));
    }
}
