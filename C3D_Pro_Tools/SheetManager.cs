using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Group = System.Text.RegularExpressions.Group;

namespace C3DProTools
{
    /// <summary>One sheet (paper layout) and everything that carries its identity.</summary>
    internal sealed class SheetRecord
    {
        public ObjectId LayoutId, BlockId;
        public string Layout = "";
        public int Tab;
        public string Tile = "", Part = "";
        /// <summary>The group the user put the sheet in, for their own reference; empty when none (kept on the layout, see SheetGroups).</summary>
        public string Group = "";
        /// <summary>Where the tile and part were read from ("sheet text", "title block", "key plan"), empty when not found.</summary>
        public string TileSource = "";

        public ObjectId TitleBlockId;
        public string TitleBlockName = "";
        /// <summary>The title block's attributes in their order (tag → value).</summary>
        public readonly List<KeyValuePair<string, string>> Attributes = new List<KeyValuePair<string, string>>();
        /// <summary>Tags whose attribute holds a field (as %&lt;\AcVar ctab&gt;%): read as the field shows it, never written over.</summary>
        public readonly HashSet<string> FieldTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ObjectId SheetIdTextId, PartTextId, ScaleTextId, KeyTileTextId, KeyHatchId;
        public string ScaleText = "";
        public readonly List<(ObjectId id, string tile, string part, Point3d center)> KeyTexts = new List<(ObjectId, string, string, Point3d)>();

        public ObjectId ViewportId;
        public double Scale, Twist;
        public bool Locked;
        public int FrozenLayers;
        public string PageSetup = "", Paper = "", Device = "";

        public string Attr(string tag) => string.IsNullOrEmpty(tag) ? "" : Attributes.FirstOrDefault(a => a.Key.Equals(tag, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        public bool HasAttr(string tag) => !string.IsNullOrEmpty(tag) && Attributes.Any(a => a.Key.Equals(tag, StringComparison.OrdinalIgnoreCase));
        /// <summary>"1:500" from the main viewport (paper mm per model m), empty without one.</summary>
        public string ViewportScale => Scale > 0 ? "1:" + Math.Round(1000.0 / Scale).ToString(CultureInfo.InvariantCulture) : "";
        public string TilePart => Tile.Length > 0 || Part.Length > 0 ? $"{Tile}-{Part}" : "";
        /// <summary>A plan sheet of a tile part: it carries the sheet ID text. Other sheets (profiles, details) may show a tile but are no part of it.</summary>
        public bool IsPart => !SheetIdTextId.IsNull;
        public override string ToString() => Layout;
    }

    internal sealed class SheetIssue
    {
        public string Layout = "", Text = "";
        /// <summary>What a fix does, or null when it cannot be fixed automatically.</summary>
        public string FixText;
        /// <summary>Information only (a viewport scaled on purpose): shown, but not counted as a problem and never fixed.</summary>
        public bool IsNote;
        public Action<Transaction> Fix;
    }

    /// <summary>
    /// Sheet management beyond AutoCAD's: sheets read with their title block, sheet ID, part letter, key plan and scale note,
    /// and kept in step when renamed, renumbered, duplicated or moved to another tile part. Also title block tables,
    /// revisions, viewport scale and layers, checks, page setups and PDF output. Changes are made in the active drawing
    /// (layout operations need it) inside the caller's transaction.
    /// </summary>
    internal static class SheetManager
    {
        private static readonly char[] BadNameChars = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

        // =====================================================================================
        // Reading
        // =====================================================================================

        public static List<SheetRecord> Read(Transaction tr, Database db, SheetProfile p)
        {
            List<SheetRecord> sheets = new List<SheetRecord>();
            DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry de in layouts)
            {
                if (!(tr.GetObject(de.Value, OpenMode.ForRead) is Layout lay) || lay.ModelType) continue;
                sheets.Add(ReadOne(tr, lay, p));
            }
            return sheets.OrderBy(s => s.Tab).ToList();
        }

        public static SheetRecord ReadOne(Transaction tr, Layout lay, SheetProfile p)
        {
            SheetRecord s = new SheetRecord
            {
                LayoutId = lay.ObjectId, BlockId = lay.BlockTableRecordId, Layout = lay.LayoutName, Tab = lay.TabOrder,
                PageSetup = Safe(() => lay.PlotSettingsName), Paper = Safe(() => lay.CanonicalMediaName), Device = Safe(() => lay.PlotConfigurationName),
                Group = SheetGroups.Get(tr, lay)
            };
            Regex idRx = Rx(p.SheetIdPattern), partRx = Rx(p.PartTextPattern), scaleRx = Rx(p.ScaleTextPattern), keyRx = Rx(p.KeyPlanPattern);
            List<(ObjectId id, Point3d c)> hatches = new List<(ObjectId, Point3d)>();
            List<(ObjectId id, Point3d c, string text)> digits = new List<(ObjectId, Point3d, string)>();
            List<(BlockReference br, int count)> blocks = new List<(BlockReference, int)>();
            double viewportArea = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                Entity e;
                try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch (Exception) { continue; }
                if (e == null) continue;
                if (e is Viewport vp)
                {
                    // The sheet's own paper viewport is skipped; the largest of the others is the main view.
                    if (IsPaperViewport(vp)) continue;
                    if (vp.CustomScale <= 0 || !vp.ViewDirection.IsParallelTo(Vector3d.ZAxis)) continue;
                    double area = vp.Width * vp.Height;
                    if (area <= viewportArea) continue;
                    viewportArea = area;
                    s.ViewportId = id; s.Scale = vp.CustomScale; s.Twist = vp.TwistAngle; s.Locked = vp.Locked;
                    try { s.FrozenLayers = vp.GetFrozenLayers()?.Count ?? 0; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                else if (e is BlockReference br) blocks.Add((br, br.AttributeCollection.Count));
                else if (e is Hatch) hatches.Add((id, Center(e)));
                else if (e is DBText || e is MText)
                {
                    string text = PlainText(e).Trim();
                    if (text.Length == 0) continue;
                    Match m;
                    if (idRx != null && (m = idRx.Match(text)).Success && s.SheetIdTextId.IsNull)
                    {
                        s.SheetIdTextId = id;
                        s.Tile = m.Groups["tile"].Value; s.Part = m.Groups["part"].Value; s.TileSource = "sheet text";
                    }
                    else if (keyRx != null && (m = keyRx.Match(text)).Success) s.KeyTexts.Add((id, m.Groups["tile"].Value, m.Groups["part"].Value, Center(e)));
                    else if (partRx != null && partRx.IsMatch(text) && s.PartTextId.IsNull && e is DBText) s.PartTextId = id;
                    else if (scaleRx != null && scaleRx.IsMatch(text) && s.ScaleTextId.IsNull) { s.ScaleTextId = id; s.ScaleText = text; }
                    else if (Regex.IsMatch(text, @"^\d+$")) digits.Add((id, Center(e), text));
                }
            }

            // The title block: the named one, else the block with the most attributes.
            BlockReference title = null, renamed = null;
            foreach ((BlockReference br, int count) in blocks)
            {
                if (count == 0) continue;
                string name = BlockName(tr, br);
                if (!string.IsNullOrWhiteSpace(p.TitleBlock))
                {
                    // The exact saved name first; else the same block after Drawing Standards renamed its prefix
                    // (JCB-TTB_… -> KSIA_TTB_…), so a second prefix variant on the sheet never wins over the exact one (review finding).
                    if (name.Equals(p.TitleBlock.Trim(), StringComparison.OrdinalIgnoreCase)) { title = br; break; }
                    if (renamed == null && Features.Standards.NamePrefix.SameIgnoringPrefix(name, p.TitleBlock)) renamed = br;
                }
                else if (title == null || count > title.AttributeCollection.Count) title = br;
            }
            title ??= renamed;
            if (title != null)
            {
                s.TitleBlockId = title.ObjectId;
                s.TitleBlockName = BlockName(tr, title);
                foreach (ObjectId a in title.AttributeCollection)
                    if (tr.GetObject(a, OpenMode.ForRead) is AttributeReference ar)
                    {
                        string value = AttributeText(ar);
                        string codes = FieldCodes(ar);
                        if (codes != null)
                        {
                            s.FieldTags.Add(ar.Tag);
                            // A CTAB field shows its own layout's name on its sheet; the stored text is the last one evaluated, the
                            // same on every sheet (your drawing number field, 2026-11-18).
                            // Only when CTAB is the attribute's only field: another field's code cannot be shown, so then the stored text stays (review finding).
                            if (CtabField.IsMatch(codes) && CtabField.Replace(codes, s.Layout) is string shown && !shown.Contains("%<")) value = shown;
                        }
                        s.Attributes.Add(new KeyValuePair<string, string>(ar.Tag, value));
                    }
            }

            // The key plan: the quadrant labels' area; its tile number and the hatch marking this sheet's quadrant.
            if (s.KeyTexts.Count >= 2)
            {
                double cell = CellSize(s.KeyTexts);
                Extents3d box = new Extents3d(s.KeyTexts[0].center, s.KeyTexts[0].center);
                foreach (var k in s.KeyTexts) box.AddPoint(k.center);
                bool In(Point3d c) => c.X >= box.MinPoint.X - cell && c.X <= box.MaxPoint.X + cell && c.Y >= box.MinPoint.Y - cell && c.Y <= box.MaxPoint.Y + cell;
                // The tile number in the middle is the key plan's own tile, not any number that happens to lie near it.
                string keyTile = s.KeyTexts[0].tile;
                s.KeyTileTextId = digits.Where(d => In(d.c) && d.text == keyTile).Select(d => d.id).FirstOrDefault();
                s.KeyHatchId = hatches.Where(h => In(h.c)).Select(h => h.id).FirstOrDefault();
            }
            // Without a sheet ID text: the title block's sheet title ("SHEET 105 PART - A"), else the key plan's tile only
            // (profile and detail sheets show the tile but are no part of it).
            if (s.Tile.Length == 0 && TemplateRx(p.SheetTitleTemplate) is Regex tr2 && tr2.Match(s.Attr(p.SheetTitleTag)) is Match tm && tm.Success)
            {
                s.Tile = tm.Groups["tile"].Value; s.Part = tm.Groups["part"].Value; s.TileSource = "title block";
            }
            // A profile sheet's title names its tile ("PROFILE SHEET 123 - PART 1"); before the key plan, which on tile 124's
            // drawing showed 159 on two sheets titled 123 (2026-11-16): the title is the sheet's own statement.
            if (s.Tile.Length == 0 && Regex.Match(s.Attr(p.SheetTitleTag), @"\bSHEET\s+(\d+)\b", RegexOptions.IgnoreCase) is Match st && st.Success)
            {
                s.Tile = st.Groups[1].Value; s.TileSource = "sheet title (tile only)";
            }
            if (s.Tile.Length == 0 && s.KeyTexts.Count > 0) { s.Tile = s.KeyTexts[0].tile; s.TileSource = "key plan (tile only)"; }
            return s;
        }

        /// <summary>
        /// The layout's own paper viewport (number 1 once the layout has been opened; 1:1 onto the paper's origin before).
        /// A layout never opened has none, so the first viewport is not always it.
        /// </summary>
        public static bool IsPaperViewport(Viewport vp) =>
            vp.Number == 1 || (Math.Abs(vp.CustomScale - 1) < 1e-9 && vp.ViewTarget.IsEqualTo(Point3d.Origin) && Math.Abs(vp.TwistAngle) < 1e-12);

        private static double CellSize(List<(ObjectId id, string tile, string part, Point3d center)> keys)
        {
            double best = double.MaxValue;
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++) best = Math.Min(best, keys[i].center.DistanceTo(keys[j].center));
            return best < double.MaxValue ? best : 5;
        }

        public static string BlockName(Transaction tr, BlockReference br)
        {
            try { return ((BlockTableRecord)tr.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead)).Name; }
            catch (Exception) { return br.Name; }
        }

