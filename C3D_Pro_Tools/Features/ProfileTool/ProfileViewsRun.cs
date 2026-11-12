using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using C3DProTools.Features.Sheets.Application;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using C3DProTools.Features.Pavement;
using Alignment = Autodesk.Civil.DatabaseServices.Alignment;
using ProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using StationElevationLabel = Autodesk.Civil.DatabaseServices.StationElevationLabel;
using Surface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(C3DProTools.ProfileTool.ProfileViewsRun))]

// C3DPROPV: the profile view steps run from the command line with no dialog, using the "profileViews" section of
// C3DPro.project.json beside the drawing. The same command serves as the user's preset run and as the headless drawing test.
//   Report  reads only: TOP status per alignment and the DIMs each view would get
//   Top     fits each TOP profile that differs from its ducts (and creates missing ones when createMissingTop is on)
//   Dims    draws cover and clearance DIMs, replacing the tool DIMs of an earlier run
//   All     Top, then Dims
// Every writing step is one transaction (one UNDO).
namespace C3DProTools.ProfileTool
{
    internal sealed class ProfileViewsSettings
    {
        /// <summary>Alignment names to include; wildcards * and ?, several separated by commas.</summary>
        public string Alignments { get; set; } = "*";
        public string TopPattern { get; set; } = "TOP";
        public double OffsetTolerance { get; set; } = 0.30;
        public bool OuterFace { get; set; } = true;
        public bool ShownOnly { get; set; } = true;
        public bool CreateMissingTop { get; set; } = false;
        public string TopNameTemplate { get; set; } = "{alignment}-Duct-TOP";
        public string TopStyle { get; set; } = "";
        public string TopLabelSet { get; set; } = "";
        public string TopLayer { get; set; } = "";
        /// <summary>Surface names for the existing and proposed ground. Empty: the names saved in Profile View Tools.</summary>
        public string ExistingSurface { get; set; } = "";
        public string ProposedSurface { get; set; } = "";
        public ProfileDimSettings Dims { get; set; } = new ProfileDimSettings();

        /// <summary>Match: a Profile View Tools template name, or the name of a finished view in the drawing to copy from.</summary>
        public string MatchTemplate { get; set; } = "";
        public string MatchSourceView { get; set; } = "";
        /// <summary>Profiles in a view with no role: Hide (default), Leave, or Delete (only when both surfaces are found).</summary>
        public string MatchOthers { get; set; } = "Hide";
        /// <summary>Elevation ranges: Widen (default: keep each view's range, widen only one that cuts something off), Fit (refit every view), Leave.</summary>
        public string MatchElevation { get; set; } = "Widen";

        /// <summary>Role rules with this preset's surface names over the saved ones; null when the preset names none.</summary>
        public ProfileRoleRules RoleRules()
        {
            if (string.IsNullOrWhiteSpace(ExistingSurface) && string.IsNullOrWhiteSpace(ProposedSurface)) return null;
            ProfileRoleRules r = (ProfileViewToolsSettings.Load().Rules ?? new ProfileRoleRules()).Copy();
            if (!string.IsNullOrWhiteSpace(ExistingSurface)) r.ExistingSurface = ExistingSurface.Trim();
            if (!string.IsNullOrWhiteSpace(ProposedSurface)) r.ProposedSurface = ProposedSurface.Trim();
            return r;
        }

        public TopOptions Top() => new TopOptions
        {
            Pattern = TopPattern, OffsetTolerance = OffsetTolerance, OuterFace = OuterFace, ShownOnly = ShownOnly,
        };

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        };

