using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    internal enum SwapKind { Pipe, Structure, PressurePipe, Fitting, Appurtenance }

    /// <summary>What a group of parts becomes: a gravity part (family and size) or a pressure part size, or the family's nearest size.</summary>
    internal sealed class SwapTarget
    {
        /// <summary>True: each group gets the family's size nearest its own inside size, instead of one fixed size.</summary>
        public bool Nearest;
        /// <summary>The gravity part (pipes and structures); null for a pressure target.</summary>
        public PartPick Gravity;
        public ObjectId PressureListId;
        public string PressureListName = "", PressureFamily = "", PressureSizeGuid = "", PressureSizeName = "";

        public string Family => Gravity != null ? Gravity.FamilyName : PressureFamily;
        public string Label => Nearest ? $"{(string.IsNullOrEmpty(Family) ? PressureSizeName : Family)} · nearest size" : Gravity != null ? Gravity.Label : PressureSizeName;
    }

    /// <summary>The chosen parts that share one part (kind, family and size), across every network, and what they are to become.</summary>
    internal sealed class SwapGroup
    {
        public SwapKind Kind;
        public string Family = "", Size = "";
        public readonly List<ObjectId> Ids = new List<ObjectId>();
        public readonly SortedSet<string> Networks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Inside width (or diameter) and height (pipes) or length (structures) of the group's size in metres; 0 = unknown.</summary>
        public double Width, Height;
        public SwapTarget Target;
        /// <summary>The size the target gives this group (worked out for a nearest-size target), or why it gets none.</summary>
        public string ToSize = "", Problem;
        /// <summary>How much wider (+) or narrower (-) inside the size it gets is, in metres; NaN when not known.</summary>
        public double ToChange = double.NaN;
        public Dictionary<PartContextType, double> ToValues;
        public string ToPressureGuid;

        public bool IsPressure => Kind >= SwapKind.PressurePipe;
        public DomainType Domain => Kind == SwapKind.Structure ? DomainType.Structure : DomainType.Pipe;
        public PressurePartDomainType PressureDomain => Kind == SwapKind.Fitting ? PressurePartDomainType.Fitting
            : Kind == SwapKind.Appurtenance ? PressurePartDomainType.Appurtenance : PressurePartDomainType.Pipe;
        public string Current => string.IsNullOrEmpty(Family) ? Size : $"{Family} · {Size}";
        public static string KindText(SwapKind k) => k == SwapKind.Pipe ? "Pipe" : k == SwapKind.Structure ? "Structure" : k == SwapKind.PressurePipe ? "Pressure pipe" : k == SwapKind.Fitting ? "Fitting" : "Appurtenance";
    }

    /// <summary>A saved swap: parts of this kind, family and size (empty = any size) get this target.</summary>
    internal sealed class SwapRule
    {
        public string Kind { get; set; }
        public string Family { get; set; }
        public string Size { get; set; }
        public bool Nearest { get; set; }
        public string ListName { get; set; }
        public string FamilyGuid { get; set; }
        public string FamilyName { get; set; }
        public string SizeName { get; set; }
        public Dictionary<string, double> Values { get; set; }
        public string PressureSizeGuid { get; set; }
    }

    internal sealed class SwapSet
    {
        public string Name { get; set; }
        public List<SwapRule> Rules { get; set; } = new List<SwapRule>();
    }

    /// <summary>
    /// Batch part swaps across networks: gravity pipes and structures get another family and size, pressure pipes, fittings and
    /// appurtenances another part size. Parts are grouped by what they are now; each group gets one target, which can be a fixed
    /// size or the nearest inside size in a family. Gravity pipes keep their inverts (or centrelines), structures their rim,
    /// sump, rotation and position.
    /// </summary>
    internal static class PartSwap
    {
        // =====================================================================================
        // The chosen parts, grouped
        // =====================================================================================

        /// <summary>
        /// The chosen pipes and other parts by their current part. The structures at gravity pipe ends and the fittings and
        /// appurtenances at pressure pipe ends come along when asked.
        /// </summary>
        public static List<SwapGroup> Collect(Transaction tr, IEnumerable<ObjectId> pipes, IEnumerable<ObjectId> parts, bool structuresAtEnds, bool fittingsAtEnds)
        {
            List<ObjectId> ids = new List<ObjectId>();
            HashSet<ObjectId> seen = new HashSet<ObjectId>();
            void Take(ObjectId id) { if (!id.IsNull && id.IsValid && !id.IsErased && seen.Add(id)) ids.Add(id); }
            foreach (ObjectId id in pipes)
            {
                Take(id);
                if (id.IsErased) continue;
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (structuresAtEnds && obj is Pipe p) { Take(p.StartStructureId); Take(p.EndStructureId); }
                if (fittingsAtEnds && obj is PressurePipe pp) { Take(Safe(() => pp.StartPartId, ObjectId.Null)); Take(Safe(() => pp.EndPartId, ObjectId.Null)); }
            }
            foreach (ObjectId id in parts) Take(id);

            Dictionary<string, SwapGroup> groups = new Dictionary<string, SwapGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in ids)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                SwapKind kind;
                string family, size, network;
                double w = 0, h = 0;
                if (obj is Pipe p)
                {
                    kind = SwapKind.Pipe;
                    family = Safe(() => p.PartFamilyName, ""); size = Safe(() => p.PartSizeName, ""); network = Safe(() => p.NetworkName, "");
                    w = Safe(() => p.InnerDiameterOrWidth, 0); h = Safe(() => p.InnerHeight, w);
                }
                else if (obj is Structure st)
                {
                    kind = SwapKind.Structure;
                    family = Safe(() => st.PartFamilyName, ""); size = Safe(() => st.PartSizeName, ""); network = Safe(() => st.NetworkName, "");
                    w = Safe(() => st.InnerDiameterOrWidth, 0);
                    h = Safe(() => st.BoundingShape == BoundingShapeType.Cylinder, false) ? w : Safe(() => st.InnerLength, w);
                }
                else if (obj is PressurePart pp)
                {
                    kind = pp is PressurePipe ? SwapKind.PressurePipe : pp is PressureFitting ? SwapKind.Fitting : SwapKind.Appurtenance;
                    family = Safe(() => pp.PartFamilyName, ""); size = Safe(() => pp.PartDescription, ""); network = Safe(() => pp.NetworkName, "");
                    w = h = pp is PressurePipe pipe ? Safe(() => pipe.InnerDiameter, 0) : PressureDiameter(Safe(() => pp.GetPartSize(), null), size, false);
                }
                else continue;
                if (string.IsNullOrWhiteSpace(size)) size = "(no size name)";
                string key = $"{kind}|{family}|{size}";
                if (!groups.TryGetValue(key, out SwapGroup g)) groups[key] = g = new SwapGroup { Kind = kind, Family = family, Size = size, Width = w, Height = h };
                g.Ids.Add(id);
                if (!string.IsNullOrEmpty(network)) g.Networks.Add(network);
            }
            return groups.Values.OrderBy(g => g.Kind).ThenBy(g => g.Family, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Width).ThenBy(g => g.Size, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); } catch (Exception) { return fallback; }
        }

        // =====================================================================================
        // Pressure part sizes
        // =====================================================================================

        public static List<(ObjectId id, string name)> PressureLists(Transaction tr, Database db)
        {
            List<(ObjectId, string)> lists = new List<(ObjectId, string)>();
            foreach (ObjectId id in PressurePartList.GetAvailablePartLists(db))
                if (tr.GetObject(id, OpenMode.ForRead) is PressurePartList pl) lists.Add((id, pl.Name));
            return lists.OrderBy(l => l.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<PressurePartSize> PressureSizes(Transaction tr, ObjectId listId, PressurePartDomainType domain)
        {
            if (listId.IsNull || !(tr.GetObject(listId, OpenMode.ForRead) is PressurePartList pl)) return new List<PressurePartSize>();
            try { return pl.GetParts(domain) ?? new List<PressurePartSize>(); } catch (Exception) { return new List<PressurePartSize>(); }
        }

        public static string PressureFamily(PressurePartSize s) => Safe(() => Convert.ToString(s.GetProperty(PressurePartContextType.PartFamilyName), CultureInfo.InvariantCulture), "") ?? "";

        /// <summary>
        /// A pressure size's inside diameter (pipes) or nominal diameter (fittings, appurtenances) in metres, from its catalog
        /// properties, else from the size named in its description ("75 mm"). Catalog values above 3 are taken as millimetres.
        /// </summary>
        public static double PressureDiameter(PressurePartSize s, string description, bool pipe)
        {
            double Num(PressurePartContextType c)
            {
                if (s == null) return 0;
                try { object v = s.GetProperty(c); return v == null ? 0 : Metres(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
                catch (Exception) { return 0; }
            }
            if (pipe)
            {
                double inside = Num(PressurePartContextType.PipeDiamterInside);
                if (inside > 0) return inside;
                double outside = Num(PressurePartContextType.PipeDiameterOutside), wall = Num(PressurePartContextType.PipeThickness);
                if (outside > 0) return outside - 2 * wall;
            }
            double nominal = Num(PressurePartContextType.ConnectionPointNominalDiameter);
            if (nominal > 0) return nominal;
            string text = string.IsNullOrWhiteSpace(description) ? Safe(() => s?.Description, "") : description;
            Match m = Regex.Match(text ?? "", @"(\d+(?:[.,]\d+)?)\s*mm", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(text ?? "", @"(\d+(?:[.,]\d+)?)");
            return m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? Metres(d) : 0;
        }

        private static double Metres(double v) => v > 3 ? v / 1000.0 : v;

        // =====================================================================================
        // Working out each group's size
        // =====================================================================================

        /// <summary>
        /// The size each group's target gives it (for a nearest-size target, the family's size nearest the group's inside
        /// size), or the reason it gets none. Gravity families are read through a transaction that is thrown away.
        /// </summary>
        public static void Resolve(Transaction tr, Database db, SwapGroup g)
        {
            g.ToSize = "";
            g.Problem = null;
            g.ToValues = null;
            g.ToPressureGuid = null;
            g.ToChange = double.NaN;
            SwapTarget t = g.Target;
            if (t == null) return;
            try { ResolveSize(tr, db, g, t); }
            finally
            {
                if (g.Problem == null && g.Width > 0)
                {
                    if (g.ToValues != null && Width(g.ToValues) > 0) g.ToChange = Width(g.ToValues) - g.Width;
                    else if (g.ToPressureGuid != null)
                    {
                        PressurePartSize s = PressureSizes(tr, t.PressureListId, g.PressureDomain).FirstOrDefault(x => Safe(() => x.PartSizeGuid, "") == g.ToPressureGuid);
                        double d = s == null ? 0 : PressureDiameter(s, null, g.Kind == SwapKind.PressurePipe);
                        if (d > 0) g.ToChange = d - g.Width;
                    }
                }
            }
        }

        /// <summary>A size's inside width (or diameter), metres.</summary>
        private static double Width(Dictionary<PartContextType, double> v)
        {
            foreach (PartContextType c in new[] { PartContextType.PipeInnerDiameter, PartContextType.PipeInnerWidth, PartContextType.StructInnerDiameter, PartContextType.StructInnerWidth })
                if (v.TryGetValue(c, out double x) && x > 0) return x;
            return 0;
        }

        private static void ResolveSize(Transaction tr, Database db, SwapGroup g, SwapTarget t)
        {
            if (g.IsPressure != (t.Gravity == null) || (t.Gravity != null && t.Gravity.Domain != g.Domain)) { g.Problem = "the target is a part of another kind"; return; }

            if (!g.IsPressure)
            {
                if (!t.Nearest) { g.ToSize = t.Gravity.SizeName; g.ToValues = t.Gravity.Values; return; }
                if (g.Width <= 0) { g.Problem = "its inside size could not be read"; return; }
                (string name, Dictionary<PartContextType, double> values) = NearestGravity(db, t.Gravity, g.Width, g.Height);
                if (values == null) { g.Problem = $"{t.Gravity.FamilyName} offers no size to choose from"; return; }
                g.ToSize = name;
                g.ToValues = values;
                return;
            }

            List<PressurePartSize> sizes = PressureSizes(tr, t.PressureListId, g.PressureDomain);
            if (!t.Nearest)
            {
                PressurePartSize exact = sizes.FirstOrDefault(s => Safe(() => s.PartSizeGuid, "") == t.PressureSizeGuid);
                if (exact == null) { g.Problem = $"{t.PressureSizeName} is not in the list {t.PressureListName}"; return; }
                g.ToSize = Safe(() => exact.Description, t.PressureSizeName);
                g.ToPressureGuid = t.PressureSizeGuid;
                return;
            }
            if (g.Width <= 0) { g.Problem = "its inside size could not be read"; return; }
            // The chosen size's family (pipe material and class, fitting type and angle), or its part type when the list names no family.
            PressurePartSize chosen = sizes.FirstOrDefault(s => Safe(() => s.PartSizeGuid, "") == t.PressureSizeGuid);
            PressurePartType? type = chosen == null ? (PressurePartType?)null : Safe(() => chosen.PartType, PressurePartType.PressurePipe);
            bool pipe = g.Kind == SwapKind.PressurePipe;
            PressurePartSize best = null;
            double bestOff = double.MaxValue;
            foreach (PressurePartSize s in sizes)
            {
                string family = PressureFamily(s);
                if (!string.IsNullOrEmpty(t.PressureFamily) ? !family.Equals(t.PressureFamily, StringComparison.OrdinalIgnoreCase) : type.HasValue && Safe(() => s.PartType, type.Value) != type.Value) continue;
                double d = PressureDiameter(s, null, pipe);
                if (d <= 0) continue;
                double off = Math.Abs(d - g.Width);
                if (off < bestOff - 1e-9) { bestOff = off; best = s; }
            }
            if (best == null) { g.Problem = $"no size of {t.Label.Replace(" · nearest size", "")} has a readable diameter"; return; }
            g.ToSize = Safe(() => best.Description, "");
            g.ToPressureGuid = Safe(() => best.PartSizeGuid, "");
        }

        /// <summary>The family's size (in the list, else in the catalog) nearest an inside width and height or length.</summary>
        private static (string name, Dictionary<PartContextType, double> values) NearestGravity(Database db, PartPick pick, double w, double h)
        {
            string bestName = null;
            Dictionary<PartContextType, double> best = null;
            double bestOff = double.MaxValue;
            void Try(string name, Dictionary<PartContextType, double> v)
            {
                double off = Offset(v, w, h);
                if (off < bestOff - 1e-9) { bestOff = off; best = v; bestName = name; }
            }
            // A family read from the catalog is added to the list for reading only; the transaction is thrown away.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    ObjectId famId = PartsListTools.EnsureFamily(tr, pick.PartsListId, pick.Domain, pick.FamilyGuid, out bool added);
                    if (!famId.IsNull)
                    {
                        PartFamily fam = (PartFamily)tr.GetObject(famId, OpenMode.ForRead);
                        List<Dictionary<PartContextType, double>> listed = new List<Dictionary<PartContextType, double>>();
                        // Sizes already in the list win a tie, so nothing is added that the list has an equal of.
                        foreach ((ObjectId _, string name, Dictionary<PartContextType, double> v) in PartsListTools.ListSizes(tr, fam))
                        {
                            Try(added ? PartsListTools.Describe(v) : name, v);
                            listed.Add(v);
                        }
                        foreach (Dictionary<PartContextType, double> v in PartsListTools.CatalogSizes(fam))
                            if (!listed.Any(x => PartsListTools.Same(v, x))) Try(PartsListTools.Describe(v), v);
                    }
                }
                finally { tr.Abort(); }
            }
            return (bestName, best);
        }

        /// <summary>How far a size's inside dimensions are from a width and height (pipes) or width and length (structures).</summary>
        private static double Offset(Dictionary<PartContextType, double> v, double w, double h)
        {
            double Get(PartContextType c) => v.TryGetValue(c, out double x) ? x : 0;
            double a, b;
            if (Get(PartContextType.PipeInnerDiameter) > 0) a = b = Get(PartContextType.PipeInnerDiameter);
            else if (Get(PartContextType.PipeInnerWidth) > 0) { a = Get(PartContextType.PipeInnerWidth); b = Get(PartContextType.PipeInnerHeight); }
            else if (Get(PartContextType.StructInnerDiameter) > 0) a = b = Get(PartContextType.StructInnerDiameter);
            else { a = Get(PartContextType.StructInnerWidth); b = Get(PartContextType.StructInnerLength); }
            if (a <= 0) return double.MaxValue;
            if (b <= 0) b = a;
            return Math.Abs(a - w) + Math.Abs(b - (h > 0 ? h : w));
        }

        // =====================================================================================
        // Applying
        // =====================================================================================

        /// <summary>A structure's levels and placement before its swap.</summary>
        private sealed class StructureLevels
        {
            public Point3d Location;
            public double Rotation, Rim, Sump, Depth;
            public StructureControlSumpType By;
            public bool AutoRim;
        }

        /// <summary>
        /// Swaps every group with a target. Everything that must stay is read first: the ends of each gravity pipe that is
        /// swapped or meets a swapped structure, each swapped structure's position and levels, and the connections of the
        /// pressure pipes involved. Then structures, fittings and appurtenances are swapped before pipes, and finally the
        /// pipe ends (inverts or centrelines), the structure levels and any pressure connection that came apart are put back:
        /// Civil 3D moves connected pipe ends and re-applies its rules on each swap.
        /// </summary>
        /// <param name="holdCentre">False: gravity pipes keep their inverts (the centre moves by half the change in inside height); true: their centrelines.</param>
        public static NetworkEditOps.Counts Apply(Transaction tr, Database db, List<SwapGroup> groups, bool holdCentre, Editor ed)
        {
            NetworkEditOps.Counts c = new NetworkEditOps.Counts();
            Dictionary<string, (ObjectId family, ObjectId size)> gravity = new Dictionary<string, (ObjectId, ObjectId)>();
            Dictionary<string, PressurePartSize> pressure = new Dictionary<string, PressurePartSize>();

            // The work, structures and fittings first.
            List<(SwapGroup g, PartPick pick, ObjectId id)> work = new List<(SwapGroup, PartPick, ObjectId)>();
            foreach (SwapGroup g in groups)
            {
                if (g.Target == null) continue;
                Resolve(tr, db, g);
                if (g.Problem != null)
                {
                    ed.WriteMessage($"\n[Skipped] {g.Ids.Count} {SwapGroup.KindText(g.Kind).ToLowerInvariant()}(s) of {g.Current}: {g.Problem}.");
                    c.Skipped += g.Ids.Count;
                    continue;
                }
                PartPick pick = null;
                if (!g.IsPressure)
                {
                    PartPick t = g.Target.Gravity;
                    pick = new PartPick
                    {
                        Domain = t.Domain, PartsListId = t.PartsListId, PartsListName = t.PartsListName, FamilyGuid = t.FamilyGuid, FamilyName = t.FamilyName,
                        SizeId = g.Target.Nearest ? ObjectId.Null : t.SizeId, SizeName = g.ToSize, Values = new Dictionary<PartContextType, double>(g.ToValues), SwitchNetwork = t.SwitchNetwork
                    };
                }
                foreach (ObjectId id in g.Ids) if (!id.IsErased) work.Add((g, pick, id));
            }
            int Order(SwapKind k) => k == SwapKind.Structure || k == SwapKind.Fitting || k == SwapKind.Appurtenance ? 0 : 1;
            work = work.OrderBy(w => Order(w.g.Kind)).ToList();

            // What must stay.
            Dictionary<ObjectId, (Point3d start, Point3d end, double inner)> ends = new Dictionary<ObjectId, (Point3d, Point3d, double)>();
            Dictionary<ObjectId, StructureLevels> levels = new Dictionary<ObjectId, StructureLevels>();
            Dictionary<ObjectId, List<(int port, ObjectId other, int otherPort)>> links = new Dictionary<ObjectId, List<(int, ObjectId, int)>>();
            void KeepEnds(ObjectId pipeId)
            {
                if (!ends.ContainsKey(pipeId) && !pipeId.IsErased && tr.GetObject(pipeId, OpenMode.ForRead) is Pipe p) ends[pipeId] = (p.StartPoint, p.EndPoint, Safe(() => p.InnerHeight, 0));
            }
            void KeepLinks(ObjectId pipeId)
            {
                if (links.ContainsKey(pipeId) || pipeId.IsErased || !(tr.GetObject(pipeId, OpenMode.ForRead) is PressurePipe pp)) return;
                List<(int, ObjectId, int)> list = new List<(int, ObjectId, int)>();
                for (int i = 0; i < Safe(() => pp.ConnectionCount, 0); i++)
                    using (PressurePartConnection con = Safe(() => pp.GetConnectionAt(i), null))
                        if (con != null && !con.Open && !con.ConnectedId.IsNull) list.Add((i, con.ConnectedId, con.ConnectedIndex));
                links[pipeId] = list;
            }
            foreach ((SwapGroup g, PartPick _, ObjectId id) in work)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is Pipe) KeepEnds(id);
                else if (obj is Structure st)
                {
                    levels[id] = new StructureLevels
                    {
                        Location = st.Location, Rotation = st.Rotation, Rim = st.RimElevation, By = Safe(() => st.ControlSumpBy, StructureControlSumpType.ByElevation),
                        Sump = Safe(() => st.SumpElevation, double.NaN), Depth = Safe(() => st.SumpDepth, double.NaN), AutoRim = Safe(() => st.AutomaticRimSurfaceAdjustment, false)
                    };
                }
                else if (obj is PressurePipe) KeepLinks(id);
                else if (obj is PressurePart part)
                    for (int i = 0; i < Safe(() => part.ConnectionCount, 0); i++)
                        using (PressurePartConnection con = Safe(() => part.GetConnectionAt(i), null))
                            if (con != null && !con.Open) KeepLinks(con.ConnectedId);
            }

            // The pipes that meet a swapped structure, from each network's pipes.
            foreach (ObjectId netId in levels.Keys.Select(id => ((Structure)tr.GetObject(id, OpenMode.ForRead)).NetworkId).Distinct().ToList())
                foreach (ObjectId pid in ((Network)tr.GetObject(netId, OpenMode.ForRead)).GetPipeIds())
                    if (tr.GetObject(pid, OpenMode.ForRead) is Pipe p && (levels.ContainsKey(p.StartStructureId) || levels.ContainsKey(p.EndStructureId))) KeepEnds(pid);

            HashSet<ObjectId> swappedPipes = new HashSet<ObjectId>();
            foreach ((SwapGroup g, PartPick pick, ObjectId id) in work)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                string name = (obj as Part)?.Name ?? (obj as PressurePart)?.Name ?? id.Handle.ToString();
                {
                    try
                    {
                        if (obj is Part part)
                        {
                            string key = $"{part.NetworkId.Handle}|{pick.FamilyGuid}|{pick.SizeName}|{pick.PartsListId.Handle}";
                            if (!gravity.TryGetValue(key, out var ids))
                            {
                                // Once per network: a family from a list built on another catalog cannot be added to this network's list.
                                try { ids = PartsListTools.Resolve(tr, part.NetworkId, ObjectId.Null, pick, c.Log); }
                                catch (Exception ex)
                                {
                                    ids = (ObjectId.Null, ObjectId.Null);
                                    string network = Safe(() => part.NetworkName, "its network");
                                    ed.WriteMessage($"\n[Skipped] {pick.FamilyName} cannot be put in the parts list of {network}: {ex.Message.TrimEnd('.')}. " +
                                                    "Choose \"Switch the network to this list\" in the part chooser, or a family of the network's own catalog.");
                                    c.Log.Add($"{pick.FamilyName} could not be added to the parts list of {network} ({ex.Message.TrimEnd('.')}); its parts were left as they are.");
                                }
                                gravity[key] = ids;
                            }
                            if (ids.size.IsNull) { c.Skipped++; continue; }
                            if (part.PartFamilyId == ids.family && Safe(() => part.GetPartSizeId(), ObjectId.Null) == ids.size) { c.Skipped++; continue; }
                            part.SwapPartFamilyAndSize(ids.family, ids.size);
                            // Civil 3D can leave the part as it was without a word (a part it cannot build: family or catalog missing).
                            if (Safe(() => part.GetPartSizeId(), ObjectId.Null) != ids.size)
                                throw new InvalidOperationException("Civil 3D left it unchanged; it cannot build that part here (its family is from another catalog, or the catalog is missing)");
                            if (part is Pipe) swappedPipes.Add(id);
                            c.Done++;
                        }
                        else if (obj is PressurePart pp)
                        {
                            string key = $"{pp.NetworkId.Handle}|{g.ToPressureGuid}";
                            if (!pressure.TryGetValue(key, out PressurePartSize size)) pressure[key] = size = PressureSizeIn(tr, pp.NetworkId, g.Target.PressureListId, g.ToPressureGuid, c.Log);
                            if (size == null) { ed.WriteMessage($"\n[Skipped] <{name}>: {g.ToSize} could not be put in its network's parts list."); c.Skipped++; continue; }
                            if (Safe(() => pp.GetPartSize()?.PartSizeGuid, null) == g.ToPressureGuid) { c.Skipped++; continue; }
                            pp.SwapPartSize(size);
                            c.Done++;
                        }
                    }
                    catch (Exception ex) { ed.WriteMessage($"\n[Error] Swapping <{name}> to {g.ToSize}: {ex.Message}"); c.Failed++; }
                }
            }

            // Pipe ends: a swapped pipe keeps its inverts (its centre moves by half the change in inside height) or its
            // centreline; a pipe that only meets a swapped structure keeps its ends as they were.
            foreach (KeyValuePair<ObjectId, (Point3d start, Point3d end, double inner)> kv in ends)
            {
                if (kv.Key.IsErased || !(tr.GetObject(kv.Key, OpenMode.ForWrite) is Pipe pipe)) continue;
                double inner = Safe(() => pipe.InnerHeight, kv.Value.inner);
                Vector3d up = new Vector3d(0, 0, holdCentre || kv.Value.inner <= 0 || !swappedPipes.Contains(kv.Key) ? 0 : (inner - kv.Value.inner) / 2.0);
                try
                {
                    if (!pipe.StartPoint.IsEqualTo(kv.Value.start + up)) pipe.StartPoint = kv.Value.start + up;
                    if (!pipe.EndPoint.IsEqualTo(kv.Value.end + up)) pipe.EndPoint = kv.Value.end + up;
                }
                catch (Exception ex) { ed.WriteMessage($"\n[Warning] The ends of pipe <{pipe.Name}> could not be put back: {ex.Message}"); }
            }

            // Structures: position, rotation, rim (unless it follows the surface) and sump, once the pipes are back.
            int surfaceRims = 0;
            foreach (KeyValuePair<ObjectId, StructureLevels> kv in levels)
            {
                if (kv.Key.IsErased || !(tr.GetObject(kv.Key, OpenMode.ForWrite) is Structure st)) continue;
                StructureLevels l = kv.Value;
                try { if (new Point2d(st.Location.X, st.Location.Y).GetDistanceTo(new Point2d(l.Location.X, l.Location.Y)) > 1e-6) st.Location = new Point3d(l.Location.X, l.Location.Y, st.Location.Z); } catch (Exception) { }
                try { if (Math.Abs(st.Rotation - l.Rotation) > 1e-9) st.Rotation = l.Rotation; } catch (Exception) { }
                if (l.AutoRim) { if (Math.Abs(st.RimElevation - l.Rim) > 0.001) surfaceRims++; }
                else try { if (Math.Abs(st.RimElevation - l.Rim) > 1e-6) st.RimElevation = l.Rim; } catch (Exception) { }
                try
                {
                    if (st.ControlSumpBy != l.By) st.ControlSumpBy = l.By;
                    if (l.By == StructureControlSumpType.ByElevation) { if (!double.IsNaN(l.Sump) && Math.Abs(st.SumpElevation - l.Sump) > 1e-6) st.SumpElevation = l.Sump; }
                    else if (!double.IsNaN(l.Sump))
                    {
                        // A depth is measured differently by the new part: the depth is corrected until the sump is back at its level.
                        for (int i = 0; i < 3 && Math.Abs(st.SumpElevation - l.Sump) > 1e-4; i++) st.SumpDepth = st.SumpDepth + (st.SumpElevation - l.Sump);
                    }
                }
                catch (Exception) { }
            }
            if (surfaceRims > 0)
                c.Log.Add($"{surfaceRims} structure rim(s) follow the surface (\"Automatic surface adjustment\"), so Civil 3D set them on the surface again for the new part.");

            // Pressure connections a swap took apart are made again.
            int relinked = 0, lost = 0;
            foreach (KeyValuePair<ObjectId, List<(int port, ObjectId other, int otherPort)>> kv in links)
            {
                if (kv.Key.IsErased || !(tr.GetObject(kv.Key, OpenMode.ForWrite) is PressurePipe pp)) continue;
                foreach ((int port, ObjectId other, int otherPort) in kv.Value)
                {
                    bool still;
                    using (PressurePartConnection con = Safe(() => pp.GetConnectionAt(port), null)) still = con != null && !con.Open && con.ConnectedId == other;
                    if (still) continue;
                    try
                    {
                        if (other.IsErased) throw new InvalidOperationException("the part it met is gone");
                        PressurePart o = (PressurePart)tr.GetObject(other, OpenMode.ForWrite);
                        if (o is PressurePipe) pp.ConnectToPipe(port, other, otherPort);
                        else o.ConnectToPipe(otherPort, kv.Key, port);
                        relinked++;
                    }
                    catch (Exception ex)
                    {
                        lost++;
                        ed.WriteMessage($"\n[Warning] Pressure pipe <{pp.Name}> came apart from <{Safe(() => ((PressurePart)tr.GetObject(other, OpenMode.ForRead)).Name, other.Handle.ToString())}> and could not be connected again: {ex.Message}");
                    }
                }
            }
            if (relinked > 0) c.Log.Add($"{relinked} pressure connection(s) the swap took apart were made again.");
            if (lost > 0) c.Log.Add($"{lost} pressure connection(s) came apart and could not be made again (see the command line).");
            return c;
        }

        /// <summary>The pressure size from the network's own parts list, copied there from the chosen list when it lacks it.</summary>
        private static PressurePartSize PressureSizeIn(Transaction tr, ObjectId networkId, ObjectId listId, string guid, List<string> log)
        {
            PressurePartList from = (PressurePartList)tr.GetObject(listId, OpenMode.ForRead);
            ObjectId ownId = tr.GetObject(networkId, OpenMode.ForRead) is PressurePipeNetwork net ? Safe(() => net.PartsListId, ObjectId.Null) : ObjectId.Null;
            if (ownId.IsNull || ownId == listId) return Safe(() => from.GetPart(guid), null);
            PressurePartList own = (PressurePartList)tr.GetObject(ownId, OpenMode.ForRead);
            PressurePartSize have = Safe(() => own.GetPart(guid), null);
            if (have != null && Safe(() => have.IsValid, true)) return have;
            PressurePartSize part = Safe(() => from.GetPart(guid), null);
            if (part == null) return null;
            own.UpgradeOpen();
            own.AddPart(part);
            log.Add($"Added {Safe(() => part.Description, guid)} to the pressure parts list \"{own.Name}\".");
            return Safe(() => own.GetPart(guid), null);
        }

        // =====================================================================================
        // Saved swap sets
        // =====================================================================================

        /// <summary>The groups' targets as rules; a nearest-size target applies to every size of its group's family.</summary>
        public static List<SwapRule> RulesOf(IEnumerable<SwapGroup> groups)
        {
            List<SwapRule> rules = new List<SwapRule>();
            foreach (SwapGroup g in groups.Where(x => x.Target != null))
            {
                SwapTarget t = g.Target;
                SwapRule r = new SwapRule { Kind = g.Kind.ToString(), Family = g.Family, Size = t.Nearest ? "" : g.Size, Nearest = t.Nearest };
                if (t.Gravity != null)
                {
                    r.ListName = t.Gravity.PartsListName; r.FamilyGuid = t.Gravity.FamilyGuid; r.FamilyName = t.Gravity.FamilyName; r.SizeName = t.Gravity.SizeName;
                    r.Values = t.Gravity.Values.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                }
                else { r.ListName = t.PressureListName; r.FamilyName = t.PressureFamily; r.SizeName = t.PressureSizeName; r.PressureSizeGuid = t.PressureSizeGuid; }
                if (!rules.Any(x => x.Kind == r.Kind && string.Equals(x.Family, r.Family, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Size, r.Size, StringComparison.OrdinalIgnoreCase))) rules.Add(r);
            }
            return rules;
        }

        /// <summary>
        /// Gives each group the target of the set's rule for its kind and family (a rule for its own size before one for any
        /// size). Returns how many groups got one; rules whose parts list is not in this drawing are named in <paramref name="missing"/>.
        /// </summary>
        public static int ApplySet(Transaction tr, Database db, SwapSet set, List<SwapGroup> groups, List<string> missing)
        {
            int count = 0;
            foreach (SwapGroup g in groups)
            {
                SwapRule rule = set.Rules.Where(r => r.Kind == g.Kind.ToString() && string.Equals(r.Family ?? "", g.Family, StringComparison.OrdinalIgnoreCase)
                                                     && (string.IsNullOrEmpty(r.Size) || string.Equals(r.Size, g.Size, StringComparison.OrdinalIgnoreCase)))
                                         .OrderBy(r => string.IsNullOrEmpty(r.Size) ? 1 : 0).FirstOrDefault();
                if (rule == null) continue;
                SwapTarget t = TargetOf(tr, db, rule, g);
                if (t == null) { if (!missing.Contains(rule.ListName)) missing.Add(rule.ListName); continue; }
                g.Target = t;
                count++;
            }
            return count;
        }

        private static SwapTarget TargetOf(Transaction tr, Database db, SwapRule r, SwapGroup g)
        {
            if (g.IsPressure)
            {
                (ObjectId id, string name) list = PressureLists(tr, db).FirstOrDefault(l => l.name.Equals(r.ListName ?? "", StringComparison.OrdinalIgnoreCase));
                if (list.id.IsNull) return null;
                return new SwapTarget { Nearest = r.Nearest, PressureListId = list.id, PressureListName = list.name, PressureFamily = r.FamilyName ?? "", PressureSizeGuid = r.PressureSizeGuid ?? "", PressureSizeName = r.SizeName ?? "" };
            }
            ObjectId listId = ObjectId.Null;
            string listName = r.ListName;
            foreach (ObjectId id in CivilApplication.ActiveDocument.Styles.PartsListSet)
                if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl && pl.Name.Equals(r.ListName ?? "", StringComparison.OrdinalIgnoreCase)) { listId = id; listName = pl.Name; break; }
            if (listId.IsNull) return null;
            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            foreach (KeyValuePair<string, double> kv in r.Values ?? new Dictionary<string, double>())
                if (Enum.TryParse(kv.Key, out PartContextType ctx)) values[ctx] = kv.Value;
            PartPick pick = new PartPick { Domain = g.Domain, PartsListId = listId, PartsListName = listName, FamilyGuid = r.FamilyGuid ?? "", FamilyName = r.FamilyName ?? "", SizeId = ObjectId.Null, SizeName = r.SizeName ?? "", Values = values };
            return new SwapTarget { Nearest = r.Nearest, Gravity = pick };
        }
    }
}
