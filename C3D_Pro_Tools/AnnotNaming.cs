using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    internal enum SheetSource { NetworkName, SheetText, Fixed }
    internal enum TypeSource { Style, PartSize, Family, Description }
    internal enum ParallelOrder { RouteByRoute, SetBySet }

    /// <summary>How parts are named: templates with tokens, where the sheet number and type come from, and the order of numbering.</summary>
    internal sealed class NamingOptions
    {
        /// <summary>Tokens: {sheet}, {type}, {network}, {n} (or {n:00} / {n:000} for padding).</summary>
        public string PipeTemplate { get; set; } = "IC-{sheet}-{type}-{n:00}";
        public string StructureTemplate { get; set; } = "ICT-STR-{type}_{n}";
        public SheetSource Sheet { get; set; } = SheetSource.NetworkName;
        public string SheetPattern { get; set; } = @"SHEET[-\s]*(\d+)";
        public string FixedSheet { get; set; } = "";
        public TypeSource PipeType { get; set; } = TypeSource.Style;
        public TypeSource StructureType { get; set; } = TypeSource.Family;
        /// <summary>Type values as found (style name, size…) → the code written in names.</summary>
        public Dictionary<string, string> TypeCodes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ParallelOrder Parallel { get; set; } = ParallelOrder.RouteByRoute;
        public bool Pipes { get; set; } = true;
        public bool Structures { get; set; } = true;
    }

    internal sealed class RenameRow
    {
        public ObjectId Id;
        public bool IsPipe;
        public string Network = "", Sheet = "", Type = "", Old = "", New = "";
        public int Route;
        public string Note = "";
        /// <summary>The new name cannot be used (taken in the network).</summary>
        public bool Clash;
    }

    /// <summary>
    /// Numbers network parts along their routes: each network is split into routes (parallel pipes kept apart), pipes are
    /// numbered route by route or set by set, each distinct name prefix (sheet, type…) counting from 1 again.
    /// </summary>
    internal static class AnnotNaming
    {
        public static string TypeValue(Transaction tr, Part part, TypeSource source)
        {
            try
            {
                switch (source)
                {
                    case TypeSource.Style: return part.StyleName ?? "";
                    case TypeSource.PartSize: return part.PartSizeName ?? "";
                    case TypeSource.Family: return tr.GetObject(part.PartFamilyId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.PartFamily f ? f.Name : "";
                    default: return part.Description ?? "";
                }
            }
            catch (Exception) { return ""; }
        }

        /// <summary>The distinct type values of the networks' parts (to fill the code table).</summary>
        public static SortedSet<string> TypeValues(Transaction tr, IEnumerable<ObjectId> networkIds, NamingOptions o)
        {
            SortedSet<string> values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId netId in networkIds)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                if (o.Pipes) foreach (ObjectId id in net.GetPipeIds()) values.Add(TypeValue(tr, (Part)tr.GetObject(id, OpenMode.ForRead), o.PipeType));
                if (o.Structures) foreach (ObjectId id in net.GetStructureIds()) values.Add(TypeValue(tr, (Part)tr.GetObject(id, OpenMode.ForRead), o.StructureType));
            }
            values.Remove("");
            return values;
        }

        /// <summary>A short code suggested for a type value: the part after the last "-"/"_" with letters and digits (e.g. "Duct IC-3B" → "IC3B").</summary>
        public static string SuggestCode(string value)
        {
            // A code written as a word of its own ("Type:IC-3B" → "IC3B"), not part of a size such as "6x4".
            Match m = Regex.Match(value ?? "", @"\b([A-Za-z]{1,4})[-_ ]?(\d+[A-Za-z]?)\b");
            if (m.Success) return (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant();
            // Else the short token at the end ("KSIA ICT 6x4 - 3B" → "3B").
            Match tail = Regex.Match(value ?? "", @"([A-Za-z0-9]{1,6})[^A-Za-z0-9]*$");
            if (tail.Success && Regex.IsMatch(tail.Groups[1].Value, @"\d")) return tail.Groups[1].Value.ToUpperInvariant();
            return Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "");
        }

        /// <param name="only">When given, only these parts are renamed (numbered among themselves, in route order).</param>
        public static List<RenameRow> Plan(Transaction tr, IEnumerable<ObjectId> networkIds, NamingOptions o, List<SheetInfo> sheets, double gap, double centerTolerance,
            ICollection<ObjectId> only = null)
        {
            List<RenameRow> rows = new List<RenameRow>();
            NameTemplate pipeTemplate = NameTemplate.Parse(o.PipeTemplate), structureTemplate = NameTemplate.Parse(o.StructureTemplate);
            Dictionary<string, int> counters = new Dictionary<string, int>();
            Dictionary<SheetInfo, string> sheetNumbers = new Dictionary<SheetInfo, string>();
            foreach (ObjectId netId in networkIds)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                bool reference = net.IsReferenceObject;
                string netSheet = Regex.Match(net.Name, @"(\d+)(?!.*\d)").Value;
                List<ObjectId> pipeIds = net.GetPipeIds().Cast<ObjectId>().ToList();

                // Routes in flow direction (most pipes); main lines first.
                List<CoverJob> routes = pipeIds.Count == 0 ? new List<CoverJob>() : CoverJobs.FromPipes(tr, pipeIds, gap, centerTolerance, true, net.Name);
                foreach (CoverJob r in routes)
                {
                    int forward = r.Pipes.Count(p => p.forward);
                    if (r.Pipes.Count - forward > forward)
                    {
                        r.Pipes.Reverse();
                        for (int i = 0; i < r.Pipes.Count; i++) r.Pipes[i] = (r.Pipes[i].id, !r.Pipes[i].forward);
                    }
                }
                List<ObjectId> pipeOrder = o.Parallel == ParallelOrder.RouteByRoute
                    ? routes.SelectMany(r => r.Pipes.Select(p => p.id)).ToList()
                    : SetBySet(tr, routes);

                string SheetOf(Point3d p)
                {
                    switch (o.Sheet)
                    {
                        case SheetSource.Fixed: return o.FixedSheet ?? "";
                        case SheetSource.NetworkName: return netSheet;
                        default:
                            // Where sheets overlap at their match lines, the sheet showing the point furthest inside its viewport.
                            string best = "";
                            double depth = double.MinValue;
                            foreach (SheetInfo s in sheets)
                            {
                                if (!sheetNumbers.TryGetValue(s, out string n)) sheetNumbers[s] = n = s.SheetNumber(o.SheetPattern);
                                if (string.IsNullOrEmpty(n)) continue;
                                foreach (SheetViewport v in s.Viewports)
                                {
                                    if (!v.Sees(p)) continue;
                                    double d = Geometry2d.DistanceToPolygon(v.Outline, v.ToPaper(p));
                                    if (d > depth) { depth = d; best = n; }
                                }
                            }
                            return best;
                    }
                }

                string Code(string value) => o.TypeCodes.TryGetValue(value ?? "", out string c) && !string.IsNullOrWhiteSpace(c) ? c.Trim() : SuggestCode(value);

                if (o.Pipes)
                    foreach (ObjectId id in pipeOrder)
                    {
                        if (only != null && !only.Contains(id)) continue;
                        Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                        Point3d mid = new Point3d((p.StartPoint.X + p.EndPoint.X) / 2, (p.StartPoint.Y + p.EndPoint.Y) / 2, 0);
                        RenameRow row = new RenameRow { Id = id, IsPipe = true, Network = net.Name, Old = p.Name, Sheet = SheetOf(mid), Type = Code(TypeValue(tr, p, o.PipeType)) };
                        row.Route = routes.FindIndex(r => r.Pipes.Any(x => x.id == id)) + 1;
                        row.New = pipeTemplate.Errors.Count > 0 ? row.Old : Render(pipeTemplate, row, net.Name, counters);
                        if (pipeTemplate.Errors.Count > 0) row.Note = "Pipe template: " + pipeTemplate.Errors[0];
                        if (reference) row.Note = "Data reference: rename it in its source drawing";
                        rows.Add(row);
                    }
                if (o.Structures)
                {
                    // Structures in the order the routes reach them; any not on a route after them, by position.
                    List<ObjectId> order = new List<ObjectId>();
                    foreach (ObjectId pid in pipeOrder)
                    {
                        Pipe p = (Pipe)tr.GetObject(pid, OpenMode.ForRead);
                        CoverJob route = routes.FirstOrDefault(r => r.Pipes.Any(x => x.id == pid));
                        bool forward = route?.Pipes.First(x => x.id == pid).forward ?? true;
                        foreach (ObjectId s in forward ? new[] { p.StartStructureId, p.EndStructureId } : new[] { p.EndStructureId, p.StartStructureId })
                            if (!s.IsNull && !order.Contains(s)) order.Add(s);
                    }
                    foreach (ObjectId s in net.GetStructureIds().Cast<ObjectId>()
                                 .OrderBy(s => ((Structure)tr.GetObject(s, OpenMode.ForRead)).Location.X).ThenBy(s => ((Structure)tr.GetObject(s, OpenMode.ForRead)).Location.Y))
                        if (!order.Contains(s)) order.Add(s);
                    foreach (ObjectId id in order)
                    {
                        if (only != null && !only.Contains(id)) continue;
                        Structure st = (Structure)tr.GetObject(id, OpenMode.ForRead);
                        RenameRow row = new RenameRow { Id = id, IsPipe = false, Network = net.Name, Old = st.Name, Sheet = SheetOf(st.Location), Type = Code(TypeValue(tr, st, o.StructureType)) };
                        row.New = structureTemplate.Errors.Count > 0 ? row.Old : Render(structureTemplate, row, net.Name, counters);
                        if (structureTemplate.Errors.Count > 0) row.Note = "Structure template: " + structureTemplate.Errors[0];
                        if (reference) row.Note = "Data reference: rename it in its source drawing";
                        rows.Add(row);
                    }
                }
            }
            CheckNames(rows, KeptNames(tr, networkIds, rows));
            return rows;
        }

        /// <summary>
        /// Set by set: pipes running side by side are numbered together. Each pipe is placed along the main (longest) route by its
        /// middle point; pipes at about the same place along it (within half their length) form a set, numbered across the routes.
        /// </summary>
        private static List<ObjectId> SetBySet(Transaction tr, List<CoverJob> routes)
        {
            if (routes.Count == 0) return new List<ObjectId>();
            List<Point3d> axis = new List<Point3d>();
            foreach ((ObjectId id, bool forward) in routes[0].Pipes)
            {
                Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                if (axis.Count == 0) axis.Add(forward ? p.StartPoint : p.EndPoint);
                axis.Add(forward ? p.EndPoint : p.StartPoint);
            }
            (double station, double offset) Place(Point3d q)
            {
                double best = double.MaxValue, bestStation = 0, bestOffset = 0, along = 0;
                for (int i = 1; i < axis.Count; i++)
                {
                    Vector2d seg = new Vector2d(axis[i].X - axis[i - 1].X, axis[i].Y - axis[i - 1].Y);
                    double len = seg.Length;
                    if (len < 1e-9) continue;
                    Vector2d u = seg / len, w = new Vector2d(q.X - axis[i - 1].X, q.Y - axis[i - 1].Y);
                    double t = Math.Max(0, Math.Min(len, w.DotProduct(u)));
                    Point2d foot = new Point2d(axis[i - 1].X, axis[i - 1].Y) + u * t;
                    double d = foot.GetDistanceTo(new Point2d(q.X, q.Y));
                    if (d < best) { best = d; bestStation = along + t; bestOffset = u.X * w.Y - u.Y * w.X; }
                    along += len;
                }
                return (bestStation, bestOffset);
            }
            var placed = new List<(ObjectId id, double station, double offset, double length, int route)>();
            for (int r = 0; r < routes.Count; r++)
                foreach ((ObjectId id, bool _) in routes[r].Pipes)
                {
                    Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                    Point3d mid = new Point3d((p.StartPoint.X + p.EndPoint.X) / 2, (p.StartPoint.Y + p.EndPoint.Y) / 2, 0);
                    (double st, double off) = Place(mid);
                    placed.Add((id, st, off, new Point2d(p.StartPoint.X, p.StartPoint.Y).GetDistanceTo(new Point2d(p.EndPoint.X, p.EndPoint.Y)), r));
                }
            List<ObjectId> order = new List<ObjectId>();
            HashSet<ObjectId> used = new HashSet<ObjectId>();
            foreach (var first in placed.OrderBy(x => x.station))
            {
                if (used.Contains(first.id)) continue;
                // The set: one pipe per route near this place, across the routes from left to right.
                var set = placed.Where(x => !used.Contains(x.id) && Math.Abs(x.station - first.station) <= Math.Max(1.0, first.length / 2))
                                .GroupBy(x => x.route).Select(g => g.OrderBy(x => Math.Abs(x.station - first.station)).First())
                                .OrderByDescending(x => x.offset).ToList();
                foreach (var x in set) { order.Add(x.id); used.Add(x.id); }
            }
            return order;
        }

        /// <summary>The name from the template; the counter is kept per distinct prefix, so each sheet and type starts at 1.</summary>
        private static string Render(NameTemplate template, RenameRow row, string network, Dictionary<string, int> counters)
        {
            string Value(string token) => token == "sheet" ? row.Sheet : token == "type" ? row.Type : token == "network" ? network : "";
            string key = (row.IsPipe ? "P|" : "S|") + network + "|" + template.Render(Value, null);
            counters.TryGetValue(key, out int count);
            counters[key] = ++count;
            return template.Render(Value, count);
        }
        /// <summary>Per network, the names of its parts that are not being renamed: no new name may take one of them.</summary>
        public static Dictionary<string, HashSet<string>> KeptNames(Transaction tr, IEnumerable<ObjectId> networkIds, List<RenameRow> rows)
        {
            HashSet<ObjectId> planned = new HashSet<ObjectId>(rows.Where(r => r.New != r.Old).Select(r => r.Id));
            Dictionary<string, HashSet<string>> kept = new Dictionary<string, HashSet<string>>();
            foreach (ObjectId netId in networkIds)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>()))
                    if (!planned.Contains(id) && tr.GetObject(id, OpenMode.ForRead) is Part p) names.Add(p.Name);
                kept[net.Name] = names;
            }
            return kept;
        }

        /// <summary>
        /// Marks names that cannot be used: the same new name twice in a network, or a name another part of the network keeps.
        /// The same name in different networks is allowed by Civil 3D but noted.
        /// </summary>
        public static void CheckNames(List<RenameRow> rows, Dictionary<string, HashSet<string>> keptNames)
        {
            foreach (RenameRow r in rows)
            {
                r.Clash = false;
                r.Note = string.Join("; ", (r.Note ?? "").Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries).Where(n => !CheckNotes.Contains(n)));
            }
            foreach (var g in rows.Where(r => r.New != r.Old).GroupBy(r => r.Network + "|" + r.New, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (RenameRow r in g) { r.Clash = true; r.Note = AddNote(r.Note, CheckNotes[0]); }
            foreach (RenameRow r in rows)
                if (r.New != r.Old && keptNames != null && keptNames.TryGetValue(r.Network, out HashSet<string> kept) && kept.Contains(r.New))
                { r.Clash = true; r.Note = AddNote(r.Note, CheckNotes[1]); }
            foreach (var g in rows.Where(r => r.New != r.Old && !r.Clash).GroupBy(r => r.New, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(r => r.Network).Distinct().Count() > 1))
                foreach (RenameRow r in g) r.Note = AddNote(r.Note, CheckNotes[2]);
        }

        private static readonly string[] CheckNotes = { "Same new name as another part of the network", "Another part of the network already has this name", "Same name in another network" };

        private static string AddNote(string note, string add) => string.IsNullOrEmpty(note) ? add : note.Contains(add) ? note : note + "; " + add;

        /// <summary>Renames in two steps (temporary names first), so a new name that another part still holds never clashes.</summary>
        /// <summary>
        /// Renames the rows in two passes (all to temporary names first, so names can swap). All or nothing: when any row
        /// fails, <c>complete</c> is false and the caller must abort the transaction, so no part is ever left with a
        /// temporary name.
        /// </summary>
        public static (int renamed, int failed, List<string> errors, bool complete) Apply(Transaction tr, List<RenameRow> rows)
        {
            List<string> errors = new List<string>();
            List<RenameRow> todo = rows.Where(r => r.New != r.Old && !r.Clash && !string.IsNullOrWhiteSpace(r.New) && !r.Note.StartsWith("Data reference")).ToList();
            int i = 0;
            foreach (RenameRow r in todo)
            {
                try { ((Part)tr.GetObject(r.Id, OpenMode.ForWrite)).Name = $"~c3dpro-{Guid.NewGuid():N}".Substring(0, 20) + (i++); }
                catch (Exception ex) { errors.Add($"{r.Old}: {ex.Message}"); }
            }
            if (errors.Count > 0) return (0, errors.Count, errors, false);

            int renamed = 0;
            foreach (RenameRow r in todo)
            {
                try
                {
                    ((Part)tr.GetObject(r.Id, OpenMode.ForWrite)).Name = r.New;
                    renamed++;
                }
                catch (Exception ex) { errors.Add($"{r.Old} → {r.New}: {ex.Message}"); }
            }
            return (renamed, errors.Count, errors, errors.Count == 0);
        }
    }

    /// <summary>
    /// A parsed name template: text with {sheet}, {type}, {network} and {n} tokens. Spaces inside the braces and any letter case
    /// are accepted; {n:2} or {n:00} gives two digits. {{ and }} write a brace. A token with no value removes the separator
    /// (-, _, . or space) in front of it, so an empty sheet or type never leaves "--".
    /// </summary>
    internal sealed class NameTemplate
    {
        private readonly List<(string text, string token, int pad)> _parts = new List<(string, string, int)>();
        public readonly List<string> Errors = new List<string>();
        public bool HasNumber => _parts.Any(p => p.token == "n");

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet"] = "sheet", ["type"] = "type", ["network"] = "network", ["n"] = "n", ["num"] = "n", ["number"] = "n", ["no"] = "n"
        };

        public static NameTemplate Parse(string text)
        {
            NameTemplate t = new NameTemplate();
            text = text ?? "";
            StringBuilder literal = new StringBuilder();
            void Flush() { if (literal.Length > 0) { t._parts.Add((literal.ToString(), null, 0)); literal.Clear(); } }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { literal.Append('{'); i++; continue; }
                if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { literal.Append('}'); i++; continue; }
                if (c == '}') { t.Errors.Add($"\"}}\" at position {i + 1} has no \"{{\" (write }}}} for a brace)"); continue; }
                if (c != '{') { literal.Append(c); continue; }
                int close = text.IndexOf('}', i + 1);
                int nextOpen = text.IndexOf('{', i + 1);
                if (close < 0 || (nextOpen >= 0 && nextOpen < close)) { t.Errors.Add($"\"{{\" at position {i + 1} is not closed"); continue; }
                string inner = text.Substring(i + 1, close - i - 1);
                i = close;
                int colon = inner.IndexOf(':');
                string name = (colon < 0 ? inner : inner.Substring(0, colon)).Trim();
                string format = colon < 0 ? "" : inner.Substring(colon + 1).Trim();
                if (!Names.TryGetValue(name, out string token)) { t.Errors.Add($"{{{inner}}} is not a token (use {{sheet}}, {{type}}, {{network}} or {{n}})"); continue; }
                int pad = 1;
                if (format.Length > 0)
                {
                    if (token != "n") { t.Errors.Add($"Only {{n}} takes a format, not {{{inner}}}"); continue; }
                    if (format.All(ch => ch == '0' || ch == '#')) pad = format.Length;
                    else if (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out pad) || pad < 1 || pad > 9)
                    { t.Errors.Add($"{{{inner}}}: write the digits as {{n:2}} or {{n:00}}"); continue; }
                }
                Flush();
                t._parts.Add((null, token, pad));
            }
            Flush();
            if (t._parts.Count == 0) t.Errors.Add("The template is empty");
            return t;
        }

        /// <summary>The name; <paramref name="number"/> null leaves the number out (used as the counting key).</summary>
        public string Render(Func<string, string> value, int? number)
        {
            List<string> pieces = new List<string>();
            bool dropNextSeparator = false;
            foreach ((string text, string token, int pad) in _parts)
            {
                if (token == null)
                {
                    string s = text;
                    if (dropNextSeparator && s.Length > 0 && IsSeparator(s[0])) s = s.Substring(1);
                    dropNextSeparator = false;
                    pieces.Add(s);
                    continue;
                }
                string v = token == "n" ? (number.HasValue ? number.Value.ToString(new string('0', pad), CultureInfo.InvariantCulture) : "\u0001") : (value(token) ?? "").Trim();
                if (v.Length == 0)
                {
                    // Take the separator in front with it; at the start, the one after.
                    int last = pieces.Count - 1;
                    if (last >= 0 && pieces[last].Length > 0 && IsSeparator(pieces[last][pieces[last].Length - 1])) pieces[last] = pieces[last].Substring(0, pieces[last].Length - 1);
                    else if (pieces.All(p => p.Length == 0)) dropNextSeparator = true;
                    continue;
                }
                pieces.Add(v);
            }
            return string.Concat(pieces).Trim();
        }

        private static bool IsSeparator(char c) => c == '-' || c == '_' || c == '.' || c == ' ';
    }}
