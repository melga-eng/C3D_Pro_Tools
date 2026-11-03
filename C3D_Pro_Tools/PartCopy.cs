using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Linq;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>
    /// New pipes and structures like ones already in the drawing, even when Civil 3D cannot build them from their family:
    /// a part whose family is not defined in the drawing (imported or copied networks) or whose catalog is missing. Such a
    /// part is made by copying the existing one and moving the copy into place, which needs neither family nor catalog.
    /// </summary>
    internal static class PartCopy
    {
        /// <summary>True when the id is a part family object Civil 3D can build new parts from.</summary>
        public static bool IsFamily(Transaction tr, ObjectId familyId)
        {
            try { return !familyId.IsNull && familyId.IsValid && !familyId.IsErased && tr.GetObject(familyId, OpenMode.ForRead) is PartFamily; }
            catch (Exception) { return false; }
        }

        /// <summary>A straight pipe from start to end in the network: from the family and size when Civil 3D can, else a copy of the template.</summary>
        /// <param name="stayIfNotMovable">
        /// A family-less copy cannot be moved to another network (Civil 3D refuses); true leaves it in the template's network
        /// instead of failing.
        /// </param>
        public static ObjectId Pipe(Transaction tr, Network network, ObjectId familyId, ObjectId sizeId, Pipe template, Point3d start, Point3d end, out bool copied,
            bool stayIfNotMovable = false)
        {
            copied = false;
            if (IsFamily(tr, familyId) && !sizeId.IsNull)
            {
                try
                {
                    ObjectId id = ObjectId.Null;
                    using (LineSegment3d line = new LineSegment3d(start, end)) network.AddLinePipe(familyId, sizeId, line, ref id, false);
                    if (!id.IsNull) return id;
                }
                catch (Exception) when (template != null) { }
            }
            if (template == null) throw new InvalidOperationException("Civil 3D cannot make this pipe from its family, and there is no pipe of it to copy.");
            copied = true;
            return CopyPipe(tr, network, template, start, end, stayIfNotMovable);
        }

        /// <summary>A straight copy of the pipe (a straight pipe of its size in its network when it is curved), moved to start-end in the network.</summary>
        public static ObjectId CopyPipe(Transaction tr, Network network, Pipe template, Point3d start, Point3d end, bool stayIfNotMovable = false)
        {
            Pipe source = template;
            if (source.SubEntityType == PipeSubEntityType.Curved)
            {
                // Moving the ends of a curved copy keeps it curved; a straight pipe of the same size is copied instead.
                string size = Safe(() => template.PartSizeName);
                Network own = (Network)tr.GetObject(template.NetworkId, OpenMode.ForRead);
                source = own.GetPipeIds().Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                            .FirstOrDefault(p => p != null && p.SubEntityType != PipeSubEntityType.Curved && Safe(() => p.PartSizeName) == size)
                         ?? throw new InvalidOperationException($"{template.Name} is curved and its network has no straight pipe of {size} to copy.");
            }
            // A cloned pipe names its network but is not in the network's own list (Prospector does not select it, routes
            // cannot pass it, it cannot be moved): breaking a member pipe makes a real member instead.
            ObjectId copy = BreakCopy(tr, source);
            if (copy.IsNull) copy = Clone(tr, source.ObjectId);
            Pipe p2 = (Pipe)tr.GetObject(copy, OpenMode.ForWrite);
            // A copy keeps no structure connections; its ends go where they are asked for.
            try { if (!p2.StartStructureId.IsNull) p2.Disconnect(ConnectorPositionType.Start); } catch (Exception) { }
            try { if (!p2.EndStructureId.IsNull) p2.Disconnect(ConnectorPositionType.End); } catch (Exception) { }
            p2.StartPoint = start;
            p2.EndPoint = end;
            if (!p2.StartPoint.IsEqualTo(start)) p2.StartPoint = start;
            try { MoveTo(tr, p2.NetworkId, copy, network.ObjectId); }
            catch (Exception) when (stayIfNotMovable) { }
            return copy;
        }

        /// <summary>A structure at a point: from the family and size when Civil 3D can, else a copy of the template.</summary>
        public static ObjectId Structure(Transaction tr, Network network, ObjectId familyId, ObjectId sizeId, Structure template, Point3d at, double rotation, out bool copied)
        {
            copied = false;
            if (IsFamily(tr, familyId) && !sizeId.IsNull)
            {
                try
                {
                    ObjectId id = ObjectId.Null;
                    network.AddStructure(familyId, sizeId, at, rotation, ref id, false);
                    if (!id.IsNull) return id;
                }
                catch (Exception) when (template != null) { }
            }
            if (template == null) throw new InvalidOperationException("Civil 3D cannot make this structure from its family, and there is no structure of it to copy.");
            copied = true;
            ObjectId copy = Clone(tr, template.ObjectId);
            Structure s = (Structure)tr.GetObject(copy, OpenMode.ForWrite);
            s.Location = at;
            s.Rotation = rotation;
            MoveTo(tr, s.NetworkId, copy, network.ObjectId);
            return copy;
        }

        /// <summary>True when the pipe is in its network's own list (not only naming the network, as a cloned pipe does).</summary>
        public static bool IsMember(Transaction tr, Pipe pipe)
        {
            try { return tr.GetObject(pipe.NetworkId, OpenMode.ForRead) is Network n && n.GetPipeIds().Contains(pipe.ObjectId); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// A new member pipe of the same part, made by breaking a member pipe of it (the template, else a straight one of the
        /// same size in its network) at a structure of the network and putting everything back: the broken pipe's ends and
        /// connections and the structure's place. Null when there is no member pipe or structure to do it with.
        /// </summary>
        private static ObjectId BreakCopy(Transaction tr, Pipe template)
        {
            Network net;
            try { net = (Network)tr.GetObject(template.NetworkId, OpenMode.ForRead); } catch (Exception) { return ObjectId.Null; }
            ObjectIdCollection members = net.GetPipeIds();
            string size = Safe(() => template.PartSizeName);
            Pipe source = members.Contains(template.ObjectId) && template.SubEntityType == PipeSubEntityType.Straight ? template
                : members.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                         .FirstOrDefault(p => p != null && p.SubEntityType == PipeSubEntityType.Straight && Safe(() => p.PartSizeName) == size);
            if (source == null || source.Length2D < 1e-3) return ObjectId.Null;
            ObjectId anchor = !source.StartStructureId.IsNull ? source.StartStructureId : !source.EndStructureId.IsNull ? source.EndStructureId
                : net.GetStructureIds().Cast<ObjectId>().FirstOrDefault(id => !id.IsErased);
            if (anchor.IsNull) return ObjectId.Null;

            Point3d s = source.StartPoint, e = source.EndPoint;
            ObjectId ss = source.StartStructureId, es = source.EndStructureId;
            Structure st = (Structure)tr.GetObject(anchor, OpenMode.ForWrite);
            Point3d anchorAt = st.Location;
            double anchorRotation = st.Rotation;
            ObjectId newId = ObjectId.Null;
            try
            {
                net.UpgradeOpen();
                net.BreakPipe(source.ObjectId, s + (e - s) / 2, anchor, ref newId);
            }
            catch (Exception) { return ObjectId.Null; }
            if (newId.IsNull) return ObjectId.Null;

            Pipe copy = (Pipe)tr.GetObject(newId, OpenMode.ForWrite);
            source.UpgradeOpen();
            try { if (!copy.StartStructureId.IsNull) copy.Disconnect(ConnectorPositionType.Start); } catch (Exception) { }
            try { if (!copy.EndStructureId.IsNull) copy.Disconnect(ConnectorPositionType.End); } catch (Exception) { }
            try { if (!source.StartStructureId.IsNull && source.StartStructureId != ss) source.Disconnect(ConnectorPositionType.Start); } catch (Exception) { }
            try { if (!source.EndStructureId.IsNull && source.EndStructureId != es) source.Disconnect(ConnectorPositionType.End); } catch (Exception) { }
            if (!st.Location.IsEqualTo(anchorAt)) st.Location = anchorAt;
            if (Math.Abs(st.Rotation - anchorRotation) > 1e-12) st.Rotation = anchorRotation;
            source.StartPoint = s;
            source.EndPoint = e;
            if (!source.StartPoint.IsEqualTo(s)) source.StartPoint = s;
            try { if (!ss.IsNull && source.StartStructureId != ss) source.ConnectToStructure(ConnectorPositionType.Start, ss, false); } catch (Exception) { }
            try { if (!es.IsNull && source.EndStructureId != es) source.ConnectToStructure(ConnectorPositionType.End, es, false); } catch (Exception) { }
            // Connecting may pull an end onto the structure; the pipe keeps its own 3D points.
            if (!source.StartPoint.IsEqualTo(s)) source.StartPoint = s;
            if (!source.EndPoint.IsEqualTo(e)) source.EndPoint = e;
            return newId;
        }

        private static ObjectId Clone(Transaction tr, ObjectId source)
        {
            Entity e = (Entity)tr.GetObject(source, OpenMode.ForRead);
            IdMapping map = new IdMapping();
            e.Database.DeepCloneObjects(new ObjectIdCollection { source }, e.OwnerId, map, false);
            return map[source].Value;
        }

        private static void MoveTo(Transaction tr, ObjectId fromNetwork, ObjectId part, ObjectId toNetwork)
        {
            if (fromNetwork == toNetwork || toNetwork.IsNull) return;
            ((Network)tr.GetObject(fromNetwork, OpenMode.ForWrite)).MoveParts(new ObjectIdCollection { part }, toNetwork);
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; } catch (Exception) { return ""; }
        }
    }
}
