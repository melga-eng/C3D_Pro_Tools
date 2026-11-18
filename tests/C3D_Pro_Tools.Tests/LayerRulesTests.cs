using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Standards;
using Xunit;

namespace C3DProTools.Tests
{
    public class LayerRulesTests
    {
        private static readonly string[] PartBases = { "C-Ss_55-M-Pipe", "C-Ss_20-M-Struct" };
        private static readonly string[] LabelBases = { "C-Ss_55-T-Pipe", "C-Ss_20-T-Struct" };

        private static LayerItem Part(string desc, string layer, string size = "") =>
            new LayerItem { Handle = "1", Kind = "pipe", Description = desc, Size = size, Layer = layer };

        [Theory]
        [InlineData("Type:IC-3B  Size:ICT 6x4 way", "", "TYPE IC-3B")]
        [InlineData("Type:IC-3A  ICT 6x4 Reinforcement", "", "TYPE IC-3A")]
        [InlineData("type: ic-1c Size:ICT 1x1 way 50mm", "", "TYPE IC-1C")]
        [InlineData("TYPE C TYPE 1 MH", "", "TYPE C TYPE 1 MH")]
        [InlineData("  ICT 1x2   way ", "", "ICT 1X2 WAY")]
        [InlineData("", "ICT 3x4 way", "SIZE ICT 3X4 WAY")]
        [InlineData("", "?", "")]
        public void TypeKey_reads_the_type_from_the_description(string desc, string size, string key) =>
            Assert.Equal(key, LayerRules.TypeKey(desc, size));

        [Fact]
        public void Suffix_layers_are_the_base_plus_underscore()
        {
            Assert.True(LayerRules.IsSuffixLayer("C-Ss_55-M-Pipe_ICT-TILE-124 (2)", PartBases));
            Assert.False(LayerRules.IsSuffixLayer("C-Ss_55-M-Pipe", PartBases));
            Assert.False(LayerRules.IsSuffixLayer("IC-Ss_75_10_21_88-M-IctNewDuct6x4_Type-3B", PartBases));
            Assert.False(LayerRules.IsSuffixLayer("C-Ss_55-M-PipeProf_ICT-126", PartBases));
        }

        [Fact]
        public void Learn_takes_the_layer_most_parts_of_a_type_are_on_ignoring_0_and_per_network_layers()
        {
            var parts = new List<LayerItem>();
            for (int i = 0; i < 5; i++) parts.Add(Part("Type:IC-3A Reinforcement", "IC-M-Type-3A"));
            parts.Add(Part("Type:IC-3A Reinforcement", "IC-M-Type-3B"));
            for (int i = 0; i < 9; i++) parts.Add(Part("Type:IC-3A Reinforcement", "C-Ss_55-M-Pipe_ICT-TILE-124"));
            parts.Add(Part("Type:IC-3A Reinforcement", "0"));
            Dictionary<string, string> rules = LayerRules.Learn(parts, PartBases);
            Assert.Equal("IC-M-Type-3A", rules["TYPE IC-3A"]);
        }

        [Fact]
        public void Check_moves_a_part_to_its_type_layer_and_the_description_wins()
        {
            var rules = new Dictionary<string, string> { ["TYPE IC-3A"] = "IC-M-Type-3A" };
            List<LayerFinding> f = LayerRules.Check(new[]
            {
                Part("Type:IC-3A Reinforcement", "IC-M-Type-3B"),
                Part("Type:IC-3A Reinforcement", "IC-M-Type-3A"),
                Part("Type:IC-3A Reinforcement", "0"),
            }, rules, PartBases, LabelBases);
            Assert.Equal(2, f.Count);
            Assert.All(f, x => Assert.Equal("IC-M-Type-3A", x.To));
            Assert.Equal("on layer 0", f[1].Why);
        }

        [Fact]
        public void Check_lists_a_part_with_no_rule_only_when_on_0_or_a_per_network_layer()
        {
            List<LayerFinding> f = LayerRules.Check(new[]
            {
                Part("Unknown thing", "C-Ss_55-M-Pipe_Tile-9"),
                Part("Another thing", "IC-Some-Layer"),
            }, new Dictionary<string, string>(), PartBases, LabelBases);
            Assert.Single(f);
            Assert.Null(f[0].To);
        }

        [Fact]
        public void Labels_on_per_network_layers_go_to_the_base_tile_layers_stay()
        {
            LayerItem L(string layer) => new LayerItem { Handle = "2", IsLabel = true, Kind = "pipe label", Layer = layer, LabelBase = "C-Ss_55-T-Pipe" };
            List<LayerFinding> f = LayerRules.Check(new[] { L("C-Ss_55-T-Pipe_ICT-TILE-124 (2)"), L("_TILE-124-D"), L("C-Ss_55-T-Pipe"), L("0") },
                new Dictionary<string, string>(), PartBases, LabelBases);
            Assert.Equal(2, f.Count);
            Assert.All(f, x => Assert.Equal("C-Ss_55-T-Pipe", x.To));
            Assert.DoesNotContain(f, x => x.Item.Layer == "_TILE-124-D");
        }

        [Fact]
        public void Labels_on_a_layer_frozen_in_a_viewport_stay_even_when_it_looks_per_network()
        {
            var label = new LayerItem { Handle = "3", IsLabel = true, Kind = "pipe label", Layer = "C-Ss_55-T-Pipe_TILE-124-D", LabelBase = "C-Ss_55-T-Pipe" };
            Assert.Single(LayerRules.Check(new[] { label }, new Dictionary<string, string>(), PartBases, LabelBases));
            Assert.Empty(LayerRules.Check(new[] { label }, new Dictionary<string, string>(), PartBases, LabelBases, new[] { "C-Ss_55-T-Pipe_TILE-124-D" }));
        }

        [Fact]
        public void Rules_round_trip_through_the_saved_text()
        {
            var rules = new Dictionary<string, string> { ["TYPE IC-3B"] = "IC-Ss_75_10_21_88-M-IctNewDuct6x4_Type-3B", ["TYPE C TYPE 1 MH"] = "IC-Ss_75_10_21_88-M-IctManhole_Type C" };
            Dictionary<string, string> back = LayerRules.Read(LayerRules.Write(rules));
            Assert.Equal(rules.OrderBy(k => k.Key), back.OrderBy(k => k.Key));
        }
    }
}
