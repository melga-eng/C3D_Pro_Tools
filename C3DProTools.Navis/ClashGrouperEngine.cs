using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace C3DProTools.Navis
{
    /// <summary>What one grouping run did to one test.</summary>
    public sealed class TestGrouping
    {
        public string Test = "";
        public int Clashes, Groups, Grouped, Single, Skipped;
        public List<string> GroupNames = new List<string>();
        public string Error;
    }

    /// <summary>
    /// Clash Grouper in Navisworks: reads a clash test's results, finds the main side's elements (item 1 by default) and the
    /// main-side pieces near them, groups the clashes (ClashGrouping), and writes the test back with the new groups (its old
    /// groups discarded). The test before the change is kept in memory so the last grouping can be undone.
    /// </summary>
    public static class ClashGrouperEngine
    {
        /// <summary>Each test as it was before the last grouping, for Undo.</summary>
        private static readonly Dictionary<string, ClashTest> Before = new Dictionary<string, ClashTest>(StringComparer.Ordinal);  // by test name: a replaced test may get a new id

        public static IEnumerable<ClashTest> Tests(Document doc) => doc.GetClash().TestsData.Tests.OfType<ClashTest>();

        /// <summary>Every clash result of the test, groups opened.</summary>
        public static List<ClashResult> Results(ClashTest t)
        {
            var list = new List<ClashResult>();
            void Walk(SavedItem s)
            {
                if (s is ClashResult r) list.Add(r);
                else if (s is GroupItem g) foreach (SavedItem c in g.Children) Walk(c);
            }
            foreach (SavedItem s in t.Children) Walk(s);
            return list;
        }

        private static ModelItem MainItem(ClashResult r, bool item1) => item1 ? r.Item1 : r.Item2;

        public static TestGrouping Group(Document doc, ClashTest test, bool item1Main, GroupingOptions o)
        {
            var report = new TestGrouping { Test = test.DisplayName };
            double toMetres = NavisExtractor.ToMetres(doc.Units);
            List<ClashResult> results = Results(test);
            report.Clashes = results.Count;
            if (results.Count == 0) return report;

            // The main-side elements: those of the clashes, then the main-side pieces near them (a run of ducts is several
            // pieces; a piece between two clashing ones connects them though it clashes with nothing).
            var elements = new List<MainElement>();
            var index = new Dictionary<ModelItem, int>();
            int Add(ModelItem m)
            {
                if (index.TryGetValue(m, out int i)) return i;
                i = elements.Count;
                elements.Add(Element(m, toMetres));
                index[m] = i;
                return i;
            }
            var points = new List<ClashPoint>();
            for (int k = 0; k < results.Count; k++)
            {
                ClashResult r = results[k];
                ModelItem main = MainItem(r, item1Main);
                if (main == null) { report.Skipped++; continue; }
                Point3D c = r.Center;
                points.Add(new ClashPoint { Index = k, Status = r.Status.ToString(), X = c.X * toMetres, Y = c.Y * toMetres, Z = c.Z * toMetres, Element = Add(main) });
            }
            foreach (ModelItem near in NearbyMainPieces(test, item1Main, points, o, toMetres))
                Add(near);

            int[] sets = ClashGrouping.ConnectedSets(elements, o.ConnectGap);
            List<List<int>> groups = ClashGrouping.Group(points, sets, o);

            // The test written back: the new groups, then every clash not grouped, as it was (old groups discarded).
            ClashTest copy = (ClashTest)test.CreateCopy();
            copy.Children.Clear();
            var used = new HashSet<int>();
            var perStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (List<int> g in groups)
            {
                string status = results[g[0]].Status.ToString();
                perStatus[status] = perStatus.TryGetValue(status, out int n) ? n + 1 : 1;
                var group = new ClashResultGroup
                {
                    DisplayName = ClashGrouping.GroupName(status, perStatus[status], g.Select(i => MainItem(results[i], item1Main)?.DisplayName ?? ""), g.Count)
                };
                foreach (int i in g) { group.Children.Add(results[i].CreateCopy()); used.Add(i); }
                copy.Children.Add(group);
                report.GroupNames.Add(group.DisplayName);
            }
            for (int i = 0; i < results.Count; i++)
                if (!used.Contains(i)) copy.Children.Add(results[i].CreateCopy());

            // TestsEditTestFromCopy takes the settings only, not the results (2026-11-22, the groups were not written):
            // the whole test is replaced by the copy.
            Before[Key(doc, test)] = (ClashTest)test.CreateCopy();
            Replace(doc, test, copy);
            report.Groups = groups.Count;
            report.Grouped = used.Count;
            report.Single = results.Count - used.Count;
            return report;
        }

        /// <summary>The undo key: the model file and the test name (review finding: another model's test of the same name must not be overwritten).</summary>
        private static string Key(Document doc, ClashTest test) => (doc.FileName ?? "") + "|" + test.DisplayName;

        /// <summary>Replaces the test (settings and results) by the copy, in its place in the list.</summary>
        private static void Replace(Document doc, ClashTest test, ClashTest copy)
        {
            DocumentClashTests data = doc.GetClash().TestsData;
            int at = -1;
            for (int i = 0; i < data.Tests.Count; i++) if (data.Tests[i].Guid == test.Guid) { at = i; break; }
            if (at < 0) throw new InvalidOperationException($"the clash test \"{test.DisplayName}\" is no longer in the document");
            data.TestsReplaceWithCopy(at, copy);
        }

        /// <summary>Puts the test back as it was before its last grouping; false when there is none.</summary>
        public static bool Undo(Document doc, ClashTest test)
        {
            if (!Before.TryGetValue(Key(doc, test), out ClashTest was)) return false;
            Replace(doc, test, was);
            Before.Remove(Key(doc, test));
            return true;
        }

        /// <summary>The main-side geometry items whose box comes within the span of a clash (pieces that may join two clashing ones).</summary>
        private static IEnumerable<ModelItem> NearbyMainPieces(ClashTest test, bool item1Main, List<ClashPoint> points, GroupingOptions o, double toMetres)
        {
            if (points.Count == 0) yield break;
            ModelItemCollection side;
            try { side = (item1Main ? test.SelectionA : test.SelectionB).Selection.GetSelectedItems(); }
            catch (Exception) { yield break; }
            double reach = o.MaxSpan + o.ConnectGap;
            double minX = points.Min(p => p.X) - reach, maxX = points.Max(p => p.X) + reach, minY = points.Min(p => p.Y) - reach, maxY = points.Max(p => p.Y) + reach;
            foreach (ModelItem root in side)
                foreach (ModelItem m in root.DescendantsAndSelf)
                {
                    if (!m.HasGeometry) continue;
                    BoundingBox3D b = m.BoundingBox();
                    if (b == null || b.IsEmpty) continue;
                    double bx0 = b.Min.X * toMetres, bx1 = b.Max.X * toMetres, by0 = b.Min.Y * toMetres, by1 = b.Max.Y * toMetres;
                    if (bx1 < minX || bx0 > maxX || by1 < minY || by0 > maxY) continue;
                    if (points.Any(p => p.X >= bx0 - reach && p.X <= bx1 + reach && p.Y >= by0 - reach && p.Y <= by1 + reach)) yield return m;
                }
        }

        /// <summary>A main-side element: its box and up to 4,000 of its shape's triangles, in metres.</summary>
        private static MainElement Element(ModelItem m, double toMetres)
        {
            var e = new MainElement { Name = m.DisplayName ?? "" };
            BoundingBox3D b = m.BoundingBox();
            if (b != null && !b.IsEmpty)
            {
                e.MinX = b.Min.X * toMetres; e.MinY = b.Min.Y * toMetres; e.MinZ = b.Min.Z * toMetres;
                e.MaxX = b.Max.X * toMetres; e.MaxY = b.Max.Y * toMetres; e.MaxZ = b.Max.Z * toMetres;
            }
            try
            {
                var tris = new List<double>();
                if (m.HasGeometry) NavisExtractor.AddTriangles(m, tris, toMetres);
                else foreach (ModelItem d in m.Descendants) if (d.HasGeometry) NavisExtractor.AddTriangles(d, tris, toMetres);
                // Whole triangles (every k-th, up to 4,000), so a point can be measured to the faces.
                int nt = tris.Count / 9, step = Math.Max(1, nt / 4000);
                var pts = new List<double>();
                for (int k = 0; k < nt; k += step) for (int q = 0; q < 9; q++) pts.Add(tris[9 * k + q]);
                e.Points = pts.ToArray();
            }
            catch (Exception ex) { C3DProLogger.Warn($"Clash Grouper: the shape of {e.Name} could not be read; its box is used", ex); }
            return e;
        }

        /// <summary>The items of the groups (and single clashes) chosen: item 1, item 2 or both.</summary>
        public static ModelItemCollection Items(ClashTest test, IEnumerable<string> groupNames, bool item1, bool item2)
        {
            var names = new HashSet<string>(groupNames ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var items = new ModelItemCollection();
            foreach (SavedItem s in test.Children)
            {
                if (!names.Contains(s.DisplayName)) continue;
                IEnumerable<ClashResult> rs = s is ClashResult r ? new[] { r } : s is GroupItem g ? g.Children.OfType<ClashResult>() : Enumerable.Empty<ClashResult>();
                foreach (ClashResult c in rs)
                {
                    if (item1 && c.Item1 != null) items.Add(c.Item1);
                    if (item2 && c.Item2 != null) items.Add(c.Item2);
                }
            }
            return items;
        }

        // ---- Send to Civil 3D: the other side's items selected, and their clash areas kept for the link ----

        private static HashSet<ModelItem> _sentItems;
        private static List<NavisLink.NavisClashArea> _sentAreas = new List<NavisLink.NavisClashArea>();

        /// <summary>
        /// Selects the other side's items of the chosen groups and single clashes (the main side, the network being changed, is
        /// not imported) and keeps one area per group or clash for Civil 3D. Returns the number of items and areas.
        /// </summary>
        public static (int items, int areas) Send(Document doc, ClashTest test, IEnumerable<string> names, bool item1Main)
        {
            double toMetres = NavisExtractor.ToMetres(doc.Units);
            var chosen = new HashSet<string>(names ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var items = new ModelItemCollection();
            var areas = new List<NavisLink.NavisClashArea>();
            foreach (SavedItem s in test.Children)
            {
                if (!chosen.Contains(s.DisplayName)) continue;
                List<ClashResult> rs = s is ClashResult r ? new List<ClashResult> { r } : s is GroupItem g ? g.Children.OfType<ClashResult>().ToList() : new List<ClashResult>();
                if (rs.Count == 0) continue;
                var a = new NavisLink.NavisClashArea
                {
                    Name = s.DisplayName, Test = test.DisplayName, Clashes = rs.Count,
                    Status = rs.Select(x => x.Status.ToString()).Distinct().Count() == 1 ? rs[0].Status.ToString() : "mixed",
                    MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue
                };
                double z = 0;
                foreach (ClashResult c in rs)
                {
                    ModelItem other = item1Main ? c.Item2 : c.Item1;
                    if (other != null) items.Add(other);
                    // The clash point, not the result's box: that box covers both whole elements, so a long duct made a 24 x 40 m
                    // area of clashes 6 m apart (test 04, 2026-11-22). Civil 3D adds the margin round the points.
                    Point3D p = c.Center;
                    a.MinX = Math.Min(a.MinX, p.X * toMetres); a.MaxX = Math.Max(a.MaxX, p.X * toMetres);
                    a.MinY = Math.Min(a.MinY, p.Y * toMetres); a.MaxY = Math.Max(a.MaxY, p.Y * toMetres);
                    z += p.Z * toMetres;
                }
                a.Z = z / rs.Count;
                areas.Add(a);
            }
            doc.CurrentSelection.CopyFrom(items);
            _sentItems = new HashSet<ModelItem>(doc.CurrentSelection.SelectedItems.Cast<ModelItem>());
            _sentAreas = areas;
            return (items.Count, areas.Count);
        }

        /// <summary>The areas sent with the current selection: only while it is still exactly what Send selected.</summary>
        public static List<NavisLink.NavisClashArea> AreasForSelection(Document doc)
        {
            if (_sentItems == null) return new List<NavisLink.NavisClashArea>();
            ModelItemCollection now = doc.CurrentSelection.SelectedItems;
            if (now.Count != _sentItems.Count || now.Cast<ModelItem>().Any(m => !_sentItems.Contains(m)))
            {
                // The selection changed: the areas are forgotten, so they never come back with a later plain selection (review finding).
                _sentItems = null;
                _sentAreas = new List<NavisLink.NavisClashArea>();
                return new List<NavisLink.NavisClashArea>();
            }
            return _sentAreas;
        }

        public static string Describe(IEnumerable<TestGrouping> runs)
        {
            var sb = new StringBuilder();
            foreach (TestGrouping t in runs)
            {
                if (t.Error != null) { sb.AppendLine($"{t.Test}: failed ({t.Error})"); continue; }
                sb.AppendLine($"{t.Test}: {t.Clashes} clashes -> {t.Groups} groups ({t.Grouped} clashes), {t.Single} single" + (t.Skipped > 0 ? $", {t.Skipped} without a main-side item" : ""));
                foreach (string g in t.GroupNames) sb.AppendLine("    " + g);
            }
            return sb.ToString();
        }
    }
}
