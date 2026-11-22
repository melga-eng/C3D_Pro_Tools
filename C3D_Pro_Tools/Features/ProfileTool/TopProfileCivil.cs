using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace C3DProTools.ProfileTool
{
    public sealed class TopOptions
    {
        /// <summary>A profile whose name contains this (case-insensitive) is a TOP profile.</summary>
        public string Pattern = "TOP";
        /// <summary>Max plan offset (drawing units) for a duct to count as running along the alignment.</summary>
        public double OffsetTolerance = 0.30;
        /// <summary>True: duct end at the structure's outer face (inside edge + wall), as profile views draw it. False: inside edge.</summary>
        public bool OuterFace = true;
        /// <summary>True: only ducts shown in the alignment's profile views. False: every duct along the alignment.</summary>
        public bool ShownOnly = true;
        /// <summary>
        /// False: the TOP follows the highest outside, an encasement drawn around a cable included. True: where one pipe holds
        /// another (an encasement around a cable), the TOP follows the pipe inside.
        /// </summary>
        public bool InsideEncasement = false;
    }

    public sealed class ProfileRef
    {
        public ObjectId Id;
        public string Name;
        public double Start, End;
        public bool OnAlignment;
        public override string ToString() => Name;
    }

    public sealed class DuctInfo
    {
        public string Name, Network;
        public DuctSegment Segment;
    }

    /// <summary>Everything worked out for one alignment; shared by all of its profile views.</summary>
    public sealed class AlignmentFit
    {
        public ObjectId AlignmentId;
        public string Alignment;
        public double Start, End;
        public List<DuctInfo> Ducts = new();
        public TopFitResult Result = new();
        public List<ProfileRef> Tops = new();
        public ProfileRef Primary;
        public double? MaxDiff;
        public string Status = "";
        public List<string> Notes = new();

        public const string StatusFits = "fits", StatusDiffers = "differs", StatusMissing = "missing TOP", StatusNoDucts = "no ducts";
    }

    public sealed class ViewRow
    {
        public ObjectId ViewId;
        public string View;
        public double StaStart, StaEnd;
        public int DuctsInView;
        public string Networks = "";
        public AlignmentFit Fit;
    }

    public sealed class TopScan
    {
        public List<ViewRow> Views = new();
        public List<AlignmentFit> Alignments = new();
        public List<string> TopStyles = new();
        public List<string> TopLayers = new();
    }

    public static class TopProfileCivil
    {
        private const double FitTolerance = 0.005;

        public static TopScan Scan(Database db, CivilDocument civil, TopOptions o)
        {
            var scan = new TopScan();
            using var tr = db.TransactionManager.StartTransaction();

            // All gravity pipes once: their drawn (trimmed) ends, and which profile views show them.
            var pipes = new List<(Pipe pipe, string network, Point3d a, Point3d b)>();
            var shownIn = new Dictionary<ObjectId, HashSet<ObjectId>>();
            foreach (ObjectId nid in civil.GetPipeNetworkIds())
            {
                if (tr.GetObject(nid, OpenMode.ForRead) is not Network net) continue;
                var netPipes = net.GetPipeIds().Live()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe).Where(p => p != null).ToList();
                var trims = StructureTrims(tr, netPipes, o.OuterFace);
                foreach (var p in netPipes)
                {
                    var (ta, tb) = trims[p.ObjectId];
                    if (!TrimmedEnds(p, ta, tb, out Point3d a, out Point3d b)) continue;
                    pipes.Add((p, net.Name, a, b));
                    var views = new HashSet<ObjectId>();
                    try { foreach (ObjectId v in p.GetProfileViewsDisplayingMe()) views.Add(v); } catch { }
                    shownIn[p.ObjectId] = views;
                }
            }

            var styleCount = new Dictionary<string, int>();
            var layerCount = new Dictionary<string, int>();

            foreach (ObjectId aid in civil.GetAlignmentIds())
            {
                if (tr.GetObject(aid, OpenMode.ForRead) is not Alignment al) continue;
                var viewIds = al.GetProfileViewIds().Cast<ObjectId>().ToList();
                if (viewIds.Count == 0) continue;
                var viewSet = new HashSet<ObjectId>(viewIds);

                var fit = new AlignmentFit { AlignmentId = aid, Alignment = al.Name, Start = al.StartingStation, End = al.EndingStation };
                Extents3d? reach = null;
                try { var e = al.GeometricExtents; reach = new Extents3d(e.MinPoint - new Vector3d(1, 1, 0) * 5, e.MaxPoint + new Vector3d(1, 1, 0) * 5); } catch { }

                foreach (var (pipe, network, a, b) in pipes)
                {
                    if (o.ShownOnly && !shownIn[pipe.ObjectId].Overlaps(viewSet)) continue;
                    if (reach.HasValue && !InBox(reach.Value, a) && !InBox(reach.Value, b)) continue;
                    if (!Along(al, a, b, o.OffsetTolerance, out double s0, out double s1, out double offset)) continue;
                    double half = OuterHeightOf(pipe) / 2.0;
                    fit.Ducts.Add(new DuctInfo
                    {
                        Name = pipe.Name,
                        Network = network,
                        Segment = new DuctSegment(pipe.Name, s0, a.Z + half, s1, b.Z + half) { Height = 2 * half, Offset = offset, Width = C3DProTools.CoverRoute.OuterWidthOf(pipe) },
                    });
                }
                fit.Ducts.Sort((x, y) => x.Segment.S0.CompareTo(y.Segment.S0));
                fit.Result = TopProfileMath.Compute(fit.Ducts.Select(d => d.Segment), fit.Start, fit.End, o.InsideEncasement);

                foreach (ObjectId pid in al.GetProfileIds())
                {
                    if (tr.GetObject(pid, OpenMode.ForRead) is not Profile p) continue;
                    if (string.IsNullOrEmpty(o.Pattern) || p.Name.IndexOf(o.Pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var r = new ProfileRef { Id = pid, Name = p.Name };
                    try { r.Start = p.StartingStation; r.End = p.EndingStation; } catch { }
                    r.OnAlignment = Math.Min(r.End, fit.End) - Math.Max(r.Start, fit.Start) > TopProfileMath.StationEps;
                    if (!r.OnAlignment) fit.Notes.Add($"'{p.Name}' lies outside the alignment (sta {r.Start:F2} to {r.End:F2}; alignment {fit.Start:F2} to {fit.End:F2}).");
                    fit.Tops.Add(r);
                    Bump(styleCount, p.StyleName);
                    Bump(layerCount, p.Layer);
                }
                fit.Primary = fit.Tops.Where(t => t.OnAlignment)
                    .OrderByDescending(t => Math.Min(t.End, fit.End) - Math.Max(t.Start, fit.Start)).FirstOrDefault();
                if (fit.Tops.Count(t => t.OnAlignment) > 1)
                    fit.Notes.Add($"{fit.Tops.Count(t => t.OnAlignment)} TOP profiles on this alignment; Fit changes '{fit.Primary.Name}'.");

                if (fit.Result.Points.Count < 2) fit.Status = AlignmentFit.StatusNoDucts;
                else if (fit.Primary == null) fit.Status = AlignmentFit.StatusMissing;
                else
                {
                    fit.MaxDiff = MaxDifference(tr, fit.Primary.Id, fit.Result.Points, fit.Notes, out bool shortProfile);
                    fit.Status = fit.MaxDiff <= FitTolerance && !shortProfile ? AlignmentFit.StatusFits : AlignmentFit.StatusDiffers;
                }
                scan.Alignments.Add(fit);

                foreach (ObjectId vid in viewIds)
                {
                    if (tr.GetObject(vid, OpenMode.ForRead) is not ProfileView v) continue;
                    var row = new ViewRow { ViewId = vid, View = v.Name, Fit = fit };
                    try { row.StaStart = v.StationStart; row.StaEnd = v.StationEnd; } catch { row.StaStart = fit.Start; row.StaEnd = fit.End; }
                    var inView = fit.Ducts.Where(d => d.Segment.S1 > row.StaStart && d.Segment.S0 < row.StaEnd).ToList();
                    row.DuctsInView = inView.Count;
                    row.Networks = string.Join(", ", inView.Select(d => d.Network).Distinct());
                    scan.Views.Add(row);
                }
            }

            scan.TopStyles = styleCount.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
            scan.TopLayers = layerCount.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
            tr.Commit();
            return scan;
        }

        // ---------- operations ----------

        /// <summary>Replaces the geometry of each alignment's TOP profile with the duct fit. Name, style, labels and views stay.</summary>
        public static OperationReport Fit(Document doc, IEnumerable<AlignmentFit> fits)
        {
            var report = new OperationReport();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var f in fits)
                {
                    if (f.Primary == null || f.Result.Points.Count < 2) { report.Skipped++; continue; }
                    // One nested transaction per profile: a failure after Clear restores that profile, not an empty one.
                    using var one = tr.TransactionManager.StartTransaction();
                    try
                    {
                        var p = (Profile)one.GetObject(f.Primary.Id, OpenMode.ForWrite);
                        WriteTangents(p, f.Result.Points);
                        one.Commit();
                        report.Done++;
                    }
                    catch (Exception ex)
                    {
                        one.Abort();
                        report.Skipped++;
                        report.Notes.Add($"{f.Alignment} / {f.Primary.Name}: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            return report;
        }

        public sealed class CreateTopOptions
        {
            public string NameTemplate = "{alignment}-Duct-TOP";
            public ObjectId StyleId, LabelSetId, LayerId;
        }

        /// <summary>Creates a layout TOP profile from the duct fit on each alignment that has ducts.</summary>
        public static OperationReport Create(Document doc, IEnumerable<AlignmentFit> fits, CreateTopOptions o)
        {
            var report = new OperationReport();
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layer = o.LayerId.IsNull ? db.Clayer : o.LayerId;
                foreach (var f in fits)
                {
                    if (f.Result.Points.Count < 2) { report.Skipped++; report.Notes.Add($"{f.Alignment}: no ducts"); continue; }
                    // Nested per alignment: a profile whose tangents fail is not left behind half made.
                    using var one = tr.TransactionManager.StartTransaction();
                    try
                    {
                        var al = (Alignment)one.GetObject(f.AlignmentId, OpenMode.ForRead);
                        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId pid in al.GetProfileIds())
                            if (tr.GetObject(pid, OpenMode.ForRead) is Profile ep) taken.Add(ep.Name);
                        string baseName = (string.IsNullOrWhiteSpace(o.NameTemplate) ? "{alignment}-Duct-TOP" : o.NameTemplate)
                            .Replace("{alignment}", al.Name, StringComparison.OrdinalIgnoreCase);
                        string name = baseName;
                        for (int i = 1; taken.Contains(name); i++) name = $"{baseName} ({i})";

                        ObjectId id = Profile.CreateByLayout(name, f.AlignmentId, layer, o.StyleId, o.LabelSetId);
                        var p = (Profile)one.GetObject(id, OpenMode.ForWrite);
                        WriteTangents(p, f.Result.Points);
                        one.Commit();
                        report.Done++;
                    }
                    catch (Exception ex)
                    {
                        one.Abort();
                        report.Skipped++;
                        report.Notes.Add($"{f.Alignment}: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            return report;
        }

        public static void ZoomTo(Document doc, ObjectId entityId)
        {
            Extents3d ext;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                ext = ((Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(entityId, OpenMode.ForRead)).GeometricExtents;
                tr.Commit();
            }
            var ed = doc.Editor;
            using var view = ed.GetCurrentView();
            double w = ext.MaxPoint.X - ext.MinPoint.X, h = ext.MaxPoint.Y - ext.MinPoint.Y;
            double ratio = view.Width / view.Height;
            if (w / h > ratio) h = w / ratio; else w = h * ratio;
            view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
            view.Width = w * 1.1;
            view.Height = h * 1.1;
            ed.SetCurrentView(view);
            ed.UpdateScreen();
        }

        // ---------- helpers ----------

        private static void WriteTangents(Profile p, List<StationElevation> pts)
        {
            p.Entities.Clear();
            for (int i = 0; i + 1 < pts.Count; i++)
                p.Entities.AddFixedTangent(new Point2d(pts[i].Station, pts[i].Elevation), new Point2d(pts[i + 1].Station, pts[i + 1].Elevation));
        }

        /// <summary>
        /// How far each pipe end is drawn back from its plan end point. A pipe connected to a structure runs to the
        /// structure centre; Civil 3D draws it to the inside edge (Length2D - Length2DToInsideEdge, split per end),
        /// and the structure body covers it to the outer face (+ wall).
        /// </summary>
        private static Dictionary<ObjectId, (double start, double end)> StructureTrims(Transaction tr, List<Pipe> pipes, bool outerFace)
        {
            var known = new Dictionary<ObjectId, double>();
            var info = new List<(Pipe p, ObjectId ss, ObjectId es, double total)>();
            foreach (var p in pipes)
            {
                ObjectId ss = ObjectId.Null, es = ObjectId.Null;
                double total = 0;
                try { ss = p.StartStructureId; es = p.EndStructureId; total = Math.Max(0, p.Length2D - p.Length2DToInsideEdge); } catch { }
                info.Add((p, ss, es, total));
                if (ss.IsNull != es.IsNull && total > 0)
                {
                    var sid = ss.IsNull ? es : ss;
                    known[sid] = known.TryGetValue(sid, out double prev) ? Math.Max(prev, total) : total;
                }
            }

            var result = new Dictionary<ObjectId, (double, double)>();
            foreach (var (p, ss, es, total) in info)
            {
                double ts = 0, te = 0;
                if (!ss.IsNull && !es.IsNull)
                {
                    if (known.TryGetValue(ss, out double k)) { ts = Math.Min(k, total); te = total - ts; }
                    else if (known.TryGetValue(es, out k)) { te = Math.Min(k, total); ts = total - te; }
                    else { ts = te = total / 2; }
                }
                else if (!ss.IsNull) ts = total;
                else if (!es.IsNull) te = total;

                // A pipe that reports no trim of its own (end dragged onto the structure) still stops at the
                // structure's edge, as its other pipes do.
                if (!ss.IsNull && known.TryGetValue(ss, out double ks) && ks > ts) ts = ks;
                if (!es.IsNull && known.TryGetValue(es, out double ke) && ke > te) te = ke;

                if (outerFace)
                {
                    if (!ss.IsNull) ts += WallOf(tr, ss);
                    if (!es.IsNull) te += WallOf(tr, es);
                }
                result[p.ObjectId] = (ts, te);
            }
            return result;
        }

        private static double WallOf(Transaction tr, ObjectId structureId)
        {
            try { return Math.Max(0, ((Structure)tr.GetObject(structureId, OpenMode.ForRead)).WallThickness); }
            catch { return 0; }
        }

        private static bool TrimmedEnds(Pipe p, double trimStart, double trimEnd, out Point3d a, out Point3d b)
        {
            Point3d s = p.StartPoint, e = p.EndPoint;
            a = s; b = e;
            var plan = new Vector2d(e.X - s.X, e.Y - s.Y);
            double len = plan.Length;
            if (len < 1e-6 || trimStart + trimEnd >= len - 1e-3) return false;
            a = Lerp(s, e, trimStart / len);
            b = Lerp(s, e, 1 - trimEnd / len);
            return true;
        }

        private static Point3d Lerp(Point3d s, Point3d e, double t) =>
            new Point3d(s.X + (e.X - s.X) * t, s.Y + (e.Y - s.Y) * t, s.Z + (e.Z - s.Z) * t);

        /// <summary>Both ends and the middle within tolerance of the alignment. Stations are clipped to the alignment later.</summary>
        private static bool Along(Alignment al, Point3d a, Point3d b, double tol, out double s0, out double s1, out double offset)
        {
            s0 = s1 = offset = 0;
            if (!StaOff(al, a, out s0, out double o0) || Math.Abs(o0) > tol) return false;
            if (!StaOff(al, b, out s1, out double o1) || Math.Abs(o1) > tol) return false;
            var mid = Lerp(a, b, 0.5);
            if (!StaOff(al, mid, out _, out double om) || Math.Abs(om) > tol) return false;
            offset = om;
            return true;
        }

        private static bool StaOff(Alignment al, Point3d p, out double station, out double offset)
        {
            station = 0; offset = 0;
            bool outOfRange = false;
            try { al.StationOffsetAcceptOutOfRange(p.X, p.Y, ref station, ref offset, ref outOfRange); }
            catch { return false; }
            // A duct a little past the alignment end is still along it; Compute clips it.
            return true;
        }

        private static bool InBox(Extents3d e, Point3d p) =>
            p.X >= e.MinPoint.X && p.X <= e.MaxPoint.X && p.Y >= e.MinPoint.Y && p.Y <= e.MaxPoint.Y;

        /// <summary>Same rule as C3D_Pro_Tools CoverRoute.OuterHeightOf: duct banks report OuterHeight 0, so rebuild it.</summary>
        private static double OuterHeightOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterHeight; } catch { }
            try { inner = pipe.InnerHeight; } catch { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch { }
            return Math.Max(outer, inner + 2 * wall);
        }

        /// <summary>
        /// Largest |existing - fit| at the fit's PVIs and the existing profile's PVIs. Stations within 1 cm past the
        /// existing profile's ends are read at its end (rounding); a profile genuinely shorter than the fit is noted.
        /// </summary>
        private static double MaxDifference(Transaction tr, ObjectId profileId, List<StationElevation> fit, List<string> notes, out bool shortProfile)
        {
            const double EndSlack = 0.01;
            var p = (Profile)tr.GetObject(profileId, OpenMode.ForRead);
            double ps = p.StartingStation, pe = p.EndingStation;
            var stations = fit.Select(f => f.Station).ToList();
            try { foreach (ProfilePVI v in p.PVIs) stations.Add(v.RawStation); } catch { }
            // A vertical step (two PVIs a hair apart) has two heights at its station: compared just before and after it instead.
            var steps = new List<double>();
            for (int i = 0; i + 1 < fit.Count; i++)
                if (fit[i + 1].Station - fit[i].Station < TopProfileMath.StationEps) steps.Add(fit[i].Station);
            if (steps.Count > 0)
            {
                stations = stations.Where(s => !steps.Any(t => Math.Abs(s - t) < 5 * TopProfileMath.StepRun)).ToList();
                foreach (double t in steps) { stations.Add(t - 0.002); stations.Add(t + 0.002); }
            }
            double max = 0, maxAt = 0;
            foreach (double s in stations)
            {
                double? want = TopProfileMath.ElevationAt(fit, s);
                if (want == null) continue;
                if (s < ps - EndSlack || s > pe + EndSlack) continue;
                double have;
                try { have = p.ElevationAt(Math.Clamp(s, ps, pe)); }
                catch { continue; }
                double d = Math.Abs(have - want.Value);
                if (d > max) { max = d; maxAt = s; }
            }
            if (max > 0.005) notes.Add($"Largest difference {max:F3} at sta {maxAt:F2}.");

            double fs = fit[0].Station, fe = fit[^1].Station;
            shortProfile = false;
            if (ps - fs > EndSlack) { shortProfile = true; notes.Add($"'{p.Name}' starts {ps - fs:F2} after the fit (sta {ps:F2}, fit {fs:F2})."); }
            if (fe - pe > EndSlack) { shortProfile = true; notes.Add($"'{p.Name}' ends {fe - pe:F2} before the fit (sta {pe:F2}, fit {fe:F2})."); }
            return max;
        }

        private static void Bump(Dictionary<string, int> d, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            d[key] = d.TryGetValue(key, out int c) ? c + 1 : 1;
        }
    }
}
