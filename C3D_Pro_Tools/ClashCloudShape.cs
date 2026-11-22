using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    /// <summary>
    /// A revision cloud's outline (plain data): the area's corners, counter-clockwise, every edge split into arcs that bulge
    /// outward. Each vertex carries the bulge of the arc that leaves it (AutoCAD polyline bulge: negative turns clockwise,
    /// which for a counter-clockwise outline bulges outward).
    /// </summary>
    internal static class ClashCloudShape
    {
        public const double Bulge = -0.5;

        /// <summary>The cloud's vertices for a closed polygon (any turn; made counter-clockwise), arcs about <paramref name="arc"/> long.</summary>
        public static List<(double X, double Y, double B)> Outline(IReadOnlyList<(double X, double Y)> corners, double arc)
        {
            var pts = corners.ToList();
            if (pts.Count < 3) return new List<(double, double, double)>();
            double area = 0;
            for (int i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; area += p.X * q.Y - q.X * p.Y; }
            if (area < 0) pts.Reverse();
            var result = new List<(double, double, double)>();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                double len = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                int n = Math.Max(1, (int)Math.Round(len / Math.Max(1e-6, arc)));
                for (int k = 0; k < n; k++)
                    result.Add((p.X + (q.X - p.X) * k / n, p.Y + (q.Y - p.Y) * k / n, Bulge));
            }
            return result;
        }

        /// <summary>The arc length for a cloud: about a twentieth of its edge, between 0.5 and 3 m.</summary>
        public static double ArcFor(IReadOnlyList<(double X, double Y)> corners)
        {
            double per = 0;
            for (int i = 0; i < corners.Count; i++) { var p = corners[i]; var q = corners[(i + 1) % corners.Count]; per += Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)); }
            return Math.Min(3.0, Math.Max(0.5, per / 20));
        }

        /// <summary>The area's box grown by the margin, as four corners (counter-clockwise).</summary>
        public static List<(double X, double Y)> Box(double minX, double minY, double maxX, double maxY, double margin) =>
            new List<(double, double)> { (minX - margin, minY - margin), (maxX + margin, minY - margin), (maxX + margin, maxY + margin), (minX - margin, maxY + margin) };
    }
}
