using System.Linq;
using Xunit;

namespace C3DProTools.Tests
{
    public class ClashCloudShapeTests
    {
        [Fact]
        public void A_box_grows_by_the_margin()
        {
            var b = ClashCloudShape.Box(10, 20, 14, 22, 2);
            Assert.Equal((8.0, 18.0), b[0]);
            Assert.Equal((16.0, 24.0), b[2]);
        }

        [Fact]
        public void Edges_are_split_into_outward_arcs_of_about_the_given_length()
        {
            var o = ClashCloudShape.Outline(ClashCloudShape.Box(0, 0, 10, 4, 0), 2);
            Assert.Equal(5 + 2 + 5 + 2, o.Count);
            Assert.All(o, v => Assert.Equal(ClashCloudShape.Bulge, v.B));
        }

        [Fact]
        public void A_clockwise_outline_is_turned_counter_clockwise_so_arcs_still_bulge_outward()
        {
            var cw = ClashCloudShape.Box(0, 0, 4, 4, 0).AsEnumerable().Reverse().ToList();
            var o = ClashCloudShape.Outline(cw, 4);
            double area = 0;
            for (int i = 0; i < o.Count; i++) { var p = o[i]; var q = o[(i + 1) % o.Count]; area += p.X * q.Y - q.X * p.Y; }
            Assert.True(area > 0);
        }

        [Fact]
        public void Every_arc_bulges_out_of_the_box()
        {
            var o = ClashCloudShape.Outline(ClashCloudShape.Box(0, 0, 10, 4, 0), 2);
            for (int i = 0; i < o.Count; i++)
            {
                var p = o[i]; var q = o[(i + 1) % o.Count];
                double cx = q.X - p.X, cy = q.Y - p.Y, len = System.Math.Sqrt(cx * cx + cy * cy);
                // An AutoCAD bulge b puts the arc's middle b * chord / 2 from the chord's middle, to the right of p -> q when b > 0.
                double s = p.B * len / 2, mx = (p.X + q.X) / 2 + cy / len * s, my = (p.Y + q.Y) / 2 - cx / len * s;
                Assert.True(mx < 0 || mx > 10 || my < 0 || my > 4, $"arc {i} bulges inward ({mx:0.##}, {my:0.##})");
            }
        }

        [Fact]
        public void Arc_length_scales_with_the_area_within_limits()
        {
            Assert.Equal(0.5, ClashCloudShape.ArcFor(ClashCloudShape.Box(0, 0, 1, 1, 0)));
            Assert.Equal(3.0, ClashCloudShape.ArcFor(ClashCloudShape.Box(0, 0, 100, 100, 0)));
        }
    }
}
