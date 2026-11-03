using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    // The solver-facing members of these classes are in CoverRoute.Model.cs.

    internal sealed partial class RoutePipe
    {
        public ObjectId Id;
        public Point3d Entry, Exit;
    }

    internal sealed partial class RouteSection
    {
        public Point3d P0, P1;
        /// <summary>Structure the section starts / ends at (a manhole on the route), or null at a plain joint.</summary>
        public ObjectId StartStructure, EndStructure;
        /// <summary>For a new curved section drawn along a path: its arc in plan.</summary>
        public ArcInfo? Arc;

        public Point3d PointAt(double s)
        {
            double t = Length < 1e-9 ? 0.0 : (s - S0) / Length;
            return new Point3d(P0.X + (P1.X - P0.X) * t, P0.Y + (P1.Y - P0.Y) * t, 0.0);
        }
    }

    internal sealed partial class RouteRecord
    {
        public Point2d Plan;
    }

    internal sealed partial class RouteCrossing
    {
        public ObjectId PipeId;
    }

    internal sealed class RouteStructure
    {
        /// <summary>Null for a structure the route will add (a new path).</summary>
        public ObjectId Id;
        public string Name;
        public Point3d Location;
        public double Ground;
        public double Rotation;
        /// <summary>Sections arriving at / leaving the structure along the route (-1 at the route ends).</summary>
        public int SectionIn = -1, SectionOut = -1;
    }

    internal sealed partial class CoverRoute
    {
        // Pipes whose directions differ by less than this (and stay this close to the line) form one straight section.
        private const double CollinearAngle = 0.5 * Math.PI / 180.0;
        private const double CollinearOffset = 0.02;

        public readonly List<RouteStructure> Structures = new List<RouteStructure>();
        public ObjectId NetworkId;

        /// <summary>True for a route of pipes still to be drawn (along a path); its pipes have no ObjectId yet.</summary>
        public bool IsNew => Pipes.Count > 0 && Pipes[0].Id.IsNull;

        /// <summary>
        /// The pipe's outermost height (outside of wall or encasement). Parts drawn with no wall (duct banks whose catalog size is
        /// the encasement) report an outer height of 0, so it is rebuilt from the inner height and wall.
        /// </summary>
        public static double OuterHeightOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterHeight; } catch (Exception) { }
            try { inner = pipe.InnerHeight; } catch (Exception) { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch (Exception) { }
            return Math.Max(outer, inner + 2 * wall);
        }

        /// <summary>The pipe's outermost width in plan, rebuilt from the inner width and wall like <see cref="OuterHeightOf"/>.</summary>
        public static double OuterWidthOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterDiameterOrWidth; } catch (Exception) { }
            try { inner = pipe.InnerDiameterOrWidth; } catch (Exception) { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch (Exception) { }
            return Math.Max(outer, inner + 2 * wall);
        }

        public static CoverRoute Build(Transaction tr, PipeRunBuilder builder, List<RunStep> steps, Autodesk.Civil.DatabaseServices.Surface surface, double spacing)
        {
            CoverRoute route = new CoverRoute();
            double s = 0.0;
            Point3d previousExit = Point3d.Origin;
            RouteSection section = null;

            for (int i = 0; i < steps.Count; i++)
            {
                RunStep step = steps[i];
                RunPipe rp = builder.Pipes[step.Pipe];
                Pipe pipe = (Pipe)tr.GetObject(rp.Id, OpenMode.ForRead);
                if (i == 0) route.NetworkId = pipe.NetworkId;
                route.IsReference |= pipe.IsReferenceObject || pipe.IsReferenceSubObject;

                Point3d entry = step.Forward ? rp.Start : rp.End;
                Point3d exit = step.Forward ? rp.End : rp.Start;
                bool curved = rp.Arc.HasValue;
                double length = curved ? PathLength(PipeArcGeometry.SamplePath(entry, exit, rp.Arc, 0.25, 4000)) : PlanDistance(entry, exit);

                RunHub entryHub = builder.HubAt(step, true);
                bool manhole = entryHub != null && tr.GetObject(entryHub.Id, OpenMode.ForRead) is Structure;
                if (i == 0 && manhole) route.AddStructure(tr, entryHub.Id, -1, 0, surface);
                if (i > 0) s += PlanDistance(previousExit, entry);

                bool newSection = section == null || manhole || curved || section.Curved ||
                                  Math.Abs(pipe.InnerHeight - section.InnerHeight) > 1e-6 || Math.Abs(OuterHeightOf(pipe) - section.OuterHeight) > 1e-6 || Math.Abs(OuterWidthOf(pipe) - section.OuterWidth) > 1e-6 ||
                                  !IsCollinear(section, entry, exit);
                if (newSection)
                {
                    RouteSection previous = section;
                    section = new RouteSection
                    {
                        Index = route.Sections.Count, Curved = curved, S0 = s, P0 = entry,
                        InnerHeight = pipe.InnerHeight, OuterHeight = OuterHeightOf(pipe), OuterWidth = OuterWidthOf(pipe),
                        StartStructure = manhole ? entryHub.Id : ObjectId.Null,
                        AfterManhole = manhole && previous != null
                    };
                    if (manhole && previous != null)
                    {
                        previous.EndStructure = entryHub.Id;
                        route.Structures[route.Structures.Count - 1].SectionOut = section.Index;
                    }
                    route.Sections.Add(section);
                }
                section.S1 = s + length;
                section.P1 = exit;

                route.Pipes.Add(new RoutePipe
                {
                    Id = rp.Id, Name = rp.Name, Forward = step.Forward, Curved = curved, Section = section.Index,
                    S0 = s, S1 = s + length, Z0 = entry.Z, Z1 = exit.Z, Entry = entry, Exit = exit,
                    InnerHeight = pipe.InnerHeight, OuterHeight = OuterHeightOf(pipe)
                });
                section.Pipes.Add(route.Pipes[route.Pipes.Count - 1]);

                // A manhole at this pipe's exit is recorded now; the next section links to it as it starts.
                RunHub exitHub = builder.HubAt(step, false);
                if (exitHub != null && tr.GetObject(exitHub.Id, OpenMode.ForRead) is Structure)
                {
                    route.AddStructure(tr, exitHub.Id, section.Index, -1, surface);
                    if (i == steps.Count - 1) section.EndStructure = exitHub.Id;
                }

                s += length;
                previousExit = exit;
            }

            route.BuildRecords(builder, steps, surface, spacing);
            return route;
        }

        private void AddStructure(Transaction tr, ObjectId id, int sectionIn, int sectionOut, Autodesk.Civil.DatabaseServices.Surface surface)
        {
            if (Structures.Count > 0 && Structures[Structures.Count - 1].Id == id) return;
            Structure st = (Structure)tr.GetObject(id, OpenMode.ForRead);
            Structures.Add(new RouteStructure
            {
                Id = id, Name = st.Name, Location = st.Location, Rotation = st.Rotation, SectionIn = sectionIn, SectionOut = sectionOut,
                Ground = GroundAt(surface, st.Location.X, st.Location.Y)
            });
        }

        // ---------------- New pipes along a path ----------------

        /// <summary>How the pipes drawn along a path are laid out before their levels are optimised.</summary>
        internal sealed class PathLayout
        {
            public double InnerHeight, OuterHeight;
            /// <summary>Depth of the first guess (centreline below ground); only the solver's starting grid depends on it.</summary>
            public double InitialDepth = 1.0;
            public bool StructuresAtBends, StructuresAtEnds;
            /// <summary>Longest distance between structures (0 = no limit): longer straights and arcs get structures in between.</summary>
            public double MaxStructureSpacing;
            /// <summary>Existing structures the path starts / ends in (connected instead of adding new ones), or null.</summary>
            public ObjectId StartStructure, EndStructure;
        }

        /// <summary>A route of new pipes along a path: one section per straight run or arc, with structures where the layout asks for them.</summary>
        public static CoverRoute BuildFromPath(Transaction tr, PathGeometry path, Autodesk.Civil.DatabaseServices.Surface surface, double spacing, PathLayout layout)
        {
            CoverRoute route = new CoverRoute();

            // Pieces: collinear straights joined, each arc on its own; long pieces split at the structure spacing.
            List<(Point2d p0, Point2d p1, ArcInfo? arc, double length, bool structureBefore)> pieces = new List<(Point2d, Point2d, ArcInfo?, double, bool)>();
            foreach (PathSegment seg in path.Segments)
            {
                if (pieces.Count > 0 && seg.Arc == null && pieces[pieces.Count - 1].arc == null)
                {
                    var last = pieces[pieces.Count - 1];
                    Vector2d a = last.p1 - last.p0, b = seg.P1 - seg.P0;
                    double across = Math.Abs(a.GetNormal().X * (seg.P1 - last.p0).Y - a.GetNormal().Y * (seg.P1 - last.p0).X);
                    if (a.GetAngleTo(b) <= CollinearAngle && across <= CollinearOffset)
                    {
                        pieces[pieces.Count - 1] = (last.p0, seg.P1, null, last.p0.GetDistanceTo(seg.P1), last.structureBefore);
                        continue;
                    }
                }
                pieces.Add((seg.P0, seg.P1, seg.Arc, seg.Length, pieces.Count > 0 && layout.StructuresAtBends));
            }
            if (layout.MaxStructureSpacing > 0)
            {
                var split = new List<(Point2d, Point2d, ArcInfo?, double, bool)>();
                foreach (var p in pieces)
                {
                    int n = Math.Max(1, (int)Math.Ceiling(p.length / layout.MaxStructureSpacing - 1e-9));
                    for (int i = 0; i < n; i++)
                    {
                        Point2d a = PieceAt(p.p0, p.p1, p.arc, (double)i / n), b = PieceAt(p.p0, p.p1, p.arc, (double)(i + 1) / n);
                        ArcInfo? arc = p.arc.HasValue ? new ArcInfo { Center = p.arc.Value.Center, Radius = p.arc.Value.Radius, Mid = PieceAt(p.p0, p.p1, p.arc, (i + 0.5) / n) } : (ArcInfo?)null;
                        split.Add((a, b, arc, p.length / n, i == 0 ? p.structureBefore : true));
                    }
                }
                pieces = split;
            }

            double s = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                RouteSection sec = new RouteSection
                {
                    Index = i, Curved = p.arc.HasValue, Arc = p.arc, S0 = s, S1 = s + p.length,
                    P0 = new Point3d(p.p0.X, p.p0.Y, 0), P1 = new Point3d(p.p1.X, p.p1.Y, 0),
                    InnerHeight = layout.InnerHeight, OuterHeight = layout.OuterHeight, AfterManhole = p.structureBefore
                };
                route.Sections.Add(sec);
                double rotation = Math.Atan2(p.p1.Y - p.p0.Y, p.p1.X - p.p0.X);
                if (i == 0 && (layout.StructuresAtEnds || !layout.StartStructure.IsNull))
                    route.Structures.Add(NewOrExisting(tr, layout.StartStructure, sec.P0, rotation, -1, 0, surface));
                if (p.structureBefore)
                    route.Structures.Add(NewOrExisting(tr, ObjectId.Null, sec.P0, rotation, i - 1, i, surface));
                s += p.length;
            }
            RouteSection lastSec = route.Sections[route.Sections.Count - 1];
            if (layout.StructuresAtEnds || !layout.EndStructure.IsNull)
                route.Structures.Add(NewOrExisting(tr, layout.EndStructure, lastSec.P1, Math.Atan2(lastSec.P1.Y - lastSec.P0.Y, lastSec.P1.X - lastSec.P0.X), lastSec.Index, -1, surface));

            foreach (RouteSection sec in route.Sections)
            {
                sec.FirstRecord = route.Records.Count;
                List<Point3d> pts = sec.Curved ? PipeArcGeometry.SamplePath(sec.P0, sec.P1, sec.Arc, spacing, 4000) : null;
                if (pts != null)
                {
                    double along = sec.S0;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        if (i > 0) along += PlanDistance(pts[i - 1], pts[i]);
                        route.AddRecord(sec, i == pts.Count - 1 ? sec.S1 : along, new Point2d(pts[i].X, pts[i].Y), false, surface);
                    }
                }
                else
                {
                    int count = Math.Max(1, (int)Math.Ceiling(sec.Length / spacing - 1e-9));
                    for (int i = 0; i <= count; i++)
                    {
                        double st = i == count ? sec.S1 : sec.S0 + i * spacing;
                        Point3d pt = sec.PointAt(st);
                        route.AddRecord(sec, st, new Point2d(pt.X, pt.Y), i > 0 && i < count, surface);
                    }
                }
                sec.LastRecord = route.Records.Count - 1;
            }
            route.FillMissingGround();

            // One placeholder pipe per section: its size, and a first-guess level a fixed depth below the ground.
            foreach (RouteSection sec in route.Sections)
            {
                double z0 = route.Records[sec.FirstRecord].Ground - layout.InitialDepth, z1 = route.Records[sec.LastRecord].Ground - layout.InitialDepth;
                RoutePipe rp = new RoutePipe
                {
                    Id = ObjectId.Null, Name = "New pipe", Forward = true, Curved = sec.Curved, Section = sec.Index,
                    S0 = sec.S0, S1 = sec.S1, Z0 = z0, Z1 = z1,
                    Entry = new Point3d(sec.P0.X, sec.P0.Y, z0), Exit = new Point3d(sec.P1.X, sec.P1.Y, z1),
                    InnerHeight = layout.InnerHeight, OuterHeight = layout.OuterHeight
                };
                route.Pipes.Add(rp);
                sec.Pipes.Add(rp);
            }
            return route;
        }

        private static RouteStructure NewOrExisting(Transaction tr, ObjectId existing, Point3d at, double rotation, int sectionIn, int sectionOut,
            Autodesk.Civil.DatabaseServices.Surface surface)
        {
            if (!existing.IsNull && tr.GetObject(existing, OpenMode.ForRead) is Structure st)
                return new RouteStructure { Id = existing, Name = st.Name, Location = st.Location, Rotation = st.Rotation, SectionIn = sectionIn, SectionOut = sectionOut, Ground = GroundAt(surface, st.Location.X, st.Location.Y) };
            return new RouteStructure { Id = ObjectId.Null, Name = "New structure", Location = at, Rotation = rotation, SectionIn = sectionIn, SectionOut = sectionOut, Ground = GroundAt(surface, at.X, at.Y) };
        }

        /// <summary>The point a fraction <paramref name="t"/> of the way along a straight piece or an arc.</summary>
        private static Point2d PieceAt(Point2d p0, Point2d p1, ArcInfo? arc, double t)
        {
            if (!arc.HasValue) return p0 + (p1 - p0) * t;
            ArcInfo a = arc.Value;
            bool clockwise = PipeArcGeometry.IsClockwise(p0, a.Mid, p1);
            double start = Math.Atan2(p0.Y - a.Center.Y, p0.X - a.Center.X);
            double sweep = Math.Abs((p0 - a.Center).GetAngleTo(a.Mid - a.Center)) + Math.Abs((a.Mid - a.Center).GetAngleTo(p1 - a.Center));
            double angle = start + (clockwise ? -1 : 1) * sweep * t;
            return new Point2d(a.Center.X + a.Radius * Math.Cos(angle), a.Center.Y + a.Radius * Math.Sin(angle));
        }

        // ---------------- Crossing pipes ----------------

        /// <summary>
        /// Finds the gravity and pressure pipes the route must clear vertically (not those in <paramref name="exclude"/>, nor
        /// pipes meeting the route at one of its structures): every pipe crossing it in plan at any angle, with a station
        /// exactly where the centrelines cross, and every pipe whose outside comes within <paramref name="horizontalClearance"/>
        /// of the route's outside, over the whole stretch where it does (shallow-skew crossings and pipes alongside).
        /// </summary>
        public void FindCrossings(Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument doc, HashSet<ObjectId> exclude, double horizontalClearance = 0)
        {
            HashSet<ObjectId> routeStructures = new HashSet<ObjectId>();
            foreach (RouteStructure rs in Structures) if (!rs.Id.IsNull) routeStructures.Add(rs.Id);

            // The route in plan, as stationed polylines per section.
            List<(RouteSection sec, List<Point3d> pts, List<double> st)> paths = new List<(RouteSection, List<Point3d>, List<double>)>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, widest = 0;
            foreach (RouteSection sec in Sections)
            {
                List<Point3d> pts = new List<Point3d>();
                List<double> st = new List<double>();
                for (int r = sec.FirstRecord; r <= sec.LastRecord; r++)
                {
                    pts.Add(new Point3d(Records[r].Plan.X, Records[r].Plan.Y, 0));
                    st.Add(Records[r].S);
                    minX = Math.Min(minX, Records[r].Plan.X); maxX = Math.Max(maxX, Records[r].Plan.X);
                    minY = Math.Min(minY, Records[r].Plan.Y); maxY = Math.Max(maxY, Records[r].Plan.Y);
                }
                paths.Add((sec, pts, st));
                widest = Math.Max(widest, sec.PlanWidth);
            }

            List<(ObjectId id, string name, List<Point3d> path, double halfHeight, double halfWidth)> others = new List<(ObjectId, string, List<Point3d>, double, double)>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                {
                    if (exclude.Contains(id) || !(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                    if (routeStructures.Contains(p.StartStructureId) || routeStructures.Contains(p.EndStructureId)) continue;
                    ArcInfo? arc = null;
                    if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
                    others.Add((id, p.Name, PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 2.0, 200), OuterHeightOf(p) / 2.0, OuterWidthOf(p) / 2.0));
                }
            }
            foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                {
                    if (exclude.Contains(id) || !(tr.GetObject(id, OpenMode.ForRead) is PressurePipe p)) continue;
                    ArcInfo? arc = null;
                    if (p.IsCurve && PipeArcGeometry.TryGetPressureArc(p, out ArcInfo a)) arc = a;
                    others.Add((id, p.Name, PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 2.0, 200), p.OuterDiameter / 2.0, p.OuterDiameter / 2.0));
                }
            }

            // Only pipes coming within reach of the route's extents need a closer look.
            double margin = 5 + widest + horizontalClearance;
            others = others.FindAll(o => o.path.Exists(q => q.X >= minX - margin - o.halfWidth && q.X <= maxX + margin + o.halfWidth && q.Y >= minY - margin - o.halfWidth && q.Y <= maxY + margin + o.halfWidth));

            // 1. Where the centrelines cross in plan, at any angle (shallow-skew crossings were once skipped as running alongside).
            foreach ((ObjectId id, string name, List<Point3d> path, double halfHeight, double _) in others)
            {
                double total = 0;
                for (int j = 1; j < path.Count; j++) total += PlanDistance(path[j - 1], path[j]);
                double along = 0;
                for (int j = 1; j < path.Count; j++)
                {
                    double segLength = PlanDistance(path[j - 1], path[j]);
                    foreach ((RouteSection sec, List<Point3d> pts, List<double> st) in paths)
                    {
                        for (int k = 1; k < pts.Count; k++)
                        {
                            if (!CrossingGeometry.Intersect(pts[k - 1].X, pts[k - 1].Y, pts[k].X, pts[k].Y, path[j - 1].X, path[j - 1].Y, path[j].X, path[j].Y, out double tRoute, out double tOther)) continue;
                            // A pipe ending on the route (a branch) is not a crossing.
                            double fraction = (along + tOther * segLength) / Math.Max(1e-9, total);
                            if (fraction < 0.01 || fraction > 0.99) continue;
                            double z = path[j - 1].Z + (path[j].Z - path[j - 1].Z) * tOther;
                            double s = st[k - 1] + (st[k] - st[k - 1]) * tRoute;
                            if (Crossings.Exists(c => c.PipeId == id && Math.Abs(c.S - s) < 0.5)) continue;
                            Crossings.Add(new RouteCrossing { PipeId = id, Name = name, S = s, Top = z + halfHeight, Bottom = z - halfHeight });
                        }
                    }
                    along += segLength;
                }
            }

            // Each such crossing gets its own station, so the clearance is checked exactly where the pipes cross.
            foreach (RouteCrossing c in Crossings)
            {
                RouteSection sec = Sections.Find(x => c.S >= x.S0 - 1e-6 && c.S <= x.S1 + 1e-6);
                if (sec == null) continue;
                int at = sec.FirstRecord;
                while (at <= sec.LastRecord && Records[at].S < c.S - 1e-6) at++;
                if (at <= sec.LastRecord && Math.Abs(Records[at].S - c.S) <= 1e-6) { Records[at].AddCrossing(c, c.Top, c.Bottom); continue; }
                if (at > sec.LastRecord || at == sec.FirstRecord) { Records[Math.Min(at, sec.LastRecord)].AddCrossing(c, c.Top, c.Bottom); continue; }
                RouteRecord before = Records[at - 1], after = Records[Math.Min(at, sec.LastRecord)];
                double t = (c.S - before.S) / Math.Max(1e-9, after.S - before.S);
                Records.Insert(at, new RouteRecord
                {
                    S = c.S, Section = sec.Index, CanBreak = false,
                    Plan = new Point2d(before.Plan.X + (after.Plan.X - before.Plan.X) * t, before.Plan.Y + (after.Plan.Y - before.Plan.Y) * t),
                    Ground = before.Ground + (after.Ground - before.Ground) * t
                });
                Records[at].AddCrossing(c, c.Top, c.Bottom);
                foreach (RouteSection other in Sections)
                {
                    if (other.Index > sec.Index) { other.FirstRecord++; other.LastRecord++; }
                }
                sec.LastRecord++;
            }

            // 2. Every station where a pipe's outside comes within reach of the route's: the whole width of a skewed
            //    crossing, the whole length of a pipe alongside.
            List<(double X, double Y)> plan = Records.ConvertAll(r => (r.Plan.X, r.Plan.Y));
            foreach ((ObjectId id, string name, List<Point3d> path, double halfHeight, double halfWidth) in others)
            {
                List<RouteCrossing> ofThisPipe = Crossings.FindAll(c => c.PipeId == id);
                MarkCrossingBands(plan, path.ConvertAll(q => (q.X, q.Y, q.Z)), halfWidth, halfHeight, horizontalClearance, ofThisPipe,
                    () => new RouteCrossing { PipeId = id, Name = name });
            }
            Crossings.Sort((a, b) => a.S.CompareTo(b.S));
        }

        private static bool IsCollinear(RouteSection section, Point3d entry, Point3d exit)
        {
            Vector2d a = new Vector2d(section.P1.X - section.P0.X, section.P1.Y - section.P0.Y);
            Vector2d b = new Vector2d(exit.X - entry.X, exit.Y - entry.Y);
            if (a.Length < 1e-9 || b.Length < 1e-9) return false;
            if (a.GetAngleTo(b) > CollinearAngle) return false;
            Vector2d u = a.GetNormal();
            Vector2d toExit = new Vector2d(exit.X - section.P0.X, exit.Y - section.P0.Y);
            return Math.Abs(u.X * toExit.Y - u.Y * toExit.X) <= CollinearOffset;
        }

        private void BuildRecords(PipeRunBuilder builder, List<RunStep> steps, Autodesk.Civil.DatabaseServices.Surface surface, double spacing)
        {
            foreach (RouteSection sec in Sections)
            {
                sec.FirstRecord = Records.Count;
                if (sec.Curved)
                {
                    RoutePipe rp = sec.Pipes[0];
                    RunPipe run = builder.Pipes[builder.IndexOf(rp.Id)];
                    List<Point3d> path = PipeArcGeometry.SamplePath(rp.Entry, rp.Exit, run.Arc, spacing, 4000);
                    double along = sec.S0;
                    for (int i = 0; i < path.Count; i++)
                    {
                        if (i > 0) along += PlanDistance(path[i - 1], path[i]);
                        AddRecord(sec, i == path.Count - 1 ? sec.S1 : along, new Point2d(path[i].X, path[i].Y), false, surface);
                    }
                }
                else
                {
                    int count = Math.Max(1, (int)Math.Ceiling(sec.Length / spacing - 1e-9));
                    for (int i = 0; i <= count; i++)
                    {
                        double st = i == count ? sec.S1 : sec.S0 + i * spacing;
                        Point3d p = sec.PointAt(st);
                        AddRecord(sec, st, new Point2d(p.X, p.Y), i > 0 && i < count, surface);
                    }
                }
                sec.LastRecord = Records.Count - 1;
            }
            FillMissingGround();
        }

        private void AddRecord(RouteSection sec, double s, Point2d plan, bool canBreak, Autodesk.Civil.DatabaseServices.Surface surface)
        {
            double g = GroundAt(surface, plan.X, plan.Y);
            if (double.IsNaN(g)) MissingGround++;
            Records.Add(new RouteRecord { S = s, Plan = plan, Ground = g, Section = sec.Index, CanBreak = canBreak });
        }

        /// <summary>Stations off the surface take the ground linearly interpolated from their nearest sampled neighbours.</summary>
        private void FillMissingGround()
        {
            for (int i = 0; i < Records.Count; i++)
            {
                if (!double.IsNaN(Records[i].Ground)) continue;
                int before = i - 1, after = i + 1;
                while (before >= 0 && double.IsNaN(Records[before].Ground)) before--;
                while (after < Records.Count && double.IsNaN(Records[after].Ground)) after++;
                if (before >= 0 && after < Records.Count)
                {
                    double t = (Records[i].S - Records[before].S) / Math.Max(1e-9, Records[after].S - Records[before].S);
                    Records[i].Ground = Records[before].Ground + (Records[after].Ground - Records[before].Ground) * t;
                }
                else if (before >= 0) Records[i].Ground = Records[before].Ground;
                else if (after < Records.Count) Records[i].Ground = Records[after].Ground;
            }
        }

        public static double GroundAt(Autodesk.Civil.DatabaseServices.Surface surface, double x, double y)
        {
            try { return surface.FindElevationAtXY(x, y); }
            catch (Exception) { return double.NaN; }
        }

        private static double PathLength(List<Point3d> path)
        {
            double len = 0;
            for (int i = 1; i < path.Count; i++) len += PlanDistance(path[i - 1], path[i]);
            return len;
        }

        private static double PlanDistance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
