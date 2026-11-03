using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    /// <summary>A plain 3D vector, so the fitting code has no AutoCAD dependency and can be tested on its own.</summary>
    internal readonly struct V3
    {
        public readonly double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public V3 Cross(V3 b) => new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V3 Unit => this * (1.0 / Length);
        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####})";
    }

    internal enum FitShape { None, Circular, Rectangular }

    /// <summary>A straight prism (pipe, culvert, duct bank) measured from its mesh.</summary>
    internal sealed class LinearFit
    {
        public V3 Start, End;
        public FitShape Shape;
        /// <summary>Outer diameter (circular) or outer width × height (rectangular), in model units.</summary>
        public double OuterDiameter, Width, Height;
        /// <summary>Inner size when the mesh has an inner wall; 0 for solid meshes.</summary>
        public double InnerDiameter, InnerWidth, InnerHeight;
        /// <summary>Rotation of a rectangular section about the axis, from upright (radians). Civil 3D boxes are always upright.</summary>
        public double Roll;
        /// <summary>Largest distance of any vertex from the fitted shape.</summary>
        public double MaxDeviation;
        public bool Straight = true;
        public string Note = "";
        /// <summary>Section frame: U across the pipe (horizontal), W up. Barrel offsets are measured along these.</summary>
        public V3 U, W;
        /// <summary>Centres of each barrel relative to the axis when the mesh holds several pipes side by side (twin culverts).</summary>
        public List<(double u, double w)> Barrels = new List<(double u, double w)>();
        public double Length => (End - Start).Length;
    }

    /// <summary>A structure measured in plan: a circle or a rotated rectangle, with its top and bottom.</summary>
    internal sealed class FootprintFit
    {
        public double CenterX, CenterY, Top, Bottom;
        public FitShape Shape;
        public double Diameter, Width, Length;
        /// <summary>Direction of the rectangle's length in plan (radians from +X).</summary>
        public double Rotation;
        public double MaxDeviation;
    }

    /// <summary>Measures pipes and structures from the triangles Navisworks returns, in world coordinates.</summary>
    internal static class NavisGeometryFit
    {
        /// <summary>Vertices closer than this are the same point (model units, metres).</summary>
        private const double Merge = 1e-5;

        public static List<V3> UniqueVertices(IReadOnlyList<double> triangles)
        {
            Dictionary<(long, long, long), V3> unique = new Dictionary<(long, long, long), V3>();
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                V3 v = new V3(triangles[i], triangles[i + 1], triangles[i + 2]);
                unique[((long)Math.Round(v.X / Merge), (long)Math.Round(v.Y / Merge), (long)Math.Round(v.Z / Merge))] = v;
            }
            return unique.Values.ToList();
        }

        /// <summary>
        /// Fits a straight prism. The axis comes from the vertices' principal direction, then from the centres of the two
        /// end caps; the section is read from the cap vertices. Returns null when the mesh is not a long, straight shape.
        /// </summary>
        public static LinearFit FitLinear(IReadOnlyList<double> triangles)
        {
            List<V3> pts = UniqueVertices(triangles);
            if (pts.Count < 6) return null;
            V3 c = Centroid(pts);
            V3 axis = PrincipalAxis(pts, c);

            // Two passes: the second uses the axis through the end-cap centres, which is exact for symmetric sections.
            List<V3> startCap = null, endCap = null;
            double tMin = 0, tMax = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                tMin = double.MaxValue; tMax = double.MinValue;
                foreach (V3 p in pts) { double t = (p - c).Dot(axis); tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t); }
                double tol = Math.Max(1e-4, (tMax - tMin) * 2e-3);
                startCap = pts.Where(p => (p - c).Dot(axis) - tMin < tol).ToList();
                endCap = pts.Where(p => tMax - (p - c).Dot(axis) < tol).ToList();
                if (startCap.Count < 3 || endCap.Count < 3) return null;
                if (pass == 0)
                {
                    V3 dir = Centroid(endCap) - Centroid(startCap);
                    if (dir.Length < 1e-9) return null;
                    axis = dir.Unit;
                }
            }

            LinearFit fit = new LinearFit { Start = Centroid(startCap), End = Centroid(endCap) };
            double length = fit.Length;
            int middle = pts.Count - startCap.Count - endCap.Count;
            if (middle > 0)
            {
                fit.Straight = false;
                fit.Note = $"{middle} vertices between the ends (curved, mitred or detailed shape)";
            }

            // Section frame: u horizontal across the pipe, w "up" (perpendicular to the axis, as close to +Z as possible).
            V3 a = (fit.End - fit.Start).Unit;
            V3 u = Math.Abs(a.Z) > 0.999 ? new V3(1, 0, 0) : new V3(0, 0, 1).Cross(a).Unit;
            V3 w = a.Cross(u);
            if (w.Z < 0) { u = u * -1; w = w * -1; }
            fit.U = u;
            fit.W = w;

            List<(double u, double w)> sec = new List<(double, double)>();
            foreach (V3 p in startCap) { V3 d = p - fit.Start; sec.Add((d.Dot(u), d.Dot(w))); }
            foreach (V3 p in endCap) { V3 d = p - fit.End; sec.Add((d.Dot(u), d.Dot(w))); }
            ClassifySection(fit, sec, Math.Max(startCap.Count, endCap.Count));

            // Deviation of every vertex from the fitted prism, including any between the ends.
            double dev = 0;
            foreach (V3 p in pts)
            {
                double t = (p - fit.Start).Dot(a);
                V3 d = p - fit.Start - a * t;
                double pu = d.Dot(u), pw = d.Dot(w);
                double e = Math.Max(Math.Max(-t, t - length), 0);
                e = Math.Max(e, SectionDistance(fit, pu, pw));
                dev = Math.Max(dev, e);
            }
            fit.MaxDeviation = dev;
            return fit;
        }

        private static void ClassifySection(LinearFit fit, List<(double u, double w)> sec, int perCap)
        {
            if (TryBarrels(fit, sec)) return;
            List<double> radii = sec.Select(p => Math.Sqrt(p.u * p.u + p.w * p.w)).OrderBy(r => r).ToList();
            List<List<double>> rings = Cluster(radii, 0.002);
            double outerR = rings.Last().Average();
            int outerCount = sec.Count(p => Math.Abs(Math.Sqrt(p.u * p.u + p.w * p.w) - outerR) < 0.002);

            // A circle needs many points on one radius; a rectangle has four corners (eight with an inner wall).
            bool circular = rings.Count <= 2 && outerCount / 2 >= 6 && rings.All(r => r.Max() - r.Min() < Math.Max(0.002, r.Average() * 0.01));
            if (circular)
            {
                fit.Shape = FitShape.Circular;
                fit.OuterDiameter = 2 * outerR;
                if (rings.Count == 2) fit.InnerDiameter = 2 * rings[0].Average();
                return;
            }

            // Rectangle: minimum-area rectangle of the outer points gives the roll; widths are measured in that frame.
            List<(double u, double w)> outer = sec.Where(p => Math.Sqrt(p.u * p.u + p.w * p.w) > outerR * 0.98 - 0.002).ToList();
            double roll = MinAreaRectangleAngle(outer);
            // Keep the roll within ±45°, so width stays the horizontal side.
            while (roll > Math.PI / 4) roll -= Math.PI / 2;
            while (roll < -Math.PI / 4) roll += Math.PI / 2;
            (double cu, double cw) R((double u, double w) p) => (p.u * Math.Cos(roll) + p.w * Math.Sin(roll), -p.u * Math.Sin(roll) + p.w * Math.Cos(roll));
            List<(double u, double w)> rot = sec.Select(R).ToList();
            fit.Shape = FitShape.Rectangular;
            fit.Roll = roll;
            fit.Width = rot.Max(p => p.u) - rot.Min(p => p.u);
            fit.Height = rot.Max(p => p.w) - rot.Min(p => p.w);
            if (rings.Count >= 2)
            {
                List<(double u, double w)> inner = rot.Where(p => Math.Abs(p.u) < fit.Width / 2 - 0.002 && Math.Abs(p.w) < fit.Height / 2 - 0.002).ToList();
                if (inner.Count >= 4)
                {
                    fit.InnerWidth = inner.Max(p => p.u) - inner.Min(p => p.u);
                    fit.InnerHeight = inner.Max(p => p.w) - inner.Min(p => p.w);
                }
            }
        }

        /// <summary>
        /// Several equal circles side by side (a twin or triple culvert drawn as one object): the section points split into
        /// groups with clear gaps across the pipe, and each group is a circle of the same size.
        /// </summary>
        private static bool TryBarrels(LinearFit fit, List<(double u, double w)> sec)
        {
            double minU = sec.Min(p => p.u), maxU = sec.Max(p => p.u);
            if (maxU - minU <= 0) return false;
            for (int k = 2; k <= 4; k++)
            {
                List<(double u, double w, double d)> circles = TryBarrelCount(sec, k, minU, maxU);
                if (circles == null) continue;
                fit.Shape = FitShape.Circular;
                fit.OuterDiameter = circles.Average(c => c.d);
                fit.Barrels = circles.Select(c => (c.u, c.w)).ToList();
                return true;
            }
            return false;
        }

        /// <summary>Groups the section points into <paramref name="k"/> barrels across the width (1-D k-means) and checks each is the same circle.</summary>
        private static List<(double u, double w, double d)> TryBarrelCount(List<(double u, double w)> sec, int k, double minU, double maxU)
        {
            double[] centres = Enumerable.Range(0, k).Select(i => minU + (maxU - minU) * (i + 0.5) / k).ToArray();
            int[] owner = new int[sec.Count];
            for (int iter = 0; iter < 30; iter++)
            {
                bool moved = false;
                for (int i = 0; i < sec.Count; i++)
                {
                    int best = 0;
                    for (int c = 1; c < k; c++) if (Math.Abs(sec[i].u - centres[c]) < Math.Abs(sec[i].u - centres[best])) best = c;
                    if (owner[i] != best) { owner[i] = best; moved = true; }
                }
                for (int c = 0; c < k; c++)
                {
                    List<double> us = sec.Where((p, i) => owner[i] == c).Select(p => p.u).ToList();
                    if (us.Count > 0) centres[c] = (us.Min() + us.Max()) / 2;
                }
                if (!moved && iter > 0) break;
            }

            List<(double u, double w, double d)> circles = new List<(double u, double w, double d)>();
            for (int c = 0; c < k; c++)
            {
                List<(double u, double w)> g = sec.Where((p, i) => owner[i] == c).ToList();
                if (g.Count < 12 || !FitCircle(g, out double cu, out double cw, out double radius)) return null;
                // Every outline vertex of a barrel lies on its circle (a joint outline adds no other points).
                if (g.Any(p => Math.Abs(Math.Sqrt((p.u - cu) * (p.u - cu) + (p.w - cw) * (p.w - cw)) - radius) > Math.Max(0.002, radius * 0.01))) return null;
                circles.Add((cu, cw, 2 * radius));
            }
            double d0 = circles[0].d;
            if (circles.Any(c => Math.Abs(c.d - d0) > Math.Max(0.003, d0 * 0.01))) return null;
            // Barrels must not overlap; overlapping "circles" are really one odd shape.
            List<(double u, double w, double d)> ordered = circles.OrderBy(c => c.u).ToList();
            for (int i = 1; i < ordered.Count; i++) if (ordered[i].u - ordered[i - 1].u < d0 * 0.98) return null;
            return ordered;
        }

        /// <summary>Distance of a section point from the fitted outline (outside or inside the wall line).</summary>
        private static double SectionDistance(LinearFit fit, double u, double w)
        {
            if (fit.Barrels.Count > 1)
                return fit.Barrels.Min(b => Math.Max(Math.Sqrt((u - b.u) * (u - b.u) + (w - b.w) * (w - b.w)) - fit.OuterDiameter / 2, 0));
            if (fit.Shape == FitShape.Circular)
            {
                double r = Math.Sqrt(u * u + w * w);
                double d = Math.Abs(r - fit.OuterDiameter / 2);
                if (fit.InnerDiameter > 0) d = Math.Min(d, Math.Abs(r - fit.InnerDiameter / 2));
                return r > fit.OuterDiameter / 2 ? r - fit.OuterDiameter / 2 : (fit.InnerDiameter > 0 ? d : 0);
            }
            double cu = u * Math.Cos(fit.Roll) + w * Math.Sin(fit.Roll);
            double cw = -u * Math.Sin(fit.Roll) + w * Math.Cos(fit.Roll);
            return Math.Max(Math.Max(Math.Abs(cu) - fit.Width / 2, Math.Abs(cw) - fit.Height / 2), 0);
        }

        /// <summary>Fits a structure's plan outline (circle or rotated rectangle) and its vertical extent.</summary>
        public static FootprintFit FitFootprint(IReadOnlyList<double> triangles)
        {
            List<V3> pts = UniqueVertices(triangles);
            if (pts.Count < 3) return null;
            List<(double x, double y)> hull = ConvexHull(pts.Select(p => (p.X, p.Y)).ToList());
            FootprintFit fit = new FootprintFit { Top = pts.Max(p => p.Z), Bottom = pts.Min(p => p.Z) };

            double angle = MinAreaRectangleAngle(hull);
            double cs = Math.Cos(angle), sn = Math.Sin(angle);
            List<(double a, double b)> local = hull.Select(p => (p.x * cs + p.y * sn, -p.x * sn + p.y * cs)).ToList();
            double minA = local.Min(p => p.a), maxA = local.Max(p => p.a), minB = local.Min(p => p.b), maxB = local.Max(p => p.b);
            double ca = (minA + maxA) / 2, cb = (minB + maxB) / 2;
            double rectX = ca * cs - cb * sn, rectY = ca * sn + cb * cs;

            // Circle when the hull has many points at one distance from its centre.
            double hx = hull.Average(p => p.x), hy = hull.Average(p => p.y);
            List<double> r = hull.Select(p => Math.Sqrt((p.x - hx) * (p.x - hx) + (p.y - hy) * (p.y - hy))).ToList();
            bool circle = hull.Count >= 8 && r.Max() - r.Min() < Math.Max(0.003, r.Average() * 0.01);
            if (circle)
            {
                fit.Shape = FitShape.Circular;
                fit.CenterX = hx; fit.CenterY = hy;
                fit.Diameter = 2 * r.Average();
                fit.MaxDeviation = r.Max() - r.Min();
            }
            else
            {
                fit.Shape = FitShape.Rectangular;
                fit.CenterX = rectX; fit.CenterY = rectY;
                fit.Length = maxA - minA;
                fit.Width = maxB - minB;
                fit.Rotation = angle;
                if (fit.Width > fit.Length) { (fit.Width, fit.Length) = (fit.Length, fit.Width); fit.Rotation += Math.PI / 2; }
                // How far the outline is from a rectangle: distance of each hull point from the nearest side.
                fit.MaxDeviation = local.Max(p => Math.Min(Math.Min(p.a - minA, maxA - p.a), Math.Min(p.b - minB, maxB - p.b)));
            }
            return fit;
        }

        // ---------------- helpers ----------------

        private static V3 Centroid(List<V3> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (V3 p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new V3(x / pts.Count, y / pts.Count, z / pts.Count);
        }

        /// <summary>Direction of largest spread (dominant eigenvector of the covariance, by power iteration).</summary>
        private static V3 PrincipalAxis(List<V3> pts, V3 c)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (V3 p in pts)
            {
                V3 d = p - c;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }
            // Start from the bounding diagonal so a pipe along any direction converges quickly.
            V3 v = new V3(pts.Max(p => p.X) - pts.Min(p => p.X) + 1e-6, pts.Max(p => p.Y) - pts.Min(p => p.Y) + 2e-6, pts.Max(p => p.Z) - pts.Min(p => p.Z) + 3e-6).Unit;
            for (int i = 0; i < 200; i++)
            {
                V3 n = new V3(xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z);
                if (n.Length < 1e-12) break;
                n = n.Unit;
                if ((n - v).Length < 1e-12) { v = n; break; }
                v = n;
            }
            return v;
        }

        /// <summary>Least-squares circle through the points (algebraic fit: x² + y² + Dx + Ey + F = 0).</summary>
        private static bool FitCircle(List<(double u, double w)> pts, out double cu, out double cw, out double radius)
        {
            cu = cw = radius = 0;
            // Work relative to the mean so the normal equations stay well conditioned.
            double mu = pts.Average(p => p.u), mw = pts.Average(p => p.w);
            double suu = 0, suw = 0, sww = 0, su = 0, sw = 0, suz = 0, swz = 0, sz = 0;
            int n = pts.Count;
            foreach ((double u0, double w0) in pts)
            {
                double u = u0 - mu, w = w0 - mw, z = u * u + w * w;
                suu += u * u; suw += u * w; sww += w * w; su += u; sw += w; suz += u * z; swz += w * z; sz += z;
            }
            // Solve [suu suw su; suw sww sw; su sw n] [D E F]ᵀ = -[suz swz sz]ᵀ by Cramer's rule.
            double det = suu * (sww * n - sw * sw) - suw * (suw * n - sw * su) + su * (suw * sw - sww * su);
            if (Math.Abs(det) < 1e-18) return false;
            double D = -(suz * (sww * n - sw * sw) - suw * (swz * n - sw * sz) + su * (swz * sw - sww * sz)) / det;
            double E = -(suu * (swz * n - sz * sw) - suz * (suw * n - sw * su) + su * (suw * sz - swz * su)) / det;
            double F = -(suu * (sww * sz - sw * swz) - suw * (suw * sz - swz * su) + suz * (suw * sw - sww * su)) / det;
            double r2 = D * D / 4 + E * E / 4 - F;
            if (r2 <= 0) return false;
            cu = mu - D / 2;
            cw = mw - E / 2;
            radius = Math.Sqrt(r2);
            return true;
        }

        private static List<List<double>> Cluster(List<double> sorted, double gap)
        {
            List<List<double>> groups = new List<List<double>>();
            foreach (double r in sorted)
            {
                if (groups.Count == 0 || r - groups.Last().Last() > gap) groups.Add(new List<double>());
                groups.Last().Add(r);
            }
            return groups;
        }

        private static List<(double x, double y)> ConvexHull(List<(double x, double y)> p)
        {
            List<(double x, double y)> pts = p.Distinct().OrderBy(q => q.x).ThenBy(q => q.y).ToList();
            if (pts.Count < 3) return pts;
            double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            List<(double x, double y)> hull = new List<(double x, double y)>();
            foreach (var q in pts) { while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 1e-12) hull.RemoveAt(hull.Count - 1); hull.Add(q); }
            int lower = hull.Count + 1;
            for (int i = pts.Count - 2; i >= 0; i--) { var q = pts[i]; while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 1e-12) hull.RemoveAt(hull.Count - 1); hull.Add(q); }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        /// <summary>Angle of the minimum-area enclosing rectangle (one side is always collinear with a hull edge).</summary>
        private static double MinAreaRectangleAngle(List<(double x, double y)> points)
        {
            List<(double x, double y)> hull = ConvexHull(points);
            if (hull.Count < 3) return 0;
            double bestArea = double.MaxValue, best = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                double ang = Math.Atan2(b.y - a.y, b.x - a.x);
                double cs = Math.Cos(ang), sn = Math.Sin(ang);
                double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                foreach (var q in hull)
                {
                    double pa = q.x * cs + q.y * sn, pb = -q.x * sn + q.y * cs;
                    minA = Math.Min(minA, pa); maxA = Math.Max(maxA, pa); minB = Math.Min(minB, pb); maxB = Math.Max(maxB, pb);
                }
                double area = (maxA - minA) * (maxB - minB);
                if (area < bestArea - 1e-12) { bestArea = area; best = ang; }
            }
            return best;
        }
    }
}
