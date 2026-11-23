using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using C3DProTools.Features.Standards;
using Xunit;
using Xunit.Abstractions;

namespace C3DProTools.Tests
{
    public class LayerSuggestTests
    {
        private readonly ITestOutputHelper output;
        private static readonly string[] Template = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ksia-template-layers.txt"));
        private static readonly string[] Drawing = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "test-drawing-layers.txt"));

        public LayerSuggestTests(ITestOutputHelper output) => this.output = output;

        [Theory]
        [InlineData("C-Zz_20_10_45-T_Labels", true)]
        [InlineData("C-Zz_60_50-M-Marks", true)]
        [InlineData("C-Ss_55-M-Pipe_ICT-CENTRAL_DUCT", true)]
        [InlineData("0", false)]
        [InlineData("Defpoints", false)]
        [InlineData("defPOINTS", false)]
        [InlineData("Background", false)]
        [InlineData("C-ANNO-TABL", false)]
        [InlineData("C-Zz_2-T_Labels", false)]
        [InlineData("C-Zz_20_10_45-T_", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void Standard_form_accepts_both_description_separators(string layer, bool expected) =>
            Assert.Equal(expected, LayerSuggest.IsStandard(layer, Array.Empty<string>()));

        [Fact]
        public void Template_names_are_accepted_case_insensitively_except_reserved_layers()
        {
            Assert.Equal(871, Template.Length);
            foreach (string layer in Template.Where(x => x != "0" && !x.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)))
                Assert.True(LayerSuggest.IsStandard(layer.ToLowerInvariant(), Template), layer);
            Assert.False(LayerSuggest.IsStandard("0", Template));
            Assert.False(LayerSuggest.IsStandard("Defpoints", Template));
        }

        [Fact]
        public void Bound_standard_name_returns_its_own_name_sure()
        {
            const string inner = "O-Ss_50_35_06_14-M-IctManholeTypeC";
            const string layer = "NCRD_ICT Network_N2 (AGC Modified)$0$" + inner;
            var result = LayerSuggest.Suggest(layer, Template, Drawing);
            Assert.Equal(layer, result.Layer);
            Assert.Equal(inner, result.Target);
            Assert.True(result.Sure);
            Assert.Equal("bound xref layer: back to its own name", result.Why);
        }

        [Theory]
        [InlineData("outer$1$nested$23$Inner", "Inner")]
        [InlineData("outer$1$inner", "inner")]
        public void Bound_known_names_are_sure_using_last_numbered_separator(string layer, string expected)
        {
            foreach (bool inTemplate in new[] { true, false })
            {
                var result = LayerSuggest.Suggest(layer, inTemplate ? new[] { "INNER" } : Array.Empty<string>(),
                    inTemplate ? Array.Empty<string>() : new[] { "INNER" });
                Assert.Equal(expected, result.Target);
                Assert.True(result.Sure);
            }
        }

        [Fact]
        public void Bound_unknown_Aia_name_is_matched_by_meaning_and_remains_unsure()
        {
            var result = LayerSuggest.Suggest("drawing$0$C-ANNO-DIM", Template, Drawing);
            Assert.Equal("Z-Zz_20_20-D-Dim", result.Target);
            Assert.False(result.Sure);
            Assert.Contains("bound xref", result.Why);
            Assert.Null(LayerSuggest.Suggest("drawing$0$Background", Template, Array.Empty<string>()).Target);
        }

        [Theory]
        [InlineData("C-ANNO-TABL", "Tabl")]
        [InlineData("C-ANNO-TABL-TEXT", "TablTxt")]
        [InlineData("C-ANNO-TABL-TITL", "TablTxtTtl")]
        [InlineData("C-ROAD-CNTR", "ClineRoad")]
        [InlineData("C-PROP-TEXT", "Prcl")]
        [InlineData("A-BLDG", "Bldg")]
        [InlineData("C-ROAD-CORR", "Corr")]
        public void Aia_abbreviations_match_template_meanings_but_are_never_sure(string layer, string expectedPart)
        {
            var result = LayerSuggest.Suggest(layer, Template, Drawing);
            Assert.NotNull(result.Target);
            Assert.Contains(expectedPart, result.Target);
            Assert.Contains(result.Target, Template);
            Assert.False(result.Sure);
        }

        [Theory]
        [InlineData("Background")]
        [InlineData("C-ANNO")]
        [InlineData("C-XYZZ-QQQQ")]
        [InlineData("drawing$0$")]
        public void Unclear_names_have_no_target(string layer)
        {
            var result = LayerSuggest.Suggest(layer, Template, Drawing);
            Assert.Null(result.Target);
            Assert.False(result.Sure);
            Assert.EndsWith("no clear match", result.Why);
        }

        [Fact]
        public void Suggestions_never_return_the_same_name_and_ties_are_deterministic()
        {
            string[] choices = { "C-Zz_30_90-S-TablLine", "C-Zz_30_90-S-TablFill", "c-anno-tabl" };
            var result = LayerSuggest.Suggest("C-ANNO-TABL", choices, Array.Empty<string>());
            Assert.Equal("C-Zz_30_90-S-TablFill", result.Target);
            Assert.Equal(result.Target, LayerSuggest.Suggest("C-ANNO-TABL", choices.Reverse().ToArray(), null).Target);
            Assert.Null(LayerSuggest.Suggest("C-ANNO-TABL", new[] { "c-anno-tabl" }, null).Target);
        }

        [Fact]
        public void Real_drawing_reports_nonstandard_own_layers_and_suggestion_counts()
        {
            Assert.Equal(1568, Drawing.Length);
            string[] own = Drawing.Where(x => !x.Contains('|') && x != "0" && !x.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)).ToArray();
            var results = own.Where(x => !LayerSuggest.IsStandard(x, Template))
                .Select(x => LayerSuggest.Suggest(x, Template, Drawing)).ToArray();
            output.WriteLine($"Own layers: {own.Length}; non-standard: {results.Length}; Sure: {results.Count(x => x.Sure)}; unsure target: {results.Count(x => !x.Sure && x.Target != null)}; none: {results.Count(x => x.Target == null)}");
            Assert.All(results, x =>
            {
                if (x.Sure) Assert.False(string.IsNullOrEmpty(x.Target));
                Assert.False(x.Target != null && x.Layer.Equals(x.Target, StringComparison.OrdinalIgnoreCase));
            });
            Assert.Contains("C-Zz_20_10_45-T_Labels", Drawing);
            Assert.True(LayerSuggest.IsStandard("C-Zz_20_10_45-T_Labels", Template));
            foreach (string layer in Drawing.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Z]{1,2}-(?:Pr|Ss|EF|Zz|SL|En|Co|Ac|PM|FI|Ma|PC|RK|Ro|TE)(?:_\d{2}){1,5}-[A-Z]{1,2}_")))
                Assert.True(LayerSuggest.IsStandard(layer, Template), layer);
            foreach (string layer in new[]
            {
                "NCRD_ICT Network_N2 (AGC Modified)$0$O-Ss_50_35_06_14-M-IctManholeTypeC",
                "NCRD_ICT Network_N2 (AGC Modified)$0$C-ANNO-DIM", "C-ANNO-TABL", "C-ANNO-TABL-TEXT",
                "C-ANNO-TABL-TITL", "C-ROAD-CNTR", "C-PROP-TEXT", "A-BLDG", "C-ROAD-CORR", "Background"
            })
            {
                var result = LayerSuggest.Suggest(layer, Template, Drawing);
                output.WriteLine($"{layer} -> {result.Target ?? "null"}, sure={result.Sure}");
            }
        }
    }
}