        private static readonly Regex CtabField = new Regex(@"%<\\AcVar\s+ctab(\s+\\f\s+""[^""]*"")?>%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The attribute's text with its field codes, or null when it holds no field.</summary>
        internal static string FieldCodes(AttributeReference ar)
        {
            try
            {
                if (!ar.HasFields) return null;
                return ar.IsMTextAttribute ? ar.MTextAttribute.getMTextWithFieldCodes() : ar.getTextWithFieldCodes();
            }
            catch (Exception) { return null; }
        }

        private static string AttributeText(AttributeReference ar)
        {
            try { return ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString; } catch (Exception) { return ar.TextString; }
        }

        public static string PlainText(Entity e) => e is DBText t ? t.TextString : e is MText m ? m.Text : "";

        private static Point3d Center(Entity e)
        {
            try { Extents3d x = e.GeometricExtents; return new Point3d((x.MinPoint.X + x.MaxPoint.X) / 2, (x.MinPoint.Y + x.MaxPoint.Y) / 2, 0); }
            catch (Exception) { return e is DBText t ? t.Position : e is MText m ? m.Location : Point3d.Origin; }
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; } catch (Exception) { return ""; }
        }

        public static Regex Rx(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try { return new Regex(pattern, RegexOptions.IgnoreCase); } catch (ArgumentException) { return null; }
        }

        /// <summary>A template such as "SHEET {tile} PART - {part}" as a pattern reading the tile and part back.</summary>
        public static Regex TemplateRx(string template)
        {
            if (string.IsNullOrWhiteSpace(template)) return null;
            string rx = Regex.Escape(template.Trim()).Replace(@"\{tile}", @"(?<tile>\d+)").Replace(@"\{part}", @"(?<part>[A-Za-z])").Replace(@"\{number}", @"(?<number>.+)");
            return Rx("^" + rx + "$");
        }

        public static string Fill(string template, string tile, string part, string number = "", int row = 0) =>
            (template ?? "").Replace("{tile}", tile ?? "").Replace("{part}", part ?? "").Replace("{number}", number ?? "").Replace("{i}", row.ToString(CultureInfo.InvariantCulture));

        // =====================================================================================
        // Names and numbers
        // =====================================================================================

        /// <summary>Why a layout name cannot be used, or null.</summary>
        public static string NameProblem(string name, IEnumerable<SheetRecord> sheets, SheetRecord self = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The name is empty.";
            if (name.Length > 255) return "The name is longer than 255 characters.";
            if (name.IndexOfAny(BadNameChars) >= 0) return "A layout name cannot contain < > / \\ \" : ; ? * | , = `";
            if (name.Equals("Model", StringComparison.OrdinalIgnoreCase)) return "\"Model\" is reserved.";
            if (sheets.Any(s => s != self && s.Layout.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))) return $"There is already a sheet \"{name.Trim()}\".";
            return null;
        }

        /// <summary>The running number in a name and where it is ("…-102027" → 102027), or null.</summary>
        public static Match Number(string name, SheetProfile p) => Rx(p.NumberPattern)?.Match(name ?? "") is Match m && m.Success ? m : null;

        public static string WithNumber(string name, long number, SheetProfile p)
        {
            Match m = Number(name, p);
            if (m == null) return name + "-" + number.ToString(CultureInfo.InvariantCulture);
            Group g = m.Groups.Count > 1 ? m.Groups[1] : m.Groups[0];
            return name.Substring(0, g.Index) + number.ToString(new string('0', g.Length), CultureInfo.InvariantCulture) + name.Substring(g.Index + g.Length);
        }

