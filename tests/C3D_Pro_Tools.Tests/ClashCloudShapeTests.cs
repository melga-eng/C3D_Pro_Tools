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
        public void Arc_length_scales_with_the_area_within_limits()
        {
            Assert.Equal(0.5, ClashCloudShape.ArcFor(ClashCloudShape.Box(0, 0, 1, 1, 0)));
            Assert.Equal(3.0, ClashCloudShape.ArcFor(ClashCloudShape.Box(0, 0, 100, 100, 0)));
        }
    }
}
