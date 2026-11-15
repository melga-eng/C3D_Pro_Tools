using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    /// <summary>What the source line's elevation is on the pipe: its centre, or a face inside or outside its wall.</summary>
    internal enum LineElevationRef { Center, Invert, InnerTop, OuterTop, OuterBottom }

    /// <summary>One pipe to be: straight, or a plan arc (constant grade) from A to B. Z is at the chosen reference until created.</summary>
    internal sealed class LinePiece
    {
        public Point3d A, B;
        public bool Curved;
        public Point2d Center;
        public bool Clockwise;

        /// <summary>The point a fraction <paramref name="t"/> of the way along the piece in plan (Z interpolated by length).</summary>
        public Point3d At(double t)
        {
            double z = A.Z + (B.Z - A.Z) * t;
            if (!Curved) return new Point3d(A.X + (B.X - A.X) * t, A.Y + (B.Y - A.Y) * t, z);
            Vector2d r = new Point2d(A.X, A.Y) - Center;
            double a = Math.Atan2(r.Y, r.X) + (Clockwise ? -1 : 1) * Sweep * t;
            return new Point3d(Center.X + r.Length * Math.Cos(a), Center.Y + r.Length * Math.Sin(a), z);
        }

        /// <summary>The same arc (or line) from <paramref name="a"/> to <paramref name="b"/>, two points on it in order.</summary>
        public LinePiece Between(Point3d a, Point3d b) => new LinePiece { A = a, B = b, Curved = Curved, Center = Center, Clockwise = Clockwise };

        public double PlanLength => Curved ? (new Point2d(A.X, A.Y) - Center).Length * Sweep : new Point2d(A.X, A.Y).GetDistanceTo(new Point2d(B.X, B.Y));

        /// <summary>The arc's turn in radians (0 for a straight piece).</summary>
        public double Sweep
        {
            get
            {
                if (!Curved) return 0;
                Vector2d a = new Point2d(A.X, A.Y) - Center, b = new Point2d(B.X, B.Y) - Center;
                double sweep = Math.Atan2(a.X * b.Y - a.Y * b.X, a.DotProduct(b));
                if (Clockwise) sweep = -sweep;
                if (sweep < 0) sweep += 2 * Math.PI;
                return sweep;
            }
        }

        public double Length3d => Math.Sqrt(PlanLength * PlanLength + (B.Z - A.Z) * (B.Z - A.Z));
        /// <summary>Grade in %, + = rising from A to B.</summary>
        public double Slope => PlanLength < 1e-9 ? 0 : (B.Z - A.Z) / PlanLength * 100.0;

        public LinePiece Reversed() => new LinePiece { A = B, B = A, Curved = Curved, Center = Center, Clockwise = !Clockwise };
    }

    internal sealed class LineNetworkOptions
    {
        /// <summary>True: elevations are the surface's, less <see cref="Depth"/>; false: the line's own Z.</summary>
        public bool FromSurface;
        public ObjectId SurfaceId = ObjectId.Null;
        public double Depth = 1.0;
        public LineElevationRef Reference = LineElevationRef.Center;
        /// <summary>Straight spans that run on in a straight line (in 3D, within the tolerance) become one pipe.</summary>
        public bool MergeStraight = true;
        public double MergeTolerance = 0.005;
        /// <summary>Straight spans shorter than this join their straight neighbour.</summary>
        public double MinLength = 0.05;
        /// <summary>Straight pipes longer than this are split into equal parts (0 = no limit).</summary>
        public double MaxLength;
        /// <summary>
        /// Pipes run downhill, instead of the way the line was drawn: the line's overall fall decides (its grade over the whole
        /// length, each stretch weighted by its length), not only its two ends.
        /// </summary>
        public bool Downhill;
        /// <summary>
        /// An arc whose elevation points (or, with levels from a surface, whose ground) are more than this off one constant grade
        /// is split into curved pipes at those points, so no elevation control is lost (m).
        /// </summary>
        public double ArcGradeTolerance = 0.005;
        /// <summary>A line end lying on a pipe of the network away from its ends (no structure there): reported, or that pipe is split there.</summary>
        public bool SplitPipeMetMidSpan;
        /// <summary>Structure at every joint between two pipes of a line / at both ends of a line; null = none.</summary>
        public PartPick Joint, Ends;
        /// <summary>An end lying inside a structure already in the target network is connected to it.</summary>
        public bool ConnectExisting = true;
        public bool EraseSource;
    }

    /// <summary>One source line in the tool's list: what it is, where it goes, and its planned pipes.</summary>
    internal sealed class LineRow
    {
        public ObjectId SourceId;
        public string SourceName = "", Kind = "";
        public bool Use = true;
        public string Network = "";
        public bool NetworkEdited;
        /// <summary>This line's own pipe part; null = the tool's default part.</summary>
        public PartPick Part;
        public List<LinePiece> Pieces = new List<LinePiece>();
        public string Error;
        public readonly List<string> Notes = new List<string>();
        public double Length => Pieces.Sum(p => p.PlanLength);
    }

    internal sealed class LineNetworkResult
    {
        public int Networks, Lines, Pipes, Structures, Connected, Failed, Erased;
        /// <summary>Pipes of the network split where a line ended on them mid-span.</summary>
        public int Split;
        /// <summary>Arcs Civil 3D would not take as one curved pipe, drawn as straight pipes along them instead.</summary>
        public int Chorded;
        public readonly List<string> FailedLines = new List<string>();
        public readonly List<ObjectId> PipeIds = new List<ObjectId>();
        public readonly List<string> Log = new List<string>();
    }

    /// <summary>
    /// Gravity networks from feature lines (and 3D polylines / polylines): every straight span between two points becomes a
    /// pipe, every arc between two PIs one curved pipe (split at elevation points that leave its grade). Elevations are the line's Z
    /// or a surface's less a depth, taken as the pipe's centre, invert, inner top, outer top or outer bottom.
    /// </summary>
    internal static class NetworkFromLines
    {
        // A span counts as an arc when its middle lies this far off the chord (m).
        private const double ArcSagitta = 0.002;
        private const double SamePoint = 1e-4;

        public static bool IsSource(DBObject obj) => obj is FeatureLine || obj is Polyline || obj is Polyline3d;

        public static string NameOf(DBObject obj)
        {
            if (obj is FeatureLine fl && !string.IsNullOrWhiteSpace(fl.Name)) return fl.Name.Trim();
            string kind = KindOf(obj);
            return $"{kind} {obj.Handle}";
        }

        public static string KindOf(DBObject obj) => obj is FeatureLine ? "Feature line" : obj is Polyline3d ? "3D polyline" : obj is Polyline ? "Polyline" : obj.GetType().Name;

        // =====================================================================================
        // Reading the source geometry
        // =====================================================================================

        /// <summary>The line as spans at its own elevations, or null with the reason.</summary>
        public static List<LinePiece> Read(Transaction tr, ObjectId id, out string error) => Read(tr, id, double.PositiveInfinity, out error, out _);

        /// <summary>The line's spans; feature line arcs whose elevation points leave one grade by more than the tolerance are split at them.</summary>
        public static List<LinePiece> Read(Transaction tr, ObjectId id, double arcGradeTolerance, out string error, out int splitArcs)
        {
            error = null;
            splitArcs = 0;
            List<LinePiece> spans = new List<LinePiece>();
            try
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is FeatureLine fl) ReadFeatureLine(fl, spans, arcGradeTolerance, ref splitArcs);
                else if (obj is Polyline pl) ReadPolyline(pl, spans);
                else if (obj is Polyline3d p3) ReadPolyline3d(tr, p3, spans);
                else { error = "Not a feature line or polyline."; return null; }
            }
            catch (Exception ex) { error = ex.Message; return null; }
            spans.RemoveAll(s => s.PlanLength < SamePoint);
            if (spans.Count == 0) { error = "It has no length in plan."; return null; }
            return spans;
        }

        /// <summary>
        /// Between two PIs a feature line is straight or one arc; elevation points split straight runs (grade breaks) but an arc
        /// stays one piece from PI to PI. The curve's parameter is the point index, so the points and half-way points between two
        /// PIs show which it is: the one farthest off the chord decides (Civil 3D's segment bulges are not reliable once an
        /// elevation point splits an arc, and the first half-way point alone lies almost on the chord when an elevation point
        /// sits close to the PI).
        /// </summary>
        private static void ReadFeatureLine(FeatureLine fl, List<LinePiece> spans, double arcGradeTolerance, ref int splitArcs)
        {
            Point3d[] all = fl.GetPoints(FeatureLinePointType.AllPoints).Cast<Point3d>().ToArray();
            Point3d[] pis = fl.GetPoints(FeatureLinePointType.PIPoint).Cast<Point3d>().ToArray();
            if (all.Length < 2) return;
            bool[] isPi = new bool[all.Length];
            for (int i = 0; i < all.Length; i++)
                isPi[i] = i == 0 || i == all.Length - 1 || pis.Any(p => Math.Abs(p.X - all[i].X) < SamePoint && Math.Abs(p.Y - all[i].Y) < SamePoint);
            Curve curve = fl;
            int a = 0;
            for (int b = 1; b < all.Length; b++)
            {
                if (!isPi[b]) continue;
                Point3d mid = curve.GetPointAtParameter(a + 0.5);
                double far = OffChord(all[a], all[b], mid);
                for (int k = a + 1; k < b; k++)
                    foreach (Point3d q in new[] { all[k], curve.GetPointAtParameter(k + 0.5) })
                    {
                        double off = OffChord(all[a], all[b], q);
                        if (off > far) { far = off; mid = q; }
                    }
                if (TryArc(all[a], mid, all[b], out Point2d center, out bool clockwise))
                {
                    LinePiece arc = new LinePiece { A = all[a], B = all[b], Curved = true, Center = center, Clockwise = clockwise };
                    // Elevation points on the arc off its one grade: a curved pipe from each to the next, so none is lost.
                    List<(double S, double Z)> grade = new List<(double S, double Z)>();
                    for (int k = a; k <= b; k++) grade.Add((k == a ? 0 : arc.Between(all[a], all[k]).PlanLength, all[k].Z));
                    if (b - a > 1 && PlanGeometry.GradeDeviation(grade) > arcGradeTolerance)
                    {
                        for (int k = a; k < b; k++) spans.Add(arc.Between(all[k], all[k + 1]));
                        splitArcs++;
                    }
                    else spans.Add(arc);
                }
                else
                    for (int k = a; k < b; k++) spans.Add(new LinePiece { A = all[k], B = all[k + 1] });
                a = b;
            }
        }

        private static void ReadPolyline(Polyline pl, List<LinePiece> spans)
        {
            int n = pl.NumberOfVertices, count = pl.Closed ? n : n - 1;
            for (int i = 0; i < count; i++)
            {
                Point3d a = pl.GetPoint3dAt(i), b = pl.GetPoint3dAt((i + 1) % n);
                double bulge = pl.GetBulgeAt(i);
                if (Math.Abs(bulge) > 1e-9)
                {
                    // The arc's middle: off the chord's midpoint by the sagitta, to the right of the direction for a positive bulge.
                    Vector2d chord = new Point2d(b.X, b.Y) - new Point2d(a.X, a.Y);
                    Vector2d left = new Vector2d(-chord.Y, chord.X).GetNormal();
                    Point2d m = new Point2d((a.X + b.X) / 2, (a.Y + b.Y) / 2) - left * (bulge * chord.Length / 2.0);
                    if (TryArc(a, new Point3d(m.X, m.Y, 0), b, out Point2d center, out bool clockwise))
                    {
                        spans.Add(new LinePiece { A = a, B = b, Curved = true, Center = center, Clockwise = clockwise });
                        continue;
                    }
                }
                spans.Add(new LinePiece { A = a, B = b });
            }
        }

        private static void ReadPolyline3d(Transaction tr, Polyline3d p3, List<LinePiece> spans)
        {
            List<Point3d> pts = new List<Point3d>();
            foreach (ObjectId vid in p3)
                if (tr.GetObject(vid, OpenMode.ForRead) is PolylineVertex3d v && v.VertexType != Vertex3dType.ControlVertex) pts.Add(v.Position);
            if (p3.Closed && pts.Count > 2) pts.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++) spans.Add(new LinePiece { A = pts[i - 1], B = pts[i] });
        }

        /// <summary>Plan distance from p to the line through a and b.</summary>
        private static double OffChord(Point3d a, Point3d b, Point3d p)
        {
            Vector2d chord = new Vector2d(b.X - a.X, b.Y - a.Y);
            if (chord.Length < SamePoint) return 0;
            return Math.Abs(chord.X * (p.Y - a.Y) - chord.Y * (p.X - a.X)) / chord.Length;
        }

        /// <summary>
        /// The plan circle through three points, when the middle one lies off the chord. Worked out relative to A: squaring
        /// project coordinates (hundreds of km) leaves about a millimetre of precision, and Civil 3D refuses a curved pipe whose
        /// ends are not equally far from its centre ("Cannot generate curve geometry") or makes a broken one.
        /// </summary>
        private static bool TryArc(Point3d a, Point3d m, Point3d b, out Point2d center, out bool clockwise)
        {
            center = Point2d.Origin;
            clockwise = false;
            Vector2d u = new Vector2d(m.X - a.X, m.Y - a.Y), v = new Vector2d(b.X - a.X, b.Y - a.Y);
            if (v.Length < SamePoint) return false;
            double cross = v.X * u.Y - v.Y * u.X;
            if (Math.Abs(cross) / v.Length < ArcSagitta) return false;
            double d = 2 * (u.X * v.Y - u.Y * v.X);
            if (Math.Abs(d) < 1e-12) return false;
            double u2 = u.X * u.X + u.Y * u.Y, v2 = v.X * v.X + v.Y * v.Y;
            Vector2d c = new Vector2d((v.Y * u2 - u.Y * v2) / d, (u.X * v2 - v.X * u2) / d);
            // Onto the chord's perpendicular bisector, so A and B are exactly a radius away.
            Vector2d n = new Vector2d(-v.Y, v.X).GetNormal();
            c = v / 2.0 + n * (c - v / 2.0).DotProduct(n);
            center = new Point2d(a.X + c.X, a.Y + c.Y);
            // Passing to the left of the chord on the way from A to B means the arc turns clockwise.
            clockwise = cross > 0;
            return true;
        }

        // =====================================================================================
        // Planning: elevations and clean-up (Z stays at the chosen reference)
        // =====================================================================================

        public static void Plan(Transaction tr, LineRow row, LineNetworkOptions o)
        {
            row.Pieces = new List<LinePiece>();
            row.Error = null;
            row.Notes.Clear();
            List<LinePiece> spans = Read(tr, row.SourceId, o.ArcGradeTolerance, out string error, out int splitArcs);
            if (spans == null) { row.Error = error; return; }
            if (splitArcs > 0) row.Notes.Add($"{splitArcs} arc(s) split at their elevation points");

            if (o.MaxLength > 0) spans = spans.SelectMany(s => SplitLong(s, o.MaxLength)).ToList();

            if (o.FromSurface)
            {
                Autodesk.Civil.DatabaseServices.Surface surface = o.SurfaceId.IsNull ? null : tr.GetObject(o.SurfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (surface == null) { row.Error = "Choose the surface the elevations come from."; return; }
                int missed = 0;
                Point3d Drop(Point3d p)
                {
                    double g = CoverRoute.GroundAt(surface, p.X, p.Y);
                    if (double.IsNaN(g)) { missed++; return p; }
                    return new Point3d(p.X, p.Y, g - o.Depth);
                }
                // An arc over uneven ground is split where the ground leaves one grade, so it follows the surface like straight spans do.
                int groundArcs = 0;
                spans = spans.SelectMany(s =>
                {
                    if (!s.Curved) return new[] { s };
                    int n = Math.Max(2, Math.Min(64, (int)Math.Ceiling(s.PlanLength / ArcSurfaceStep)));
                    List<Point3d> pts = Enumerable.Range(0, n + 1).Select(i => s.At((double)i / n)).ToList();
                    List<(double S, double Z)> ground = new List<(double S, double Z)>();
                    for (int i = 0; i <= n; i++)
                    {
                        double g = CoverRoute.GroundAt(surface, pts[i].X, pts[i].Y);
                        if (double.IsNaN(g)) return new[] { s };
                        ground.Add((s.PlanLength * i / n, g));
                    }
                    if (PlanGeometry.GradeDeviation(ground) <= o.ArcGradeTolerance) return new[] { s };
                    groundArcs++;
                    return Enumerable.Range(0, n).Select(i => s.Between(pts[i], pts[i + 1])).ToArray();
                }).ToList();
                if (groundArcs > 0) row.Notes.Add($"{groundArcs} arc(s) split to follow the surface");
                foreach (LinePiece s in spans) { s.A = Drop(s.A); s.B = Drop(s.B); }
                if (missed > 0) { row.Error = $"{missed} point(s) lie outside the surface."; return; }
            }

            if (o.MergeStraight) spans = MergeStraight(spans, o.MergeTolerance);
            spans = MergeShort(spans, o.MinLength, out int shortArcs);
            if (shortArcs > 0) row.Notes.Add($"{shortArcs} arc(s) shorter than {ToolUi.Format(o.MinLength)} m kept");

            if (o.Downhill && OverallGrade(spans) > 0)
            {
                bool endsDisagree = spans[0].A.Z >= spans[spans.Count - 1].B.Z;
                spans.Reverse();
                spans = spans.Select(s => s.Reversed()).ToList();
                row.Notes.Add(endsDisagree ? "reversed to run downhill (by its overall fall; its ends alone say otherwise)" : "reversed to run downhill");
            }
            int flat = spans.Count(s => Math.Abs(s.Slope) < 0.001);
            int rising = spans.Count(s => s.Slope >= 0.001);
            if (flat > 0) row.Notes.Add($"{flat} flat pipe(s)");
            if (rising > 0 && rising < spans.Count) row.Notes.Add($"{rising} pipe(s) rise along the flow");
            row.Pieces = spans;
        }

        /// <summary>A surface arc is checked against the ground about this often (m).</summary>
        private const double ArcSurfaceStep = 5.0;

        /// <summary>The line's overall grade (rise per metre, + = rising in the drawn direction), weighted by length.</summary>
        public static double OverallGrade(List<LinePiece> spans)
        {
            List<(double S, double Z)> profile = new List<(double S, double Z)> { (0, spans[0].A.Z) };
            double s = 0;
            foreach (LinePiece p in spans)
            {
                if (p.PlanLength < SamePoint) continue;
                // A jump in level between pieces (none in a line, but kept honest) counts at its station.
                if (Math.Abs(profile[profile.Count - 1].Z - p.A.Z) > 1e-9) profile.Add((s, p.A.Z));
                s += p.PlanLength;
                profile.Add((s, p.B.Z));
            }
            return PlanGeometry.TrendSlope(profile);
        }

        private static IEnumerable<LinePiece> SplitLong(LinePiece s, double max)
        {
            double len = s.PlanLength;
            if (s.Curved || len <= max + 1e-6) { yield return s; yield break; }
            int n = (int)Math.Ceiling(len / max - 1e-9);
            Point3d Lerp(double t) => new Point3d(s.A.X + (s.B.X - s.A.X) * t, s.A.Y + (s.B.Y - s.A.Y) * t, s.A.Z + (s.B.Z - s.A.Z) * t);
            for (int i = 0; i < n; i++) yield return new LinePiece { A = Lerp((double)i / n), B = Lerp((double)(i + 1) / n) };
        }

        private static List<LinePiece> MergeStraight(List<LinePiece> spans, double tolerance)
        {
            List<LinePiece> result = new List<LinePiece>();
            foreach (LinePiece s in spans)
            {
                LinePiece last = result.Count > 0 ? result[result.Count - 1] : null;
                if (last != null && !last.Curved && !s.Curved && OffLine(last.A, s.B, last.B) <= tolerance)
                {
                    result[result.Count - 1] = new LinePiece { A = last.A, B = s.B };
                    continue;
                }
                result.Add(s);
            }
            return result;
        }

        /// <summary>Distance in 3D from p to the segment a-b (beyond its ends counts as off).</summary>
        private static double OffLine(Point3d a, Point3d b, Point3d p)
        {
            Vector3d ab = b - a;
            if (ab.Length < 1e-9) return p.DistanceTo(a);
            double t = (p - a).DotProduct(ab) / ab.DotProduct(ab);
            if (t <= 0 || t >= 1) return double.MaxValue;
            return p.DistanceTo(a + ab * t);
        }

        private static List<LinePiece> MergeShort(List<LinePiece> spans, double min, out int shortArcs)
        {
            shortArcs = 0;
            if (min <= 0) return spans;
            List<LinePiece> list = new List<LinePiece>(spans);
            for (int i = 0; i < list.Count && list.Count > 1;)
            {
                LinePiece s = list[i];
                if (s.Length3d >= min) { i++; continue; }
                if (s.Curved) { shortArcs++; i++; continue; }
                // The short span's far point moves onto its straight neighbour's line: the joint between them goes.
                if (i + 1 < list.Count && !list[i + 1].Curved) { list[i + 1] = new LinePiece { A = s.A, B = list[i + 1].B }; list.RemoveAt(i); continue; }
                if (i > 0 && !list[i - 1].Curved) { list[i - 1] = new LinePiece { A = list[i - 1].A, B = s.B }; list.RemoveAt(i); i = Math.Max(0, i - 1); continue; }
                i++;
            }
            return list;
        }

        /// <summary>How far the pipe's centre sits above the line's elevation.</summary>
        public static double CenterOffset(LineElevationRef r, double inner, double outer)
        {
            switch (r)
            {
                case LineElevationRef.Invert: return inner / 2.0;
                case LineElevationRef.InnerTop: return -inner / 2.0;
                case LineElevationRef.OuterTop: return -outer / 2.0;
                case LineElevationRef.OuterBottom: return outer / 2.0;
                default: return 0;
            }
        }

        // =====================================================================================
        // Creating
        // =====================================================================================

        public static LineNetworkResult Create(Transaction tr, CivilDocument doc, List<LineRow> rows, PartPick defaultPart, LineNetworkOptions o, Editor ed)
        {
            LineNetworkResult result = new LineNetworkResult();
            Dictionary<string, ObjectId> networks = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in doc.GetPipeNetworkIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Network n) networks[n.Name] = id;
            // The structures ends can be connected to in each network: those it had before this run (when asked) and those this
            // run makes, so lines meeting at a point share one structure.
            Dictionary<ObjectId, List<StructureFootprint>> inNetwork = new Dictionary<ObjectId, List<StructureFootprint>>();
            Dictionary<string, (ObjectId family, ObjectId size)> parts = new Dictionary<string, (ObjectId, ObjectId)>();
            Dictionary<string, (double inner, double outer)> dims = new Dictionary<string, (double, double)>();
            Autodesk.Civil.DatabaseServices.Surface surface = o.SurfaceId.IsNull ? null : tr.GetObject(o.SurfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;

            (ObjectId family, ObjectId size) PartIn(ObjectId netId, PartPick pick)
            {
                string key = $"{netId.Handle}|{pick.PartsListId.Handle}|{pick.FamilyGuid}|{pick.SizeName}";
                if (!parts.TryGetValue(key, out var ids)) parts[key] = ids = PartsListTools.Resolve(tr, netId, ObjectId.Null, pick, result.Log);
                return ids;
            }

            foreach (LineRow row in rows)
            {
                if (!row.Use || row.Error != null || row.Pieces.Count == 0) continue;
                PartPick pick = row.Part ?? defaultPart;
                List<ObjectId> madeIds = new List<ObjectId>();
                string madeNetwork = null;
                int structures = 0, connected = 0;
                try
                {
                    // The network: one of that name (made now when there is none).
                    string name = string.IsNullOrWhiteSpace(row.Network) ? row.SourceName : row.Network.Trim();
                    if (!networks.TryGetValue(name, out ObjectId netId))
                    {
                        string made = name;
                        netId = Network.Create(doc, ref made);
                        Network created = (Network)tr.GetObject(netId, OpenMode.ForWrite);
                        try { created.PartsListId = pick.PartsListId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        if (surface != null) try { created.ReferenceSurfaceId = surface.ObjectId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        networks[name] = netId;
                        madeNetwork = name;
                        inNetwork[netId] = new List<StructureFootprint>();
                        result.Networks++;
                    }
                    else if (!inNetwork.ContainsKey(netId)) inNetwork[netId] = o.ConnectExisting ? StructureFootprint.OfNetwork(tr, netId) : new List<StructureFootprint>();
                    Network network = (Network)tr.GetObject(netId, OpenMode.ForWrite);

                    (ObjectId familyId, ObjectId sizeId) = PartIn(netId, pick);
                    if (sizeId.IsNull) throw new InvalidOperationException($"{pick.Label} could not be put in the parts list of network {network.Name}.");
                    string dimKey = $"{familyId.Handle}|{sizeId.Handle}";
                    if (!dims.TryGetValue(dimKey, out var d))
                    {
                        // The size's own dimensions (outer = inside + two walls, as for cover); a size without them is measured.
                        d = (pick.InnerHeight, pick.InnerHeight + 2 * Math.Max(0, pick.Wall));
                        if (d.inner <= 0) d = Measure(tr, network, familyId, sizeId, row.Pieces[0].A);
                        dims[dimKey] = d;
                    }
                    double off = CenterOffset(o.Reference, d.inner, d.outer);

                    List<(Pipe pipe, LinePiece piece)> pipes = new List<(Pipe, LinePiece)>();
                    foreach (LinePiece piece in row.Pieces)
                    {
                        List<(ObjectId id, LinePiece piece)> made = AddPiece(tr, network, familyId, sizeId, piece, off, out bool chorded);
                        if (chorded)
                        {
                            ed.WriteMessage($"\n[Warning] {row.SourceName}: Civil 3D would not take the arc at {ToolUi.Format(piece.A.X)}, {ToolUi.Format(piece.A.Y)} " +
                                            $"as a curved pipe; it is drawn as {made.Count} straight pipes along the arc.");
                            result.Chorded++;
                        }
                        foreach ((ObjectId id, LinePiece p) in made) { pipes.Add(((Pipe)tr.GetObject(id, OpenMode.ForWrite), p)); madeIds.Add(id); }
                    }

                    // A structure the end lies in: one already in the network (when asked) or one this run made there.
                    StructureFootprint At(Point3d at) => inNetwork[netId].FirstOrDefault(f => f.Contains(at, 0.05));
                    ObjectId Add(ObjectId sf, ObjectId ss, Point3d at, LinePiece piece, params Pipe[] around)
                    {
                        ObjectId sid = AddStructure(tr, network, sf, ss, at, Direction(piece), surface, d, around);
                        madeIds.Add(sid);
                        inNetwork[netId].Add(StructureFootprint.Of((Structure)tr.GetObject(sid, OpenMode.ForRead)));
                        structures++;
                        return sid;
                    }

                    if (o.Joint != null && pipes.Count > 1)
                    {
                        (ObjectId sf, ObjectId ss) = PartIn(netId, o.Joint);
                        if (ss.IsNull) ed.WriteMessage($"\n[Warning] {o.Joint.Label} could not be put in the parts list of {network.Name}; {row.SourceName} has no joint structures.");
                        else
                            for (int i = 1; i < pipes.Count; i++)
                            {
                                Point3d at = pipes[i].pipe.StartPoint;
                                ObjectId sid = At(at)?.Id ?? ObjectId.Null;
                                if (sid.IsNull) sid = Add(sf, ss, at, pipes[i].piece, pipes[i - 1].pipe, pipes[i].pipe);
                                else connected++;
                                CoverApply.Connect(pipes[i - 1].pipe, true, false, sid, ed);
                                CoverApply.Connect(pipes[i].pipe, true, true, sid, ed);
                            }
                    }

                    // Ends: a structure around the end first, else a new one when asked.
                    (ObjectId ef, ObjectId es) = o.Ends != null ? PartIn(netId, o.Ends) : (ObjectId.Null, ObjectId.Null);
                    if (o.Ends != null && es.IsNull) ed.WriteMessage($"\n[Warning] {o.Ends.Label} could not be put in the parts list of {network.Name}; {row.SourceName} has no end structures.");
                    ObjectId startStructure = ObjectId.Null;
                    List<(Pipe pipe, bool atStart, NetworkEditOps.SplitPlan plan)> midSpan = new List<(Pipe, bool, NetworkEditOps.SplitPlan)>();
                    foreach (bool atStart in new[] { true, false })
                    {
                        (Pipe p, LinePiece piece) = atStart ? pipes[0] : pipes[pipes.Count - 1];
                        Point3d at = atStart ? p.StartPoint : p.EndPoint;
                        ObjectId sid = At(at)?.Id ?? ObjectId.Null;
                        if (!sid.IsNull) connected++;
                        else if (MidSpan(tr, network, madeIds, at) is NetworkEditOps.SplitPlan met)
                        {
                            // On a pipe of the network, away from its ends: a junction there (made last), or reported.
                            Pipe other = (Pipe)tr.GetObject(met.PipeId, OpenMode.ForRead);
                            PartPick junction = o.Joint ?? o.Ends;
                            if (o.SplitPipeMetMidSpan && junction != null) { midSpan.Add((p, atStart, met)); continue; }
                            string why = o.SplitPipeMetMidSpan ? " (choose a joint or end structure to split it there)" : "";
                            string note = $"{row.SourceName}: its {(atStart ? "start" : "end")} lies on pipe {other.Name} mid-span; not connected{why}.";
                            ed.WriteMessage($"\n[Note] {note}");
                            result.Log.Add(note);
                            if (!es.IsNull) sid = Add(ef, es, at, piece, p);
                        }
                        else if (!es.IsNull) sid = Add(ef, es, at, piece, p);
                        // Civil 3D refuses both ends of one pipe at the same structure.
                        if (sid.IsNull || (!atStart && pipes.Count == 1 && sid == startStructure)) continue;
                        CoverApply.Connect(p, true, atStart, sid, ed);
                        if (atStart) startStructure = sid;
                    }

                    // Last, once the line is made: the pipes met mid-span are split, with a junction structure their ends connect to.
                    foreach ((Pipe p, bool atStart, NetworkEditOps.SplitPlan plan) in midSpan)
                    {
                        Point3d at = plan.Points[0];
                        NetworkEditOps.Counts c = NetworkEditOps.ApplySplit(tr, new List<NetworkEditOps.SplitPlan> { plan }, o.Joint ?? o.Ends, ed);
                        ObjectId sid = JunctionAt(tr, plan.PipeId, at);
                        if (c.Failed > 0 || sid.IsNull) { result.Log.Add($"{row.SourceName}: pipe {plan.Name} could not be split for a junction."); continue; }
                        structures += c.Structures;
                        inNetwork[netId].Add(StructureFootprint.Of((Structure)tr.GetObject(sid, OpenMode.ForRead)));
                        CoverApply.Connect(p, true, atStart, sid, ed);
                        result.Log.Add($"{row.SourceName}: pipe {plan.Name} split, and joined at a new structure where the line meets it.");
                        result.Split++;
                    }

                    if (o.EraseSource)
                        try { tr.GetObject(row.SourceId, OpenMode.ForWrite).Erase(); result.Erased++; }
                        catch (Exception ex) { ed.WriteMessage($"\n[Warning] {row.SourceName} could not be erased: {ex.Message}"); }
                    result.PipeIds.AddRange(pipes.Select(x => x.pipe.ObjectId));
                    result.Pipes += pipes.Count;
                    result.Structures += structures;
                    result.Connected += connected;
                    result.Lines++;
                }
                catch (Exception ex)
                {
                    // Nothing half made: a line that fails leaves no pipes or structures behind.
                    foreach (ObjectId id in madeIds)
                        try { if (!id.IsErased) tr.GetObject(id, OpenMode.ForWrite).Erase(); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
                    foreach (List<StructureFootprint> l in inNetwork.Values) l.RemoveAll(f => madeIds.Contains(f.Id));
                    if (madeNetwork != null && networks.TryGetValue(madeNetwork, out ObjectId emptyId))
                    {
                        try { tr.GetObject(emptyId, OpenMode.ForWrite).Erase(); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
                        networks.Remove(madeNetwork);
                        inNetwork.Remove(emptyId);
                        result.Networks--;
                    }
                    ed.WriteMessage($"\n[Error] {row.SourceName}: {ex.Message}");
                    result.Failed++;
                    result.FailedLines.Add(row.SourceName);
                }
            }
            return result;
        }

        /// <summary>How close (m, in plan) a line end must be to a pipe to count as meeting it.</summary>
        private const double MidSpanTolerance = 0.05;

        /// <summary>The split that would put a junction where <paramref name="at"/> meets a pipe of the network mid-span (not one made now), or null.</summary>
        private static NetworkEditOps.SplitPlan MidSpan(Transaction tr, Network network, List<ObjectId> made, Point3d at)
        {
            List<ObjectId> candidates = network.GetPipeIds().Live().Where(id => !made.Contains(id)).ToList();
            if (candidates.Count == 0) return null;
            List<NetworkEditOps.SplitPlan> plans = NetworkEditOps.PlanSplitAtPoints(tr, candidates, new List<Point3d> { at }, MidSpanTolerance, out _);
            return plans.Count == 1 && plans[0].Points.Count == 1 ? plans[0] : null;
        }

        /// <summary>The structure the split pipe now has at the split point (it ends there, or starts there for a curved one).</summary>
        private static ObjectId JunctionAt(Transaction tr, ObjectId pipeId, Point3d at)
        {
            Pipe pipe = (Pipe)tr.GetObject(pipeId, OpenMode.ForRead);
            foreach (ObjectId sid in new[] { pipe.EndStructureId, pipe.StartStructureId })
                if (!sid.IsNull && tr.GetObject(sid, OpenMode.ForRead) is Structure st &&
                    new Point2d(st.Location.X, st.Location.Y).GetDistanceTo(new Point2d(at.X, at.Y)) < 0.01) return sid;
            return ObjectId.Null;
        }

        private const double ChordSagitta = 0.01;

        /// <summary>
        /// The pipe(s) for one piece, with the piece each one follows. Civil 3D sometimes refuses a curved pipe or makes one whose
        /// ends are not where they were asked for; that one is erased and tried again from the arc's bulge, and as a last resort
        /// the arc is drawn as straight pipes along it, none more than <see cref="ChordSagitta"/> off it.
        /// </summary>
        private static List<(ObjectId id, LinePiece piece)> AddPiece(Transaction tr, Network network, ObjectId familyId, ObjectId sizeId, LinePiece piece, double off, out bool chorded)
        {
            chorded = false;
            Point3d a = new Point3d(piece.A.X, piece.A.Y, piece.A.Z + off), b = new Point3d(piece.B.X, piece.B.Y, piece.B.Z + off);
            List<(ObjectId, LinePiece)> made = new List<(ObjectId, LinePiece)>();
            ObjectId Line(Point3d p, Point3d q)
            {
                ObjectId id = ObjectId.Null;
                using (LineSegment3d line = new LineSegment3d(p, q)) network.AddLinePipe(familyId, sizeId, line, ref id, false);
                if (id.IsNull) throw new InvalidOperationException("Civil 3D did not return a new pipe.");
                return id;
            }
            if (!piece.Curved) { made.Add((Line(a, b), piece)); return made; }

            double bulge = Math.Tan(piece.Sweep / 4.0) * (piece.Clockwise ? -1 : 1);
            foreach (Func<PipeCurveGeometry> geometry in new Func<PipeCurveGeometry>[]
                     { () => new PipeCurveGeometry(a, piece.Center, b, piece.Clockwise), () => new PipeCurveGeometry(a, b, bulge) })
            {
                ObjectId id = ObjectId.Null;
                try { using (PipeCurveGeometry g = geometry()) id = network.AddCurvePipe(familyId, sizeId, g, false); }
                catch (Exception) { continue; }
                if (id.IsNull) continue;
                Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
                if (p.StartPoint.DistanceTo(a) < 0.001 && p.EndPoint.DistanceTo(b) < 0.001 && Math.Abs(p.Length2D - piece.PlanLength) < 0.001 + piece.PlanLength * 1e-4)
                {
                    made.Add((id, piece));
                    return made;
                }
                p.Erase();
            }

            chorded = true;
            Vector2d r0 = new Point2d(a.X, a.Y) - piece.Center;
            double radius = r0.Length, sweep = piece.Sweep, turn = piece.Clockwise ? -1 : 1;
            int n = Math.Max(2, (int)Math.Ceiling(sweep / (2 * Math.Acos(Math.Max(-1.0, 1 - ChordSagitta / radius)))));
            Point3d prev = a;
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n, ang = Math.Atan2(r0.Y, r0.X) + turn * sweep * t;
                Point3d next = i == n ? b : new Point3d(piece.Center.X + radius * Math.Cos(ang), piece.Center.Y + radius * Math.Sin(ang), a.Z + (b.Z - a.Z) * t);
                made.Add((Line(prev, next), new LinePiece { A = prev, B = next }));
                prev = next;
            }
            return made;
        }

        /// <summary>The part's inside and outside height, read from a pipe of it (made and erased again).</summary>
        private static (double inner, double outer) Measure(Transaction tr, Network network, ObjectId familyId, ObjectId sizeId, Point3d near)
        {
            ObjectId id = ObjectId.Null;
            using (LineSegment3d line = new LineSegment3d(near, near + new Vector3d(1, 0, 0))) network.AddLinePipe(familyId, sizeId, line, ref id, false);
            Pipe probe = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
            double inner = 0;
            try { inner = probe.InnerHeight; } catch (Exception failure) when (C3DProLogger.IsProbeFailure(failure)) { }
            double outer = CoverRoute.OuterHeightOf(probe);
            probe.Erase();
            return (inner, Math.Max(inner, outer));
        }

        private static double Direction(LinePiece p)
        {
            if (!p.Curved) return Math.Atan2(p.B.Y - p.A.Y, p.B.X - p.A.X);
            Vector2d r = new Point2d(p.A.X, p.A.Y) - p.Center;
            return Math.Atan2(r.Y, r.X) + (p.Clockwise ? -Math.PI / 2 : Math.PI / 2);
        }

        /// <summary>A structure at a pipe end: rim on the surface (else level with the highest pipe top), sump at the lowest invert.</summary>
        private static ObjectId AddStructure(Transaction tr, Network network, ObjectId familyId, ObjectId sizeId, Point3d at, double rotation,
            Autodesk.Civil.DatabaseServices.Surface surface, (double inner, double outer) d, params Pipe[] pipes)
        {
            double ground = surface == null ? double.NaN : CoverRoute.GroundAt(surface, at.X, at.Y);
            double top = pipes.Max(p => EndZ(p, at)) + d.outer / 2.0;
            double invert = pipes.Min(p => EndZ(p, at)) - d.inner / 2.0;
            double rim = double.IsNaN(ground) ? top : ground;
            ObjectId id = ObjectId.Null;
            network.AddStructure(familyId, sizeId, new Point3d(at.X, at.Y, rim), rotation, ref id, false);
            Structure st = (Structure)tr.GetObject(id, OpenMode.ForWrite);
            try { st.RimElevation = rim; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { st.ControlSumpBy = StructureControlSumpType.ByElevation; st.SumpElevation = invert; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return id;
        }

        private static double EndZ(Pipe p, Point3d at) => p.StartPoint.DistanceTo(at) <= p.EndPoint.DistanceTo(at) ? p.StartPoint.Z : p.EndPoint.Z;
    }
}
