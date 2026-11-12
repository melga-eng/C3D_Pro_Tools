using C3DProTools;
using Xunit;

namespace C3D_Pro_Tools.Tests
{
    public class NamePatternsTests
    {
        private static readonly string[] Alignments = { "ICT-ALI-124-1-89-01", "ICT-ALI-124-1-89-02", "ICT-ALI-125-2-01" };

        [Fact]
        public void KeepsPatternsThatMatch()
        {
            Assert.Equal("ICT-ALI-124*", NamePatterns.Widen("ICT-ALI-124*", Alignments, "alignment", out string note));
            Assert.Null(note);
        }

        [Fact]
        public void WidensAMissingPrefix()
        {
            // The user typed 124*; the drawing's names carry ICT-ALI- in front.
            string used = NamePatterns.Widen("124*", Alignments, "alignment", out string note);
            Assert.Equal("*124*", used);
            Assert.Contains("2 found", note);
            Assert.True(NamePatterns.Matches(used, "ICT-ALI-124-1-89-01"));
            Assert.False(NamePatterns.Matches(used, "ICT-ALI-125-2-01"));
        }

        [Fact]
        public void KeepsExclusionsWhenWidening()
        {
            string used = NamePatterns.Widen("12*,!*-02", Alignments, "alignment", out _);
            Assert.Equal("*12*,!*-02", used);
            Assert.True(NamePatterns.Matches(used, "ICT-ALI-124-1-89-01"));
            Assert.False(NamePatterns.Matches(used, "ICT-ALI-124-1-89-02"));
        }

        [Fact]
        public void ListsNamesWhenNothingMatches()
        {
            string used = NamePatterns.Widen("999", Alignments, "alignment", out string note);
            Assert.Equal("999", used);
            Assert.Contains("ICT-ALI-124-1-89-01", note);
        }
    }
}
