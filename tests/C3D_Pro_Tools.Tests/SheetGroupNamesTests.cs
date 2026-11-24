using System.Linq;
using Xunit;

namespace C3DProTools.Tests
{
    /// <summary>Sheet groups: names, the list of groups, and the tab order that puts each group together.</summary>
    public class SheetGroupNamesTests
    {
        [Theory]
        [InlineData("  Tile 124  ", "Tile 124")]
        [InlineData("Profiles   MV", "Profiles MV")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void NamesAreCleaned(string name, string expected) => Assert.Equal(expected, SheetGroupNames.Clean(name));

        [Fact]
        public void GroupsInTabOrderWithCounts()
        {
            var groups = SheetGroupNames.Groups(new[] { "", "Plans", "Profiles", "plans", " ", "Profiles", "Plans" });
            Assert.Equal(new[] { ("Plans", 3), ("Profiles", 2) }, groups.ToArray());
        }

        [Fact]
        public void EachGroupTogetherWhereItsFirstSheetIs()
        {
            // Tabs: Cover, P1 (Plans), X1 (Profiles), P2 (Plans), Notes, X2 (Profiles)
            var order = SheetGroupNames.Together(new[] { "", "Plans", "Profiles", "plans", "", "Profiles" });
            Assert.Equal(new[] { 0, 1, 3, 2, 5, 4 }, order.ToArray());
        }

        [Fact]
        public void NoGroupsKeepsTheOrder() => Assert.Equal(new[] { 0, 1, 2 }, SheetGroupNames.Together(new[] { "", "", "" }).ToArray());
    }
}
