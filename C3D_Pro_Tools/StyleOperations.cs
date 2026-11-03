using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    internal enum ConflictMode { Skip, Overwrite, Rename }

    /// <summary>Counts and messages from one operation.</summary>
    internal sealed class OpResult
    {
        public readonly string Title;
        public int Changed, Skipped, Failed;
        public readonly List<string> Log = new List<string>();
        public OpResult(string title) { Title = title; }

        public string Summary(string verb) =>
            $"{Title}: {Changed} {verb}" + (Skipped > 0 ? $", {Skipped} skipped" : "") + (Failed > 0 ? $", {Failed} failed" : "") + ".";

        public void Add(OpResult other)
        {
            Changed += other.Changed; Skipped += other.Skipped; Failed += other.Failed;
            Log.AddRange(other.Log);
        }
    }

    /// <summary>The styles of one drawing with their scanned usage. Reload after any change to the drawing.</summary>
    internal sealed class StyleSession
    {
        public readonly Database Db;
        public List<StyleItem> Items = new List<StyleItem>();
        public ScanResult Scan = new ScanResult();
        public StyleTemplate Template;
        public List<StyleItem> Missing = new List<StyleItem>();

        public StyleSession(Database db) { Db = db; }

        public void Reload(Action<double> progress = null)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                Items = StyleCatalog.Build(Db, tr);
                Scan = StyleReferenceScanner.Scan(Db, tr, Items, progress);
                tr.Commit();
            }
            StyleOperations.FindDuplicates(Items);
            Missing = Template != null ? StyleOperations.CompareStandards(Items, Template.Items) : new List<StyleItem>();
        }

        public StyleItem Find(ObjectId id) => Items.FirstOrDefault(i => i.Id == id);

        public bool PurgeTrusted(StyleItem item) => item.IsCivil ? Scan.CivilPurgeTrusted : Scan.AcadPurgeTrusted;
    }

    /// <summary>A template (DWT/DWG) read in the background as the company standard.</summary>
    internal sealed class StyleTemplate : IDisposable
    {
        public readonly string Path;
        public readonly Database Db;
        public readonly List<StyleItem> Items;

        public StyleTemplate(string path)
        {
            Path = path;
            Db = new Database(false, true);
            try
            {
                Db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                Db.CloseInput(true);
                using (Transaction tr = Db.TransactionManager.StartTransaction())
                {
                    Items = StyleCatalog.Build(Db, tr);
                    tr.Commit();
                }
            }
            catch
            {
                Db.Dispose();
                throw;
            }
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>Find/replace, prefix, suffix and case rules applied to names or descriptions.</summary>
    internal sealed class RenameRule
    {
        public bool TargetDescription { get; set; }
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
        public bool MatchCase { get; set; }
        public bool UseRegex { get; set; }
        public string AddPrefix { get; set; } = "";
        public string RemovePrefix { get; set; } = "";
        public string AddSuffix { get; set; } = "";
        public string RemoveSuffix { get; set; } = "";
        /// <summary>0 = keep, 1 = UPPER, 2 = lower, 3 = Title Case.</summary>
        public int CaseMode { get; set; }

        public bool IsEmpty => string.IsNullOrEmpty(Find) && string.IsNullOrEmpty(AddPrefix) && string.IsNullOrEmpty(RemovePrefix)
                               && string.IsNullOrEmpty(AddSuffix) && string.IsNullOrEmpty(RemoveSuffix) && CaseMode == 0;

        /// <summary>Returns the new text, or null with an error when the find pattern is not a valid expression.</summary>
        public string Apply(string text, out string error)
        {
            error = null;
            string s = text ?? "";
            StringComparison cmp = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!string.IsNullOrEmpty(Find))
            {
                if (UseRegex)
                {
                    try { s = Regex.Replace(s, Find, Replace ?? "", MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase); }
                    catch (ArgumentException ex) { error = "Invalid pattern: " + ex.Message; return null; }
                }
                else s = ReplaceAll(s, Find, Replace ?? "", cmp);
            }
            if (!string.IsNullOrEmpty(RemovePrefix) && s.StartsWith(RemovePrefix, cmp)) s = s.Substring(RemovePrefix.Length);
            if (!string.IsNullOrEmpty(RemoveSuffix) && s.EndsWith(RemoveSuffix, cmp)) s = s.Substring(0, s.Length - RemoveSuffix.Length);
            // Names that already carry the prefix/suffix are left alone, so running the rule twice changes nothing.
            if (!string.IsNullOrEmpty(AddPrefix) && !s.StartsWith(AddPrefix, cmp)) s = AddPrefix + s;
            if (!string.IsNullOrEmpty(AddSuffix) && !s.EndsWith(AddSuffix, cmp)) s += AddSuffix;
            switch (CaseMode)
            {
                case 1: s = s.ToUpperInvariant(); break;
                case 2: s = s.ToLowerInvariant(); break;
                case 3: s = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()); break;
            }
            return s;
        }

        private static string ReplaceAll(string source, string find, string replace, StringComparison cmp)
        {
            StringBuilder sb = new StringBuilder();
            int start = 0, index;
            while ((index = source.IndexOf(find, start, cmp)) >= 0)
            {
                sb.Append(source, start, index - start).Append(replace);
                start = index + find.Length;
            }
            return sb.Append(source, start, source.Length - start).ToString();
        }

        public RenameRule Clone() => (RenameRule)MemberwiseClone();
    }

    internal sealed class PlannedRename
    {
        public StyleItem Item;
        public string OldValue, NewValue;
        /// <summary>Why it cannot be applied (duplicate, invalid...), or empty.</summary>
        public string Problem = "";
    }

    /// <summary>One "from → to" line of a saved style mapping.</summary>
    internal sealed class StyleMapEntry
    {
        public string Collection { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    internal static partial class StyleOperations
    {
        // ---------------- Rename ----------------

        public static List<PlannedRename> PlanRenames(List<StyleItem> all, IEnumerable<StyleItem> targets, RenameRule rule)
        {
            List<PlannedRename> plans = new List<PlannedRename>();
            foreach (StyleItem item in targets)
            {
                string old = rule.TargetDescription ? item.Description : item.Name;
                string updated = rule.Apply(old, out string error);
                if (error != null) { plans.Add(new PlannedRename { Item = item, OldValue = old, NewValue = old, Problem = error }); continue; }
                if (updated == old) continue;
                plans.Add(new PlannedRename { Item = item, OldValue = old, NewValue = updated });
            }
            Validate(all, plans, rule.TargetDescription);
            return plans;
        }

        public static PlannedRename PlanSingle(List<StyleItem> all, StyleItem item, string newName)
        {
            PlannedRename plan = new PlannedRename { Item = item, OldValue = item.Name, NewValue = newName };
            Validate(all, new List<PlannedRename> { plan }, false);
            return plan;
        }

        private static void Validate(List<StyleItem> all, List<PlannedRename> plans, bool description)
        {
            if (description)
            {
                foreach (PlannedRename p in plans)
                    if (p.Problem.Length == 0 && !p.Item.IsCivil && (p.Item.Category == "Text Styles" || p.Item.Category == "Dimension Styles"))
                        p.Problem = "This style kind has no description";
                return;
            }

            Dictionary<ObjectId, string> final = plans.Where(p => p.Problem.Length == 0).ToDictionary(p => p.Item.Id, p => p.NewValue);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (StyleItem item in all)
            {
                string name = final.TryGetValue(item.Id, out string n) ? n : item.Name;
                string key = item.CollectionKey + "|" + name;
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            foreach (PlannedRename p in plans)
            {
                if (p.Problem.Length > 0) continue;
                if (string.IsNullOrWhiteSpace(p.NewValue)) { p.Problem = "Empty name"; continue; }
                if (!p.Item.IsCivil)
                {
                    try { SymbolUtilityServices.ValidateSymbolName(p.NewValue, false); }
                    catch (Exception) { p.Problem = "Invalid characters for an AutoCAD name"; continue; }
                }
                if (counts[p.Item.CollectionKey + "|" + p.NewValue] > 1) p.Problem = "Name already used in this collection";
            }
        }

        public static OpResult ApplyRenames(Database db, List<PlannedRename> plans, bool description)
        {
            OpResult result = new OpResult(description ? "Description change" : "Rename");
            List<PlannedRename> valid = plans.Where(p => p.Problem.Length == 0).ToList();
            result.Skipped = plans.Count - valid.Count;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (description)
                {
                    foreach (PlannedRename p in valid)
                    {
                        try
                        {
                            if (StyleCatalog.SetDescription(tr.GetObject(p.Item.Id, OpenMode.ForWrite), p.NewValue)) result.Changed++;
                            else { result.Skipped++; result.Log.Add($"{p.OldValue}: no description for this style kind"); }
                        }
                        catch (Exception ex) { result.Failed++; result.Log.Add($"{p.Item.Name}: {ex.Message}"); }
                    }
                }
                else
                {
                    // Two passes through temporary names, so swaps and chains (A→B, B→C) never collide.
                    List<PlannedRename> parked = new List<PlannedRename>();
                    foreach (PlannedRename p in valid)
                    {
                        try
                        {
                            StyleCatalog.SetName(tr, tr.GetObject(p.Item.Id, OpenMode.ForWrite), "zzTmp_" + p.Item.Id.Handle);
                            parked.Add(p);
                        }
                        catch (Exception ex) { result.Failed++; result.Log.Add($"{p.OldValue}: {ex.Message}"); }
                    }
                    foreach (PlannedRename p in parked)
                    {
                        DBObject obj = tr.GetObject(p.Item.Id, OpenMode.ForWrite);
                        try { StyleCatalog.SetName(tr, obj, p.NewValue); result.Changed++; }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            result.Log.Add($"{p.OldValue} → {p.NewValue}: {ex.Message}");
                            try { StyleCatalog.SetName(tr, obj, p.OldValue); } catch (Exception) { }
                        }
                    }
                }
                tr.Commit();
            }
            return result;
        }

        // ---------------- Delete / purge ----------------

        /// <summary>
        /// Erases the given styles, but only those Purge confirms nothing references (or, where Purge does not understand
        /// Civil 3D styles, those the reference scan found unused). Protected and used styles are always kept.
        /// </summary>
        public static OpResult Delete(StyleSession session, IEnumerable<StyleItem> candidates)
        {
            OpResult result = new OpResult("Delete");
            List<StyleItem> list = candidates.Where(i => !i.Id.IsErased).ToList();
            ObjectIdCollection ids = new ObjectIdCollection();
            foreach (StyleItem i in list) if (!i.Protected) ids.Add(i.Id);
            try { session.Db.Purge(ids); } catch (Exception) { ids.Clear(); }
            HashSet<ObjectId> purgeable = new HashSet<ObjectId>(ids.Cast<ObjectId>());
            HashSet<ObjectId> chosen = new HashSet<ObjectId>(list.Select(i => i.Id));

            using (Transaction tr = session.Db.TransactionManager.StartTransaction())
            {
                foreach (StyleItem item in list)
                {
                    if (item.Protected) { result.Skipped++; result.Log.Add($"{item.Name}: kept ({item.ProtectedReason})"); continue; }
                    if (item.Uses > 0 || (!purgeable.Contains(item.Id) && session.PurgeTrusted(item)))
                    {
                        result.Skipped++;
                        result.Log.Add($"{item.Name}: kept, still used ({Describe(item)})");
                        continue;
                    }
                    // Where Purge does not understand Civil 3D styles, the reference scan may miss a reference it cannot
                    // see: such styles are reported, never deleted.
                    if (!purgeable.Contains(item.Id))
                    {
                        result.Skipped++;
                        result.Log.Add($"{item.Name}: kept, nothing references it that the scan can see, but AutoCAD Purge does not confirm it is free (report only)");
                        continue;
                    }
                    // Erasing a parent label style takes its children with it.
                    if (item.IsChild && chosen.Contains(item.ParentId)) { result.Changed++; continue; }
                    try
                    {
                        tr.GetObject(item.Id, OpenMode.ForWrite).Erase(true);
                        result.Changed++;
                    }
                    catch (Exception ex) { result.Failed++; result.Log.Add($"{item.Name}: {ex.Message}"); }
                }
                tr.Commit();
            }
            return result;
        }

        public static string Describe(StyleItem item)
        {
            if (item.Uses == 0) return item.Usage == StyleUsage.Hidden ? "hidden reference, e.g. a default in settings" : "not used";
            List<string> parts = new List<string>();
            if (item.EntityUsers.Count > 0) parts.Add($"{item.EntityUsers.Count} object(s)");
            if (item.OtherReferrers.Count > 0) parts.Add($"{item.OtherReferrers.Count} style/setting reference(s)");
            if (item.SettingsDefaults > 0) parts.Add($"default in {item.SettingsDefaults} Civil 3D setting(s)");
            return string.Join(", ", parts);
        }

        // ---------------- Merge ----------------

        /// <summary>
        /// Moves every user of each source style to its target, then deletes the sources that are free afterwards.
        /// Moving is all-or-nothing; a source that something still points at (a setting the tool cannot change) is kept and reported.
        /// </summary>
        public static OpResult Merge(StyleSession session, List<KeyValuePair<StyleItem, StyleItem>> pairs)
        {
            OpResult result = new OpResult("Merge");
            int moved = 0;
            using (Transaction tr = session.Db.TransactionManager.StartTransaction())
            {
                try
                {
                    foreach (KeyValuePair<StyleItem, StyleItem> pair in pairs)
                    {
                        StyleItem src = pair.Key, dst = pair.Value;
                        foreach (ObjectId entId in src.EntityUsers)
                        {
                            DBObject obj = tr.GetObject(entId, OpenMode.ForWrite, false, true);
                            if (StyleEntityHelper.TrySetStyleId(obj, dst.Id)) moved++;
                        }
                        foreach (ObjectId refId in src.OtherReferrers)
                        {
                            DBObject obj = tr.GetObject(refId, OpenMode.ForWrite, false, true);
                            if (Retarget(obj, src.Id, dst.Id)) moved++;
                        }
                    }
                    tr.Commit();
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    result.Failed = pairs.Count;
                    result.Log.Add("Merge was rolled back, nothing changed: " + ex.Message);
                    return result;
                }
            }
            result.Log.Add($"{moved} object(s) and style reference(s) moved to their new styles.");

            // Rescan so the delete step sees the references as they are now.
            session.Reload();
            List<StyleItem> sources = pairs.Select(p => session.Find(p.Key.Id)).Where(i => i != null).ToList();
            OpResult deleted = Delete(session, sources);
            result.Changed = deleted.Changed;
            result.Skipped = deleted.Skipped;
            result.Failed += deleted.Failed;
            result.Log.AddRange(deleted.Log);
            return result;
        }

        /// <summary>Points a style-holding object (dimension style, multileader style, Civil 3D object or style) at a new style.</summary>
        private static bool Retarget(DBObject obj, ObjectId from, ObjectId to)
        {
            bool changed = false;
            if (obj is DimStyleTableRecord dim && dim.Dimtxsty == from) { dim.Dimtxsty = to; changed = true; }
            if (obj is MLeaderStyle ml && ml.TextStyleId == from) { ml.TextStyleId = to; changed = true; }
            if (obj is Entity && StyleEntityHelper.GetStyleId(obj) == from && StyleEntityHelper.TrySetStyleId(obj, to)) changed = true;

            foreach (PropertyInfo p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(ObjectId) || !p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.Name.IndexOf("Style", StringComparison.Ordinal) < 0 && p.Name.IndexOf("PartsList", StringComparison.Ordinal) < 0
                    && p.Name.IndexOf("Rule", StringComparison.Ordinal) < 0) continue;
                try
                {
                    if ((ObjectId)p.GetValue(obj) != from) continue;
                    p.SetValue(obj, to);
                    changed = true;
                }
                catch (Exception) { }
            }
            return changed;
        }

        /// <summary>Styles that can be merged into each other: same collection and same kind, and not child label styles.</summary>
        public static bool CanMerge(StyleItem a, StyleItem b) =>
            a != null && b != null && a.Id != b.Id && !a.IsChild && !b.IsChild && a.CollectionKey == b.CollectionKey && a.TypeName == b.TypeName;

        // ---------------- Duplicates ----------------

        private static readonly Regex[] DuplicatePatterns =
        {
            new Regex(@"^(?<b>.+?)\s*\(\d+\)$"),
            new Regex(@"^Copy(?:\s*\(\d+\))?\s+of\s+(?<b>.+)$", RegexOptions.IgnoreCase),
            new Regex(@"^(?<b>.+?)\s*-\s*Copy(?:\s*\(\d+\))?$", RegexOptions.IgnoreCase),
            new Regex(@"^(?<b>.+?)[\s_]+\d{1,2}$")
        };

        /// <summary>Marks styles whose name is a copy of another style in the same collection ("X (1)", "Copy of X", "X - Copy", "X_2").</summary>
        public static void FindDuplicates(List<StyleItem> items)
        {
            Dictionary<string, StyleItem> byKey = new Dictionary<string, StyleItem>();
            foreach (StyleItem i in items) byKey[i.Key] = i;
            foreach (StyleItem item in items)
            {
                item.DuplicateOf = null;
                if (item.IsChild) continue;
                foreach (Regex rx in DuplicatePatterns)
                {
                    Match m = rx.Match(item.Name);
                    if (!m.Success) continue;
                    string baseKey = item.CollectionKey + "|" + m.Groups["b"].Value.Trim().ToUpperInvariant();
                    if (byKey.TryGetValue(baseKey, out StyleItem original) && CanMerge(item, original))
                    {
                        // Follow chains ("X (1) (1)") back to the first original.
                        while (original.DuplicateOf != null) original = original.DuplicateOf;
                        item.DuplicateOf = original;
                        break;
                    }
                }
            }
        }

        // ---------------- Standards ----------------

        /// <summary>Marks each drawing style as in or outside the template, and returns the template styles the drawing lacks.</summary>
        public static List<StyleItem> CompareStandards(List<StyleItem> drawing, List<StyleItem> template)
        {
            HashSet<string> templateKeys = new HashSet<string>(template.Select(t => t.Key));
            HashSet<string> drawingKeys = new HashSet<string>(drawing.Select(d => d.Key));
            foreach (StyleItem item in drawing) item.Standard = templateKeys.Contains(item.Key) ? StandardState.Standard : StandardState.NonStandard;
            return template.Where(t => !t.IsChild && !drawingKeys.Contains(t.Key)).ToList();
        }

        /// <summary>Best guess at the template style a non-standard style should become, by comparing simplified names.</summary>
        public static StyleItem SuggestTarget(StyleItem source, IEnumerable<StyleItem> candidates)
        {
            string s = Normalize(source.DuplicateOf?.Name ?? source.Name);
            StyleItem best = null;
            int bestScore = 0;
            foreach (StyleItem c in candidates)
            {
                if (c.CollectionKey != source.CollectionKey || c.IsChild) continue;
                string n = Normalize(c.Name);
                if (n.Length == 0) continue;
                int score = n == s ? 1000 : (s.EndsWith(n) || s.StartsWith(n) || n.EndsWith(s)) ? Math.Min(n.Length, s.Length) : 0;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return bestScore >= 4 ? best : null;
        }

        private static string Normalize(string name)
        {
            foreach (Regex rx in DuplicatePatterns)
            {
                Match m = rx.Match(name);
                if (m.Success) { name = m.Groups["b"].Value; break; }
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static List<StyleMapEntry> LoadMapping(string path) =>
            JsonSerializer.Deserialize<List<StyleMapEntry>>(File.ReadAllText(path)) ?? new List<StyleMapEntry>();

        public static void SaveMapping(string path, List<StyleMapEntry> entries) =>
            File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>Turns saved "from → to" lines into merge pairs for this drawing. Lines whose styles are absent are ignored.</summary>
        public static List<KeyValuePair<StyleItem, StyleItem>> ResolveMapping(List<StyleItem> items, List<StyleMapEntry> entries)
        {
            Dictionary<string, StyleItem> byKey = items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
            List<KeyValuePair<StyleItem, StyleItem>> pairs = new List<KeyValuePair<StyleItem, StyleItem>>();
            foreach (StyleMapEntry e in entries)
            {
                if (!byKey.TryGetValue(e.Collection + "|" + e.From.ToUpperInvariant(), out StyleItem src)) continue;
                if (!byKey.TryGetValue(e.Collection + "|" + e.To.ToUpperInvariant(), out StyleItem dst)) continue;
                if (CanMerge(src, dst)) pairs.Add(new KeyValuePair<StyleItem, StyleItem>(src, dst));
            }
            return pairs;
        }

        // ---------------- Import / transfer ----------------

        /// <summary>Copies styles from one database into another. Child label styles travel with their parent.</summary>
        public static OpResult Import(Database source, Database dest, IEnumerable<StyleItem> styles, ConflictMode mode)
        {
            OpResult result = new OpResult("Import");
            StyleConflictResolverType resolver = mode == ConflictMode.Overwrite ? StyleConflictResolverType.Override
                : mode == ConflictMode.Rename ? StyleConflictResolverType.Rename : StyleConflictResolverType.Ignore;
            DuplicateRecordCloning cloning = mode == ConflictMode.Overwrite ? DuplicateRecordCloning.Replace
                : mode == ConflictMode.Rename ? DuplicateRecordCloning.MangleName : DuplicateRecordCloning.Ignore;

            Dictionary<string, ObjectIdCollection> acad = new Dictionary<string, ObjectIdCollection>();
            foreach (StyleItem item in styles)
            {
                if (item.IsChild) { result.Skipped++; continue; }
                if (item.IsCivil)
                {
                    try
                    {
                        StyleBase.ExportTo(new ObjectIdCollection { item.Id }, dest, resolver);
                        result.Changed++;
                    }
                    catch (Exception ex) { result.Failed++; result.Log.Add($"{item.Name}: {ex.Message}"); }
                }
                else
                {
                    if (!acad.TryGetValue(item.Category, out ObjectIdCollection ids)) acad[item.Category] = ids = new ObjectIdCollection();
                    ids.Add(item.Id);
                }
            }

            foreach (KeyValuePair<string, ObjectIdCollection> group in acad)
            {
                ObjectId owner = AcadOwner(dest, group.Key);
                if (owner.IsNull) { result.Skipped += group.Value.Count; continue; }
                try
                {
                    source.WblockCloneObjects(group.Value, owner, new IdMapping(), cloning, false);
                    result.Changed += group.Value.Count;
                }
                catch (Exception ex) { result.Failed += group.Value.Count; result.Log.Add($"{group.Key}: {ex.Message}"); }
            }
            return result;
        }

        private static ObjectId AcadOwner(Database db, string category)
        {
            switch (category)
            {
                case "Text Styles": return db.TextStyleTableId;
                case "Dimension Styles": return db.DimStyleTableId;
                case "Multileader Styles": return db.MLeaderStyleDictionaryId;
                case "Table Styles": return db.TableStyleDictionaryId;
                case "Multiline Styles": return db.MLStyleDictionaryId;
                default: return ObjectId.Null;
            }
        }

        // ---------------- Report ----------------

        public static void ExportCsv(string path, IEnumerable<StyleItem> items)
        {
            using (StreamWriter sw = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("Group,Category,Style Name,Status,Objects,Other References,Duplicate Of,Standard,Protected,Description,Type,Created By,Modified By,Modified");
                foreach (StyleItem i in items)
                {
                    sw.WriteLine(string.Join(",", new[]
                    {
                        Csv(i.Group), Csv(i.Category), Csv(i.Name), StatusText(i), i.EntityUsers.Count.ToString(), i.OtherReferrers.Count.ToString(),
                        Csv(i.DuplicateOf?.Name), i.Standard == StandardState.Unknown ? "" : i.Standard.ToString(), Csv(i.ProtectedReason),
                        Csv(i.Description), i.TypeName, Csv(i.CreatedBy), Csv(i.ModifiedBy), i.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""
                    }));
                }
            }
        }

        public static string StatusText(StyleItem i) =>
            i.Protected ? "Protected" : i.Usage == StyleUsage.Used ? "Used" : i.Usage == StyleUsage.Unused ? "Unused" : "Hidden reference";

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
