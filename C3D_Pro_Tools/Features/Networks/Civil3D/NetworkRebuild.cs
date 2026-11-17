using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(C3DProTools.Features.Networks.Civil3D.NetworkRebuild))]

// C3DPROREBUILD: a pipe network that cannot be edited (a data-shortcut reference whose source is gone, so it can neither be
// repaired nor promoted) rebuilt as an ordinary network of this drawing: every structure at its place, rotation, rim and sump;
// every pipe with its ends and curve, connected to the new structures; names, descriptions, styles and layers. A dead reference
// has no parts list and its parts cannot be copied (a clone stays a reference: tested 2026-11-17), so each size is made from
// the drawing's active catalog. The reference itself is only read. Report lists what would be rebuilt; Rebuild does it, all or
// nothing (one UNDO).
namespace C3DProTools.Features.Networks.Civil3D
{
    public sealed class NetworkRebuild
    {
        public const string Suffix = " - REBUILT";

        [CommandMethod("C3DPRO", "C3DPROREBUILD", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var kw = new PromptKeywordOptions("\nRebuild networks") { AllowNone = true };
            kw.Keywords.Add("Report"); kw.Keywords.Add("Rebuild");
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            bool write = kr.StringResult == "Rebuild";
            PromptResult nr = ed.GetString(new PromptStringOptions("\nNetworks (wildcards; Enter for every data-shortcut reference) <refs>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            ed.WriteMessage("\n" + RunReport.Write(Execute(doc, nr.StringResult.Trim(), write), "C3DPROREBUILD", write ? "Rebuild" : "Report") + "\n");
            if (write) ViewHelper.Regen(doc);
        }

        /// <summary>
        /// The networks to rebuild: those named (wildcards), or every data-shortcut reference when no name is given; each name
        /// once (a dead reference is listed twice by Civil 3D) and never one this tool made.
        /// </summary>
        internal static List<Network> Chosen(Transaction tr, CivilDocument civil, string names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Network>();
            foreach (ObjectId id in civil.GetPipeNetworkIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Network n && !n.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) &&
                    (names.Length == 0 ? IsReference(n) : NamePatterns.Matches(names, n.Name)) && seen.Add(n.Name))
                    list.Add(n);
            return list;
        }

        internal static bool IsReference(Network n)
        {
            try { return n.IsReferenceObject; } catch (Exception) { return false; }
        }

        internal static string Execute(Document doc, string names, bool write)
        {
            var log = new StringBuilder();
            log.AppendLine($"C3DPROREBUILD {(write ? "Rebuild" : "Report")} on {Path.GetFileName(doc.Name)}, networks \"{(names.Length == 0 ? "every data-shortcut reference" : names)}\"");
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                    List<Network> nets = Chosen(tr, civil, names);
                    if (nets.Count == 0) log.AppendLine("No network to rebuild" + (names.Length == 0 ? ": the drawing has no data-shortcut reference network." : $" is named \"{names}\"."));
                    var parts = new Dictionary<string, (ObjectId fam, ObjectId size, string error)>();
                    bool ok = true;
                    foreach (Network n in nets) ok &= Rebuild(tr, civil, n, write, parts, log);
                    // All or nothing: a network left half built would be worse than none.
                    if (write && ok) tr.Commit(); else tr.Abort();
                    if (write && !ok) log.AppendLine("Nothing was changed: a network could not be rebuilt completely (see above).");
                }
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROREBUILD failed", ex);
                log.AppendLine($"C3DPROREBUILD failed: {ex.Message}. Nothing was changed.");
            }
            return log.ToString();
        }

