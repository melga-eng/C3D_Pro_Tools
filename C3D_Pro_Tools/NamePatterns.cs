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
        /// When the patterns match none of the names, tries each one as the start of a part of the name (124* finds
        /// ICT-ALI-124-1-89-01, not ICT-1124-A), because names usually carry a prefix the user leaves out. Returns the
        /// patterns to use: unchanged, or the exact names found (comma separated). note says what was done, or is null when
        /// the patterns matched as typed.
        /// </summary>
        public static string Widen(string patterns, IEnumerable<string> names, string what, out string note)
        {
            note = null;
            List<string> all = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0 || all.Any(n => Matches(patterns, n))) return patterns;

            string[] parts = Split(patterns);
            string[] include = parts.Where(p => !p.StartsWith("!")).ToArray();
            string[] exclude = parts.Where(p => p.StartsWith("!")).Select(p => p.Substring(1)).ToArray();
            List<string> found = all
                .Where(n => include.Length > 0 && include.Any(p => StartsAPart(n, p)) && !exclude.Any(p => Like(n, p)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (found.Count > 0)
            {
                note = $"No {what} is named \"{patterns}\"; using the {found.Count} whose name has a part starting with it: " +
                       string.Join(", ", found.Take(4)) + (found.Count > 4 ? ", ..." : "") + ".";
                return string.Join(",", found);
            }
            note = $"No {what} matches \"{patterns}\". Names in the drawing: " +
                   string.Join(", ", all.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(5)) + (all.Count > 5 ? ", ..." : "");
            return patterns;
        }

        private static string[] Split(string patterns) =>
            (string.IsNullOrWhiteSpace(patterns) ? "*" : patterns).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string Glob(string pattern) => Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");

        private static bool Like(string name, string pattern) =>
            Regex.IsMatch(name ?? "", "^" + Glob(pattern) + "$", RegexOptions.IgnoreCase);

        /// <summary>The pattern matches from the start of the name or right after a - _ . or space, to the end of the name.</summary>
        private static bool StartsAPart(string name, string pattern) =>
            Regex.IsMatch(name ?? "", "(^|[-_. ])" + Glob(pattern.TrimStart('*').TrimEnd('*')) + ".*$", RegexOptions.IgnoreCase);
    }
}
