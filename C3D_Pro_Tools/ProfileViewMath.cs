using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// Profile view arithmetic: station text, datum rounding, part names from a pattern and where a route enters and leaves
// zones. No AutoCAD / Civil 3D types, so it is tested without Civil 3D.
namespace C3DProTools
{
    internal static class ProfileViewMath
    {
        /// <summary>
        /// Station text such as 1+250 or 0+050.00, with the sign in front for stations before zero (-0+050, not 0+-50). The
        /// value is rounded first, so 999.96 with one decimal is 1+000.0, not 0+1000.0.
        /// </summary>
        public static string Station(double s, int decimals = 0)
        {
            decimals = Math.Max(0, Math.Min(6, decimals));
            double rounded = Math.Round(Math.Abs(s), decimals, MidpointRounding.AwayFromZero);
            double km = Math.Floor(rounded / 1000.0 + 1e-12);
            double rest = Math.Max(0, rounded - km * 1000.0);
            string fmt = decimals == 0 ? "000" : "000." + new string('0', decimals);
            string sign = s < 0 && rounded > 0 ? "-" : "";
            return sign + km.ToString("0", CultureInfo.InvariantCulture) + "+" + rest.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// An elevation range for a view: the lowest and highest levels shown, each <paramref name="margin"/> further out and
        /// rounded outward to a whole <paramref name="increment"/> (1, 2, 5 m...); at least one increment high.
        /// </summary>
        public static (double low, double high) DatumRange(double lo, double hi, double margin, double increment)
        {
            double inc = increment > 0 ? increment : 1.0;
            double low = Math.Floor((lo - margin) / inc + 1e-9) * inc, high = Math.Ceiling((hi + margin) / inc - 1e-9) * inc;
            if (high - low < inc - 1e-9) high = low + inc;
            return (Math.Round(low, 6), Math.Round(high, 6));
        }

        private static readonly Regex Token = new Regex(@"\{(?<name>[A-Za-z]+)(?::(?<format>[^}]*))?\}", RegexOptions.Compiled);

        /// <summary>
        /// A name from a pattern of tokens, each written {Token} or {Token:format} (e.g. {SheetNo:000}). Token names are not case
        /// sensitive; a token not in <paramref name="values"/> is left as typed. Numbers take .NET number formats; text ignores the format.
        /// </summary>
        public static string Name(string pattern, IReadOnlyDictionary<string, object> values)
        {
            Dictionary<string, object> byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> kv in values) byName[kv.Key] = kv.Value;
            return Token.Replace(pattern ?? "", m =>
            {
                if (!byName.TryGetValue(m.Groups["name"].Value, out object v) || v == null) return m.Value;
                string format = m.Groups["format"].Success ? m.Groups["format"].Value : null;
                if (format != null && v is IFormattable f)
                    try { return f.ToString(format, CultureInfo.InvariantCulture); } catch (FormatException) { }
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            });
        }

        /// <summary>
        /// The token values of a split part: {view}/{ViewName}, {n}/{part}/{PartIndex}, {from}/{StartSta}, {to}/{EndSta},
        /// {alignment}/{AlignmentName} and {sheet}/{SheetNo} (the first sheet number plus the part's place).
        /// </summary>
        public static Dictionary<string, object> SplitTokens(string view, string alignment, int n, string from, string to, int firstSheet)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["view"] = view, ["ViewName"] = view,
                ["n"] = n, ["part"] = n, ["PartIndex"] = n,
                ["from"] = from, ["StartSta"] = from, ["to"] = to, ["EndSta"] = to,
                ["alignment"] = alignment, ["AlignmentName"] = alignment,
                ["sheet"] = firstSheet + n - 1, ["SheetNo"] = firstSheet + n - 1
            };
        }

        /// <summary>
        /// The station ranges of a path inside any of the plan polygons: sampled every <paramref name="step"/> from
        /// <paramref name="s0"/> to <paramref name="s1"/>, each entry and exit then found to within a millimetre. Ranges of
        /// overlapping polygons are merged.
        /// </summary>
        public static List<(double s0, double s1)> ZoneRanges(double s0, double s1, double step, Func<double, (double X, double Y)> at,
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> polygons)
        {
            List<(double, double)> ranges = new List<(double, double)>();
            if (polygons.Count == 0 || s1 <= s0) return ranges;
            step = step > 0 ? step : 1.0;
            bool Inside(double s)
            {
                (double x, double y) = at(s);
                foreach (IReadOnlyList<(double X, double Y)> poly in polygons)
                    if (poly.Count >= 3 && CrossingGeometry.Inside(poly, x, y)) return true;
                return false;
            }
            double Edge(double a, double b, bool aInside)
            {
                for (int i = 0; i < 60 && b - a > 0.001; i++)
                {
                    double m = (a + b) / 2;
                    if (Inside(m) == aInside) a = m; else b = m;
                }
                return (a + b) / 2;
            }
            double prev = s0;
            bool inside = Inside(s0);
            double start = inside ? s0 : double.NaN;
            int count = (int)Math.Ceiling((s1 - s0) / step);
            for (int i = 1; i <= count; i++)
            {
                double s = Math.Min(s1, s0 + i * step);
                bool now = Inside(s);
                if (now != inside)
                {
                    double edge = Edge(prev, s, inside);
                    if (now) start = edge;
                    else ranges.Add((start, edge));
                    inside = now;
                }
                prev = s;
            }
            if (inside) ranges.Add((start, s1));
            return ranges;
        }
        // Band style names: which profiles a data band is meant to show. Roles as ints: 0 none, 1 existing, 2 proposed, 3 top.
        private static bool Word(string name, string pattern) =>
            Regex.IsMatch(name ?? "", $@"(^|[^A-Za-z])({pattern})([^A-Za-z]|$)", RegexOptions.IgnoreCase);

        /// <summary>
        /// The profiles a profile data band shows, told by its style name: "Existing Levels", "NGL", "EG" the existing ground;
        /// "Proposed Levels", "FGL", "Design" the proposed ground; "Top of Duct", "TOP", "TOD" the top of pipe / duct. A
        /// difference band ("Level Difference", "Depth", "Cover", "Cut / Fill") compares the ground (existing when its name
        /// says so, else proposed) as profile 1 with the top as profile 2. Null for a name that says none of these (chainage,
        /// title), whose profiles are left as recorded.
        /// </summary>
        public static (int p1, int p2)? BandRolesFromName(string styleName)
        {
            bool existing = Word(styleName, "EXISTING|EXIST|NGL|EGL|EG|OGL|NATURAL");
            bool proposed = Word(styleName, "PROPOSED|PROP|FGL|FG|DESIGN|FINISHED");
            bool top = Word(styleName, "TOP|TOD|DUCT|DUCTS|PIPE|PIPES");
            bool difference = Word(styleName, "DIFFERENCE|DIFF|DEPTH|COVER|CUT|FILL|CUT/FILL");
            if (difference) return (existing && !proposed ? 1 : 2, 3);
            if (existing && !proposed && !top) return (1, 1);
            if (proposed && !existing && !top) return (2, 2);
            if (top && !existing && !proposed) return (3, 3);
            return null;
        }
    }
}
