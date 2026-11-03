using System.Collections.Generic;

namespace C3DProTools.Tests.Helpers
{
    /// <summary>
    /// Closed prism meshes as Navisworks returns them: a flat list of triangles (x1,y1,z1,x2,y2,z2,x3,y3,z3 ...), caps
    /// fanned from their first vertex (no centre vertex).
    /// </summary>
    internal static class Meshes
    {
        /// <summary>A circular pipe (polygon of <paramref name="sides"/> sides) from a to b.</summary>
        public static double[] Cylinder(V3 a, V3 b, double radius, int sides = 48)
        {
            (V3 u, V3 w) = Frame(b - a);
            List<V3> ring = new List<V3>();
            for (int i = 0; i < sides; i++)
            {
                double t = 2 * Math.PI * i / sides;
                ring.Add(u * (radius * Math.Cos(t)) + w * (radius * Math.Sin(t)));
            }
            return Prism(a, b, ring);
        }

        /// <summary>A rectangular duct from a to b: <paramref name="width"/> across (horizontal), <paramref name="height"/> up.</summary>
        public static double[] Box(V3 a, V3 b, double width, double height)
        {
            (V3 u, V3 w) = Frame(b - a);
            double hw = width / 2, hh = height / 2;
            return Prism(a, b, new List<V3> { u * -hw + w * -hh, u * hw + w * -hh, u * hw + w * hh, u * -hw + w * hh });
        }

        /// <summary>A vertical round chamber: centre (x, y), from <paramref name="bottom"/> to <paramref name="top"/>.</summary>
        public static double[] RoundChamber(double x, double y, double bottom, double top, double diameter, int sides = 48) =>
            Cylinder(new V3(x, y, bottom), new V3(x, y, top), diameter / 2, sides);

        /// <summary>A vertical rectangular chamber: <paramref name="length"/> along the plan direction <paramref name="rotation"/> (radians from +X).</summary>
        public static double[] RectChamber(double x, double y, double bottom, double top, double length, double width, double rotation)
        {
            V3 along = new V3(Math.Cos(rotation), Math.Sin(rotation), 0), across = new V3(-Math.Sin(rotation), Math.Cos(rotation), 0);
            double hl = length / 2, hw = width / 2;
            List<V3> ring = new List<V3> { along * -hl + across * -hw, along * hl + across * -hw, along * hl + across * hw, along * -hl + across * hw };
            return Prism(new V3(x, y, bottom), new V3(x, y, top), ring);
        }

        private static (V3 u, V3 w) Frame(V3 axis)
        {
            V3 d = axis.Unit;
            // U horizontal across the axis (any horizontal direction for a vertical axis), W completes the frame.
            V3 u = Math.Abs(d.Z) > 0.99 ? new V3(1, 0, 0) : new V3(0, 0, 1).Cross(d).Unit;
            V3 w = d.Cross(u).Unit;
            return (u, w);
        }

        private static double[] Prism(V3 a, V3 b, List<V3> ring)
        {
            List<double> t = new List<double>();
            void Tri(V3 p, V3 q, V3 r) { t.AddRange(new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z, r.X, r.Y, r.Z }); }
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                V3 p0 = a + ring[i], p1 = a + ring[(i + 1) % n], q0 = b + ring[i], q1 = b + ring[(i + 1) % n];
                Tri(p0, p1, q1);
                Tri(p0, q1, q0);
            }
            for (int i = 1; i + 1 < n; i++)
            {
                Tri(a + ring[0], a + ring[i + 1], a + ring[i]);
                Tri(b + ring[0], b + ring[i], b + ring[i + 1]);
            }
            return t.ToArray();
        }
    }
}