        /// <summary>The "profileViews" section of C3DPro.project.json beside the drawing; defaults when the file or section is missing.</summary>
        public static ProfileViewsSettings Load(string drawingPath, out string problem, out string source)
        {
            problem = null;
            string file = ProjectSettings.PathFor(drawingPath);
            source = "defaults (no " + ProjectSettings.FileName + " beside the drawing)";
            if (!File.Exists(file)) return new ProfileViewsSettings();
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "profileViews", StringComparison.OrdinalIgnoreCase))
                    {
                        ProfileViewsSettings s = p.Value.Deserialize<ProfileViewsSettings>(Json) ?? new ProfileViewsSettings();
                        s.Dims ??= new ProfileDimSettings();
                        source = file;
                        return s;
                    }
                source = $"defaults: {file} has no \"profileViews\" section";
                return new ProfileViewsSettings();
            }
            catch (System.Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = $"{ProjectSettings.FileName} could not be read: {ex.Message}";
                return new ProfileViewsSettings();
            }
        }

        public bool Includes(string alignment)
        {
            string[] parts = (Alignments ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return true;
            return parts.Any(p => Regex.IsMatch(alignment ?? "", "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase));
        }
    }

    public sealed class ProfileViewsRun
    {
        [CommandMethod("C3DPRO", "C3DPROPV", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var kw = new PromptKeywordOptions("\nProfile views") { AllowNone = true };
            foreach (string k in new[] { "Report", "Top", "Match", "Dims", "Pavement", "All" }) kw.Keywords.Add(k);
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Report" : kr.StringResult;

            ProfileViewsSettings s = ProfileViewsSettings.Load(doc.Name, out string problem, out string source);
            if (problem != null) { ed.WriteMessage($"\n{problem}\n"); return; }

            var pr = new PromptStringOptions($"\nAlignments (wildcards, comma separated) <{s.Alignments}>: ") { AllowSpaces = true };
            PromptResult ar = ed.GetString(pr);
            if (ar.Status != PromptStatus.OK) return;
            if (!string.IsNullOrWhiteSpace(ar.StringResult)) s.Alignments = ar.StringResult.Trim();

            var log = new StringBuilder();
            log.AppendLine($"C3DPROPV {mode} on {Path.GetFileName(doc.Name)}, alignments \"{s.Alignments}\", settings from {source}");
            try
            {
                // Offsets, spacings and clearances are metres; a drawing in other units would be fitted with wrong tolerances.
                if (Features.Networks.Civil3D.ConduitCivil3DAccess.Metres(doc.Database.Insunits) != 1.0)
                    throw new InvalidOperationException($"the drawing units are {doc.Database.Insunits}; C3DPROPV works in metre drawings only. Nothing was changed.");
                CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                bool doTop = mode == "Top" || mode == "All", doDims = mode == "Dims" || mode == "All";
                bool doMatch = mode == "Match" || (mode == "All" && (s.MatchTemplate.Length > 0 || s.MatchSourceView.Length > 0));
                TopStep(doc, civil, s, doTop, log);
                if (doMatch || mode == "Report") MatchStep(doc, civil, s, doMatch, log);
                DimStep(doc, s, doDims, log);
                PavementStep(doc, civil, s, mode == "Pavement" || mode == "All", log);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROPV failed", ex);
                log.AppendLine($"C3DPROPV failed: {ex.Message}");
            }

            string file = Path.Combine(Path.GetTempPath(), $"C3DPROPV-{mode}.txt");
            try { File.WriteAllText(file, log.ToString()); } catch (IOException) { file = null; }
            // The command line gets the summary (unindented lines); the per-alignment detail is in the report file.
            string[] summary = log.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != ' ').ToArray();
            ed.WriteMessage("\n" + string.Join("\n", summary) + (file != null ? $"\nFull report: {file}" : "") + "\n");
        }

        private static void TopStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            TopScan scan = TopProfileCivil.Scan(doc.Database, civil, s.Top());
            List<AlignmentFit> fits = scan.Alignments.Where(a => s.Includes(a.Alignment)).OrderBy(a => a.Alignment).ToList();
            log.AppendLine($"TOP: {fits.Count} alignments with profile views; " +
                           string.Join(", ", fits.GroupBy(f => f.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));
            foreach (AlignmentFit f in fits)
            {
                string diff = f.MaxDiff.HasValue ? $" max diff {f.MaxDiff:F3} m" : "";
                log.AppendLine($"  {f.Alignment,-28} {f.Status,-12} ducts {f.Ducts.Count,3}  TOP {f.Primary?.Name ?? "-"}{diff}");
                foreach (string n in f.Notes.Take(3)) log.AppendLine($"      {n}");
            }
            if (!write) return;

            List<AlignmentFit> toFit = fits.Where(f => f.Status == AlignmentFit.StatusDiffers).ToList();
            if (toFit.Count > 0)
            {
                OperationReport r = TopProfileCivil.Fit(doc, toFit);
                log.AppendLine("TOP " + r.Summary("fitted"));
            }
            else log.AppendLine("TOP: nothing to fit.");

            List<AlignmentFit> missing = fits.Where(f => f.Status == AlignmentFit.StatusMissing).ToList();
            if (missing.Count == 0) return;
            if (!s.CreateMissingTop) { log.AppendLine($"TOP: {missing.Count} alignments have no TOP profile; set createMissingTop to create them."); return; }
            if (!ResolveCreate(doc, civil, s, out TopProfileCivil.CreateTopOptions o, out string why)) { log.AppendLine($"TOP: not created: {why}"); return; }
            log.AppendLine("TOP " + TopProfileCivil.Create(doc, missing, o).Summary("created"));
        }

        /// <summary>
        /// Marks where each view's alignment enters and leaves each pavement (from the pavement surfaces) with a station-elevation
        /// label on the proposed ground; the tool's earlier pavement labels in the view are replaced, hand-made ones stay.
        /// </summary>
        private static void PavementStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            PavementSettings ps = PavementSettings.Load(doc.Name, out string problem);
            if (problem != null) { log.AppendLine($"Pavement: {problem}"); return; }
            if (!ps.Any) { if (write) log.AppendLine("Pavement: no pavement surfaces set (pavement.zones in the project file)."); return; }
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var missing = new List<string>();
                List<Func<double, double, bool>> tests = PavementCivil3D.Tests(tr, civil, ps, missing);
                // A pavement surface that is not there (a reference not loaded) would make its edges vanish and erase their labels.
                if (missing.Count > 0)
                {
                    log.AppendLine($"Pavement: surfaces not in the drawing: {string.Join(", ", missing)}." + (write ? " Nothing was labelled or erased." : ""));
                    write = false;
                }
                Surface proposed = null;
                foreach (ObjectId id in civil.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Surface sf && string.Equals(sf.Name, s.ProposedSurface ?? "", StringComparison.OrdinalIgnoreCase)) proposed = sf;

                // The styles, by zone (its own) or the section's; all must exist before anything is written.
                // A style named in the settings must exist; with none named, the tool's own is used (made from the base style).
                var styles = new Dictionary<(int, bool), ObjectId>();
                var badStyles = new List<string>();
                var toMake = new List<string>();
                List<string> available = PavementCivil3D.StationElevationStyleNames(tr, civil);
                ObjectId baseStyle = PavementCivil3D.StationElevationStyle(tr, civil, ps.BaseLabelStyle is { Length: > 0 } bn ? bn : available.FirstOrDefault());
                for (int k = 1; k <= ps.Zones.Count; k++)
                    foreach (bool start in new[] { true, false })
                    {
                        PavementZone z = ps.Zones[k - 1];
                        string name = start ? (z.StartLabelStyle is { Length: > 0 } a ? a : ps.StartLabelStyle) : (z.EndLabelStyle is { Length: > 0 } b ? b : ps.EndLabelStyle);
                        ObjectId id;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            id = PavementCivil3D.StationElevationStyle(tr, civil, name);
                            if (id.IsNull) badStyles.Add($"\"{name}\"");
                        }
                        else
                        {
                            string own = PavementCivil3D.OwnStyleName(z, start);
                            id = PavementCivil3D.StationElevationStyle(tr, civil, own);
                            if (id.IsNull && baseStyle.IsNull) badStyles.Add($"{own}: no station-elevation style to make it from");
                            else if (id.IsNull && !write) toMake.Add(own);
                            else if (id.IsNull) id = PavementCivil3D.EnsureEdgeStyle(tr, civil, baseStyle, own, $"{(start ? "Start" : "End")} Of {z.Name}");
                        }
                        styles[(k, start)] = id;
                    }
                if (toMake.Count > 0)
                    log.AppendLine($"Pavement: the label styles {string.Join(", ", toMake)} will be made from \"{(ps.BaseLabelStyle is { Length: > 0 } b2 ? b2 : available.FirstOrDefault())}\" (or set pavement.startLabelStyle / endLabelStyle).");
                ObjectId marker = PavementCivil3D.Marker(tr, civil, ps.MarkerStyle);

                int views = 0, edges = 0, made = 0, replaced = 0, failed = 0;
                var atTop = new List<string>();
                foreach (ObjectId aid in civil.GetAlignmentIds())
                {
                    if (!(tr.GetObject(aid, OpenMode.ForRead) is Alignment al) || !s.Includes(al.Name)) continue;
                    foreach (ObjectId vid in al.GetProfileViewIds())
                    {
                        if (!(tr.GetObject(vid, OpenMode.ForRead) is ProfileView v)) continue;
                        views++;
                        List<PavementRun> runs = PavementCivil3D.AlongAlignment(al, v.StationStart, v.StationEnd, tests, ps.Step);
                        var found = PavementCivil3D.Edges(runs, v.StationStart, v.StationEnd);
                        edges += found.Count;
                        if (found.Count > 0 || write)
                            log.AppendLine($"  {v.Name}: " + (found.Count == 0 ? "no pavement edge" :
                                string.Join(", ", found.Select(e => $"{(e.start ? "start" : "end")} of {ps.Of(e.kind).Name} {e.station:0.000}"))));
                        if (!write || badStyles.Count > 0) continue;

                        using Transaction one = tr.TransactionManager.StartTransaction();
                        try
                        {
                            foreach (ObjectId old in PavementCivil3D.EarlierLabels(one, db, vid)) { one.GetObject(old, OpenMode.ForWrite).Erase(); replaced++; }
                            foreach (var e in found)
                            {
                                double elevation = double.NaN;
                                if (proposed != null)
                                {
                                    double x = 0, y = 0;
                                    try { al.PointLocation(e.station, 0, ref x, ref y); elevation = proposed.FindElevationAtXY(x, y); }
                                    catch (System.Exception) { elevation = double.NaN; }
                                }
                                if (double.IsNaN(elevation))
                                {
                                    // No proposed level there: on the view's top line, and said so.
                                    elevation = v.ElevationMax;
                                    atTop.Add($"{v.Name} {e.station:0.000}");
                                }
                                ObjectId id = StationElevationLabel.Create(vid, styles[(e.kind, e.start)], marker, e.station, elevation);
                                PavementCivil3D.Tag(one, db, one.GetObject(id, OpenMode.ForWrite));
                                made++;
                            }
                            one.Commit();
                        }
                        catch (System.Exception ex)
                        {
                            one.Abort();
                            failed++;
                            log.AppendLine($"    {v.Name}: no pavement labels ({ex.Message}).");
                        }
                    }
                }
                if (badStyles.Count > 0)
                    log.AppendLine($"Pavement: label styles not found ({string.Join("; ", badStyles.Distinct())}); " +
                                   $"set pavement.startLabelStyle / endLabelStyle to one of: {string.Join(", ", PavementCivil3D.StationElevationStyleNames(tr, civil))}." +
                                   (write ? " Nothing was labelled." : ""));
                log.AppendLine($"Pavement: {views} profile views, {edges} pavement edges" +
                               (write && badStyles.Count == 0 ? $"; labelled {made}, earlier tool labels replaced {replaced}, views failed {failed}." : "."));
                if (atTop.Count > 0)
                    log.AppendLine($"Pavement: {atTop.Count} labels are on the view's top line, the proposed surface \"{s.ProposedSurface}\" has no level there: {string.Join(", ", atTop.Take(10))}.");
                if (write && badStyles.Count == 0) tr.Commit(); else tr.Abort();
            }
        }

        private static bool ResolveCreate(Document doc, CivilDocument civil, ProfileViewsSettings s, out TopProfileCivil.CreateTopOptions o, out string why)
        {
            o = new TopProfileCivil.CreateTopOptions { NameTemplate = s.TopNameTemplate };
            why = null;
            using Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            o.StyleId = ByName(tr, civil.Styles.ProfileStyles, s.TopStyle);
            o.LabelSetId = ByName(tr, civil.Styles.LabelSetStyles.ProfileLabelSetStyles, s.TopLabelSet);
            if (o.StyleId.IsNull) why = $"profile style \"{s.TopStyle}\" is not in the drawing (set topStyle)";
            else if (o.LabelSetId.IsNull) why = $"profile label set \"{s.TopLabelSet}\" is not in the drawing (set topLabelSet)";
            if (!string.IsNullOrWhiteSpace(s.TopLayer))
            {
                var layers = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (layers.Has(s.TopLayer)) o.LayerId = layers[s.TopLayer];
                else why ??= $"layer \"{s.TopLayer}\" is not in the drawing";
            }
            tr.Commit();
            return why == null;
        }

        private static ObjectId ByName(Transaction tr, System.Collections.IEnumerable styles, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            foreach (ObjectId id in styles)
                if (tr.GetObject(id, OpenMode.ForRead) is StyleBase st && string.Equals(st.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return id;
            return ObjectId.Null;
        }

        /// <summary>
        /// Matches the chosen alignments' views to a template or a source view. The role surfaces come from this preset (or the
        /// names saved in Profile View Tools), never from the template, whose names belong to the drawing it was captured in.
        /// </summary>
        private static void MatchStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            ProfileViewToolsSettings saved = ProfileViewToolsSettings.Load();
            ProfileRoleRules rules = s.RoleRules() ?? saved.Rules ?? new ProfileRoleRules();
            if (s.MatchTemplate.Length == 0 && s.MatchSourceView.Length == 0)
            {
                if (write) log.AppendLine("Match: set matchTemplate or matchSourceView in the profileViews settings.");
                return;
            }
            var o = new MatchOptions
            {
                ViewStyle = saved.MatchViewStyle, Bands = saved.MatchBands, ProfileStyles = saved.MatchProfileStyles, ProfileLabels = saved.MatchProfileLabels,
                Elevation = !string.Equals(s.MatchElevation, "Leave", StringComparison.OrdinalIgnoreCase),
                ElevationOnlyWhenCut = !string.Equals(s.MatchElevation, "Fit", StringComparison.OrdinalIgnoreCase),
                StylesInView = saved.StylesInView, BandRolesByName = saved.BandRolesByName, CreateMissing = saved.CreateMissing,
                Others = Enum.TryParse(s.MatchOthers, true, out OtherProfileAction others) ? others : OtherProfileAction.Hide, Margin = saved.Margin, DatumIncrement = saved.DatumIncrement, TopPrefix = saved.TopPrefix,
            };
            var changes = new List<MatchChange>();
            var problems = new List<string>();
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var surfaces = new ProfileViewMatch.Surfaces(tr, civil);
                ObjectId sourceId = ObjectId.Null;
                ProfileViewRecipe recipe;
                List<(ObjectId id, string name, string alignment)> views = new();
                foreach (ObjectId vid in ProfileViewMatch.AllViews(tr, db))
                {
                    if (tr.GetObject(vid, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.ProfileView v) continue;
                    string al = tr.GetObject(v.AlignmentId, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Alignment a ? a.Name : "";
                    views.Add((vid, v.Name, al));
                }
                if (s.MatchSourceView.Length > 0)
                {
                    var src = views.FirstOrDefault(v => string.Equals(v.name, s.MatchSourceView.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (src.id.IsNull) { log.AppendLine($"Match: no profile view named \"{s.MatchSourceView}\"."); return; }
                    sourceId = src.id;
                    recipe = ProfileViewMatch.Capture(tr, (Autodesk.Civil.DatabaseServices.ProfileView)tr.GetObject(src.id, OpenMode.ForRead), rules, surfaces);
                }
                else
                {
                    recipe = saved.Templates.FirstOrDefault(t => string.Equals(t.Name, s.MatchTemplate.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (recipe == null)
                    {
                        log.AppendLine($"Match: no template \"{s.MatchTemplate}\"; saved templates: {string.Join(", ", saved.Templates.Select(t => t.Name))}.");
                        return;
                    }
                    recipe.Rules = rules.Copy();
                }

                foreach (ProfileRole role in ProfileViewMatch.Roles)
                {
                    string surface = role == ProfileRole.Existing ? rules.ExistingSurface : role == ProfileRole.Proposed ? rules.ProposedSurface : null;
                    if (surface != null && surfaces.IdOf(surface).IsNull)
                        problems.Add($"the {ProfileViewMatch.RoleName(role).ToLowerInvariant()} surface \"{surface}\" is not in the drawing, so bands meant for that profile keep what they show");
                }

                // Deleting "other" profiles is only safe when the ground roles are known; otherwise a real ground profile could
                // count as other and be erased.
                if (o.Others == OtherProfileAction.Delete && problems.Count > 0)
                {
                    o.Others = OtherProfileAction.Hide;
                    problems.Add("matchOthers is Delete, but a surface is missing, so other profiles are hidden instead of deleted");
                }
                List<string> missingStyles = ProfileViewMatch.MissingStyles(tr, civil, recipe, o);
                if (write && missingStyles.Count > 0)
                {
                    tr.Abort();
                    log.AppendLine($"Match not applied: this drawing lacks {missingStyles.Count} style(s) the template uses, and bands or labels " +
                                   $"would be replaced with an incomplete set: {string.Join("; ", missingStyles.Take(10))}.");
                    return;
                }
                foreach (string m in missingStyles.Take(10)) problems.Add($"missing {m}; Apply will refuse");

                var targets = views.Where(v => v.id != sourceId && s.Includes(v.alignment)).ToList();
                var log2 = new List<string>();
                foreach (var t in targets)
                {
                    try { ProfileViewMatch.Apply(tr, civil, recipe, t.id, o, surfaces, write, changes, log2); }
                    catch (System.Exception ex) { log2.Add($"{t.name}: {ex.Message}"); }
                }
                if (write) tr.Commit(); else tr.Abort();

                int changed = changes.Select(c => c.View).Distinct().Count();
                log.AppendLine($"Match{(write ? "" : " (preview)")} from {(sourceId.IsNull ? $"template \"{recipe.Name}\"" : $"view \"{s.MatchSourceView}\"")}: " +
                               $"{targets.Count} views, {changed} {(write ? "changed" : "would change")}, {changes.Count} changes" +
                               $"{(log2.Count > 0 ? $", {log2.Count} notes" : "")}.");
                foreach (string p in problems) log.AppendLine($"Match: {p}.");
                foreach (var g in changes.GroupBy(c => c.What.Split('"')[0].Trim()).OrderByDescending(g => g.Count()).Take(12))
                    log.AppendLine($"  {g.Count(),4} x {g.Key}");
                foreach (MatchChange c in changes)
                    log.AppendLine($"    {c.View}: {c.What}: {c.From} -> {c.To}");
                foreach (string n in log2.Take(20)) log.AppendLine($"  note: {n}");
            }
        }

        private static List<string> SurfaceNames(Document doc)
        {
            var names = new List<string>();
            using Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            foreach (ObjectId id in CivilDocument.GetCivilDocument(doc.Database).GetSurfaceIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface sf) names.Add(sf.Name);
            tr.Commit();
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void DimStep(Document doc, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            ProfileDimSettings ds = s.Dims ?? new ProfileDimSettings();
            ProfileRoleRules rules = s.RoleRules() ?? ProfileViewToolsSettings.Load().Rules ?? new ProfileRoleRules();
            List<string> surfaces = SurfaceNames(doc);
            log.AppendLine($"Surfaces in the drawing: {string.Join(", ", surfaces)}");
            log.AppendLine($"Surfaces used: existing \"{rules.ExistingSurface}\", proposed \"{rules.ProposedSurface}\"" +
                           " (set existingSurface / proposedSurface in the profileViews settings to change them)");
            List<ViewDims> views = ProfileDimsCivil3D.Plan(doc, ds, out var issues, rules)
                .Where(v => s.Includes(v.AlignmentName)).ToList();
            int cover = views.Sum(v => v.Plan.Dims.Count(d => d.Dim.Kind == Features.Sheets.Contracts.DimKind.Cover));
            int clear = views.Sum(v => v.Plan.Dims.Count(d => d.Dim.Kind == Features.Sheets.Contracts.DimKind.Clearance));
            log.AppendLine($"DIMs: {views.Count} profile views; planned {cover} cover and {clear} clearance DIMs; " +
                           $"already drawn {views.Sum(v => v.Tool)} by the tool and {views.Sum(v => v.Hand)} by hand.");
            foreach (var i in issues.Take(5)) log.AppendLine($"  {i.Severity} {i.Code}: {i.Message}");
            foreach (ViewDims v in views.Where(v => v.Plan.Dims.Count > 0).Take(40))
                log.AppendLine($"  {v.AlignmentName,-28} view {v.ViewId.Handle}: {v.Plan.Dims.Count} DIMs");
            if (!write) return;
            // Write replaces every earlier tool DIM of the views. Without the proposed surface no cover DIM can be planned, so
            // writing would erase good cover DIMs and draw none: refuse instead.
            if (ds.Cover && !surfaces.Any(n => string.Equals(n, rules.ProposedSurface, StringComparison.OrdinalIgnoreCase)))
            {
                log.AppendLine($"DIMs not drawn: the proposed surface \"{rules.ProposedSurface}\" is not in the drawing, so cover DIMs cannot be planned " +
                               "and the earlier tool DIMs would be lost. Set proposedSurface in the profileViews settings, or set dims.cover to false.");
                return;
            }
            DimWriteResult r = ProfileDimsCivil3D.Write(doc, views, ds);
            log.AppendLine($"DIMs: drew {r.Created}, replaced {r.Removed}, skipped {r.SkippedNearHand} next to hand DIMs.");
            foreach (string p in r.Problems.Take(15)) log.AppendLine($"  {p}");
        }
    }
}
