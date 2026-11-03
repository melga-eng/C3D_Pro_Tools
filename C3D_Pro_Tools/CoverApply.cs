using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    internal sealed class CoverApplyResult
    {
        /// <summary>The route's pipes after the change, in route order.</summary>
        public readonly List<ObjectId> PipeIds = new List<ObjectId>();
        /// <summary>Structures on the route (existing and new), in route order.</summary>
        public readonly List<ObjectId> StructureIds = new List<ObjectId>();
        public int Reused, Created, Erased, NewStructures, RimsSet, Failed;
        /// <summary>New parts made by copying an existing one (their family is not in the drawing, or their catalog is missing).</summary>
        public int Copied;
        public bool NotedCopy;
    }

    /// <summary>
    /// Writes a <see cref="CoverSolution"/> into the network. Straight sections reuse their original pipes in order,
    /// add copies of the original pipe (same family, size, style, layer) where more are needed and erase the rest;
    /// curved pipes only get new end levels. Gravity mode adds a manhole at every new break. Manhole rims on the route
    /// are set to the surface, and sumps are lowered when a pipe now sits below them.
    /// </summary>
    internal static class CoverApply
    {
        public static CoverApplyResult Apply(Transaction tr, CoverRoute route, CoverSolution solution, CoverOptions o,
            ObjectId structureFamilyId, ObjectId structureSizeId, Editor ed, ObjectId structureTemplateId = default)
        {
            Structure structureTemplate = structureTemplateId.IsNull ? null : tr.GetObject(structureTemplateId, OpenMode.ForRead) as Structure;
            CoverApplyResult result = new CoverApplyResult();
            Network network = (Network)tr.GetObject(route.NetworkId, OpenMode.ForWrite);
            bool gravity = o.Mode == CoverMode.Gravity;

            foreach (RouteSection sec in route.Sections)
            {
                List<ProfilePipe> segments = solution.Pipes.FindAll(p => p.Section == sec.Index);
                if (segments.Count == 0) continue;

                if (sec.Curved)
                {
                    RoutePipe rp = sec.Pipes[0];
                    Pipe pipe = (Pipe)tr.GetObject(rp.Id, OpenMode.ForWrite);
                    Point3d start = pipe.StartPoint, end = pipe.EndPoint;
                    double zStart = rp.Forward ? segments[0].Z0 : segments[0].Z1;
                    double zEnd = rp.Forward ? segments[0].Z1 : segments[0].Z0;
                    pipe.StartPoint = new Point3d(start.X, start.Y, zStart);
                    pipe.EndPoint = new Point3d(end.X, end.Y, zEnd);
                    result.PipeIds.Add(rp.Id);
                    result.Reused++;
                    continue;
                }

                // Free the originals from the section's end structures; the new layout is reconnected below.
                foreach (RoutePipe rp in sec.Pipes)
                {
                    Pipe pipe = (Pipe)tr.GetObject(rp.Id, OpenMode.ForWrite);
                    try { if (!pipe.StartStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.Start); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { if (!pipe.EndStructureId.IsNull) pipe.Disconnect(ConnectorPositionType.End); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }

                List<(Pipe pipe, bool forward)> placed = new List<(Pipe, bool)>();
                for (int i = 0; i < segments.Count; i++)
                {
                    ProfilePipe seg = segments[i];
                    Point3d p0 = sec.PointAt(seg.S0), p1 = sec.PointAt(seg.S1);
                    p0 = new Point3d(p0.X, p0.Y, seg.Z0);
                    p1 = new Point3d(p1.X, p1.Y, seg.Z1);

                    Pipe pipe;
                    bool forward;
                    if (i < sec.Pipes.Count)
                    {
                        pipe = (Pipe)tr.GetObject(sec.Pipes[i].Id, OpenMode.ForWrite);
                        // Gravity pipes are drawn in the flow direction (the route's); dry pipes keep their own direction.
                        forward = gravity || sec.Pipes[i].Forward;
                        result.Reused++;
                    }
                    else
                    {
                        Pipe template = (Pipe)tr.GetObject(seg.Source.Id, OpenMode.ForRead);
                        forward = gravity || seg.Source.Forward;
                        // Like the route's pipe; a copy of it when its family is not in the drawing or its catalog is missing.
                        ObjectId familyId = template.PartFamilyId, sizeId = PartCopy.IsFamily(tr, familyId) ? PipePartCatalog.SizeIdOf(tr, template) : ObjectId.Null;
                        ObjectId newId = PartCopy.Pipe(tr, network, familyId, sizeId, template, forward ? p0 : p1, forward ? p1 : p0, out bool copied);
                        if (copied) result.Copied++;
                        pipe = (Pipe)tr.GetObject(newId, OpenMode.ForWrite);
                        pipe.StyleId = template.StyleId;
                        pipe.Layer = template.Layer;
                        result.Created++;
                    }
                    SetPoints(pipe, forward, p0, p1);
                    placed.Add((pipe, forward));
                    result.PipeIds.Add(pipe.ObjectId);
                }

                for (int i = segments.Count; i < sec.Pipes.Count; i++)
                {
                    tr.GetObject(sec.Pipes[i].Id, OpenMode.ForWrite).Erase();
                    result.Erased++;
                }

                if (!sec.StartStructure.IsNull) Connect(placed[0].pipe, placed[0].forward, true, sec.StartStructure, ed);
                if (!sec.EndStructure.IsNull) Connect(placed[placed.Count - 1].pipe, placed[placed.Count - 1].forward, false, sec.EndStructure, ed);

                if (gravity && placed.Count > 1)
                {
                    if ((structureFamilyId.IsNull || structureSizeId.IsNull) && structureTemplate == null)
                    {
                        ed.WriteMessage($"\n[Warning] No manhole family / size chosen: {placed.Count - 1} new break(s) were left without a manhole.");
                    }
                    else
                    {
                        double rotation = Math.Atan2(sec.P1.Y - sec.P0.Y, sec.P1.X - sec.P0.X);
                        for (int i = 1; i < placed.Count; i++)
                        {
                            ProfilePipe seg = segments[i];
                            AddBreakManhole(tr, network, route, sec, seg.S0, rotation, structureFamilyId, structureSizeId,
                                placed[i - 1], placed[i], Math.Min(segments[i - 1].Z1 - seg.Source.InnerHeight / 2.0, seg.Z0 - seg.Source.InnerHeight / 2.0),
                                result, ed, structureTemplate);
                        }
                    }
                }
            }

            UpdateRouteStructures(tr, route, solution, result, ed);
            return result;
        }

        private static void SetPoints(Pipe pipe, bool forward, Point3d p0, Point3d p1)
        {
            pipe.StartPoint = forward ? p0 : p1;
            pipe.EndPoint = forward ? p1 : p0;
        }

        /// <summary>Connects the pipe's route-entry (or route-exit) end to the structure, putting its 3D points back if Civil 3D moved them.</summary>
        internal static void Connect(Pipe pipe, bool forward, bool atEntry, ObjectId structureId, Editor ed)
        {
            Point3d start = pipe.StartPoint, end = pipe.EndPoint;
            ConnectorPositionType position = atEntry == forward ? ConnectorPositionType.Start : ConnectorPositionType.End;
            try
            {
                NetworkEditOps.ConnectEnd(pipe, position, structureId);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Error] Connecting pipe <{pipe.Name}> to a structure: {ex.Message}");
            }
            if (!pipe.StartPoint.IsEqualTo(start)) pipe.StartPoint = start;
            if (!pipe.EndPoint.IsEqualTo(end)) pipe.EndPoint = end;
        }

        internal static void AddBreakManhole(Transaction tr, Network network, CoverRoute route, RouteSection sec, double station, double rotation,
            ObjectId familyId, ObjectId sizeId, (Pipe pipe, bool forward) upstream, (Pipe pipe, bool forward) downstream, double lowestInvert,
            CoverApplyResult result, Editor ed, Structure template = null)
        {
            try
            {
                Point3d at = sec.PointAt(station);
                double ground = GroundAtStation(route, sec, station);
                // The chosen manhole; when Civil 3D cannot build it (no catalog), a copy of the given structure or the route's own.
                ObjectId templateId = !sec.StartStructure.IsNull ? sec.StartStructure : sec.EndStructure;
                if (template == null && !templateId.IsNull) template = tr.GetObject(templateId, OpenMode.ForRead) as Structure;
                ObjectId structureId = PartCopy.Structure(tr, network, familyId, sizeId, template, new Point3d(at.X, at.Y, double.IsNaN(ground) ? lowestInvert : ground), rotation, out bool copied);
                if (copied && result.Copied++ >= 0 && !result.NotedCopy)
                {
                    result.NotedCopy = true;
                    ed.WriteMessage($"\n[Note] The chosen manhole cannot be built (its family or catalog is missing); new manholes on this route are copies of {template.Name}.");
                }
                Structure st = (Structure)tr.GetObject(structureId, OpenMode.ForWrite);
                if (!double.IsNaN(ground)) st.RimElevation = ground;

                Connect(upstream.pipe, upstream.forward, false, structureId, ed);
                Connect(downstream.pipe, downstream.forward, true, structureId, ed);
                try { st.SumpElevation = lowestInvert; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

                result.NewStructures++;
                result.StructureIds.Add(structureId);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Error] Adding a manhole at station {station:0.00}: {ex.Message}");
                result.Failed++;
            }
        }

        private static void UpdateRouteStructures(Transaction tr, CoverRoute route, CoverSolution solution, CoverApplyResult result, Editor ed)
        {
            foreach (RouteStructure rs in route.Structures)
            {
                try
                {
                    Structure st = (Structure)tr.GetObject(rs.Id, OpenMode.ForWrite);
                    if (!double.IsNaN(rs.Ground))
                    {
                        st.RimElevation = rs.Ground;
                        result.RimsSet++;
                    }

                    double lowest = double.MaxValue;
                    ProfilePipe arriving = rs.SectionIn >= 0 ? solution.Pipes.FindLast(p => p.Section == rs.SectionIn) : null;
                    ProfilePipe leaving = rs.SectionOut >= 0 ? solution.Pipes.Find(p => p.Section == rs.SectionOut) : null;
                    if (arriving != null) lowest = Math.Min(lowest, arriving.Z1 - arriving.Source.InnerHeight / 2.0);
                    if (leaving != null) lowest = Math.Min(lowest, leaving.Z0 - leaving.Source.InnerHeight / 2.0);
                    if (lowest < double.MaxValue && st.SumpElevation > lowest) st.SumpElevation = lowest;

                    result.StructureIds.Add(rs.Id);
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Updating structure <{rs.Name}>: {ex.Message}");
                    result.Failed++;
                }
            }
        }

        private static double GroundAtStation(CoverRoute route, RouteSection sec, double s)
        {
            for (int r = sec.FirstRecord; r <= sec.LastRecord; r++)
            {
                if (Math.Abs(route.Records[r].S - s) < 1e-6) return route.Records[r].Ground;
            }
            return double.NaN;
        }
    }
}
