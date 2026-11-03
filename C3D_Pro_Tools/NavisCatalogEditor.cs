using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace C3DProTools
{
    /// <summary>A pipe or structure catalog registered in Civil 3D (its .apc index file).</summary>
    internal sealed class CatalogInfo
    {
        public string Guid = "", ApcPath = "", Name = "";
        public string Folder => Path.GetDirectoryName(ApcPath);
        public readonly HashSet<string> PartIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One family Method A creates: a copy of a stock family whose size parameters are free ranges.</summary>
    internal sealed class AnySizeFamily
    {
        public string Key, Guid, PartName, Description, BaseGuid;
        public bool Pipe;
        /// <summary>Size parameters (catalog contexts) turned into ranges.</summary>
        public string[] RangeContexts;
    }

    /// <summary>
    /// Edits Civil 3D part catalogs so imported parts can have their exact size.
    /// Method A adds C3D Pro "any size" families (copies of stock families whose width, height, diameter and wall are free ranges).
    /// Method B adds the missing rows or list values to the stock families themselves.
    /// Every changed file is backed up first. No AutoCAD types are used, so this can be tested on its own.
    /// </summary>
    internal static class NavisCatalogEditor
    {
        public const string ChapterName = "C3D Pro Tools";
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;
        private static readonly HashSet<string> BackedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fixed GUIDs, so the same family has the same identity on every machine and in every drawing.
        public static readonly AnySizeFamily[] AnySize =
        {
            new AnySizeFamily { Key = "box", Guid = "3B6F4C1E-6E2A-4C8B-9B57-6A1D0C3F7E21", PartName = "C3DPro_AnySizeBoxCulvert", Description = "C3D Pro Any-Size Box Culvert",
                BaseGuid = "E0156B07-4726-443F-88E5-F725B7B32298", Pipe = true, RangeContexts = new[] { "PipeInnerWidth", "PipeInnerHeight", "WallThickness" } },
            new AnySizeFamily { Key = "circle", Guid = "8A2D9E43-1F7B-4E65-A0C9-2B4E8D17F6A3", PartName = "C3DPro_AnySizeCircularPipe", Description = "C3D Pro Any-Size Circular Pipe",
                BaseGuid = "C71BCD45-9DB0-468E-8A26-D364D8AF42BD", Pipe = true, RangeContexts = new[] { "PipeInnerDiameter", "WallThickness" } },
            new AnySizeFamily { Key = "rect", Guid = "5E1C7B92-3D48-4A16-8F2E-9C0B6A4D1E58", PartName = "C3DPro_AnySizeRectStructure", Description = "C3D Pro Any-Size Rectangular Structure",
                BaseGuid = "1CF61C61-F401-48C2-B423-7EFEDA40B164", Pipe = false, RangeContexts = new[] { "StructInnerWidth", "StructInnerLength", "WallThickness", "FloorThickness", "StructRimToSumpHeight" } },
            new AnySizeFamily { Key = "cyl", Guid = "C94A2F15-7B3E-4D08-B61A-4E2F9D7C08B6", PartName = "C3DPro_AnySizeCylStructure", Description = "C3D Pro Any-Size Cylindrical Structure",
                BaseGuid = "068A9DC3-FBB1-47B6-AAFE-67AA722BF948", Pipe = false, RangeContexts = new[] { "StructInnerDiameter", "WallThickness", "FloorThickness", "StructRimToSumpHeight" } }
        };

        public static bool IsAnySize(string familyGuid) => AnySize.Any(f => f.Guid.Equals(familyGuid, StringComparison.OrdinalIgnoreCase));

        // =====================================================================================
        // Finding catalogs
        // =====================================================================================

        /// <summary>Catalogs registered for a Civil 3D profile (HKCU\{productKey}\Profiles\{profile}\Preferences\AeccUiNetwork130).</summary>
        public static List<CatalogInfo> RegisteredCatalogs(string productKey, string profile)
        {
            List<CatalogInfo> list = new List<CatalogInfo>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"{productKey}\Profiles\{profile}\Preferences\AeccUiNetwork130"))
            {
                if (key == null) return list;
                foreach (string name in key.GetValueNames())
                {
                    string path = key.GetValue(name) as string;
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".apc", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                    CatalogInfo info = new CatalogInfo { Guid = name, ApcPath = path, Name = Path.GetFileNameWithoutExtension(path) };
                    try
                    {
                        XmlDocument apc = Load(path);
                        foreach (XmlElement part in apc.GetElementsByTagName("Part")) info.PartIds.Add(part.GetAttribute("id"));
                    }
                    catch (Exception) { continue; }
                    list.Add(info);
                }
            }
            return list;
        }

        /// <summary>The catalog the drawing uses for a domain: the registered one whose families match what Civil 3D offers.</summary>
        public static CatalogInfo ActiveCatalog(List<CatalogInfo> catalogs, ICollection<string> availableFamilyGuids)
        {
            if (availableFamilyGuids.Count == 0) return null;
            HashSet<string> wanted = new HashSet<string>(availableFamilyGuids, StringComparer.OrdinalIgnoreCase);
            // Catalogs can share stock GUIDs (Metric and KSA do), so score by how exactly the sets match.
            return catalogs.Select(c => (c, hits: c.PartIds.Count(wanted.Contains), extra: c.PartIds.Count(id => !wanted.Contains(id) && !IsAnySize(id))))
                .Where(x => x.hits > 0).OrderByDescending(x => x.hits).ThenBy(x => x.extra).Select(x => x.c).FirstOrDefault();
        }

        /// <summary>The part table (XML) of a family in a catalog, or null.</summary>
        public static string FamilyXml(CatalogInfo catalog, string familyGuid)
        {
            XmlDocument apc = Load(catalog.ApcPath);
            foreach (XmlElement part in apc.GetElementsByTagName("Part"))
            {
                if (!part.GetAttribute("id").Equals(familyGuid, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (XmlElement url in part.GetElementsByTagName("URL"))
                    if (url.ParentNode?.LocalName == "TableRef")
                    {
                        string href = url.GetAttribute("href", "http://www.w3.org/1999/xlink");
                        string path = Path.Combine(catalog.Folder, href);
                        return File.Exists(path) ? path : null;
                    }
            }
            return null;
        }

        // =====================================================================================
        // Method B: add a size to a stock family
        // =====================================================================================

        internal enum ParamKind { Missing, Table, List, Range, Constant, Calculated }

        /// <summary>The catalog contexts of the family's size table columns (a new row needs a value for each).</summary>
        public static List<string> TableContexts(string xmlPath) =>
            Load(xmlPath).DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Column").Select(e => e.GetAttribute("context")).ToList();

        public static ParamKind KindOf(string xmlPath, string context)
        {
            XmlElement col = Column(Load(xmlPath), context);
            if (col == null) return ParamKind.Missing;
            switch (col.LocalName)
            {
                case "Column": return ParamKind.Table;
                case "ColumnConstList": return ParamKind.List;
                case "ColumnRangeList": return ParamKind.Range;
                case "ColumnCalc": return ParamKind.Calculated;
                default: return ParamKind.Constant;
            }
        }

        /// <summary>
        /// Makes a size available in a stock family: a new table row for table parameters, a new item for list parameters,
        /// a widened range for range parameters. Values are in millimetres, keyed by catalog context. Returns what changed (empty = nothing needed).
        /// </summary>
        public static List<string> AddSize(string xmlPath, IDictionary<string, double> valuesMm, CatalogJournal journal = null)
        {
            // Read, change and write under the folder's lock, so two people adding sizes to a shared catalog never overwrite each other.
            using (CatalogLock.Acquire(Path.GetDirectoryName(xmlPath)))
                return AddSizeLocked(xmlPath, valuesMm, journal);
        }

        private static List<string> AddSizeLocked(string xmlPath, IDictionary<string, double> valuesMm, CatalogJournal journal)
        {
            XmlDocument doc = Load(xmlPath);
            XmlElement root = doc.DocumentElement;
            List<string> changes = new List<string>();

            // Table parameters must change together: one new row holding every table column.
            List<XmlElement> table = root.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Column").ToList();
            List<XmlElement> tableInRequest = table.Where(t => valuesMm.ContainsKey(t.GetAttribute("context"))).ToList();
            if (tableInRequest.Count > 0)
            {
                foreach (XmlElement t in table)
                    if (!valuesMm.ContainsKey(t.GetAttribute("context")))
                        throw new InvalidOperationException($"A new row needs a value for {t.GetAttribute("desc")} ({t.GetAttribute("context")}).");
                if (!RowExists(table, valuesMm))
                {
                    XmlElement unique = root.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "ColumnUnique");
                    int index = 0;
                    while (doc.SelectSingleNode($"//*[@id='r{index}']") != null) index++;
                    string rowId = "r" + index;
                    if (unique != null) Append(unique, "RowUnique", rowId, System.Guid.NewGuid().ToString().ToUpperInvariant());
                    foreach (XmlElement t in table) Append(t, "Row", rowId, Num(valuesMm[t.GetAttribute("context")]));
                    changes.Add("table row " + string.Join(" × ", table.Select(t => $"{t.GetAttribute("desc").Trim()} {Num(valuesMm[t.GetAttribute("context")])}")));
                }
            }

            foreach (KeyValuePair<string, double> kv in valuesMm)
            {
                XmlElement col = Column(doc, kv.Key);
                if (col == null || col.LocalName == "Column") continue;
                if (col.LocalName == "ColumnConstList")
                {
                    List<XmlElement> items = col.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Item").ToList();
                    if (items.Any(i => Math.Abs(Parse(i.InnerText) - kv.Value) < 0.0005)) continue;
                    int index = 0;
                    while (items.Any(i => i.GetAttribute("id") == "i" + index)) index++;
                    Append(col, "Item", "i" + index, Num(kv.Value), "id");
                    changes.Add($"{col.GetAttribute("desc").Trim()} {Num(kv.Value)}");
                }
                else if (col.LocalName == "ColumnRangeList")
                {
                    XmlElement min = RangeItem(col, "Min"), max = RangeItem(col, "Max");
                    if (min != null && kv.Value < Parse(min.InnerText)) { min.InnerText = Num(kv.Value); changes.Add($"{col.GetAttribute("desc").Trim()} range min {Num(kv.Value)}"); }
                    if (max != null && kv.Value > Parse(max.InnerText)) { max.InnerText = Num(kv.Value); changes.Add($"{col.GetAttribute("desc").Trim()} range max {Num(kv.Value)}"); }
                }
                else if (col.LocalName == "ColumnConst" && Math.Abs(Parse(col.InnerText) - kv.Value) >= 0.0005)
                    throw new InvalidOperationException($"{col.GetAttribute("desc").Trim()} is fixed at {col.InnerText.Trim()} mm in this family.");
            }

            if (changes.Count > 0)
            {
                Backup(xmlPath);
                Save(doc, xmlPath, journal);
            }
            return changes;
        }

        /// <summary>
        /// True when the size table already has a row with the same values for every requested column except <paramref name="varying"/>
        /// (for example the same diameter with another wall). Civil 3D always picks the first such row, so a second one could never be used.
        /// </summary>
        public static bool TableKeyConflict(string xmlPath, IDictionary<string, double> valuesMm, string varying)
        {
            XmlDocument doc = Load(xmlPath);
            List<XmlElement> table = doc.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Column").ToList();
            XmlElement vary = table.FirstOrDefault(t => t.GetAttribute("context") == varying);
            if (vary == null || !valuesMm.ContainsKey(varying)) return false;
            List<XmlElement> keys = table.Where(t => t != vary && valuesMm.ContainsKey(t.GetAttribute("context"))).ToList();
            if (keys.Count == 0) return false;
            List<XmlElement> varyRows = vary.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Row").ToList();
            List<List<XmlElement>> keyRows = keys.Select(k => k.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Row").ToList()).ToList();
            int count = Math.Min(varyRows.Count, keyRows.Min(r => r.Count));
            for (int i = 0; i < count; i++)
            {
                bool sameKey = true;
                for (int k = 0; k < keys.Count && sameKey; k++) sameKey = Math.Abs(Parse(keyRows[k][i].InnerText) - valuesMm[keys[k].GetAttribute("context")]) < 0.0005;
                if (sameKey && Math.Abs(Parse(varyRows[i].InnerText) - valuesMm[varying]) >= 0.0005) return true;
            }
            return false;
        }

        private static bool RowExists(List<XmlElement> table, IDictionary<string, double> values)
        {
            List<List<XmlElement>> rows = table.Select(t => t.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Row").ToList()).ToList();
            int count = rows.Min(r => r.Count);
            for (int i = 0; i < count; i++)
            {
                bool all = true;
                for (int c = 0; c < table.Count && all; c++)
                    all = Math.Abs(Parse(rows[c][i].InnerText) - values[table[c].GetAttribute("context")]) < 0.0005;
                if (all) return true;
            }
            return false;
        }

        // =====================================================================================
        // Method A: any-size families
        // =====================================================================================

        /// <summary>
        /// Adds the any-size family to the catalog when it is not there yet: copies the stock base family's model and preview,
        /// writes its table with ranges, and lists it in the catalog index. Returns the table path, or null when the base family
        /// is not in this catalog.
        /// </summary>
        public static string EnsureAnySizeFamily(CatalogInfo catalog, AnySizeFamily family, List<string> log)
        {
            // All or nothing: the family's files and its entry in the catalog index are written together, or put back.
            CatalogJournal journal = new CatalogJournal();
            using (CatalogLock.Acquire(catalog.Folder))
            {
                try { return EnsureAnySizeFamilyLocked(catalog, family, log, journal); }
                catch (Exception)
                {
                    journal.Rollback();
                    throw;
                }
            }
        }

        private static string EnsureAnySizeFamilyLocked(CatalogInfo catalog, AnySizeFamily family, List<string> log, CatalogJournal journal)
        {
            string folder = Path.Combine(catalog.Folder, ChapterName);
            string xmlPath = Path.Combine(folder, family.PartName + ".xml");
            XmlDocument apc = Load(catalog.ApcPath);
            bool listed = apc.GetElementsByTagName("Part").OfType<XmlElement>().Any(p => p.GetAttribute("id").Equals(family.Guid, StringComparison.OrdinalIgnoreCase));
            if (listed && File.Exists(xmlPath)) return xmlPath;

            string baseXml = FamilyXml(catalog, family.BaseGuid);
            if (baseXml == null) { log.Add($"{family.Description}: its base family is not in the {catalog.Name} catalog."); return null; }
            string baseFolder = Path.GetDirectoryName(baseXml), baseName = Path.GetFileNameWithoutExtension(baseXml);
            Directory.CreateDirectory(folder);

            XmlDocument doc = Load(baseXml);
            XmlElement root = doc.DocumentElement;
            // Model and preview image travel with the family under its own name.
            XmlElement view = root.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "ColumnConstView");
            string recipe = view?.GetElementsByTagName("Recipe").OfType<XmlElement>().FirstOrDefault()?.InnerText.Trim() ?? baseName + ".dwg";
            string dwg = Path.Combine(folder, family.PartName + ".dwg"), bmp = Path.Combine(folder, family.PartName + ".bmp");
            journal.Record(dwg);
            File.Copy(Path.Combine(baseFolder, recipe), dwg, true);
            string bmpSource = Path.Combine(baseFolder, baseName + ".bmp");
            if (File.Exists(bmpSource))
            {
                journal.Record(bmp);
                File.Copy(bmpSource, bmp, true);
            }
            if (view != null)
            {
                foreach (XmlElement r in view.GetElementsByTagName("Recipe").OfType<XmlElement>().ToList()) r.InnerText = family.PartName + ".dwg";
                foreach (XmlElement u in view.GetElementsByTagName("URL").OfType<XmlElement>()) u.SetAttribute("href", "http://www.w3.org/1999/xlink", family.PartName + ".bmp");
            }

            SetConst(root, "Catalog_PartName", family.PartName);
            SetConst(root, "Catalog_PartDesc", family.Description);
            SetConst(root, "Catalog_PartID", family.Guid);

            foreach (string context in family.RangeContexts)
            {
                XmlElement col = Column(doc, context);
                if (col == null) continue;
                double def = DefaultOf(col);
                XmlElement range = doc.CreateElement("ColumnRangeList");
                foreach (string attr in new[] { "desc", "dataType", "unit", "name", "id", "visible", "context", "index" })
                    if (col.HasAttribute(attr)) range.SetAttribute(attr, col.GetAttribute(attr));
                if (!range.HasAttribute("dataType")) range.SetAttribute("dataType", "float");
                if (!range.HasAttribute("unit")) range.SetAttribute("unit", "mm");
                double min = context == "WallThickness" || context == "FloorThickness" ? 0 : 1;
                Append(range, "Item", "i0", Num(min), "id").SetAttribute("rangeVal", "Min");
                Append(range, "Item", "i1", Num(200000), "id").SetAttribute("rangeVal", "Max");
                Append(range, "Item", "i2", Num(def), "id").SetAttribute("rangeVal", "Default");
                root.ReplaceChild(range, col);
            }
            // With every size parameter a range, there is no size table left.
            if (!root.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName == "Column"))
                foreach (XmlElement u in root.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "ColumnUnique").ToList()) root.RemoveChild(u);
            Save(doc, xmlPath, journal);

            if (!listed)
            {
                XmlDocument index = Load(catalog.ApcPath);
                XmlElement baseEntry = index.GetElementsByTagName("Part").OfType<XmlElement>().First(p => p.GetAttribute("id").Equals(family.BaseGuid, StringComparison.OrdinalIgnoreCase));
                XmlElement chapter = index.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "Chapter" && e.GetAttribute("name") == ChapterName);
                if (chapter == null)
                {
                    chapter = index.CreateElement("Chapter");
                    chapter.SetAttribute("name", ChapterName);
                    chapter.AppendChild(index.CreateTextNode(ChapterName));
                    XmlElement desc = index.CreateElement("Desc");
                    desc.InnerText = ChapterName;
                    chapter.AppendChild(desc);
                    index.DocumentElement.AppendChild(chapter);
                }
                XmlElement entry = (XmlElement)baseEntry.CloneNode(true);
                entry.SetAttribute("name", family.PartName);
                entry.SetAttribute("id", family.Guid);
                foreach (XmlElement d in entry.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Desc")) d.InnerText = family.Description;
                foreach (XmlElement url in entry.GetElementsByTagName("URL"))
                {
                    bool table = url.ParentNode?.LocalName == "TableRef";
                    url.SetAttribute("href", "http://www.w3.org/1999/xlink", ChapterName + "\\" + family.PartName + (table ? ".xml" : ".bmp"));
                    if (url.HasAttribute("title", "http://www.w3.org/1999/xlink")) url.SetAttribute("title", "http://www.w3.org/1999/xlink", family.Description);
                }
                chapter.AppendChild(entry);
                Backup(catalog.ApcPath);
                Save(index, catalog.ApcPath, journal);
                catalog.PartIds.Add(family.Guid);
            }
            log.Add($"Added \"{family.Description}\" to the {catalog.Name} catalog.");
            return xmlPath;
        }

        // =====================================================================================
        // XML helpers
        // =====================================================================================

        private static XmlDocument Load(string path)
        {
            XmlDocument doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(path);
            return doc;
        }

        /// <summary>
        /// Writes the file whole or not at all: to a temporary file beside it (same volume), read back to prove it is
        /// well-formed XML, then swapped in with <see cref="File.Replace(string, string, string)"/>, so a reader never sees
        /// half a file and a failure leaves the old one. The file's previous content goes to <paramref name="journal"/>.
        /// </summary>
        private static void Save(XmlDocument doc, string path, CatalogJournal journal)
        {
            journal?.Record(path);
            string temp = path + ".c3dpro-tmp";
            try
            {
                doc.Save(temp);
                new XmlDocument().Load(temp);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        /// <summary>Keeps one copy of the file as it was before this session's first change.</summary>
        private static void Backup(string path)
        {
            if (!BackedUp.Add(path)) return;
            File.Copy(path, path + ".c3dpro-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak", true);
        }

        private static XmlElement Column(XmlDocument doc, string context) =>
            doc.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName.StartsWith("Column") && e.GetAttribute("context") == context);

        private static XmlElement RangeItem(XmlElement range, string which) =>
            range.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("rangeVal").Equals(which, StringComparison.OrdinalIgnoreCase));

        private static double DefaultOf(XmlElement col)
        {
            switch (col.LocalName)
            {
                case "ColumnRangeList": return Parse(RangeItem(col, "Default")?.InnerText ?? "0");
                case "ColumnConst": return Parse(col.InnerText);
                default:
                    XmlElement first = col.ChildNodes.OfType<XmlElement>().FirstOrDefault();
                    return first == null ? 0 : Parse(first.InnerText);
            }
        }

        private static void SetConst(XmlElement root, string context, string value)
        {
            XmlElement c = root.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "ColumnConst" && e.GetAttribute("context") == context);
            if (c != null) c.InnerText = value;
        }

        private static XmlElement Append(XmlElement parent, string name, string id, string text, string idAttribute = "id")
        {
            XmlElement e = parent.OwnerDocument.CreateElement(name);
            e.SetAttribute(idAttribute, id);
            e.InnerText = text;
            parent.AppendChild(e);
            return e;
        }

        private static string Num(double mm) => mm.ToString("0.0000", C);

        private static double Parse(string s) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, C, out double v) ? v : 0;
    }

    /// <summary>
    /// One writer at a time per catalog folder, across processes and computers sharing it: a lock file held open with no
    /// sharing. The operating system lets go of it when its process ends, so a crash never leaves a stale lock.
    /// </summary>
    internal static class CatalogLock
    {
        public const string FileName = ".c3dpro.lock";

        /// <summary>Waits (with growing pauses) up to <paramref name="timeoutMs"/> for the folder's lock; dispose the result to let go.</summary>
        public static IDisposable Acquire(string folder, int timeoutMs = 15000)
        {
            string path = Path.Combine(folder, FileName);
            DateTime giveUp = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int pause = 50;
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) when (DateTime.UtcNow < giveUp)
                {
                    System.Threading.Thread.Sleep(pause);
                    pause = Math.Min(pause * 2, 1000);
                }
                catch (IOException ex)
                {
                    throw new IOException($"The catalog folder {folder} is being changed by someone else; try again in a moment.", ex);
                }
            }
        }
    }

    /// <summary>
    /// The catalog files as they were before a change (or that they did not exist), so a change that fails part-way, or an
    /// import that is rolled back, can put them back.
    /// </summary>
    internal sealed class CatalogJournal
    {
        private readonly Dictionary<string, byte[]> _before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => _before.Count == 0;

        /// <summary>Remembers the file's current content (once: the first state is the one put back).</summary>
        public void Record(string path)
        {
            if (!_before.ContainsKey(path)) _before[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>Puts every recorded file back (deletes those that did not exist). Returns the files that could not be restored.</summary>
        public List<string> Rollback()
        {
            List<string> failed = new List<string>();
            foreach (KeyValuePair<string, byte[]> kv in _before)
            {
                try
                {
                    if (kv.Value == null) { if (File.Exists(kv.Key)) File.Delete(kv.Key); }
                    else
                    {
                        string temp = kv.Key + ".c3dpro-tmp";
                        File.WriteAllBytes(temp, kv.Value);
                        if (File.Exists(kv.Key)) File.Replace(temp, kv.Key, null);
                        else File.Move(temp, kv.Key);
                    }
                }
                catch (Exception) { failed.Add(kv.Key); }
            }
            _before.Clear();
            return failed;
        }

        /// <summary>Keeps the changes: forgets what the files were.</summary>
        public void Clear() => _before.Clear();
    }
}
