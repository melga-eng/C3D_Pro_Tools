using System.Collections.Generic;
using System.Linq;
using C3DProTools.Navis;
using Xunit;

namespace C3DProTools.Tests
{
    public class ClashGroupingTests
    {
        /// <summary>A straight duct piece along X from x0 to x1 at y, 0.5 m wide, with its end points.</summary>
        private static MainElement Duct(string name, double x0, double x1, double y = 0) => new MainElement
        {
            Name = name, MinX = x0, MaxX = x1, MinY = y - 0.25, MaxY = y + 0.25, MinZ = 0, MaxZ = 0.5,
            Points = new[] { x0, y, 0.0, x1, y, 0.0, (x0 + x1) / 2, y, 0.0 }
        };

        private static ClashPoint C(int i, double x, int element, string status = "New", double y = 0) =>
            new ClashPoint { Index = i, X = x, Y = y, Element = element, Status = status };

        [Fact]
        public void Pieces_touching_end_to_end_are_one_connected_set_through_a_piece_that_does_not_clash()
        {
            var e = new List<MainElement> { Duct("A", 0, 10), Duct("B", 10.5, 20), Duct("C", 20.8, 30), Duct("far", 40, 50) };
            int[] set = ClashGrouping.ConnectedSets(e, 1.0);
            Assert.Equal(set[0], set[2]);       // A-B-C chained, within 1 m at each joint
            Assert.NotEqual(set[0], set[3]);    // 10 m away
        }

        [Fact]
        public void Clashes_on_connected_pieces_group_within_the_span_and_singles_stay_single()
        {
            var e = new List<MainElement> { Duct("IF-PIP-MVDCT-T1-212 (3)", 0, 10), Duct("IF-PIP-MVDCT-T1-212 (4)", 10.2, 20), Duct("other", 100, 110) };
            int[] set = ClashGrouping.ConnectedSets(e, 1.0);
            var clashes = new[] { C(1, 5, 0), C(2, 6, 0), C(3, 15, 1), C(4, 105, 2) };
            List<List<int>> g = ClashGrouping.Group(clashes, set, new GroupingOptions());
            Assert.Single(g);
            Assert.Equal(new[] { 1, 2, 3 }, g[0]);
        }

        [Fact]
        public void No_group_spans_more_than_the_largest_span_even_along_one_route()
        {
            var e = new List<MainElement> { Duct("route", 0, 100) };
            var clashes = Enumerable.Range(0, 11).Select(i => C(i, i * 10, 0)).ToArray();   // every 10 m along 100 m
            List<List<int>> g = ClashGrouping.Group(clashes, new[] { 0 }, new GroupingOptions { MaxSpan = 25 });
            Assert.All(g, grp => Assert.True(grp.Max() * 10 - grp.Min() * 10 <= 25));
            Assert.True(g.Count >= 3);
        }

        [Fact]
        public void Only_clashes_of_one_status_group_together_and_unticked_statuses_stay_ungrouped()
        {
            var clashes = new[] { C(1, 0, 0, "Active"), C(2, 1, 0, "Active"), C(3, 2, 0, "Approved"), C(4, 3, 0, "Approved"), C(5, 4, 0, "Resolved"), C(6, 5, 0, "Resolved") };
            var o = new GroupingOptions();
            o.Statuses = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Active", "Approved" };
            List<List<int>> g = ClashGrouping.Group(clashes, new[] { 0 }, o);
            Assert.Equal(2, g.Count);
            Assert.Contains(g, x => x.SequenceEqual(new[] { 1, 2 }));
            Assert.Contains(g, x => x.SequenceEqual(new[] { 3, 4 }));
        }

        [Fact]
        public void A_diagonal_duct_whose_box_overlaps_but_whose_shape_is_far_is_not_connected()
        {
            var diagonal = new MainElement { Name = "diag", MinX = 0, MaxX = 20, MinY = 0, MaxY = 20, MinZ = 0, MaxZ = 1, Points = new[] { 0, 0, 0.0, 20, 20, 0.0, 10, 10, 0.0 } };
            var corner = new MainElement { Name = "corner", MinX = 18, MaxX = 19, MinY = 1, MaxY = 2, MinZ = 0, MaxZ = 1, Points = new[] { 18, 1, 0.0, 19, 2, 0.0 } };
            Assert.False(ClashGrouping.Touch(diagonal, corner, 1.0));
        }

        [Theory]
        [InlineData("IF-PIP-MVDCT-T1-212 (3)", "IF-PIP-MVDCT-T1-212")]
        [InlineData("IF-PIP-ROAD-CROSSING-DUCT-049 (3) (2)", "IF-PIP-ROAD-CROSSING-DUCT-049")]
        public void Names_lose_their_copy_numbers(string name, string expected) => Assert.Equal(expected, ClashGrouping.BaseName(name));

        [Fact]
        public void Group_names_say_status_number_main_elements_and_count() =>
            Assert.Equal("Active 01 - IF-PIP-MVDCT-T1-212 - 14 clashes", ClashGrouping.GroupName("Active", 1, new[] { "IF-PIP-MVDCT-T1-212 (3)", "IF-PIP-MVDCT-T1-212 (4)" }, 14));
    }
}
