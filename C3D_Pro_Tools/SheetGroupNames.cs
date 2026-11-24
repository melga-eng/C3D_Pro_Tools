using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    /// <summary>
    /// Sheet groups, the plain-data part: a group is a name the user gives a set of sheets for their own reference (one group
    /// per sheet, none by default). Unit tested.
    /// </summary>
    internal static class SheetGroupNames
    {
        /// <summary>The name as kept: trimmed, inner runs of spaces made one; empty means no group.</summary>
        public static string Clean(string name) => Regex.Replace(name ?? "", @"\s+", " ").Trim();

        /// <summary>
        /// The groups of these sheets in the order they first appear (tab order), each with its sheet count. Names that differ
        /// only in case are one group, under the spelling met first.
        /// </summary>
        public static List<(string name, int count)> Groups(IEnumerable<string> groupOfEachSheet)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in groupOfEachSheet)
            {
                string g = Clean(raw);
                if (g.Length == 0) continue;
                if (counts.ContainsKey(g)) counts[g]++;
                else { counts[g] = 1; order.Add(g); }
            }
            return order.Select(g => (g, counts[g])).ToList();
        }

        /// <summary>
        /// The order that puts each group's sheets together: groups where their first sheet is, sheets with no group where
        /// they are relative to the groups, and the order within a group kept.
        /// </summary>
        public static List<int> Together(IList<string> groupOfEachSheet)
        {
            var result = new List<int>();
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < groupOfEachSheet.Count; i++)
            {
                string g = Clean(groupOfEachSheet[i]);
                if (g.Length == 0) { result.Add(i); continue; }
                if (!placed.Add(g)) continue;
                for (int j = i; j < groupOfEachSheet.Count; j++)
                    if (Clean(groupOfEachSheet[j]).Equals(g, StringComparison.OrdinalIgnoreCase)) result.Add(j);
            }
            return result;
        }
    }
}
