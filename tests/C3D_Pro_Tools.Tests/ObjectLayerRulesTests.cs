using C3DProTools.Features.Standards;
using Xunit;

namespace C3DProTools.Tests
{
    /// <summary>Civil 3D object kinds to their object-layer setting, from the kinds found in the project's TEST and MV drawings.</summary>
    public class ObjectLayerRulesTests
    {
        [Theory]
        [InlineData("AECC_ALIGNMENT", "Alignment")]
        [InlineData("AECC_ALIGNMENT_STATION_LABEL_GROUP", "AlignmentLabeling")]
        [InlineData("AECC_ALIGNMENT_MINOR_STATION_LABEL_GROUP", "AlignmentLabeling")]
        [InlineData("AECC_STATION_OFFSET_LABEL", "AlignmentLabeling")]
        [InlineData("AECC_PROFILE", "Profile")]
        [InlineData("AECC_PROFILE_VIEW", "ProfileView")]
        [InlineData("AECC_PROFILEDATABAND_LABELGROUP", "ProfileViewLabeling")]
        [InlineData("AECC_PIPENETWORKBAND_LABELGROUP", "ProfileViewLabeling")]
        [InlineData("AECC_PROFILE_PROJECTION_LABEL", "ProfileViewLabeling")]
        [InlineData("AECC_STATION_ELEVATION_LABEL", "ProfileViewLabeling")]
        [InlineData("AECC_TIN_SURFACE", "TinSurface")]
        [InlineData("AECC_PIPE_PROFILE_LABEL", "PipeLabeling")]
        [InlineData("AECC_PIPE_CROSSING_PROFILE_LABEL", "PipeLabeling")]
        [InlineData("AECC_STRUCTURE_PROFILE_LABEL", "StructureLabeling")]
        [InlineData("AECC_CROSSING_PRESSURE_PIPE_PROFILE_LABEL", "PressurePipeLabeling")]
        [InlineData("AECC_GRAPH_PROFILE_NETWORK_PART", "PipeOrStructureProfile")]
        [InlineData("AECC_GRAPH_PROFILE_PRESSURE_PART", "PressurePartProfile")]
        public void KindsMapToTheirSetting(string dxf, string setting) => Assert.Equal(setting, ObjectLayerRules.KindOf(dxf)?.setting);

        [Theory]
        // Network parts (Network Layers' job), points (no setting) and Civil 3D's internal objects are not checked here.
        [InlineData("AECC_PIPE")]
        [InlineData("AECC_STRUCTURE")]
        [InlineData("AECC_PRESSUREPIPE")]
        [InlineData("AECC_FITTING")]
        [InlineData("AECC_COGO_POINT")]
        [InlineData("AECC_NETWORK")]
        [InlineData("AECC_NETWORK_PART_CONNECTOR")]
        [InlineData("AECC_PARCEL")]
        public void OtherKindsAreNotChecked(string dxf) => Assert.Null(ObjectLayerRules.KindOf(dxf));

        [Theory]
        [InlineData("0", true, true)]
        [InlineData("C-Ss_55-T-Pipe_ICT-TILE-124", true, true)]       // the per-object layer Civil 3D made ("_*")
        [InlineData("Profile Dim", false, true)]                      // not in the KSIA form
        [InlineData("Z-Zz_20_90-T-TitleBlock", true, false)]          // another KSIA layer: maybe on purpose
        public void SureMoves(string layer, bool standard, bool sure) => Assert.Equal(sure, ObjectLayerRules.IsSure(layer, "C-Ss_55-T-Pipe", standard));
    }
}
