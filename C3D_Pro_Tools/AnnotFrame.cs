using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using CLabel = Autodesk.Civil.DatabaseServices.Label;
using Color = Autodesk.AutoCAD.Colors.Color;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>A label as it shows on one sheet (paper mm): where it points, where its label point is, and its outline.</summary>
    internal sealed class LabelOutline
    {
        public (double X, double Y) Anchor, Location;
        /// <summary>The text and frame as one outline (convex hull), turned with the text as the sheet shows it.</summary>
        public List<(double X, double Y)> Hull = new List<(double X, double Y)>();
        /// <summary>The first text's direction on the sheet (degrees, 0 = reading along the viewport's X axis).</summary>
        public double TextAngleDeg = double.NaN;
        /// <summary>How far the outline was turned to read upright on the sheet (View-oriented labels), degrees.</summary>
        public double CorrectionDeg;
        public OrientationReferenceType? Orientation;
        public bool Dragged;
        /// <summary>Paper units per drawing unit of the label's own geometry.</summary>
        public double Factor;
    }

    /// <summary>
    /// The viewport frame the label tools work in, made visible: each label's outline as the sheet shows it, the obstacles, the
    /// viewport's axes and rotation (drawn in paper space on a layer that does not plot), and each viewport's boundary in model
    /// space (as a rectangle moved there with CHSPACE would be).
    /// </summary>
    internal static class AnnotFrame
    {
        public const string CheckLayer = "C3DPRO-ARRANGE-CHECK";
        public const string FramesLayer = "C3DPRO-VIEWPORT-FRAMES";

        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        /// <summary>The label style's orientation reference (Object, View or World), or null when it cannot be read.</summary>
        public static OrientationReferenceType? OrientationOf(Transaction tr, ObjectId styleId)
        {
            try
            {
                if (tr.GetObject(styleId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.LabelStyle s) return s.Properties.Behavior.OrientationReference.Value;
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return null;
        }

        /// <summary>
        /// The style shows a dragged label as stacked text: Civil 3D then draws it reading level in whatever view shows it
        /// (checked on a plot of tile 124 sheet 102054: dragged pipe labels of an Object-oriented style plot upright).
        /// </summary>
        public static bool StackedWhenDragged(Transaction tr, ObjectId styleId)
        {
            try
            {
                return tr.GetObject(styleId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.LabelStyle s &&
                       s.Properties.DraggedStateComponents.DisplayType.Value == Autodesk.Civil.LabelContentDisplayType.StackedText;
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return false; }
        }

        /// <summary>A DBText's four corners, turned with the text (its extents are an upright box round a turned text).</summary>
        internal static IEnumerable<Point3d> TextCorners(DBText t)
        {
            using (DBText c = (DBText)t.Clone())
            {
                c.TransformBy(Matrix3d.Rotation(-t.Rotation, Vector3d.ZAxis, t.Position));
                Extents3d x = c.GeometricExtents;
                Matrix3d back = Matrix3d.Rotation(t.Rotation, Vector3d.ZAxis, t.Position);
                return new[]
                {
                    x.MinPoint, new Point3d(x.MaxPoint.X, x.MinPoint.Y, x.MinPoint.Z), x.MaxPoint, new Point3d(x.MinPoint.X, x.MaxPoint.Y, x.MinPoint.Z)
                }.Select(p => p.TransformBy(back)).ToList();
            }
        }

        /// <summary>The label's text and frame points in the model (its leader left out), the first text's direction and height.</summary>
        private static (List<Point3d> pts, Vector3d? textDir, double textHeight) Explode(CLabel label)
        {
            List<Point3d> pts = new List<Point3d>();
            Vector3d? dir = null;
            double height = 0;
            ExplodedPieces.ForEach(label, 3, o =>
            {
                try
                {
                    switch (o)
                    {
                        case Leader _:
                        case MLeader _:
                            break;
                        case MText mt:
                            if (string.IsNullOrWhiteSpace(mt.Text)) break;
                            pts.AddRange(AnnotLabels.MTextCorners(mt));
                            if (dir == null) { dir = mt.Direction; height = mt.TextHeight; }
                            break;
                        case DBText dt:
                            if (string.IsNullOrWhiteSpace(dt.TextString)) break;
                            pts.AddRange(TextCorners(dt));
                            if (dir == null) { dir = new Vector3d(Math.Cos(dt.Rotation), Math.Sin(dt.Rotation), 0); height = dt.Height; }
                            break;
                        case Line l:
                            pts.Add(l.StartPoint); pts.Add(l.EndPoint);
                            break;
                        case Polyline pl:
                            for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint3dAt(i));
                            break;
                        case Entity e:
                            Extents3d x = e.GeometricExtents;
                            pts.Add(x.MinPoint); pts.Add(x.MaxPoint);
                            break;
                    }
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            });
            return (pts, dir, height);
        }

        /// <summary>
        /// The label as this sheet shows it: its points through the viewport from its anchor (turned with the view), at its plotted
        /// size. A View-oriented label reads horizontally in every viewport, whatever view it was last drawn in, so its outline is
        /// turned upright about its label point when the geometry read from the drawing is not.
        /// </summary>
        /// <param name="paperPerUnit">Paper units per drawing unit, for the style's plotted text height.</param>
        /// <param name="uprightWhenDragged">
        /// Dragged, the label reads upright on the sheet (as its style's dragged labels already do there): a label just moved is
        /// read back before Civil 3D draws it again for the viewport, turned with the model, so its outline is turned upright.
        /// </param>
        public static LabelOutline Outline(Transaction tr, CLabel label, SheetViewport v, double paperPerUnit, OrientationReferenceType? orientation, double factorHint = 0,
            bool uprightWhenDragged = false)
        {
            (List<Point3d> pts, Vector3d? dir, double textH) = Explode(label);
            if (pts.Count < 2) return null;
            Point3d anchor, location;
            try { anchor = label.AnchorInfo.Location; } catch (Exception) { anchor = pts[0]; }
            try { location = label.LabelLocation; } catch (Exception) { location = anchor; }
            double styleH = AnnotLabels.StyleTextHeight(tr, label.StyleId, paperPerUnit);
            double factor = styleH > 0 && textH > 0 ? styleH / textH : factorHint > 0 ? factorHint : v.Scale;

            LabelOutline o = new LabelOutline { Factor = factor, Orientation = orientation };
            try { o.Dragged = label.Dragged; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            Point2d ap = v.ToPaper(anchor), lp = v.LabelToPaper(anchor, location, factor);
            o.Anchor = (ap.X, ap.Y);
            o.Location = (lp.X, lp.Y);
            List<(double X, double Y)> paper = pts.Select(p => { Point2d q = v.LabelToPaper(anchor, p, factor); return (q.X, q.Y); }).ToList();
            if (dir.HasValue)
            {
                Vector3d d = dir.Value.TransformBy(v.ModelToPaper);
                double angle = Math.Atan2(d.Y, d.X);
                o.TextAngleDeg = Deg(angle);
                double off = LabelLayout.OffUpright(angle);
                bool upright = orientation == OrientationReferenceType.View || (o.Dragged && (uprightWhenDragged || StackedWhenDragged(tr, label.StyleId)));
                if (upright && Math.Abs(off) > Math.PI / 180)
                {
                    paper = LabelLayout.Turn(paper, o.Location, -off);
                    o.CorrectionDeg = -Deg(off);
                }
            }
            o.Hull = LabelLayout.Hull(paper);
            return o;
        }

        // =====================================================================================
        // Layers and drawing
        // =====================================================================================

        private static ObjectId Layer(Transaction tr, Database db, string name, short color)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            LayerTableRecord rec = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color), IsPlottable = false };
            ObjectId id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        /// <summary>Erases what this tool drew on <paramref name="layer"/> in the given block (a layout's paper space, or model space).</summary>
        private static int ClearIn(Transaction tr, ObjectId blockId, ObjectId layer, Func<Entity, bool> mine = null)
        {
            int n = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e && e.LayerId == layer && (mine == null || mine(e)))
                {
                    e.UpgradeOpen();
                    e.Erase();
                    n++;
                }
            return n;
        }

        private sealed class Pen
        {
            public Transaction Tr;
            public BlockTableRecord Space;
            public ObjectId Layer;
            public int Count;

            public void Poly(IEnumerable<(double X, double Y)> pts, short color, bool closed, double width = 0)
            {
                List<(double X, double Y)> list = pts.ToList();
                if (list.Count < 2) return;
                Polyline pl = new Polyline { LayerId = Layer, Color = Color.FromColorIndex(ColorMethod.ByAci, color), Closed = closed };
                for (int i = 0; i < list.Count; i++) pl.AddVertexAt(i, new Point2d(list[i].X, list[i].Y), 0, width, width);
                Add(pl);
            }

            public void Text((double X, double Y) at, string text, double height, short color, double rotation = 0)
            {
                Add(new DBText
                {
                    LayerId = Layer, Color = Color.FromColorIndex(ColorMethod.ByAci, color), Position = new Point3d(at.X, at.Y, 0),
                    Height = height, TextString = text, Rotation = rotation
                });
            }

            public void Cross((double X, double Y) p, double size, short color)
            {
                Poly(new[] { (p.X - size, p.Y - size), (p.X + size, p.Y + size) }, color, false);
                Poly(new[] { (p.X - size, p.Y + size), (p.X + size, p.Y - size) }, color, false);
            }

            private void Add(Entity e)
            {
                Space.AppendEntity(e);
                Tr.AddNewlyCreatedDBObject(e, true);
                Count++;
            }
        }

        /// <summary>One label style as found on the sheets checked: what it labels, how it is oriented, how many labels use it.</summary>
        internal sealed class StyleSeen
        {
            public LabelKind Kind;
            public string Style = "";
            public OrientationReferenceType? Orientation;
            public int Count, Dragged, Corrected;
        }

        /// <summary>
        /// Draws, in each sheet's paper space on <see cref="CheckLayer"/> (not plotted), what the label tools see in each viewport:
        /// its outline and X / Y axes with its rotation and UCS, the network parts, text and leaders labels keep clear of (grey and
        /// yellow), and every pipe and structure label's outline as the sheet shows it (cyan normal, magenta dragged, with its
        /// leader). Earlier drawings of this check on those sheets are replaced. Returns the styles found.
        /// </summary>
        public static List<StyleSeen> DrawCheck(Transaction tr, Database db, IEnumerable<SheetInfo> sheets, AnnotScene scene, List<LabelInfo> labels,
            double paperPerUnit, List<string> report)
        {
            ObjectId layer = Layer(tr, db, CheckLayer, 1);
            Dictionary<string, StyleSeen> styles = new Dictionary<string, StyleSeen>();
            Dictionary<ObjectId, OrientationReferenceType?> orientations = new Dictionary<ObjectId, OrientationReferenceType?>();
            foreach (SheetInfo sheet in sheets)
            {
                ClearIn(tr, sheet.BlockId, layer);
                Pen pen = new Pen { Tr = tr, Space = (BlockTableRecord)tr.GetObject(sheet.BlockId, OpenMode.ForWrite), Layer = layer };
                foreach (SheetViewport v in sheet.Viewports)
                {
                    List<(double X, double Y)> outline = v.Outline.Select(p => (p.X, p.Y)).ToList();
                    bool Shows((double X, double Y) p) => LabelLayout.InPolygon(outline, p);
                    List<(double X, double Y)> ToPaper(IEnumerable<Point3d> pts) => pts.Select(p => { Point2d q = v.ToPaper(p); return (q.X, q.Y); }).ToList();

                    // The viewport, its axes and its rotation.
                    pen.Poly(outline, 1, true, 0.35);
                    (double X, double Y) c = ((v.PaperBox.MinPoint.X + v.PaperBox.MaxPoint.X) / 2, (v.PaperBox.MinPoint.Y + v.PaperBox.MaxPoint.Y) / 2);
                    pen.Poly(new[] { c, (c.X + 25, c.Y) }, 1, false, 0.5);
                    pen.Text((c.X + 26, c.Y - 1.25), "X", 2.5, 1);
                    pen.Poly(new[] { c, (c.X, c.Y + 25) }, 3, false, 0.5);
                    pen.Text((c.X - 1, c.Y + 26), "Y", 2.5, 3);
                    double xInModel = Deg(v.XAxisInModel);
                    string ucs = double.IsNaN(v.UcsAngle) ? "no UCS of its own"
                        : $"UCS X at {Deg(v.UcsAngle):0.##}° in the model ({(Math.Abs(Math.IEEERemainder(v.UcsAngle - v.XAxisInModel, 2 * Math.PI)) < 1e-4 ? "matches the view" : "differs from the view")})";
                    string info = $"{v.ScaleText}, twist {Deg(v.Twist):0.##}°: the sheet's X axis runs at {xInModel:0.##}° in the model; {ucs}";
                    pen.Text((v.PaperBox.MinPoint.X + 3, v.PaperBox.MaxPoint.Y - 5), info, 2.5, 1);
                    report.Add($"{sheet.Layout}: {info}.");

                    // What labels keep clear of.
                    foreach ((Point3d[] o, ObjectId _, ObjectId _) in scene.Parts)
                    {
                        List<(double X, double Y)> p = ToPaper(o);
                        if (p.Exists(Shows)) pen.Poly(p, 2, true);
                    }
                    foreach (Point3d[] t in scene.Texts)
                    {
                        List<(double X, double Y)> p = ToPaper(t);
                        if (p.Exists(Shows)) pen.Poly(LabelLayout.Hull(p), 8, true);
                    }
                    foreach ((Point3d a, Point3d b) in scene.LeaderLines)
                    {
                        List<(double X, double Y)> p = ToPaper(new[] { a, b });
                        if (p.Exists(Shows)) pen.Poly(p, 8, false);
                    }
                    foreach (ObjectId id in scene.OtherLabels)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is CLabel other)) continue;
                        LabelOutline lo = Outline(tr, other, v, paperPerUnit, null);
                        if (lo == null || !lo.Hull.Exists(Shows)) continue;
                        pen.Poly(lo.Hull, 8, true);
                    }

                    // The pipe and structure labels, as the sheet shows them.
                    foreach (LabelInfo l in labels)
                    {
                        if (!(v.Sees(l.Anchor) || v.Sees(l.Location)) || !(tr.GetObject(l.Id, OpenMode.ForRead) is CLabel label)) continue;
                        if (!orientations.TryGetValue(label.StyleId, out OrientationReferenceType? orient)) orientations[label.StyleId] = orient = OrientationOf(tr, label.StyleId);
                        LabelOutline lo = Outline(tr, label, v, paperPerUnit, orient);
                        if (lo == null) continue;
                        short color = (short)(lo.Dragged ? 6 : 4);
                        pen.Poly(lo.Hull, color, true, 0.25);
                        pen.Cross(lo.Anchor, 0.8, color);
                        if (lo.Dragged) pen.Poly(new[] { lo.Anchor, lo.Location }, color, false);
                        string key = l.Kind + ":" + l.Style;
                        if (!styles.TryGetValue(key, out StyleSeen s)) styles[key] = s = new StyleSeen { Kind = l.Kind, Style = l.Style, Orientation = orient };
                        s.Count++;
                        if (lo.Dragged) s.Dragged++;
                        if (Math.Abs(lo.CorrectionDeg) > 1e-6) s.Corrected++;
                    }
                }
            }
            return styles.Values.OrderBy(s => s.Kind).ThenBy(s => s.Style, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Draws a check or a preview of arranging on the sheets (layer <see cref="CheckLayer"/>, not plotted): each label's
        /// outline as checked or planned (cyan normal, green dragged, red with a problem or no clear place, magenta removed as
        /// not the labelled pipe of its parallel run), its leader and the
        /// point it labels. Earlier drawings on those sheets are replaced.
        /// </summary>
        public static int DrawResult(Transaction tr, Database db, IEnumerable<SheetInfo> sheets, IEnumerable<ArrangeShape> shapes)
        {
            ObjectId layer = Layer(tr, db, CheckLayer, 1);
            List<SheetInfo> list = sheets.ToList();
            foreach (SheetInfo s in list) ClearIn(tr, s.BlockId, layer);
            Dictionary<ObjectId, Pen> pens = list.ToDictionary(s => s.BlockId, s => new Pen { Tr = tr, Space = (BlockTableRecord)tr.GetObject(s.BlockId, OpenMode.ForWrite), Layer = layer });
            int n = 0;
            foreach (ArrangeShape a in shapes)
            {
                if (!pens.TryGetValue(a.SheetBlock, out Pen pen)) continue;
                short color = (short)(a.State == 3 ? 6 : a.State == 2 ? 1 : a.State == 1 ? 3 : 4);
                pen.Poly(a.Outline, color, true, a.State == 2 ? 0.5 : 0.3);
                pen.Cross(a.Anchor, 0.8, color);
                if (a.End.HasValue) pen.Poly(new[] { a.Anchor, a.End.Value }, color, false, 0.2);
                n++;
            }
            return n;
        }

        /// <summary>Erases the check drawings from these sheets.</summary>
        public static int ClearCheck(Transaction tr, Database db, IEnumerable<SheetInfo> sheets)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(CheckLayer)) return 0;
            return sheets.Sum(s => ClearIn(tr, s.BlockId, lt[CheckLayer]));
        }

        /// <summary>
        /// Draws each viewport's boundary in model space on <see cref="FramesLayer"/> (not plotted), named with its sheet: the area
        /// the sheet shows and its X / Y directions, as a rectangle round the viewport moved there with CHSPACE. A sheet drawn
        /// again replaces its earlier frame.
        /// </summary>
        public static int FramesToModel(Transaction tr, Database db, IEnumerable<SheetInfo> sheets, IReadOnlyDictionary<string, string> tilePartOf = null)
        {
            ObjectId layer = Layer(tr, db, SheetFramesLayer, 30);
            RegAppTable apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!apps.Has(AnnotLabels.RegApp))
            {
                apps.UpgradeOpen();
                RegAppTableRecord app = new RegAppTableRecord { Name = AnnotLabels.RegApp };
                apps.Add(app);
                tr.AddNewlyCreatedDBObject(app, true);
            }
            ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            List<SheetInfo> list = sheets.ToList();
            RemoveFrames(tr, db, list);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForWrite);
            int n = 0;
            foreach (SheetInfo sheet in list)
                for (int i = 0; i < sheet.Viewports.Count; i++)
                {
                    SheetViewport v = sheet.Viewports[i];
                    ResultBuffer Tag() => new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AnnotLabels.RegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "frame:" + sheet.Layout));
                    Polyline pl = new Polyline { LayerId = layer, Closed = true };
                    for (int k = 0; k < v.Outline.Count; k++)
                    {
                        Point3d m = new Point3d(v.Outline[k].X, v.Outline[k].Y, 0).TransformBy(v.PaperToModel);
                        pl.AddVertexAt(k, new Point2d(m.X, m.Y), 0, 0, 0);
                    }
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    pl.XData = Tag();
                    // In its top left corner, reading along the sheet's X axis: the tile and part, 5 mm high as plotted (when the
                    // sheet has them), and the sheet's name under it, 3.5 mm.
                    string tilePart = tilePartOf != null && tilePartOf.TryGetValue(sheet.Layout, out string tp) ? tp : "";
                    string layoutText = sheet.Viewports.Count > 1 ? $"{sheet.Layout} ({i + 1})" : sheet.Layout;
                    double y = v.PaperBox.MaxPoint.Y - 3;
                    void Text(string s, double heightMm)
                    {
                        y -= heightMm;
                        DBText t = new DBText
                        {
                            LayerId = layer, Position = new Point3d(v.PaperBox.MinPoint.X + 3, y, 0).TransformBy(v.PaperToModel),
                            Height = heightMm / v.Scale, Rotation = v.XAxisInModel, TextString = s
                        };
                        ms.AppendEntity(t);
                        tr.AddNewlyCreatedDBObject(t, true);
                        t.XData = Tag();
                        y -= heightMm * 0.6;
                    }
                    if (tilePart.Length > 0) Text(tilePart, 5);
                    Text(layoutText, 3.5);
                    n++;
                }
            return n;
        }

        /// <summary>The KSIA layer of the sheet frames: Zz_10_20_95 is "Viewports" (Uniclass); model content, not plotted.</summary>
        public const string SheetFramesLayer = "Z-Zz_10_20_95-M-SheetFrames";

        /// <summary>
        /// Erases the frames drawn in model space for these sheets (every sheet when null): only what the frames tool drew (its
        /// tag), on the frames layer or the one it used before (C3DPRO-VIEWPORT-FRAMES).
        /// </summary>
        public static int RemoveFrames(Transaction tr, Database db, IEnumerable<SheetInfo> sheets = null)
        {
            HashSet<string> tags = sheets == null ? null : new HashSet<string>(sheets.Select(s => "frame:" + s.Layout));
            bool Mine(Entity e)
            {
                ResultBuffer rb = e.GetXDataForApplication(AnnotLabels.RegApp);
                if (rb == null) return false;
                using (rb) return rb.AsArray().Any(t => t.Value is string s && s.StartsWith("frame:", StringComparison.Ordinal) && (tags == null || tags.Contains(s)));
            }
            ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            int n = 0;
            foreach (string name in new[] { SheetFramesLayer, FramesLayer })
                if (lt.Has(name)) n += ClearIn(tr, msId, lt[name], Mine);
            return n;
        }
    }
}
