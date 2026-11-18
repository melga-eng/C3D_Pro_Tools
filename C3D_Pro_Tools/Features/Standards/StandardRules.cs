using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// The KSIA CAD standard rules a drawing can be checked against (AFXXXX0000000D-C00043-PRS-XX-STD-BM-000001, 05-Mar-2026;
    /// EIR Appendix E title block). Plain data, no Autodesk types.
    /// </summary>
    internal static class StandardRules
    {
        /// <summary>STD 8: the plotted line widths allowed, in hundredths of a millimetre.</summary>
        public static readonly int[] LineWeights = { 5, 13, 18, 25, 35, 50, 70, 100, 140, 200 };

        /// <summary>STD 9: the scales allowed (1:n); 1:125 and 1:150 are not preferred.</summary>
        public static readonly int[] Scales = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 1250, 2000, 2500, 5000, 10000, 20000, 50000 };
        public static readonly int[] NotPreferredScales = { 125, 150 };

        /// <summary>STD 6: the font of all annotation.</summary>
        public const string Font = "Arial Narrow";

        /// <summary>STD 5.1, GIR 8.3: the coordinate system.</summary>
        public const string CoordinateSystem = "UTM84-38N";

        /// <summary>A layer line weight (hundredths of a mm; negative: by layer, by block, default) the standard allows; the defaults are not judged.</summary>
        public static bool LineWeightAllowed(int hundredths) => hundredths < 0 || LineWeights.Contains(hundredths);

        /// <summary>"allowed", "not preferred" or "not allowed" for a viewport at 1:<paramref name="denominator"/> (within 0.5 %).</summary>
        public static string ScaleState(double denominator)
        {
            if (denominator <= 0 || double.IsNaN(denominator)) return "not allowed";
            bool Near(int s) => Math.Abs(denominator - s) <= s * 0.005;
            if (Scales.Any(Near)) return "allowed";
            return NotPreferredScales.Any(Near) ? "not preferred" : "not allowed";
        }

        /// <summary>A font the standard allows: Arial Narrow (its TrueType file is arialn.ttf).</summary>
        public static bool FontAllowed(string typeface, string fileName) =>
            string.Equals((typeface ?? "").Trim(), Font, StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(fileName ?? "", @"(^|[\\/])arialn(b|i|bi)?\.ttf$", RegexOptions.IgnoreCase);

        /// <summary>TB p.1–4: a date written DD/MM/YYYY.</summary>
        public static bool DateOk(string value) =>
            DateTime.TryParseExact((value ?? "").Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        /// <summary>The scale box: 1:n with an allowed n, or NTS, As Shown, N/A.</summary>
        public static bool ScaleTextOk(string value)
        {
            string v = (value ?? "").Trim();
            if (Regex.IsMatch(v, @"^(NTS|AS SHOWN|N/A)$", RegexOptions.IgnoreCase)) return true;
            Match m = Regex.Match(v, @"^1\s*:\s*([\d,]+)$");
            return m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out int n) && ScaleState(n) != "not allowed";
        }

        /// <summary>A path written relative to the drawing ("..\x.dwg", ".\x.dwg", "x.dwg"), not a full one.</summary>
        public static bool IsRelativePath(string path) =>
            !string.IsNullOrEmpty(path) && !System.IO.Path.IsPathRooted(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);

        /// <summary>The path of <paramref name="target"/> relative to the folder of <paramref name="drawing"/>; null when they are on different drives.</summary>
        public static string RelativeTo(string drawing, string target)
        {
            if (string.IsNullOrEmpty(drawing) || string.IsNullOrEmpty(target)) return null;
            string from = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(drawing));
            string to = System.IO.Path.GetFullPath(target);
            if (!string.Equals(System.IO.Path.GetPathRoot(from), System.IO.Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) return null;
            string rel = System.IO.Path.GetRelativePath(from, to);
            return rel.StartsWith("..", StringComparison.Ordinal) ? rel : @".\" + rel;
        }
    }
}
