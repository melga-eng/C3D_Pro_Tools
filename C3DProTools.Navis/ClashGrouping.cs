using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools.Navis
{
    /// <summary>One clash as the grouping sees it (metres, plain data).</summary>
    public sealed class ClashPoint
    {
        public int Index;
        public string Status = "";
        public double X, Y, Z;
        /// <summary>The main side's element (item 1 by default), as an index into the element list.</summary>
        public int Element;
    }

    /// <summary>An element of the main side: its box and some of its points (metres), to tell whether two elements touch.</summary>
    public sealed class MainElement
    {
        public string Name = "";
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        /// <summary>Points of its shape (triangle corners); empty when only the box is known.</summary>
        public double[] Points = new double[0];
    }

    public sealed class GroupingOptions
    {
        /// <summary>Elements closer than this (m) are connected (default 1 m).</summary>
        public double ConnectGap = 1.0;
        /// <summary>No two clashes of a group are farther apart than this (m, by their clash points; default 25 m).</summary>
        public double MaxSpan = 25.0;
        /// <summary>The statuses that are grouped; clashes of other statuses stay ungrouped.</summary>
        public HashSet<string> Statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New", "Active", "Reviewed" };
    }

    /// <summary>
    /// Clash grouping (your rules, 2026-11-22): two clashes belong together when they have the same status, their main-side
    /// elements are the same or connected (touching within the gap, through other main-side elements too), and every clash
    /// of the group is within the largest span of every other one. A group of one clash is no group: it stays single.
    /// </summary>
    public static class ClashGrouping
    {
        /// <summary>Connected element sets: each element's set number (elements touching within the gap, chained).</summary>
        public static int[] ConnectedSets(IReadOnlyList<MainElement> elements, double gap)
        {
            int n = elements.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (Find(i) != Find(j) && Touch(elements[i], elements[j], gap)) parent[Find(i)] = Find(j);
            return Enumerable.Range(0, n).Select(Find).ToArray();
        }

        /// <summary>True when the boxes are within the gap and (when both shapes are known) two of their points are.</summary>
        public static bool Touch(MainElement a, MainElement b, double gap)
        {
            double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
            double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
            double dz = Math.Max(0, Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
            if (dx * dx + dy * dy + dz * dz > gap * gap) return false;
            if (a.Points.Length < 3 || b.Points.Length < 3) return true;
            // A long diagonal duct's box covers ground it is far from: the shapes decide.
            double g2 = gap * gap;
            for (int i = 0; i + 2 < a.Points.Length; i += 3)
            {
                double x = a.Points[i], y = a.Points[i + 1], z = a.Points[i + 2];
                if (x < b.MinX - gap || x > b.MaxX + gap || y < b.MinY - gap || y > b.MaxY + gap || z < b.MinZ - gap || z > b.MaxZ + gap) continue;
                for (int j = 0; j + 2 < b.Points.Length; j += 3)
                {
                    double ex = x - b.Points[j], ey = y - b.Points[j + 1], ez = z - b.Points[j + 2];
                    if (ex * ex + ey * ey + ez * ez <= g2) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The groups (each a list of clash indexes, two or more), largest first. Within one status and one connected element
        /// set, clashes are joined closest first while every clash stays within the largest span of every other (complete
        /// linkage), so a chain along a long route never grows past the span.
        /// </summary>
        public static List<List<int>> Group(IReadOnlyList<ClashPoint> clashes, int[] elementSet, GroupingOptions o)
        {
            var result = new List<List<int>>();
            var buckets = clashes.Where(c => o.Statuses.Contains(c.Status))
                                 .GroupBy(c => (c.Status.ToLowerInvariant(), elementSet[c.Element]));
            foreach (var bucket in buckets)
            {
                List<List<ClashPoint>> groups = bucket.Select(c => new List<ClashPoint> { c }).ToList();
                while (true)
                {
                    int bi = -1, bj = -1;
                    double best = double.MaxValue;
                    for (int i = 0; i < groups.Count; i++)
                        for (int j = i + 1; j < groups.Count; j++)
                        {
                            double far = 0, near = double.MaxValue;
                            foreach (ClashPoint a in groups[i])
                                foreach (ClashPoint b in groups[j])
                                {
                                    double d = Dist(a, b);
                                    far = Math.Max(far, d); near = Math.Min(near, d);
                                }
                            if (far <= o.MaxSpan && near < best) { best = near; bi = i; bj = j; }
                        }
                    if (bi < 0) break;
                    groups[bi].AddRange(groups[bj]);
                    groups.RemoveAt(bj);
                }
                result.AddRange(groups.Where(g => g.Count > 1).Select(g => g.Select(c => c.Index).OrderBy(x => x).ToList()));
            }
            return result.OrderByDescending(g => g.Count).ThenBy(g => g[0]).ToList();
        }

        private static double Dist(ClashPoint a, ClashPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>An element's name without Navisworks' copy numbers: "IF-PIP-MVDCT-T1-212 (3)" -> "IF-PIP-MVDCT-T1-212".</summary>
        public static string BaseName(string name) => Regex.Replace(name ?? "", @"(\s*\(\d+\))+$", "").Trim();

        /// <summary>"Active 01 - IF-PIP-MVDCT-T1-212 - 14 clashes" (the first two main elements by name; "+n" for more).</summary>
        public static string GroupName(string status, int number, IEnumerable<string> mainNames, int count)
        {
            var names = mainNames.Select(BaseName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            string what = names.Count == 0 ? "" : string.Join(", ", names.Take(2)) + (names.Count > 2 ? $" +{names.Count - 2}" : "");
            return $"{status} {number:00}" + (what.Length > 0 ? " - " + what : "") + $" - {count} clashes";
        }
    }
}
