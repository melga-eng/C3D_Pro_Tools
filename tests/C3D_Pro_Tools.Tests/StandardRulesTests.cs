using C3DProTools.Features.Standards;
using Xunit;

namespace C3DProTools.Tests
{
    public class StandardRulesTests
    {
        [Theory]
        [InlineData(25, true)]
        [InlineData(18, true)]
        [InlineData(0, false)]
        [InlineData(15, false)]
        [InlineData(30, false)]
        [InlineData(-3, true)]
        public void Line_weights_follow_STD_8(int lw, bool ok) => Assert.Equal(ok, StandardRules.LineWeightAllowed(lw));

        [Theory]
        [InlineData(500, "allowed")]
        [InlineData(500.4, "allowed")]
        [InlineData(1250, "allowed")]
        [InlineData(150, "not preferred")]
        [InlineData(300, "not allowed")]
        [InlineData(0, "not allowed")]
        public void Viewport_scales_follow_STD_9(double n, string state) => Assert.Equal(state, StandardRules.ScaleState(n));

        [Theory]
        [InlineData("Arial Narrow", "", true)]
        [InlineData("", @"C:\Windows\Fonts\ARIALN.TTF", true)]
        [InlineData("Arial", "arial.ttf", false)]
        [InlineData("", "romans.shx", false)]
        public void Fonts_follow_STD_6(string face, string file, bool ok) => Assert.Equal(ok, StandardRules.FontAllowed(face, file));

        [Theory]
        [InlineData("05/03/2026", true)]
        [InlineData("5/3/2026", false)]
        [InlineData("05.03.26", false)]
        [InlineData("31/02/2026", false)]
        public void Dates_are_DD_MM_YYYY(string v, bool ok) => Assert.Equal(ok, StandardRules.DateOk(v));

        [Theory]
        [InlineData("1:500", true)]
        [InlineData("1 : 1,000", true)]
        [InlineData("NTS", true)]
        [InlineData("As Shown", true)]
        [InlineData("1:300", false)]
        [InlineData("1:xxx", false)]
        public void Scale_box_text(string v, bool ok) => Assert.Equal(ok, StandardRules.ScaleTextOk(v));

        [Fact]
        public void Xref_paths_made_relative_to_the_drawing()
        {
            Assert.Equal(@"..\IFC\x.dwg", StandardRules.RelativeTo(@"D:\Work\Tile\a.dwg", @"D:\Work\IFC\x.dwg"));
            Assert.Equal(@".\Xref\x.dwg", StandardRules.RelativeTo(@"D:\Work\a.dwg", @"D:\Work\Xref\x.dwg"));
            Assert.Null(StandardRules.RelativeTo(@"D:\Work\a.dwg", @"C:\Other\x.dwg"));
            Assert.True(StandardRules.IsRelativePath(@"..\x.dwg"));
            Assert.False(StandardRules.IsRelativePath(@"C:\x.dwg"));
        }
    }
}
