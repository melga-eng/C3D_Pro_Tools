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
            Before[test.DisplayName] = (ClashTest)test.CreateCopy();
            Replace(doc, test, copy);
            report.Groups = groups.Count;
            report.Grouped = used.Count;
            report.Single = results.Count - used.Count;
            return report;
        }

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
            if (!Before.TryGetValue(test.DisplayName, out ClashTest was)) return false;
            Replace(doc, test, was);
            Before.Remove(test.DisplayName);
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

        /// <summary>A main-side element: its box and up to 800 of its shape's points, in metres.</summary>
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
                int n = tris.Count / 3, step = Math.Max(1, n / 800);
                var pts = new List<double>();
                for (int i = 0; i < n; i += step) { pts.Add(tris[3 * i]); pts.Add(tris[3 * i + 1]); pts.Add(tris[3 * i + 2]); }
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
