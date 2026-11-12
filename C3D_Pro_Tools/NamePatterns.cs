using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    /// <summary>Name filters typed at the preset-run prompts: comma-separated wildcards, a leading ! leaves names out.</summary>
    internal static class NamePatterns
    {
        public static bool Matches(string patterns, string name)
        {
            string[] parts = Split(patterns);
            if (parts.Where(p => p.StartsWith("!")).Any(p => Like(name, p.Substring(1)))) return false;
            string[] include = parts.Where(p => !p.StartsWith("!")).ToArray();
            return include.Length == 0 || include.Any(p => Like(name, p));
        }

        /// <summary>
        /// When the patterns match none of the names, tries them as "contains" (124* becomes *124*), because names usually
        /// carry a prefix the user leaves out (ICT-ALI-124-...). Returns the patterns to use; note says what was done, or is
        /// null when the patterns matched as typed.
        /// </summary>
        public static string Widen(string patterns, IEnumerable<string> names, string what, out string note)
        {
            note = null;
            List<string> all = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (all.Count == 0 || all.Any(n => Matches(patterns, n))) return patterns;

            string widened = string.Join(",", Split(patterns).Select(p =>
            {
                bool not = p.StartsWith("!");
                string q = not ? p.Substring(1) : p;
                if (!not) q = (q.StartsWith("*") ? "" : "*") + q + (q.EndsWith("*") ? "" : "*");
                return (not ? "!" : "") + q;
            }));
            int found = all.Count(n => Matches(widened, n));
            if (found > 0)
            {
                note = $"No {what} is named \"{patterns}\"; using \"{widened}\" ({found} found).";
                return widened;
            }
            note = $"No {what} matches \"{patterns}\". Names in the drawing: " +
                   string.Join(", ", all.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(5)) + (all.Count > 5 ? ", ..." : "");
            return patterns;
        }

        private static string[] Split(string patterns) =>
            (string.IsNullOrWhiteSpace(patterns) ? "*" : patterns).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool Like(string name, string pattern) =>
            Regex.IsMatch(name ?? "", "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
    }
}
