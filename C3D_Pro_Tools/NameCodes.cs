using System.Globalization;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    /// <summary>Type codes for part names, from a part's style, size, family or description (plain text; unit tested).</summary>
    internal static class NameCodes
    {
        /// <summary>A short code suggested for a type value: the part after the last "-"/"_" with letters and digits (e.g. "Duct IC-3B" â†’ "IC3B").</summary>
        public static string Suggest(string value)
        {
            // MV patterns (KSIA MV drawing): trenches / duct banks "Medium Voltage Trench - Type 01", "TYPE - 04 - 5 X 1 Duct Bank" â†’ T1, T4;
            // manholes "... Manhole For Aircraft Loading - Type 02" â†’ ALMH-T2, "... Vehicular Loading - TYPE 02" â†’ VLMH-T2.
            string mvType = MvTypeNumber(value);
            if (mvType != null)
            {
                string v = value ?? "";
                if (Regex.IsMatch(v, @"manhole|\bMH\b", RegexOptions.IgnoreCase))
                {
                    string load = Regex.IsMatch(v, @"air\s*craft", RegexOptions.IgnoreCase) ? "ALMH" : Regex.IsMatch(v, @"vehic", RegexOptions.IgnoreCase) ? "VLMH" : "MH";
                    return $"{load}-T{mvType}";
                }
                return "T" + mvType;
            }
            // A code written as a word of its own ("Type:IC-3B" â†’ "IC3B"), not part of a size such as "6x4".
            Match m = Regex.Match(value ?? "", @"\b([A-Za-z]{1,4})[-_ ]?(\d+[A-Za-z]?)\b");
            if (m.Success) return (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant();
            // Else the short token at the end ("KSIA ICT 6x4 - 3B" â†’ "3B").
            Match tail = Regex.Match(value ?? "", @"([A-Za-z0-9]{1,6})[^A-Za-z0-9]*$");
            if (tail.Success && Regex.IsMatch(tail.Groups[1].Value, @"\d")) return tail.Groups[1].Value.ToUpperInvariant();
            return Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "");
        }

        /// <summary>
        /// The MV type number of a medium-voltage trench, duct bank or manhole text ("Type 01", "TYPE - 04", "TYPE-1", "T1"), or
        /// null: not MV, or a type written with a letter (ICT "Type-3B"), or two types at once ("Type-1 &amp; 2").
        /// </summary>
        public static string MvTypeNumber(string value)
        {
            string v = value ?? "";
            if (!Regex.IsMatch(v, @"medium\s*voltage|\bMV\b|trench|duct\s*bank", RegexOptions.IgnoreCase)) return null;
            Match m = Regex.Match(v, @"\bTYPE\s*[-:_]?\s*0?(\d{1,2})(?![\dA-Za-z])(?!\s*(?:&|and|/|,)\s*\d)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(v, @"(?<![A-Za-z])T0?(\d)(?![\dA-Za-z])(?!\s*(?:&|and|/|,)\s*\d)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
        }
    }
}
