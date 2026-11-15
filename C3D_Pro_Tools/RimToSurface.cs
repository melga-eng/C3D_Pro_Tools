using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using Surface = Autodesk.Civil.DatabaseServices.Surface;

namespace C3DProTools
{
    /// <summary>
    /// Sets structure rims on a surface (once, or linked with Civil 3D's Automatic Surface Adjustment). Sumps stay at their level
    /// and pipes are not touched: a structure whose sump is controlled by depth would otherwise have its sump move with the rim.
    /// </summary>
    internal static class RimToSurface
    {
        internal sealed class Row
        {
            public ObjectId Id;
            public string Name, Network, Part;
            public double Rim, Ground, NewRim, Sump, PipeTop;
            public RimCheck Check;
            /// <summary>The structure comes from a data shortcut: read-only here.</summary>
            public bool Reference;
        }

        internal sealed class Outcome
        {
            public NetworkEditOps.Counts Counts = new NetworkEditOps.Counts();
            /// <summary>Sumps changed from depth to elevation control so that they stay put while the rim follows the surface.</summary>
            public int SumpControlChanged;
            /// <summary>Structures taken off Automatic Surface Adjustment, which would have overridden a rim set once.</summary>
            public int Unlinked;
            /// <summary>Rims Civil 3D did not leave where they were asked to be.</summary>
            public int NotAtTarget;
        }

        /// <summary>The structures at the ends of the given gravity pipes and those chosen directly, each with what would happen to it.</summary>
        public static List<Row> Plan(Transaction tr, IEnumerable<ObjectId> gravityPipes, IEnumerable<ObjectId> parts, Surface surface, double aboveSurface)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>();
            foreach (ObjectId id in gravityPipes)
                if (tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                {
                    if (!p.StartStructureId.IsNull) ids.Add(p.StartStructureId);
                    if (!p.EndStructureId.IsNull) ids.Add(p.EndStructureId);
                }
            foreach (ObjectId id in parts)
                if (tr.GetObject(id, OpenMode.ForRead) is Structure) ids.Add(id);

            Dictionary<ObjectId, Network> networks = new Dictionary<ObjectId, Network>();
            Dictionary<ObjectId, double> pipeTops = new Dictionary<ObjectId, double>();
            List<Row> rows = new List<Row>();
            foreach (ObjectId id in ids)
            {
                Structure st = (Structure)tr.GetObject(id, OpenMode.ForRead);
                if (!networks.TryGetValue(st.NetworkId, out Network net))
                {
                    net = (Network)tr.GetObject(st.NetworkId, OpenMode.ForRead);
                    networks[st.NetworkId] = net;
                    AddPipeTops(tr, net, pipeTops);
                }
                double ground = CoverRoute.GroundAt(surface, st.Location.X, st.Location.Y);
                double sump = Read(() => st.SumpElevation), top = pipeTops.TryGetValue(id, out double t) ? t : double.NaN;
                (RimCheck check, double newRim) = RimRules.Check(st.RimElevation, ground, aboveSurface, sump, top);
                rows.Add(new Row
                {
                    Id = id, Name = st.Name, Network = net.Name, Part = Read(() => st.PartSizeName) ?? "", Rim = st.RimElevation,
                    Ground = ground, NewRim = newRim, Sump = sump, PipeTop = top, Check = check, Reference = net.IsReferenceObject
                });
            }
            return rows.OrderBy(r => r.Network, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Adds the highest outside pipe top at each structure of the network.</summary>
        private static void AddPipeTops(Transaction tr, Network net, Dictionary<ObjectId, double> tops)
        {
            void Add(ObjectId structure, double top)
            {
                if (structure.IsNull) return;
                tops[structure] = tops.TryGetValue(structure, out double was) ? Math.Max(was, top) : top;
            }
            foreach (ObjectId id in net.GetPipeIds().Live())
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                double half = CoverRoute.OuterHeightOf(p) / 2.0;
                Add(p.StartStructureId, p.StartPoint.Z + half);
                Add(p.EndStructureId, p.EndPoint.Z + half);
            }
        }

        public static Outcome Apply(Transaction tr, List<Row> rows, ObjectId surfaceId, double aboveSurface, bool follow, Editor ed)
        {
            Outcome o = new Outcome();
            foreach (Row r in rows)
            {
                try
                {
                    Structure st = (Structure)tr.GetObject(r.Id, OpenMode.ForWrite);
                    double sump = st.SumpElevation;
                    if (follow)
                    {
                        // By depth, the sump would move every time the surface does.
                        if (st.ControlSumpBy != StructureControlSumpType.ByElevation) { st.ControlSumpBy = StructureControlSumpType.ByElevation; o.SumpControlChanged++; }
                        st.RefSurfaceId = surfaceId;
                        st.SurfaceAdjustmentValue = aboveSurface;
                        st.AutomaticRimSurfaceAdjustment = true;
                    }
                    else if (st.AutomaticRimSurfaceAdjustment)
                    {
                        st.AutomaticRimSurfaceAdjustment = false;
                        o.Unlinked++;
                    }
                    if (Math.Abs(st.RimElevation - r.NewRim) > RimRules.Tolerance / 10) st.RimElevation = r.NewRim;
                    HoldSump(st, sump);
                    if (Math.Abs(st.RimElevation - r.NewRim) > RimRules.Tolerance)
                    {
                        o.NotAtTarget++;
                        ed.WriteMessage($"\n[Rims to surface] <{r.Name}>: the rim is at {st.RimElevation:0.000}, not the {r.NewRim:0.000} asked.");
                    }
                    o.Counts.Done++;
                }
                catch (Exception ex)
                {
                    o.Counts.Failed++;
                    ed.WriteMessage($"\n[Error] Rim of <{r.Name}>: {ex.Message}");
                }
            }
            return o;
        }

        /// <summary>Puts the sump back at its level after the rim moved.</summary>
        private static void HoldSump(Structure st, double sump)
        {
            if (Math.Abs(st.SumpElevation - sump) <= 1e-4) return;
            if (st.ControlSumpBy == StructureControlSumpType.ByElevation) { st.SumpElevation = sump; return; }
            for (int i = 0; i < 3 && Math.Abs(st.SumpElevation - sump) > 1e-4; i++) st.SumpDepth = st.SumpDepth + (st.SumpElevation - sump);
        }

        private static double Read(Func<double> get)
        {
            try { return get(); }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return double.NaN; }
        }

        private static string Read(Func<string> get)
        {
            try { return get(); }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return null; }
        }
    }
}
