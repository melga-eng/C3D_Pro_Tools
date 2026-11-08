using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    /// <summary>One gravity network as the Network Organizer lists it.</summary>
    internal sealed class NetworkSummary
    {
        public ObjectId Id;
        public string Name = "";
        public int Pipes, Structures, Copies;
        /// <summary>A data shortcut reference: read-only here, so it cannot be merged.</summary>
        public bool Reference;
        public string Sizes = "";
        public bool Empty => Pipes == 0 && Structures == 0;
    }

    internal sealed class MergeResult
    {
        public int Moved, Deleted, Reconnected, Connected, NetworksDeleted;
        public ObjectId TargetId = ObjectId.Null;
        public string TargetName = "";
        public readonly List<string> Log = new List<string>();
    }

    /// <summary>
    /// Organises gravity networks: lists them with their parts and the copies they share, and merges networks into one: copies
    /// of the same part in the same place are deleted (the most used copy, by labels and profile views, stays), parts of a
    /// different size in the same place are left for the user to choose, and everything else moves into the target network.
    /// </summary>
    internal static class NetworkOrganizer
    {
        /// <summary>How much the drawing uses each part: its labels (plan and profile) and the profile views it is drawn in.</summary>
        private static Dictionary<ObjectId, int> LabelCounts(Transaction tr, Database db)
        {
            Dictionary<ObjectId, int> count = new Dictionary<ObjectId, int>();
            foreach (LabelInfo l in AnnotLabels.Scan(tr, db))
                if (!l.PartId.IsNull) count[l.PartId] = count.TryGetValue(l.PartId, out int c) ? c + 1 : 1;
            return count;
        }

        private static int Views(Part p)
        {
            try { return p is Pipe pipe ? pipe.GetProfileViewsDisplayingMe().Count : p is Structure st ? st.GetProfileViewsDisplayingMe().Count : 0; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return 0; }
        }

        private static string SizeOf(Part p)
        {
            try { return p.PartSizeName ?? ""; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return ""; }
        }

        /// <summary>The part family's name ("" for a part without a family, such as a duct bank built by copying).</summary>
        private static string FamilyOf(Part p)
        {
            try { return p.PartFamilyName ?? ""; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return ""; }
        }

        /// <summary>The pipe's bulge from start to end (0: straight). NaN when it cannot be read, so the pipe matches no other.</summary>
        private static double BulgeOf(Pipe p)
        {
            try { using (PipeCurveGeometry g = p.CurveGeometry) return g?.Bulge ?? 0.0; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return double.NaN; }
        }

        /// <summary>The parts of these networks for merging, each with how much it is used.</summary>
        private static List<(MergePart part, ObjectId id, ObjectId network)> Parts(Transaction tr, IEnumerable<ObjectId> networks, ObjectId target, Dictionary<ObjectId, int> labels)
        {
            List<(MergePart, ObjectId, ObjectId)> list = new List<(MergePart, ObjectId, ObjectId)>();
            foreach (ObjectId netId in networks)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                        list.Add((new MergePart
                        {
                            Id = id.Handle.ToString(), Name = p.Name, Network = net.Name, IsPipe = true, Family = FamilyOf(p), Size = SizeOf(p), Bulge = BulgeOf(p), InTarget = netId == target,
                            A = (p.StartPoint.X, p.StartPoint.Y, p.StartPoint.Z), B = (p.EndPoint.X, p.EndPoint.Y, p.EndPoint.Z),
                            Uses = (labels.TryGetValue(id, out int c) ? c : 0) + Views(p)
                        }, id, netId));
                foreach (ObjectId id in net.GetStructureIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Structure st)
                        list.Add((new MergePart
                        {
                            Id = id.Handle.ToString(), Name = st.Name, Network = net.Name, IsPipe = false, Family = FamilyOf(st), Size = SizeOf(st), InTarget = netId == target,
                            A = (st.Location.X, st.Location.Y, st.Location.Z), Uses = (labels.TryGetValue(id, out int c) ? c : 0) + Views(st)
                        }, id, netId));
            }
            return list;
        }

        /// <summary>Every gravity network of the drawing, with its parts, sizes and the copies it shares (with itself or others).</summary>
        public static List<NetworkSummary> Read(Transaction tr, Database db, CivilDocument doc)
        {
            List<NetworkSummary> list = new List<NetworkSummary>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                NetworkSummary s = new NetworkSummary { Id = netId, Name = net.Name, Pipes = net.GetPipeIds().Count, Structures = net.GetStructureIds().Count };
                try { s.Reference = net.IsReferenceObject; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                List<string> sizes = new List<string>();
                foreach (ObjectId id in net.GetPipeIds()) if (tr.GetObject(id, OpenMode.ForRead) is Pipe p) sizes.Add(SizeOf(p));
                s.Sizes = string.Join(", ", sizes.Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}"));
                list.Add(s);
            }
            var parts = Parts(tr, list.Where(s => !s.Reference).Select(s => s.Id), ObjectId.Null, new Dictionary<ObjectId, int>());
            Dictionary<string, int> copies = NetworkMerge.CopiesByNetwork(parts.Select(p => p.part).ToList());
            foreach (NetworkSummary s in list) s.Copies = copies.TryGetValue(s.Name, out int c) ? c : 0;
            return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The plan of a merge of <paramref name="networks"/> into <paramref name="target"/> (null: a new network), with each row's part id.</summary>
        public static List<(MergeRow row, ObjectId id, ObjectId network)> Plan(Transaction tr, Database db, IEnumerable<ObjectId> networks, ObjectId target)
        {
            var parts = Parts(tr, networks, target, LabelCounts(tr, db));
            Dictionary<MergePart, (ObjectId id, ObjectId net)> byPart = parts.ToDictionary(p => p.part, p => (p.id, p.network));
            return NetworkMerge.Plan(parts.Select(p => p.part).ToList()).Select(r => (r, byPart[r.Part].id, byPart[r.Part].net)).ToList();
        }

        /// <summary>
        /// Merges: the target network (made now when <paramref name="target"/> is null, with the first network's parts list and
        /// surface), the chosen parts moved into it, then the chosen copies deleted (pipes on a deleted structure are connected to
        /// the copy that stays), pipe ends lying inside one of its structures connected when asked, and emptied networks deleted.
        /// </summary>
        /// <param name="delete">The parts to delete (copies, and parts of another size the user ticked); all other rows' parts move.</param>
        /// <param name="move">The parts to move into the target (untick a row to leave its part where it is).</param>
        public static MergeResult Merge(Transaction tr, CivilDocument doc, List<ObjectId> networks, ObjectId target, string newName,
            ICollection<ObjectId> move, IDictionary<ObjectId, ObjectId> delete, bool connectEnds, bool deleteEmpty, Editor ed)
        {
            MergeResult result = new MergeResult();
            Network first = (Network)tr.GetObject(networks[0], OpenMode.ForRead);
            if (target.IsNull)
            {
                string name = string.IsNullOrWhiteSpace(newName) ? "Merged network" : newName.Trim();
                target = Network.Create(doc, ref name);
                Network made = (Network)tr.GetObject(target, OpenMode.ForWrite);
                try { made.PartsListId = first.PartsListId; } catch (Exception ex) { result.Log.Add("The new network could not take the parts list of " + first.Name + ": " + ex.Message); }
                try { made.ReferenceSurfaceId = first.ReferenceSurfaceId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            Network targetNet = (Network)tr.GetObject(target, OpenMode.ForWrite);
            result.TargetId = target;
            result.TargetName = targetNet.Name;

            // 1. Into the target, network by network (part by part where a network's move is refused, to name the ones that stay).
            foreach (ObjectId netId in networks.Where(n => n != target))
            {
                Network net = (Network)tr.GetObject(netId, OpenMode.ForWrite);
                HashSet<ObjectId> here = new HashSet<ObjectId>(net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>()));
                List<ObjectId> ids = here.Where(id => move.Contains(id) && !delete.ContainsKey(id)).ToList();
                if (ids.Count == 0) continue;
                try
                {
                    net.MoveParts(new ObjectIdCollection(ids.ToArray()), target);
                    result.Moved += ids.Count;
                }
                catch (Exception all)
                {
                    C3DProLogger.Warn($"Network Organizer: moving {ids.Count} part(s) of {net.Name} at once was refused", all);
                    foreach (ObjectId id in ids)
                    {
                        try { net.MoveParts(new ObjectIdCollection { id }, target); result.Moved++; }
                        catch (Exception ex)
                        {
                            string name = tr.GetObject(id, OpenMode.ForRead) is Part p ? p.Name : id.Handle.ToString();
                            result.Log.Add($"{name} ({net.Name}) could not be moved: {ex.Message} (parts without a part family cannot change network); it stays in {net.Name}.");
                        }
                    }
                }
            }

            // 2. Copies: pipes on a structure being deleted go to the copy that stays, then the copies are deleted.
            foreach (KeyValuePair<ObjectId, ObjectId> kv in delete.Where(kv => tr.GetObject(kv.Key, OpenMode.ForRead) is Structure))
                foreach (ObjectId netId in new[] { target }.Concat(networks).Distinct())
                {
                    if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                    foreach (ObjectId pid in net.GetPipeIds())
                    {
                        if (delete.ContainsKey(pid) || !(tr.GetObject(pid, OpenMode.ForRead) is Pipe pipe)) continue;
                        foreach (ConnectorPositionType end in new[] { ConnectorPositionType.Start, ConnectorPositionType.End })
                        {
                            if ((end == ConnectorPositionType.Start ? pipe.StartStructureId : pipe.EndStructureId) != kv.Key) continue;
                            pipe.UpgradeOpen();
                            Point3d s = pipe.StartPoint, e = pipe.EndPoint;
                            try
                            {
                                pipe.Disconnect(end);
                                NetworkEditOps.ConnectEnd(pipe, end, kv.Value);
                                result.Reconnected++;
                            }
                            catch (Exception ex) { result.Log.Add($"{pipe.Name}: could not be connected to the structure that stays ({ex.Message})."); }
                            if (!pipe.StartPoint.IsEqualTo(s)) pipe.StartPoint = s;
                            if (!pipe.EndPoint.IsEqualTo(e)) pipe.EndPoint = e;
                        }
                    }
                }
            foreach (ObjectId id in delete.Keys)
            {
                try { tr.GetObject(id, OpenMode.ForWrite).Erase(); result.Deleted++; }
                catch (Exception ex) { result.Log.Add($"A copy ({id.Handle}) could not be deleted: {ex.Message}"); }
            }

            // 3. Pipe ends of the target lying inside one of its structures, not connected: connected.
            if (connectEnds)
            {
                List<StructureFootprint> structures = StructureFootprint.OfNetwork(tr, target);
                foreach (ObjectId pid in targetNet.GetPipeIds())
                {
                    if (!(tr.GetObject(pid, OpenMode.ForRead) is Pipe pipe)) continue;
                    foreach (bool atStart in new[] { true, false })
                    {
                        if (!(atStart ? pipe.StartStructureId : pipe.EndStructureId).IsNull) continue;
                        Point3d at = atStart ? pipe.StartPoint : pipe.EndPoint;
                        StructureFootprint fp = structures.FirstOrDefault(f => f.Contains(at, 0.0));
                        if (fp == null || fp.Id == (atStart ? pipe.EndStructureId : pipe.StartStructureId)) continue;
                        pipe.UpgradeOpen();
                        CoverApply.Connect(pipe, true, atStart, fp.Id, ed);
                        if (!(atStart ? pipe.StartStructureId : pipe.EndStructureId).IsNull) result.Connected++;
                    }
                }
            }

            // 4. Networks left empty.
            if (deleteEmpty)
                foreach (ObjectId netId in networks.Where(n => n != target))
                {
                    Network net = (Network)tr.GetObject(netId, OpenMode.ForRead);
                    if (net.GetPipeIds().Count > 0 || net.GetStructureIds().Count > 0) continue;
                    try { net.UpgradeOpen(); net.Erase(); result.NetworksDeleted++; }
                    catch (Exception ex) { result.Log.Add($"Network {net.Name} is empty but could not be deleted: {ex.Message}"); }
                }
            return result;
        }

        /// <summary>Deletes these networks where they are empty; returns how many.</summary>
        public static int DeleteEmpty(Transaction tr, IEnumerable<ObjectId> networks, List<string> log)
        {
            int n = 0;
            foreach (ObjectId id in networks)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Network net) || net.GetPipeIds().Count > 0 || net.GetStructureIds().Count > 0) continue;
                try { net.UpgradeOpen(); net.Erase(); n++; }
                catch (Exception ex) { log.Add($"{net.Name}: {ex.Message}"); }
            }
            return n;
        }

        /// <summary>The extents of these networks' parts (for zooming), or null.</summary>
        public static Extents3d? Extents(Transaction tr, IEnumerable<ObjectId> networks)
        {
            Extents3d? ext = null;
            foreach (ObjectId netId in networks)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>()))
                {
                    try
                    {
                        Extents3d e = ((Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(id, OpenMode.ForRead)).GeometricExtents;
                        if (ext.HasValue) { Extents3d x = ext.Value; x.AddExtents(e); ext = x; } else ext = e;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }
            return ext;
        }
    }
}
