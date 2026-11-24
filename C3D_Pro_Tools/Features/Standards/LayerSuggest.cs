using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools.Features.Standards
{
    /// <summary>Plain layer-name checks and suggestions; meaning matches always need a user's choice.</summary>
    internal static class LayerSuggest
    {
        private static readonly Regex Standard = new Regex(@"^[A-Z]{1,2}-(?:Pr|Ss|EF|Zz|SL|En|Co|Ac|PM|FI|Ma|PC|RK|Ro|TE)(?:_\d{2}){1,5}-[A-Z]{1,2}[-_](?<description>.+)\z", RegexOptions.CultureInvariant);
        private static readonly Regex Bound = new Regex(@"\$\d+\$", RegexOptions.CultureInvariant);
        private static readonly Regex Aia = new Regex(@"^[A-Z]{1,2}-(?<words>[A-Z]{3,}(?:-[A-Z0-9]+)*)\z", RegexOptions.CultureInvariant);
        private static readonly Regex Words = new Regex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", RegexOptions.CultureInvariant);

        // Each row is one meaning, including both AIA and KSIA description abbreviations.
        private static readonly string[] Meanings =
        {
            "annotation anno", "table tabl", "text txt", "title titl ttl ttlb ttbl",
            "road", "centre center cntr cl cline", "property parcel prcl",
            "bearing brng", "assembly assm", "corridor corr", "building bldg",
            "footprint fprt", "hatch hatching pattern patt fill", "dimension dimensions dim dims",
            "mark marks marking", "site", "utility utilities util", "label labels labl",
            "boundary bndy bdy", "profile prof", "section sect sctn", "surface surf tinn topo",
            "point points pnt pnts", "line", "grid", "pipe", "duct duc", "water watr",
            "storm strm swdr", "structure strc stru", "fence fnce", "symbol symbols symb"
        };
        private static readonly Dictionary<string, string> Aliases = Meanings
            .Select(row => row.Split(' ')).SelectMany(row => row.Select(word => (word, meaning: row[0])))
            .ToDictionary(x => x.word, x => x.meaning, StringComparer.Ordinal);

        public static bool IsStandard(string name, ICollection<string> template) =>
            !string.IsNullOrEmpty(name) && name != "0" && !name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) &&
            (Contains(template, name) || Standard.IsMatch(name) ||
             // Bound xref layers kept as XREF_[layer name] (your rule, 2026-11-24).
             (name.StartsWith(NamePrefix.XrefLayer, StringComparison.OrdinalIgnoreCase) && name.Length > NamePrefix.XrefLayer.Length && !Bound.IsMatch(name)));

        public sealed class Suggestion
        {
            public string Layer;
            public string Target;
            public bool Sure;
            public string Why;
        }

        public static Suggestion Suggest(string layer, ICollection<string> template, ICollection<string> drawingLayers)
        {
            string inner = layer ?? "";
            MatchCollection bound = Bound.Matches(inner);
            bool isBound = bound.Count > 0;
            if (isBound)
            {
                // A bound xref layer is named XREF_[its layer name] (your rule, 2026-11-24).
                string xref = NamePrefix.XrefLayerName(layer);
                if (xref != null) return new Suggestion { Layer = layer, Target = xref, Sure = true, Why = "bound xref layer: XREF_ and its own name" };
            }

            string target = MeaningMatch(inner, layer, template);
            return new Suggestion
            {
                Layer = layer,
                Target = target,
                Sure = false,
                Why = (isBound ? "bound xref layer: " : "") + (target == null ? "no clear match" : "template layer with matching meaning")
            };
        }

        private static bool Contains(ICollection<string> layers, string name) =>
            layers != null && layers.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

        private static HashSet<string> Tokens(string text, bool aia = false) => new HashSet<string>(Words.Matches(text)
            .Cast<Match>().Select(m => m.Value.ToLowerInvariant())
            // PROP is property in AIA; in KSIA descriptions (BldgProp) it means proposed.
            .Select(w => aia && w == "prop" ? "property" : Aliases.TryGetValue(w, out string meaning) ? meaning : w)
            .Where(w => w != "annotation"), StringComparer.Ordinal);

        private static string MeaningMatch(string name, string original, ICollection<string> template)
        {
            Match aia = Aia.Match(name);
            if (!aia.Success || template == null) return null;
            HashSet<string> source = Tokens(aia.Groups["words"].Value, aia: true);
            if (source.Count == 0) return null;
            string best = null;
            double bestScore = 0.4;
            foreach (string candidate in template)
            {
                if (string.IsNullOrEmpty(candidate) || candidate == "0" || candidate.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals(original, StringComparison.OrdinalIgnoreCase) || candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                // Compare descriptions only: role, classification numbers and presentation are not meanings.
                Match form = Standard.Match(candidate);
                string description = form.Success ? form.Groups["description"].Value :
                    Aia.IsMatch(candidate) ? candidate.Substring(candidate.IndexOf('-') + 1) : candidate;
                HashSet<string> words = Tokens(description, aia: !form.Success && Aia.IsMatch(candidate));
                int overlap = source.Count(words.Contains);
                // At least half the source meanings must agree; generic annotation alone never scores.
                if (overlap == 0 || overlap * 2 < source.Count) continue;
                // ROAD is broad context; it must not outweigh a specific meaning such as corridor or centre.
                double Weight(string word) => word == "road" ? 0.25 : 1.0;
                double score = 2.0 * source.Where(words.Contains).Sum(Weight) / (source.Sum(Weight) + words.Sum(Weight));
                if (score > bestScore || best != null && score == bestScore && string.CompareOrdinal(candidate, best) < 0)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }
    }
}
