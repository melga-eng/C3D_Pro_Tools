using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace C3DProTools
{
    internal enum StyleUsage
    {
        /// <summary>Objects or other styles/settings point at it.</summary>
        Used,
        /// <summary>Nothing points at it and Purge agrees it can go.</summary>
        Unused,
        /// <summary>No reference was found, but Purge still refuses it (a default in settings, a reference the scan cannot see).</summary>
        Hidden
    }

    internal enum StandardState { Unknown, Standard, NonStandard }

    /// <summary>One style (Civil 3D style, label style, parts list, AutoCAD text/dim/mleader/table/multiline style).</summary>
    internal sealed class StyleItem
    {
        public ObjectId Id;
        public string Name = "";
        public string Description = "";
        /// <summary>"Civil 3D" or "AutoCAD".</summary>
        public string Group = "";
        /// <summary>Display path below the group, e.g. "Label Styles › Alignment › Station". Child label styles end with their parent's name.</summary>
        public string Category = "";
        /// <summary>Category used for the navigation tree: child label styles sit with their parent instead of one node per parent.</summary>
        public string TreeCategory = "";
        /// <summary>Identifies the sibling set the name must be unique in; also matches the same collection in another drawing.</summary>
        public string CollectionKey = "";
        public string TypeName = "";
        public bool IsCivil;
        public ObjectId ParentId;
        public bool IsChild => !ParentId.IsNull;
        public bool Protected;
        public string ProtectedReason = "";
        public string CreatedBy = "", ModifiedBy = "";
        public DateTime? Modified;

        /// <summary>Entities whose own style is this one (pipes, labels, text...).</summary>
        public readonly List<ObjectId> EntityUsers = new List<ObjectId>();
        /// <summary>Other objects holding a reference: styles, label styles, settings, networks...</summary>
        public readonly List<ObjectId> OtherReferrers = new List<ObjectId>();
        /// <summary>Civil 3D drawing, feature and command settings naming this style as a default (they do not show as references).</summary>
        public int SettingsDefaults;
        public int Uses => EntityUsers.Count + OtherReferrers.Count + SettingsDefaults;
        public bool Purgeable;
        public StyleUsage Usage;

        /// <summary>Set when the name looks like a copy ("X (1)", "Copy of X") of another style in the same collection.</summary>
        public StyleItem DuplicateOf;
        public StandardState Standard;

        public string FullCategory => Group + " › " + Category;
        public string TreePath => Group + " › " + TreeCategory;
        public string Key => CollectionKey + "|" + Name.ToUpperInvariant();
    }

    /// <summary>Reads every style a drawing holds. Civil 3D collections are found by walking the styles tree with reflection,
    /// so new collections in later releases are picked up without code changes.</summary>
    internal static class StyleCatalog
    {
        public const string CivilGroup = "Civil 3D";
        public const string AcadGroup = "AutoCAD";
        private const int MaxDepth = 5;

        public static List<StyleItem> Build(Database db, Transaction tr)
        {
            List<StyleItem> items = new List<StyleItem>();
            HashSet<ObjectId> seen = new HashSet<ObjectId>();

            CivilDocument cdoc = null;
            try { cdoc = CivilDocument.GetCivilDocument(db); } catch (Exception) { }
            if (cdoc != null)
            {
                StylesRoot root = cdoc.Styles;
                Walk(root, "", 0, items, seen, tr);

                // Pressure collections come from extension methods, not properties.
                AddCivilCollection(Safe(() => root.GetPressurePipeStyles()), "Pressure Pipe", items, seen, tr);
                AddCivilCollection(Safe(() => root.GetPressureFittingStyles()), "Pressure Fitting", items, seen, tr);
                AddCivilCollection(Safe(() => root.GetPressureAppurtenanceStyles()), "Pressure Appurtenance", items, seen, tr);
                AddCivilCollection(Safe(() => root.GetPressurePartLists()), "Pressure Parts Lists", items, seen, tr);
            }

            AddAcad(items, tr, db);
            return items;
        }

        private static IEnumerable Safe(Func<IEnumerable> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }

        private static void Walk(object node, string path, int depth, List<StyleItem> items, HashSet<ObjectId> seen, Transaction tr)
        {
            if (node == null || depth > MaxDepth) return;
            foreach (PropertyInfo p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                Type t = p.PropertyType;
                if (t.IsValueType || t == typeof(string) || t.Namespace == null || !t.Namespace.StartsWith("Autodesk.Civil")) continue;
                if (p.Name.IndexOf("Expression", StringComparison.Ordinal) >= 0 || p.Name.IndexOf("Setting", StringComparison.Ordinal) >= 0) continue;

                object value;
                try { value = p.GetValue(node); } catch (Exception) { continue; }
                if (value == null) continue;

                string sub = path.Length == 0 ? Pretty(p.Name, value is IEnumerable) : path + " › " + Pretty(p.Name, true);
                if (value is IEnumerable collection)
                {
                    // A style collection yields ObjectIds; anything else enumerable (expressions...) is not a style list.
                    if (!AddCivilCollection(collection, sub, items, seen, tr)) continue;
                }
                else
                {
                    Walk(value, sub, depth + 1, items, seen, tr);
                }
            }
        }

        /// <summary>Adds the styles of one collection. Returns false when it did not hold ObjectIds.</summary>
        private static bool AddCivilCollection(IEnumerable collection, string category, List<StyleItem> items, HashSet<ObjectId> seen, Transaction tr)
        {
            if (collection == null) return false;
            bool any = false;
            try
            {
                foreach (object o in collection)
                {
                    if (!(o is ObjectId id)) return any;
                    any = true;
                    AddCivilStyle(id, category, category, category, ObjectId.Null, items, seen, tr);
                }
            }
            catch (Exception) { }
            return true;
        }

        private static void AddCivilStyle(ObjectId id, string category, string treeCategory, string key, ObjectId parent, List<StyleItem> items, HashSet<ObjectId> seen, Transaction tr)
        {
            if (id.IsNull || !id.IsValid || id.IsErased || !seen.Add(id)) return;
            DBObject obj;
            try { obj = tr.GetObject(id, OpenMode.ForRead); } catch (Exception) { return; }
            if (!(obj is StyleBase style)) return;

            StyleItem item = new StyleItem
            {
                Id = id,
                Name = style.Name ?? "",
                Description = Try(() => style.Description) ?? "",
                Group = CivilGroup,
                Category = category,
                TreeCategory = treeCategory,
                CollectionKey = CivilGroup + "/" + key,
                TypeName = obj.GetType().Name,
                IsCivil = true,
                ParentId = parent,
                CreatedBy = Try(() => style.CreateBy) ?? "",
                ModifiedBy = Try(() => style.ModifiedBy) ?? ""
            };
            // Civil 3D returns the date as text.
            try { if (DateTime.TryParse(style.DateModified, out DateTime modified)) item.Modified = modified; } catch (Exception) { }
            items.Add(item);

            // Label styles can have child styles, named uniquely under their parent.
            if (style is LabelStyle label)
            {
                int count = 0;
                try { count = label.ChildrenCount; } catch (Exception) { }
                for (int i = 0; i < count; i++)
                {
                    ObjectId childId;
                    try { childId = label[i]; } catch (Exception) { continue; }
                    AddCivilStyle(childId, category + " › " + item.Name, treeCategory, key + "/" + item.Name.ToUpperInvariant(), id, items, seen, tr);
                }
            }
        }

        private static void AddAcad(List<StyleItem> items, Transaction tr, Database db)
        {
            TextStyleTable texts = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in texts)
            {
                TextStyleTableRecord r = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (r.IsShapeFile || string.IsNullOrEmpty(r.Name)) continue;
                AddAcadItem(items, r, r.Name, "Text Styles", ProtectReason(r.Name, id == db.Textstyle, "Standard"));
            }

            DimStyleTable dims = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in dims)
            {
                DimStyleTableRecord r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                // "Name$0" records are sub-styles that belong to their parent dimension style.
                if (string.IsNullOrEmpty(r.Name) || r.Name.Contains("$")) continue;
                AddAcadItem(items, r, r.Name, "Dimension Styles", ProtectReason(r.Name, id == db.Dimstyle, "Standard"));
            }

            AddDictionary(items, tr, db.MLeaderStyleDictionaryId, "Multileader Styles", db.MLeaderstyle, "Standard");
            AddDictionary(items, tr, db.TableStyleDictionaryId, "Table Styles", db.Tablestyle, "Standard");
            AddDictionary(items, tr, db.MLStyleDictionaryId, "Multiline Styles", db.CmlstyleID, "Standard");
        }

        private static void AddDictionary(List<StyleItem> items, Transaction tr, ObjectId dictId, string category, ObjectId current, string standardName)
        {
            if (dictId.IsNull) return;
            DBDictionary dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (string.IsNullOrEmpty(e.Key) || e.Key.StartsWith("*")) continue;
                DBObject obj;
                try { obj = tr.GetObject(e.Value, OpenMode.ForRead); } catch (Exception) { continue; }
                AddAcadItem(items, obj, e.Key, category, ProtectReason(e.Key, e.Value == current, standardName));
            }
        }

        private static string ProtectReason(string name, bool isCurrent, string standardName)
        {
            if (isCurrent) return "Current style of the drawing";
            if (string.Equals(name, standardName, StringComparison.OrdinalIgnoreCase)) return "AutoCAD default style";
            return "";
        }

        private static void AddAcadItem(List<StyleItem> items, DBObject obj, string name, string category, string protectReason)
        {
            items.Add(new StyleItem
            {
                Id = obj.ObjectId,
                Name = name,
                Description = ReadString(obj, "Description") ?? "",
                Group = AcadGroup,
                Category = category,
                TreeCategory = category,
                CollectionKey = AcadGroup + "/" + category,
                TypeName = obj.GetType().Name,
                Protected = protectReason.Length > 0,
                ProtectedReason = protectReason
            });
        }

        /// <summary>"AlignmentLabelStyles" → "Alignment". Top-level roots keep their full name ("Label Styles").</summary>
        private static string Pretty(string propertyName, bool trimSuffix)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < propertyName.Length; i++)
            {
                char c = propertyName[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(propertyName[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            string s = sb.ToString();
            if (!trimSuffix) return s;
            foreach (string suffix in new[] { " Label Styles", " Styles", " Style" })
                if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length) return s.Substring(0, s.Length - suffix.Length);
            return s;
        }

        private static T Try<T>(Func<T> get) where T : class
        {
            try { return get(); } catch (Exception) { return null; }
        }

        // --- Name / description access shared by every style kind ---

        internal static string ReadString(object obj, string property)
        {
            try { return obj.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as string; }
            catch (Exception) { return null; }
        }

        /// <summary>Renames a style of any kind. The object must be open for write.</summary>
        internal static void SetName(Transaction tr, DBObject obj, string newName)
        {
            if (obj is StyleBase civil) { civil.Name = newName; return; }
            if (obj is SymbolTableRecord record)
            {
                SymbolUtilityServices.ValidateSymbolName(newName, false);
                record.Name = newName;
                return;
            }

            // Dictionary-owned styles (multileader, table, multiline): the dictionary key is the name AutoCAD shows.
            DBDictionary owner = tr.GetObject(obj.OwnerId, OpenMode.ForRead) as DBDictionary;
            if (owner == null) throw new InvalidOperationException("This style cannot be renamed.");
            SymbolUtilityServices.ValidateSymbolName(newName, false);
            string oldKey = owner.NameAt(obj.ObjectId);
            if (owner.Contains(newName) && !string.Equals(oldKey, newName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{newName}' already exists.");
            owner.UpgradeOpen();
            owner.SetName(oldKey, newName);
            PropertyInfo nameProp = obj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null && nameProp.CanWrite)
            {
                try { nameProp.SetValue(obj, newName); } catch (Exception) { /* key already renamed; the inner name is cosmetic */ }
            }
        }

        /// <summary>Sets a style's description. Returns false for kinds without one (text and dimension styles).</summary>
        internal static bool SetDescription(DBObject obj, string description)
        {
            if (obj is StyleBase civil) { civil.Description = description; return true; }
            PropertyInfo p = obj.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite || p.PropertyType != typeof(string)) return false;
            p.SetValue(obj, description);
            return true;
        }
    }
}
