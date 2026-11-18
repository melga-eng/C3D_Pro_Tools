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

        /// <summary>True for a layer Civil 3D made per network: one of the base layers followed by "_" and the network name.</summary>
        public static bool IsSuffixLayer(string layer, IEnumerable<string> bases) =>
            !string.IsNullOrEmpty(layer) && bases.Any(b => !string.IsNullOrEmpty(b) && layer.Length > b.Length + 1 &&
                                                           layer.StartsWith(b + "_", StringComparison.OrdinalIgnoreCase));

        /// <summary>A layer a part may be taught from: not 0, not Defpoints, not a per-network layer.</summary>
        public static bool Teaches(string layer, IEnumerable<string> partBases) =>
            !string.IsNullOrEmpty(layer) && layer != "0" && !layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) && !IsSuffixLayer(layer, partBases);

        /// <summary>For each type, the layer most of its parts are on (ties: the name first in order); per-network layers and 0 do not count.</summary>
        public static Dictionary<string, string> Learn(IEnumerable<LayerItem> parts, IEnumerable<string> partBases)
        {
            List<string> bases = partBases.ToList();
            return parts.Where(p => !p.IsLabel && Teaches(p.Layer, bases))
                        .Select(p => (key: TypeKey(p.Description, p.Size), p.Layer))
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
            IEnumerable<string> keepLabelLayers = null)
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
                string key = TypeKey(it.Description, it.Size);
                if (key.Length > 0 && rules.TryGetValue(key, out string to) && !string.IsNullOrEmpty(to))
                {
                    if (!it.Layer.Equals(to, StringComparison.OrdinalIgnoreCase))
                        found.Add(new LayerFinding { Item = it, Key = key, To = to, Why = it.Layer == "0" ? "on layer 0" : IsSuffixLayer(it.Layer, pb) ? "on a per-network layer" : "not on its type's layer" });
                }
                else if (!Teaches(it.Layer, pb))
                    found.Add(new LayerFinding { Item = it, Key = key, To = null, Why = (it.Layer == "0" ? "on layer 0" : "on a per-network layer") + ", and no layer is set for its type" });
            }
            return found;
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
