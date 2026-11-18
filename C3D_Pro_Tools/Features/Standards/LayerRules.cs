using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools.Features.Standards
{
    /// <summary>One network part or label as the layer check sees it (plain data).</summary>
    internal sealed class LayerItem
    {
        public string Handle;
        public bool IsLabel;
        public string Kind;          // "pipe", "structure", or for a label "pipe label" / "structure label"
        public string Network;
        public string Name;
        public string Description;
        public string Size;
        public string Layer;
        public string LabelBase;     // labels: the drawing's labelling layer for this kind (no suffix)
    }

    /// <summary>A change the fix makes, or a part the check cannot place.</summary>
    internal sealed class LayerFinding
    {
        public LayerItem Item;
        public string Key;           // parts: the type key the rule is looked up by
        public string To;            // null: no rule for this type yet
        public string Why;
        /// <summary>The layer is only a template suggestion: the user ticks it, a command-line Fix skips it.</summary>
        public bool Suggested;
    }

    /// <summary>
    /// The layer rules of network parts and their labels (KSIA, 2026-11-18): a part's layer follows its type, read from its
    /// description ("Type:IC-3B  Size:ICT 6x4 way" -> IC-3B), the description being right when the two disagree; a label goes
    /// on the drawing's labelling layer of its kind without the per-network suffix; labels on other layers (the tile layers
    /// frozen per viewport) are left alone.
    /// </summary>
    internal static class LayerRules
    {
        private static readonly Regex TypeToken = new Regex(@"\bType\s*:\s*([A-Za-z0-9][A-Za-z0-9\-\.]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>The type a part's layer follows: the "Type:" code of its description, else the description, else its size.</summary>
        public static string TypeKey(string description, string size)
        {
            string d = (description ?? "").Trim();
            Match m = TypeToken.Match(d);
            if (m.Success) return "TYPE " + m.Groups[1].Value.ToUpperInvariant();
            if (d.Length > 0) return Spaces.Replace(d, " ").ToUpperInvariant();
            string s = (size ?? "").Trim();
            return s.Length > 0 && s != "?" ? "SIZE " + Spaces.Replace(s, " ").ToUpperInvariant() : "";
        }

        /// <summary>
        /// The key a part's rule is kept under: its type, scoped by service and state when they can be read from the network
        /// name, description and size ("[water ex] 500 MM DUCTILE IRON PIPE"), so water and fuel pipes sharing a catalogue
        /// description never share a layer (review finding).
        /// </summary>
        public static string ItemKey(LayerItem it)
        {
            string type = TypeKey(it.Description, it.Size);
            if (type.Length == 0) return "";
            List<string> words = Tokens(it.Network + " " + it.Description + " " + it.Size);
            string service = ServiceOf(words);
            if (service == null) return type;
            bool existing = words.Contains("ex") || words.Contains("exist") || words.Contains("existing");
            return $"[{service}{(existing ? " ex" : "")}] {type}";
        }

        /// <summary>The part's type without the scope: how rules saved before scoping (2026-11-18) are still found.</summary>
        public static string BareKey(string key) => key.StartsWith("[") && key.IndexOf("] ", StringComparison.Ordinal) > 0 ? key.Substring(key.IndexOf("] ", StringComparison.Ordinal) + 2) : key;

        /// <summary>True for a layer Civil 3D made per network: one of the base layers followed by "_" and the network name.</summary>
        public static bool IsSuffixLayer(string layer, IEnumerable<string> bases) =>
            !string.IsNullOrEmpty(layer) && bases.Any(b => !string.IsNullOrEmpty(b) && layer.Length > b.Length + 1 &&
                                                           layer.StartsWith(b + "_", StringComparison.OrdinalIgnoreCase));

        /// <summary>The KSIA layer name: Role-Uniclass code-Presentation-Description (CAD standard 5.6), e.g. IC-Ss_75_10_21_88-M-IctManhole_Type C.</summary>
        private static readonly Regex Standard = new Regex(@"^[A-Z]{1,2}-(?:Pr|Ss|EF|Zz|SL|En|Co|Ac|PM|FI|Ma|PC|RK|Ro|TE)(?:_\d{2}){1,5}-[A-Z]{1,2}-.+$", RegexOptions.CultureInvariant);

        public static bool IsStandardName(string layer) => !string.IsNullOrEmpty(layer) && Standard.IsMatch(layer);

        /// <summary>
        /// A layer a part may be taught from: a KSIA-named layer that is not a per-network one. Not 0, and not an old name such
        /// as C-STRM-PIPE (AGL ducts sat there in TEST DRAWING, 2026-11-18: learning from it put every such duct on a storm layer).
        /// </summary>
        public static bool Teaches(string layer, IEnumerable<string> partBases) => IsStandardName(layer) && !IsSuffixLayer(layer, partBases);

        /// <summary>For each type, the layer most of its parts are on (ties: the name first in order); per-network layers and 0 do not count.</summary>
        public static Dictionary<string, string> Learn(IEnumerable<LayerItem> parts, IEnumerable<string> partBases)
        {
            List<string> bases = partBases.ToList();
            return parts.Where(p => !p.IsLabel && Teaches(p.Layer, bases))
                        .Select(p => (key: ItemKey(p), p.Layer))
                        .Where(x => x.key.Length > 0)
                        .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase)
                                                        .OrderByDescending(l => l.Count()).ThenBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
                                                        .First().Key, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What is wrong: parts not on their type's layer (the saved rule first, else the learned one), parts with no rule that sit
        /// on 0 or a per-network layer, labels on a per-network labelling layer or on 0.
        /// </summary>
        public static List<LayerFinding> Check(IEnumerable<LayerItem> items, IReadOnlyDictionary<string, string> rules, IEnumerable<string> partBases, IEnumerable<string> labelBases,
            IEnumerable<string> keepLabelLayers = null, IReadOnlyDictionary<string, string> suggestions = null)
        {
            List<string> pb = partBases.ToList(), lb = labelBases.ToList();
            // Layers frozen in a viewport hold labels for one sheet part (the tile layers): never emptied (review finding).
            var keep = new HashSet<string>(keepLabelLayers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var found = new List<LayerFinding>();
            foreach (LayerItem it in items)
            {
                if (it.IsLabel)
                {
                    if (string.IsNullOrEmpty(it.LabelBase) || it.Layer.Equals(it.LabelBase, StringComparison.OrdinalIgnoreCase) || keep.Contains(it.Layer)) continue;
                    if (IsSuffixLayer(it.Layer, lb)) found.Add(new LayerFinding { Item = it, To = it.LabelBase, Why = "label on a per-network layer" });
                    else if (it.Layer == "0") found.Add(new LayerFinding { Item = it, To = it.LabelBase, Why = "label on layer 0" });
                    continue;
                }
                string key = ItemKey(it);
                if (key.Length > 0 && (rules.TryGetValue(key, out string to) || rules.TryGetValue(BareKey(key), out to)) && !string.IsNullOrEmpty(to))
                {
                    if (!it.Layer.Equals(to, StringComparison.OrdinalIgnoreCase))
                        found.Add(new LayerFinding { Item = it, Key = key, To = to, Why = it.Layer == "0" ? "on layer 0" : IsSuffixLayer(it.Layer, pb) ? "on a per-network layer" : "not on its type's layer" });
                }
                else if (!Teaches(it.Layer, pb))
                {
                    string where = it.Layer == "0" ? "on layer 0" : "on a per-network layer";
                    if (suggestions != null && key.Length > 0 && suggestions.TryGetValue(key, out string s) && !string.IsNullOrEmpty(s))
                        found.Add(new LayerFinding { Item = it, Key = key, To = s, Suggested = true, Why = where + "; template layer suggested" });
                    else found.Add(new LayerFinding { Item = it, Key = key, To = null, Why = where + ", and no layer is set for its type" });
                }
            }
            return found;
        }

        // Services as the KSIA template names them in its layer descriptions, and the words that point to each in network
        // names, descriptions and sizes. The first group whose word matches decides.
        private static readonly (string service, string[] words)[] Services =
        {
            // Specific services first: "FuelPowerDataDuct" is fuel, "MvCable" is MV; LV last, it takes any "cable".
            ("fuel", new[] { "fuel" }),
            ("gas", new[] { "gas" }),
            ("firemain", new[] { "fire", "firemain", "fw" }),
            ("water", new[] { "water", "potable", "pw" }),
            ("swdr", new[] { "swd", "swdr", "storm", "strm", "stormwater" }),
            ("fwdr", new[] { "fwd", "fwdr", "foul", "sewer", "sewage" }),
            ("chil", new[] { "chilled", "chw", "chil" }),
            ("agl", new[] { "agl", "aeronautical", "airfield", "met", "nvs", "nav", "navaid" }),
            ("mv", new[] { "mv" }),
            ("hv", new[] { "hv", "ehv" }),
            ("comm", new[] { "ict", "comm", "foc", "fiber", "fibre", "telecommunication", "telecom", "data" }),
            ("lv", new[] { "lv", "cable", "cables", "elec", "electric", "electrical" }),
        };
        private static readonly Regex Words = new Regex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", RegexOptions.CultureInvariant);

        /// <summary>The words of a text, lower case: "LvDuctProp-T5" -> lv, duct, prop, t, 5; "SWD_NCR" -> swd, ncr.</summary>
        public static List<string> Tokens(string text) =>
            Words.Matches(text ?? "").Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Select(w => w == "type" || w == "typ" ? "t" : w).ToList();  // "Type-3", "Typ3" and "T3" alike

        public static string ServiceOf(IEnumerable<string> tokens)
        {
            var set = new HashSet<string>(tokens);
            foreach ((string service, string[] words) in Services) if (words.Any(set.Contains)) return service;
            return null;
        }

        /// <summary>
        /// A template layer for a part type the drawing does not show: the layer of the same service (from the network name,
        /// description and size), the same kind (pipe or duct; manhole, pit or chamber) and the same state (existing or
        /// proposed), the one sharing most other words (a type number) first; null when the service is not recognised.
        /// Only offered, never applied without the user.
        /// </summary>
        public static string Suggest(string network, string description, string size, bool structure, IEnumerable<string> templateLayers)
        {
            List<string> words = Tokens(network + " " + description + " " + size);
            string service = ServiceOf(words);
            if (service == null) return null;
            bool existing = words.Contains("ex") || words.Contains("exist") || words.Contains("existing");
            var mine = new HashSet<string>(words);
            string best = null;
            int bestScore = int.MinValue;
            foreach (string layer in templateLayers)
            {
                // Only model layers: Role-Code-M-Description.
                string[] f = layer.Split(new[] { '-' }, 4);
                if (f.Length < 4 || f[2] != "M") continue;
                List<string> lw = Tokens(f[3]);
                if (ServiceOf(lw) != service) continue;
                bool lStruct = lw.Any(w => w == "manh" || w == "pit" || w == "chamber" || w == "man" || w == "manhole" || w == "box");
                bool lPipe = lw.Any(w => w == "pipe" || w == "duct" || w == "duc" || w == "cable" || w == "conduit");
                if (structure ? !lStruct : !lPipe) continue;
                bool lExist = lw.Contains("exist");
                int score = (lExist == existing ? 10 : 0) + lw.Count(mine.Contains) - lw.Count / 4;
                if (score > bestScore || score == bestScore && string.CompareOrdinal(layer, best) < 0) { best = layer; bestScore = score; }
            }
            return best;
        }

        /// <summary>The rules as saved in the drawing: one "type key TAB layer" per line.</summary>
        public static string Write(IReadOnlyDictionary<string, string> rules) =>
            string.Join("\n", rules.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "\t" + kv.Value));

        public static Dictionary<string, string> Read(string text)
        {
            var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (text ?? "").Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && tab < line.Length - 1) rules[line.Substring(0, tab).Trim()] = line.Substring(tab + 1).Trim();
            }
            return rules;
        }
    }
}
