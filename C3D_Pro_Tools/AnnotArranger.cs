using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
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
        /// <summary>Longest leader a dragged label gets, on paper (mm).</summary>
        public double MaxDistance = 60;
        /// <summary>Clear space kept around every label (mm).</summary>
        public double Gap = 1.5;
        /// <summary>Clear space kept inside the viewport edge (mm).</summary>
        public double Margin = 3;
        /// <summary>True: every unlocked label is placed again by its style's rule. False: only labels with a problem move.</summary>
        public bool RearrangeAll = true;
        /// <summary>A Normal label that clashes first slides along its pipe (staying normal) before it is dragged.</summary>
        public bool SlideOnPipe = true;
        /// <summary>The labels that may move (null = all); the others stay where they are and only count as obstacles.</summary>
        public HashSet<ObjectId> Only;
        /// <summary>Text and multileaders in model space count as obstacles (they are never covered).</summary>
        public bool AvoidModelText = true;
        /// <summary>Label styles that are always dragged (placed with a leader); every other style is Normal (dragged only when it clashes).</summary>
        public HashSet<string> DraggedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Label styles ("Kind:Style") whose labels may slide along their pipe (a name label); others stay at their point (an
        /// N / E / IL label describes its pipe end, so its arrow must stay there).
        /// </summary>
        public HashSet<string> SlideStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>When set, each viewport's arrangement is also written there as a case file (for replaying it in the tests).</summary>
        public string ExportFolder;
        /// <summary>Networks (by name) no label may cover, besides each label's own network.</summary>
        public HashSet<string> KeepClearNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>One label as checked or placed, on its sheet (paper): for the preview drawing.</summary>
    internal sealed class ArrangeShape
    {
        public ObjectId SheetBlock;
        public List<(double X, double Y)> Outline = new List<(double X, double Y)>();
        public (double X, double Y) Anchor;
        /// <summary>The leader end of a dragged label (null for a normal one).</summary>
        public (double X, double Y)? End;
        /// <summary>0 = normal label, clear; 1 = dragged label, clear; 2 = has a problem / no clear place; 3 = removed (parallel run).</summary>
        public int State;
    }

    /// <summary>What a set of sheets shows: the label styles used there and the networks seen in their viewports.</summary>
    internal sealed class SheetScan
    {
        /// <summary>Each style: what it labels, how many labels, how many dragged now, and how many sit along their pipe (not at an end).</summary>
        public readonly List<(LabelKind kind, string style, int labels, int dragged, int alongPipe)> Styles = new List<(LabelKind, string, int, int, int)>();
        public readonly List<(string network, int parts, int labels)> Networks = new List<(string, int, int)>();

        /// <summary>
        /// Scans the sheets' viewports (within their outlines): the pipe and structure labels shown, by style and element, and
        /// every network with parts shown, with how many of its parts and labels each shows.
        /// </summary>
        public static SheetScan Of(Transaction tr, CivilDocument doc, List<LabelInfo> all, List<SheetInfo> sheets, bool plan, bool profile)
        {
            SheetScan scan = new SheetScan();
            List<SheetViewport> views = sheets.SelectMany(s => s.Viewports).ToList();
            bool Shows(Point3d p) => views.Any(v => v.Sees(p));
            List<LabelInfo> shown = AnnotLabels.OnSheets(all, sheets, plan, profile);
            // A pipe label well inside its pipe (not at an end, where an N / E / IL label points) may slide along it.
            bool AlongPipe(LabelInfo l)
            {
                if (!l.IsPipe) return false;
                try
                {
                    DBObject o = tr.GetObject(l.Id, OpenMode.ForRead);
                    double r = o is PipeLabel pl ? pl.Ratio : o is PipeProfileLabel ppl ? ppl.Ratio : 0;
                    return r > 0.1 && r < 0.9;
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return false; }
            }
            foreach (var g in shown.GroupBy(l => (l.Kind, l.Style ?? "")).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Item2, StringComparer.OrdinalIgnoreCase))
                scan.Styles.Add((g.Key.Kind, g.Key.Item2, g.Count(), g.Count(l => l.Dragged), g.Count(AlongPipe)));

            Dictionary<ObjectId, int> labelsOf = new Dictionary<ObjectId, int>();
            foreach (LabelInfo l in shown)
            {
                ObjectId net = ObjectId.Null;
                try { if (!l.PartId.IsNull && tr.GetObject(l.PartId, OpenMode.ForRead) is Part p) net = p.NetworkId; }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (!net.IsNull) labelsOf[net] = labelsOf.TryGetValue(net, out int c) ? c + 1 : 1;
            }
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                int parts = 0;
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Pipe p && (Shows(p.StartPoint) || Shows(p.EndPoint) || Shows(new Point3d((p.StartPoint.X + p.EndPoint.X) / 2, (p.StartPoint.Y + p.EndPoint.Y) / 2, 0)))) parts++;
                foreach (ObjectId id in net.GetStructureIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Structure st && Shows(st.Location)) parts++;
                labelsOf.TryGetValue(netId, out int labels);
                if (parts > 0 || labels > 0) scan.Networks.Add((net.Name, parts, labels));
            }
            foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                int parts = 0;
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is PressurePipe p && (Shows(p.StartPoint) || Shows(p.EndPoint))) parts++;
                if (parts > 0) scan.Networks.Add((net.Name, parts, 0));
            }
            scan.Networks.Sort((a, b) => string.Compare(a.network, b.network, StringComparison.OrdinalIgnoreCase));
            return scan;
        }
    }

    /// <summary>Networks of cables laid side by side: only one pipe of each parallel run keeps its labels.</summary>
    internal static class RunLabels
    {
        /// <summary>
        /// The plan pipe labels (among <paramref name="labels"/>) on pipes of these networks that do not carry their parallel run's
        /// labels: the middle cable of three, the one right of the middle of two or four (see <see cref="PlanGeometry.LabelledOfRun"/>).
        /// </summary>
        /// <param name="maxGap">Cables at most this far apart (centre to centre, drawing units) run together.</param>
        public static List<LabelInfo> ToRemove(Transaction tr, CivilDocument doc, IEnumerable<LabelInfo> labels, ICollection<string> networks, double maxGap)
        {
            HashSet<ObjectId> drop = new HashSet<ObjectId>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net) || !networks.Contains(net.Name)) continue;
                List<(ObjectId id, ((double X, double Y) a, (double X, double Y) b) line)> pipes = new List<(ObjectId, ((double, double), (double, double)))>();
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Pipe p) pipes.Add((id, ((p.StartPoint.X, p.StartPoint.Y), (p.EndPoint.X, p.EndPoint.Y))));
                var lines = pipes.Select(x => x.line).ToList();
                for (int i = 0; i < pipes.Count; i++)
                    if (!PlanGeometry.CarriesRunLabels(i, lines, maxGap)) drop.Add(pipes[i].id);
            }
            return labels.Where(l => l.Kind == LabelKind.PlanPipe && drop.Contains(l.PartId)).ToList();
        }
    }

    internal sealed class ArrangeReport
    {
        public int Checked, Moved, Kept, Unresolved, Unmeasured, Problems;
        public readonly List<ArrangeShape> Shapes = new List<ArrangeShape>();
        public readonly List<ArrangeIssue> Issues = new List<ArrangeIssue>();
        /// <summary>Where each label sits on the sheet (paper), as checked or as placed.</summary>
        public readonly Dictionary<ObjectId, Box2> Boxes = new Dictionary<ObjectId, Box2>();
        public void Add(ArrangeReport r)
        {
            foreach (KeyValuePair<ObjectId, Box2> kv in r.Boxes) Boxes[kv.Key] = kv.Value;
            Shapes.AddRange(r.Shapes);
            Checked += r.Checked; Moved += r.Moved; Kept += r.Kept; Unresolved += r.Unresolved; Unmeasured += r.Unmeasured; Problems += r.Problems;
            Issues.AddRange(r.Issues);
        }
    }

    /// <summary>What labels must keep clear of, gathered once from the drawing (model coordinates).</summary>
    internal sealed class AnnotScene
    {
        /// <summary>Network part outlines in plan: pipes as bands of their outside width, structures as their outer walls.</summary>
        public readonly List<(Point3d[] outline, ObjectId part, ObjectId network)> Parts = new List<(Point3d[], ObjectId, ObjectId)>();
        /// <summary>Each network part's network, and each network's name.</summary>
        public readonly Dictionary<ObjectId, ObjectId> NetworkOf = new Dictionary<ObjectId, ObjectId>();
        public readonly Dictionary<ObjectId, string> NetworkNames = new Dictionary<ObjectId, string>();
        /// <summary>Text in model space (not inside blocks or xrefs), and the text and lines of multileaders and leaders.</summary>
        public readonly List<Point3d[]> Texts = new List<Point3d[]>();
        public readonly List<(Point3d a, Point3d b)> LeaderLines = new List<(Point3d, Point3d)>();
        /// <summary>Civil 3D labels that are not pipe or structure labels (alignments, surfaces, points...): their text is never covered.</summary>
        public readonly List<ObjectId> OtherLabels = new List<ObjectId>();
        /// <summary>Per profile view: its whole box, its band area below the graph, and the parts drawn in it.</summary>
        public readonly Dictionary<ObjectId, ProfileArea> Profiles = new Dictionary<ObjectId, ProfileArea>();

        internal sealed class ProfileArea
        {
            public Point3d[] View, Bands;
            public readonly List<(Point3d[] outline, ObjectId part)> Parts = new List<(Point3d[], ObjectId)>();
        }

        /// <summary>A band of half width <paramref name="half"/> along a segment (a pipe drawn with its width).</summary>
        private static Point3d[] Band(Point3d a, Point3d b, double half)
        {
            Vector3d d = new Vector3d(b.X - a.X, b.Y - a.Y, 0);
            if (d.Length < 1e-9) d = Vector3d.XAxis;
            Vector3d n = new Vector3d(-d.Y, d.X, 0).GetNormal() * Math.Max(half, 1e-3);
            return new[] { new Point3d(a.X, a.Y, 0) + n, new Point3d(b.X, b.Y, 0) + n, new Point3d(b.X, b.Y, 0) - n, new Point3d(a.X, a.Y, 0) - n };
        }

        /// <summary>
        /// A structure's outer wall in plan, turned with the structure. (Its extents are an upright box in the model, which shows
        /// turned on a twisted sheet and far larger than the structure; they are used only for a part with no size of its own.)
        /// </summary>
        private static Point3d[] Outline(Structure st)
        {
            // What is drawn in plan, as one outline: right whatever the part's size data says (parts copied without a family
            // often have none).
            Point3d[] drawn = DrawnOutline(st);
            if (drawn != null) return drawn;
            StructureFootprint fp = StructureFootprint.Of(st);
            List<Point3d> pts = new List<Point3d>();
            double hx = fp.HalfX + fp.Wall, hy = fp.HalfY + fp.Wall, c = Math.Cos(fp.Rotation), s = Math.Sin(fp.Rotation);
            if (fp.Round)
                for (int i = 0; i < 24; i++) pts.Add(new Point3d(fp.Center.X + hx * Math.Cos(i * Math.PI / 12), fp.Center.Y + hx * Math.Sin(i * Math.PI / 12), 0));
            else
                foreach ((double x, double y) in new[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) })
                    pts.Add(new Point3d(fp.Center.X + x * c - y * s, fp.Center.Y + x * s + y * c, 0));
            return pts.ToArray();
        }

        /// <summary>The smallest rectangle round a structure's plan lines and curves (broken apart in memory; nothing in the drawing changes), or null.</summary>
        private static Point3d[] DrawnOutline(Structure st)
        {
            List<(double X, double Y)> pts = new List<(double X, double Y)>();
            ExplodedPieces.ForEach(st, 3, o =>
            {
                try
                {
                    switch (o)
                    {
                        case DBText _:
                        case MText _:
                            break;
                        case Line l:
                            pts.Add((l.StartPoint.X, l.StartPoint.Y)); pts.Add((l.EndPoint.X, l.EndPoint.Y));
                            break;
                        case Polyline pl:
                            for (int i = 0; i < pl.NumberOfVertices; i++) { Point3d p = pl.GetPoint3dAt(i); pts.Add((p.X, p.Y)); }
                            break;
                        case Curve c:
                            for (int i = 0; i <= 24; i++) { Point3d p = c.GetPointAtParameter(c.StartParam + (c.EndParam - c.StartParam) * i / 24); pts.Add((p.X, p.Y)); }
                            break;
                        // Anything else (hatches, solids, 3D bodies) is left out: its extents are an upright box in the model,
                        // which would show turned on a twisted sheet.
                    }
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            });
            // The smallest rectangle round the drawn lines: square to the structure, and clean even if a stray point gets in.
            List<(double X, double Y)> rect = LabelLayout.MinAreaRect(pts);
            return rect.Count >= 3 ? rect.Select(p => new Point3d(p.X, p.Y, 0)).ToArray() : null;
        }

        public static AnnotScene Read(Transaction tr, Database db, CivilDocument doc, bool texts, bool profiles)
        {
            AnnotScene s = new AnnotScene();
            List<(Pipe pipe, ObjectIdCollection views)> profilePipes = new List<(Pipe, ObjectIdCollection)>();
            List<(Structure st, ObjectIdCollection views)> profileStructures = new List<(Structure, ObjectIdCollection)>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                s.NetworkNames[netId] = net.Name;
                foreach (ObjectId id in net.GetPipeIds())
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                    s.NetworkOf[id] = netId;
                    ArcInfo? arc = null;
                    if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
                    List<Point3d> path = PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 2.0, 100);
                    double half = CoverRoute.OuterWidthOf(p) / 2.0;
                    for (int i = 1; i < path.Count; i++) s.Parts.Add((Band(path[i - 1], path[i], half), id, netId));
                    if (profiles) { try { ObjectIdCollection v = p.GetProfileViewsDisplayingMe(); if (v.Count > 0) profilePipes.Add((p, v)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } }
                }
                foreach (ObjectId id in net.GetStructureIds())
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Structure st)) continue;
                    s.NetworkOf[id] = netId;
                    try { s.Parts.Add((Outline(st), id, netId)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (profiles) { try { ObjectIdCollection v = st.GetProfileViewsDisplayingMe(); if (v.Count > 0) profileStructures.Add((st, v)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } }
                }
            }
            foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                s.NetworkNames[netId] = net.Name;
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is PressurePipe p)
                    {
                        s.NetworkOf[id] = netId;
                        s.Parts.Add((Band(p.StartPoint, p.EndPoint, p.OuterDiameter / 2.0), id, netId));
                    }
            }

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            List<Point3d[]> textOutlines = new List<Point3d[]>(), frames = new List<Point3d[]>();
            foreach (ObjectId id in ms)
            {
                string dxf = id.ObjectClass.DxfName;
                bool text = dxf == "MTEXT" || dxf == "TEXT", leader = dxf == "MULTILEADER" || dxf == "LEADER", table = dxf == "ACAD_TABLE", frame = dxf == "LWPOLYLINE";
                bool label = !text && !leader && !table && !frame && id.ObjectClass.Name.Contains("Label");
                if (!text && !leader && !label && !table && !frame) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || !e.Visible) continue;
                try
                {
                    if (label)
                    {
                        if (e is CLabel && AnnotLabels.KindOf(e) == null) s.OtherLabels.Add(id);
                        continue;
                    }
                    if (!texts) continue;
                    if (e is MText mt) { Point3d[] c = AnnotLabels.MTextCorners(mt).ToArray(); s.Texts.Add(c); textOutlines.Add(c); }
                    else if (e is DBText dt) { Point3d[] c = AnnotFrame.TextCorners(dt).ToArray(); s.Texts.Add(c); textOutlines.Add(c); }
                    else if (e is Autodesk.AutoCAD.DatabaseServices.Table tb) s.Texts.Add(TableCorners(tb));
                    else if (e is Polyline pl && (pl.Closed || (pl.NumberOfVertices >= 4 && pl.GetPoint3dAt(0).DistanceTo(pl.GetPoint3dAt(pl.NumberOfVertices - 1)) < 1e-6)))
                        frames.Add(Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetPoint3dAt(i)).ToArray());
                    else if (e is Leader ld)
                        for (int i = 1; i < ld.NumVertices; i++) s.LeaderLines.Add((ld.VertexAt(i - 1), ld.VertexAt(i)));
                    else if (e is MLeader ml) s.ReadMLeader(ml);
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            foreach (Point3d[] frameOutline in frames)
                if (FramesText(frameOutline, textOutlines)) s.Texts.Add(frameOutline);

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
                        if (a1 != null && b1 != null && a2 != null && b2 != null) area.Parts.Add((new[] { a1.Value, b1.Value, b2.Value, a2.Value }, p.ObjectId));
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
                            area.Parts.Add((new[] { new Point3d(x1, y1, 0), new Point3d(x2, y1, 0), new Point3d(x2, y2, 0), new Point3d(x1, y2, 0) }, st.ObjectId));
                    }
            }
            return s;
        }

        /// <summary>A multileader's text box and leader lines (its block content, if any, by its extents).</summary>
        private void ReadMLeader(MLeader ml)
        {
            ExplodedPieces.ForEach(ml, 0, o =>
            {
                try
                {
                    switch (o)
                    {
                        case MText mt: Texts.Add(AnnotLabels.MTextCorners(mt).ToArray()); break;
                        case DBText dt: Texts.Add(AnnotFrame.TextCorners(dt).ToArray()); break;
                        // A closed frame round the text is part of the text: no leader may cross it.
                        case Polyline frame when frame.Closed || (frame.NumberOfVertices >= 4 && frame.GetPoint3dAt(0).DistanceTo(frame.GetPoint3dAt(frame.NumberOfVertices - 1)) < 1e-6):
                            Texts.Add(Enumerable.Range(0, frame.NumberOfVertices).Select(i => frame.GetPoint3dAt(i)).ToArray());
                            break;
                        case Line l: LeaderLines.Add((l.StartPoint, l.EndPoint)); break;
                        case Polyline pl: for (int i = 1; i < pl.NumberOfVertices; i++) LeaderLines.Add((pl.GetPoint3dAt(i - 1), pl.GetPoint3dAt(i))); break;
                    }
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            });
        }

        /// <summary>A table's four corners, turned with it (its insertion point is its top left corner).</summary>
        private static Point3d[] TableCorners(Autodesk.AutoCAD.DatabaseServices.Table t)
        {
            Vector3d x = t.Direction.GetNormal(), y = t.Normal.CrossProduct(x).GetNormal();
            Point3d p = t.Position;
            return new[] { p, p + x * t.Width, p + x * t.Width - y * t.Height, p - y * t.Height };
        }

        /// <summary>
        /// True when a closed outline tightly frames a text (a schedule or callout box): a text's middle lies inside it, and the
        /// frame is no more than three times the text's area. A boundary or parcel line with a note inside it is not a frame.
        /// </summary>
        private static bool FramesText(Point3d[] frame, List<Point3d[]> texts)
        {
            List<(double X, double Y)> f = frame.Select(p => (p.X, p.Y)).ToList();
            double area = Math.Abs(PolygonArea(f));
            if (area <= 0) return false;
            LRect fb = LRect.Around(f);
            foreach (Point3d[] t in texts)
            {
                List<(double X, double Y)> tp = t.Select(p => (p.X, p.Y)).ToList();
                LRect tb = LRect.Around(tp);
                if (!fb.Overlaps(tb)) continue;
                double ta = Math.Abs(PolygonArea(LabelLayout.Hull(tp)));
                if (ta <= 0 || area > 3 * ta) continue;
                if (LabelLayout.InPolygon(f, tb.Center)) return true;
            }
            return false;
        }

        private static double PolygonArea(IReadOnlyList<(double X, double Y)> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) a += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
            return a / 2;
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
    /// Places labels so the plotted sheet reads cleanly, worked out in each viewport's own frame (on paper, turned with the
    /// view). Each label style is Normal (stays a plain label; when it clashes it first slides along its pipe, and only then is
    /// dragged) or Dragged (always placed with a leader). Dragged labels of a viewport are planned together: crowded ones first,
    /// leaders square to the viewport where they can be and at any angle where they cannot, lined up with their neighbours,
    /// crossings untangled. A label never covers its own network (or a network chosen to keep clear of), text, labels or
    /// leaders; other networks, blocks and xrefs may be covered. Every move is measured again in the drawing; a label with no
    /// clear place is put back as it was and listed.
    /// </summary>
    internal static class AnnotArranger
    {
        private sealed class Item
        {
            public LabelInfo Info;
            public string Key, PartKey, NetworkKey;
            public (double X, double Y) Anchor;
            /// <summary>The label's outline on this sheet, turned with its text.</summary>
            public List<(double X, double Y)> Shape;
            public (double X, double Y) Location;
            /// <summary>Paper units per drawing unit of the label's own geometry (plotted size over its size in the model).</summary>
            public double Factor;
            public OrientationReferenceType? Orientation;
            /// <summary>The first text's direction on the sheet as read (degrees).</summary>
            public double TextAngle = double.NaN;
            public LRect? Region;
            public bool Fixed, Dragged, WantsDragged;
            /// <summary>Its style lets it slide along its pipe (see <see cref="ArrangeOptions.SlideStyles"/>).</summary>
            public bool MaySlide;
            /// <summary>Dragged, this style reads upright on the sheet (see <see cref="AnnotFrame.Outline"/>).</summary>
            public bool UprightDragged = true;
            public HashSet<string> Networks;
            // The label as it was, to put back when it has no clear place.
            public Point3d OriginalLocation;
            public double? OriginalRatio;
        }

        private static (double X, double Y) P(Point2d p) => (p.X, p.Y);
        private static LRect R(Box2 b) => new LRect(b.MinX, b.MinY, b.MaxX, b.MaxY);
        private static Box2 B(LRect r) => new Box2(r.MinX, r.MinY, r.MaxX, r.MaxY);
        private static string K(ObjectId id) => id.IsNull ? null : id.Handle.ToString();

        /// <summary>Arranges (or with <paramref name="checkOnly"/> only checks) the labels shown in the sheet's viewports.</summary>
        public static ArrangeReport Arrange(Transaction tr, SheetInfo sheet, List<LabelInfo> labels, AnnotScene scene, ArrangeOptions o, HashSet<ObjectId> handled,
            bool checkOnly, double paperPerUnit = 1000)
        {
            ArrangeReport report = new ArrangeReport();
            Dictionary<ObjectId, OrientationReferenceType?> orientations = new Dictionary<ObjectId, OrientationReferenceType?>();
            foreach (SheetViewport v in sheet.Viewports)
                report.Add(ArrangeViewport(tr, sheet, v, labels, scene, o, handled, checkOnly, AnnotSheets.PaperObstacles(tr, sheet, v), orientations, paperPerUnit));
            return report;
        }

        /// <summary>The label as it is now in the drawing, on this sheet (null when it cannot be measured).</summary>
        private static LabelOutline Measure(Transaction tr, CLabel label, Item it, SheetViewport v, double paperPerUnit) =>
            AnnotFrame.Outline(tr, label, v, paperPerUnit, it.Orientation, it.Factor, it.UprightDragged);

        /// <summary>Puts the label point (where the leader ends) at a paper position, in the drawing.</summary>
        private static bool SetLocation(CLabel label, Item it, SheetViewport v, (double X, double Y) paper)
        {
            Point3d anchor;
            try { anchor = label.AnchorInfo.Location; } catch (Exception) { anchor = it.Info.Anchor; }
            // Back through the viewport: a move along the sheet's axes is turned into the model by the view's twist.
            try { label.LabelLocation = v.LabelToModel(anchor, new Point2d(paper.X, paper.Y), it.Factor, label.LabelLocation.Z); return true; }
            catch (Exception) { return false; }
        }

        private static double? Ratio(CLabel label)
        {
            try { return label is PipeLabel pl ? pl.Ratio : label is PipeProfileLabel ppl ? ppl.Ratio : (double?)null; }
            catch (Exception) { return null; }
        }

        private static void SetRatio(CLabel label, double r)
        {
            if (label is PipeLabel pl) pl.Ratio = r; else if (label is PipeProfileLabel ppl) ppl.Ratio = r;
        }

        /// <summary>A label as it was before this run.</summary>
        private static void Restore(CLabel label, Item it)
        {
            try
            {
                // The ratio first (a label may have slid along its pipe), then its place.
                if (it.OriginalRatio.HasValue) SetRatio(label, it.OriginalRatio.Value);
                if (it.Dragged) label.LabelLocation = it.OriginalLocation;
                else if (label.Dragged) label.ResetLocation();
            }
            catch (Exception ex) { C3DProLogger.Warn($"Arrange labels: label {it.Info.Id.Handle} could not be put back", ex); }
        }

        /// <summary>
        /// A dragged label's shape: its text box relative to the label point with the point right and left of the anchor, above and
        /// below it, found by dragging it there and measuring (the dragged layout differs from the normal one).
        /// </summary>
        private static DraggedShape Probe(Transaction tr, CLabel label, Item it, SheetViewport v, double paperPerUnit)
        {
            LRect? Rel(double dx, double dy)
            {
                if (!SetLocation(label, it, v, (it.Anchor.X + dx, it.Anchor.Y + dy))) return null;
                LabelOutline lo = Measure(tr, label, it, v, paperPerUnit);
                if (lo == null || lo.Hull.Count < 2) return null;
                return LRect.Around(lo.Hull).Offset(-lo.Location.X, -lo.Location.Y);
            }
            // Above and below the anchor, on both sides: Civil 3D may join the leader to the text elsewhere below it.
            LRect? upRight = Rel(25, 15), upLeft = Rel(-25, 15), downRight = Rel(25, -15), downLeft = Rel(-25, -15);
            if (upRight == null || upLeft == null) return null;
            return new DraggedShape { Right = upRight.Value, Left = upLeft.Value, UpRight = upRight, UpLeft = upLeft, DownRight = downRight, DownLeft = downLeft };
        }

        private static ArrangeReport ArrangeViewport(Transaction tr, SheetInfo sheet, SheetViewport v, List<LabelInfo> labels, AnnotScene scene, ArrangeOptions o,
            HashSet<ObjectId> handled, bool checkOnly, List<Box2> paper, Dictionary<ObjectId, OrientationReferenceType?> orientations, double paperPerUnit)
        {
            ArrangeReport report = new ArrangeReport();
            LabelObstacles obstacles = new LabelObstacles();
            double near = PlanGeometry.PaperToModel(o.NearMarginMm, v.Scale);
            bool Near(Point3d p) => p.X >= v.ModelBox.MinPoint.X - near && p.X <= v.ModelBox.MaxPoint.X + near && p.Y >= v.ModelBox.MinPoint.Y - near && p.Y <= v.ModelBox.MaxPoint.Y + near;
            bool NearAny(IEnumerable<Point3d> pts)
            {
                double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
                foreach (Point3d p in pts) { a = Math.Min(a, p.X); b = Math.Min(b, p.Y); c = Math.Max(c, p.X); d = Math.Max(d, p.Y); }
                return c >= v.ModelBox.MinPoint.X - near && a <= v.ModelBox.MaxPoint.X + near && d >= v.ModelBox.MinPoint.Y - near && b <= v.ModelBox.MaxPoint.Y + near;
            }
            List<(double X, double Y)> ToPaper(IEnumerable<Point3d> pts) => pts.Select(p => P(v.ToPaper(p))).ToList();
            OrientationReferenceType? OrientationOf(ObjectId style)
            {
                if (!orientations.TryGetValue(style, out OrientationReferenceType? or)) orientations[style] = or = AnnotFrame.OrientationOf(tr, style);
                return or;
            }

            // Networks to keep clear of besides each label's own.
            HashSet<string> extra = new HashSet<string>(scene.NetworkNames.Where(kv => o.KeepClearNetworks.Contains(kv.Value)).Select(kv => K(kv.Key)));

            // ---- Obstacles on this sheet, as exact outlines ----
            foreach (Box2 b in paper) obstacles.AddRect(R(b), ObstacleGroup.Text, "sheet text or leader");
            foreach ((Point3d[] outline, ObjectId part, ObjectId net) in scene.Parts)
                if (NearAny(outline))
                    obstacles.AddPolygon(ToPaper(outline), ObstacleGroup.Network, scene.NetworkNames.TryGetValue(net, out string nn) ? $"part of {nn}" : "network part", K(net), K(part));
            foreach (Point3d[] c in scene.Texts)
                if (c.Length > 0 && NearAny(c)) obstacles.AddPolygon(ToPaper(c), ObstacleGroup.Text, "text");
            foreach ((Point3d a, Point3d b) in scene.LeaderLines)
                if (Near(a) || Near(b)) obstacles.AddSegment(P(v.ToPaper(a)), P(v.ToPaper(b)), ObstacleGroup.Leader, "leader");
            foreach (ObjectId id in scene.OtherLabels)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is CLabel other)) continue;
                try { if (!NearAny(AnnotScene.Corners(other.GeometricExtents))) continue; } catch (Exception) { continue; }
                LabelOutline lo = AnnotFrame.Outline(tr, other, v, paperPerUnit, OrientationOf(other.StyleId));
                if (lo == null) continue;
                obstacles.AddPolygon(lo.Hull, ObstacleGroup.Text, "label");
                if (lo.Dragged) obstacles.AddSegment(lo.Anchor, lo.Location, ObstacleGroup.Leader, "leader");
            }
            Dictionary<ObjectId, LRect> profileRegions = new Dictionary<ObjectId, LRect>();
            LRect vpRect = new LRect(v.PaperBox.MinPoint.X, v.PaperBox.MinPoint.Y, v.PaperBox.MaxPoint.X, v.PaperBox.MaxPoint.Y);
            foreach (KeyValuePair<ObjectId, AnnotScene.ProfileArea> kv in scene.Profiles)
            {
                if (kv.Value == null || !NearAny(kv.Value.View)) continue;
                LRect region = LRect.Around(ToPaper(kv.Value.View));
                if (!region.Overlaps(vpRect)) continue;
                profileRegions[kv.Key] = region;
                if (kv.Value.Bands != null) obstacles.AddRect(LRect.Around(ToPaper(kv.Value.Bands)), ObstacleGroup.Text, "profile band");
                foreach ((Point3d[] outline, ObjectId part) in kv.Value.Parts)
                    obstacles.AddPolygon(ToPaper(outline), ObstacleGroup.Network, "profile part", scene.NetworkOf.TryGetValue(part, out ObjectId net) ? K(net) : null, K(part));
            }

            // ---- The pipe and structure labels shown here ----
            List<Item> items = new List<Item>();
            foreach (LabelInfo l in labels)
            {
                if ((l.IsProfile ? !o.Profile : !o.Plan) || !(v.Sees(l.Anchor) || v.Sees(l.Location))) continue;
                if (!(tr.GetObject(l.Id, OpenMode.ForRead) is CLabel e)) continue;
                bool chosen = (o.Only == null || o.Only.Contains(l.Id)) && !handled.Contains(l.Id);
                OrientationReferenceType? orient = OrientationOf(e.StyleId);
                LabelOutline lo = AnnotFrame.Outline(tr, e, v, paperPerUnit, orient);
                if (lo == null || lo.Hull.Count < 3)
                {
                    if (chosen)
                    {
                        report.Unmeasured++;
                        report.Issues.Add(new ArrangeIssue { LabelId = l.Id, Sheet = sheet.Layout, Where = l.Anchor, Text = "Could not be measured; left in place" });
                    }
                    continue;
                }
                ObjectId net = scene.NetworkOf.TryGetValue(l.PartId, out ObjectId n) ? n : ObjectId.Null;
                Item it = new Item
                {
                    Info = l, Key = "label " + K(l.Id), PartKey = K(l.PartId), NetworkKey = K(net), Anchor = lo.Anchor, Location = lo.Location, Shape = lo.Hull,
                    Factor = lo.Factor, Orientation = orient, TextAngle = lo.TextAngleDeg, Fixed = !chosen || l.Locked, Dragged = lo.Dragged,
                    WantsDragged = o.DraggedStyles.Contains(AnnotLabelRules.StyleKey(l.Kind, l.Style ?? "")), OriginalLocation = l.Location, OriginalRatio = Ratio(e),
                    MaySlide = l.IsPipe && o.SlideStyles.Contains(AnnotLabelRules.StyleKey(l.Kind, l.Style ?? ""))
                };
                it.Networks = new HashSet<string>(extra);
                if (it.NetworkKey != null) it.Networks.Add(it.NetworkKey);
                if (l.IsProfile && profileRegions.TryGetValue(l.ViewId, out LRect r)) it.Region = r;
                items.Add(it);
            }
            report.Checked = items.Count(i => !i.Fixed);
            if (report.Checked == 0) return report;

            // How each style's dragged labels read on this sheet, from those already dragged (upright when none are).
            Dictionary<string, List<double>> dragAngles = new Dictionary<string, List<double>>();
            foreach (Item it in items.Where(i => i.Dragged && !double.IsNaN(i.TextAngle)))
            {
                string key = AnnotLabelRules.StyleKey(it.Info.Kind, it.Info.Style ?? "");
                if (!dragAngles.TryGetValue(key, out List<double> l)) dragAngles[key] = l = new List<double>();
                l.Add(Math.Abs(LabelLayout.OffUpright(it.TextAngle * Math.PI / 180)) * 180 / Math.PI);
            }
            foreach (Item it in items)
                it.UprightDragged = !dragAngles.TryGetValue(AnnotLabelRules.StyleKey(it.Info.Kind, it.Info.Style ?? ""), out List<double> seen) ||
                                    seen.Count(a => a < 2) * 2 >= seen.Count;

            bool InArea(Item it, IEnumerable<(double X, double Y)> shape) =>
                shape.All(p => v.Inside(new Point2d(p.X, p.Y), o.Margin) && (it.Region == null || it.Region.Value.Contains(p)));
            // Every labelled point is kept clear of other labels' text.
            foreach (Item it in items) LabelLayout.ReserveAnchor(obstacles, it.Anchor, it.Key);
            void ClaimAsIs(Item it, LabelObstacles into)
            {
                into.AddPolygon(it.Shape, ObstacleGroup.Text, "label", part: it.Key);
                if (it.Dragged) into.AddSegment(it.Anchor, it.Location, ObstacleGroup.Leader, "leader", part: it.Key);
            }
            // A normal label sits on its own part on purpose; a dragged one keeps clear of it too.
            string ProblemIn(LabelObstacles set, Item it, IReadOnlyList<(double X, double Y)> shape, bool dragged)
            {
                if (!shape.All(p => v.Inside(new Point2d(p.X, p.Y), o.Margin))) return "Cut by (or too close to) the viewport edge";
                if (it.Region != null && !shape.All(p => it.Region.Value.Contains(p))) return "Outside its profile view";
                string what = set.Blocker(shape, o.Gap, it.Networks, it.Key, dragged ? null : it.PartKey);
                return what == null ? "" : "Covers a " + what;
            }
            void Record(Item it, List<(double X, double Y)> shape, (double X, double Y) anchor, (double X, double Y)? end, int state)
            {
                report.Boxes[it.Info.Id] = B(LRect.Around(shape));
                report.Shapes.Add(new ArrangeShape { SheetBlock = sheet.BlockId, Outline = shape, Anchor = anchor, End = end, State = state });
            }

            if (checkOnly)
            {
                foreach (Item it in items) ClaimAsIs(it, obstacles);
                foreach (Item it in items.Where(i => !i.Fixed))
                {
                    string why = ProblemIn(obstacles, it, it.Shape, it.Dragged);
                    Record(it, it.Shape, it.Anchor, it.Dragged ? it.Location : ((double, double)?)null, why.Length > 0 ? 2 : it.Dragged ? 1 : 0);
                    if (why.Length > 0)
                    {
                        report.Problems++;
                        report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = why });
                    }
                    handled.Add(it.Info.Id);
                }
                return report;
            }

            // Labels that stay: not chosen, locked, or (when not rearranging all) fine where they are and in the state their style
            // asks for. "Fine" is judged against every label as it is now.
            LabelObstacles asIs = null;
            if (!o.RearrangeAll)
            {
                asIs = obstacles.Clone();
                foreach (Item it in items) ClaimAsIs(it, asIs);
            }
            List<Item> movable = new List<Item>();
            foreach (Item it in items)
            {
                bool stays = it.Fixed || (asIs != null && it.Dragged == it.WantsDragged && ProblemIn(asIs, it, it.Shape, it.Dragged).Length == 0);
                if (!stays) { movable.Add(it); continue; }
                ClaimAsIs(it, obstacles);
                if (it.Fixed) continue;
                report.Kept++;
                Record(it, it.Shape, it.Anchor, it.Dragged ? it.Location : ((double, double)?)null, it.Dragged ? 1 : 0);
                handled.Add(it.Info.Id);
            }

            void Unresolved(Item it, string why)
            {
                report.Unresolved++;
                Record(it, it.Shape, it.Anchor, it.Dragged ? it.Location : ((double, double)?)null, 2);
                report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = why });
            }

            // ---- 1. Normal labels: back to normal; if they clash, along their pipe; else they are dragged below ----
            List<Item> toDrag = new List<Item>();
            foreach (Item it in movable.OrderBy(i => i.Anchor.X).ThenBy(i => i.Anchor.Y))
            {
                handled.Add(it.Info.Id);
                if (it.WantsDragged) { toDrag.Add(it); continue; }
                CLabel label = (CLabel)tr.GetObject(it.Info.Id, OpenMode.ForWrite);
                try { if (label.Dragged) label.ResetLocation(); } catch (Exception ex) { C3DProLogger.Warn("Arrange labels: a label could not be reset to its normal place", ex); }
                LabelOutline lo = Measure(tr, label, it, v, paperPerUnit);
                bool clear = lo != null && ProblemIn(obstacles, it, lo.Hull, false).Length == 0;
                if (!clear && lo != null && o.SlideOnPipe && it.MaySlide)
                {
                    LabelOutline slid = Slide(tr, label, it, v, paperPerUnit, h => ProblemIn(obstacles, it, h, false).Length == 0);
                    if (slid != null) { lo = slid; clear = true; }
                }
                if (!clear) { toDrag.Add(it); continue; }
                obstacles.AddPolygon(lo.Hull, ObstacleGroup.Text, "label", part: it.Key);
                bool moved = it.Dragged || Math.Abs(LRect.Around(lo.Hull).MinX - LRect.Around(it.Shape).MinX) > 0.2 || Math.Abs(LRect.Around(lo.Hull).MinY - LRect.Around(it.Shape).MinY) > 0.2;
                if (moved) report.Moved++; else report.Kept++;
                Record(it, lo.Hull, lo.Anchor, null, 0);
            }

            // ---- 2. Dragged labels: planned together, then moved and checked one by one ----
            Dictionary<Item, DraggedShape> shapes = new Dictionary<Item, DraggedShape>();
            foreach (Item it in toDrag)
            {
                CLabel label = (CLabel)tr.GetObject(it.Info.Id, OpenMode.ForWrite);
                DraggedShape shape = Probe(tr, label, it, v, paperPerUnit);
                if (shape == null) { Restore(label, it); ClaimAsIs(it, obstacles); Unresolved(it, "Civil 3D would not drag this label"); continue; }
                shapes[it] = shape;
            }
            LayoutRules rules = new LayoutRules { MaxDistance = o.MaxDistance, Gap = o.Gap, MinLeader = 3, Step = 2 };
            List<(Item it, PlanLabel plan)> plans = shapes.Keys.Select(it => (it, new PlanLabel
            {
                Key = it.Key, Anchor = it.Anchor, Shape = shapes[it], Networks = it.Networks, InArea = b => InArea(it, b.Corners()),
                Ratio = it.OriginalRatio, AltAnchors = it.MaySlide ? SlidePoints((CLabel)tr.GetObject(it.Info.Id, OpenMode.ForWrite), it, v, o.Margin + 2) : new List<(double X, double Y, double Ratio)>()
            })).ToList();
            if (!string.IsNullOrEmpty(o.ExportFolder)) Export(o, sheet, v, obstacles, plans.Select(p => (p.plan, p.it.Region)).ToList());
            LabelPlanner.Plan(obstacles, plans.Select(p => p.plan).ToList(), rules);

            // The plan as obstacles; each label's planned place is swapped for where Civil 3D actually puts it.
            LabelObstacles live = obstacles.Clone();
            foreach ((Item it, PlanLabel plan) in plans)
                if (plan.Spot.HasValue)
                {
                    if (plan.Spot.Value.Ratio.HasValue) LabelLayout.ReserveAnchor(live, plan.Spot.Value.Anchor, it.Key);
                    LabelLayout.Claim(live, plan.Spot.Value.Box, plan.Start, plan.Spot.Value.End, it.Key);
                }
            foreach ((Item it, PlanLabel plan) in plans)
            {
                CLabel label = (CLabel)tr.GetObject(it.Info.Id, OpenMode.ForWrite);
                LabelObstacles others = live.Without(new[] { it.Key });
                List<LabelSpot> tried = new List<LabelSpot>();
                LabelSpot? spot = plan.Spot;
                bool done = false;
                // Second best: a tried place clear of every label, text, part and the viewport edge that only covers another
                // label's leader line. Better than putting the label back where it overlaps labels.
                LabelObstacles noLeaders = others.WithoutLeaders();
                (LabelSpot spot, (double X, double Y) end)? fallback = null;
                for (int attempt = 0; attempt < 5 && !done; attempt++)
                {
                    if (attempt > 0 || !spot.HasValue)
                        spot = LabelPlanner.ChooseFor(others, plan, rules, null, tried);
                    if (!spot.HasValue) break;
                    tried.Add(spot.Value);
                    // Slid along its pipe: the ratio first, so its anchor is where the leader starts.
                    if (spot.Value.Ratio.HasValue) try { SetRatio(label, spot.Value.Ratio.Value); } catch (Exception) { continue; }
                    (double X, double Y) start = spot.Value.Ratio.HasValue ? spot.Value.Anchor : it.Anchor;
                    if (!SetLocation(label, it, v, spot.Value.End)) break;
                    // Where Civil 3D actually put it: it must be clear there too. Off by a little: moved once by the difference.
                    LabelOutline got = Measure(tr, label, it, v, paperPerUnit);
                    if (got == null) break;
                    bool Bad(LabelOutline g) => ProblemIn(others, it, g.Hull, true).Length > 0 || others.LeaderCrossesText(start, g.Location, it.Key);
                    bool OnlyLeaders(LabelOutline g) => ProblemIn(noLeaders, it, g.Hull, true).Length == 0 && !noLeaders.LeaderCrossesText(start, g.Location, it.Key);
                    if (Bad(got))
                    {
                        if (fallback == null && OnlyLeaders(got)) fallback = (spot.Value, spot.Value.End);
                        LRect actual = LRect.Around(got.Hull);
                        double errX = spot.Value.Box.Center.X - actual.Center.X, errY = spot.Value.Box.Center.Y - actual.Center.Y;
                        if (Math.Abs(errX) <= 0.2 && Math.Abs(errY) <= 0.2) continue;
                        (double X, double Y) corrected = (spot.Value.End.X + errX, spot.Value.End.Y + errY);
                        if (!SetLocation(label, it, v, corrected)) continue;
                        got = Measure(tr, label, it, v, paperPerUnit);
                        if (got != null && Bad(got) && fallback == null && OnlyLeaders(got)) fallback = (spot.Value, corrected);
                        if (got == null || Bad(got)) continue;
                    }
                    live = others;
                    if (spot.Value.Ratio.HasValue) LabelLayout.ReserveAnchor(live, start, it.Key);
                    live.AddPolygon(got.Hull, ObstacleGroup.Text, "label", part: it.Key);
                    live.AddSegment(start, got.Location, ObstacleGroup.Leader, "leader", part: it.Key);
                    live.Placed(LRect.Around(got.Hull), it.Key);
                    report.Moved++;
                    Record(it, got.Hull, start, got.Location, 1);
                    done = true;
                }
                if (!done && fallback.HasValue)
                {
                    LabelSpot fs = fallback.Value.spot;
                    bool ratioOk = true;
                    if (fs.Ratio.HasValue) try { SetRatio(label, fs.Ratio.Value); } catch (Exception) { ratioOk = false; }
                    (double X, double Y) start = fs.Ratio.HasValue ? fs.Anchor : it.Anchor;
                    LabelOutline got = ratioOk && SetLocation(label, it, v, fallback.Value.end) ? Measure(tr, label, it, v, paperPerUnit) : null;
                    if (got != null && ProblemIn(noLeaders, it, got.Hull, true).Length == 0 && !noLeaders.LeaderCrossesText(start, got.Location, it.Key))
                    {
                        live = others;
                        if (fs.Ratio.HasValue) LabelLayout.ReserveAnchor(live, start, it.Key);
                        live.AddPolygon(got.Hull, ObstacleGroup.Text, "label", part: it.Key);
                        live.AddSegment(start, got.Location, ObstacleGroup.Leader, "leader", part: it.Key);
                        live.Placed(LRect.Around(got.Hull), it.Key);
                        report.Moved++;
                        Record(it, got.Hull, start, got.Location, 1);
                        report.Issues.Add(new ArrangeIssue { LabelId = it.Info.Id, Sheet = sheet.Layout, Where = it.Info.Anchor, Text = "Placed clear of labels and text, but over another label's leader" });
                        done = true;
                    }
                }
                if (!done)
                {
                    Restore(label, it);
                    live = others;
                    ClaimAsIs(it, live);
                    Unresolved(it, "No clear place within the longest leader; put back as it was");
                }
            }
            return report;
        }

        /// <summary>
        /// The points along its pipe a label may slide its anchor to (ratios 0.5, 0.35, 0.65, 0.22, 0.78), as this sheet shows them:
        /// read back from Civil 3D with the label set to each ratio (exact on curved pipes and in profile views), those inside the
        /// viewport kept. The label is left at its own ratio.
        /// </summary>
        private static List<(double X, double Y, double Ratio)> SlidePoints(CLabel label, Item it, SheetViewport v, double margin)
        {
            List<(double X, double Y, double Ratio)> points = new List<(double X, double Y, double Ratio)>();
            double? original = Ratio(label);
            if (!original.HasValue) return points;
            foreach (double r in new[] { 0.5, 0.35, 0.65, 0.22, 0.78 })
            {
                if (Math.Abs(r - original.Value) < 0.05) continue;
                try
                {
                    SetRatio(label, r);
                    Point2d p = v.ToPaper(label.AnchorInfo.Location);
                    if (v.Inside(p, margin)) points.Add((p.X, p.Y, r));
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            try { SetRatio(label, original.Value); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return points;
        }

        /// <summary>Writes this viewport's arrangement as a case file (paper mm, in the viewport's frame; geometry only).</summary>
        private static void Export(ArrangeOptions o, SheetInfo sheet, SheetViewport v, LabelObstacles obstacles, List<(PlanLabel plan, LRect? region)> labels)
        {
            try
            {
                ArrangeCase c = new ArrangeCase { Sheet = sheet.Layout, MaxDistance = o.MaxDistance, Gap = o.Gap, Margin = o.Margin };
                c.Viewport = v.Outline.Select(p => new[] { p.X, p.Y }).ToList();
                c.AddObstacles(obstacles);
                foreach ((PlanLabel plan, LRect? region) in labels) c.AddLabel(plan, region);
                string name = string.Concat(sheet.Layout.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                int index = sheet.Viewports.IndexOf(v) + 1;
                System.IO.File.WriteAllText(System.IO.Path.Combine(o.ExportFolder, $"{name}-vp{index}.json"), c.ToJson());
            }
            catch (Exception ex) { C3DProLogger.Warn($"Arrange labels: the case for {sheet.Layout} could not be written", ex); }
        }

        /// <summary>For a normal label on its pipe: other positions along the pipe, nearest first; the label is left at the first clear one.</summary>
        private static LabelOutline Slide(Transaction tr, CLabel label, Item it, SheetViewport v, double paperPerUnit, Func<List<(double X, double Y)>, bool> clear)
        {
            double? original = Ratio(label);
            if (!original.HasValue) return null;
            foreach (double r in Enumerable.Range(1, 17).Select(i => i * 0.05).Where(r => Math.Abs(r - original.Value) > 0.01).OrderBy(r => Math.Abs(r - original.Value)))
            {
                try { SetRatio(label, r); } catch (Exception) { break; }
                LabelOutline lo = Measure(tr, label, it, v, paperPerUnit);
                if (lo != null && clear(lo.Hull)) return lo;
            }
            try { SetRatio(label, original.Value); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return null;
        }
    }
}
