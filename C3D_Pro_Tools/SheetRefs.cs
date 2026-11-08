using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// Rewriting references to renamed sheets (match line texts such as "SEE S-01"). No AutoCAD types, so it is tested without
// Civil 3D; SheetManager.Rename uses it.
namespace C3DProTools
{
    internal static class SheetRefs
    {
        /// <summary>
        /// The text with every sheet name in <paramref name="renames"/> (old → new, any case) replaced by its new name, in one
        /// pass over the original text, so names can be swapped (S-01 ↔ S-02) without a replaced name being replaced again.
        /// A name only matches where it is not followed by a letter, digit, "_" or "-" (S-1 does not match inside S-10).
        /// </summary>
        public static string Replace(string text, IReadOnlyDictionary<string, string> renames)
        {
            if (string.IsNullOrEmpty(text) || renames == null || renames.Count == 0) return text;
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in renames.Where(kv => !string.IsNullOrEmpty(kv.Key))) map[kv.Key] = kv.Value;
            if (map.Count == 0) return text;
            // Longest first, so "S-01A" is matched before "S-01".
            string names = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            return Regex.Replace(text, "(?:" + names + @")(?![\w-])", m => map[m.Value], RegexOptions.IgnoreCase);
        }
    }
}