        /// <summary>The next number of the name's series (same text around the number) not yet used, from <paramref name="from"/> on.</summary>
        public static string NextFreeName(string like, long from, IEnumerable<SheetRecord> sheets, ICollection<string> taken, SheetProfile p)
        {
            HashSet<string> used = new HashSet<string>(sheets.Select(s => s.Layout).Concat(taken ?? new string[0]), StringComparer.OrdinalIgnoreCase);
            for (long n = Math.Max(0, from); n < from + 100000; n++)
            {
                string name = WithNumber(like, n, p);
                if (!used.Contains(name)) return name;
            }
            return like + " (copy)";
        }

        public static long NumberOf(string name, SheetProfile p)
        {
            Match m = Number(name, p);
            Group g = m == null ? null : (m.Groups.Count > 1 ? m.Groups[1] : m.Groups[0]);
            return g != null && long.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
        }

        /// <summary>Natural order: "…-2" before "…-10".</summary>
        public static int Natural(string a, string b)
        {
            string[] x = Regex.Split(a ?? "", @"(\d+)"), y = Regex.Split(b ?? "", @"(\d+)");
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                int c = long.TryParse(x[i], out long nx) && long.TryParse(y[i], out long ny) ? nx.CompareTo(ny) : string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }

        /// <summary>
        /// Renames sheets (old → new) in two steps so names can be swapped, keeping each title block's drawing number equal to
        /// its layout name and rewriting match line references to the old names on every sheet.
        /// </summary>
        public static List<string> Rename(Transaction tr, Database db, List<SheetRecord> all, Dictionary<SheetRecord, string> renames, SheetProfile p)
        {
            List<string> log = new List<string>();
            LayoutManager lm = LayoutManager.Current;
            Dictionary<SheetRecord, string> todo = renames.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value.Trim() != kv.Key.Layout)
                                                          .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
            foreach (KeyValuePair<SheetRecord, string> kv in todo)
            {
                List<SheetRecord> others = all.Where(s => !todo.ContainsKey(s)).ToList();
                string problem = NameProblem(kv.Value, others);
                if (problem == null && todo.Count(x => x.Value.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)) > 1) problem = $"\"{kv.Value}\" is given to more than one sheet.";
                if (problem != null) throw new InvalidOperationException($"{kv.Key.Layout}: {problem}");
            }
            Dictionary<SheetRecord, string> temp = new Dictionary<SheetRecord, string>();
            int i = 0;
            foreach (SheetRecord s in todo.Keys) { string t = $"~sheet-{Guid.NewGuid():N}".Substring(0, 18) + i++; lm.RenameLayout(s.Layout, t); temp[s] = t; }
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<SheetRecord, string> kv in todo)
            {
                lm.RenameLayout(temp[kv.Key], kv.Value);
                map[kv.Key.Layout] = kv.Value;
                log.Add($"{kv.Key.Layout} → {kv.Value}");
                SetAttributes(tr, kv.Key, new Dictionary<string, string> { [NumberTagOf(kv.Key, p)] = kv.Value });
                kv.Key.Layout = kv.Value;
            }
            // Match lines pointing at a renamed sheet.
            string[] matchTags = { p.MatchRightTag, p.MatchLeftTag, p.MatchUpTag, p.MatchDownTag };
            foreach (SheetRecord s in all)
            {
                Dictionary<string, string> changes = new Dictionary<string, string>();
                foreach (string tag in matchTags.Where(t => !string.IsNullOrEmpty(t)))
                {
                    string v = s.Attr(tag), nv = SheetRefs.Replace(v, map);
                    if (nv != v) changes[tag] = nv;
                }
                if (changes.Count > 0) SetAttributes(tr, s, changes);
            }
            return log;
        }

        // =====================================================================================
        // Tile and part
        // =====================================================================================

        /// <summary>The grid position of a part in "AB/CD" (row from the top, column from the left), or (-1, -1).</summary>
        public static (int row, int col) PartCell(string part, SheetProfile p)
        {
            string[] rows = (p.PartGrid ?? "AB/CD").Split('/');
            for (int r = 0; r < rows.Length; r++)
            {
                int c = rows[r].IndexOf((part ?? "").ToUpperInvariant(), StringComparison.Ordinal);
                if (c >= 0 && part.Length == 1) return (r, c);
            }
            return (-1, -1);
        }

        public static string PartAt(int row, int col, SheetProfile p)
        {
            string[] rows = (p.PartGrid ?? "AB/CD").Split('/');
            return row >= 0 && row < rows.Length && col >= 0 && col < rows[row].Length ? rows[row][col].ToString() : null;
        }

        public static List<string> AllParts(SheetProfile p) => (p.PartGrid ?? "AB/CD").Replace("/", "").Select(c => c.ToString()).ToList();

        /// <summary>Gives the sheet another tile and part: sheet ID text, part letter, title block sheet title and the key plan.</summary>
        public static void SetTilePart(Transaction tr, SheetRecord s, string tile, string part, SheetProfile p, List<string> log)
        {
            tile = (tile ?? "").Trim();
            part = (part ?? "").Trim().ToUpperInvariant();
            string oldId = Fill(p.SheetIdTemplate, s.Tile, s.Part), newId = Fill(p.SheetIdTemplate, tile, part);
            if (!s.SheetIdTextId.IsNull) ReplaceText(tr, s.SheetIdTextId, oldId, newId);
            else log?.Add($"{s.Layout}: no sheet ID text found (pattern \"{p.SheetIdPattern}\").");
            if (!s.PartTextId.IsNull && part.Length > 0) ReplaceText(tr, s.PartTextId, null, part);
            if (TitleFollowsTemplate(s, p)) SetAttributes(tr, s, new Dictionary<string, string> { [p.SheetTitleTag] = Fill(p.SheetTitleTemplate, tile, part) });
            s.Tile = tile;
            s.Part = part;
            UpdateKeyPlan(tr, s, p, log);
        }

        /// <summary>
        /// True when the title block's sheet title is the tool's to write: empty, or already in the template's form. A title
        /// of another form ("PROFILE SHEET 105 - PART 1") is never overwritten.
        /// </summary>
        public static bool TitleFollowsTemplate(SheetRecord s, SheetProfile p)
        {
            if (!s.HasAttr(p.SheetTitleTag) || string.IsNullOrWhiteSpace(p.SheetTitleTemplate)) return false;
            string now = s.Attr(p.SheetTitleTag);
            return string.IsNullOrWhiteSpace(now) || (TemplateRx(p.SheetTitleTemplate)?.IsMatch(now.Trim()) ?? false);
        }

        /// <summary>The key plan shows the sheet's tile: quadrant labels and tile number rewritten, the hatch moved onto its part.</summary>
        public static bool UpdateKeyPlan(Transaction tr, SheetRecord s, SheetProfile p, List<string> log)
        {
            if (s.KeyTexts.Count == 0) { log?.Add($"{s.Layout}: no key plan found (pattern \"{p.KeyPlanPattern}\")."); return false; }
            for (int i = 0; i < s.KeyTexts.Count; i++)
            {
                var k = s.KeyTexts[i];
                if (k.tile != s.Tile && s.Tile.Length > 0)
                {
                    ReplaceText(tr, k.id, $"{k.tile}-{k.part}", $"{s.Tile}-{k.part}");
                    s.KeyTexts[i] = (k.id, s.Tile, k.part, k.center);
                }
            }
            if (!s.KeyTileTextId.IsNull && s.Tile.Length > 0) ReplaceText(tr, s.KeyTileTextId, null, s.Tile);
            if (s.Part.Length == 0) return true;
            if (s.KeyHatchId.IsNull) { log?.Add($"{s.Layout}: the key plan has no hatch to mark the part."); return false; }
            var target = s.KeyTexts.FirstOrDefault(k => k.part.Equals(s.Part, StringComparison.OrdinalIgnoreCase));
            if (target.id.IsNull) { log?.Add($"{s.Layout}: the key plan has no quadrant {s.Part}."); return false; }
            Hatch h = (Hatch)tr.GetObject(s.KeyHatchId, OpenMode.ForWrite);
            Point3d hc = Center(h);
            var now = s.KeyTexts.OrderBy(k => k.center.DistanceTo(hc)).First();
            if (now.id != target.id) h.TransformBy(Matrix3d.Displacement(target.center - now.center));
            return true;
        }

        /// <summary>The part the key plan's hatch marks now, or "".</summary>
        public static string KeyPlanPart(Transaction tr, SheetRecord s)
        {
            if (s.KeyHatchId.IsNull || s.KeyTexts.Count == 0) return "";
            Point3d hc = Center((Entity)tr.GetObject(s.KeyHatchId, OpenMode.ForRead));
            return s.KeyTexts.OrderBy(k => k.center.DistanceTo(hc)).First().part;
        }

        private static void ReplaceText(Transaction tr, ObjectId id, string oldText, string newText)
        {
            Entity e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            if (e is DBText t) t.TextString = oldText != null && t.TextString.Contains(oldText) ? t.TextString.Replace(oldText, newText) : newText;
            else if (e is MText m)
            {
                // Keep the formatting codes around the visible text.
                string plain = m.Text;
                string from = oldText != null && m.Contents.Contains(oldText) ? oldText : plain;
                m.Contents = from.Length > 0 && m.Contents.Contains(from) ? m.Contents.Replace(from, newText) : newText;
            }
        }

        /// <summary>
        /// Fills the match line references between the parts of each tile ("FOR CONTINUATION SEE DRAWING …"): the neighbour to
        /// the right, left, above and below in the part grid. References to other tiles are left as they are.
        /// </summary>
        public static int FillMatchLines(Transaction tr, List<SheetRecord> sheets, IEnumerable<SheetRecord> which, SheetProfile p)
        {
            int changed = 0;
            foreach (SheetRecord s in which)
            {
                (int r, int c) = PartCell(s.Part, p);
                if (r < 0 || s.Tile.Length == 0) continue;
                Dictionary<string, string> values = new Dictionary<string, string>();
                foreach ((string tag, int dr, int dc) in new[] { (p.MatchRightTag, 0, 1), (p.MatchLeftTag, 0, -1), (p.MatchUpTag, -1, 0), (p.MatchDownTag, 1, 0) })
                {
                    if (string.IsNullOrEmpty(tag) || !s.HasAttr(tag)) continue;
                    string part = PartAt(r + dr, c + dc, p);
                    SheetRecord n = part == null ? null : sheets.FirstOrDefault(x => x != s && x.Tile == s.Tile && x.Part.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (n == null) continue;
                    string v = Fill(p.MatchTemplate, s.Tile, part, n.Layout);
                    if (s.Attr(tag) != v) values[tag] = v;
                }
                if (values.Count > 0) { SetAttributes(tr, s, values); changed++; }
            }
            return changed;
        }

        // =====================================================================================
        // Creating, copying, deleting, ordering
        // =====================================================================================

        /// <summary>A copy of the sheet as a new layout right after it (or at the end), read back.</summary>
        public static SheetRecord Duplicate(Transaction tr, Database db, SheetRecord source, string newName, SheetProfile p)
        {
            LayoutManager lm = LayoutManager.Current;
            // The source's number tag, before the copy carries the new name (review finding, 2026-11-16).
            string numberTag = NumberTagOf(source, p);
            lm.CloneLayout(source.Layout, newName, source.Tab + 1);
            Layout lay = (Layout)tr.GetObject(lm.GetLayoutId(newName), OpenMode.ForRead);
            // The copy stays in the source's group, whatever CloneLayout does with the layout's extension dictionary.
            SheetGroups.Set(tr, lay.ObjectId, source.Group);
            SheetRecord copy = ReadOne(tr, lay, p);
            // Its number, and no match lines: the source's neighbours are not the copy's.
            Dictionary<string, string> v = new Dictionary<string, string> { [numberTag] = newName };
            foreach (string tag in new[] { p.MatchRightTag, p.MatchLeftTag, p.MatchUpTag, p.MatchDownTag }) if (!string.IsNullOrEmpty(tag)) v[tag] = "";
            SetAttributes(tr, copy, v);
            return copy;
        }

        /// <summary>
        /// A new sheet from a layout of another drawing or template (DWG/DWT): page setup and everything on it copied, the
        /// sheet's own paper viewport left to the new layout.
        /// </summary>
        public static SheetRecord FromTemplate(Transaction tr, Database db, string file, string layoutName, string newName, SheetProfile p)
        {
            if (string.Equals(file, db.Filename, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(file))
            {
                LayoutManager.Current.CloneLayout(layoutName, newName, LayoutCount(tr, db) + 1);
                // A new sheet starts in no group (only Duplicate keeps the source's).
                SheetGroups.Set(tr, LayoutManager.Current.GetLayoutId(newName), "");
                return ReadOne(tr, (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(newName), OpenMode.ForRead), p);
            }
            using (Database src = new Database(false, true))
            {
                src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, null);
                src.CloseInput(true);
                using (Transaction st = src.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)st.GetObject(src.LayoutDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(layoutName)) throw new InvalidOperationException($"\"{layoutName}\" is not a layout of {Path.GetFileName(file)}.");
                    Layout from = (Layout)st.GetObject(dict.GetAt(layoutName), OpenMode.ForRead);
                    ObjectId newId = LayoutManager.Current.CreateLayout(newName);
                    Layout to = (Layout)tr.GetObject(newId, OpenMode.ForWrite);
                    try { to.CopyFrom(from); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    ObjectIdCollection ids = new ObjectIdCollection();
                    foreach (ObjectId id in (BlockTableRecord)st.GetObject(from.BlockTableRecordId, OpenMode.ForRead))
                        if (!(st.GetObject(id, OpenMode.ForRead) is Viewport vp && IsPaperViewport(vp))) ids.Add(id);
                    IdMapping map = new IdMapping();
                    if (ids.Count > 0) src.WblockCloneObjects(ids, to.BlockTableRecordId, map, DuplicateRecordCloning.Ignore, false);
                    st.Commit();
                    return ReadOne(tr, to, p);
                }
            }
        }

        /// <summary>The paper layouts of a drawing file, in tab order.</summary>
        public static List<string> LayoutsIn(string file)
        {
            using (Database src = new Database(false, true))
            {
                src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, null);
                src.CloseInput(true);
                using (Transaction st = src.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)st.GetObject(src.LayoutDictionaryId, OpenMode.ForRead);
                    List<Layout> all = new List<Layout>();
                    foreach (DBDictionaryEntry e in dict) all.Add((Layout)st.GetObject(e.Value, OpenMode.ForRead));
                    return all.Where(l => !l.ModelType).OrderBy(l => l.TabOrder).Select(l => l.LayoutName).ToList();
                }
            }
        }

        private static int LayoutCount(Transaction tr, Database db) => ((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Count;

        public static void Delete(IEnumerable<SheetRecord> sheets, List<SheetRecord> all)
        {
            LayoutManager lm = LayoutManager.Current;
            List<SheetRecord> gone = sheets.ToList();
            if (gone.Count >= all.Count) throw new InvalidOperationException("A drawing keeps at least one sheet.");
            // The current layout cannot be deleted: another sheet is made current first.
            if (gone.Any(s => s.Layout == lm.CurrentLayout)) lm.CurrentLayout = all.First(s => !gone.Contains(s)).Layout;
            foreach (SheetRecord s in gone) lm.DeleteLayout(s.Layout);
        }

        /// <summary>Puts the tabs in the given order (model space stays first).</summary>
        public static void Reorder(Transaction tr, List<SheetRecord> order)
        {
            // First out of the way, then into place: two layouts never share a tab number on the way.
            int far = 10000;
            foreach (SheetRecord s in order) { ((Layout)tr.GetObject(s.LayoutId, OpenMode.ForWrite)).TabOrder = far++; }
            for (int i = 0; i < order.Count; i++) { ((Layout)tr.GetObject(order[i].LayoutId, OpenMode.ForWrite)).TabOrder = i + 1; order[i].Tab = i + 1; }
        }

        // =====================================================================================
        // Title block
        // =====================================================================================

        /// <summary>Writes title block attributes (tags the block lacks are skipped); the record follows.</summary>
        public static int SetAttributes(Transaction tr, SheetRecord s, Dictionary<string, string> values)
        {
            if (s.TitleBlockId.IsNull || values.Count == 0) return 0;
            int n = 0;
            BlockReference br = (BlockReference)tr.GetObject(s.TitleBlockId, OpenMode.ForRead);
            foreach (ObjectId a in br.AttributeCollection)
            {
                if (!(tr.GetObject(a, OpenMode.ForRead) is AttributeReference ar)) continue;
                string tag = values.Keys.FirstOrDefault(k => k.Equals(ar.Tag, StringComparison.OrdinalIgnoreCase));
                if (tag == null) continue;
                string value = values[tag] ?? "";
                // A field (the drawing number's CTAB) keeps itself up to date: writing text over it would end that.
                if (FieldCodes(ar) != null) continue;
                if (AttributeText(ar) == value) continue;
                ar.UpgradeOpen();
                if (ar.IsMTextAttribute) { MText m = ar.MTextAttribute; m.Contents = value; ar.MTextAttribute = m; ar.UpdateMTextAttribute(); }
                else ar.TextString = value;
                int i = s.Attributes.FindIndex(x => x.Key.Equals(ar.Tag, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) s.Attributes[i] = new KeyValuePair<string, string>(s.Attributes[i].Key, value);
                n++;
            }
            return n;
        }

        /// <summary>The next revision code: the number at its end plus one, same width ("C01" → "C02"); "" → "C01".</summary>
        public static string NextRevision(string current)
        {
            Match m = Regex.Match(current ?? "", @"(\d+)(?!.*\d)");
            if (!m.Success) return string.IsNullOrWhiteSpace(current) ? "C01" : current.Trim() + "1";
            long n = long.Parse(m.Value, CultureInfo.InvariantCulture) + 1;
            return current.Substring(0, m.Index) + n.ToString(new string('0', m.Length), CultureInfo.InvariantCulture) + current.Substring(m.Index + m.Length);
        }

        internal sealed class Revision
        {
            /// <summary>Empty: each sheet's next code after its own.</summary>
            public string Code = "";
            public string Date = "", Description = "", By = "", Checked = "", Approved = "";
            /// <summary>True: the rows move down one (row 1 newest, the oldest dropped); false: the first empty row takes it.</summary>
            public bool Shift;
            public bool SetDate = true;
        }

        /// <summary>Adds the revision to a sheet's title block; returns the code it got, or why it could not.</summary>
        public static string AddRevision(Transaction tr, SheetRecord s, Revision r, SheetProfile p, out string problem)
        {
            problem = null;
            if (s.TitleBlockId.IsNull) { problem = "no title block"; return null; }
            string code = string.IsNullOrWhiteSpace(r.Code) ? NextRevision(s.Attr(p.RevisionTag)) : r.Code.Trim();
            string[] fields = { p.RevDateTag, p.RevCodeTag, p.RevDescTag, p.RevNoTag, p.RevChkTag, p.RevAppTag };
            Dictionary<string, string> v = new Dictionary<string, string>();
            int rows = Math.Max(1, p.RevisionRows);
            string Tag(string f, int i) => string.IsNullOrEmpty(f) ? null : Fill(f, "", "", "", i);
            int row;
            if (r.Shift)
            {
                for (int i = rows; i > 1; i--)
                    foreach (string f in fields) if (Tag(f, i) is string to && Tag(f, i - 1) is string from) v[to] = s.Attr(from);
                row = 1;
            }
            else
            {
                row = Enumerable.Range(1, rows).FirstOrDefault(i => string.IsNullOrWhiteSpace(s.Attr(Tag(p.RevCodeTag, i))) && string.IsNullOrWhiteSpace(s.Attr(Tag(p.RevDateTag, i))));
                if (row == 0) { problem = $"all {rows} revision rows are full (use \"shift the rows\")"; return null; }
            }
            long no = Enumerable.Range(1, rows).Select(i => long.TryParse(s.Attr(Tag(p.RevNoTag, i)), out long x) ? x : 0).DefaultIfEmpty(0).Max() + 1;
            if (Tag(p.RevDateTag, row) is string d) v[d] = r.Date;
            if (Tag(p.RevCodeTag, row) is string c) v[c] = code;
            if (Tag(p.RevDescTag, row) is string ds) v[ds] = r.Description;
            if (Tag(p.RevNoTag, row) is string nt) v[nt] = no.ToString("00", CultureInfo.InvariantCulture);
            if (Tag(p.RevChkTag, row) is string ck) v[ck] = r.Checked;
            if (Tag(p.RevAppTag, row) is string ap) v[ap] = r.Approved;
            if (!string.IsNullOrEmpty(p.RevisionTag)) v[p.RevisionTag] = code;
            if (r.SetDate && !string.IsNullOrEmpty(p.DateTag)) v[p.DateTag] = r.Date;
            if (!string.IsNullOrWhiteSpace(r.By) && !string.IsNullOrEmpty(p.DrawnTag)) v[p.DrawnTag] = r.By;
            if (!string.IsNullOrWhiteSpace(r.Checked) && !string.IsNullOrEmpty(p.CheckedTag)) v[p.CheckedTag] = r.Checked;
            if (!string.IsNullOrWhiteSpace(r.Approved) && !string.IsNullOrEmpty(p.ApprovedTag)) v[p.ApprovedTag] = r.Approved;
            SetAttributes(tr, s, v);
            return code;
        }

        /// <summary>Every attribute tag found on the sheets, in title block order.</summary>
        public static List<string> AllTags(IEnumerable<SheetRecord> sheets)
        {
            List<string> tags = new List<string>();
            foreach (SheetRecord s in sheets) foreach (KeyValuePair<string, string> a in s.Attributes) if (!tags.Contains(a.Key, StringComparer.OrdinalIgnoreCase)) tags.Add(a.Key);
            return tags;
        }

        // ---------------- Register (CSV, opens in Excel) ----------------

        public static void ExportRegister(string file, List<SheetRecord> sheets, List<string> tags)
        {
            StringBuilder sb = new StringBuilder();
            string Cell(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            sb.AppendLine(string.Join(",", new[] { "Tab", "Layout", "Tile", "Part", "Scale" }.Concat(tags).Select(Cell)));
            foreach (SheetRecord s in sheets)
                sb.AppendLine(string.Join(",", new[] { s.Tab.ToString(CultureInfo.InvariantCulture), s.Layout, s.Tile, s.Part, s.ViewportScale }.Concat(tags.Select(s.Attr)).Select(Cell)));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>The register's rows (layout → tag → value) for the tags it has; unknown columns are ignored.</summary>
        public static Dictionary<string, Dictionary<string, string>> ReadRegister(string file, out List<string> tags)
        {
            List<List<string>> rows = ParseCsv(File.ReadAllText(file));
            tags = new List<string>();
            Dictionary<string, Dictionary<string, string>> result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (rows.Count == 0) return result;
            List<string> head = rows[0];
            int layoutCol = head.FindIndex(h => h.Equals("Layout", StringComparison.OrdinalIgnoreCase));
            if (layoutCol < 0) throw new InvalidOperationException("The register has no \"Layout\" column.");
            string[] own = { "Tab", "Layout", "Tile", "Part", "Scale" };
            for (int c = 0; c < head.Count; c++) if (!own.Contains(head[c], StringComparer.OrdinalIgnoreCase) && head[c].Length > 0) tags.Add(head[c]);
            foreach (List<string> row in rows.Skip(1))
            {
                if (row.Count <= layoutCol || string.IsNullOrWhiteSpace(row[layoutCol])) continue;
                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < head.Count && c < row.Count; c++) values[head[c]] = row[c];
                result[row[layoutCol].Trim()] = values;
            }
            return result;
        }

        private static List<List<string>> ParseCsv(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool quoted = false;
            // Excel saves with the list separator of the machine: a semicolon when the header has no comma.
            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            string first = nl < 0 ? text : text.Substring(0, nl);
            char sep = first.Count(ch => ch == ';') > first.Count(ch => ch == ',') ? ';' : ',';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cell.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Any(c => c.Length > 0)) rows.Add(row);
                    row = new List<string>();
                }
                else if (ch != '﻿') cell.Append(ch);
            }
            row.Add(cell.ToString());
            if (row.Any(c => c.Length > 0)) rows.Add(row);
            return rows;
        }

        // =====================================================================================
        // Viewports and scale
        // =====================================================================================

        /// <summary>
        /// The title block scale and the scale note set from the main viewport's scale. Tile part (plan) sheets only: a profile
        /// sheet's title block gives the drawing's horizontal scale (1:500) while its viewport is at another (1:750), so its scale
        /// text is never written from the viewport (docs/sheet-production-contract.md, section 2).
        /// </summary>
        public static bool SyncScale(Transaction tr, SheetRecord s, SheetProfile p)
        {
            if (!s.IsPart) return false;
            string scale = s.ViewportScale;
            if (scale.Length == 0) return false;
            bool changed = SetAttributes(tr, s, new Dictionary<string, string> { [p.ScaleTag] = scale }) > 0;
            return SetScaleNote(tr, s, scale) || changed;
        }

        /// <summary>The scale note set to the title block's scale (the intended one), whatever the viewport is at.</summary>
        public static bool SyncNoteToTitle(Transaction tr, SheetRecord s, SheetProfile p)
        {
            if (!s.IsPart) return false;
            string title = Regex.Match(s.Attr(p.ScaleTag), @"1\s*:\s*[\d.,]+").Value.Replace(" ", "");
            return title.Length > 0 && SetScaleNote(tr, s, title);
        }

        private static bool SetScaleNote(Transaction tr, SheetRecord s, string scale)
        {
            if (s.ScaleTextId.IsNull) return false;
            Entity e = (Entity)tr.GetObject(s.ScaleTextId, OpenMode.ForRead);
            // The first scale in the note only, so a note that also gives a second scale keeps it.
            string text = PlainText(e), now = new Regex(@"1\s*:\s*[\d.,]+").Replace(text, scale, 1);
            if (now == text) return false;
            ReplaceText(tr, s.ScaleTextId, text, now);
            s.ScaleText = now;
            return true;
        }

        /// <summary>The main viewport at 1:<paramref name="n"/> (model metres, paper millimetres), its view centre kept; then the scale notes.</summary>
        public static void SetScale(Transaction tr, SheetRecord s, double n, SheetProfile p)
        {
            if (s.ViewportId.IsNull || n <= 0) return;
            Viewport vp = (Viewport)tr.GetObject(s.ViewportId, OpenMode.ForWrite);
            bool locked = vp.Locked;
            if (locked) vp.Locked = false;
            vp.CustomScale = 1000.0 / n;
            if (locked) vp.Locked = true;
            s.Scale = vp.CustomScale;
            SyncScale(tr, s, p);
        }

        public static void SetLocked(Transaction tr, SheetRecord s, bool locked)
        {
            if (s.ViewportId.IsNull) return;
            Viewport vp = (Viewport)tr.GetObject(s.ViewportId, OpenMode.ForWrite);
            vp.Locked = locked;
            s.Locked = locked;
        }

        /// <summary>The main viewport freezes exactly the layers the source sheet's main viewport freezes.</summary>
        public static void CopyFrozenLayers(Transaction tr, SheetRecord from, SheetRecord to)
        {
            if (from.ViewportId.IsNull || to.ViewportId.IsNull || from == to) return;
            Viewport src = (Viewport)tr.GetObject(from.ViewportId, OpenMode.ForRead);
            Viewport dst = (Viewport)tr.GetObject(to.ViewportId, OpenMode.ForWrite);
            ObjectIdCollection frozen = src.GetFrozenLayers() ?? new ObjectIdCollection();
            dst.ThawAllLayersInViewport();
            if (frozen.Count > 0) dst.FreezeLayersInViewport(frozen.Cast<ObjectId>().GetEnumerator());
            to.FrozenLayers = frozen.Count;
        }

        // =====================================================================================
        // Checks
        // =====================================================================================

        public static List<SheetIssue> Check(Transaction tr, List<SheetRecord> sheets, SheetProfile p)
        {
            List<SheetIssue> issues = new List<SheetIssue>();
            void Add(SheetRecord s, string text, string fixText = null, Action<Transaction> fix = null) => issues.Add(new SheetIssue { Layout = s.Layout, Text = text, FixText = fixText, Fix = fix });
            HashSet<string> names = new HashSet<string>(sheets.Select(s => s.Layout), StringComparer.OrdinalIgnoreCase);
            Regex matchRx = TemplateRx(p.MatchTemplate);
            foreach (SheetRecord s in sheets)
            {
                if (s.TitleBlockId.IsNull) { Add(s, $"No title block ({(string.IsNullOrWhiteSpace(p.TitleBlock) ? "no block with attributes" : "\"" + p.TitleBlock + "\" not found")})."); continue; }
                if (NumberTagOf(s, p) is string numberTag && s.HasAttr(numberTag) && s.Attr(numberTag) != s.Layout)
                    Add(s, $"Drawing number \"{s.Attr(numberTag)}\" differs from the layout name.", "Set it to the layout name",
                        t => SetAttributes(t, s, new Dictionary<string, string> { [numberTag] = s.Layout }));
                foreach (string tag in p.RequiredTags.Where(t => s.HasAttr(t) && string.IsNullOrWhiteSpace(s.Attr(t))))
                    Add(s, $"{tag} is empty.");
                if (s.IsPart)
                {
                    if (TitleFollowsTemplate(s, p) && s.Attr(p.SheetTitleTag) != Fill(p.SheetTitleTemplate, s.Tile, s.Part))
                        Add(s, $"{p.SheetTitleTag} \"{s.Attr(p.SheetTitleTag)}\" does not match {s.TilePart}.", "Rewrite it",
                            t => SetAttributes(t, s, new Dictionary<string, string> { [p.SheetTitleTag] = Fill(p.SheetTitleTemplate, s.Tile, s.Part) }));
                    if (!s.PartTextId.IsNull && PlainText((Entity)tr.GetObject(s.PartTextId, OpenMode.ForRead)).Trim() != s.Part)
                        Add(s, $"The part letter does not match {s.TilePart}.", "Rewrite it", t => ReplaceText(t, s.PartTextId, null, s.Part));
                    string marked = KeyPlanPart(tr, s);
                    if (marked.Length > 0 && !marked.Equals(s.Part, StringComparison.OrdinalIgnoreCase))
                        Add(s, $"The key plan marks part {marked}, the sheet is {s.TilePart}.", "Move the key plan hatch", t => UpdateKeyPlan(t, s, p, null));
                    if (s.KeyTexts.Any(k => k.tile != s.Tile)) Add(s, $"The key plan shows another tile than {s.Tile}.", "Rewrite the key plan", t => UpdateKeyPlan(t, s, p, null));
                }
                // A profile or detail sheet: its title names the tile, the key plan should show the same one. Reported only (the
                // plan-part rewrite moves part hatches a profile sheet does not have).
                else if (s.TileSource == "sheet title (tile only)" && s.KeyTexts.Count > 0 && s.KeyTexts[0].tile != s.Tile)
                    Add(s, $"The key plan shows tile {s.KeyTexts[0].tile}; the sheet title says {s.Tile}.");
                if (s.IsPart && sheets.Count(x => x.IsPart && x.Tile == s.Tile && x.Part.Equals(s.Part, StringComparison.OrdinalIgnoreCase)) > 1)
                    Add(s, $"Another sheet is also {s.TilePart}.");
                if (s.ViewportId.IsNull) Add(s, "No model viewport.");
                else
                {
                    if (!s.Locked) Add(s, "The main viewport is not locked.", "Lock it", t => SetLocked(t, s, true));
                    string title = Regex.Match(s.Attr(p.ScaleTag), @"1\s*:\s*[\d.,]+").Value.Replace(" ", "");
                    string note = Regex.Match(s.ScaleText, @"1\s*:\s*[\d.,]+").Value.Replace(" ", "");
                    string shown = title.Length > 0 ? title : note;
                    // The scale written on the sheet is the nominal one. A plan viewport may be scaled differently on purpose, to
                    // fit its parts, so that is a note with no fix. A profile sheet's viewport is never at its horizontal scale.
                    if (s.IsPart && shown.Length > 0 && shown != s.ViewportScale)
                        issues.Add(new SheetIssue { Layout = s.Layout, IsNote = true, Text = $"Note: the sheet says {shown}; its viewport is at {s.ViewportScale} (kept, as a viewport may be scaled to fit)." });
                    // Only a tile part's note is rewritten: a profile sheet's note gives two scales (H and V) and stays as the user wrote it.
                    if (title.Length > 0 && note.Length > 0 && title != note)
                    {
                        if (s.IsPart) Add(s, $"The title block says {title}, the scale note {note}.", $"Write {title} in the scale note", t => SyncNoteToTitle(t, s, p));
                        else Add(s, $"The title block says {title}, the scale note {note}.");
                    }
                }
                if (matchRx != null)
                    foreach (string tag in new[] { p.MatchRightTag, p.MatchLeftTag, p.MatchUpTag, p.MatchDownTag })
                        if (matchRx.Match(s.Attr(tag)) is Match m && m.Success && !names.Contains(m.Groups["number"].Value.Trim()))
                            Add(s, $"{tag} refers to {m.Groups["number"].Value.Trim()}, which is not a sheet of this drawing.");
            }
            foreach (IGrouping<string, SheetRecord> g in sheets.Where(s => s.HasAttr(NumberTagOf(s, p))).GroupBy(s => s.Attr(NumberTagOf(s, p)), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (SheetRecord s in g) Add(s, $"Drawing number {g.Key} is on {g.Count()} sheets.");
            List<SheetRecord> numbered = sheets.Where(s => NumberOf(s.Layout, p) >= 0).ToList();
            if (numbered.Select(s => s.Layout).SequenceEqual(numbered.OrderBy(s => s.Layout, Comparer<string>.Create(Natural)).Select(s => s.Layout)) == false)
                issues.Add(new SheetIssue { Layout = "", Text = "The sheet tabs are not in number order (Sheets page: Sort)." });
            return issues;
        }

        // =====================================================================================
        // Page setups and PDF
        // =====================================================================================

        public static List<string> PageSetups(Transaction tr, Database db)
        {
            DBDictionary d = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            List<string> names = new List<string>();
            foreach (DBDictionaryEntry e in d) names.Add(e.Key);
            return names.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void ApplyPageSetup(Transaction tr, Database db, SheetRecord s, string name)
        {
            DBDictionary d = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            if (!d.Contains(name)) throw new InvalidOperationException($"There is no page setup \"{name}\".");
            PlotSettings ps = (PlotSettings)tr.GetObject(d.GetAt(name), OpenMode.ForRead);
            Layout lay = (Layout)tr.GetObject(s.LayoutId, OpenMode.ForWrite);
            lay.CopyFrom(ps);
            s.PageSetup = name;
            s.Paper = Safe(() => lay.CanonicalMediaName);
            s.Device = Safe(() => lay.PlotConfigurationName);
        }

        public static string PdfName(string template, SheetRecord s, SheetProfile p)
        {
            string name = (string.IsNullOrWhiteSpace(template) ? "{layout}" : template)
                .Replace("{layout}", s.Layout).Replace("{tile}", s.Tile).Replace("{part}", s.Part)
                .Replace("{rev}", s.Attr(p.RevisionTag)).Replace("{title}", s.Attr(p.SheetTitleTag));
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>
        /// Plots the sheets to PDF with their own page setups (or forced to DWG To PDF), one file each or one multi-sheet file.
        /// Returns the files written. The current layout is put back afterwards.
        /// </summary>
        public static List<string> Publish(Database db, List<SheetRecord> sheets, string folder, string nameTemplate, bool oneFile, bool forcePdf, SheetProfile p, Action<string> status)
        {
            List<string> files = new List<string>();
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) throw new InvalidOperationException("Another plot is in progress.");
            List<string> problems = Preflight(db, sheets, forcePdf);
            if (problems.Count > 0) throw new InvalidOperationException("Nothing was plotted:\n" + string.Join("\n", problems));
            Directory.CreateDirectory(folder);
            object background = AcadCoreApp.GetSystemVariable("BACKGROUNDPLOT");
            AcadCoreApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
            LayoutManager lm = LayoutManager.Current;
            string current = lm.CurrentLayout;
            try
            {
                using (PlotEngine engine = PlotFactory.CreatePublishEngine())
                {
                    // Whatever is open is closed again even when a sheet fails part-way; otherwise AutoCAD stays
                    // "plotting" and refuses every plot for the rest of the session.
                    bool plotOpen = false, documentOpen = false, pageOpen = false;
                    try
                    {
                        engine.BeginPlot(null, null);
                        plotOpen = true;
                        string single = oneFile ? Path.Combine(folder, PdfName(nameTemplate, sheets[0], p) + ".pdf") : null;
                        for (int i = 0; i < sheets.Count; i++)
                        {
                            SheetRecord s = sheets[i];
                            status?.Invoke($"Plotting {s.Layout} ({i + 1}/{sheets.Count})…");
                            lm.CurrentLayout = s.Layout;
                            PlotInfo info = new PlotInfo { Layout = s.LayoutId };
                            using (Transaction tr = db.TransactionManager.StartTransaction())
                            {
                                Layout lay = (Layout)tr.GetObject(s.LayoutId, OpenMode.ForRead);
                                PlotSettings ps = new PlotSettings(lay.ModelType);
                                ps.CopyFrom(lay);
                                if (ForcedToPdf(lay, forcePdf))
                                {
                                    PlotSettingsValidator v = PlotSettingsValidator.Current;
                                    v.SetPlotConfigurationName(ps, PdfDevice, null);
                                    v.RefreshLists(ps);
                                    string media = v.GetCanonicalMediaNameList(ps).Cast<string>().FirstOrDefault(m => m == lay.CanonicalMediaName)
                                                   ?? v.GetCanonicalMediaNameList(ps).Cast<string>().FirstOrDefault(m => m.IndexOf(lay.CanonicalMediaName ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (media != null) v.SetCanonicalMediaName(ps, media);
                                }
                                info.OverrideSettings = ps;
                                PlotInfoValidator piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                                piv.Validate(info);
                                if (!documentOpen)
                                {
                                    string file = oneFile ? single : Path.Combine(folder, PdfName(nameTemplate, s, p) + ".pdf");
                                    engine.BeginDocument(info, Path.GetFileNameWithoutExtension(db.Filename), null, 1, true, file);
                                    documentOpen = true;
                                    files.Add(file);
                                }
                                bool lastPage = !oneFile || i == sheets.Count - 1;
                                engine.BeginPage(new PlotPageInfo(), info, lastPage, null);
                                pageOpen = true;
                                engine.BeginGenerateGraphics(null);
                                engine.EndGenerateGraphics(null);
                                engine.EndPage(null);
                                pageOpen = false;
                                if (lastPage)
                                {
                                    engine.EndDocument(null);
                                    documentOpen = false;
                                }
                                tr.Commit();
                            }
                        }
                        engine.EndPlot(null);
                        plotOpen = false;
                    }
                    finally
                    {
                        // Only reached with something open when a sheet failed; that failure is the one reported.
                        if (pageOpen) try { engine.EndPage(null); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        if (documentOpen) try { engine.EndDocument(null); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        if (plotOpen) try { engine.EndPlot(null); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    }
                }
            }
            finally
            {
                try { lm.CurrentLayout = current; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                AcadCoreApp.SetSystemVariable("BACKGROUNDPLOT", background);
            }
            return files;
        }

        private const string PdfDevice = "DWG To PDF.pc3";

        /// <summary>Sheets set up for a system printer (or none) are plotted with the PDF driver, as are all when it is forced.</summary>
        private static bool ForcedToPdf(Layout lay, bool forcePdf) =>
            forcePdf || !(lay.PlotConfigurationName ?? "").EndsWith(".pc3", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What would stop the sheets plotting as set up, before anything is plotted: a plotter configuration (.pc3) or paper
        /// size that is not on this computer, or a plot style table (.ctb / .stb) that is missing (the PDF would come out
        /// with the wrong colours and lineweights, with no error from AutoCAD).
        /// </summary>
        public static List<string> Preflight(Database db, List<SheetRecord> sheets, bool forcePdf)
        {
            List<string> problems = new List<string>();
            PlotSettingsValidator v = PlotSettingsValidator.Current;
            HashSet<string> devices = new HashSet<string>(v.GetPlotDeviceList().Cast<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> styleSheets = new HashSet<string>(v.GetPlotStyleSheetList().Cast<string>(), StringComparer.OrdinalIgnoreCase);
            if (!devices.Contains(PdfDevice)) problems.Add($"The {PdfDevice} plotter is not installed on this computer.");
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (SheetRecord s in sheets)
                {
                    if (!(tr.GetObject(s.LayoutId, OpenMode.ForRead) is Layout lay))
                    {
                        problems.Add($"{s.Layout}: the layout no longer exists.");
                        continue;
                    }
                    string device = lay.PlotConfigurationName ?? "";
                    if (!ForcedToPdf(lay, forcePdf))
                    {
                        if (!devices.Contains(device)) problems.Add($"{s.Layout}: plotter \"{device}\" is not on this computer (or tick \"force PDF\").");
                        else
                        {
                            using (PlotSettings ps = new PlotSettings(lay.ModelType))
                            {
                                ps.CopyFrom(lay);
                                v.RefreshLists(ps);
                                if (!string.IsNullOrEmpty(lay.CanonicalMediaName) && !v.GetCanonicalMediaNameList(ps).Cast<string>().Contains(lay.CanonicalMediaName))
                                    problems.Add($"{s.Layout}: paper size \"{lay.CanonicalMediaName}\" is not offered by \"{device}\".");
                            }
                        }
                    }
                    string style = lay.CurrentStyleSheet ?? "";
                    if (style.Length > 0 && !styleSheets.Contains(style))
                        problems.Add($"{s.Layout}: plot style table \"{style}\" is missing from this computer.");
                }
            }
            return problems;
        }

        // =====================================================================================
        // Profile detection
        // =====================================================================================

        /// <summary>
        /// A profile read from a sheet: its title block (the block with the most attributes) and the tags holding the drawing
        /// number, sheet title, scale, revision and revision rows, found by their values and names.
        /// </summary>
        /// <summary>
        /// The tag holding this sheet's drawing number: the configured one when the title block has it, otherwise the one
        /// Detect would find (the attribute equal to the layout name, else a tag named like a drawing number). The settings
        /// saved on this computer named DR_NO, which the KSIA block does not have (DRAWING_NUMBER), so rename and copy left the
        /// number unchanged (tile 124 copy, 2026-11-16).
        /// </summary>
        public static string NumberTagOf(SheetRecord s, SheetProfile p)
        {
            if (s.HasAttr(p.NumberTag) || s.Attributes.Count == 0) return p.NumberTag;
            // By name first: a value equal to the layout name can be another field (review finding, 2026-11-16).
            return s.Attributes.Where(a => Regex.IsMatch(a.Key, "DRAW.*N(O|UM)|DWG.*N(O|UM)", RegexOptions.IgnoreCase)).Select(a => a.Key).FirstOrDefault()
                ?? s.Attributes.Where(a => a.Value == s.Layout).Select(a => a.Key).FirstOrDefault()
                ?? p.NumberTag;
        }

        public static SheetProfile Detect(Transaction tr, Layout lay, SheetProfile start)
        {
            SheetProfile p = start.Copy();
            p.TitleBlock = "";
            SheetRecord s = ReadOne(tr, lay, p);
            if (s.TitleBlockId.IsNull) return start;
            p.TitleBlock = s.TitleBlockName;
            string Find(Func<KeyValuePair<string, string>, bool> test, string fallback) => s.Attributes.Where(test).Select(a => a.Key).FirstOrDefault() ?? fallback;
            p.NumberTag = Find(a => a.Value == s.Layout, Find(a => Regex.IsMatch(a.Key, "DRAW.*N(O|UM)|DWG.*N(O|UM)", RegexOptions.IgnoreCase), p.NumberTag));
            p.SheetTitleTag = Find(a => Regex.IsMatch(a.Value, @"SHEET\s*\d+", RegexOptions.IgnoreCase), p.SheetTitleTag);
            p.ScaleTag = Find(a => Regex.IsMatch(a.Key, "^SCALE$", RegexOptions.IgnoreCase), Find(a => Regex.IsMatch(a.Value, @"^1\s*:\s*\d+$"), p.ScaleTag));
            p.RevisionTag = Find(a => Regex.IsMatch(a.Key, "^REV(ISION)?_?(NO|CODE)?$", RegexOptions.IgnoreCase), p.RevisionTag);
            int rows = s.Attributes.Select(a => Regex.Match(a.Key, @"^REV(\d+)_DATE$", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            if (rows > 0) p.RevisionRows = rows;
            p.TableTags = p.TableTags.Where(t => s.HasAttr(t)).ToList();
            if (p.TableTags.Count == 0) p.TableTags = s.Attributes.Take(8).Select(a => a.Key).ToList();
            p.RequiredTags = p.RequiredTags.Where(t => s.HasAttr(t)).ToList();
            return p;
        }
    }
}
