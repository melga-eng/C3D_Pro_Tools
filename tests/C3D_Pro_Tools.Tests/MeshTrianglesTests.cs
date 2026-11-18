using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Surfaces;
using Xunit;

namespace C3DProTools.Tests
{
    public class MeshTrianglesTests
    {
        private static P3 P(double x, double y, double z) => new P3(x, y, z);

        /// <summary>A 10 x 4 x 1 box, faces outward (counter-clockwise seen from outside).</summary>
        private static List<Tri> Box()
        {
            P3 a = P(0, 0, 0), b = P(10, 0, 0), c = P(10, 4, 0), d = P(0, 4, 0), e = P(0, 0, 1), f = P(10, 0, 1), g = P(10, 4, 1), h = P(0, 4, 1);
            return new List<Tri>
            {
                new Tri(e, f, g), new Tri(e, g, h),           // top, up
                new Tri(a, c, b), new Tri(a, d, c),           // bottom, down
                new Tri(a, b, f), new Tri(a, f, e), new Tri(b, c, g), new Tri(b, g, f),
                new Tri(c, d, h), new Tri(c, h, g), new Tri(d, a, e), new Tri(d, e, h),
            };
        }

        [Fact]
        public void A_closed_solid_gives_its_up_faces_as_top_and_down_faces_as_bottom()
        {
            List<Tri> box = Box();
            Assert.True(MeshTriangles.IsClosed(box));
            List<Tri> top = MeshTriangles.Side(box, true, SurfaceSide.Top), bottom = MeshTriangles.Side(box, true, SurfaceSide.Bottom);
            Assert.Equal(2, top.Count);
            Assert.All(top, t => Assert.Equal(1, t.A.Z));
            Assert.Equal(2, bottom.Count);
            Assert.All(bottom, t => Assert.Equal(0, t.A.Z));
        }

        [Fact]
        public void An_open_sheet_is_its_own_top_and_bottom_whichever_way_it_faces()
        {
            var sheet = new List<Tri> { new Tri(P(0, 0, 0), P(0, 5, 1), P(5, 0, 2)) };  // faces down
            Assert.False(MeshTriangles.IsClosed(sheet));
            Assert.Single(MeshTriangles.Side(sheet, false, SurfaceSide.Top));
            Assert.Single(MeshTriangles.Side(sheet, false, SurfaceSide.Bottom));
        }

        [Fact]
        public void A_polygon_is_split_into_triangles_that_keep_its_direction()
        {
            var l = new List<P3> { P(0, 0, 5), P(4, 0, 5), P(4, 2, 5), P(2, 1, 5), P(0, 2, 5) };  // concave, counter-clockwise
            List<Tri> t = MeshTriangles.Triangulate(l);
            Assert.Equal(3, t.Count);
            Assert.All(t, x => Assert.True(x.NormalZ > 0.99));
            Assert.Equal(6, t.Sum(x => x.PlanArea), 6);
        }

        [Fact]
        public void The_outline_of_two_joined_pieces_is_one_loop_and_a_hole_is_a_hole()
        {
            // Two 1 x 1 squares side by side (sharing an edge), and a 3 x 3 square with a 1 x 1 hole.
            var tris = new List<Tri>
            {
                new Tri(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0)), new Tri(P(0, 0, 0), P(1, 1, 0), P(0, 1, 0)),
                new Tri(P(1, 0, 0), P(2, 0, 0), P(2, 1, 0)), new Tri(P(1, 0, 0), P(2, 1, 0), P(1, 1, 0)),
            };
            List<List<P3>> loops = MeshTriangles.Outline(tris);
            Assert.Single(loops);
            Assert.Equal(2, MeshTriangles.PlanArea(loops[0]), 6);

            var ring = new List<Tri>();
            void Sq(double x, double y) { ring.Add(new Tri(P(x, y, 0), P(x + 1, y, 0), P(x + 1, y + 1, 0))); ring.Add(new Tri(P(x, y, 0), P(x + 1, y + 1, 0), P(x, y + 1, 0))); }
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (!(i == 1 && j == 1)) Sq(10 + i, j);
            var (outer, holes) = MeshTriangles.Nest(MeshTriangles.Outline(ring));
            Assert.Single(outer);
            Assert.Single(holes);
            Assert.Equal(9, MeshTriangles.PlanArea(outer[0]), 6);
            Assert.Equal(1, MeshTriangles.PlanArea(holes[0]), 6);
        }
    }
}
