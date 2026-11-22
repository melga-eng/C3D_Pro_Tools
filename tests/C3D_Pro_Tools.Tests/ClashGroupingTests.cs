using System.Collections.Generic;
using System.Linq;
using C3DProTools.Navis;
using Xunit;

namespace C3DProTools.Tests
{
    public class ClashGroupingTests
    {
        /// <summary>An axis-aligned box as an element with its 12 face triangles.</summary>
        private static MainElement Box(string name, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            double[][] v =
            {
                new[] { x0, y0, z0 }, new[] { x1, y0, z0 }, new[] { x1, y1, z0 }, new[] { x0, y1, z0 },
                new[] { x0, y0, z1 }, new[] { x1, y0, z1 }, new[] { x1, y1, z1 }, new[] { x0, y1, z1 },
            };
            int[][] f = { new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 4, 5, 6 }, new[] { 4, 6, 7 }, new[] { 0, 1, 5 }, new[] { 0, 5, 4 },
                          new[] { 1, 2, 6 }, new[] { 1, 6, 5 }, new[] { 2, 3, 7 }, new[] { 2, 7, 6 }, new[] { 3, 0, 4 }, new[] { 3, 4, 7 } };
            return new MainElement
            {
                Name = name, MinX = x0, MinY = y0, MinZ = z0, MaxX = x1, MaxY = y1, MaxZ = z1,
                Points = f.SelectMany(t => t.SelectMany(i => v[i])).ToArray()
            };
        }

        /// <summary>A straight duct piece along X from x0 to x1, 0.5 m square.</summary>
        private static MainElement Duct(string name, double x0, double x1, double y = 0) => Box(name, x0, y - 0.25, 0, x1, y + 0.25, 0.5);

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
        public void A_duct_ending_in_the_middle_of_a_manhole_wall_is_connected_to_it()
        {
            // 4 x 4 x 3 m manhole; the duct ends at the middle of its east wall, 2.5 m and more from every corner.
            MainElement manhole = Box("manhole", 0, 0, 0, 4, 4, 3);
            MainElement duct = Box("duct", 4.0, 1.75, 1.25, 12, 2.25, 1.75);
            Assert.True(ClashGrouping.Touch(duct, manhole, 1.0));
            Assert.True(ClashGrouping.Touch(manhole, duct, 1.0));
            var clashes = new[] { C(1, 2, 0), C(2, 8, 1) };   // one item 2 crossing both
            Assert.Single(ClashGrouping.Group(clashes, ClashGrouping.ConnectedSets(new List<MainElement> { manhole, duct }, 1.0), new GroupingOptions()));
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
            // A flat strip along the diagonal (0,0)-(20,20): its box covers the corner piece at (18..19, 1..2), its shape does not.
            var diagonal = new MainElement
            {
                Name = "diag", MinX = 0, MaxX = 20.2, MinY = 0, MaxY = 20.2, MinZ = 0, MaxZ = 0,
                Points = new double[] { 0, 0, 0, 20, 20, 0, 20.2, 19.8, 0, 0, 0, 0, 20.2, 19.8, 0, 0.2, -0.2, 0 }
            };
            MainElement corner = Box("corner", 18, 1, 0, 19, 2, 1);
            Assert.False(ClashGrouping.Touch(diagonal, corner, 1.0));
        }

        [Fact]
        public void Point_to_triangle_distance_is_measured_to_the_face_not_the_corners() =>
            Assert.Equal(0.25, ClashGrouping.PointTriangle2(5, 5, 0.5, 0, 0, 0, 10, 0, 0, 0, 10, 0), 9);

        [Theory]
        [InlineData("IF-PIP-MVDCT-T1-212 (3)", "IF-PIP-MVDCT-T1-212")]
        [InlineData("IF-PIP-ROAD-CROSSING-DUCT-049 (3) (2)", "IF-PIP-ROAD-CROSSING-DUCT-049")]
        public void Names_lose_their_copy_numbers(string name, string expected) => Assert.Equal(expected, ClashGrouping.BaseName(name));

        [Fact]
        public void Group_names_say_status_number_main_elements_and_count() =>
            Assert.Equal("Active 01 - IF-PIP-MVDCT-T1-212 - 14 clashes", ClashGrouping.GroupName("Active", 1, new[] { "IF-PIP-MVDCT-T1-212 (3)", "IF-PIP-MVDCT-T1-212 (4)" }, 14));
    }
}
