using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    internal enum NetworkGrouping { SourceNetwork, Layer, Single }

    internal enum ExistingMode { Replace, Skip, Duplicate }

    internal sealed class NavisImportOptions
    {
        public string PartsListName = "Navis Import";
        public NetworkGrouping Grouping = NetworkGrouping.SourceNetwork;
        public string Prefix = "NAVIS - ";
        public string SingleNetworkName = "NAVIS - Import";
        public ObjectId SurfaceId = ObjectId.Null;
        public bool ConnectToStructures = true;
        public string SolidLayer = "NAVIS-SOLIDS";
        public ExistingMode Existing = ExistingMode.Replace;
        /// <summary>A catalog size further than this from the Navisworks size (metres) is not trusted.</summary>
        public double MaxPartError = 0.025;
        /// <summary>True: such rows come in as exact solids. False: the nearest size is used anyway.</summary>
        public bool SolidWhenNoSize = true;
        /// <summary>Use an exact size already in one of the drawing's parts lists before matching against the catalog.</summary>
        public bool SearchDrawingLists = true;
        /// <summary>How sizes the catalog lacks are made: nearest size, any-size families (Method A) or catalog additions (Method B).</summary>
        public CatalogMode Mode = CatalogMode.None;
        /// <summary>Family and size picked by the user for a row, from any parts list; never replaced by the automatic match.</summary>
        public Dictionary<ImportCandidate, (ObjectId family, ObjectId size)> Overrides = new Dictionary<ImportCandidate, (ObjectId, ObjectId)>();
    }

    /// <summary>What happened (or, in a plan, would happen) to one row.</summary>
    internal sealed class RowOutcome
    {
        public ImportCandidate Row;
        public string Family = "", Size = "";
        public double PartDeviation;
        public string Status = "";
        public string Detail = "";
        public bool Failed;
        /// <summary>No catalog size was close enough, so the row is brought in as its exact solid.</summary>
        public bool AsSolid;
        public bool Overridden;
        public readonly List<ObjectId> Created = new List<ObjectId>();
    }

    internal sealed class ImportReport
    {
        public readonly Dictionary<ImportCandidate, RowOutcome> Rows = new Dictionary<ImportCandidate, RowOutcome>();
        public readonly List<string> AddedFamilies = new List<string>();
        public readonly List<string> AddedSizes = new List<string>();
        public readonly List<string> Networks = new List<string>();
        public bool CreatesPartsList;
        public int CatalogPipeFamilies, CatalogStructureFamilies;
        public string Error = "";
        /// <summary>Method B: sizes that will be (plan) or were (import) added to stock catalog families.</summary>
        public readonly List<string> CatalogAdditions = new List<string>();
        public readonly List<string> CatalogNotes = new List<string>();
    }

    /// <summary>Creates Civil 3D pipes, structures and solids from import rows. <see cref="Plan"/> runs the same matching in a
    /// transaction that is thrown away, so the user sees every family and size that will be added before anything changes.</summary>
    internal static class NavisImporter
    {
        public const string RegApp = "C3DPRO_NAVIS";

        public static string NetworkFor(ImportCandidate c, NavisImportOptions o)
        {
            switch (o.Grouping)
            {
                case NetworkGrouping.Single: return o.SingleNetworkName;
                case NetworkGrouping.Layer: return o.Prefix + (string.IsNullOrEmpty(c.Layer) ? "Import" : c.Layer);
                // Without a source network, one network per source file keeps its pipes and structures together (so they can connect).
                default:
                    string file = c.Source?.SourceFile ?? "";
                    return o.Prefix + (!string.IsNullOrEmpty(c.Network) ? c.Network
                        : file.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(file)
                        : !string.IsNullOrEmpty(c.Layer) ? c.Layer : "Import");
            }
        }

        public static ImportReport Plan(AcadDocument doc, List<ImportCandidate> rows, NavisImportOptions o) => Run(doc, rows, o, false);

        public static ImportReport Import(AcadDocument doc, List<ImportCandidate> rows, NavisImportOptions o) => Run(doc, rows, o, true);

        private static ImportReport Run(AcadDocument doc, List<ImportCandidate> rows, NavisImportOptions o, bool commit)
        {
            ImportReport report = new ImportReport();
            Database db = doc.Database;
            CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
            double s = DrawingScale(db);
            // Method B writes catalog files while the drawing transaction is open: if the import then fails and is rolled
            // back, the catalog is put back too (UNDO alone never reaches files on disk).
            CatalogJournal catalogChanges = new CatalogJournal();

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    NavisCatalogContext catalogs = o.Mode == CatalogMode.None ? new NavisCatalogContext() : NavisCatalogContext.Resolve();
                    NavisPartMatcher matcher = new NavisPartMatcher(tr, cdoc, o.PartsListName, o.Mode, catalogs);
                    if (o.Mode == CatalogMode.AnySize && !(matcher.AnySizeAvailable("box") && matcher.AnySizeAvailable("rect")))
                        report.CatalogNotes.Add("The C3D Pro any-size families are not loaded in this Civil 3D session yet (restart Civil 3D once after setting them up); the nearest catalog sizes are used meanwhile.");
                    report.CreatesPartsList = matcher.CreatedPartsList;
                    report.CatalogPipeFamilies = matcher.CatalogPipeFamilies;
                    report.CatalogStructureFamilies = matcher.CatalogStructureFamilies;

                    Dictionary<ImportCandidate, PartChoice> parts = new Dictionary<ImportCandidate, PartChoice>();
                    List<(Autodesk.Civil.DatabaseServices.Styles.PartFamily fam, Autodesk.Civil.DatabaseServices.Styles.PartSize size, string list)> drawingSizes =
                        o.SearchDrawingLists ? DrawingSizes(tr, cdoc, matcher.PartsListId) : new List<(Autodesk.Civil.DatabaseServices.Styles.PartFamily, Autodesk.Civil.DatabaseServices.Styles.PartSize, string)>();
                    foreach (ImportCandidate c in rows)
                    {
                        RowOutcome outcome = report.Rows[c] = new RowOutcome { Row = c };
                        if (c.Action == ImportKind.Skip) { outcome.Status = "Skipped"; continue; }
                        if (c.Action == ImportKind.Solid) { outcome.Family = "3D solid"; outcome.Size = $"{c.Source.Triangles.Length / 9} triangles"; outcome.Status = "Solid"; continue; }
                        PartChoice p;
                        if (o.Overrides.TryGetValue(c, out (ObjectId family, ObjectId size) chosen)
                            && tr.GetObject(chosen.family, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartFamily fam
                            && tr.GetObject(chosen.size, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartSize size)
                        {
                            p = NavisPartMatcher.Evaluate(fam, size, c);
                            outcome.Overridden = true;
                        }
                        else p = ExactInDrawing(drawingSizes, c) ?? (c.IsLinear ? matcher.MatchLinear(c) : matcher.MatchStructure(c));

                        // Method B on import: write the missing size into the catalog family, then match the row again.
                        if (p.Pending != null && commit) p = AddToCatalog(matcher, c, p, report, catalogChanges);
                        parts[c] = p;
                        if (p.Pending != null)
                        {
                            outcome.Family = p.FamilyName;
                            outcome.Size = p.SizeName;
                            outcome.PartDeviation = 0;
                            outcome.Detail = p.Note;
                            outcome.Status = "Catalog size to add";
                            if (!report.CatalogAdditions.Contains($"{p.Pending.FamilyDescription}: {p.Pending.Label}")) report.CatalogAdditions.Add($"{p.Pending.FamilyDescription}: {p.Pending.Label}");
                            continue;
                        }
                        outcome.Family = p.FamilyName;
                        outcome.Size = p.SizeName;
                        outcome.PartDeviation = p.Deviation;
                        outcome.Detail = p.Note;
                        outcome.Status = p.SizeId.IsNull ? "No part" : p.Deviation <= NavisInterpreter.HighTolerance ? "Exact" : "Nearest size";
                        outcome.Failed = p.SizeId.IsNull;

                        // A part that is the wrong size gives false clashes; the exact solid is the honest fallback. Your own pick always stands.
                        if (!outcome.Overridden && o.SolidWhenNoSize && (p.SizeId.IsNull || p.Deviation > o.MaxPartError))
                        {
                            outcome.AsSolid = true;
                            outcome.Failed = false;
                            outcome.Status = "Solid (no size)";
                            outcome.Detail = (p.SizeId.IsNull
                                ? "No catalog part fits"
                                : $"The nearest catalog size, {p.FamilyName} · {p.SizeName}, is {p.Deviation * 1000:0} mm off (limit {o.MaxPartError * 1000:0} mm)")
                                + "; brought in as an exact solid. Choose a part yourself to override. " + (p.SizeId.IsNull ? p.Note : p.Note.Replace("Chosen by you. ", ""));
                            outcome.Detail = outcome.Detail.Trim();
                        }
                    }

                    // Only list what imported parts will really use; the rest is removed again on import.
                    HashSet<ObjectId> used = new HashSet<ObjectId>(parts.Where(kv => !report.Rows[kv.Key].AsSolid && !kv.Value.SizeId.IsNull).Select(kv => kv.Value.SizeId));
                    report.AddedSizes.AddRange(matcher.CreatedSizes.Where(s => used.Contains(s.Key)).Select(s => s.Value.label).Distinct());
                    HashSet<ObjectId> usedFamilies = new HashSet<ObjectId>(parts.Where(kv => used.Contains(kv.Value.SizeId)).Select(kv => kv.Value.FamilyId));
                    report.AddedFamilies.AddRange(matcher.CreatedFamilies.Where(f => usedFamilies.Contains(f.Key)).Select(f => f.Value).Distinct());
                    report.Networks.AddRange(rows.Where(r => r.Action != ImportKind.Skip && r.Action != ImportKind.Solid && !report.Rows[r].AsSolid).Select(r => NetworkFor(r, o)).Distinct());

                    if (!commit)
                    {
                        tr.Abort();
                        return report;
                    }

                    Create(tr, db, cdoc, rows, parts, report, o, matcher.PartsListId, s);
                    matcher.Prune(new HashSet<ObjectId>(parts.Where(kv => !report.Rows[kv.Key].AsSolid).Select(kv => kv.Value.SizeId)));
                    tr.Commit();
                    catalogChanges.Clear();
                }
                catch (Exception ex)
                {
                    report.Error = ex.Message;
                    tr.Abort();
                    if (!catalogChanges.IsEmpty)
                    {
                        List<string> stuck = catalogChanges.Rollback();
                        report.CatalogNotes.Add(stuck.Count == 0
                            ? "The sizes added to the catalog for this import were taken out again."
                            : "These catalog files could not be put back (restore them from their .bak copies): " + string.Join(", ", stuck));
                    }
                }
            }
            return report;
        }

        /// <summary>Applies one Method B addition (once per distinct size) and matches the row again; Civil 3D re-reads sizes at once.</summary>
        private static PartChoice AddToCatalog(NavisPartMatcher matcher, ImportCandidate c, PartChoice pending, ImportReport report, CatalogJournal journal)
        {
            string label = $"{pending.Pending.FamilyDescription}: {pending.Pending.Label}";
            try
            {
                if (!report.CatalogAdditions.Contains(label))
                {
                    List<string> changes = NavisCatalogEditor.AddSize(pending.Pending.XmlPath, pending.Pending.ValuesMm, journal);
                    report.CatalogAdditions.Add(label);
                    if (changes.Count > 0) report.CatalogNotes.Add($"{pending.Pending.FamilyDescription}: added {string.Join(", ", changes)}.");
                }
            }
            catch (Exception ex)
            {
                report.CatalogNotes.Add($"Could not add {pending.Pending.Label} to {pending.Pending.FamilyDescription}: {ex.Message}");
                return new PartChoice { Note = "The size could not be added to the catalog: " + ex.Message };
            }
            matcher.ClearCache();
            PartChoice again = c.IsLinear ? matcher.MatchLinear(c) : matcher.MatchStructure(c);
            if (again.Pending != null)
                return new PartChoice { Note = "The size was added to the catalog, but Civil 3D did not offer it; restart Civil 3D and import again." };
            again.Note = ($"Size added to the \"{pending.Pending.FamilyDescription}\" catalog family. " + again.Note).Trim();
            return again;
        }

        /// <summary>
        /// Method A setup: adds the C3D Pro any-size families to every registered catalog that has their stock base family
        /// (so drawings on the Metric or KSA catalogs both get them). Returns true when Civil 3D must be restarted to list them.
        /// </summary>
        public static bool EnsureAnySizeFamilies(List<string> log)
        {
            NavisCatalogContext ctx = NavisCatalogContext.Resolve();
            HashSet<string> loaded = new HashSet<string>(
                Autodesk.Civil.DatabaseServices.Styles.PartsList.GetAvailablePartFamilies(DomainType.Pipe).Concat(Autodesk.Civil.DatabaseServices.Styles.PartsList.GetAvailablePartFamilies(DomainType.Structure)).Select(f => f.GUID), StringComparer.OrdinalIgnoreCase);
            bool restart = false;
            foreach (AnySizeFamily fam in NavisCatalogEditor.AnySize)
            {
                List<CatalogInfo> targets = ctx.All.Where(cat => cat.PartIds.Contains(fam.BaseGuid)).ToList();
                CatalogInfo active = fam.Pipe ? ctx.Pipe : ctx.Structure;
                if (active != null && !targets.Contains(active)) targets.Insert(0, active);
                foreach (CatalogInfo cat in targets)
                {
                    if (!NavisCatalogContext.Writable(cat)) { log.Add($"The {cat.Name} catalog folder is read-only; ask your CAD manager to add the any-size families there."); continue; }
                    try { NavisCatalogEditor.EnsureAnySizeFamily(cat, fam, log); }
                    catch (Exception ex) { log.Add($"{fam.Description} could not be added to {cat.Name}: {ex.Message}"); }
                }
                if (!loaded.Contains(fam.Guid)) restart = true;
            }
            return restart;
        }

        /// <summary>Every size in the drawing's other parts lists (the import list itself is matched by the catalog matcher).</summary>
        private static List<(Autodesk.Civil.DatabaseServices.Styles.PartFamily fam, Autodesk.Civil.DatabaseServices.Styles.PartSize size, string list)> DrawingSizes(
            Transaction tr, CivilDocument cdoc, ObjectId importListId)
        {
            var sizes = new List<(Autodesk.Civil.DatabaseServices.Styles.PartFamily, Autodesk.Civil.DatabaseServices.Styles.PartSize, string)>();
            foreach (ObjectId listId in cdoc.Styles.PartsListSet)
            {
                if (listId == importListId || !(tr.GetObject(listId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartsList pl)) continue;
                foreach (DomainType domain in new[] { DomainType.Pipe, DomainType.Structure })
                    foreach (ObjectId famId in pl.GetPartFamilyIdsByDomain(domain))
                    {
                        if (!(tr.GetObject(famId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartFamily fam)) continue;
                        for (int i = 0; i < fam.PartSizeCount; i++)
                            if (tr.GetObject(fam[i], OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartSize size) sizes.Add((fam, size, pl.Name));
                    }
            }
            return sizes;
        }

        /// <summary>The closest size of the right shape already in the drawing, when it is within the exact tolerance.</summary>
        private static PartChoice ExactInDrawing(List<(Autodesk.Civil.DatabaseServices.Styles.PartFamily fam, Autodesk.Civil.DatabaseServices.Styles.PartSize size, string list)> sizes, ImportCandidate c)
        {
            PartChoice best = null;
            string bestList = "";
            DomainType domain = c.IsLinear ? DomainType.Pipe : DomainType.Structure;
            string material = NavisInterpreter.MaterialKey(c.Material);
            foreach ((var fam, var size, string list) in sizes)
            {
                if (fam.Domain != domain) continue;
                if (c.IsLinear && fam.SweptShape != (c.Circular ? SweptShapeType.Circular : SweptShapeType.Rectangular)) continue;
                if (!c.IsLinear && fam.BoundingShape != (c.StructCircular ? BoundingShapeType.Cylinder : BoundingShapeType.Box)) continue;
                PartChoice p;
                try { p = NavisPartMatcher.Evaluate(fam, size, c); } catch (Exception) { continue; }
                if (p.Deviation > NavisInterpreter.HighTolerance) continue;
                // Same size in two materials: keep the one whose family matches the Navisworks material.
                bool matMatch = material.Length == 0 || NavisInterpreter.MaterialKey(fam.Name) == material || NavisInterpreter.MaterialKey(fam.Description) == material;
                p.MaterialMatched = matMatch;
                if (best == null || (p.MaterialMatched && !best.MaterialMatched) || (p.MaterialMatched == best.MaterialMatched && p.Deviation < best.Deviation)) { best = p; bestList = list; }
            }
            if (best != null) best.Note = $"Exact size found in the drawing's \"{bestList}\" parts list.";
            return best;
        }

        private static void Create(Transaction tr, Database db, CivilDocument cdoc, List<ImportCandidate> rows, Dictionary<ImportCandidate, PartChoice> parts,
            ImportReport report, NavisImportOptions o, ObjectId partsListId, double s)
        {
            RegisterApp(tr, db);
            Dictionary<string, List<ObjectId>> existing = FindExisting(tr, db);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            Dictionary<string, ObjectId> networks = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            // Structures by network and Navisworks name, so pipes can find the structures they were connected to.
            Dictionary<string, Dictionary<string, (ObjectId id, ImportCandidate row)>> structures = new Dictionary<string, Dictionary<string, (ObjectId, ImportCandidate)>>();

            // Structures first, so pipes can connect to them.
            foreach (ImportCandidate c in rows.Where(r => r.Action == ImportKind.Structure).Concat(rows.Where(r => r.IsLinear)).Concat(rows.Where(r => r.Action == ImportKind.Solid)))
            {
                RowOutcome outcome = report.Rows[c];
                if (!PrepareExisting(tr, c, existing, o, outcome)) continue;
                try
                {
                    if (c.Action == ImportKind.Solid || outcome.AsSolid)
                    {
                        ObjectId id = CreateSolid(tr, db, ms, c, o, s, outcome);
                        Tag(tr, id, c);
                        outcome.Created.Add(id);
                        continue;
                    }
                    PartChoice p = parts[c];
                    if (p.SizeId.IsNull) { outcome.Status = "Not imported"; outcome.Failed = true; continue; }
                    string netName = NetworkFor(c, o);
                    if (!networks.TryGetValue(netName, out ObjectId netId)) networks[netName] = netId = EnsureNetwork(tr, cdoc, netName, partsListId, o.SurfaceId);
                    Network network = (Network)tr.GetObject(netId, OpenMode.ForWrite);

                    if (c.Action == ImportKind.Structure)
                    {
                        ObjectId id = CreateStructure(tr, network, c, p, s);
                        Tag(tr, id, c);
                        outcome.Created.Add(id);
                        if (!structures.TryGetValue(netName, out var byName)) structures[netName] = byName = new Dictionary<string, (ObjectId, ImportCandidate)>(StringComparer.OrdinalIgnoreCase);
                        byName[c.Name] = (id, c);
                    }
                    else
                    {
                        structures.TryGetValue(netName, out var byName);
                        for (int b = 0; b < c.Barrels.Count; b++)
                        {
                            ObjectId id = CreatePipe(tr, network, c, p, b, s);
                            Tag(tr, id, c);
                            outcome.Created.Add(id);
                            if (o.ConnectToStructures && byName != null) Connect(tr, id, c, byName, s, outcome);
                        }
                    }
                    outcome.Status = outcome.Status == "Exact" || outcome.Status == "Nearest size" ? "Imported" + (outcome.Status == "Nearest size" ? " (nearest size)" : "") : outcome.Status;
                }
                catch (Exception ex)
                {
                    outcome.Status = "Failed";
                    outcome.Failed = true;
                    outcome.Detail = ex.Message;
                }
            }
        }

        /// <summary>Applies the "already imported" choice. Returns false when the row must not be created.</summary>
        private static bool PrepareExisting(Transaction tr, ImportCandidate c, Dictionary<string, List<ObjectId>> existing, NavisImportOptions o, RowOutcome outcome)
        {
            if (string.IsNullOrEmpty(c.Guid) || !existing.TryGetValue(c.Guid, out List<ObjectId> ids) || ids.Count == 0) return true;
            switch (o.Existing)
            {
                case ExistingMode.Skip:
                    outcome.Status = "Already imported";
                    outcome.Created.AddRange(ids);
                    return false;
                case ExistingMode.Replace:
                    foreach (ObjectId id in ids)
                    {
                        try { tr.GetObject(id, OpenMode.ForWrite, false, true).Erase(); } catch (Exception) { }
                    }
                    outcome.Detail = (outcome.Detail + " Replaced the earlier import.").Trim();
                    return true;
                default:
                    return true;
            }
        }

        // ---------------- Networks, structures, pipes ----------------

        private static ObjectId EnsureNetwork(Transaction tr, CivilDocument cdoc, string name, ObjectId partsListId, ObjectId surfaceId)
        {
            foreach (ObjectId id in cdoc.GetPipeNetworkIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Network n && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return id;
            string netName = name;
            ObjectId netId = Network.Create(cdoc, ref netName);
            Network network = (Network)tr.GetObject(netId, OpenMode.ForWrite);
            network.PartsListId = partsListId;
            if (!surfaceId.IsNull) network.ReferenceSurfaceId = surfaceId;
            return netId;
        }

        private static ObjectId CreateStructure(Transaction tr, Network network, ImportCandidate c, PartChoice p, double s)
        {
            double rotation = c.Origin == "Civil 3D" ? c.Rotation : c.Rotation + Math.PI / 2;
            // The chosen size's width runs along the structure's own X axis; turn it a quarter when the catalog put the longer side there.
            if (!c.StructCircular && p.InnerWidth > p.InnerLength + 1e-6) rotation += Math.PI / 2;
            ObjectId id = ObjectId.Null;
            network.AddStructure(p.FamilyId, p.SizeId, new Point3d(c.X * s, c.Y * s, c.Rim * s), rotation, ref id, false);
            CivilStructure st = (CivilStructure)tr.GetObject(id, OpenMode.ForWrite);
            SetName(st, c.Name);
            st.RimElevation = c.Rim * s;
            if (!double.IsNaN(c.Sump))
            {
                st.ControlSumpBy = StructureControlSumpType.ByElevation;
                st.SumpElevation = c.Sump * s;
            }
            st.Description = "Navisworks: " + c.Name;
            return id;
        }

        private static ObjectId CreatePipe(Transaction tr, Network network, ImportCandidate c, PartChoice p, int barrel, double s)
        {
            (double u, double w) = c.Barrels[barrel];
            V3 offset = c.BarrelAxis * u + c.BarrelUp * w;
            V3 a = c.Start + offset, b = c.End + offset;
            ObjectId id = ObjectId.Null;
            network.AddLinePipe(p.FamilyId, p.SizeId, new LineSegment3d(new Point3d(a.X * s, a.Y * s, a.Z * s), new Point3d(b.X * s, b.Y * s, b.Z * s)), ref id, false);
            Pipe pipe = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
            SetName(pipe, c.Barrels.Count > 1 ? $"{c.Name}-B{barrel + 1}" : c.Name);
            pipe.Description = "Navisworks: " + c.Name;
            return id;
        }

        /// <summary>Connects each pipe end to the structure it was connected to in the source (by name), or to the one it sits inside.
        /// The pipe's 3D ends are put back afterwards, because connecting can snap them.</summary>
        private static void Connect(Transaction tr, ObjectId pipeId, ImportCandidate c, Dictionary<string, (ObjectId id, ImportCandidate row)> structures, double s, RowOutcome outcome)
        {
            Pipe pipe = (Pipe)tr.GetObject(pipeId, OpenMode.ForWrite);
            Point3d start = pipe.StartPoint, end = pipe.EndPoint;
            // Civil 3D exports say which structure each end connects to (blank = not connected); other models are matched by position.
            bool byName = c.Origin == "Civil 3D";
            ObjectId used = ObjectId.Null;
            foreach ((ConnectorPositionType pos, Point3d pt, string sourceName) in new[]
            {
                (ConnectorPositionType.Start, start, c.StartStructure), (ConnectorPositionType.End, end, c.EndStructure)
            })
            {
                ObjectId target = ObjectId.Null;
                if (!string.IsNullOrEmpty(sourceName) && structures.TryGetValue(sourceName, out var named) && Contains(named.row, pt, s, 1.0)) target = named.id;
                if (target.IsNull && !byName) target = structures.Values.Where(x => Contains(x.row, pt, s, 0.05)).Select(x => x.id).FirstOrDefault();
                // Both ends of one pipe never go to the same structure (Civil 3D refuses it).
                if (target.IsNull || target == used) continue;
                used = target;
                try { pipe.ConnectToStructure(pos, target, false); }
                catch (Exception ex)
                {
                    // Civil 3D sometimes reports a failure for a connection it did make; only a real miss is reported.
                    ObjectId now = pos == ConnectorPositionType.Start ? pipe.StartStructureId : pipe.EndStructureId;
                    if (now != target) outcome.Detail = (outcome.Detail + $" Could not connect the {pos.ToString().ToLowerInvariant()}: {ex.Message.TrimEnd('.')}.").Trim();
                }
            }
            if (!pipe.StartPoint.IsEqualTo(start)) pipe.StartPoint = start;
            if (!pipe.EndPoint.IsEqualTo(end)) pipe.EndPoint = end;
        }

        /// <summary>True when a point (drawing units) lies within a structure's measured footprint, plus a margin in metres.</summary>
        private static bool Contains(ImportCandidate st, Point3d pt, double s, double margin)
        {
            double x = pt.X / s - st.X, y = pt.Y / s - st.Y, z = pt.Z / s;
            if (!double.IsNaN(st.Sump) && (z < Math.Min(st.Sump, st.Bottom) - margin || z > st.Rim + margin)) return false;
            if (st.StructCircular) return Math.Sqrt(x * x + y * y) <= st.StructDiameter / 2 + margin;
            double ang = st.Footprint != null ? st.Footprint.Rotation : st.Rotation;
            double along = x * Math.Cos(ang) + y * Math.Sin(ang), across = -x * Math.Sin(ang) + y * Math.Cos(ang);
            double half1 = (st.Footprint != null ? st.Footprint.Length : st.StructLength) / 2, half2 = (st.Footprint != null ? st.Footprint.Width : st.StructWidth) / 2;
            return Math.Abs(along) <= half1 + margin && Math.Abs(across) <= half2 + margin;
        }

        private static void SetName(Part part, string name)
        {
            for (int i = 0; i < 50; i++)
            {
                try { part.Name = i == 0 ? name : $"{name} ({i})"; return; }
                catch (Exception) { }
            }
        }

        // ---------------- Solids ----------------

        /// <summary>The Navisworks mesh as an AutoCAD solid (triangle for triangle). When it is not closed it stays a mesh.</summary>
        private static ObjectId CreateSolid(Transaction tr, Database db, BlockTableRecord ms, ImportCandidate c, NavisImportOptions o, double s, RowOutcome outcome)
        {
            double[] t = c.Source.Triangles;
            Point3dCollection vertices = new Point3dCollection();
            Int32Collection faces = new Int32Collection();
            Dictionary<(long, long, long), int> index = new Dictionary<(long, long, long), int>();
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                faces.Add(3);
                for (int k = 0; k < 3; k++)
                {
                    double x = t[i + 3 * k], y = t[i + 3 * k + 1], z = t[i + 3 * k + 2];
                    (long, long, long) key = ((long)Math.Round(x * 1e5), (long)Math.Round(y * 1e5), (long)Math.Round(z * 1e5));
                    if (!index.TryGetValue(key, out int vi)) { vi = vertices.Count; vertices.Add(new Point3d(x * s, y * s, z * s)); index[key] = vi; }
                    faces.Add(vi);
                }
            }
            ObjectId layer = EnsureLayer(tr, db, o.SolidLayer);
            SubDMesh mesh = new SubDMesh();
            mesh.SetDatabaseDefaults();
            mesh.SetSubDMesh(vertices, faces, 0);
            mesh.LayerId = layer;
            ObjectId meshId = ms.AppendEntity(mesh);
            tr.AddNewlyCreatedDBObject(mesh, true);
            try
            {
                Solid3d solid = mesh.ConvertToSolid(false, true);
                solid.LayerId = layer;
                ObjectId solidId = ms.AppendEntity(solid);
                tr.AddNewlyCreatedDBObject(solid, true);
                mesh.Erase();
                outcome.Status = "Imported (solid)";
                return solidId;
            }
            catch (Exception)
            {
                outcome.Status = "Imported (mesh)";
                outcome.Detail = (outcome.Detail + " The mesh is not closed, so it was kept as a mesh (exact, but not a solid).").Trim();
                return meshId;
            }
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            LayerTableRecord rec = new LayerTableRecord { Name = name };
            ObjectId id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        // ---------------- Tags: which Navisworks item each object came from ----------------

        private static void RegisterApp(Transaction tr, Database db)
        {
            RegAppTable table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(RegApp)) return;
            table.UpgradeOpen();
            RegAppTableRecord rec = new RegAppTableRecord { Name = RegApp };
            table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        private static void Tag(Transaction tr, ObjectId id, ImportCandidate c)
        {
            if (id.IsNull) return;
            Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            ent.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, c.Guid ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Clip(c.Name)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Clip(c.Source.SourceFile)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        }

        private static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 250 ? s.Substring(0, 250) : s;

        /// <summary>Objects already imported, by Navisworks GUID.</summary>
        public static Dictionary<string, List<ObjectId>> FindExisting(Transaction tr, Database db)
        {
            Dictionary<string, List<ObjectId>> map = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch (Exception) { continue; }
                ResultBuffer rb = ent?.GetXDataForApplication(RegApp);
                if (rb == null) continue;
                TypedValue[] values = rb.AsArray();
                if (values.Length < 2 || !(values[1].Value is string guid) || guid.Length == 0) continue;
                if (!map.TryGetValue(guid, out List<ObjectId> list)) map[guid] = list = new List<ObjectId>();
                list.Add(id);
            }
            return map;
        }

        public static HashSet<string> ExistingGuids(AcadDocument doc)
        {
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                return new HashSet<string>(FindExisting(tr, doc.Database).Keys, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Drawing units per metre (Navisworks sends metres). Civil 3D's own drawing units decide, with its foot definition
        /// (international or US survey: 2 ppm apart, 2 cm at 10 km from the origin); INSUNITS only when they cannot be read.
        /// </summary>
        public static double DrawingScale(Database db)
        {
            try
            {
                Autodesk.Civil.Settings.SettingsUnitZone zone = CivilDocument.GetCivilDocument(db).Settings.DrawingSettings.UnitZoneSettings;
                if (zone.DrawingUnits == Autodesk.Civil.Settings.DrawingUnitType.Meters) return 1;
                if (zone.DrawingUnits == Autodesk.Civil.Settings.DrawingUnitType.Feet)
                    return zone.ImperialToMetricConversion == Autodesk.Civil.Settings.ImperialToMetricConversionType.UsSurveyFoot ? UsSurveyFeetPerMetre : 1 / 0.3048;
            }
            catch (Exception) { }
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters: return 1000;
                case UnitsValue.Centimeters: return 100;
                case UnitsValue.Decimeters: return 10;
                case UnitsValue.Feet: return 1 / 0.3048;
                case UnitsValue.USSurveyFeet: return UsSurveyFeetPerMetre;
                case UnitsValue.Inches: return 1 / 0.0254;
                case UnitsValue.Yards: return 1 / 0.9144;
                case UnitsValue.Kilometers: return 0.001;
                default: return 1;
            }
        }

        /// <summary>The US survey foot is exactly 1200 / 3937 m.</summary>
        private const double UsSurveyFeetPerMetre = 3937.0 / 1200.0;
    }
}
