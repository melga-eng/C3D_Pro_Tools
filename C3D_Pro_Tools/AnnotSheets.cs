using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>
    /// One viewport of a sheet: which part of the model it shows, at what scale and rotation, and its outline on paper.
    /// Everything the annotation tools arrange is worked out in paper millimetres, so rotated sheets read correctly.
    /// </summary>
    internal sealed class SheetViewport
    {
        public ObjectId Id;
        public string Layout = "";
        public int TabOrder;
        /// <summary>Model to paper (and back): view target, twist, view centre and custom scale.</summary>
        public Matrix3d ModelToPaper, PaperToModel;
        /// <summary>Paper units per model unit.</summary>
        public double Scale;
        public double Twist;
        /// <summary>The viewport outline on paper (its clip boundary when it has one).</summary>
        public List<Point2d> Outline = new List<Point2d>();
        public Extents2d PaperBox;
        /// <summary>The model area seen, as an axis-aligned box around the outline (for quick filtering).</summary>
        public Extents2d ModelBox;

        public string ScaleText => Scale > 0 ? "1:" + Math.Round(1000.0 / Scale) : "";
        public string Label => $"{Layout}  ({ScaleText}{(Math.Abs(Twist) > 1e-6 ? $", rotated {Twist * 180 / Math.PI:0.#}°" : "")})";

        public Point2d ToPaper(Point3d model)
        {
            Point3d p = model.TransformBy(ModelToPaper);
            return new Point2d(p.X, p.Y);
        }

        /// <summary>A paper vector (mm) as the model vector that moves something by it on this sheet.</summary>
        public Vector3d ToModel(Vector2d paper) => new Vector3d(paper.X, paper.Y, 0).TransformBy(PaperToModel);

        public bool Sees(Point3d model)
        {
            if (model.X < ModelBox.MinPoint.X || model.X > ModelBox.MaxPoint.X || model.Y < ModelBox.MinPoint.Y || model.Y > ModelBox.MaxPoint.Y) return false;
            return Inside(ToPaper(model), 0);
        }

        /// <summary>True when the paper point lies inside the outline, at least <paramref name="margin"/> mm from it.</summary>
        public bool Inside(Point2d p, double margin)
        {
            if (!Geometry2d.InPolygon(Outline, p)) return false;
            return margin <= 0 || Geometry2d.DistanceToPolygon(Outline, p) >= margin;
        }

        public bool Inside(Box2 box, double margin) =>
            Inside(new Point2d(box.MinX, box.MinY), margin) && Inside(new Point2d(box.MaxX, box.MinY), margin) &&
            Inside(new Point2d(box.MaxX, box.MaxY), margin) && Inside(new Point2d(box.MinX, box.MaxY), margin);
    }

    /// <summary>A layout and the model viewports on it.</summary>
    internal sealed class SheetInfo
    {
        public string Layout = "";
        public int TabOrder;
        public ObjectId LayoutId, BlockId;
        public readonly List<SheetViewport> Viewports = new List<SheetViewport>();
        /// <summary>Text on the sheet (title block, notes), for finding its sheet number.</summary>
        public readonly List<string> Texts = new List<string>();

        public override string ToString() => Layout;

        /// <summary>The sheet number found in the sheet's own text by a pattern such as "SHEET[- ]*(\d+)", or null.</summary>
        public string SheetNumber(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try
            {
                Regex rx = new Regex(pattern, RegexOptions.IgnoreCase);
                foreach (string t in Texts)
                {
                    Match m = rx.Match(t);
                    if (m.Success) return m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                }
            }
            catch (ArgumentException) { }
            return null;
        }
    }

    /// <summary>Axis-aligned box on paper (mm).</summary>
    internal struct Box2
    {
        public double MinX, MinY, MaxX, MaxY;
        public Box2(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public Point2d Center => new Point2d((MinX + MaxX) / 2, (MinY + MaxY) / 2);
        public bool IsEmpty => MaxX < MinX || MaxY < MinY;
        public static Box2 Empty => new Box2(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        public Box2 Add(Point2d p) => new Box2(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));
        public Box2 Offset(Vector2d v) => new Box2(MinX + v.X, MinY + v.Y, MaxX + v.X, MaxY + v.Y);
        public Box2 Grow(double d) => new Box2(MinX - d, MinY - d, MaxX + d, MaxY + d);
        public bool Overlaps(Box2 o) => MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY;
        public double OverlapArea(Box2 o) => Math.Max(0, Math.Min(MaxX, o.MaxX) - Math.Max(MinX, o.MinX)) * Math.Max(0, Math.Min(MaxY, o.MaxY) - Math.Max(MinY, o.MinY));
        public bool Contains(Point2d p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
        public bool Same(Box2 o) => MinX == o.MinX && MinY == o.MinY && MaxX == o.MaxX && MaxY == o.MaxY;

        /// <summary>The point of the box nearest to <paramref name="p"/> (where a leader would end).</summary>
        public Point2d Nearest(Point2d p) => new Point2d(Math.Max(MinX, Math.Min(MaxX, p.X)), Math.Max(MinY, Math.Min(MaxY, p.Y)));

        /// <summary>True when the segment crosses the box (or lies in it).</summary>
        public bool Hits(Point2d a, Point2d b)
        {
            if (Contains(a) || Contains(b)) return true;
            Point2d[] c = { new Point2d(MinX, MinY), new Point2d(MaxX, MinY), new Point2d(MaxX, MaxY), new Point2d(MinX, MaxY) };
            for (int i = 0; i < 4; i++) if (Geometry2d.SegmentsCross(a, b, c[i], c[(i + 1) % 4])) return true;
            return false;
        }
    }

    internal static class Geometry2d
    {
        public static bool InPolygon(List<Point2d> poly, Point2d p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X)
                    inside = !inside;
            }
            return inside;
        }

        public static double DistanceToPolygon(List<Point2d> poly, Point2d p)
        {
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) best = Math.Min(best, DistanceToSegment(p, poly[j], poly[i]));
            return best;
        }

        public static double DistanceToSegment(Point2d p, Point2d a, Point2d b)
        {
            Vector2d ab = b - a;
            double len2 = ab.DotProduct(ab);
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).DotProduct(ab) / len2));
            return p.GetDistanceTo(a + ab * t);
        }

        public static bool SegmentsCross(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(Point2d a, Point2d b, Point2d p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
    }

    /// <summary>Reads the drawing's sheets: every paper layout with the model viewports on it.</summary>
    internal static class AnnotSheets
    {
        public static List<SheetInfo> Read(Transaction tr, Database db)
        {
            List<SheetInfo> sheets = new List<SheetInfo>();
            DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry de in layouts)
            {
                if (!(tr.GetObject(de.Value, OpenMode.ForRead) is Layout lay) || lay.ModelType) continue;
                SheetInfo sheet = new SheetInfo { Layout = lay.LayoutName, TabOrder = lay.TabOrder, LayoutId = de.Value, BlockId = lay.BlockTableRecordId };
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    DBObject obj;
                    try { obj = tr.GetObject(id, OpenMode.ForRead); } catch (Exception) { continue; }
                    if (obj is Viewport vp)
                    {
                        // The layout's own paper viewport (number 1, or 1:1 looking at the origin) is the sheet itself, not a view into the model.
                        if (vp.Number == 1 || (Math.Abs(vp.CustomScale - 1) < 1e-9 && vp.ViewTarget.IsEqualTo(Point3d.Origin))) continue;
                        if (vp.CustomScale <= 0 || !vp.ViewDirection.IsParallelTo(Vector3d.ZAxis)) continue;
                        SheetViewport v = Build(tr, vp, sheet);
                        if (v != null) sheet.Viewports.Add(v);
                    }
                    else if (obj is DBText dt) sheet.Texts.Add(dt.TextString);
                    else if (obj is MText mt) sheet.Texts.Add(mt.Text);
                    else if (obj is BlockReference br)
                    {
                        foreach (ObjectId attId in br.AttributeCollection)
                            if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference ar) sheet.Texts.Add(ar.TextString);
                    }
                }
                sheets.Add(sheet);
            }
            return sheets.OrderBy(s => s.TabOrder).ToList();
        }

        private static SheetViewport Build(Transaction tr, Viewport vp, SheetInfo sheet)
        {
            // Model (WCS) → display (DCS): the view looks at the target, turned by the twist; DCS → paper: centred and scaled.
            Matrix3d dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget)
                                * Matrix3d.Displacement(vp.ViewTarget.GetAsVector())
                                * Matrix3d.PlaneToWorld(vp.ViewDirection);
            Matrix3d dcsToPaper = Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint)
                                  * Matrix3d.Displacement(vp.CenterPoint - new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0));
            Matrix3d modelToPaper = dcsToPaper * dcsToWcs.Inverse();
            SheetViewport v = new SheetViewport
            {
                Id = vp.ObjectId, Layout = sheet.Layout, TabOrder = sheet.TabOrder, ModelToPaper = modelToPaper, PaperToModel = modelToPaper.Inverse(),
                Scale = vp.CustomScale, Twist = vp.TwistAngle
            };

            if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Curve clip)
            {
                try
                {
                    double length = clip.GetDistanceAtParameter(clip.EndParam);
                    int n = clip is Polyline pl && Enumerable.Range(0, pl.NumberOfVertices).All(i => Math.Abs(pl.GetBulgeAt(i)) < 1e-9) ? -1 : 200;
                    if (n < 0) { Polyline pl2 = (Polyline)clip; for (int i = 0; i < pl2.NumberOfVertices; i++) v.Outline.Add(pl2.GetPoint2dAt(i)); }
                    else for (int i = 0; i < n; i++) { Point3d p = clip.GetPointAtDist(length * i / n); v.Outline.Add(new Point2d(p.X, p.Y)); }
                }
                catch (Exception) { v.Outline.Clear(); }
            }
            if (v.Outline.Count < 3)
            {
                double hw = vp.Width / 2, hh = vp.Height / 2;
                Point3d c = vp.CenterPoint;
                v.Outline.AddRange(new[] { new Point2d(c.X - hw, c.Y - hh), new Point2d(c.X + hw, c.Y - hh), new Point2d(c.X + hw, c.Y + hh), new Point2d(c.X - hw, c.Y + hh) });
            }
            Box2 box = Box2.Empty;
            foreach (Point2d p in v.Outline) box = box.Add(p);
            v.PaperBox = new Extents2d(box.MinX, box.MinY, box.MaxX, box.MaxY);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point2d p in v.Outline)
            {
                Point3d m = new Point3d(p.X, p.Y, 0).TransformBy(v.PaperToModel);
                minX = Math.Min(minX, m.X); minY = Math.Min(minY, m.Y); maxX = Math.Max(maxX, m.X); maxY = Math.Max(maxY, m.Y);
            }
            v.ModelBox = new Extents2d(minX, minY, maxX, maxY);
            return v;
        }

        /// <summary>The paper-space boxes of the sheet's own text, leaders and blocks lying over a viewport (title notes, callouts).</summary>
        public static List<Box2> PaperObstacles(Transaction tr, SheetInfo sheet, SheetViewport v)
        {
            List<Box2> boxes = new List<Box2>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(sheet.BlockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (!(id.ObjectClass.Name.Contains("Text") || id.ObjectClass.Name.Contains("Leader"))) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                try
                {
                    Extents3d x = e.GeometricExtents;
                    Box2 b = new Box2(x.MinPoint.X, x.MinPoint.Y, x.MaxPoint.X, x.MaxPoint.Y);
                    if (b.Overlaps(new Box2(v.PaperBox.MinPoint.X, v.PaperBox.MinPoint.Y, v.PaperBox.MaxPoint.X, v.PaperBox.MaxPoint.Y))) boxes.Add(b);
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return boxes;
        }
    }
}
