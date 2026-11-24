using System;
using System.Collections.Generic;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// Which Civil 3D object-layer setting governs each kind of Civil 3D object (by its DXF name), and whether a move to that
    /// setting's base layer is certain. One layer per kind (your rule, 2026-11-24): the base layer, with no "_name" suffix.
    /// Plain data; unit tested. Network parts (pipes, structures, pressure parts) are not here: Network Layers puts them on
    /// their type layers.
    /// </summary>
    internal static class ObjectLayerRules
    {
        /// <summary>DXF name (exact) to the setting's name in SettingsObjectLayerType, and a short name for the report.</summary>
        private static readonly Dictionary<string, (string setting, string kind)> Exact = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["AECC_ALIGNMENT"] = ("Alignment", "alignments"),
            ["AECC_STATION_OFFSET_LABEL"] = ("AlignmentLabeling", "alignment labels"),
            ["AECC_PROFILE"] = ("Profile", "profiles"),
            ["AECC_PROFILE_VIEW"] = ("ProfileView", "profile views"),
            ["AECC_PROFILEDATABAND_LABELGROUP"] = ("ProfileViewLabeling", "profile view band labels"),
            ["AECC_PIPENETWORKBAND_LABELGROUP"] = ("ProfileViewLabeling", "profile view band labels"),
            ["AECC_SECTIONDATABAND_LABELGROUP"] = ("SectionViewLabeling", "section view band labels"),
            ["AECC_STATION_ELEVATION_LABEL"] = ("ProfileViewLabeling", "profile view labels"),
            ["AECC_PROFILE_PROJECTION_LABEL"] = ("ProfileViewLabeling", "profile view labels"),
            ["AECC_PROFILEVIEW_DEPTH_LABEL"] = ("ProfileViewLabeling", "profile view labels"),
            ["AECC_TIN_SURFACE"] = ("TinSurface", "TIN surfaces"),
            ["AECC_GRID_SURFACE"] = ("GridSurface", "grid surfaces"),
            ["AECC_FEATURE_LINE"] = ("FeatureLine", "feature lines"),
            ["AECC_CORRIDOR"] = ("Corridor", "corridors"),
            ["AECC_ASSEMBLY"] = ("Assembly", "assemblies"),
            ["AECC_SUBASSEMBLY"] = ("Subassembly", "subassemblies"),
            ["AECC_SAMPLE_LINE"] = ("SampleLine", "sample lines"),
            ["AECC_SECTION"] = ("Section", "sections"),
            ["AECC_SECTION_VIEW"] = ("SectionView", "section views"),
            ["AECC_VIEW_FRAME"] = ("ViewFrame", "view frames"),
            ["AECC_MATCH_LINE"] = ("MatchLine", "match lines"),
            ["AECC_GENERAL_NOTE_LABEL"] = ("GeneralNoteLabel", "note labels"),
            ["AECC_GENERAL_SEGMENT_LABEL"] = ("GeneralSegmentLabel", "segment labels"),
            ["AECC_PIPE_LABEL"] = ("PipeLabeling", "pipe labels"),
            ["AECC_PIPE_PROFILE_LABEL"] = ("PipeLabeling", "pipe labels"),
            ["AECC_PIPE_CROSSING_PROFILE_LABEL"] = ("PipeLabeling", "pipe labels"),
            ["AECC_PIPE_SECTION_LABEL"] = ("PipeLabeling", "pipe labels"),
            ["AECC_STRUCTURE_LABEL"] = ("StructureLabeling", "structure labels"),
            ["AECC_STRUCTURE_PROFILE_LABEL"] = ("StructureLabeling", "structure labels"),
            ["AECC_STRUCTURE_SECTION_LABEL"] = ("StructureLabeling", "structure labels"),
            ["AECC_PRESSURE_PIPE_LABEL"] = ("PressurePipeLabeling", "pressure pipe labels"),
            ["AECC_PRESSURE_PIPE_PROFILE_LABEL"] = ("PressurePipeLabeling", "pressure pipe labels"),
            ["AECC_CROSSING_PRESSURE_PIPE_PROFILE_LABEL"] = ("PressurePipeLabeling", "pressure pipe labels"),
            ["AECC_GRAPH_PROFILE_NETWORK_PART"] = ("PipeOrStructureProfile", "pipes and structures drawn in profile views"),
            ["AECC_GRAPH_PROFILE_PRESSURE_PART"] = ("PressurePartProfile", "pressure parts drawn in profile views"),
        };

        /// <summary>Starts of DXF names for the label families with many kinds (alignment, profile, surface labels).</summary>
        private static readonly (string prefix, string setting, string kind)[] Prefixes =
        {
            ("AECC_ALIGNMENT_", "AlignmentLabeling", "alignment labels"),
            ("AECC_PROFILE_", "ProfileLabeling", "profile labels"),
            ("AECC_SURFACE_", "TinSurfaceLabeling", "surface labels"),
            ("AECC_SAMPLE_LINE_", "SampleLineLabeling", "sample line labels"),
            ("AECC_SECTION_VIEW_", "SectionViewLabeling", "section view labels"),
            ("AECC_PARCEL_SEGMENT_", "ParcelSegmentLabeling", "parcel segment labels"),
        };

        /// <summary>The setting that governs an object of this DXF name and a name for the report; null when none does (network parts, points, internal objects).</summary>
        public static (string setting, string kind)? KindOf(string dxf)
        {
            if (string.IsNullOrEmpty(dxf)) return null;
            if (Exact.TryGetValue(dxf, out var k)) return k;
            foreach (var p in Prefixes)
                if (dxf.StartsWith(p.prefix, StringComparison.OrdinalIgnoreCase) && dxf.IndexOf("LABEL", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (p.setting, p.kind);
            return null;
        }

        /// <summary>
        /// A move to the base layer is certain when the object is on 0 / Defpoints, on the base layer with a name added
        /// ("base_name", what Civil 3D makes with the "_*" suffix), or on a layer that is not in the KSIA form. On another
        /// KSIA-form layer it may be there on purpose: listed, not ticked.
        /// </summary>
        public static bool IsSure(string layer, string baseLayer, bool layerIsStandard) =>
            layer == "0" || layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) ||
            layer.StartsWith(baseLayer + "_", StringComparison.OrdinalIgnoreCase) || !layerIsStandard;
    }
}
