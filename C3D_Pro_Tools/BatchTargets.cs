using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Where the style batch saves its copies. No AutoCAD types, so it is tested without Civil 3D; FormStyleBatch checks the plan
// before any drawing is opened, because Database.SaveAs replaces an existing file without asking.
namespace C3DProTools
{
    internal sealed class BatchTargetPlan
    {
        /// <summary>Each source drawing (full path) and the path its copy is saved to.</summary>
        public readonly Dictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Why the batch cannot run as set; empty when it can.</summary>
        public readonly List<string> Problems = new List<string>();
        /// <summary>Copies that already exist and will be replaced.</summary>
        public int Existing;
        public bool Ok => Problems.Count == 0;
    }

    internal static class BatchTargets
    {
        /// <summary>
        /// The copy of each drawing in <paramref name="outputFolder"/>, keeping its folder below the folder all the drawings share
        /// (two drawings of the same name in different folders get different copies). A problem is reported, and no drawing
        /// should be processed, when two copies would have the same path or a copy would replace one of the source drawings.
        /// </summary>
        public static BatchTargetPlan Plan(IReadOnlyList<string> sources, string outputFolder, Func<string, bool> exists = null)
        {
            exists = exists ?? File.Exists;
            BatchTargetPlan plan = new BatchTargetPlan();
            List<string> full = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (full.Count == 0) return plan;
            string output = Path.GetFullPath(outputFolder);
            string root = CommonFolder(full.Select(Path.GetDirectoryName));
            HashSet<string> sourceSet = new HashSet<string>(full, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> byTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in full)
            {
                // Drawings on different drives share no folder: the file name alone (a clash is then reported below).
                string t = Path.GetFullPath(Path.Combine(output, root.Length == 0 ? Path.GetFileName(s) : Path.GetRelativePath(root, s)));
                plan.Targets[s] = t;
                if (sourceSet.Contains(t)) plan.Problems.Add($"The copy of {s} would replace a source drawing. Choose an output folder outside the drawings' folders.");
                else if (byTarget.TryGetValue(t, out string other)) plan.Problems.Add($"{other} and {s} would both be saved as {t}.");
                else { byTarget[t] = s; if (exists(t)) plan.Existing++; }
            }
            return plan;
        }

        /// <summary>The deepest folder containing all these folders.</summary>
        private static string CommonFolder(IEnumerable<string> folders)
        {
            List<string[]> parts = folders.Select(f => f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })).ToList();
            int n = 0;
            while (parts.All(p => p.Length > n && string.Equals(p[n], parts[0][n], StringComparison.OrdinalIgnoreCase))) n++;
            // A drive root ("C:") needs its separator to stay a folder.
            return n == 0 ? "" : string.Join(Path.DirectorySeparatorChar.ToString(), parts[0].Take(n)) + Path.DirectorySeparatorChar;
        }
    }
}
