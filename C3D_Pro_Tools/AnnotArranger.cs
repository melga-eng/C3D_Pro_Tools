using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using CLabel = Autodesk.Civil.DatabaseServices.Label;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    internal sealed class ArrangeOptions
    {
        /// <summary>Furthest a label may sit from the point it labels, on paper (mm).</summary>
        public double MaxDistance = 60;
        /// <summary>Clear space kept around every label (mm).</summary>
        public double Gap = 1.5;
        /// <summary>Clear space kept inside the viewport edge (mm).</summary>
        public double Margin = 3;
        /// <summary>True: every unlocked label is placed again. False: only labels that overlap, are cut by the edge or cover pipes move.</summary>
        public bool RearrangeAll = true;
        /// <summary>Labels sitting on their pipe (not dragged) slide along it instead of being dragged out with a leader.</summary>
        public bool SlideOnPipe;
        /// <summary>The labels that may move (null = all); the others stay where they are and only count as obstacles.</summary>
        public HashSet<ObjectId> Only;
        public bool AvoidModelText = true;
        /// <summary>Linework and text of loaded xrefs (and the blocks in them) count as obstacles too.</summary>
        public bool AvoidXrefs = true;
        /// <summary>How far past a viewport's edge (paper mm, through its scale) obstacles are gathered.</summary>
        public double NearMarginMm = 20;
        public bool Plan = true, Profile = true;
    }

    internal sealed class ArrangeIssue
    {
        public ObjectId LabelId;
        public string Sheet = "", Text = "";
        public Point3d Where;
    }

    internal sealed class ArrangeReport
    {
        public int Checked, Moved, Kept, Unresolved, Unmeasured, Problems;
        public readonly List<ArrangeIssue> Issues = new List<ArrangeIssue>();
        /// <summary>Where each label sits on the sheet (paper), as checked or as placed.</summary>
        public readonly Dictionary<ObjectId, Box2> Boxes = new Dictionary<ObjectId, Box2>();
        public void Add(ArrangeReport r)
        {
            foreach (KeyValuePair<ObjectId, Box2> kv in r.Boxes) Boxes[kv.Key] = kv.Value;
            Checked += r.Checked; Moved += r.Moved; Kept += r.Kept; Unresolved += r.Unresolved; Unmeasured += r.Unmeasured; Problems += r.Problems;
            Issues.AddRange(r.Issues);
        }
    }

    /// <summary>What labels must keep clear of, gathered once from the drawing (model coordinates).</summary>
    internal sealed class AnnotScene
    {
        public readonly List<(Point3d a, Point3d b, ObjectId part)> PipeSegments = new List<(Point3d, Point3d, ObjectId)>();
        public readonly List<(Point3d[] corners, ObjectId part)> Structures = new List<(Point3d[], ObjectId)>();
        public readonly List<Point3d[]> Texts = new List<Point3d[]>();
        /// <summary>Linework and text boxes of loaded xrefs, in the host drawing's coordinates.</summary>
        public readonly List<(Point3d a, Point3d b)> XrefSegments = new List<(Point3d, Point3d)>();
        public readonly List<Point3d[]> XrefTexts = new List<Point3d[]>();
        /// <summary>Most xref items read, so a huge background drawing cannot stall the arranger.</summary>
        private const int MaxXrefItems = 200000;
        /// <summary>Per profile view: its whole box, its band area below the graph, and the parts drawn in it.</summary>
        public readonly Dictionary<ObjectId, ProfileArea> Profiles = new Dictionary<ObjectId, ProfileArea>();

        private static bool Hidden(Transaction tr, ObjectId layerId)
        {
            try { return tr.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord l && (l.IsOff || l.IsFrozen); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The visible linework and text of a loaded xref (an insert at depth 0 must be an xref; blocks inside it are read too,
        /// two levels down), moved into the host drawing by <paramref name="xf"/>.
        /// </summary>
        private void ReadXref(Transaction tr, BlockReference br, Matrix3d xf, int depth)
        {
            if (depth > 2 || !(tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr)) return;
            if (depth == 0 && (!btr.IsFromExternalReference || !btr.IsResolved)) return;
            if (Hidden(tr, br.LayerId)) return;
            foreach (ObjectId id in btr)
            {
                if (XrefSegments.Count + XrefTexts.Count > MaxXrefItems) return;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || !e.Visible || Hidden(tr, e.LayerId)) continue;
                try
                {
                    switch (e)
                    {
                        case Line l:
                            XrefSegments.Add((l.StartPoint.TransformBy(xf), l.EndPoint.TransformBy(xf)));
                            break;
                        case Polyline pl:
                        {
                            int n = pl.NumberOfVertices, count = pl.Closed ? n : n - 1;
                            for (int i = 0; i < count; i++)
                            {
                                Point3d a = pl.GetPoint3dAt(i), b = pl.GetPoint3dAt((i + 1) % n);
                                Point3d prev = a;
                                foreach ((double X, double Y) q in PlanGeometry.ArcPoints(a.X, a.Y, b.X, b.Y, pl.GetBulgeAt(i), 0.1, 32))
                                {
                                    Point3d next = new Point3d(q.X, q.Y, a.Z);
                                    XrefSegments.Add((prev.TransformBy(xf), next.TransformBy(xf)));
                                    prev = next;
                                }
                            }
                            break;
                        }
                        case MText mt:
                            XrefTexts.Add(mt.GetBoundingPoints().Cast<Point3d>().Select(p => p.TransformBy(xf)).ToArray());
                            break;
                        case DBText _:
                            XrefTexts.Add(Corners(e.GeometricExtents).Select(p => p.TransformBy(xf)).ToArray());
                            break;
                        case BlockReference inner:
                            ReadXref(tr, inner, xf * inner.BlockTransform, depth + 1);
                            break;
                        case Curve c:
                        {
                            // Arcs, circles, splines, 2D / 3D polylines: sampled along their parameter.
                            double p0 = c.StartParam, p1 = c.EndParam;
                            int n = c is Polyline2d || c is Polyline3d ? Math.Max(1, (int)Math.Round(p1 - p0)) * 4 : 32;
                            Point3d prev = c.GetPointAtParameter(p0);
                            for (int i = 1; i <= n; i++)
                            {
                                Point3d next = c.GetPointAtParameter(p0 + (p1 - p0) * i / n);
                                XrefSegments.Add((prev.TransformBy(xf), next.TransformBy(xf)));
                                prev = next;
                            }
                            break;
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        internal sealed class ProfileArea
        {
            public Point3d[] View, Bands;
            public readonly List<(Point3d a, Point3d b, ObjectId part)> Segments = new List<(Point3d, Point3d, ObjectId)>();
            public readonly List<Point3d[]> Boxes = new List<Point3d[]>();
        }

        public static AnnotScene Read(Transaction tr, Database db, CivilDocument doc, bool texts, bool profiles, bool xrefs = false)
        {
            AnnotScene s = new AnnotScene();
            if (xrefs)
            {
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in space)
                    if (id.ObjectClass.DxfName == "INSERT" && tr.GetObject(id, OpenMode.ForRead) is BlockReference br && br.Visible)
                        try { s.ReadXref(tr, br, br.BlockTransform, 0); } catch (Exception) { }
            }
            List<(Pipe pipe, ObjectIdCollection views)> profilePipes = new List<(Pipe, ObjectIdCollection)>();
            List<(Structure st, ObjectIdCollection views)> profileStructures = new List<(Structure, ObjectIdCollection)>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                    ArcInfo? arc = null;
                    if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
                    List<Point3d> path = PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 2.0, 100);
                    for (int i = 1; i < path.Count; i++) s.PipeSegments.Add((path[i - 1], path[i], id));
                    if (profiles) { try { ObjectIdCollection v = p.GetProfileViewsDisplayingMe(); if (v.Count > 0) profilePipes.Add((p, v)); } catch (Exception) { } }
                }
                foreach (ObjectId id in net.GetStructureIds())
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Structure st)) continue;
                    try { s.Structures.Add((Corners(st.GeometricExtents), id)); } catch (Exception) { }
                    if (profiles) { try { ObjectIdCollection v = st.GetProfileViewsDisplayingMe(); if (v.Count > 0) profileStructures.Add((st, v)); } catch (Exception) { } }
                }
            }
            foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is PressurePipe p) s.PipeSegments.Add((p.StartPoint, p.EndPoint, id));
            }

            if (texts)
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    string cls = id.ObjectClass.DxfName;
                    if (cls != "MTEXT" && cls != "TEXT") continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || !e.Visible) continue;
                    try
                    {
                        if (e is MText mt) { Point3dCollection c = mt.GetBoundingPoints(); s.Texts.Add(c.Cast<Point3d>().ToArray()); }
                        else s.Texts.Add(Corners(e.GeometricExtents));
                    }
                    catch (Exception) { }
                }
            }

            if (profiles)
            {
                foreach ((Pipe p, ObjectIdCollection views) in profilePipes)
                    foreach (ObjectId viewId in views)
                    {
                        ProfileArea area = Area(tr, s, viewId);
                        if (area == null || !(tr.GetObject(viewId, OpenMode.ForRead) is ProfileView pv)) continue;
                        Alignment al = tr.GetObject(pv.AlignmentId, OpenMode.ForRead) as Alignment;
                        if (al == null) continue;
                        double half = CoverRoute.OuterHeightOf(p) / 2.0;
                        Point3d? a1 = ProfilePoint(pv, al, p.StartPoint, p.StartPoint.Z + half), b1 = ProfilePoint(pv, al, p.EndPoint, p.EndPoint.Z + half);
                        Point3d? a2 = ProfilePoint(pv, al, p.StartPoint, p.StartPoint.Z - half), b2 = ProfilePoint(pv, al, p.EndPoint, p.EndPoint.Z - half);
                        if (a1 != null && b1 != null) area.Segments.Add((a1.Value, b1.Value, p.ObjectId));
                        if (a2 != null && b2 != null) area.Segments.Add((a2.Value, b2.Value, p.ObjectId));
                    }
                foreach ((Structure st, ObjectIdCollection views) in profileStructures)
                    foreach (ObjectId viewId in views)
                    {
                        ProfileArea area = Area(tr, s, viewId);
                        if (area == null || !(tr.GetObject(viewId, OpenMode.ForRead) is ProfileView pv)) continue;
                        Alignment al = tr.GetObject(pv.AlignmentId, OpenMode.ForRead) as Alignment;
                        if (al == null) continue;
                        double hw = Math.Max(0.3, st.DiameterOrWidth / 2.0);
                        double sta = 0, off = 0;
                        try { al.StationOffset(st.Location.X, st.Location.Y, ref sta, ref off); } catch (Exception) { continue; }
                        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                        if (pv.FindXYAtStationAndElevation(sta - hw, st.SumpElevation, ref x1, ref y1) && pv.FindXYAtStationAndElevation(sta + hw, st.RimElevation, ref x2, ref y2))
                            area.Boxes.Add(new[] { new Point3d(x1, y1, 0), new Point3d(x2, y1, 0), new Point3d(x2, y2, 0), new Point3d(x1, y2, 0) });
                    }
            }
            return s;
        }

        private static ProfileArea Area(Transaction tr, AnnotScene s, ObjectId viewId)
        {
            if (viewId.IsNull || viewId.IsErased) return null;
            if (s.Profiles.TryGetValue(viewId, out ProfileArea a)) return a;
            if (!(tr.GetObject(viewId, OpenMode.ForRead) is ProfileView pv)) return s.Profiles[viewId] = null;
            try
            {
                Extents3d x = pv.GeometricExtents;
                a = new ProfileArea { View = Corners(x) };
                double gx = 0, gy = 0;
                if (pv.FindXYAtStationAndElevation(pv.StationStart, pv.ElevationMin, ref gx, ref gy) && gy > x.MinPoint.Y)
                    a.Bands = Corners(new Extents3d(x.MinPoint, new Point3d(x.MaxPoint.X, gy, 0)));
            }
            catch (Exception) { a = null; }
            return s.Profiles[viewId] = a;
        }

        private static Point3d? ProfilePoint(ProfileView pv, Alignment al, Point3d plan, double elevation)
        {
            double sta = 0, off = 0, x = 0, y = 0;
            try { al.StationOffset(plan.X, plan.Y, ref sta, ref off); } catch (Exception) { return null; }
            if (sta < pv.StationStart - 1e-6 || sta > pv.StationEnd + 1e-6) return null;
            return pv.FindXYAtStationAndElevation(sta, elevation, ref x, ref y) ? new Point3d(x, y, 0) : (Point3d?)null;
        }

        public static Point3d[] Corners(Extents3d x) => new[]
        {
            x.MinPoint, new Point3d(x.MaxPoint.X, x.MinPoint.Y, 0), x.MaxPoint, new Point3d(x.MinPoint.X, x.MaxPoint.Y, 0)
        };
    }

    /// <summary>
    /// Places labels so the plotted sheet reads cleanly. Everything is worked out on each viewport's paper: the anchor goes
    /// through the viewport (its scale and rotation), and the label itself appears at its plotted size, upright on the sheet.
    /// Labels keep inside the viewport (and their profile view), clear of pipes, structures, text and each other: rows beside the
    /// route first, then the nearest free spot within the distance limit. Dragged labels stay dragged, pointing to their part;
    /// labels sitting on their pipe slide along it instead.
    /// </summary>
    internal static class AnnotArranger
    {

        private sealed class Item
        {
            public LabelInfo Info;
            public Point2d Anchor, Location;
            public Box2 Box;
            /// <summary>Paper units per drawing unit of the label's own geometry (plotted size over its size in the model).</summary>
            public double Factor;
            /// <summary>Height of the label point above the box middle (it attaches at the box side facing the anchor).</summary>
            public double AttachDy;
            public Vector2d Along = new Vector2d(1, 0);
            public Box2? Region;
            public bool Movable, Slides, Rough, Fixed;
        }

        /// <summary>Spatial buckets on paper, so each candidate is checked only against what is near it.</summary>
        private sealed class Grid
        {
            private const double Cell = 15.0;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
            public void Add(Box2 b, int index)
            {
                if (b.Width > 5000 || b.Height > 5000) return;
                for (int x = (int)Math.Floor(b.MinX / Cell); x <= (int)Math.Floor(b.MaxX / Cell); x++)
                    for (int y = (int)Math.Floor(b.MinY / Cell); y <= (int)Math.Floor(b.MaxY / Cell); y++)
                    {
                        if (!_cells.TryGetValue(Key(x, y), out List<int> l)) _cells[Key(x, y)] = l = new List<int>();
                        l.Add(index);
                    }
            }
            public HashSet<int> Query(Box2 b)
            {
                HashSet<int> found = new HashSet<int>();
                for (int x = (int)Math.Floor(b.MinX / Cell); x <= (int)Math.Floor(b.MaxX / Cell); x++)
                    for (int y = (int)Math.Floor(b.MinY / Cell); y <= (int)Math.Floor(b.MaxY / Cell); y++)
                        if (_cells.TryGetValue(Key(x, y), out List<int> l)) foreach (int i in l) found.Add(i);
                return found;
            }
        }

        /// <summary>Arranges (or with <paramref name="checkOnly"/> only checks) the labels shown in the sheet's viewports.</summary>
        public static ArrangeReport Arrange(Transaction tr, SheetInfo sheet, List<LabelInfo> labels, AnnotScene scene, ArrangeOptions o, HashSet<ObjectId> handled,
            bool checkOnly, double paperPerUnit = 1000)
        {
            ArrangeReport report = new ArrangeReport();
            Dictionary<ObjectId, double> styleHeights = new Dictionary<ObjectId, double>();
            foreach (SheetViewport v in sheet.Viewports)
                report.Add(ArrangeViewport(tr, sheet, v, labels, scene, o, handled, checkOnly, AnnotSheets.PaperObstacles(tr, sheet, v), styleHeights, paperPerUnit));
            return report;
        }

        private static Box2 ToPaper(SheetViewport v, IEnumerable<Point3d> pts)
        {
            Box2 b = Box2.Empty;
            foreach (Point3d p in pts) b = b.Add(v.ToPaper(p));
            return b;
        }

        /// <summary>How the label appears on this sheet: its anchor through the viewport, its own shape upright at plotted size.</summary>
        private static Box2 Shown(Item it, LabelInfo l, IEnumerable<Point3d> pts)
        {
            Box2 b = Box2.Empty;
            foreach (Point3d p in pts) b = b.Add(new Point2d(it.Anchor.X + (p.X - l.Anchor.X) * it.Factor, it.Anchor.Y + (p.Y - l.Anchor.Y) * it.Factor));
            return b;
        }

        private static ArrangeReport ArrangeViewport(Transaction tr, SheetInfo sheet, SheetViewport v, List<LabelInfo> labels, AnnotScene scene, ArrangeOptions o,
            HashSet<ObjectId> handled, bool checkOnly, List<Box2> paper, Dictionary<ObjectId, double> styleHeights, double paperPerUnit)
        {
            ArrangeReport report = new ArrangeReport();
            Box2 vpBox = new Box2(v.PaperBox.MinPoint.X, v.PaperBox.MinPoint.Y, v.PaperBox.MaxPoint.X, v.PaperBox.MaxPoint.Y);

            // ---- Obstacles on this sheet ----
            List<(Point2d a, Point2d b)> segs = new List<(Point2d, Point2d)>();
            List<string> segKinds = new List<string>();
            List<Box2> boxes = new List<Box2>(paper);
            List<string> boxKinds = paper.Select(_ => "sheet text").ToList();
            // Obstacles are gathered a little past the viewport: a set paper distance, in drawing units through its scale.
            double near = PlanGeometry.PaperToModel(o.NearMarginMm, v.Scale);
            bool Near(Point3d p) => p.X >= v.ModelBox.MinPoint.X - near && p.X <= v.ModelBox.MaxPoint.X + near && p.Y >= v.ModelBox.MinPoint.Y - near && p.Y <= v.ModelBox.MaxPoint.Y + near;
            // A segment counts when its own box reaches the area (a long pipe can cross the viewport with both ends far outside).
            bool NearSegment(Point3d a, Point3d b) =>
                Math.Max(a.X, b.X) >= v.ModelBox.MinPoint.X - near && Math.Min(a.X, b.X) <= v.ModelBox.MaxPoint.X + near &&
                Math.Max(a.Y, b.Y) >= v.ModelBox.MinPoint.Y - near && Math.Min(a.Y, b.Y) <= v.ModelBox.MaxPoint.Y + near;
            foreach ((Point3d a, Point3d b, ObjectId _) in scene.PipeSegments)
                if (NearSegment(a, b)) { Point2d pa = v.ToPaper(a), pb = v.ToPaper(b); if (vpBox.Hits(pa, pb)) { segs.Add((pa, pb)); segKinds.Add("pipe"); } }
            foreach ((Point3d a, Point3d b) in scene.XrefSegments)
                if (NearSegment(a, b)) { Point2d pa = v.ToPaper(a), pb = v.ToPaper(b); if (vpBox.Hits(pa, pb)) { segs.Add((pa, pb)); segKinds.Add("xref line"); } }
            foreach (Point3d[] c in scene.XrefTexts)
                if (c.Length > 0 && Near(c[0])) { Box2 b = ToPaper(v, c); if (b.Overlaps(vpBox)) { boxes.Add(b); boxKinds.Add("xref text"); } }
            foreach ((Point3d[] c, ObjectId _) in scene.Structures)
                if (Near(c[0])) { Box2 b = ToPaper(v, c); if (b.Overlaps(vpBox)) { boxes.Add(b); boxKinds.Add("structure"); } }
            foreach (Point3d[] c in scene.Texts)
                if (Near(c[0])) { Box2 b = ToPaper(v, c); if (b.Overlaps(vpBox)) { boxes.Add(b); boxKinds.Add("text"); } }
            Dictionary<ObjectId, Box2> profileRegions = new Dictionary<ObjectId, Box2>();
            foreach (KeyValuePair<ObjectId, AnnotScene.ProfileArea> kv in scene.Profiles)
            {
                if (kv.Value == null || !Near(kv.Value.View[0]) && !Near(kv.Value.View[2])) continue;
                Box2 region = ToPaper(v, kv.Value.View);
                if (!region.Overlaps(vpBox)) continue;
                profileRegions[kv.Key] = region;
                if (kv.Value.Bands != null) { boxes.Add(ToPaper(v, kv.Value.Bands)); boxKinds.Add("profile band"); }
                foreach ((Point3d a, Point3d b, ObjectId _) in kv.Value.Segments) { segs.Add((v.ToPaper(a), v.ToPaper(b))); segKinds.Add("pipe"); }
                foreach (Point3d[] c in kv.Value.Boxes) { boxes.Add(ToPaper(v, c)); boxKinds.Add("profile structure"); }
            }

            // ---- The labels shown here, as they appear on the sheet ----
            List<Item> items = new List<Item>();
            List<(Item it, List<Point3d> pts, double textH)> pending = new List<(Item, List<Point3d>, double)>();
            foreach (LabelInfo l in labels)
            {
                // On this sheet when its anchor or its text is in the viewport.
                // A label already placed on another sheet (or not chosen) stays put here, and still counts as an obstacle.
                if ((l.IsProfile ? !o.Profile : !o.Plan) || !(v.Sees(l.Anchor) || v.Sees(l.Location))) continue;
                if (!(tr.GetObject(l.Id, OpenMode.ForRead) is CLabel e)) continue;
                bool chosen = (o.Only == null || o.Only.Contains(l.Id)) && !handled.Contains(l.Id);
                List<Point3d> pts = AnnotLabels.Measure(e, out double textH, out bool exact);
                if (pts == null)
                {
                    if (chosen)
                    {
                        report.Unmeasured++;
                        report.Issues.Add(new ArrangeIssue { LabelId = l.Id, Sheet = sheet.Layout, Where = l.Anchor, Text = "Could not be measured; left in place" });
                    }
                    continue;
                }
                if (!styleHeights.TryGetValue(e.StyleId, out double sh)) styleHeights[e.StyleId] = sh = AnnotLabels.StyleTextHeight(tr, e.StyleId, paperPerUnit);
                Item it = new Item { Info = l, Anchor = v.ToPaper(l.Anchor), Factor = sh > 0 && textH > 0 ? sh / textH : 0, Rough = !exact, Fixed = !chosen };
                it.Slides = o.SlideOnPipe && l.IsPipe && !l.Dragged;
                pending.Add((it, pts, textH));
            }
            // Labels without readable text take the factor of their kind.
            double fallback = pending.Where(p => p.it.Factor > 0).Select(p => p.it.Factor).DefaultIfEmpty(v.Scale).Average();
            foreach ((Item it, List<Point3d> pts, double _) in pending)
            {
                LabelInfo l = it.Info;
                if (it.Factor <= 0) it.Factor = pending.Where(p => p.it.Factor > 0 && p.it.Info.Style == l.Style).Select(p => p.it.Factor).DefaultIfEmpty(fallback).First();
                it.Box = Shown(it, l, pts);
                it.Location = new Point2d(it.Anchor.X + (l.Location.X - l.Anchor.X) * it.Factor, it.Anchor.Y + (l.Location.Y - l.Anchor.Y) * it.Factor);
                it.AttachDy = it.Location.Y - it.Box.Center.Y;
                if (l.IsProfile)
                {
                    if (profileRegions.TryGetValue(l.ViewId, out Box2 r)) it.Region = r;
                }
                else if (l.IsPipe)
                {
                    double best = double.MaxValue;
                    foreach ((Point3d a, Point3d b, ObjectId part) in scene.PipeSegments)
                    {
                        if (part != l.PartId) continue;
                        Point2d pa = v.ToPaper(a), pb = v.ToPaper(b);
                        double d = Geometry2d.DistanceToSegment(it.Anchor, pa, pb);
                        if (d < best && pa.GetDistanceTo(pb) > 1e-6) { best = d; it.Along = (pb - pa).GetNormal(); }
                    }
                }
                items.Add(it);
            }
            report.Checked = items.Count(i => !i.Fixed);
            if (report.Checked == 0) return report;

            Grid grid = new Grid();
            List<Box2> all = new List<Box2>();
            List<string> kinds = new List<string>();
            int AddBox(Box2 b, string kind) { all.Add(b); kinds.Add(kind); grid.Add(b, all.Count - 1); return all.Count - 1; }
            for (int i = 0; i < boxes.Count; i++) AddBox(boxes[i], boxKinds[i]);
            Grid segGrid = new Grid();
            for (int i = 0; i < segs.Count; i++) segGrid.Add(Box2.Empty.Add(segs[i].a).Add(segs[i].b), i);
            List<(Point2d a, Point2d b)> leaders = new List<(Point2d, Point2d)>();

            // A label sitting on its pipe (not dragged) is drawn over that pipe on purpose: only other pipes count against it.
            bool Clear(Box2 b, int skip, Item it)
            {
                Box2 g = b.Grow(o.Gap);
                foreach (int i in grid.Query(g)) if (i != skip && all[i].Overlaps(g)) return false;
                if (it != null && it.Slides) return true;
                foreach (int i in segGrid.Query(g)) if (b.Hits(segs[i].a, segs[i].b)) return false;
                return true;
            }
            bool InArea(Item it, Box2 b) => v.Inside(b, o.Margin) && (it.Region == null || (b.MinX >= it.Region.Value.MinX && b.MaxX <= it.Region.Value.MaxX && b.MinY >= it.Region.Value.MinY && b.MaxY <= it.Region.Value.MaxY));

            Dictionary<Item, int> slot = new Dictionary<Item, int>();
            foreach (Item it in items) slot[it] = AddBox(it.Box, "label");
            string Reason(Item it)
            {
                if (!v.Inside(it.Box, o.Margin)) return "Cut by (or too close to) the sheet edge";
                if (it.Region != null && !InArea(it, it.Box)) return "Outside its profile view";
                Box2 g = it.Box.Grow(o.Gap);
                foreach (int i in grid.Query(g)) if (i != slot[it] && all[i].Overlaps(g)) return "Overlaps a " + kinds[i];
                if (!it.Slides) foreach (int i in segGrid.Query(g)) if (it.Box.Hits(segs[i].a, segs[i].b)) return "Covers a " + segKinds[i];
                return "";
            }
            // Labels that are fine where they are (and locked ones) stay, and become obstacles for the rest.
            // Labels measured only by their extents (leader included) cannot be placed reliably: they stay, and are listed.
            foreach (Item it in items)
            {
                bool problem = !it.Fixed && Reason(it).Length > 0;
                if (problem) report.Problems++;
                it.Movable = !it.Fixed && !it.Rough && !it.Info.Locked && (o.RearrangeAll || problem);
                if (!it.Movable)
                {
                    leaders.Add((it.Anchor, it.Box.Nearest(it.Anchor)));
                    if (it.Fixed) continue;
                    report.Kept++;
                    if (it.Rough && !checkOnly)
                        report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = "Could not be measured exactly; left in place" + (problem ? " (" + Reason(it) + ")" : "") });
                }
            }
            foreach (Item it in items) report.Boxes[it.Info.Id] = it.Box;
            if (checkOnly)
            {
                foreach (Item it in items.Where(i => !i.Fixed))
                {
                    string why = Reason(it);
                    if (why.Length > 0) report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = why });
                }
                foreach (Item it in items.Where(i => !i.Fixed)) handled.Add(it.Info.Id);
                return report;
            }
            foreach (Item it in items.Where(i => i.Movable)) all[slot[it]] = new Box2(1e18, 1e18, 1e18, 1e18);

            // Structures first (they anchor the rows), then pipes, each in order along the sheet so leaders run side by side.
            List<Item> order = items.Where(i => i.Movable)
                .OrderBy(i => i.Info.Kind == LabelKind.PlanStructure || i.Info.Kind == LabelKind.ProfileStructure ? 0 : 1)
                .ThenBy(i => i.Anchor.X).ThenBy(i => i.Anchor.Y).ToList();
            foreach (Item it in order)
            {
                handled.Add(it.Info.Id);
                CLabel label = (CLabel)tr.GetObject(it.Info.Id, OpenMode.ForWrite);
                if (it.Slides)
                {
                    // Along its pipe: try other positions on the pipe and keep the nearest clear one.
                    (Box2 box, bool ok) = Slide(it, label, v, (b, i) => Clear(b, -1, i), InArea);
                    AddBox(box, "label");
                    report.Boxes[it.Info.Id] = box;
                    if (!ok)
                    {
                        report.Unresolved++;
                        report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = "No clear place along its pipe" });
                    }
                    else if (box.Center.GetDistanceTo(it.Box.Center) > 0.2) report.Moved++; else report.Kept++;
                    continue;
                }
                (Box2 placed, bool found) = Place(it, o, (b, i) => Clear(b, -1, i), InArea, leaders);
                AddBox(placed, "label");
                report.Boxes[it.Info.Id] = placed;
                if (!found)
                {
                    report.Unresolved++;
                    report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = "No clear place within the distance limit" });
                }
                // The label point sits on the box side facing the anchor (Civil 3D attaches dragged text there).
                Point2d loc = new Point2d(placed.Center.X >= it.Anchor.X ? placed.MinX : placed.MaxX, placed.Center.Y + it.AttachDy);
                leaders.Add((it.Anchor, loc));
                if (placed.Same(it.Box) || loc.GetDistanceTo(it.Location) < 0.2 && placed.Center.GetDistanceTo(it.Box.Center) < 0.2) { report.Kept++; continue; }
                if (MoveTo(it, label, v, loc, placed)) report.Moved++;
                else
                {
                    report.Unresolved++;
                    report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = "Civil 3D would not move this label" });
                }
            }
            foreach (Item it in items.Where(i => !i.Fixed)) handled.Add(it.Info.Id);
            return report;
        }

        /// <summary>
        /// Sets the label point, then measures the result and nudges it until the label sits where it was planned (the dragged
        /// layout can differ a little from the original one, and a structure label's anchor follows it round the structure).
        /// </summary>
        /// <returns>False when Civil 3D refuses the new position.</returns>
        private static bool MoveTo(Item it, CLabel label, SheetViewport v, Point2d loc, Box2 target)
        {
            double z = label.LabelLocation.Z;
            Point3d start = label.LabelLocation, best = start;
            double bestMiss = double.MaxValue;
            for (int pass = 0; pass < 5; pass++)
            {
                Point3d anchor;
                try { anchor = label.AnchorInfo.Location; } catch (Exception) { anchor = it.Info.Anchor; }
                Point2d ap = v.ToPaper(anchor);
                try { label.LabelLocation = new Point3d(anchor.X + (loc.X - ap.X) / it.Factor, anchor.Y + (loc.Y - ap.Y) / it.Factor, z); }
                catch (Exception) { return false; }
                List<Point3d> pts = AnnotLabels.Measure(label);
                if (pts == null) return true;
                try { anchor = label.AnchorInfo.Location; } catch (Exception) { }
                ap = v.ToPaper(anchor);
                Box2 got = Box2.Empty;
                foreach (Point3d p in pts) got = got.Add(new Point2d(ap.X + (p.X - anchor.X) * it.Factor, ap.Y + (p.Y - anchor.Y) * it.Factor));
                Vector2d miss = new Vector2d((target.MinX + target.MaxX - got.MinX - got.MaxX) / 2, (target.MinY + target.MaxY - got.MinY - got.MaxY) / 2);
                if (miss.Length < bestMiss) { bestMiss = miss.Length; best = label.LabelLocation; }
                if (miss.Length < 0.2) return true;
                Point3d now = label.LabelLocation;
                loc = new Point2d(ap.X + (now.X - anchor.X) * it.Factor, ap.Y + (now.Y - anchor.Y) * it.Factor) + miss;
            }
            try { label.LabelLocation = best; } catch (Exception) { }
            return !label.LabelLocation.IsEqualTo(start, new Tolerance(1e-6, 1e-6));
        }
        /// <summary>For a label sitting on its pipe: positions along the pipe (nearest to where it was first); the label is left at the best.</summary>
        private static (Box2 box, bool ok) Slide(Item it, CLabel label, SheetViewport v, Func<Box2, Item, bool> clear, Func<Item, Box2, bool> inArea)
        {
            double original;
            try { original = label is PipeLabel pl ? pl.Ratio : label is PipeProfileLabel ppl ? ppl.Ratio : 0.5; } catch (Exception) { return (it.Box, false); }
            void SetRatio(double r) { if (label is PipeLabel pl) pl.Ratio = r; else if (label is PipeProfileLabel ppl) ppl.Ratio = r; }
            List<double> ratios = Enumerable.Range(1, 17).Select(i => i * 0.05).Where(r => Math.Abs(r - original) > 0.01).OrderBy(r => Math.Abs(r - original)).ToList();
            foreach (double r in ratios)
            {
                try { SetRatio(r); } catch (Exception) { break; }
                List<Point3d> pts = AnnotLabels.Measure(label);
                if (pts == null) continue;
                Point3d anchor;
                try { anchor = label.AnchorInfo.Location; } catch (Exception) { continue; }
                Point2d ap = v.ToPaper(anchor);
                Box2 b = Box2.Empty;
                foreach (Point3d p in pts) b = b.Add(new Point2d(ap.X + (p.X - anchor.X) * it.Factor, ap.Y + (p.Y - anchor.Y) * it.Factor));
                if (inArea(it, b) && clear(b, it)) return (b, true);
            }
            try { SetRatio(original); } catch (Exception) { }
            return (it.Box, false);
        }

        /// <summary>The best place for a dragged label: rows beside the route first, then rings around its anchor; the least-bad one if none is clear.</summary>
        private static (Box2 box, bool ok) Place(Item it, ArrangeOptions o, Func<Box2, Item, bool> clear, Func<Item, Box2, bool> inArea, List<(Point2d a, Point2d b)> leaders)
        {
            Point2d a = it.Anchor;
            double w = it.Box.Width, h = it.Box.Height;
            Vector2d u = it.Along.Length > 1e-9 ? it.Along : new Vector2d(1, 0);
            if (u.X < 0) u = u.Negate();
            Vector2d n = new Vector2d(-u.Y, u.X);
            bool profileStructure = it.Info.Kind == LabelKind.ProfileStructure;
            double side = (it.Box.Center - a).DotProduct(n) >= 0 ? 1 : -1;
            if (profileStructure || it.Info.Kind == LabelKind.PlanStructure && it.Info.IsProfile) { u = new Vector2d(1, 0); n = new Vector2d(0, 1); side = 1; }
            double halfN = (Math.Abs(n.X) * w + Math.Abs(n.Y) * h) / 2, halfU = (Math.Abs(u.X) * w + Math.Abs(u.Y) * h) / 2;

            List<(Point2d center, double cost)> candidates = new List<(Point2d, double)> { (it.Box.Center, -0.3) };
            // Rows: lanes parallel to the pipe, the first just clear of it, sliding along it.
            double laneStep = 2 * halfN + o.Gap * 2;
            double slideStep = Math.Max(2.5, halfU / 2);
            foreach (double s in profileStructure ? new[] { 1.0 } : new[] { side, -side })
                for (int k = 0; k < 6; k++)
                {
                    double off = halfN + 3 + k * laneStep;
                    if (off - halfN > o.MaxDistance) break;
                    for (int j = 0; j * slideStep <= o.MaxDistance; j++)
                        foreach (int sign in j == 0 ? new[] { 1 } : new[] { 1, -1 })
                        {
                            double t = sign * j * slideStep;
                            candidates.Add((a + n * (s * off) + u * t, 0.25 * k + 0.6 * Math.Abs(t) / o.MaxDistance + (s == side ? 0 : 0.2)));
                        }
                }
            // Nearest free spot: rings around the anchor.
            for (double r = 4; r <= o.MaxDistance + Math.Max(w, h) / 2; r += 4)
                for (int k = 0; k < 24; k++)
                {
                    double ang = k * Math.PI / 12;
                    candidates.Add((a + new Vector2d(Math.Cos(ang), Math.Sin(ang)) * r, 0.8 + r / o.MaxDistance));
                }

            Box2 best = it.Box, fallback = it.Box;
            double bestCost = double.MaxValue, fallbackCost = double.MaxValue;
            foreach ((Point2d c, double baseCost) in candidates)
            {
                Box2 b = new Box2(c.X - w / 2, c.Y - h / 2, c.X + w / 2, c.Y + h / 2);
                // Civil 3D puts dragged text wholly on the side of its anchor that the label point is on.
                if (b.Contains(a) || b.MinX < a.X + 1 && b.MaxX > a.X - 1 && !b.Same(it.Box)) continue;
                Point2d end = new Point2d(b.Center.X >= a.X ? b.MinX : b.MaxX, b.Center.Y + it.AttachDy);
                double reach = a.GetDistanceTo(end);
                if (reach > o.MaxDistance) continue;
                int crossings = leaders.Count(l => Geometry2d.SegmentsCross(a, end, l.a, l.b));
                double cost = baseCost + reach / o.MaxDistance + 3 * crossings;
                if (!inArea(it, b)) continue;
                if (clear(b, it)) { if (cost < bestCost) { bestCost = cost; best = b; } }
                else if (cost + 10 < fallbackCost) { fallbackCost = cost + 10; fallback = b; }
            }
            return bestCost < double.MaxValue ? (best, true) : (fallbackCost < double.MaxValue ? fallback : it.Box, false);
        }
    }
}
