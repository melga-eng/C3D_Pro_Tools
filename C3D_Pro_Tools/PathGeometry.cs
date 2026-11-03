using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil;
using System;
using System.Collections.Generic;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    /// <summary>One straight or circular piece of a path in plan.</summary>
    internal sealed class PathSegment
    {
        public Point2d P0, P1;
        public ArcInfo? Arc;
        public double Length;
    }

    /// <summary>
    /// The plan geometry of an object new pipes are drawn along: lines, arcs, polylines (2D, 3D, old-style), splines,
    /// alignments and feature lines. Arcs stay arcs; anything else curved is followed with short chords.
    /// </summary>
    internal sealed class PathGeometry
    {
        // Splines and spirals are followed with chords this long (m); over 50 m radius they stay within 1 cm of the curve.
        private const double ChordLength = 2.0;
        // Pieces shorter than this are joined to their neighbour.
        private const double MinSegment = 0.001;

        public ObjectId Id;
        public string Name;
        public readonly List<PathSegment> Segments = new List<PathSegment>();
        public double Length
        {
            get { double l = 0; foreach (PathSegment s in Segments) l += s.Length; return l; }
        }

        public static string Describe(DBObject obj) =>
            obj is Alignment a ? $"Alignment {a.Name}" : obj is FeatureLine f ? $"Feature line {(string.IsNullOrEmpty(f.Name) ? f.Handle.ToString() : f.Name)}"
            : obj is Polyline3d ? "3D polyline" : obj is Polyline || obj is Polyline2d ? "Polyline" : obj is Line ? "Line" : obj is Arc ? "Arc" : obj is Spline ? "Spline" : obj.GetType().Name;

        /// <summary>Reads a path object, or returns null (with the reason) when it has no usable plan geometry.</summary>
        public static PathGeometry Read(Transaction tr, ObjectId id, out string error)
        {
            error = null;
            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
            PathGeometry path = new PathGeometry { Id = id, Name = Describe(obj) };
            try
            {
                Curve curve = obj as Curve;
                if (curve == null && obj is Autodesk.Civil.DatabaseServices.Entity civil)
                {
                    try { curve = civil.BaseCurve; } catch (Exception) { curve = null; }
                }
                if (curve != null) path.AddCurve(curve);
                else if (obj is FeatureLine fl) path.AddPoints(fl.GetPoints(FeatureLinePointType.AllPoints));
            }
            catch (Exception ex)
            {
                error = $"{path.Name}: {ex.Message}";
                return null;
            }
            if (path.Segments.Count == 0)
            {
                error = $"{path.Name}: no plan geometry could be read.";
                return null;
            }
            return path;
        }

        private void AddCurve(Curve curve)
        {
            Curve3d ge = curve.GetGeCurve();
            if (ge is CompositeCurve3d composite)
                foreach (Curve3d piece in composite.GetCurves()) AddGe(piece);
            else AddGe(ge);
        }

        private void AddGe(Curve3d ge)
        {
            if (ge is LineSegment3d line)
            {
                AddLine(line.StartPoint, line.EndPoint);
            }
            else if (ge is CircularArc3d arc && Math.Abs(arc.Normal.Z) > 0.999)
            {
                Interval iv = arc.GetInterval();
                Point3d mid = arc.EvaluatePoint((iv.LowerBound + iv.UpperBound) / 2.0);
                AddArc(arc.StartPoint, arc.EndPoint, new ArcInfo { Center = new Point2d(arc.Center.X, arc.Center.Y), Radius = arc.Radius, Mid = new Point2d(mid.X, mid.Y) });
            }
            else
            {
                // Splines, spirals, tilted arcs: chords whose sagitta stays within the tolerance.
                Interval iv = ge.GetInterval();
                double length = ge.GetLength(iv.LowerBound, iv.UpperBound, 1e-6);
                int n = Math.Max(1, Math.Min(2000, (int)Math.Ceiling(length / ChordLength)));
                Point3d previous = ge.EvaluatePoint(iv.LowerBound);
                for (int i = 1; i <= n; i++)
                {
                    Point3d p = ge.EvaluatePoint(iv.LowerBound + (iv.UpperBound - iv.LowerBound) * i / n);
                    AddLine(previous, p);
                    previous = p;
                }
            }
        }

        private void AddPoints(Point3dCollection points)
        {
            for (int i = 1; i < points.Count; i++) AddLine(points[i - 1], points[i]);
        }

        private void AddLine(Point3d a, Point3d b)
        {
            Point2d p0 = new Point2d(a.X, a.Y), p1 = new Point2d(b.X, b.Y);
            double length = p0.GetDistanceTo(p1);
            if (length < MinSegment) return;
            Segments.Add(new PathSegment { P0 = p0, P1 = p1, Length = length });
        }

        private void AddArc(Point3d a, Point3d b, ArcInfo arc)
        {
            Point2d p0 = new Point2d(a.X, a.Y), p1 = new Point2d(b.X, b.Y);
            if (p0.GetDistanceTo(p1) < MinSegment) return;
            double sweep = Math.Abs((p0 - arc.Center).GetAngleTo(arc.Mid - arc.Center)) + Math.Abs((arc.Mid - arc.Center).GetAngleTo(p1 - arc.Center));
            Segments.Add(new PathSegment { P0 = p0, P1 = p1, Arc = arc, Length = arc.Radius * sweep });
        }

        /// <summary>The same path travelled the other way.</summary>
        public PathGeometry Reversed()
        {
            PathGeometry r = new PathGeometry { Id = Id, Name = Name };
            for (int i = Segments.Count - 1; i >= 0; i--)
            {
                PathSegment s = Segments[i];
                r.Segments.Add(new PathSegment { P0 = s.P1, P1 = s.P0, Arc = s.Arc, Length = s.Length });
            }
            return r;
        }

        public List<Point3d> Sample(double spacing)
        {
            List<Point3d> points = new List<Point3d>();
            foreach (PathSegment s in Segments)
            {
                List<Point3d> piece = PipeArcGeometry.SamplePath(new Point3d(s.P0.X, s.P0.Y, 0), new Point3d(s.P1.X, s.P1.Y, 0), s.Arc, spacing, 400);
                if (points.Count > 0) piece.RemoveAt(0);
                points.AddRange(piece);
            }
            return points;
        }
    }
}
