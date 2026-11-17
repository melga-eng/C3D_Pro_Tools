using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    /// <summary>
    /// A part chosen in a <see cref="PartChooser"/>: a family (by catalog GUID) and a size, described by its dimensions so it
    /// can be found or added in any parts list. Lengths in metres.
    /// </summary>
    internal sealed class PartPick
    {
        public DomainType Domain;
        /// <summary>The parts list the part was chosen from.</summary>
        public ObjectId PartsListId;
        public string PartsListName = "";
        public string FamilyGuid = "", FamilyName = "";
        /// <summary>The family object in the drawing (in <see cref="PartsListId"/>, or any list when picked from a part); null for a catalog family.</summary>
        public ObjectId FamilyId;
        /// <summary>A pipe or structure in the drawing to copy when its part cannot be built (family not in the drawing, or catalog missing); null = none.</summary>
        public ObjectId TemplateId;
        /// <summary>The size in that parts list, or null when it is a catalog size not yet in the list.</summary>
        public ObjectId SizeId;
        public string SizeName = "";
        public Dictionary<PartContextType, double> Values = new Dictionary<PartContextType, double>();
        /// <summary>True: a network using another parts list is switched to <see cref="PartsListId"/>; false: the part is added to its own list.</summary>
        public bool SwitchNetwork;

        public string Label => $"{FamilyName} · {SizeName}";
        public double Get(PartContextType c) => Values.TryGetValue(c, out double v) ? v : 0;
        public double Wall => Get(PartContextType.WallThickness);
        /// <summary>Inside height of a pipe (its diameter when circular).</summary>
        public double InnerHeight => Values.ContainsKey(PartContextType.PipeInnerHeight) ? Get(PartContextType.PipeInnerHeight) : Get(PartContextType.PipeInnerDiameter);
    }

    /// <summary>
    /// Parts list work shared by the part chooser, the Parts Lists window and the tools: reading family and size dimensions,
    /// the catalog sizes a family offers, adding families and sizes to a list, and putting a chosen part into a network's list.
    /// </summary>
    internal static class PartsListTools
    {
        // Two sizes are the same when every dimension agrees to half a millimetre.
        private const double SameSize = 0.0005;
        // A family offering more catalog sizes than this is cut off in the lists (the rest can still be added by dimensions).
        private const int MaxCatalogSizes = 400;

        /// <summary>The dimensions that identify a size of this family, in the order a size filter must be set.</summary>
        public static PartContextType[] KeyContexts(PartFamily fam)
        {
            if (fam.Domain == DomainType.Pipe)
                return fam.SweptShape == SweptShapeType.Circular
                    ? new[] { PartContextType.PipeInnerDiameter, PartContextType.WallThickness }
                    : new[] { PartContextType.PipeInnerWidth, PartContextType.PipeInnerHeight, PartContextType.WallThickness };
            return fam.BoundingShape == BoundingShapeType.Cylinder
                ? new[] { PartContextType.StructInnerDiameter, PartContextType.WallThickness, PartContextType.FloorThickness }
                : new[] { PartContextType.StructInnerWidth, PartContextType.StructInnerLength, PartContextType.WallThickness, PartContextType.FloorThickness };
        }

        /// <summary>The identifying dimensions of a size (metres); the wall is read from the catalog row when the size does not store it.</summary>
        public static Dictionary<PartContextType, double> ReadValues(PartFamily fam, PartDataRecord r)
        {
            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            foreach (PartContextType c in KeyContexts(fam))
            {
                double v = c == PartContextType.WallThickness ? NavisPartMatcher.WallOf(fam, r) : NavisPartMatcher.Field(r, c);
                if (v > 0 || c == PartContextType.WallThickness) values[c] = v;
            }
            return values;
        }

        public static bool Same(Dictionary<PartContextType, double> a, Dictionary<PartContextType, double> b)
        {
            foreach (KeyValuePair<PartContextType, double> kv in a)
            {
                // Floor thickness is not part of every family's sizes; it only decides between otherwise equal sizes.
                if (kv.Key == PartContextType.FloorThickness) continue;
                if (!b.TryGetValue(kv.Key, out double v) || Math.Abs(v - kv.Value) > SameSize) return false;
            }
            return true;
        }

        /// <summary>A readable size label: "450 mm, wall 50" or "1200 x 900 mm, wall 150".</summary>
        public static string Describe(Dictionary<PartContextType, double> v)
        {
            string Mm(PartContextType c) => Math.Round(v.TryGetValue(c, out double x) ? x * 1000 : 0).ToString("0");
            string main = v.ContainsKey(PartContextType.PipeInnerDiameter) ? $"{Mm(PartContextType.PipeInnerDiameter)} mm"
                : v.ContainsKey(PartContextType.PipeInnerWidth) ? $"{Mm(PartContextType.PipeInnerWidth)} x {Mm(PartContextType.PipeInnerHeight)} mm"
                : v.ContainsKey(PartContextType.StructInnerDiameter) ? $"{Mm(PartContextType.StructInnerDiameter)} mm dia"
                : $"{Mm(PartContextType.StructInnerWidth)} x {Mm(PartContextType.StructInnerLength)} mm";
            return v.TryGetValue(PartContextType.WallThickness, out double w) && w > 0 ? $"{main}, wall {Math.Round(w * 1000):0}" : main;
        }

        /// <summary>The families of one domain in a parts list.</summary>
        public static List<PartFamily> Families(Transaction tr, ObjectId partsListId, DomainType domain)
        {
            List<PartFamily> list = new List<PartFamily>();
            if (partsListId.IsNull || !(tr.GetObject(partsListId, OpenMode.ForRead) is PartsList pl)) return list;
            foreach (ObjectId id in pl.GetPartFamilyIdsByDomain(domain))
                if (tr.GetObject(id, OpenMode.ForRead) is PartFamily f) list.Add(f);
            return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Every family the catalogs offer for a domain (GUID, description and shape).</summary>
        public static List<(string guid, string description)> CatalogFamilies(DomainType domain)
        {
            try
            {
                return (PartsList.GetAvailablePartFamilies(domain) ?? new DataPartFamily[0])
                    .Select(f => (f.GUID, f.Description)).OrderBy(f => f.Description, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { return new List<(string, string)>(); }
        }

        /// <summary>The family of a parts list with this catalog GUID, adding it from the catalog when the list lacks it.</summary>
        public static ObjectId EnsureFamily(Transaction tr, ObjectId partsListId, DomainType domain, string guid, out bool added)
        {
            added = false;
            PartsList pl = (PartsList)tr.GetObject(partsListId, OpenMode.ForRead);
            ObjectId found = FindFamily(tr, pl, domain, guid);
            if (!found.IsNull) return found;
            pl.UpgradeOpen();
            pl.AddPartFamilyByGuid(domain, guid);
            added = true;
            return FindFamily(tr, pl, domain, guid);
        }

        private static ObjectId FindFamily(Transaction tr, PartsList pl, DomainType domain, string guid)
        {
            foreach (ObjectId id in pl.GetPartFamilyIdsByDomain(domain))
                if (tr.GetObject(id, OpenMode.ForRead) is PartFamily f && f.GUID.Equals(guid, StringComparison.OrdinalIgnoreCase)) return id;
            return ObjectId.Null;
        }

        /// <summary>The sizes in a family of the list, with their dimensions.</summary>
        public static List<(ObjectId id, string name, Dictionary<PartContextType, double> values)> ListSizes(Transaction tr, PartFamily fam)
        {
            var sizes = new List<(ObjectId, string, Dictionary<PartContextType, double>)>();
            for (int i = 0; i < fam.PartSizeCount; i++)
                if (tr.GetObject(fam[i], OpenMode.ForRead) is PartSize s)
                {
                    Dictionary<PartContextType, double> v;
                    try { v = ReadValues(fam, s.SizeDataRecord); } catch (Exception) { v = new Dictionary<PartContextType, double>(); }
                    sizes.Add((fam[i], s.Name, v));
                }
            return sizes;
        }

        /// <summary>
        /// The sizes the catalog offers for a family: every row of its size table, or every combination of its listed values.
        /// A dimension that is a free range has no list; such sizes are made with "New size".
        /// </summary>
        public static List<Dictionary<PartContextType, double>> CatalogSizes(PartFamily fam)
        {
            List<Dictionary<PartContextType, double>> sizes = new List<Dictionary<PartContextType, double>>();
            SizeFilterRecord rec;
            try { rec = new SizeFilterRecord(fam); } catch (Exception) { return sizes; }
            PartContextType[] keys = KeyContexts(fam);
            SizeFilterField first = NavisPartMatcher.Param(rec, keys[0]);
            if (first == null) return sizes;
            bool twoDims = keys.Length > 2 && keys[1] != PartContextType.WallThickness;
            SizeFilterField second = twoDims ? NavisPartMatcher.Param(rec, keys[1]) : null;
            SizeFilterField wall = NavisPartMatcher.Param(rec, PartContextType.WallThickness);
            SizeFilterField floor = NavisPartMatcher.Param(rec, PartContextType.FloorThickness);
            double Read(SizeFilterField f) { try { return f == null ? 0 : NavisPartMatcher.ToMetres(f, f.Value); } catch (Exception) { return 0; } }

            void Add(double a, double b, double w)
            {
                Dictionary<PartContextType, double> v = new Dictionary<PartContextType, double> { [keys[0]] = a };
                if (twoDims) v[keys[1]] = b;
                v[PartContextType.WallThickness] = w;
                if (fam.Domain == DomainType.Structure) v[PartContextType.FloorThickness] = Read(floor);
                if (!sizes.Any(x => Same(x, v))) sizes.Add(v);
            }

            try
            {
                if (first.DataSource == PartDataSourceType.BasicTable)
                {
                    // Table sizes depend on each other: each value narrows the next list, and the wall follows the row.
                    foreach ((object _, object _, double am, double bm, double wm) in NavisPartMatcher.TableRows(first, second, wall))
                    {
                        Add(am, bm, wm);
                        if (sizes.Count >= MaxCatalogSizes) break;
                    }
                }
                else if (first.IsFromList)
                {
                    List<object> walls = wall != null && wall.IsFromList ? NavisPartMatcher.ListValues(wall) : new List<object> { null };
                    foreach (object a in NavisPartMatcher.ListValues(first))
                    {
                        NavisPartMatcher.Set(first, a);
                        List<object> seconds = second != null && second.IsFromList ? NavisPartMatcher.ListValues(second) : new List<object> { null };
                        foreach (object b in seconds)
                        {
                            if (b != null) NavisPartMatcher.Set(second, b);
                            foreach (object w in walls)
                            {
                                if (w != null) NavisPartMatcher.Set(wall, w);
                                Add(NavisPartMatcher.ToMetres(first, a), b == null ? Read(second) : NavisPartMatcher.ToMetres(second, b), w == null ? Read(wall) : NavisPartMatcher.ToMetres(wall, w));
                                if (sizes.Count >= MaxCatalogSizes) return Sorted(sizes, keys);
                            }
                        }
                    }
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return Sorted(sizes, keys);
        }

        private static List<Dictionary<PartContextType, double>> Sorted(List<Dictionary<PartContextType, double>> sizes, PartContextType[] keys) =>
            sizes.OrderBy(v => v.TryGetValue(keys[0], out double a) ? a : 0).ThenBy(v => keys.Length > 2 && v.TryGetValue(keys[1], out double b) ? b : 0)
                 .ThenBy(v => v.TryGetValue(PartContextType.WallThickness, out double w) ? w : 0).ToList();

        /// <summary>True when every dimension can be set to its value exactly (a listed value, a table row, or within a free range).</summary>
        public static bool CatalogAllows(PartFamily fam, Dictionary<PartContextType, double> values) => TryFill(fam, values, out _) == null;

        /// <summary>A size filter set to the values, or the reason it cannot be (a value the catalog does not offer).</summary>
        private static string TryFill(PartFamily fam, Dictionary<PartContextType, double> values, out SizeFilterRecord rec)
        {
            try { rec = new SizeFilterRecord(fam); } catch (Exception ex) { rec = null; return ex.Message; }
            foreach (PartContextType c in KeyContexts(fam))
            {
                if (!values.TryGetValue(c, out double target)) continue;
                SizeFilterField f = NavisPartMatcher.Param(rec, c);
                if (f == null) continue;
                // A wall or floor that follows the table row is checked after the row is set.
                if (f.IsReadOnly || f.DataSource == PartDataSourceType.BasicTable && c != KeyContexts(fam)[0] && (c == PartContextType.WallThickness || c == PartContextType.FloorThickness))
                {
                    if (c == PartContextType.WallThickness && Math.Abs(NavisPartMatcher.ToMetres(f, f.Value) - target) > SameSize)
                        return $"the catalog gives a {Math.Round(NavisPartMatcher.ToMetres(f, f.Value) * 1000):0} mm wall to this size, not {Math.Round(target * 1000):0} mm";
                    continue;
                }
                object raw = null;
                if (f.IsFromRange)
                {
                    PartDataRange r = f.ValueRange;
                    double min = NavisPartMatcher.ToMetres(f, r.RangeMin), max = NavisPartMatcher.ToMetres(f, r.RangeMax);
                    if (target < min - SameSize || target > max + SameSize) return $"{f.Description} must be {min * 1000:0}–{max * 1000:0} mm in this family";
                    raw = NavisPartMatcher.FromMetres(f, target);
                }
                else
                {
                    foreach (object v in NavisPartMatcher.ListValues(f))
                        if (Math.Abs(NavisPartMatcher.ToMetres(f, v) - target) <= SameSize) { raw = v; break; }
                    if (raw == null) return $"the catalog has no {f.Description.ToLowerInvariant()} of {Math.Round(target * 1000):0} mm for this family";
                }
                try { f.Value = raw; } catch (Exception ex) { return ex.Message; }
            }
            return null;
        }

        /// <summary>The size of the family with these dimensions, or null.</summary>
        public static ObjectId FindSize(Transaction tr, PartFamily fam, Dictionary<PartContextType, double> values)
        {
            foreach ((ObjectId id, string _, Dictionary<PartContextType, double> v) in ListSizes(tr, fam))
                if (Same(values, v)) return id;
            return ObjectId.Null;
        }

        /// <summary>
        /// Adds a catalog size to the family (already in the list: that one is returned). Returns null with the reason when the
        /// catalog does not offer these dimensions; the size gets the family's usual style and rules.
        /// </summary>
        public static ObjectId AddSize(Transaction tr, PartFamily fam, Dictionary<PartContextType, double> values, out string error)
        {
            error = null;
            ObjectId existing = FindSize(tr, fam, values);
            if (!existing.IsNull) return existing;
            error = TryFill(fam, values, out SizeFilterRecord rec);
            if (error != null) return ObjectId.Null;

            HashSet<ObjectId> before = NavisPartMatcher.SizeIds(fam);
            ObjectId template = before.FirstOrDefault();
            if (!fam.IsWriteEnabled) fam.UpgradeOpen();
            try { fam.AddPartSize(rec); } catch (Exception ex) { error = ex.Message; return ObjectId.Null; }
            ObjectId added = NavisPartMatcher.SizeIds(fam).FirstOrDefault(id => !before.Contains(id));
            if (added.IsNull) { error = "Civil 3D did not add the size."; return ObjectId.Null; }

            PartSize size = (PartSize)tr.GetObject(added, OpenMode.ForWrite);
            // Free-range dimensions (any-size families) are not taken from the filter: Civil 3D builds the default, and the
            // size's own values are then set and written back.
            if (!Same(values, ReadValues(fam, size.SizeDataRecord)) && KeyContexts(fam).Any(c => NavisPartMatcher.Param(rec, c)?.IsFromRange == true))
            {
                try
                {
                    PartDataRecord own = size.SizeDataRecord;
                    foreach (KeyValuePair<PartContextType, double> kv in values)
                    {
                        PartDataField f = own.GetDataFieldBy(kv.Key);
                        if (f != null && !f.IsReadOnly) f.Value = NavisPartMatcher.FromMetres(f, kv.Value);
                    }
                    size.SizeDataRecord = own;
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            Dictionary<PartContextType, double> built = ReadValues(fam, size.SizeDataRecord);
            if (!Same(values, built))
            {
                try { fam.RemovePartSize(added); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
                string fields = "";
                try { fields = string.Join("; ", size.SizeDataRecord.GetAllDataFields().Select(df => $"{df.Name}={df.Value}")); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                string sizeName = "";
                try { sizeName = size.Name; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { sizeName = "unreadable: the family's catalog is not the drawing's"; }
                error = $"Civil 3D built {DescribeAll(built)} (size \"{sizeName}\"{(fields.Length > 0 ? ", fields " + fields : "")}) from {DescribeAll(values)}.";
                return ObjectId.Null;
            }
            CopyLook(tr, template, size);
            return added;
        }

        /// <summary>Every value by name, in mm: "PipeInnerDiameter 110, WallThickness 0".</summary>
        private static string DescribeAll(Dictionary<PartContextType, double> v) =>
            v.Count == 0 ? "(no values)" : string.Join(", ", v.Select(kv => $"{kv.Key} {Math.Round(kv.Value * 1000, 1)}"));

        /// <summary>Gives a new size the style, rules and render material of another size of its family.</summary>
        private static void CopyLook(Transaction tr, ObjectId templateId, PartSize size)
        {
            if (templateId.IsNull || !(tr.GetObject(templateId, OpenMode.ForRead) is PartSize t)) return;
            try { if (!t.PartStyleId.IsNull) size.PartStyleId = t.PartStyleId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { if (!t.RulesStyleId.IsNull) size.RulesStyleId = t.RulesStyleId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { if (!t.MaterialStyleId.IsNull) size.MaterialStyleId = t.MaterialStyleId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        /// <summary>
        /// The family and size to give a part of this network: from the network's own parts list, where the chosen family and
        /// size are added when missing; or, when the pick asks for it, the network is switched to the chosen list first.
        /// <paramref name="networkId"/> may be null, with <paramref name="partsListId"/> the list to use (a new network's).
        /// </summary>
        /// <remarks>
        /// Never throws. The catalog is only one way to get the part: the network's list is searched first (no catalog needed),
        /// then the catalog adds the family or size, and when it cannot (catalog missing, or a family from another catalog) the
        /// part is used as it is from the parts list that holds it in the drawing. Civil 3D takes any part of the drawing, so
        /// work goes on without the catalog; the log says so.
        /// </remarks>
        public static (ObjectId family, ObjectId size) Resolve(Transaction tr, ObjectId networkId, ObjectId partsListId, PartPick pick, List<string> log)
        {
            ObjectId listId = partsListId;
            string networkName = null;
            try
            {
                if (!networkId.IsNull && tr.GetObject(networkId, OpenMode.ForRead) is Network net)
                {
                    networkName = net.Name;
                    if (pick.SwitchNetwork && net.PartsListId != pick.PartsListId && !pick.PartsListId.IsNull)
                    {
                        net.UpgradeOpen();
                        net.PartsListId = pick.PartsListId;
                        log?.Add($"Network {net.Name} now uses the parts list \"{pick.PartsListName}\".");
                    }
                    listId = net.PartsListId;
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (listId.IsNull) listId = pick.PartsListId;

            string why = null;
            if (!listId.IsNull)
            {
                try
                {
                    // In the network's own list already: no catalog needed.
                    PartsList list = (PartsList)tr.GetObject(listId, OpenMode.ForRead);
                    ObjectId famId = FindFamily(tr, list, pick.Domain, pick.FamilyGuid);
                    if (!famId.IsNull)
                    {
                        PartFamily fam = (PartFamily)tr.GetObject(famId, OpenMode.ForRead);
                        if (Usable(pick.SizeId) && NavisPartMatcher.SizeIds(fam).Contains(pick.SizeId)) return (famId, pick.SizeId);
                        ObjectId found = pick.Values.Count > 0 ? FindSize(tr, fam, pick.Values) : ObjectId.Null;
                        if (!found.IsNull) return (famId, found);
                    }

                    // From the catalog: the family, then the size.
                    try { famId = EnsureFamily(tr, listId, pick.Domain, pick.FamilyGuid, out bool addedFamily); if (addedFamily) log?.Add($"Added the family \"{pick.FamilyName}\" to the parts list \"{list.Name}\"."); }
                    catch (Exception ex) { famId = ObjectId.Null; why = ex.Message.TrimEnd('.'); }
                    if (!famId.IsNull)
                    {
                        PartFamily fam = (PartFamily)tr.GetObject(famId, OpenMode.ForRead);
                        ObjectId sizeId = ObjectId.Null;
                        string error = null;
                        if (pick.Values.Count > 0) try { sizeId = AddSize(tr, fam, pick.Values, out error); } catch (Exception ex) { error = ex.Message.TrimEnd('.'); }
                        if (!sizeId.IsNull) { log?.Add($"Added the size {pick.SizeName} to \"{pick.FamilyName}\" in the parts list \"{list.Name}\"."); return (famId, sizeId); }
                        why = error ?? "its dimensions could not be read";
                    }
                }
                catch (Exception ex) { why = ex.Message.TrimEnd('.'); }
            }

            // As it is in the drawing.
            (ObjectId family, ObjectId size) own = InDrawing(tr, pick);
            if (!own.size.IsNull)
            {
                log?.Add($"{pick.Label} is used as it is from the parts list \"{pick.PartsListName}\"" +
                         (networkName != null ? $" (it could not be put in the list of {networkName}{(why != null ? ": " + why : "")})." : "."));
                return own;
            }
            log?.Add($"{pick.Label} could not be found or added{(networkName != null ? " for " + networkName : "")}{(why != null ? ": " + why : "")}.");
            return (ObjectId.Null, ObjectId.Null);
        }

        private static bool Usable(ObjectId id) => !id.IsNull && id.IsValid && !id.IsErased;

        /// <summary>The picked family and size where they are in the drawing (the size by id, else by dimensions in the picked list's family).</summary>
        private static (ObjectId family, ObjectId size) InDrawing(Transaction tr, PartPick pick)
        {
            try
            {
                ObjectId famId = Usable(pick.FamilyId) ? pick.FamilyId
                    : Usable(pick.PartsListId) && tr.GetObject(pick.PartsListId, OpenMode.ForRead) is PartsList pl ? FindFamily(tr, pl, pick.Domain, pick.FamilyGuid) : ObjectId.Null;
                if (famId.IsNull || !(tr.GetObject(famId, OpenMode.ForRead) is PartFamily fam)) return (ObjectId.Null, ObjectId.Null);
                if (Usable(pick.SizeId) && NavisPartMatcher.SizeIds(fam).Contains(pick.SizeId)) return (famId, pick.SizeId);
                ObjectId size = pick.Values.Count > 0 ? FindSize(tr, fam, pick.Values) : ObjectId.Null;
                if (size.IsNull) for (int i = 0; i < fam.PartSizeCount; i++) if (tr.GetObject(fam[i], OpenMode.ForRead) is PartSize s && s.Name == pick.SizeName) { size = fam[i]; break; }
                return size.IsNull ? (ObjectId.Null, ObjectId.Null) : (famId, size);
            }
            catch (Exception) { return (ObjectId.Null, ObjectId.Null); }
        }

        /// <summary>How many pipes and structures in the drawing use each family and each size.</summary>
        public static Dictionary<ObjectId, int> Usage(Transaction tr, CivilDocument doc)
        {
            Dictionary<ObjectId, int> used = new Dictionary<ObjectId, int>();
            void Count(ObjectId id) { if (!id.IsNull) used[id] = used.TryGetValue(id, out int n) ? n + 1 : 1; }
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds().Live().Concat(net.GetStructureIds().Live()))
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Part part)) continue;
                    Count(part.PartFamilyId);
                    try { Count(part.GetPartSizeId()); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }
            return used;
        }

        /// <summary>The networks using a parts list.</summary>
        public static List<string> NetworksUsing(Transaction tr, CivilDocument doc, ObjectId partsListId)
        {
            List<string> names = new List<string>();
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
                if (tr.GetObject(netId, OpenMode.ForRead) is Network net && net.PartsListId == partsListId) names.Add(net.Name);
            return names;
        }

        /// <summary>A new parts list, empty or with the same families and sizes as another.</summary>
        public static ObjectId CreateList(Transaction tr, CivilDocument doc, string name, ObjectId copyFrom, List<string> log)
        {
            ObjectId id = doc.Styles.PartsListSet.Add(name);
            if (copyFrom.IsNull) return id;
            foreach (DomainType domain in new[] { DomainType.Pipe, DomainType.Structure })
                foreach (PartFamily src in Families(tr, copyFrom, domain))
                {
                    ObjectId famId;
                    try { famId = EnsureFamily(tr, id, domain, src.GUID, out _); }
                    catch (Exception ex) { log.Add($"{src.Name}: {ex.Message}"); continue; }
                    if (famId.IsNull) continue;
                    PartFamily fam = (PartFamily)tr.GetObject(famId, OpenMode.ForWrite);
                    // A new family comes with a default size; it stays only if the source list has it too.
                    HashSet<ObjectId> defaults = NavisPartMatcher.SizeIds(fam);
                    List<Dictionary<PartContextType, double>> wanted = new List<Dictionary<PartContextType, double>>();
                    foreach ((ObjectId sid, string sname, Dictionary<PartContextType, double> v) in ListSizes(tr, src))
                    {
                        wanted.Add(v);
                        ObjectId copy = AddSize(tr, fam, v, out string error);
                        if (copy.IsNull) { log.Add($"{src.Name} · {sname}: {error}"); continue; }
                        PartSize from = (PartSize)tr.GetObject(sid, OpenMode.ForRead), to = (PartSize)tr.GetObject(copy, OpenMode.ForWrite);
                        CopyLook(tr, sid, to);
                        try { to.Name = from.Name; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    }
                    foreach (ObjectId d in defaults)
                        if (tr.GetObject(d, OpenMode.ForRead) is PartSize ds && !wanted.Any(w => Same(w, ReadValues(fam, ds.SizeDataRecord))))
                            try { fam.RemovePartSize(d); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
                }
            return id;
        }

        /// <summary>A parts list name not used yet in the drawing.</summary>
        public static string UniqueListName(Transaction tr, CivilDocument doc, string baseName)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in doc.Styles.PartsListSet)
                if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl) names.Add(pl.Name);
            string name = baseName;
            for (int i = 2; names.Contains(name); i++) name = $"{baseName} ({i})";
            return name;
        }
    }
}
