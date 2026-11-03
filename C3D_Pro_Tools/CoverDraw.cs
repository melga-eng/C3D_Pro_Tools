using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    /// <summary>Where and with which parts the pipes along a path are drawn.</summary>
    internal sealed class DrawTarget
    {
        public ObjectId NetworkId;
        public ObjectId PipeFamilyId, PipeSizeId;
        /// <summary>Structures at bends, ends, spacing breaks and (gravity) new breaks; null = none are added.</summary>
        public ObjectId StructureFamilyId, StructureSizeId;
        /// <summary>Parts in the drawing to copy when Civil 3D cannot build the chosen ones (family not in the drawing, or catalog missing); null = none.</summary>
        public ObjectId PipeTemplateId, StructureTemplateId;
    }

    /// <summary>
    /// Draws the solved layout of a new route (<see cref="CoverRoute.BuildFromPath"/>) into a network: one pipe per solved
    /// piece (curved along the path's arcs), the route's structures, a manhole at every new break on gravity routes, rims on
    /// the surface and sumps at the lowest invert. Structures the path started or ended in are connected, not duplicated.
    /// </summary>
    internal static class CoverDraw
    {
        public static CoverApplyResult Apply(Transaction tr, CoverRoute route, CoverSolution solution, CoverOptions o, DrawTarget t, Editor ed)
        {
            CoverApplyResult result = new CoverApplyResult();
            Network network = (Network)tr.GetObject(t.NetworkId, OpenMode.ForWrite);
            bool gravity = o.Mode == CoverMode.Gravity;
            Pipe pipeTemplate = t.PipeTemplateId.IsNull ? null : tr.GetObject(t.PipeTemplateId, OpenMode.ForRead) as Pipe;
            Structure structureTemplate = t.StructureTemplateId.IsNull ? null : tr.GetObject(t.StructureTemplateId, OpenMode.ForRead) as Structure;
            bool canAddStructures = (!t.StructureFamilyId.IsNull && !t.StructureSizeId.IsNull) || structureTemplate != null;

            Dictionary<RouteStructure, ObjectId> structureIds = new Dictionary<RouteStructure, ObjectId>();
            int skippedStructures = 0;
            foreach (RouteStructure rs in route.Structures)
            {
                if (!rs.Id.IsNull) { structureIds[rs] = rs.Id; continue; }
                if (!canAddStructures) { skippedStructures++; continue; }
                try
                {
                    double z = double.IsNaN(rs.Ground) ? rs.Location.Z : rs.Ground;
                    ObjectId id = PartCopy.Structure(tr, network, t.StructureFamilyId, t.StructureSizeId, structureTemplate, new Point3d(rs.Location.X, rs.Location.Y, z), rs.Rotation, out bool copied);
                    if (copied) result.Copied++;
                    structureIds[rs] = id;
                    result.NewStructures++;
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Adding a structure at {rs.Location.X:0.00}, {rs.Location.Y:0.00}: {ex.Message}");
                    result.Failed++;
                }
            }
            if (skippedStructures > 0)
                ed.WriteMessage($"\n[Warning] No structure family / size chosen: {skippedStructures} structure(s) of the layout were not added.");

            foreach (RouteSection sec in route.Sections)
            {
                List<ProfilePipe> segments = solution.Pipes.FindAll(p => p.Section == sec.Index);
                List<(Pipe pipe, bool forward)> placed = new List<(Pipe, bool)>();
                // The solved segment each placed pipe belongs to (an arc drawn as copies gives several pipes for one).
                List<int> segmentOf = new List<int>();
                int failedHere = 0;
                for (int si = 0; si < segments.Count; si++)
                {
                    ProfilePipe seg = segments[si];
                    try
                    {
                        ObjectId id = ObjectId.Null;
                        bool copied = false;
                        if (sec.Curved && sec.Arc.HasValue)
                        {
                            Point3d p0 = new Point3d(sec.P0.X, sec.P0.Y, seg.Z0), p1 = new Point3d(sec.P1.X, sec.P1.Y, seg.Z1);
                            bool clockwise = PipeArcGeometry.IsClockwise(new Point2d(sec.P0.X, sec.P0.Y), sec.Arc.Value.Mid, new Point2d(sec.P1.X, sec.P1.Y));
                            try
                            {
                                if (!PartCopy.IsFamily(tr, t.PipeFamilyId)) throw new InvalidOperationException("no family to build a curved pipe from");
                                using (PipeCurveGeometry geom = new PipeCurveGeometry(p0, sec.Arc.Value.Center, p1, clockwise))
                                    id = network.AddCurvePipe(t.PipeFamilyId, t.PipeSizeId, geom, false);
                            }
                            catch (Exception) when (pipeTemplate != null)
                            {
                                // A copied pipe cannot be bent: the arc is drawn as copies along it (within 10 mm), joined end to end.
                                foreach ((Point3d a, Point3d b) in Chords(sec.Arc.Value.Center, p0, p1, clockwise, 0.01))
                                {
                                    ObjectId piece = PartCopy.CopyPipe(tr, network, pipeTemplate, a, b);
                                    placed.Add(((Pipe)tr.GetObject(piece, OpenMode.ForWrite), true));
                                    segmentOf.Add(si);
                                    result.PipeIds.Add(piece);
                                    result.Created++;
                                    result.Copied++;
                                }
                                ed.WriteMessage($"\n[Note] The arc at station {seg.S0:0.00} was drawn as straight copies of {pipeTemplate.Name}: the pipe's family or catalog is missing.");
                                continue;
                            }
                        }
                        else
                        {
                            Point3d a = sec.PointAt(seg.S0), b = sec.PointAt(seg.S1);
                            id = PartCopy.Pipe(tr, network, t.PipeFamilyId, t.PipeSizeId, pipeTemplate, new Point3d(a.X, a.Y, seg.Z0), new Point3d(b.X, b.Y, seg.Z1), out copied);
                        }
                        if (id.IsNull) throw new InvalidOperationException("Civil 3D did not return the new pipe.");
                        Pipe pipe = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
                        placed.Add((pipe, true));
                        segmentOf.Add(si);
                        result.PipeIds.Add(id);
                        result.Created++;
                        if (copied) result.Copied++;
                    }
                    catch (Exception ex)
                    {
                        ed.WriteMessage($"\n[Error] Drawing a pipe at station {seg.S0:0.00}: {ex.Message}");
                        result.Failed++;
                        failedHere++;
                    }
                }
                if (placed.Count == 0 || failedHere > 0) continue;

                RouteStructure atStart = route.Structures.Find(x => x.SectionOut == sec.Index);
                RouteStructure atEnd = route.Structures.Find(x => x.SectionIn == sec.Index);
                if (atStart != null && structureIds.TryGetValue(atStart, out ObjectId sId)) CoverApply.Connect(placed[0].pipe, true, true, sId, ed);
                if (atEnd != null && structureIds.TryGetValue(atEnd, out ObjectId eId)) CoverApply.Connect(placed[placed.Count - 1].pipe, true, false, eId, ed);

                if (gravity && placed.Count > 1 && canAddStructures)
                {
                    double rotation = Math.Atan2(sec.P1.Y - sec.P0.Y, sec.P1.X - sec.P0.X);
                    for (int i = 1; i < placed.Count; i++)
                    {
                        // Breaks between solved segments only; copies along one arc stay a plain run.
                        if (segmentOf[i] == segmentOf[i - 1]) continue;
                        ProfilePipe seg = segments[segmentOf[i]], before = segments[segmentOf[i - 1]];
                        CoverApply.AddBreakManhole(tr, network, route, sec, seg.S0, rotation, t.StructureFamilyId, t.StructureSizeId,
                            placed[i - 1], placed[i], Math.Min(before.Z1 - seg.Source.InnerHeight / 2.0, seg.Z0 - seg.Source.InnerHeight / 2.0), result, ed, structureTemplate);
                    }
                }
            }

            // Rims on the ground, sumps at the lowest invert reaching each structure.
            foreach (KeyValuePair<RouteStructure, ObjectId> kv in structureIds)
            {
                RouteStructure rs = kv.Key;
                try
                {
                    Structure st = (Structure)tr.GetObject(kv.Value, OpenMode.ForWrite);
                    if (!double.IsNaN(rs.Ground)) { st.RimElevation = rs.Ground; result.RimsSet++; }
                    double lowest = double.MaxValue;
                    ProfilePipe arriving = rs.SectionIn >= 0 ? solution.Pipes.FindLast(p => p.Section == rs.SectionIn) : null;
                    ProfilePipe leaving = rs.SectionOut >= 0 ? solution.Pipes.Find(p => p.Section == rs.SectionOut) : null;
                    if (arriving != null) lowest = Math.Min(lowest, arriving.Z1 - arriving.Source.InnerHeight / 2.0);
                    if (leaving != null) lowest = Math.Min(lowest, leaving.Z0 - leaving.Source.InnerHeight / 2.0);
                    if (lowest < double.MaxValue && (rs.Id.IsNull || st.SumpElevation > lowest))
                    {
                        st.ControlSumpBy = StructureControlSumpType.ByElevation;
                        st.SumpElevation = lowest;
                    }
                    result.StructureIds.Add(kv.Value);
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Updating structure <{rs.Name}>: {ex.Message}");
                    result.Failed++;
                }
            }
            return result;
        }

        /// <summary>Straight pieces along a plan arc from a to b, none farther than <paramref name="sagitta"/> from it; levels shared out by length.</summary>
        private static List<(Point3d a, Point3d b)> Chords(Point2d center, Point3d a, Point3d b, bool clockwise, double sagitta)
        {
            Vector2d ra = new Point2d(a.X, a.Y) - center, rb = new Point2d(b.X, b.Y) - center;
            double radius = ra.Length;
            double sweep = Math.Atan2(ra.X * rb.Y - ra.Y * rb.X, ra.DotProduct(rb));
            if (clockwise) sweep = -sweep;
            if (sweep < 0) sweep += 2 * Math.PI;
            int n = Math.Max(1, (int)Math.Ceiling(sweep / (2 * Math.Acos(Math.Max(-1.0, 1 - sagitta / radius)))));
            double start = Math.Atan2(ra.Y, ra.X), turn = clockwise ? -1 : 1;
            List<(Point3d, Point3d)> pieces = new List<(Point3d, Point3d)>();
            Point3d prev = a;
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n, ang = start + turn * sweep * t;
                Point3d next = i == n ? b : new Point3d(center.X + radius * Math.Cos(ang), center.Y + radius * Math.Sin(ang), a.Z + (b.Z - a.Z) * t);
                pieces.Add((prev, next));
                prev = next;
            }
            return pieces;
        }
    }
}
