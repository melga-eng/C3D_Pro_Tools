using System;
using System.Text.RegularExpressions;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// Name prefixes of blocks and styles (your rule, 2026-11-24): a prefix is KSIA_, never another company's code (JAC, JCB,
    /// JCP) or an xref drawing's name. Layers are not named this way: a bound xref layer becomes XREF_[layer name].
    /// Plain text; unit tested.
    /// </summary>
    internal static class NamePrefix
    {
        public const string Ksia = "KSIA_";
        public const string XrefLayer = "XREF_";

        /// <summary>Company codes that are not a prefix for the project (the consultants' own).</summary>
        private static readonly Regex Company = new Regex(@"^(?:JAC|JCB|JCP)[-_ ]+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        /// <summary>A bound xref: "drawing$0$name" (and nested).</summary>
        private static readonly Regex Bound = new Regex(@"\$\d+\$", RegexOptions.CultureInvariant);
        /// <summary>A project drawing number used as a prefix ("SW00650052000D-C00002-JCB-ZZ-DWG-EL-XX-462124 Legend").</summary>
        private static readonly Regex DrawingNumber = new Regex(@"^[A-Z]{2}\d{11}[A-Z]-[A-Z]\d{5}-[A-Z]{3}-[A-Z]{2}-[A-Z]{3}-[A-Z]{2}-[A-Z]{2}-\d{6}(?:-[A-Z]\d+)?[-_ ]+(?<rest>.+)$", RegexOptions.CultureInvariant);
        /// <summary>The project's own prefix, written KSIA_ or KSIA- / KSIA (space).</summary>
        private static readonly Regex Own = new Regex(@"^KSIA[-_ ]+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The name a block or style should have: KSIA_ in place of another company's code or an xref drawing's name; null when it is fine.</summary>
        public static string Proposed(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string rest = name;
            bool changed = false;
            MatchCollection bound = Bound.Matches(rest);
            if (bound.Count > 0)
            {
                Match last = bound[bound.Count - 1];
                rest = rest.Substring(last.Index + last.Length);
                changed = true;
            }
            for (int guard = 0; guard < 3; guard++)
            {
                Match m = Company.Match(rest);
                if (!m.Success) m = DrawingNumber.Match(rest);
                if (!m.Success) break;
                rest = m.Groups["rest"].Value.Trim();
                changed = true;
            }
            if (!changed || rest.Length == 0) return null;
            Match own = Own.Match(rest);
            string result = Ksia + (own.Success ? own.Groups["rest"].Value : rest);
            return result.Equals(name, StringComparison.Ordinal) ? null : result;
        }

        /// <summary>The name of a bound xref layer ("drawing$0$layer") as XREF_[layer]; null when the layer is not one.</summary>
        public static string XrefLayerName(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return null;
            MatchCollection bound = Bound.Matches(layer);
            if (bound.Count == 0) return null;
            Match last = bound[bound.Count - 1];
            string inner = layer.Substring(last.Index + last.Length);
            return inner.Length == 0 ? null : XrefLayer + inner;
        }

        /// <summary>The same block or style with or without its prefix changed (JCB-TTB_X and KSIA_TTB_X), so a saved name still finds it.</summary>
        public static bool SameIgnoringPrefix(string a, string b)
        {
            if (string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(Core(a), Core(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Core(string name)
        {
            string p = Proposed(name.Trim()) ?? name.Trim();
            Match own = Own.Match(p);
            return own.Success ? own.Groups["rest"].Value : p;
        }

        /// <summary>
        /// The revision letter a stage asks for (your rule, 2026-11-24): N for IFC drawings, C for SDR (shop drawings); null when
        /// the stage says neither.
        /// </summary>
        public static string RevisionLetter(string stage)
        {
            string s = stage ?? "";
            if (Regex.IsMatch(s, @"\bIFC\b|issued\s+for\s+construction", RegexOptions.IgnoreCase)) return "N";
            if (Regex.IsMatch(s, @"\bSDR\b|shop\s*drawing", RegexOptions.IgnoreCase)) return "C";
            return null;
        }

        /// <summary>True when the revision is the stage's letter and a number (N3 for IFC, C1 for SDR).</summary>
        public static bool RevisionFits(string revision, string letter) =>
            letter == null || Regex.IsMatch((revision ?? "").Trim(), $@"^{letter}\d+$", RegexOptions.IgnoreCase);
    }
}
