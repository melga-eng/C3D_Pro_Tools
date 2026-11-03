using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace C3DProTools
{
    /// <summary>How one pipe meets a rectangular chamber.</summary>
    internal sealed class ChamberEntryIssue
    {
        public ObjectId StructureId, PipeId;
        public string Structure = "", Pipe = "";
        public PlanGeometry.ChamberWall Wall;
        public double SkewDeg;
        /// <summary>The rotation that squares the chamber to its pipes (one pipe, or two straight through), or null.</summary>
        public double? AlignedRotation;
        public double Rotation;
        public string Problem => Wall == PlanGeometry.ChamberWall.Long ? "enters a long wall" : $"enters a narrow wall {SkewDeg:0.#}° off square";
    }

    /// <summary>A structure-to-structure span longer than allowed.</summary>
    internal sealed class LongSpan
    {
        public List<ObjectId> Pipes = new List<ObjectId>();
        public string Names = "";
        public double Length;
    }

    /// <summary>
    /// Network checks of the Network Editor: pipes entering rectangular chambers through a long wall or off square, and
    /// structure-to-structure spans longer than allowed (pipes joined with no structure between them count as one span).
    /// </summary>
    internal static class NetworkChecks
    {
        /// <summary>The structures the chosen pipes meet, and every pipe of their networks connected to each.</summary>
        private static Dictionary<ObjectId, List<Pipe>> StructuresOf(Transaction tr, IEnumerable<ObjectId> pipeIds)
        {
            HashSet<ObjectId> structures = new HashSet<ObjectId>(), networks = new HashSet<ObjectId>();
            foreach (ObjectId id in pipeIds)
                if (tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                {
                    if (!p.StartStructureId.IsNull) structures.Add(p.StartStructureId);
                    if (!p.EndStructureId.IsNull) structures.Add(p.EndStructureId);
                    networks.Add(p.NetworkId);
                }
            Dictionary<ObjectId, List<Pipe>> connected = structures.ToDictionary(s => s, s => new List<Pipe>());
            foreach (ObjectId netId in networks)
                if (tr.GetObject(netId, OpenMode.ForRead) is Network net)
                    foreach (ObjectId pid in net.GetPipeIds())
                        if (tr.GetObject(pid, OpenMode.ForRead) is Pipe p)
                            foreach (ObjectId sid in new[] { p.StartStructureId, p.EndStructureId })
                                if (!sid.IsNull && connected.TryGetValue(sid, out List<Pipe> l) && !l.Contains(p)) l.Add(p);
            return connected;
        }

        /// <summary>The pipe's end at the structure and a point along it a little way out (its far end, or along its arc).</summary>
        private static (Point3d near, Point3d far) Ends(Pipe p, ObjectId structureId)
        {
            bool atStart = p.StartStructureId == structureId;
            Point3d near = atStart ? p.StartPoint : p.EndPoint, far = atStart ? p.EndPoint : p.StartPoint;
            if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo arc))
            {
                // The arc's direction at the chamber: its first sample from that end.
                List<Point3d> path = PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 0.5, 400);
                if (path.Count > 1) far = atStart ? path[1] : path[path.Count - 2];
            }
            return (near, far);
        }

        /// <summary>
        /// Every pipe meeting a rectangular chamber (one of the structures the chosen pipes meet) through a long wall, or through
        /// a narrow wall more than <paramref name="maxSkewDeg"/> off square.
        /// </summary>
        public static List<ChamberEntryIssue> CheckEntries(Transaction tr, IEnumerable<ObjectId> pipeIds, double maxSkewDeg)
        {
            List<ChamberEntryIssue> issues = new List<ChamberEntryIssue>();
            foreach (KeyValuePair<ObjectId, List<Pipe>> kv in StructuresOf(tr, pipeIds))
            {
                if (!(tr.GetObject(kv.Key, OpenMode.ForRead) is Structure st)) continue;
                StructureFootprint fp = StructureFootprint.Of(st);
                if (fp.Round || Math.Abs(fp.HalfX - fp.HalfY) < 1e-3) continue;
                List<double> bearings = new List<double>();
                List<ChamberEntryIssue> here = new List<ChamberEntryIssue>();
                foreach (Pipe p in kv.Value)
                {
                    (Point3d near, Point3d far) = Ends(p, kv.Key);
                    bearings.Add(Math.Atan2(far.Y - near.Y, far.X - near.X));
                    var (wall, skew) = PlanGeometry.ChamberEntry(fp.Center.X, fp.Center.Y, fp.Rotation, fp.HalfX, fp.HalfY, (far.X, far.Y), (near.X, near.Y));
                    if (wall == PlanGeometry.ChamberWall.Long || (wall == PlanGeometry.ChamberWall.Narrow && skew > maxSkewDeg + 1e-9))
                        here.Add(new ChamberEntryIssue { StructureId = kv.Key, PipeId = p.ObjectId, Structure = st.Name, Pipe = p.Name, Wall = wall, SkewDeg = skew, Rotation = fp.Rotation });
                }
                double? aligned = PlanGeometry.AlignedRotation(fp.Rotation, fp.HalfX, fp.HalfY, bearings);
                foreach (ChamberEntryIssue i in here) i.AlignedRotation = aligned;
                issues.AddRange(here);
            }
            return issues;
        }

        /// <summary>Turns each chamber to its squared rotation; returns how many turned.</summary>
        public static int AlignChambers(Transaction tr, IEnumerable<ChamberEntryIssue> issues)
        {
            int turned = 0;
            foreach (ChamberEntryIssue i in issues.Where(x => x.AlignedRotation.HasValue).GroupBy(x => x.StructureId).Select(g => g.First()))
            {
                Structure st = (Structure)tr.GetObject(i.StructureId, OpenMode.ForWrite);
                st.Rotation = i.AlignedRotation.Value;
                turned++;
            }
            return turned;
        }

        /// <summary>
        /// Structure-to-structure spans longer than <paramref name="maxSpan"/> in the chosen pipes' networks that include one of
        /// them or meet one of the structures they meet (so the spans next to an edited structure are checked too).
        /// </summary>
        public static List<LongSpan> LongSpans(Transaction tr, IEnumerable<ObjectId> pipeIds, double maxSpan)
        {
            List<LongSpan> result = new List<LongSpan>();
            if (maxSpan <= 0) return result;
            HashSet<ObjectId> chosen = new HashSet<ObjectId>(pipeIds), networks = new HashSet<ObjectId>(), touched = new HashSet<ObjectId>();
            foreach (ObjectId id in chosen)
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                {
                    networks.Add(p.NetworkId);
                    if (!p.StartStructureId.IsNull) touched.Add(p.StartStructureId);
                    if (!p.EndStructureId.IsNull) touched.Add(p.EndStructureId);
                }
            foreach (ObjectId netId in networks)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                List<Pipe> pipes = net.GetPipeIds().Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe).Where(p => p != null).ToList();
                string Key(ObjectId sid, Point3d at) => sid.IsNull
                    ? "P" + Math.Round(at.X, 2).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(at.Y, 2).ToString(CultureInfo.InvariantCulture)
                    : "S" + sid.Handle;
                double LengthOf(Pipe p)
                {
                    try { return p.Length2DCenterToCenter; } catch (Exception) { return p.Length2D; }
                }
                var input = pipes.Select(p => (Key(p.StartStructureId, p.StartPoint), !p.StartStructureId.IsNull, Key(p.EndStructureId, p.EndPoint), !p.EndStructureId.IsNull, LengthOf(p))).ToList();
                foreach ((List<int> members, double length) in PlanGeometry.Spans(input))
                {
                    if (length <= maxSpan + 1e-6) continue;
                    List<Pipe> span = members.Select(i => pipes[i]).ToList();
                    bool relevant = span.Any(p => chosen.Contains(p.ObjectId) || touched.Contains(p.StartStructureId) || touched.Contains(p.EndStructureId));
                    if (!relevant) continue;
                    result.Add(new LongSpan { Pipes = span.Select(p => p.ObjectId).ToList(), Names = string.Join(", ", span.Select(p => p.Name)), Length = length });
                }
            }
            return result;
        }

        /// <summary>A line for a report, or "" when every span is within the limit.</summary>
        public static string SpanWarning(Transaction tr, IEnumerable<ObjectId> pipeIds, double maxSpan)
        {
            List<LongSpan> spans = LongSpans(tr, pipeIds, maxSpan);
            if (spans.Count == 0) return "";
            return $"Spans longer than {maxSpan:0.00} m between structures: {spans.Count}\n• " +
                   string.Join("\n• ", spans.Take(8).Select(s => $"{s.Length:0.00} m: {s.Names}")) + (spans.Count > 8 ? $"\n• and {spans.Count - 8} more" : "");
        }
    }
}
