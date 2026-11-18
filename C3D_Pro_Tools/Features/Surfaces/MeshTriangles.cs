using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Features.Surfaces
{
    /// <summary>A point in metres.</summary>
    internal readonly struct P3
    {
        public readonly double X, Y, Z;
        public P3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>A triangle with its corners in the order that gives its outward normal (right-hand rule).</summary>
    internal readonly struct Tri
    {
        public readonly P3 A, B, C;
        public Tri(P3 a, P3 b, P3 c) { A = a; B = b; C = c; }

        /// <summary>The Z part of the unit normal: 1 facing straight up, -1 straight down, 0 vertical.</summary>
        public double NormalZ
        {
            get
            {
                double ux = B.X - A.X, uy = B.Y - A.Y, uz = B.Z - A.Z, vx = C.X - A.X, vy = C.Y - A.Y, vz = C.Z - A.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                return len < 1e-12 ? 0 : nz / len;
            }
        }

        public double PlanArea => Math.Abs((B.X - A.X) * (C.Y - A.Y) - (C.X - A.X) * (B.Y - A.Y)) / 2;
    }

    internal enum SurfaceSide { Top, Bottom }

    /// <summary>
    /// The triangles a surface is made of, from solids and meshes (plain data): which face up or down, the polygons split into
    /// triangles, and the outline of the result, so the surface keeps the objects' own triangles and does not bridge gaps.
    /// </summary>
    internal static class MeshTriangles
    {
        /// <summary>A face steeper than this (|normal Z| below it, about 89.4°) is a side, on neither top nor bottom.</summary>
        public const double Vertical = 0.01;

        /// <summary>
        /// The faces of one side. A closed object (a solid, a closed mesh) gives its up-facing faces as top and down-facing as
        /// bottom; an open sheet (a daylight mesh, one layer of faces) is both its top and its bottom, whichever way it faces.
        /// </summary>
        public static List<Tri> Side(IEnumerable<Tri> faces, bool closed, SurfaceSide side)
        {
            List<Tri> flat = faces.Where(t => Math.Abs(t.NormalZ) >= Vertical && t.PlanArea > 1e-9).ToList();
            if (!closed) return flat;
            return flat.Where(t => side == SurfaceSide.Top ? t.NormalZ > 0 : t.NormalZ < 0).ToList();
        }

        /// <summary>True when every edge is shared by exactly two faces (a watertight mesh).</summary>
        public static bool IsClosed(IEnumerable<Tri> faces, double tol = 1e-4)
        {
            var count = new Dictionary<(long, long, long, long, long, long), int>();
            bool any = false;
            foreach (Tri t in faces)
                foreach (var e in Edges(t, tol)) { any = true; count[e] = count.TryGetValue(e, out int n) ? n + 1 : 1; }
            return any && count.Values.All(n => n == 2);
        }

        private static (long, long, long) Key(P3 p, double tol) => ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));

        private static IEnumerable<(long, long, long, long, long, long)> Edges(Tri t, double tol)
        {
            var a = Key(t.A, tol); var b = Key(t.B, tol); var c = Key(t.C, tol);
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                yield return p.CompareTo(q) <= 0 ? (p.Item1, p.Item2, p.Item3, q.Item1, q.Item2, q.Item3) : (q.Item1, q.Item2, q.Item3, p.Item1, p.Item2, p.Item3);
        }

        /// <summary>
        /// A planar polygon (corners in order) split into triangles in its own order, by ear clipping in plan (the polygon is not
        /// vertical, or it would be a side). Corners that follow the outward direction keep it.
        /// </summary>
        public static List<Tri> Triangulate(IReadOnlyList<P3> poly)
        {
            var result = new List<Tri>();
            if (poly.Count < 3) return result;
            if (poly.Count == 3) { result.Add(new Tri(poly[0], poly[1], poly[2])); return result; }
            // Orientation in plan: ears must turn the same way as the polygon.
            double area = 0;
            for (int i = 0; i < poly.Count; i++) { P3 p = poly[i], q = poly[(i + 1) % poly.Count]; area += p.X * q.Y - q.X * p.Y; }
            double sign = Math.Sign(area);
            if (sign == 0) return result;
            var idx = Enumerable.Range(0, poly.Count).ToList();
            int guard = poly.Count * poly.Count;
            while (idx.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    P3 a = poly[ia], b = poly[ib], c = poly[ic];
                    double cross = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                    if (cross * sign <= 1e-12) continue;
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (InTriangle(poly[j], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    result.Add(new Tri(a, b, c));
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) result.Add(new Tri(poly[idx[0]], poly[idx[1]], poly[idx[2]]));
            return result;
        }

        private static bool InTriangle(P3 p, P3 a, P3 b, P3 c)
        {
            double d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            double d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            double d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// <summary>
        /// The outline of a set of triangles: every closed chain of edges used by one triangle only, in plan (Z of the corners
        /// kept). Points within <paramref name="tol"/> are the same point, so neighbouring objects that share edges join.
        /// </summary>
        public static List<List<P3>> Outline(IEnumerable<Tri> tris, double tol = 1e-3)
        {
            var use = new Dictionary<((long, long), (long, long)), int>();
            var point = new Dictionary<(long, long), P3>();
            (long, long) K(P3 p) { var k = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol)); if (!point.ContainsKey(k)) point[k] = p; return k; }
            foreach (Tri t in tris)
            {
                var a = K(t.A); var b = K(t.B); var c = K(t.C);
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    if (p.Equals(q)) continue;
                    var e = p.CompareTo(q) <= 0 ? (p, q) : (q, p);
                    use[e] = use.TryGetValue(e, out int n) ? n + 1 : 1;
                }
            }
            var next = new Dictionary<(long, long), List<(long, long)>>();
            foreach (var e in use.Where(x => x.Value == 1).Select(x => x.Key))
            {
                if (!next.TryGetValue(e.Item1, out var l1)) next[e.Item1] = l1 = new List<(long, long)>();
                if (!next.TryGetValue(e.Item2, out var l2)) next[e.Item2] = l2 = new List<(long, long)>();
                l1.Add(e.Item2); l2.Add(e.Item1);
            }
            var loops = new List<List<P3>>();
            var usedEdge = new HashSet<((long, long), (long, long))>();
            foreach (var start in next.Keys.ToList())
            {
                foreach (var first in next[start])
                {
                    var e0 = start.CompareTo(first) <= 0 ? (start, first) : (first, start);
                    if (usedEdge.Contains(e0)) continue;
                    var loop = new List<P3> { point[start] };
                    var cur = first; usedEdge.Add(e0);
                    int guard = use.Count + 2;
                    bool closed = false;
                    while (guard-- > 0)
                    {
                        if (cur.Equals(start)) { closed = true; break; }
                        loop.Add(point[cur]);
                        bool found = false;
                        foreach (var n in next[cur])
                        {
                            var e = cur.CompareTo(n) <= 0 ? (cur, n) : (n, cur);
                            if (usedEdge.Contains(e)) continue;
                            usedEdge.Add(e); cur = n; found = true; break;
                        }
                        if (!found) break;
                    }
                    if (closed && loop.Count >= 3) loops.Add(loop);
                }
            }
            return loops;
        }

        public static double PlanArea(IReadOnlyList<P3> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++) { P3 p = loop[i], q = loop[(i + 1) % loop.Count]; a += p.X * q.Y - q.X * p.Y; }
            return Math.Abs(a) / 2;
        }

        public static bool Inside(P3 p, IReadOnlyList<P3> loop)
        {
            bool c = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y) && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X) c = !c;
            return c;
        }

        /// <summary>Outer loops (not inside another) and holes (inside one), for the surface's outer and hide boundaries.</summary>
        public static (List<List<P3>> outer, List<List<P3>> holes) Nest(List<List<P3>> loops)
        {
            var outer = new List<List<P3>>();
            var holes = new List<List<P3>>();
            foreach (List<P3> l in loops.OrderByDescending(PlanArea))
            {
                int depth = loops.Count(o => !ReferenceEquals(o, l) && PlanArea(o) > PlanArea(l) && Inside(l[0], o));
                (depth % 2 == 0 ? outer : holes).Add(l);
            }
            return (outer, holes);
        }
    }
}