        /// <summary>Rebuilds one network (or, without <paramref name="write"/>, says what it holds and which part each size gets). False when not complete.</summary>
        private static bool Rebuild(Transaction tr, CivilDocument civil, Network src, bool write, Dictionary<string, (ObjectId fam, ObjectId size, string error)> parts, StringBuilder log)
        {
            List<Structure> structures = src.GetStructureIds().Live().Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Structure>().ToList();
            List<Pipe> pipes = src.GetPipeIds().Live().Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Pipe>().ToList();
            int curved = pipes.Count(p => p.SubEntityType == PipeSubEntityType.Curved);
            log.AppendLine($"{src.Name}{(IsReference(src) ? " (data-shortcut reference)" : "")}: {structures.Count} structures, {pipes.Count} pipes ({curved} curved).");

            ObjectId listId = ObjectId.Null;
            try { listId = src.PartsListId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (listId.IsNull || !listId.IsValid) listId = civil.Styles.PartsListSet.Cast<ObjectId>().FirstOrDefault();
            if (listId.IsNull) { log.AppendLine($"  the drawing has no parts list to build the parts from."); return false; }

            // Every size first, so a report says what each one becomes and a rebuild stops before drawing anything.
            var failures = new List<string>();
            foreach (Part part in structures.Cast<Part>().Concat(pipes))
            {
                try { PartFor(tr, listId, part, parts, log); }
                catch (Exception ex) { failures.Add($"{part.Name}: {ex.Message}"); }
            }
            if (failures.Count > 0 || !write)
            {
                foreach (string f in failures.Take(10)) log.AppendLine("  cannot be made: " + f);
                if (failures.Count > 10) log.AppendLine($"  ... {failures.Count - 10} more");
                if (failures.Count > 0 && CatalogNames().IndexOf("Imperial", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // The usual cause (AF00020136002H 000004, 2026-11-17): the drawing's pipe catalog is the imperial one, whose
                    // families have no metric sizes and cannot hold the any-size families (they are made from metric ones).
                    string catalogs = CatalogNames();
                    log.AppendLine($"To rebuild: this drawing uses the {catalogs} part catalogs. Set Pipe Network Catalog (Home > Create Design) to Metric Pipes and " +
                                   "Metric Structures, set up the any-size families once (Parts Lists window > \"Set up Method A\") and restart Civil 3D, then run C3DPROREBUILD again.");
                }
                return failures.Count == 0;
            }

            string name = src.Name + Suffix;
            ObjectId newId = Network.Create(civil, ref name);
            var net = (Network)tr.GetObject(newId, OpenMode.ForWrite);
            net.PartsListId = listId;
            try { if (!src.ReferenceSurfaceId.IsNull) net.ReferenceSurfaceId = src.ReferenceSurfaceId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { net.ReferenceAlignmentId = src.ReferenceAlignmentId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

            var map = new Dictionary<ObjectId, ObjectId>();
            int joined = 0, sourceJoined = 0;
            foreach (Structure st in structures)
            {
                try
                {
                    (ObjectId fam, ObjectId size) = PartFor(tr, listId, st, parts, log);
                    ObjectId id = ObjectId.Null;
                    net.AddStructure(fam, size, st.Location, st.Rotation, ref id, false);
                    var ns = (Structure)tr.GetObject(id, OpenMode.ForWrite);
                    try { ns.AutomaticRimSurfaceAdjustment = false; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    ns.RimElevation = st.RimElevation;
                    try { ns.ControlSumpBy = st.ControlSumpBy; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (st.ControlSumpBy == StructureControlSumpType.ByElevation) ns.SumpElevation = st.SumpElevation; else ns.SumpDepth = st.SumpDepth;
                    if (!ns.Location.IsEqualTo(st.Location)) ns.Location = st.Location;
                    CopyLook(st, ns);
                    map[st.ObjectId] = id;
                }
                catch (Exception ex) { failures.Add($"structure {st.Name}: {ex.Message}"); }
            }
            foreach (Pipe p in pipes)
            {
                try
                {
                    (ObjectId fam, ObjectId size) = PartFor(tr, listId, p, parts, log);
                    Point3d s = p.StartPoint, e = p.EndPoint;
                    ObjectId id = ObjectId.Null;
                    if (p.SubEntityType == PipeSubEntityType.Curved)
                    {
                        if (!PipeArcGeometry.TryGetGravityArc(p, out ArcInfo arc) || !NetworkEditOps.TryOffsetArc(s, e, arc, 0, out _, out _, out Point2d center, out bool clockwise))
                            throw new InvalidOperationException("its curve could not be read");
                        using (var g = new PipeCurveGeometry(s, center, e, clockwise)) id = net.AddCurvePipe(fam, size, g, false);
                    }
                    else using (var line = new LineSegment3d(s, e)) net.AddLinePipe(fam, size, line, ref id, false);
                    if (id.IsNull) throw new InvalidOperationException("Civil 3D did not return the new pipe");
                    var np = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
                    sourceJoined += (p.StartStructureId.IsNull ? 0 : 1) + (p.EndStructureId.IsNull ? 0 : 1);
                    joined += Connect(np, ConnectorPositionType.Start, p.StartStructureId, map);
                    joined += Connect(np, ConnectorPositionType.End, p.EndStructureId, map);
                    // Connecting may pull an end onto the structure: the rebuilt pipe keeps the original's 3D ends.
                    if (!np.StartPoint.IsEqualTo(s)) np.StartPoint = s;
                    if (!np.EndPoint.IsEqualTo(e)) np.EndPoint = e;
                    // Putting an end back where the original's is drops the connection when that point is far from the structure
                    // (3 MV ducts on Test.dwg, 3.5 m away, 2026-11-17); connecting again would pull the end there. The exact end
                    // is kept and the pipe is listed below as not connected.
                    CopyLook(p, np);
                }
                catch (Exception ex) { failures.Add($"pipe {p.Name}: {ex.Message}"); }
            }
            // Connecting pipes lets Civil 3D move a sump set by depth (2.3 m on Test.dwg, 2026-11-17): rim and sump are set
            // again once every pipe is in, the sump by elevation, so each structure is exactly as the original.
            foreach (Structure st in structures.Where(s => map.ContainsKey(s.ObjectId)))
            {
                var ns = (Structure)tr.GetObject(map[st.ObjectId], OpenMode.ForWrite);
                try
                {
                    if (!ns.Location.IsEqualTo(st.Location)) ns.Location = st.Location;
                    ns.RimElevation = st.RimElevation;
                    ns.ControlSumpBy = StructureControlSumpType.ByElevation;
                    ns.SumpElevation = st.SumpElevation;
                    // Setting the elevation alone left sumps 18–79 mm off (Test.dwg, 2026-11-17): the height too.
                    if (Math.Abs(ns.SumpElevation - st.SumpElevation) > 0.0005) ns.RimToSumpHeight = st.RimElevation - st.SumpElevation;
                }
                catch (Exception ex) { failures.Add($"structure {st.Name}: rim or sump could not be set ({ex.Message})"); }
            }

            // Checked against the original: the largest difference in structure place, rim and sump, and in pipe ends.
            double place = 0, rim = 0, sump = 0;
            foreach (Structure st in structures.Where(s => map.ContainsKey(s.ObjectId)))
            {
                var ns = (Structure)tr.GetObject(map[st.ObjectId], OpenMode.ForRead);
                place = Math.Max(place, ns.Location.DistanceTo(st.Location));
                rim = Math.Max(rim, Math.Abs(ns.RimElevation - st.RimElevation));
                double ds = Math.Abs(ns.SumpElevation - st.SumpElevation);
                // A sump above a connected pipe's invert in the original: Civil 3D lowers a new structure's sump to the pipe
                // (Test.dwg, 3 MV manholes, 18–79 mm, 2026-11-17). Listed, not refused: the structure is otherwise exact.
                if (ds > 0.001) log.AppendLine($"  note: {st.Name} sump {ns.SumpElevation:0.000} (original {st.SumpElevation:0.000}, {ds * 1000:0} mm lower): Civil 3D keeps a sump at or below the lowest connected pipe invert");
                sump = Math.Max(sump, ds);
            }
            // The footprint: outer width and length as the original's (review finding: a fallback could grow it).
            double footprint = 0;
            foreach (Structure st in structures.Where(x => map.ContainsKey(x.ObjectId)))
            {
                var ns = (Structure)tr.GetObject(map[st.ObjectId], OpenMode.ForRead);
                footprint = Math.Max(footprint, Math.Max(Math.Abs(ns.DiameterOrWidth - st.DiameterOrWidth), Math.Abs(ns.Length - st.Length)));
            }
            log.AppendLine($"  structure footprint differs by up to {footprint * 1000:0.#} mm");
            double stMax = Math.Max(place, Math.Max(rim, footprint));

            // Every pipe end connected where the original's is, checked after all geometry changes (review finding).
            int lost = 0, wrong = 0;
            var rebuiltByName = net.GetPipeIds().Live().Select(id => (Pipe)tr.GetObject(id, OpenMode.ForRead)).GroupBy(q => q.Name).ToDictionary(g => g.Key, g => g.ToList());
            foreach (Pipe p in pipes)
            {
                if (!rebuiltByName.TryGetValue(p.Name, out List<Pipe> same) || same.Count != 1) continue;
                Pipe q = same[0];
                // A connection to another structure than the original's is wrong and refused; one left off (the original end
                // is far from its structure, where Civil 3D will not connect without moving it) is listed.
                bool Wrong(ObjectId was, ObjectId now) => !now.IsNull && !(map.TryGetValue(was, out ObjectId m) && m == now);
                if (Wrong(p.StartStructureId, q.StartStructureId) || Wrong(p.EndStructureId, q.EndStructureId)) wrong++;
                bool startOk = p.StartStructureId.IsNull || map.TryGetValue(p.StartStructureId, out ObjectId s1) && q.StartStructureId == s1;
                bool endOk = p.EndStructureId.IsNull || map.TryGetValue(p.EndStructureId, out ObjectId e1) && q.EndStructureId == e1;
                if (!startOk || !endOk)
                {
                    lost++;
                    string N(ObjectId id) => id.IsNull ? "-" : ((Structure)tr.GetObject(id, OpenMode.ForRead)).Name;
                    if (lost <= 5) log.AppendLine($"  pipe {p.Name} is not connected as the original (end kept exact): original {N(p.StartStructureId)} -> {N(p.EndStructureId)}, rebuilt {N(q.StartStructureId)} -> {N(q.EndStructureId)}");
                }
            }
            if (wrong > 0) failures.Add($"{wrong} pipe(s) are connected to another structure than in the original");
            if (lost > wrong) log.AppendLine($"  note: {lost - wrong} pipe end(s) left unconnected (listed above): exact geometry kept, the original end is away from its structure");
            var rebuiltPipes = net.GetPipeIds().Live().Select(id => (Pipe)tr.GetObject(id, OpenMode.ForRead)).ToList();
            double pMax = pipes.Select(p => rebuiltPipes.Where(q => q.Name == p.Name)
                                                        .Select(q => Math.Max(q.StartPoint.DistanceTo(p.StartPoint), q.EndPoint.DistanceTo(p.EndPoint)))
                                                        .DefaultIfEmpty(double.NaN).Min()).Where(d => !double.IsNaN(d)).DefaultIfEmpty(0).Max();
            // And the section: inner width and height, and the outer size.
            double section = pipes.Select(p => rebuiltPipes.Where(q => q.Name == p.Name)
                                                        .Select(q => new[] { Math.Abs(q.InnerDiameterOrWidth - p.InnerDiameterOrWidth), Math.Abs(q.InnerHeight - p.InnerHeight),
                                                                             Math.Abs(q.OuterDiameterOrWidth - p.OuterDiameterOrWidth), Math.Abs(q.WallThickness - p.WallThickness) }.Max())
                                                        .DefaultIfEmpty(double.NaN).Min()).Where(d => !double.IsNaN(d)).DefaultIfEmpty(0).Max();
            pMax = Math.Max(pMax, section);
            log.AppendLine($"  largest difference from the original: structure place {place * 1000:0.#} mm, rim {rim * 1000:0.#} mm, sump {sump * 1000:0.#} mm; pipe ends {pMax * 1000:0.#} mm, pipe section {section * 1000:0.#} mm");
            // More than a millimetre anywhere is a wrong rebuild: nothing is kept.
            if (stMax > 0.001 || pMax > 0.001) failures.Add($"the rebuilt network differs from the original by up to {Math.Max(stMax, pMax) * 1000:0} mm");
            log.AppendLine($"Rebuilt as {name}: {map.Count} of {structures.Count} structures, {net.GetPipeIds().Count} of {pipes.Count} pipes, " +
                           $"{FinalJoined(tr, net)} of {sourceJoined} pipe ends connected to structures" + (failures.Count > 0 ? $"; {failures.Count} not rebuilt" : "") + ".");
            foreach (string f in failures.Take(20)) log.AppendLine("  not rebuilt " + f);
            return failures.Count == 0;
        }

        /// <summary>
        /// The family and size a part is rebuilt with. Its own, when its family is still in the drawing (by size id or exact name,
        /// never the family's first size); otherwise the same shape and inner size from a family of the drawing's active catalog,
        /// added to the parts list when missing (a family added but not able to make the size is taken out again). Found once per
        /// size; throws with the reason when none can be made.
        /// </summary>
        private static (ObjectId family, ObjectId size) PartFor(Transaction tr, ObjectId listId, Part part, Dictionary<string, (ObjectId fam, ObjectId size, string error)> cache, StringBuilder log)
        {
            ObjectId own = part.PartFamilyId;
            if (PartCopy.IsFamily(tr, own))
            {
                ObjectId size = ObjectId.Null;
                try { size = part.GetPartSizeId(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (size.IsNull && tr.GetObject(own, OpenMode.ForRead) is PartFamily of)
                    for (int i = 0; i < of.PartSizeCount && size.IsNull; i++)
                        if (tr.GetObject(of[i], OpenMode.ForRead) is PartSize ps && ps.Name.Equals(part.PartSizeName, StringComparison.OrdinalIgnoreCase)) size = of[i];
                if (!size.IsNull) return (own, size);
            }

            DomainType domain = part is Pipe ? DomainType.Pipe : DomainType.Structure;
            bool round;
            SweptShapeType pipeShape = SweptShapeType.Circular;
            var v = new Dictionary<PartContextType, double>();
            if (part is Pipe p)
            {
                // The exact section shape: an egg or elliptical pipe must not become a box (review finding).
                pipeShape = p.CrossSectionalShape;
                round = pipeShape == SweptShapeType.Circular;
                if (round) v[PartContextType.PipeInnerDiameter] = p.InnerDiameterOrWidth;
                else { v[PartContextType.PipeInnerWidth] = p.InnerDiameterOrWidth; v[PartContextType.PipeInnerHeight] = p.InnerHeight; }
                // The wall always, 0 included: a default wall would change the outer size and the inverts (duct banks have
                // none; their sumps then sat up to 79 mm off, Test.dwg 2026-11-17).
                v[PartContextType.WallThickness] = Math.Max(0, p.WallThickness);
            }
            else
            {
                var st = (Structure)part;
                round = st.BoundingShape == BoundingShapeType.Cylinder;
                // A part made by copying (no family, as the KSIA manholes) reports no inner size: its outer size is used, so
                // the rebuilt part takes the same space (Test.dwg MV manholes, 2026-11-17).
                bool inner = st.InnerDiameterOrWidth > 0;
                double width = inner ? st.InnerDiameterOrWidth : st.DiameterOrWidth, length = inner && st.InnerLength > 0 ? st.InnerLength : st.Length;
                if (round) v[PartContextType.StructInnerDiameter] = width;
                else { v[PartContextType.StructInnerWidth] = width; v[PartContextType.StructInnerLength] = length > 0 ? length : width; }
                // The wall always, 0 for an outer-size fallback: a catalog wall would make the part bigger (review finding).
                v[PartContextType.WallThickness] = inner ? Math.Max(0, (st.DiameterOrWidth - st.InnerDiameterOrWidth) / 2) : 0;
            }
            string key = $"{domain} {(domain == DomainType.Pipe ? pipeShape.ToString() : round ? "round" : "box")} {PartsListTools.Describe(v)}";
            if (cache.TryGetValue(key, out var hit))
            {
                if (hit.size.IsNull) throw new InvalidOperationException(hit.error);
                return (hit.fam, hit.size);
            }

            var reasons = new List<string>();
            var list = (PartsList)tr.GetObject(listId, OpenMode.ForRead);
            foreach ((string guid, string description) in OrderedCatalogFamilies(domain, round))
            {
                ObjectId famId;
                bool added;
                try { famId = PartsListTools.EnsureFamily(tr, listId, domain, guid, out added); }
                catch (Exception ex) { reasons.Add($"{description}: {ex.Message}"); continue; }
                if (famId.IsNull) { reasons.Add($"{description}: not added to the parts list"); continue; }
                var fam = (PartFamily)tr.GetObject(famId, OpenMode.ForWrite);
                if (domain == DomainType.Pipe ? fam.SweptShape != pipeShape : (fam.BoundingShape == BoundingShapeType.Cylinder) != round)
                {
                    if (added) Remove(list, famId);
                    continue;
                }
                ObjectId size = PartsListTools.AddSize(tr, fam, v, out string error);
                if (!size.IsNull)
                {
                    cache[key] = (famId, size, null);
                    log.AppendLine($"  {key} -> \"{fam.Name}\"");
                    return (famId, size);
                }
                reasons.Add($"{description}: {error}");
                if (added) Remove(list, famId);
            }
            // Last: the C3D Pro any-size family of that shape (Method A), which takes any dimensions.
            PartFamily shaped = PartsListTools.Families(tr, listId, domain)
                .FirstOrDefault(f => domain == DomainType.Pipe ? f.SweptShape == pipeShape : (f.BoundingShape == BoundingShapeType.Cylinder) == round);
            // The any-size families are circular or box only.
            if (shaped != null && (domain == DomainType.Structure || pipeShape == SweptShapeType.Circular || pipeShape == SweptShapeType.Rectangular))
            {
                var notes = new List<string>();
                (ObjectId anyFam, ObjectId anySize) = PartSizeCreator.Create(tr, listId, shaped, v, NewSizeMethod.AnySizeFamily, notes, out string anyError);
                if (!anySize.IsNull)
                {
                    cache[key] = (anyFam, anySize, null);
                    log.AppendLine($"  {key} -> any-size family (Method A)");
                    return (anyFam, anySize);
                }
                reasons.Insert(0, $"any-size family (Method A): {anyError}");
            }
            string why = $"no {(round ? "round" : "box")} {domain.ToString().ToLowerInvariant()} family of the drawing's catalog can make {PartsListTools.Describe(v)}" +
                         (reasons.Count > 0 ? " (" + string.Join("; ", reasons.Take(3)) + ")" : "");
            cache[key] = (ObjectId.Null, ObjectId.Null, why);
            log.AppendLine($"  {key} -> not made: {why}");
            throw new InvalidOperationException(why);
        }

        /// <summary>The active catalog's families of a domain, the plain ones first (PVC, box culvert, junction structures).</summary>
        private static IEnumerable<(string guid, string description)> OrderedCatalogFamilies(DomainType domain, bool round)
        {
            string[] preferred = domain == DomainType.Pipe
                ? (round ? new[] { "PVC", "HDPE", "Ductile", "Concrete Pipe" } : new[] { "Box Culvert" })
                : (round ? new[] { "Cylindrical Junction", "Concentric Cylindrical", "Cylindrical" } : new[] { "Rectangular Junction", "Rectangular Structure" });
            int Rank(string d)
            {
                for (int i = 0; i < preferred.Length; i++) if (d.IndexOf(preferred[i], StringComparison.OrdinalIgnoreCase) >= 0) return i;
                return preferred.Length + (d.IndexOf("Headwall", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("End Section", StringComparison.OrdinalIgnoreCase) >= 0 ? 10 : 0);
            }
            return PartsListTools.CatalogFamilies(domain).OrderBy(f => Rank(f.description)).ThenBy(f => f.description, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Pipe ends connected to a structure in the network as it is now (after every end was put back).</summary>
        private static int FinalJoined(Transaction tr, Network net) =>
            net.GetPipeIds().Live().Select(id => (Pipe)tr.GetObject(id, OpenMode.ForRead))
               .Sum(q => (q.StartStructureId.IsNull ? 0 : 1) + (q.EndStructureId.IsNull ? 0 : 1));

        /// <summary>"Metric Pipes / Metric Structures": the drawing's active part catalogs.</summary>
        private static string CatalogNames()
        {
            try { NavisCatalogContext ctx = NavisCatalogContext.Resolve(); return $"{ctx.Pipe?.Name ?? "?"} / {ctx.Structure?.Name ?? "?"}"; }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return "?"; }
        }

        private static void Remove(PartsList list, ObjectId familyId)
        {
            try { if (!list.IsWriteEnabled) list.UpgradeOpen(); list.RemovePartFamily(familyId); }
            catch (Exception ex) { C3DProLogger.Warn("C3DPROREBUILD: a family added for a size could not be taken out again", ex); }
        }

        /// <summary>Connects one end to the rebuilt structure of the original's; 1 when connected.</summary>
        private static int Connect(Pipe np, ConnectorPositionType end, ObjectId oldStructure, Dictionary<ObjectId, ObjectId> map)
        {
            if (oldStructure.IsNull || !map.TryGetValue(oldStructure, out ObjectId ns)) return 0;
            try { np.ConnectToStructure(end, ns, false); return 1; }
            catch (Exception ex) { C3DProLogger.Warn($"C3DPROREBUILD: pipe {np.Name} could not be connected at its {end}", ex); return 0; }
        }

        private static void CopyLook(Part from, Part to)
        {
            try { to.Name = from.Name; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { to.Description = from.Description; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { to.StyleId = from.StyleId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { to.Layer = from.Layer; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }
    }
}
