using System;
using System.Collections.Generic;
using System.Linq;

// Plan geometry shared by the network tools: polyline arcs as chords, mitred offsets of a run, how a pipe meets a
// rectangular chamber, duct bank arrays, structure-to-structure spans and a line's overall grade. No AutoCAD / Civil 3D
// types, so it is tested without Civil 3D.
namespace C3DProTools
{
    internal static class PlanGeometry
    {
        // =====================================================================================
        // Arcs (polyline bulges)
        // =====================================================================================

        /// <summary>
        /// The points along a polyline arc segment from (x0, y0) to (x1, y1), start left out and end included, close enough
        /// that no chord is more than <paramref name="maxMidOrdinate"/> off the arc (at most <paramref name="maxSegments"/>).
        /// A zero bulge gives the end point alone.
        /// </summary>
        public static List<(double X, double Y)> ArcPoints(double x0, double y0, double x1, double y1, double bulge, double maxMidOrdinate = 0.05, int maxSegments = 256)
        {
            List<(double X, double Y)> pts = new List<(double X, double Y)>();
            double dx = x1 - x0, dy = y1 - y0, c = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(bulge) < 1e-9 || c < 1e-9) { pts.Add((x1, y1)); return pts; }
            // Bulge = tan(sweep / 4); positive turns counter-clockwise, with the centre to the left of the chord.
            double sweep = 4 * Math.Atan(bulge);
            double radius = c * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
            double toCentre = c * (1 - bulge * bulge) / (4 * bulge);
            double cx = (x0 + x1) / 2 - dy / c * toCentre, cy = (y0 + y1) / 2 + dx / c * toCentre;
            int n = ArcSegmentCount(radius, Math.Abs(sweep), maxMidOrdinate, maxSegments);
            double a0 = Math.Atan2(y0 - cy, x0 - cx);
            for (int i = 1; i < n; i++)
            {
                double a = a0 + sweep * i / n;
                pts.Add((cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
            pts.Add((x1, y1));
            return pts;
        }

        /// <summary>Chords needed for an arc so none is more than <paramref name="maxMidOrdinate"/> off it.</summary>
        public static int ArcSegmentCount(double radius, double sweepAbs, double maxMidOrdinate, int maxSegments = 256)
        {
            if (radius <= 0 || sweepAbs <= 0) return 1;
            double tol = Math.Max(1e-6, maxMidOrdinate);
            // Mid-ordinate of a chord spanning angle φ: R (1 − cos φ/2).
            double maxStep = tol >= radius ? Math.PI : 2 * Math.Acos(1 - tol / radius);
            return Math.Max(2, Math.Min(Math.Max(2, maxSegments), (int)Math.Ceiling(sweepAbs / maxStep - 1e-9)));
        }

        /// <summary>A polyline's outline with its arc segments as chords (see <see cref="ArcPoints"/>); a closed one is not repeated at its end.</summary>
        public static List<(double X, double Y)> Densify(IReadOnlyList<(double X, double Y, double Bulge)> vertices, bool closed, double maxMidOrdinate = 0.05)
        {
            List<(double X, double Y)> pts = new List<(double X, double Y)>();
            int n = vertices.Count;
            if (n == 0) return pts;
            pts.Add((vertices[0].X, vertices[0].Y));
            int count = closed ? n : n - 1;
            for (int i = 0; i < count; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % n];
                pts.AddRange(ArcPoints(a.X, a.Y, b.X, b.Y, a.Bulge, maxMidOrdinate));
            }
            if (closed && pts.Count > 1) pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        // =====================================================================================
        // Offsets of a run
        // =====================================================================================

        /// <summary>
        /// A run offset in plan (+ = right of travel) as one continuous line: each inner vertex is where the two offset legs
        /// meet (a mitre), so copies of consecutive pipes meet end to end with no gap or overlap. At a turn so sharp that the
        /// mitre would reach more than <paramref name="maxMitre"/> × the offset from the vertex, the point is held at that
        /// distance along the bisector. Points that repeat the one before are kept (one output point per input point).
        /// </summary>
        public static List<(double X, double Y)> OffsetRun(IReadOnlyList<(double X, double Y)> run, double offset, double maxMitre = 4)
        {
            int n = run.Count;
            List<(double X, double Y)> result = new List<(double X, double Y)>(n);
            if (n == 0) return result;
            (double X, double Y)? Dir(int i, int j)
            {
                double dx = run[j].X - run[i].X, dy = run[j].Y - run[i].Y, l = Math.Sqrt(dx * dx + dy * dy);
                return l < 1e-9 ? ((double X, double Y)?)null : (dx / l, dy / l);
            }
            // The leg directions, carried over zero-length legs.
            (double X, double Y)[] legs = new (double X, double Y)[Math.Max(0, n - 1)];
            (double X, double Y)? last = null;
            for (int i = 0; i < n - 1; i++) { last = Dir(i, i + 1) ?? last; legs[i] = last ?? (1, 0); }
            for (int i = n - 2; i >= 0; i--) if (Dir(i, i + 1) == null && i + 1 < n - 1) legs[i] = legs[i + 1];
            if (n == 1) { result.Add(run[0]); return result; }

            for (int i = 0; i < n; i++)
            {
                (double X, double Y) din = i == 0 ? legs[0] : legs[i - 1], dout = i == n - 1 ? legs[n - 2] : legs[i];
                // Right normals of the two legs; the mitre runs along their sum.
                (double X, double Y) rIn = (din.Y, -din.X), rOut = (dout.Y, -dout.X);
                double bx = rIn.X + rOut.X, by = rIn.Y + rOut.Y, bl = Math.Sqrt(bx * bx + by * by);
                if (bl < 1e-9)
                {
                    // A full reversal: no mitre, the vertex goes out along the incoming normal.
                    result.Add((run[i].X + rIn.X * offset, run[i].Y + rIn.Y * offset));
                    continue;
                }
                bx /= bl; by /= bl;
                double cosHalf = bx * rIn.X + by * rIn.Y;
                double reach = Math.Min(1.0 / Math.Max(1e-9, cosHalf), Math.Max(1, maxMitre));
                result.Add((run[i].X + bx * offset * reach, run[i].Y + by * offset * reach));
            }
            return result;
        }

        /// <summary>
        /// Duct bank array offsets, centred on the source: <paramref name="columns"/> across at <paramref name="pitchH"/>
        /// and <paramref name="rows"/> up at <paramref name="pitchV"/> (centre to centre).
        /// </summary>
        public static (List<double> horizontal, List<double> vertical) ArrayOffsets(int columns, int rows, double pitchH, double pitchV)
        {
            List<double> Centred(int count, double pitch) =>
                Enumerable.Range(0, Math.Max(1, count)).Select(i => Math.Round((i - (Math.Max(1, count) - 1) / 2.0) * pitch, 6)).ToList();
            return (Centred(columns, pitchH), Centred(rows, pitchV));
        }

        /// <summary>Centre-to-centre pitch from a spacing that is either the pitch itself or the clear gap between outsides.</summary>
        public static double Pitch(double spacing, double outerSize, bool clearGap) => clearGap ? outerSize + spacing : spacing;

        // =====================================================================================
        // Rectangular chambers
        // =====================================================================================

        internal enum ChamberWall { None, Narrow, Long }

        /// <summary>
        /// Which wall of a rectangular chamber a pipe passes through, and how far it is off square to it (degrees). The chamber's
        /// width lies along its own X axis (turned by <paramref name="rotation"/>) and its length along Y, as the part's sizes lie;
        /// the narrow walls are the two shorter faces, at the ends of the longer axis. The pipe runs from <paramref name="far"/>
        /// (its other end) to <paramref name="near"/> (its end at the chamber).
        /// </summary>
        public static (ChamberWall wall, double skewDeg) ChamberEntry(double cx, double cy, double rotation, double halfX, double halfY,
            (double X, double Y) far, (double X, double Y) near)
        {
            double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
            (double X, double Y) Local(double x, double y) => ((x - cx) * cos + (y - cy) * sin, -(x - cx) * sin + (y - cy) * cos);
            (double X, double Y) a = Local(far.X, far.Y), b = Local(near.X, near.Y);
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9 || halfX <= 0 || halfY <= 0) return (ChamberWall.None, 0);
            dx /= len; dy /= len;

            // Where the line from the far end first meets the box (or, when it ends short, the face its line points at).
            double tx = Math.Abs(dx) < 1e-12 ? double.PositiveInfinity : ((dx > 0 ? -halfX : halfX) - a.X) / dx;
            double ty = Math.Abs(dy) < 1e-12 ? double.PositiveInfinity : ((dy > 0 ? -halfY : halfY) - a.Y) / dy;
            bool xFace;
            if (double.IsInfinity(tx)) xFace = false;
            else if (double.IsInfinity(ty)) xFace = true;
            else
            {
                // The later of the two entry parameters is where the line actually enters the box (slab method).
                xFace = tx > ty;
            }
            // X faces (normal ±X) are the faces of length 2·halfY; Y faces are 2·halfX long.
            bool narrow = xFace ? halfY < halfX : halfX < halfY;
            if (Math.Abs(halfX - halfY) < 1e-6) narrow = true;
            double cosToNormal = xFace ? Math.Abs(dx) : Math.Abs(dy);
            double skew = Math.Acos(Math.Max(-1, Math.Min(1, cosToNormal))) * 180 / Math.PI;
            return (narrow ? ChamberWall.Narrow : ChamberWall.Long, skew);
        }

        /// <summary>
        /// The rotation that puts a rectangular chamber's narrow walls square to its pipes: one pipe, or two running straight
        /// through (bearings opposite within <paramref name="straightTolDeg"/>). The bearings point from the chamber out along
        /// each pipe. Of the two rotations that do it, the one nearer the current rotation; null when the pipes do not run
        /// straight through or the chamber is square.
        /// </summary>
        public static double? AlignedRotation(double rotation, double halfX, double halfY, IReadOnlyList<double> bearings, double straightTolDeg = 10)
        {
            if (bearings.Count == 0 || bearings.Count > 2 || Math.Abs(halfX - halfY) < 1e-6) return null;
            double axis = bearings[0];
            if (bearings.Count == 2)
            {
                double between = Math.Abs(Math.IEEERemainder(bearings[1] - bearings[0], 2 * Math.PI));
                if (Math.PI - between > straightTolDeg * Math.PI / 180) return null;
                // The mean direction of the pipe axis (the second bearing turned round).
                double b1 = bearings[1] + Math.PI;
                axis = Math.Atan2(Math.Sin(axis) + Math.Sin(b1), Math.Cos(axis) + Math.Cos(b1));
            }
            // The longer axis must lie along the pipe: Y (rotation + 90°) when the length is the longer size, else X.
            double target = halfY > halfX ? axis - Math.PI / 2 : axis;
            // Either way round will do (a rectangle turned 180° is the same): the smaller change.
            double change = Math.IEEERemainder(target - rotation, Math.PI);
            return rotation + change;
        }

        // =====================================================================================
        // Spans and grades
        // =====================================================================================

        /// <summary>
        /// Structure-to-structure spans: pipes joined end to end where no structure is (a split pipe left without one) count as
        /// one span. Each pipe has its two end keys (structure or point), whether each end is at a structure, and its length.
        /// Returns each span's pipes and total length.
        /// </summary>
        public static List<(List<int> pipes, double length)> Spans(IReadOnlyList<(string startKey, bool startAtStructure, string endKey, bool endAtStructure, double length)> pipes)
        {
            // Ends meeting at a point with no structure join their pipes into one span.
            Dictionary<string, List<int>> atFreeJoint = new Dictionary<string, List<int>>();
            for (int i = 0; i < pipes.Count; i++)
            {
                if (!pipes[i].startAtStructure) Add(pipes[i].startKey, i);
                if (!pipes[i].endAtStructure) Add(pipes[i].endKey, i);
            }
            void Add(string key, int i)
            {
                if (!atFreeJoint.TryGetValue(key, out List<int> l)) atFreeJoint[key] = l = new List<int>();
                l.Add(i);
            }
            int[] group = Enumerable.Range(0, pipes.Count).ToArray();
            int Find(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            foreach (List<int> l in atFreeJoint.Values)
                for (int k = 1; k < l.Count; k++) group[Find(l[k])] = Find(l[0]);
            return Enumerable.Range(0, pipes.Count).GroupBy(Find)
                .Select(g => (g.ToList(), g.Sum(i => pipes[i].length))).ToList();
        }

        /// <summary>
        /// The overall grade of a line (rise per unit length): the least-squares slope of its elevation over its whole length,
        /// each stretch weighted by its length, so a line that rises and falls on the way is judged by where most of it goes,
        /// not by its two end points alone. Points are (station, elevation) in order.
        /// </summary>
        public static double TrendSlope(IReadOnlyList<(double S, double Z)> pts)
        {
            if (pts.Count < 2) return 0;
            double s0 = pts[0].S, length = pts[pts.Count - 1].S - s0;
            if (length < 1e-9) return 0;
            double intZ = 0, intSZ = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double a = pts[i - 1].S - s0, b = pts[i].S - s0, za = pts[i - 1].Z, zb = pts[i].Z, h = b - a;
                if (h <= 0) continue;
                intZ += h * (za + zb) / 2;
                intSZ += h / 6 * (2 * a * za + a * zb + b * za + 2 * b * zb);
            }
            // Over s in [0, L]: mean s = L/2, variance L²/12.
            return (intSZ / length - length / 2 * (intZ / length)) / (length * length / 12);
        }

        /// <summary>
        /// The largest distance (m) between a profile's points and the straight grade from its first point to its last, e.g. to
        /// see whether elevation points along an arc would be lost by one constant-grade pipe. Points are (station, elevation).
        /// </summary>
        public static double GradeDeviation(IReadOnlyList<(double S, double Z)> pts)
        {
            if (pts.Count < 3) return 0;
            var a = pts[0];
            var b = pts[pts.Count - 1];
            double span = b.S - a.S;
            if (Math.Abs(span) < 1e-9) return pts.Max(p => Math.Abs(p.Z - a.Z));
            return pts.Max(p => Math.Abs(p.Z - (a.Z + (b.Z - a.Z) * (p.S - a.S) / span)));
        }

        /// <summary>A distance on paper (mm) in drawing units through a viewport of this scale (paper units per drawing unit).</summary>
        public static double PaperToModel(double paperMm, double viewportScale) => viewportScale > 0 ? paperMm / viewportScale : paperMm;
    }
}
