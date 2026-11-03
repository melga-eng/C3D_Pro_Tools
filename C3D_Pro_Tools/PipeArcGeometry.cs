using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    /// <summary>Plan geometry of a curved pipe, copied out of the Civil 3D wrappers so nothing outlives its owner.</summary>
    internal struct ArcInfo
    {
        public Point2d Center;
        public double Radius;
        public Point2d Mid;
    }

    /// <summary>Reads the plan arc of curved gravity and pressure pipes, and samples points along pipe paths.</summary>
    internal static class PipeArcGeometry
    {
        public static bool TryGetGravityArc(Pipe pipe, out ArcInfo arc)
        {
            try
            {
                if (TryReadArc(pipe.Curve2d, out arc)) return true;
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

            try
            {
                using (PipeCurveGeometry geom = pipe.CurveGeometry)
                {
                    if (geom != null && TryReadArc(geom.GetArc2d(), out arc)) return true;
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

            arc = default;
            return false;
        }

        public static bool TryGetPressureArc(PressurePipe pipe, out ArcInfo arc)
        {
            try
            {
                using (PipeCurveGeometry geom = pipe.CurveGeometry)
                {
                    if (geom != null && TryReadArc(geom.GetArc2d(), out arc)) return true;
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

            arc = default;
            return false;
        }

        private static bool TryReadArc(CircularArc2d source, out ArcInfo arc)
        {
            arc = default;
            if (source == null || source.Radius <= 0) return false;

            Interval range = source.GetInterval();
            arc.Center = source.Center;
            arc.Radius = source.Radius;
            arc.Mid = source.EvaluatePoint((range.LowerBound + range.UpperBound) / 2.0);
            return true;
        }

        /// <summary>True when the arc turns clockwise travelling start -> mid -> end.</summary>
        public static bool IsClockwise(Point2d start, Point2d mid, Point2d end)
        {
            Vector2d toMid = mid - start;
            Vector2d midToEnd = end - mid;
            return (toMid.X * midToEnd.Y - toMid.Y * midToEnd.X) < 0;
        }

        /// <summary>
        /// Points from start to end, at most <paramref name="spacing"/> apart (and at least three), following the arc
        /// when one is given. Elevations are interpolated linearly between the two ends.
        /// </summary>
        public static List<Point3d> SamplePath(Point3d start, Point3d end, ArcInfo? arc, double spacing, int maxSamples)
        {
            Point2d s = new Point2d(start.X, start.Y);
            Point2d e = new Point2d(end.X, end.Y);

            double sweep = 0.0, startAngle = 0.0, length;
            if (arc.HasValue)
            {
                ArcInfo a = arc.Value;
                startAngle = Math.Atan2(s.Y - a.Center.Y, s.X - a.Center.X);
                double endAngle = Math.Atan2(e.Y - a.Center.Y, e.X - a.Center.X);
                bool clockwise = IsClockwise(s, a.Mid, e);
                sweep = clockwise ? -NormalizeAngle(startAngle - endAngle) : NormalizeAngle(endAngle - startAngle);
                length = Math.Abs(sweep) * a.Radius;
            }
            else
            {
                length = s.GetDistanceTo(e);
            }

            int segments = Math.Min(maxSamples - 1, Math.Max(2, (int)Math.Ceiling(length / spacing)));
            List<Point3d> points = new List<Point3d>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double t = (double)i / segments;
                double z = start.Z + (end.Z - start.Z) * t;
                if (arc.HasValue)
                {
                    double angle = startAngle + sweep * t;
                    points.Add(new Point3d(arc.Value.Center.X + arc.Value.Radius * Math.Cos(angle),
                                           arc.Value.Center.Y + arc.Value.Radius * Math.Sin(angle), z));
                }
                else
                {
                    points.Add(new Point3d(s.X + (e.X - s.X) * t, s.Y + (e.Y - s.Y) * t, z));
                }
            }
            return points;
        }

        /// <summary>Maps an angle into (0, 2π].</summary>
        private static double NormalizeAngle(double angle)
        {
            double twoPi = 2.0 * Math.PI;
            angle %= twoPi;
            if (angle <= 0) angle += twoPi;
            return angle;
        }
    }
}
