using System;
using System.Collections.Generic;
using System.Linq;

// Merging gravity networks: which parts of the chosen networks are copies of each other (the same pipe or structure in the
// same place), which copy stays, and what happens to every part. No AutoCAD / Civil 3D types, so the rules are tested without
// Civil 3D; NetworkOrganizer reads the drawing and does the merge.
namespace C3DProTools
{
    /// <summary>A pipe or structure of one of the networks being merged.</summary>
    internal sealed class MergePart
    {
        public string Id = "", Name = "", Network = "";
        public bool IsPipe;
        /// <summary>A pipe's two ends, or a structure's location (in A).</summary>
        public (double X, double Y, double Z) A, B;
        /// <summary>The part size (family and size), to tell a true copy from a different part in the same place.</summary>
        public string Size = "";
        /// <summary>How much the drawing uses this copy (its labels and profile views): the most used copy is kept.</summary>
        public int Uses;
        public bool InTarget;
    }

    internal enum MergeAction
    {
        /// <summary>Moved into the target network.</summary>
        Move,
        /// <summary>Already in the target network; stays.</summary>
        Stay,
        /// <summary>A copy of <see cref="MergeRow.Keeper"/> (same place, same size): deleted.</summary>
        DeleteCopy,
        /// <summary>In the same place as <see cref="MergeRow.Keeper"/> but a different size or part: you choose (kept unless ticked).</summary>
        SamePlaceOtherSize
    }

    internal sealed class MergeRow
    {
        public MergePart Part;
        public MergeAction Action;
        /// <summary>The copy that stays, for a copy deleted or a part in the same place.</summary>
        public MergePart Keeper;
    }

    internal static class NetworkMerge
    {
        private static double Plan2((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>
        /// True when two parts are the same part in the same place: pipes with both ends together (either way round) within
        /// <paramref name="tol"/> in plan and <paramref name="zTol"/> in level; structures at the same plan location.
        /// </summary>
        public static bool SamePlace(MergePart a, MergePart b, double tol = 0.001, double zTol = 0.005)
        {
            if (a.IsPipe != b.IsPipe) return false;
            bool Near((double X, double Y, double Z) p, (double X, double Y, double Z) q, bool level) => Plan2(p, q) <= tol && (!level || Math.Abs(p.Z - q.Z) <= zTol);
            if (!a.IsPipe) return Near(a.A, b.A, false);
            return (Near(a.A, b.A, true) && Near(a.B, b.B, true)) || (Near(a.A, b.B, true) && Near(a.B, b.A, true));
        }

        /// <summary>
        /// What happens to every part when the networks are merged: parts in the same place are grouped; the most used copy (then
        /// the one already in the target, then the first) stays; true copies of it are deleted, parts of another size in the same
        /// place are left for you to choose; every part that stays moves into the target unless it is there already.
        /// </summary>
        public static List<MergeRow> Plan(IReadOnlyList<MergePart> parts, double tol = 0.001, double zTol = 0.005)
        {
            int n = parts.Count;
            int[] group = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            // Parts near each other first (a sort along X), so only close pairs are compared.
            int[] byX = Enumerable.Range(0, n).OrderBy(i => Math.Min(parts[i].A.X, parts[i].IsPipe ? parts[i].B.X : parts[i].A.X)).ToArray();
            for (int k = 0; k < n; k++)
            {
                MergePart a = parts[byX[k]];
                double ax = Math.Min(a.A.X, a.IsPipe ? a.B.X : a.A.X);
                for (int m = k + 1; m < n; m++)
                {
                    MergePart b = parts[byX[m]];
                    if (Math.Min(b.A.X, b.IsPipe ? b.B.X : b.A.X) - ax > tol) break;
                    if (SamePlace(a, b, tol, zTol)) group[Find(byX[m])] = Find(byX[k]);
                }
            }
            List<MergeRow> rows = new List<MergeRow>();
            foreach (IGrouping<int, int> g in Enumerable.Range(0, n).GroupBy(Find))
            {
                List<MergePart> members = g.Select(i => parts[i]).ToList();
                MergePart keeper = members.OrderByDescending(p => p.Uses).ThenByDescending(p => p.InTarget).First();
                foreach (MergePart p in members)
                {
                    MergeAction action = p == keeper ? (p.InTarget ? MergeAction.Stay : MergeAction.Move)
                        : string.Equals(p.Size, keeper.Size, StringComparison.OrdinalIgnoreCase) ? MergeAction.DeleteCopy : MergeAction.SamePlaceOtherSize;
                    rows.Add(new MergeRow { Part = p, Action = action, Keeper = p == keeper ? null : keeper });
                }
            }
            return rows.OrderBy(r => r.Part.Network, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Part.IsPipe ? 0 : 1).ThenBy(r => r.Part.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>How many parts of each network have a copy (or another part) in the same place, among these parts.</summary>
        public static Dictionary<string, int> CopiesByNetwork(IReadOnlyList<MergePart> parts, double tol = 0.001, double zTol = 0.005)
        {
            Dictionary<string, int> count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MergeRow r in Plan(parts, tol, zTol).Where(r => r.Keeper != null))
                count[r.Part.Network] = count.TryGetValue(r.Part.Network, out int c) ? c + 1 : 1;
            return count;
        }
    }
}
