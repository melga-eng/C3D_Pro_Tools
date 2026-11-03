using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    /// <summary>The Civil 3D part chosen for one import row, with what it really measures.</summary>
    internal sealed class PartChoice
    {
        public ObjectId FamilyId, SizeId;
        public string FamilyName = "", SizeName = "";
        /// <summary>Pipes: inner width/height and wall. Structures: inner width/length or diameter and wall. Metres.</summary>
        public double InnerWidth, InnerHeight, Wall, InnerLength, InnerDiameter;
        public double OuterWidth, OuterHeight, OuterLength;
        /// <summary>Largest difference from the Navisworks size (metres).</summary>
        public double Deviation;
        public bool MaterialMatched;
        public string Note = "";
        /// <summary>Method B: the size does not exist yet and will be added to this catalog family on import.</summary>
        public CatalogAddition Pending;
    }

    /// <summary>A size to add to a stock catalog family (Method B). Values in millimetres by catalog context.</summary>
    internal sealed class CatalogAddition
    {
        public string XmlPath = "", FamilyDescription = "", Label = "";
        public Dictionary<string, double> ValuesMm = new Dictionary<string, double>();
        public string Key => XmlPath + "|" + string.Join(";", ValuesMm.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value:0.###}"));
    }

    /// <summary>How sizes the catalog lacks are created.</summary>
    internal enum CatalogMode
    {
        /// <summary>Nearest catalog size (or an exact solid when it is too far off).</summary>
        None,
        /// <summary>Method A: C3D Pro any-size families, every size exact.</summary>
        AnySize,
        /// <summary>Method B: the missing sizes are added to the stock catalog families.</summary>
        Extend
    }

    /// <summary>The catalogs the drawing uses, found from the registered catalogs and the families Civil 3D offers.</summary>
    internal sealed class NavisCatalogContext
    {
        public CatalogInfo Pipe, Structure;
        public List<CatalogInfo> All = new List<CatalogInfo>();

        public static NavisCatalogContext Resolve()
        {
            NavisCatalogContext ctx = new NavisCatalogContext();
            try
            {
                string key = HostApplicationServices.Current.UserRegistryProductRootKey;
                string profile = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CPROFILE") as string ?? "";
                ctx.All = NavisCatalogEditor.RegisteredCatalogs(key, profile);
                ctx.Pipe = NavisCatalogEditor.ActiveCatalog(ctx.All, PartsList.GetAvailablePartFamilies(DomainType.Pipe).Select(f => f.GUID).ToList());
                ctx.Structure = NavisCatalogEditor.ActiveCatalog(ctx.All, PartsList.GetAvailablePartFamilies(DomainType.Structure).Select(f => f.GUID).ToList());
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return ctx;
        }

        /// <summary>True when files can be written in the catalog folder (a shared network catalog may be read-only).</summary>
        public static bool Writable(CatalogInfo c)
        {
            if (c == null) return false;
            try
            {
                string probe = System.IO.Path.Combine(c.Folder, "c3dpro-write-test.tmp");
                System.IO.File.WriteAllText(probe, "");
                System.IO.File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Matches import rows to the Civil 3D catalog. Every family the catalog offers is considered, but only the families and
    /// sizes actually chosen are added, to a dedicated parts list (default "Navis Import"), so the drawing's own lists stay clean.
    /// Exact sizes are created where the catalog allows a range; otherwise the nearest listed size is used and its error reported.
    /// </summary>
    internal sealed class NavisPartMatcher
    {
        private readonly Transaction _tr;
        private readonly PartsList _list;
        private readonly DataPartFamily[] _pipeFamilies, _structFamilies;
        private readonly Dictionary<string, ObjectId> _familyByGuid = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PartChoice> _cache = new Dictionary<string, PartChoice>();

        public ObjectId PartsListId { get; }
        /// <summary>Families and sizes this matcher added (id → label), so unused ones can be removed again.</summary>
        public readonly Dictionary<ObjectId, string> CreatedFamilies = new Dictionary<ObjectId, string>();
        public readonly Dictionary<ObjectId, (ObjectId family, string label)> CreatedSizes = new Dictionary<ObjectId, (ObjectId, string)>();
        public bool CreatedPartsList { get; }
        private readonly CatalogMode _mode;
        private readonly NavisCatalogContext _catalogs;
        /// <summary>Method B: wall of each inner size already planned for a table family, so two walls for one size are never planned.</summary>
        private readonly Dictionary<string, double> _plannedWalls = new Dictionary<string, double>();

        public NavisPartMatcher(Transaction tr, CivilDocument doc, string partsListName, CatalogMode mode = CatalogMode.None, NavisCatalogContext catalogs = null)
        {
            _tr = tr;
            _mode = mode;
            _catalogs = catalogs ?? new NavisCatalogContext();
            foreach (ObjectId id in doc.Styles.PartsListSet)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl && pl.Name.Equals(partsListName, StringComparison.OrdinalIgnoreCase)) { PartsListId = id; break; }
            }
            if (PartsListId.IsNull)
            {
                PartsListId = doc.Styles.PartsListSet.Add(partsListName);
                CreatedPartsList = true;
            }
            _list = (PartsList)tr.GetObject(PartsListId, OpenMode.ForWrite);
            foreach (DomainType domain in new[] { DomainType.Pipe, DomainType.Structure })
                foreach (ObjectId famId in _list.GetPartFamilyIdsByDomain(domain))
                    if (tr.GetObject(famId, OpenMode.ForRead) is PartFamily f) _familyByGuid[f.GUID] = famId;
            _pipeFamilies = SafeFamilies(DomainType.Pipe);
            _structFamilies = SafeFamilies(DomainType.Structure);
        }

        private static DataPartFamily[] SafeFamilies(DomainType domain)
        {
            try { return PartsList.GetAvailablePartFamilies(domain) ?? new DataPartFamily[0]; }
            catch (Exception) { return new DataPartFamily[0]; }
        }

        /// <summary>Removes sizes and families this matcher added that no imported part ended up using.</summary>
        public void Prune(HashSet<ObjectId> usedSizes)
        {
            foreach (KeyValuePair<ObjectId, (ObjectId family, string label)> s in CreatedSizes.ToList())
            {
                if (usedSizes.Contains(s.Key)) continue;
                if (_tr.GetObject(s.Value.family, OpenMode.ForWrite) is PartFamily fam) TryRemoveSize(fam, s.Key);
                CreatedSizes.Remove(s.Key);
            }
            foreach (ObjectId famId in CreatedFamilies.Keys.ToList())
            {
                if (CreatedSizes.Values.Any(v => v.family == famId)) continue;
                if (_tr.GetObject(famId, OpenMode.ForWrite) is PartFamily fam && SizeIds(fam).Any(usedSizes.Contains)) continue;
                if (_tr.GetObject(famId, OpenMode.ForWrite) is PartFamily unused) RemoveFamily(unused);
            }
        }

        public int CatalogPipeFamilies => _pipeFamilies.Count(f => !NavisCatalogEditor.IsAnySize(f.GUID));
        public int CatalogStructureFamilies => _structFamilies.Count(f => !NavisCatalogEditor.IsAnySize(f.GUID));

        /// <summary>Forgets earlier matches, after sizes were added to the catalog (Method B), so the rows match again.</summary>
        public void ClearCache() => _cache.Clear();

        /// <summary>True when this session's catalog lists the any-size family (Method A); a newly added family appears after a restart.</summary>
        public bool AnySizeAvailable(string key)
        {
            AnySizeFamily f = NavisCatalogEditor.AnySize.First(x => x.Key == key);
            return (f.Pipe ? _pipeFamilies : _structFamilies).Any(d => d.GUID.Equals(f.Guid, StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================================================
        // Pipes and boxes
        // =====================================================================================

        public PartChoice MatchLinear(ImportCandidate c)
        {
            string key = $"L|{c.Circular}|{Mm(c.OuterWidth)}|{Mm(c.OuterHeight)}|{Mm(c.InnerWidth)}|{Mm(c.InnerHeight)}|{NavisInterpreter.MaterialKey(c.Material)}";
            if (_cache.TryGetValue(key, out PartChoice cached)) return cached;

            SweptShapeType shape = c.Circular ? SweptShapeType.Circular : SweptShapeType.Rectangular;
            string material = NavisInterpreter.MaterialKey(c.Material);

            // Method A: the any-size family makes the exact size every time.
            if (_mode == CatalogMode.AnySize)
            {
                PartChoice exact = TryAnySize(c.Circular ? "circle" : "box", c);
                if (exact != null) return _cache[key] = exact;
            }

            List<DataPartFamily> families = _pipeFamilies.Where(f => f.SweptShape == shape && !NavisCatalogEditor.IsAnySize(f.GUID)).ToList();
            if (families.Count == 0) return _cache[key] = new PartChoice { Note = $"The catalog has no {(c.Circular ? "circular" : "rectangular")} pipe family." };

            // Material first (a concrete pipe stays concrete), then every other family of the shape as a fallback.
            List<DataPartFamily> ordered = families.Where(f => material.Length > 0 && FamilyMaterial(f.Description) == material)
                .Concat(families.Where(f => !(material.Length > 0 && FamilyMaterial(f.Description) == material))).ToList();

            PartChoice best = null, bestOfMaterial = null;
            foreach (DataPartFamily fam in ordered)
            {
                PartChoice choice = TryLinearFamily(fam, c);
                if (choice == null) continue;
                choice.MaterialMatched = material.Length > 0 && FamilyMaterial(fam.Description) == material;
                if (choice.MaterialMatched && (bestOfMaterial == null || choice.Deviation < bestOfMaterial.Deviation)) bestOfMaterial = choice;
                if (best == null || Better(choice, best)) best = choice;
                if (best.MaterialMatched && best.Deviation < 0.0005) break;
            }
            if (best == null) best = new PartChoice { Note = "No catalog size could be created for this section." };
            else
            {
                if (material.Length > 0 && !best.MaterialMatched)
                    best.Note = bestOfMaterial == null
                        ? $"No {material} family in the catalog; matched by size only."
                        : $"The nearest {material} size is {bestOfMaterial.Deviation * 1000:0} mm off, so the closer {best.FamilyName} size is used.";
                OrientationNote(c, best);
            }

            // Method B: the catalog lacks the size, so it will be added to the best-matching stock family on import.
            if (_mode == CatalogMode.Extend && best.Deviation > NavisInterpreter.HighTolerance)
            {
                DataPartFamily target = ordered.FirstOrDefault();
                PartChoice pending = target == null ? null : PlanLinearAddition(target, c, best);
                if (pending != null) return _cache[key] = pending;
            }
            return _cache[key] = best;
        }

        /// <summary>Method B for a pipe: the row that gives this exact section, in the stock family's own table.</summary>
        private PartChoice PlanLinearAddition(DataPartFamily data, ImportCandidate c, PartChoice nearest)
        {
            string xml = _catalogs.Pipe == null ? null : NavisCatalogEditor.FamilyXml(_catalogs.Pipe, data.GUID);
            if (xml == null) return null;
            (double iw, double ih, double wall) = TargetSection(c, nearest?.Wall ?? 0);
            CatalogAddition add = new CatalogAddition { XmlPath = xml, FamilyDescription = data.Description };
            if (c.Circular)
            {
                add.ValuesMm["PipeInnerDiameter"] = Mm1(iw);
                add.ValuesMm["WallThickness"] = Mm1(wall);
                add.Label = $"{Mm1(iw):0} mm, wall {Mm1(wall):0}";
            }
            else
            {
                add.ValuesMm["PipeInnerWidth"] = Mm1(iw);
                add.ValuesMm["PipeInnerHeight"] = Mm1(ih);
                add.ValuesMm["WallThickness"] = Mm1(wall);
                add.Label = $"{Mm1(iw):0} x {Mm1(ih):0} mm, wall {Mm1(wall):0}";
            }
            if (!NavisCatalogContext.Writable(_catalogs.Pipe)) return null;

            // In a size table the wall follows the row, and Civil 3D always uses the first row of a diameter (or width and height).
            // A second row with the same inner size but another wall could never be chosen, so Method B cannot make it.
            string innerKey = xml + "|" + string.Join(";", add.ValuesMm.Where(k => k.Key != "WallThickness").Select(k => $"{k.Key}={k.Value}"));
            bool clash = NavisCatalogEditor.KindOf(xml, "WallThickness") == NavisCatalogEditor.ParamKind.Table
                && (NavisCatalogEditor.TableKeyConflict(xml, add.ValuesMm, "WallThickness")
                    || (_plannedWalls.TryGetValue(innerKey, out double plannedWall) && Math.Abs(plannedWall - add.ValuesMm["WallThickness"]) >= 0.0005));
            if (clash)
            {
                if (nearest != null)
                    nearest.Note = (nearest.Note + $" Method B cannot add {add.Label}: \"{data.Description}\" already has this " +
                        (c.Circular ? "diameter" : "width and height") + " with another wall, and Civil 3D always uses the first one. Method A makes it exact.").Trim();
                return null;
            }
            _plannedWalls[innerKey] = add.ValuesMm["WallThickness"];
            return new PartChoice
            {
                FamilyName = data.Description, SizeName = add.Label + " (added to the catalog)", Pending = add,
                InnerWidth = iw, InnerHeight = ih, Wall = wall, OuterWidth = iw + 2 * wall, OuterHeight = ih + 2 * wall,
                MaterialMatched = nearest?.MaterialMatched ?? false,
                Note = $"The catalog has no exact size; {add.Label} will be added to \"{data.Description}\" in the {_catalogs.Pipe.Name} catalog."
            };
        }

        /// <summary>The exact inner section and wall of a row: from its properties when known, else the outer size with the given wall.</summary>
        private static (double iw, double ih, double wall) TargetSection(ImportCandidate c, double fallbackWall)
        {
            double wall = c.InnerWidth > 0 ? Math.Max(0, (c.OuterWidth - c.InnerWidth) / 2) : c.Wall > 0 ? c.Wall : fallbackWall;
            double iw = c.InnerWidth > 0 ? c.InnerWidth : c.OuterWidth - 2 * wall;
            double ih = c.Circular ? iw : c.InnerHeight > 0 ? c.InnerHeight : c.OuterHeight - 2 * wall;
            if (iw <= 0 || ih <= 0) { wall = 0; iw = c.OuterWidth; ih = c.Circular ? iw : c.OuterHeight; }
            return (iw, ih, wall);
        }

        // =====================================================================================
        // Method A: any-size families
        // =====================================================================================

        /// <summary>
        /// Makes the exact size in the any-size family: a size is added with the defaults, then its own values are set to the row
        /// (the family's parameters are free ranges, so Civil 3D builds any value), and it is named after its real dimensions.
        /// Identical sizes are reused.
        /// </summary>
        private PartChoice TryAnySize(string key, ImportCandidate c)
        {
            AnySizeFamily def = NavisCatalogEditor.AnySize.First(f => f.Key == key);
            DataPartFamily data = (def.Pipe ? _pipeFamilies : _structFamilies).FirstOrDefault(f => f.GUID.Equals(def.Guid, StringComparison.OrdinalIgnoreCase));
            if (data == null) return null;
            PartFamily fam = EnsureFamily(def.Pipe ? DomainType.Pipe : DomainType.Structure, data, out bool added);
            if (fam == null) return null;

            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            string name;
            if (def.Pipe)
            {
                (double iw, double ih, double wall) = TargetSection(c, 0);
                if (c.Circular) { values[PartContextType.PipeInnerDiameter] = iw; name = $"{Mm1(iw):0} mm Pipe wall {Mm1(wall):0} Navis"; }
                else { values[PartContextType.PipeInnerWidth] = iw; values[PartContextType.PipeInnerHeight] = ih; name = $"{Mm1(iw):0} x {Mm1(ih):0} mm Box wall {Mm1(wall):0} Navis"; }
                values[PartContextType.WallThickness] = wall;
            }
            else
            {
                // The outer size is what Navisworks shows; walls and floor are unknown, so plausible ones are used inside it.
                double outerMin = c.StructCircular ? c.StructDiameter : Math.Min(c.StructWidth, c.StructLength);
                double wall = Math.Min(0.15, outerMin / 6);
                double floor = !double.IsNaN(c.Sump) && c.Bottom != 0 && c.Sump - c.Bottom > 0.001 ? c.Sump - c.Bottom : 0.2;
                if (c.StructCircular) { values[PartContextType.StructInnerDiameter] = c.StructDiameter - 2 * wall; name = $"{Mm1(c.StructDiameter - 2 * wall):0} mm Cylindrical Structure Navis"; }
                else
                {
                    values[PartContextType.StructInnerWidth] = c.StructWidth - 2 * wall;
                    values[PartContextType.StructInnerLength] = c.StructLength - 2 * wall;
                    name = $"{Mm1(c.StructWidth - 2 * wall):0} x {Mm1(c.StructLength - 2 * wall):0} mm Structure Navis";
                }
                values[PartContextType.WallThickness] = wall;
                values[PartContextType.FloorThickness] = floor;
            }

            Func<PartDataRecord, (double, double, double)> read = r =>
            {
                double wall = WallOf(fam, r);
                if (def.Pipe) { if (c.Circular) { double d = Field(r, PartContextType.PipeInnerDiameter); return (d, d, wall); } return (Field(r, PartContextType.PipeInnerWidth), Field(r, PartContextType.PipeInnerHeight), wall); }
                if (c.StructCircular) { double d = Field(r, PartContextType.StructInnerDiameter); return (d, d, wall); }
                return (Field(r, PartContextType.StructInnerWidth), Field(r, PartContextType.StructInnerLength), wall);
            };
            Func<(double a, double b, double wall), double> score = def.Pipe ? (Func<(double a, double b, double wall), double>)(s => LinearScore(c, s.a, s.b, s.wall)) : s => StructScore(c, s.a, s.b, s.wall);

            PartChoice reuse = BestExisting(fam, read, score, c, 0.0005);
            if (reuse != null) return Finish(reuse, def, c);

            HashSet<ObjectId> before = SizeIds(fam);
            try { fam.AddPartSize(new SizeFilterRecord(fam)); } catch (Exception) { if (added) RemoveFamily(fam); return null; }
            ObjectId sizeId = SizeIds(fam).FirstOrDefault(id => !before.Contains(id));
            if (sizeId.IsNull) { if (added) RemoveFamily(fam); return null; }
            PartSize size = (PartSize)_tr.GetObject(sizeId, OpenMode.ForWrite);
            try
            {
                PartDataRecord rec = size.SizeDataRecord;
                foreach (KeyValuePair<PartContextType, double> kv in values)
                {
                    PartDataField f = rec.GetDataFieldBy(kv.Key);
                    if (f != null) f.Value = FromMetres(f, kv.Value);
                }
                SetMaterial(rec, c.Material);
                size.SizeDataRecord = rec;
                try { size.Name = name; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            catch (Exception) { TryRemoveSize(fam, sizeId); if (added) RemoveFamily(fam); return null; }

            (double, double, double) dims = read(size.SizeDataRecord);
            double deviation = score(dims);
            // A size Civil 3D did not build as asked is never used.
            if (deviation > 0.001) { TryRemoveSize(fam, sizeId); return null; }
            CreatedSizes[sizeId] = (fam.ObjectId, $"{fam.Name}: {size.Name}");
            return Finish(Describe(fam, size, dims, deviation, c), def, c);
        }

        private static PartChoice Finish(PartChoice p, AnySizeFamily def, ImportCandidate c)
        {
            p.MaterialMatched = true;
            p.Note = "Exact size in the C3D Pro any-size family.";
            return p;
        }

        /// <summary>Sets the size's material to the closest one the family offers (Reinforced Concrete, PVC, Ductile Iron...).</summary>
        private static void SetMaterial(PartDataRecord rec, string material)
        {
            string key = NavisInterpreter.MaterialKey(material);
            if (key.Length == 0) return;
            try
            {
                PartDataField f = rec.GetDataFieldBy(PartContextType.Material);
                if (f == null || !f.IsFromList) return;
                PartDataList list = f.ValueList;
                for (int i = 0; i < list.Count; i++)
                {
                    string option = list[i] as string ?? "";
                    if (NavisInterpreter.MaterialKey(option) == key || (key == "corrugated metal" && option.IndexOf("steel", StringComparison.OrdinalIgnoreCase) >= 0)) { f.Value = option; return; }
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        private static double Mm1(double metres) => Math.Round(metres * 1000);

        /// <summary>Flags a box size that lies the other way round (wide where the duct is tall), the usual sign the catalog lacks the size.</summary>
        private static void OrientationNote(ImportCandidate c, PartChoice p)
        {
            if (c.Circular || p.SizeId.IsNull || Math.Abs(c.OuterWidth - c.OuterHeight) < 0.01) return;
            bool tall = c.OuterHeight > c.OuterWidth, partTall = p.OuterHeight > p.OuterWidth;
            if (tall != partTall && Math.Abs(p.OuterWidth - p.OuterHeight) >= 0.01)
                p.Note = (p.Note + $" The nearest catalog size is {(partTall ? "taller than wide" : "wider than tall")}, but the duct is {(tall ? "taller than wide" : "wider than tall")}: the catalog has no {ImportCandidate.Mm(c.InnerWidth > 0 ? c.InnerWidth : c.OuterWidth)} × {ImportCandidate.Mm(c.InnerHeight > 0 ? c.InnerHeight : c.OuterHeight)} size.").Trim();
        }

        /// <summary>Measures a size chosen by the user against the row (any family, from any parts list).</summary>
        public static PartChoice Evaluate(PartFamily fam, PartSize size, ImportCandidate c)
        {
            PartDataRecord r = size.SizeDataRecord;
            double wall = WallOf(fam, r);
            PartChoice p = new PartChoice { FamilyId = fam.ObjectId, SizeId = size.ObjectId, FamilyName = fam.Name, SizeName = size.Name, Wall = wall, MaterialMatched = true };
            if (fam.Domain == DomainType.Pipe)
            {
                bool circular = fam.SweptShape == SweptShapeType.Circular;
                double a = Field(r, circular ? PartContextType.PipeInnerDiameter : PartContextType.PipeInnerWidth);
                double b = circular ? a : Field(r, PartContextType.PipeInnerHeight);
                p.InnerWidth = a; p.InnerHeight = b;
                p.OuterWidth = a + 2 * wall; p.OuterHeight = b + 2 * wall;
                double outer = Math.Max(Math.Abs(p.OuterWidth - c.OuterWidth), Math.Abs(p.OuterHeight - c.OuterHeight));
                double inner = c.InnerWidth > 0 ? Math.Max(Math.Abs(a - c.InnerWidth), Math.Abs(b - (c.InnerHeight > 0 ? c.InnerHeight : c.InnerWidth))) : 0;
                p.Deviation = a > 0 ? Math.Max(outer, inner) : double.MaxValue;
                OrientationNote(c, p);
            }
            else
            {
                bool circular = fam.BoundingShape == BoundingShapeType.Cylinder;
                if (circular)
                {
                    p.InnerDiameter = Field(r, PartContextType.StructInnerDiameter);
                    p.OuterWidth = p.OuterLength = p.InnerDiameter + 2 * wall;
                    double target = c.StructCircular ? c.StructDiameter : Math.Max(c.StructWidth, c.StructLength);
                    p.Deviation = p.InnerDiameter > 0 ? Math.Abs(p.OuterWidth - target) : double.MaxValue;
                }
                else
                {
                    p.InnerWidth = Field(r, PartContextType.StructInnerWidth);
                    p.InnerLength = Field(r, PartContextType.StructInnerLength);
                    p.OuterWidth = p.InnerWidth + 2 * wall; p.OuterLength = p.InnerLength + 2 * wall;
                    double w = Math.Min(p.OuterWidth, p.OuterLength), l = Math.Max(p.OuterWidth, p.OuterLength);
                    double tw = c.StructCircular ? c.StructDiameter : c.StructWidth, tl = c.StructCircular ? c.StructDiameter : c.StructLength;
                    p.Deviation = p.InnerWidth > 0 ? Math.Max(Math.Abs(w - tw), Math.Abs(l - tl)) : double.MaxValue;
                }
            }
            p.Note = ("Chosen by you. " + p.Note).Trim();
            return p;
        }

        /// <summary>Every size of a family with its dimensions, for the part picker.</summary>
        public static (double innerA, double innerB, double wall) ReadSize(PartFamily fam, PartSize size)
        {
            PartDataRecord r = size.SizeDataRecord;
            double wall = WallOf(fam, r);
            if (fam.Domain == DomainType.Pipe)
            {
                bool circular = fam.SweptShape == SweptShapeType.Circular;
                double a = Field(r, circular ? PartContextType.PipeInnerDiameter : PartContextType.PipeInnerWidth);
                return (a, circular ? a : Field(r, PartContextType.PipeInnerHeight), wall);
            }
            if (fam.BoundingShape == BoundingShapeType.Cylinder) { double d = Field(r, PartContextType.StructInnerDiameter); return (d, d, wall); }
            return (Field(r, PartContextType.StructInnerWidth), Field(r, PartContextType.StructInnerLength), wall);
        }

        /// <summary>Prefers a material match unless the other is far closer in size.</summary>
        private static bool Better(PartChoice a, PartChoice b)
        {
            if (a.MaterialMatched != b.MaterialMatched) return a.MaterialMatched ? a.Deviation <= b.Deviation + 0.05 : a.Deviation + 0.05 < b.Deviation;
            return a.Deviation < b.Deviation - 1e-6;
        }

        private PartChoice TryLinearFamily(DataPartFamily data, ImportCandidate c)
        {
            PartFamily fam = EnsureFamily(DomainType.Pipe, data, out bool added);
            if (fam == null) return null;

            Func<PartDataRecord, (double iw, double ih, double wall)> read = r =>
            {
                double wall = WallOf(fam, r);
                if (c.Circular) { double d = Field(r, PartContextType.PipeInnerDiameter); return (d, d, wall); }
                return (Field(r, PartContextType.PipeInnerWidth), Field(r, PartContextType.PipeInnerHeight), wall);
            };
            Func<(double iw, double ih, double wall), double> score = s => LinearScore(c, s.iw, s.ih, s.wall);

            PartChoice choice = BestExisting(fam, read, score, c, 0.001) ?? CreateLinearSize(fam, c, read, score) ?? BestExisting(fam, read, score, c, double.MaxValue);
            if (choice == null && added) RemoveFamily(fam);
            return choice;
        }

        /// <summary>Error of a pipe size against the row: outer size counts double (it is what clashes and sets cover), inner once when known.</summary>
        private static double LinearScore(ImportCandidate c, double iw, double ih, double wall)
        {
            if (iw <= 0) return double.MaxValue;
            if (c.Circular) ih = iw;
            double ow = iw + 2 * wall, oh = ih + 2 * wall;
            double outer = Math.Max(Math.Abs(ow - c.OuterWidth), Math.Abs(oh - c.OuterHeight));
            double inner = c.InnerWidth > 0 ? Math.Max(Math.Abs(iw - c.InnerWidth), Math.Abs(ih - (c.InnerHeight > 0 ? c.InnerHeight : c.InnerWidth))) : 0;
            return Math.Max(outer, inner);
        }

        private PartChoice CreateLinearSize(PartFamily fam, ImportCandidate c, Func<PartDataRecord, (double iw, double ih, double wall)> read, Func<(double, double, double), double> score)
        {
            SizeFilterRecord rec;
            try { rec = new SizeFilterRecord(fam); } catch (Exception) { return null; }
            SizeFilterField fWall = Param(rec, PartContextType.WallThickness);
            double wallTarget = c.InnerWidth > 0 ? (c.OuterWidth - c.InnerWidth) / 2 : c.Wall;
            List<(object raw, double m)> walls = Options(fWall, wallTarget > 0 ? wallTarget : 0.05);

            List<(Action apply, double score)> combos = new List<(Action, double)>();
            SizeFilterField first = Param(rec, c.Circular ? PartContextType.PipeInnerDiameter : PartContextType.PipeInnerWidth);
            SizeFilterField second = c.Circular ? null : Param(rec, PartContextType.PipeInnerHeight);
            if (first != null && first.DataSource == PartDataSourceType.BasicTable)
            {
                // Table sizes depend on each other: the height list narrows to the rows of the width just set, and the wall
                // follows the row. Walking the real rows avoids combinations the table does not have.
                foreach ((object a, object b, double am, double bm, double wm) in TableRows(first, second, fWall))
                    combos.Add((() => { first.Value = a; if (second != null && b != null) second.Value = b; }, LinearScore(c, am, c.Circular ? am : bm, wm)));
            }
            else if (c.Circular)
            {
                SizeFilterField fD = Param(rec, PartContextType.PipeInnerDiameter);
                if (fD == null) return null;
                foreach ((object rw, double wm) in walls)
                {
                    double diTarget = c.InnerWidth > 0 ? c.InnerWidth : c.OuterWidth - 2 * wm;
                    foreach ((object rd, double dm) in Options(fD, diTarget))
                        combos.Add((() => { Set(fD, rd); Set(fWall, rw); }, LinearScore(c, dm, dm, wm)));
                }
            }
            else
            {
                SizeFilterField fW = Param(rec, PartContextType.PipeInnerWidth), fH = Param(rec, PartContextType.PipeInnerHeight);
                if (fW == null || fH == null) return null;
                foreach ((object rw, double wm) in walls)
                {
                    double wT = c.InnerWidth > 0 ? c.InnerWidth : c.OuterWidth - 2 * wm;
                    double hT = c.InnerHeight > 0 ? c.InnerHeight : c.OuterHeight - 2 * wm;
                    List<(object raw, double m)> ws = Options(fW, wT), hs = Options(fH, hT);
                    foreach ((object rx, double xm) in ws)
                        foreach ((object ry, double ym) in hs)
                            combos.Add((() => { Set(fW, rx); Set(fH, ry); Set(fWall, rw); }, LinearScore(c, xm, ym, wm)));
                }
            }
            return TryCombos(fam, rec, combos, read, score, c);
        }

        // =====================================================================================
        // Structures
        // =====================================================================================

        public PartChoice MatchStructure(ImportCandidate c)
        {
            string key = $"S|{c.StructCircular}|{Mm(c.StructDiameter)}|{Mm(c.StructWidth)}|{Mm(c.StructLength)}";
            if (_cache.TryGetValue(key, out PartChoice cached)) return cached;
            BoundingShapeType shape = c.StructCircular ? BoundingShapeType.Cylinder : BoundingShapeType.Box;

            if (_mode == CatalogMode.AnySize)
            {
                PartChoice exact = TryAnySize(c.StructCircular ? "cyl" : "rect", c);
                if (exact != null) return _cache[key] = exact;
            }

            // Plain junction structures first: frames, cones and grates would add geometry Navisworks does not have.
            List<DataPartFamily> families = _structFamilies.Where(f => f.BoundingShape == shape && f.PartType != PartType.StructInletOutlet && !NavisCatalogEditor.IsAnySize(f.GUID))
                .OrderBy(f => StructureRank(f.Description)).ToList();
            PartChoice best = null;
            int tried = 0;
            foreach (DataPartFamily fam in families)
            {
                PartChoice choice = TryStructureFamily(fam, c);
                if (choice == null) continue;
                if (best == null || choice.Deviation < best.Deviation - 1e-6) best = choice;
                if (best.Deviation < 0.0005 || ++tried >= 4) break;
            }
            if (best == null) best = new PartChoice { Note = $"The catalog has no {(c.StructCircular ? "cylindrical" : "rectangular")} structure that fits." };

            if (_mode == CatalogMode.Extend && best.Deviation > NavisInterpreter.HighTolerance && families.Count > 0)
            {
                PartChoice pending = PlanStructureAddition(families[0], c);
                if (pending != null) return _cache[key] = pending;
            }
            return _cache[key] = best;
        }

        /// <summary>Method B for a structure: the list values (or table row) giving this outer size, using the family's own wall and floor.</summary>
        private PartChoice PlanStructureAddition(DataPartFamily data, ImportCandidate c)
        {
            string xml = _catalogs.Structure == null ? null : NavisCatalogEditor.FamilyXml(_catalogs.Structure, data.GUID);
            if (xml == null || !NavisCatalogContext.Writable(_catalogs.Structure)) return null;
            PartFamily fam = EnsureFamily(DomainType.Structure, data, out bool added);
            if (fam == null) return null;
            SizeFilterRecord rec;
            try { rec = new SizeFilterRecord(fam); } catch (Exception) { return null; }
            double DefaultOf(PartContextType ctx) { SizeFilterField f = Param(rec, ctx); try { return f == null ? 0 : ToMetres(f, f.Value); } catch (Exception) { return 0; } }

            double wall = DefaultOf(PartContextType.WallThickness);
            CatalogAddition add = new CatalogAddition { XmlPath = xml, FamilyDescription = data.Description };
            void Put(string context, double metres)
            {
                NavisCatalogEditor.ParamKind kind = NavisCatalogEditor.KindOf(xml, context);
                if (kind == NavisCatalogEditor.ParamKind.Table || kind == NavisCatalogEditor.ParamKind.List || kind == NavisCatalogEditor.ParamKind.Range) add.ValuesMm[context] = Mm1(metres);
            }
            double iw, il = 0;
            if (c.StructCircular)
            {
                iw = c.StructDiameter - 2 * wall;
                Put("StructInnerDiameter", iw);
                add.Label = $"{Mm1(iw):0} mm";
            }
            else
            {
                iw = c.StructWidth - 2 * wall;
                il = c.StructLength - 2 * wall;
                Put("StructInnerWidth", iw);
                Put("StructInnerLength", il);
                add.Label = $"{Mm1(iw):0} x {Mm1(il):0} mm";
            }
            if (iw <= 0) return null;
            Put("WallThickness", wall);
            if (!double.IsNaN(c.Sump) && c.Bottom != 0 && c.Sump - c.Bottom > 0.001) Put("FloorThickness", c.Sump - c.Bottom);
            else Put("FloorThickness", DefaultOf(PartContextType.FloorThickness));
            if (!double.IsNaN(c.Sump) && c.Rim - c.Sump > 0.1) Put("StructRimToSumpHeight", c.Rim - c.Sump);
            if (added) RemoveFamily(fam);
            return new PartChoice
            {
                FamilyName = data.Description, SizeName = add.Label + " (added to the catalog)", Pending = add, Wall = wall,
                InnerWidth = iw, InnerLength = il, InnerDiameter = c.StructCircular ? iw : 0,
                OuterWidth = iw + 2 * wall, OuterLength = c.StructCircular ? iw + 2 * wall : il + 2 * wall,
                Note = $"The catalog has no exact size; {add.Label} will be added to \"{data.Description}\" in the {_catalogs.Structure.Name} catalog."
            };
        }

        private static int StructureRank(string description)
        {
            string d = (description ?? "").ToLowerInvariant();
            if (d.Contains(" nf") || d.Contains("without frame") || d.Contains("simple")) return 0;
            if (d.Contains("junction")) return 1;
            if (d.Contains("frame") || d.Contains("2-tier") || d.Contains("eccentric")) return 3;
            return 2;
        }

        private PartChoice TryStructureFamily(DataPartFamily data, ImportCandidate c)
        {
            PartFamily fam = EnsureFamily(DomainType.Structure, data, out bool added);
            if (fam == null) return null;
            double floorTarget = !double.IsNaN(c.Sump) && c.Bottom != 0 ? Math.Max(0, c.Sump - c.Bottom) : -1;

            Func<PartDataRecord, (double a, double b, double wall)> read = r =>
            {
                double wall = WallOf(fam, r);
                if (c.StructCircular) { double d = Field(r, PartContextType.StructInnerDiameter); return (d, d, wall); }
                return (Field(r, PartContextType.StructInnerWidth), Field(r, PartContextType.StructInnerLength), wall);
            };
            Func<(double a, double b, double wall), double> score = s => StructScore(c, s.a, s.b, s.wall);

            PartChoice choice = BestExisting(fam, read, score, c, 0.001) ?? CreateStructureSize(fam, c, read, score, floorTarget) ?? BestExisting(fam, read, score, c, double.MaxValue);
            if (choice == null && added) RemoveFamily(fam);
            return choice;
        }

        private static double StructScore(ImportCandidate c, double a, double b, double wall)
        {
            if (a <= 0) return double.MaxValue;
            if (c.StructCircular) return Math.Abs(a + 2 * wall - c.StructDiameter);
            // Width and length may be either way round; rotation is set to match.
            double w = Math.Min(a, b) + 2 * wall, l = Math.Max(a, b) + 2 * wall;
            return Math.Max(Math.Abs(w - c.StructWidth), Math.Abs(l - c.StructLength));
        }

        private PartChoice CreateStructureSize(PartFamily fam, ImportCandidate c, Func<PartDataRecord, (double, double, double)> read, Func<(double, double, double), double> score, double floorTarget)
        {
            SizeFilterRecord rec;
            try { rec = new SizeFilterRecord(fam); } catch (Exception) { return null; }
            SizeFilterField fWall = Param(rec, PartContextType.WallThickness);
            SizeFilterField fFloor = Param(rec, PartContextType.FloorThickness);
            SizeFilterField fHeight = Param(rec, PartContextType.StructRimToSumpHeight);
            List<(object raw, double m)> walls = Options(fWall, 0.0);
            (object raw, double m) floor = Options(fFloor, floorTarget >= 0 ? floorTarget : 0.2).FirstOrDefault();
            (object raw, double m) height = Options(fHeight, double.IsNaN(c.Sump) ? 2.0 : Math.Max(0.5, c.Rim - c.Sump)).FirstOrDefault();

            List<(Action apply, double score)> combos = new List<(Action, double)>();
            if (c.StructCircular)
            {
                SizeFilterField fD = Param(rec, PartContextType.StructInnerDiameter);
                if (fD == null) return null;
                foreach ((object rw, double wm) in walls)
                    foreach ((object rd, double dm) in Options(fD, c.StructDiameter - 2 * wm))
                        combos.Add((() => { Set(fD, rd); Set(fWall, rw); Set(fFloor, floor.raw); Set(fHeight, height.raw); }, StructScore(c, dm, dm, wm)));
            }
            else
            {
                SizeFilterField fW = Param(rec, PartContextType.StructInnerWidth), fL = Param(rec, PartContextType.StructInnerLength);
                if (fW == null || fL == null) return null;
                foreach ((object rw, double wm) in walls)
                    foreach ((object rx, double xm) in Options(fW, c.StructWidth - 2 * wm))
                        foreach ((object ry, double ym) in Options(fL, c.StructLength - 2 * wm))
                            combos.Add((() => { Set(fW, rx); Set(fL, ry); Set(fWall, rw); Set(fFloor, floor.raw); Set(fHeight, height.raw); }, StructScore(c, xm, ym, wm)));
            }
            return TryCombos(fam, rec, combos, read, score, c);
        }

        // =====================================================================================
        // Shared size machinery
        // =====================================================================================

        /// <summary>The closest size already in the list, if its error is under <paramref name="maxScore"/>.</summary>
        private PartChoice BestExisting(PartFamily fam, Func<PartDataRecord, (double, double, double)> read, Func<(double, double, double), double> score, ImportCandidate c, double maxScore)
        {
            PartChoice best = null;
            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                if (!(_tr.GetObject(fam[i], OpenMode.ForRead) is PartSize size)) continue;
                (double, double, double) dims;
                try { dims = read(size.SizeDataRecord); } catch (Exception) { continue; }
                double s = score(dims);
                if (s < maxScore && (best == null || s < best.Deviation)) best = Describe(fam, size, dims, s, c);
            }
            return best;
        }

        /// <summary>Tries the best few size combinations; keeps the closest size Civil 3D accepts and removes any extra sizes it added.</summary>
        private PartChoice TryCombos(PartFamily fam, SizeFilterRecord rec, List<(Action apply, double score)> combos, Func<PartDataRecord, (double, double, double)> read,
            Func<(double, double, double), double> score, ImportCandidate c)
        {
            HashSet<ObjectId> before = SizeIds(fam);
            PartChoice best = null;
            ObjectId bestId = ObjectId.Null;
            foreach ((Action apply, double s) in combos.OrderBy(x => x.score).Take(10))
            {
                if (best != null && best.Deviation <= s + 1e-9) break;
                try
                {
                    apply();
                    fam.AddPartSize(rec);
                }
                catch (Exception) { continue; }
                foreach (ObjectId id in SizeIds(fam))
                {
                    if (before.Contains(id) || id == bestId) continue;
                    if (!(_tr.GetObject(id, OpenMode.ForRead) is PartSize size)) continue;
                    (double, double, double) dims;
                    try { dims = read(size.SizeDataRecord); } catch (Exception) { continue; }
                    // Civil 3D adds a row twice (as "… - 0", "… - 1") when asked again; keep the size already there instead.
                    ObjectId twin = before.FirstOrDefault(old => _tr.GetObject(old, OpenMode.ForRead) is PartSize o && SameDims(read(o.SizeDataRecord), dims));
                    if (!twin.IsNull)
                    {
                        TryRemoveSize(fam, id);
                        PartSize existing = (PartSize)_tr.GetObject(twin, OpenMode.ForRead);
                        double es = score(dims);
                        if (best == null || es < best.Deviation)
                        {
                            if (!bestId.IsNull && !before.Contains(bestId)) TryRemoveSize(fam, bestId);
                            best = Describe(fam, existing, dims, es, c);
                            bestId = twin;
                        }
                        continue;
                    }
                    double sc = score(dims);
                    if (best == null || sc < best.Deviation)
                    {
                        if (!bestId.IsNull && !before.Contains(bestId)) TryRemoveSize(fam, bestId);
                        best = Describe(fam, size, dims, sc, c);
                        bestId = id;
                    }
                    else TryRemoveSize(fam, id);
                }
                if (best != null && best.Deviation < 0.0005) break;
            }
            if (best != null && !before.Contains(bestId)) CreatedSizes[bestId] = (fam.ObjectId, $"{fam.Name}: {best.SizeName}");
            return best;
        }

        internal static bool SameDims((double a, double b, double w) x, (double a, double b, double w) y) =>
            Math.Abs(x.a - y.a) < 1e-6 && Math.Abs(x.b - y.b) < 1e-6 && Math.Abs(x.w - y.w) < 1e-6;

        private PartChoice Describe(PartFamily fam, PartSize size, (double a, double b, double wall) dims, double deviation, ImportCandidate c)
        {
            PartChoice p = new PartChoice
            {
                FamilyId = fam.ObjectId, SizeId = size.ObjectId, FamilyName = fam.Name, SizeName = size.Name,
                Wall = dims.wall, Deviation = deviation
            };
            if (fam.Domain == DomainType.Pipe)
            {
                p.InnerWidth = dims.a; p.InnerHeight = c.Circular ? dims.a : dims.b;
                p.OuterWidth = p.InnerWidth + 2 * dims.wall; p.OuterHeight = p.InnerHeight + 2 * dims.wall;
            }
            else if (c.StructCircular) { p.InnerDiameter = dims.a; p.OuterWidth = dims.a + 2 * dims.wall; }
            else
            {
                p.InnerWidth = dims.a; p.InnerLength = dims.b;
                p.OuterWidth = dims.a + 2 * dims.wall; p.OuterLength = dims.b + 2 * dims.wall;
            }
            return p;
        }

        internal static HashSet<ObjectId> SizeIds(PartFamily fam)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>();
            for (int i = 0; i < fam.PartSizeCount; i++) ids.Add(fam[i]);
            return ids;
        }

        private void TryRemoveSize(PartFamily fam, ObjectId id)
        {
            try { fam.RemovePartSize(id); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
        }

        private PartFamily EnsureFamily(DomainType domain, DataPartFamily data, out bool added)
        {
            added = false;
            if (!_familyByGuid.TryGetValue(data.GUID, out ObjectId famId))
            {
                try { _list.AddPartFamilyByGuid(domain, data.GUID); } catch (Exception) { return null; }
                foreach (ObjectId id in _list.GetPartFamilyIdsByDomain(domain))
                    if (_tr.GetObject(id, OpenMode.ForRead) is PartFamily f && f.GUID.Equals(data.GUID, StringComparison.OrdinalIgnoreCase)) { famId = id; break; }
                if (famId.IsNull) return null;
                _familyByGuid[data.GUID] = famId;
                added = true;
                CreatedFamilies[famId] = data.Description;
            }
            return _tr.GetObject(famId, OpenMode.ForWrite) as PartFamily;
        }

        private void RemoveFamily(PartFamily fam)
        {
            try
            {
                _familyByGuid.Remove(fam.GUID);
                CreatedFamilies.Remove(fam.ObjectId);
                _list.RemovePartFamily(fam.ObjectId);
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        internal static SizeFilterField Param(SizeFilterRecord rec, PartContextType context)
        {
            try { return rec.GetParamByContextAndIndex(context, 0); } catch (Exception) { return null; }
        }

        /// <summary>Every row of a size table: each value of the first parameter, each value of the second that its rows allow, and the wall of that row.</summary>
        internal static List<(object a, object b, double am, double bm, double wm)> TableRows(SizeFilterField first, SizeFilterField second, SizeFilterField wall)
        {
            List<(object, object, double, double, double)> rows = new List<(object, object, double, double, double)>();
            List<object> firstValues = ListValues(first);
            foreach (object a in firstValues)
            {
                try { first.Value = a; } catch (Exception) { continue; }
                double am = ToMetres(first, a);
                if (second == null)
                {
                    rows.Add((a, null, am, am, wall == null ? 0 : ToMetres(wall, wall.Value)));
                    continue;
                }
                foreach (object b in ListValues(second))
                {
                    try { second.Value = b; } catch (Exception) { continue; }
                    rows.Add((a, b, am, ToMetres(second, b), wall == null ? 0 : ToMetres(wall, wall.Value)));
                }
            }
            return rows;
        }

        internal static List<object> ListValues(PartDataField f)
        {
            List<object> values = new List<object>();
            try
            {
                if (f.IsFromList) { PartDataList list = f.ValueList; for (int i = 0; i < list.Count; i++) values.Add(list[i]); }
                else values.Add(f.Value);
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return values;
        }

        /// <summary>The values a size parameter may take, nearest to the target first: list items, the exact target within a range, or its fixed value.</summary>
        internal static List<(object raw, double m)> Options(PartDataField f, double targetMetres)
        {
            List<(object raw, double m)> values = new List<(object raw, double m)>();
            if (f == null) { values.Add((null, 0)); return values; }
            try
            {
                if (f.IsFromRange)
                {
                    PartDataRange r = f.ValueRange;
                    double min = ToMetres(f, r.RangeMin), max = ToMetres(f, r.RangeMax);
                    double t = Math.Max(min, Math.Min(max, targetMetres));
                    values.Add((FromMetres(f, t), t));
                }
                else if (f.IsFromList)
                {
                    PartDataList list = f.ValueList;
                    for (int i = 0; i < list.Count; i++) { object v = list[i]; values.Add((v, ToMetres(f, v))); }
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (values.Count == 0)
            {
                try { values.Add((f.Value, ToMetres(f, f.Value))); } catch (Exception) { values.Add((null, 0)); }
            }
            return values.OrderBy(v => Math.Abs(v.m - targetMetres)).Take(6).ToList();
        }

        internal static void Set(PartDataField f, object raw)
        {
            if (f == null || raw == null || f.IsReadOnly) return;
            f.Value = raw;
        }

        /// <summary>
        /// The wall of a size. Table and constant walls are not stored on the size itself, so they are read from the catalog:
        /// a size filter set to this size's own values gives the wall of its row.
        /// </summary>
        internal static double WallOf(PartFamily fam, PartDataRecord r)
        {
            try { PartDataField f = r.GetDataFieldBy(PartContextType.WallThickness); if (f != null) return ToMetres(f, f.Value); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                SizeFilterRecord rec = new SizeFilterRecord(fam);
                foreach (PartContextType ctx in new[] { PartContextType.PipeInnerDiameter, PartContextType.PipeInnerWidth, PartContextType.PipeInnerHeight,
                                                        PartContextType.StructInnerDiameter, PartContextType.StructInnerWidth, PartContextType.StructInnerLength })
                {
                    try
                    {
                        PartDataField own = r.GetDataFieldBy(ctx);
                        SizeFilterField filter = rec.GetParamByContextAndIndex(ctx, 0);
                        if (own != null && filter != null && !filter.IsReadOnly) filter.Value = own.Value;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                SizeFilterField w = rec.GetParamByContextAndIndex(PartContextType.WallThickness, 0);
                return w == null ? 0 : ToMetres(w, w.Value);
            }
            catch (Exception) { return 0; }
        }

        internal static double Field(PartDataRecord r, PartContextType context)
        {
            try
            {
                PartDataField f = r.GetDataFieldBy(context);
                return f == null ? 0 : ToMetres(f, f.Value);
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Catalog values come in the catalog's units (usually mm); everything here works in metres.</summary>
        internal static double ToMetres(PartDataField f, object value)
        {
            double v;
            try { v = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
            return v * UnitFactor(f, v);
        }

        internal static object FromMetres(PartDataField f, double metres)
        {
            double factor = UnitFactor(f, metres * 1000);
            return Math.Round(metres / factor, 3);
        }

        internal static double UnitFactor(PartDataField f, double sample)
        {
            string u = (f?.Units ?? "").Trim().ToLowerInvariant();
            if (u == "mm" || u.StartsWith("millim")) return 0.001;
            if (u == "cm" || u.StartsWith("centim")) return 0.01;
            if (u == "m" || u.StartsWith("met")) return 1.0;
            if (u == "in" || u.StartsWith("inch")) return 0.0254;
            if (u == "ft" || u.StartsWith("foot") || u.StartsWith("feet")) return 0.3048;
            // No unit given: metric catalogs store millimetres.
            return Math.Abs(sample) > 20 ? 0.001 : 1.0;
        }

        private static string FamilyMaterial(string description) => NavisInterpreter.MaterialKey(description);

        private static string Mm(double m) => Math.Round(m * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
