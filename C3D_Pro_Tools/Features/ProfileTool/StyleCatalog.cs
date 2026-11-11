using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DProTools.ProfileTool
{
    public sealed class StyleEntry
    {
        public ObjectId Id;
        public string Name;
        public string Category;
        public int Depth;
        public override string ToString() => new string(' ', Depth * 3) + Name;
    }

    public sealed class StyleCategory
    {
        public string Path;
        public List<StyleEntry> Styles = new();
        public override string ToString() => $"{Path}  [{Styles.Count}]";
    }

    /// <summary>
    /// Finds every label style collection under CivilDocument.Styles.LabelStyles by reflection (so nothing is
    /// hard-coded), with child styles under their parents.
    /// </summary>
    public static class StyleCatalog
    {
        public static List<StyleCategory> Load(Transaction tr, CivilDocument civil)
        {
            var cats = new List<StyleCategory>();
            var seenCollections = new HashSet<string>();
            var seenStyles = new HashSet<ObjectId>();
            Walk(civil.Styles.LabelStyles, "", 0, tr, cats, seenCollections, seenStyles);
            cats.RemoveAll(c => c.Styles.Count == 0);
            cats.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return cats;
        }

        private static void Walk(object node, string path, int depth, Transaction tr, List<StyleCategory> cats,
            HashSet<string> seenCollections, HashSet<ObjectId> seenStyles)
        {
            if (node == null || depth > 4) return;
            var children = new List<(string name, object value)>();
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.PropertyType.Namespace == null || !p.PropertyType.Namespace.StartsWith("Autodesk.Civil")) continue;
                try { children.Add((p.Name, p.GetValue(node))); } catch { }
            }
            foreach (var m in node.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.Name.StartsWith("Get") || !m.Name.EndsWith("LabelStyles") || m.GetParameters().Length != 0) continue;
                try { children.Add((m.Name.Substring(3), m.Invoke(node, null))); } catch { }
            }

            foreach (var (name, value) in children)
            {
                if (value == null) continue;
                string here = path.Length == 0 ? Pretty(name) : path + " › " + Pretty(name);
                if (value is LabelStyleCollection coll)
                {
                    var ids = coll.Cast<ObjectId>().ToList();
                    string key = string.Join(",", ids.Select(i => i.Handle.Value));
                    if (ids.Count > 0 && !seenCollections.Add(key)) continue; // aliased property (e.g. GradeBread/GradeBreak)
                    var cat = new StyleCategory { Path = here };
                    foreach (var id in ids) AddStyle(tr, id, here, 0, cat, seenStyles);
                    cats.Add(cat);
                }
                else if (value.GetType().Name.Contains("Root") || value.GetType().Name.EndsWith("LabelStyles"))
                {
                    Walk(value, here, depth + 1, tr, cats, seenCollections, seenStyles);
                }
            }
        }

        private static void AddStyle(Transaction tr, ObjectId id, string category, int depth, StyleCategory cat, HashSet<ObjectId> seen)
        {
            if (!seen.Add(id)) return;
            if (tr.GetObject(id, OpenMode.ForRead) is not LabelStyle s) return;
            cat.Styles.Add(new StyleEntry { Id = id, Name = s.Name, Category = category, Depth = depth });
            for (int i = 0; i < s.ChildrenCount; i++)
            {
                ObjectId child;
                try { child = s[i]; } catch { continue; }
                AddStyle(tr, child, category, depth + 1, cat, seen);
            }
        }

        private static string Pretty(string name)
        {
            if (name.EndsWith("LabelStyles") && name.Length > "LabelStyles".Length) name = name.Substring(0, name.Length - "LabelStyles".Length);
            else if (name == "LabelStyles") name = "Labels";
            // CamelCase -> words
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }
    }
}
