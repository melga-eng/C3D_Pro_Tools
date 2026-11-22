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

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.ProfileTool.ProfileViewsRun))]
#endif

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
        /// <summary>False (default): TOP follows the highest outside, encasement included. True: the cable or duct inside an encasement.</summary>
        public bool TopInsideEncasement { get; set; } = false;
        public string TopNameTemplate { get; set; } = "{alignment}-Duct-TOP";
        public string TopStyle { get; set; } = "";
        public string TopLabelSet { get; set; } = "";
        public string TopLayer { get; set; } = "";
        /// <summary>Surface names for the existing and proposed ground. Empty: the names saved in Profile View Tools.</summary>
        public string ExistingSurface { get; set; } = "";
        public string ProposedSurface { get; set; } = "";
        /// <summary>The alignment's proposed profile (pavement labels sit on it): its name contains this text, or the proposed surface's name.</summary>
        public string ProposedProfile { get; set; } = "Proposed";
        public ProfileDimSettings Dims { get; set; } = new ProfileDimSettings();

        /// <summary>Match: a Profile View Tools template name, or the name of a finished view in the drawing to copy from.</summary>
        public string MatchTemplate { get; set; } = "";
        public string MatchSourceView { get; set; } = "";
        /// <summary>Profiles in a view with no role: Hide (default), Leave, or Delete (only when both surfaces are found).</summary>
        public string MatchOthers { get; set; } = "Hide";
        /// <summary>Elevation ranges: Widen (default: keep each view's range, widen only one that cuts something off), Fit (refit every view), Leave.</summary>
        public string MatchElevation { get; set; } = "Widen";

        /// <summary>
        /// Split: a view longer than this (m of stations) is split into parts of this length (the last one takes the rest),
        /// placed one under another below it, each with its own level range and the long view's style, bands, parts and labels.
        /// The long view stays. A view whose first part already exists is left alone.
        /// </summary>
        public double SplitLength { get; set; } = 450;
        /// <summary>Part names: {AlignmentName} {ViewName} {n} {StartSta} {EndSta}.</summary>
        public string SplitName { get; set; } = "{AlignmentName} (Part-{n})";
        /// <summary>Space (drawing units) between the long view and the parts, and between parts.</summary>
        public double SplitGap { get; set; } = 30;
        /// <summary>A last part shorter than this (m) is joined to the one before it.</summary>
        public double SplitMinLast { get; set; } = 100;

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
            Pattern = TopPattern, OffsetTolerance = OffsetTolerance, OuterFace = OuterFace, ShownOnly = ShownOnly, InsideEncasement = TopInsideEncasement,
        };

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        };

        /// <summary>The "profileViews" section of C3DPro.project.json beside the drawing; defaults when the file or section is missing.</summary>
        public static ProfileViewsSettings Load(string drawingPath, out string problem, out string source)
        {
            problem = null;
            string text = Features.Shared.Contracts.SettingsSource.Text(drawingPath, out string file);
            source = file;
            try
            {
                using JsonDocument d = JsonDocument.Parse(text);
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
                problem = $"The C3D Pro settings could not be read: {ex.Message}";
                return new ProfileViewsSettings();
            }
        }

        public bool Includes(string alignment) => NamePatterns.Matches(Alignments, alignment);
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
            foreach (string k in new[] { "Report", "Top", "Match", "Split", "Dims", "Pavement", "All" }) kw.Keywords.Add(k);
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

            string nameNote, typed;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var names = new List<string>();
                foreach (ObjectId id in CivilDocument.GetCivilDocument(doc.Database).GetAlignmentIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Alignment al) names.Add(al.Name);
                typed = s.Alignments;
                s.Alignments = NamePatterns.Widen(s.Alignments, names, "alignment", out nameNote);
                tr.Commit();
            }
            if (nameNote != null && !PresetPrompt.Continue(ed, nameNote, mode != "Report")) return;
            ed.WriteMessage("\n" + RunReport.Write(Execute(doc, s, mode, source, typed, nameNote), "C3DPROPV", mode) + "\n");
        }

        /// <summary>Runs one step (or Report / All) for the alignments the settings name; returns the report.</summary>
        internal static string Execute(Document doc, ProfileViewsSettings s, string mode, string source, string typed = null, string nameNote = null)
        {
            var log = new StringBuilder();
            log.AppendLine($"C3DPROPV {mode} on {Path.GetFileName(doc.Name)}, alignments \"{typed ?? s.Alignments}\", settings from {source}");
            if (nameNote != null) log.AppendLine(nameNote);
            try
            {
                // Offsets, spacings and clearances are metres; a drawing in other units would be fitted with wrong tolerances.
                if (Features.Networks.Civil3D.ConduitCivil3DAccess.Metres(doc.Database.Insunits) != 1.0)
                    throw new InvalidOperationException($"the drawing units are {doc.Database.Insunits}; C3DPROPV works in metre drawings only. Nothing was changed.");
                CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                ResolveSurfaces(doc, s, log);
                bool doTop = mode == "Top" || mode == "All", doDims = mode == "Dims" || mode == "All";
                bool doMatch = mode == "Match" || (mode == "All" && (s.MatchTemplate.Length > 0 || s.MatchSourceView.Length > 0));
                TopStep(doc, civil, s, doTop, log);
                if (doMatch || mode == "Report") MatchStep(doc, civil, s, doMatch, log);
                // Split before Dims and Pavement, so the new parts get their DIMs and pavement labels in the same run.
                if (mode == "Split" || mode == "All" || mode == "Report") SplitStep(doc, civil, s, mode == "Split" || mode == "All", log);
                DimStep(doc, s, doDims, log);
                PavementStep(doc, civil, s, mode == "Pavement" || mode == "All", log);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROPV failed", ex);
                log.AppendLine($"C3DPROPV failed: {ex.Message}");
            }
            return log.ToString();
        }

        private static void TopStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            TopScan scan = TopProfileCivil.Scan(doc.Database, civil, s.Top());
            List<AlignmentFit> fits = scan.Alignments.Where(a => s.Includes(a.Alignment)).OrderBy(a => a.Alignment).ToList();
            log.AppendLine($"TOP: {fits.Count} alignments with profile views; " +
                           string.Join(", ", fits.GroupBy(f => f.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")) +
                           (s.TopInsideEncasement ? "; TOP follows the cable or duct inside an encasement" : "; TOP follows the highest outside (encasement included)"));
            foreach (AlignmentFit f in fits)
            {
                string diff = f.MaxDiff.HasValue ? $" max diff {f.MaxDiff:F3} m" : "";
                string enc = f.Result.EncasementSteps > 0 ? $"  encasement steps {f.Result.EncasementSteps}" : "";
                if (f.Result.EncasementsLeftOut > 0) enc += $"  encasements left out {f.Result.EncasementsLeftOut}";
                log.AppendLine($"  {f.Alignment,-28} {f.Status,-12} ducts {f.Ducts.Count,3}  TOP {f.Primary?.Name ?? "-"}{diff}{enc}");
                foreach (string n in f.Notes.Take(3)) log.AppendLine($"      {n}");
                foreach (string w in f.Result.Warnings.Where(w => w.StartsWith("Encasement")).Take(5)) log.AppendLine($"      {w}");
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
            if (!s.CreateMissingTop) { log.AppendLine($"TOP: {missing.Count} alignments have no TOP profile; tick \"Create missing TOP profiles\" in the Pro Run window (Profiles > Settings) to create them."); return; }
            if (!ResolveCreate(doc, civil, s, out TopProfileCivil.CreateTopOptions o, out string why)) { log.AppendLine($"TOP: not created: {why}"); return; }
            log.AppendLine("TOP " + TopProfileCivil.Create(doc, missing, o).Summary("created"));
        }

        /// <summary>
        /// Marks where each view's alignment enters and leaves each pavement (from the pavement surfaces) with a station-elevation
        /// label on the proposed ground; the tool's earlier pavement labels in the view are replaced, hand-made ones stay.
        /// </summary>
        private static void SplitStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            if (s.SplitLength <= 0) { log.AppendLine("Split: off (splitLength is 0)."); return; }
            Database db = doc.Database;
            ProfileRoleRules rules = s.RoleRules() ?? ProfileViewToolsSettings.Load().Rules ?? new ProfileRoleRules();
            var o = new SplitOptions
            {
                Length = s.SplitLength, Round = 0, Overlap = 0, MinLast = Math.Max(0, s.SplitMinLast), Below = true, Gap = Math.Max(0, s.SplitGap),
                NamePattern = s.SplitName, TrimParts = true, CopyLabels = true, FitElevation = true, DatumIncrement = 1.0, Margin = 1.0,
                Rules = rules,
            };
            // Long views of the chosen alignments; one whose first part already exists was split before.
            var todo = new List<(ObjectId id, string name, List<SplitPart> parts)>();
            var partly = new List<string>();
            int already = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var names = new HashSet<string>(ProfileViewMatch.AllViews(tr, db).Select(id => ((ProfileView)tr.GetObject(id, OpenMode.ForRead)).Name), StringComparer.OrdinalIgnoreCase);
                var surfaces = new ProfileViewMatch.Surfaces(tr, civil);
                foreach (ObjectId aid in civil.GetAlignmentIds())
                {
                    if (!(tr.GetObject(aid, OpenMode.ForRead) is Alignment al) || !s.Includes(al.Name)) continue;
                    foreach (ObjectId vid in al.GetProfileViewIds())
                    {
                        var v = (ProfileView)tr.GetObject(vid, OpenMode.ForRead);
                        if (v.StationEnd - v.StationStart <= s.SplitLength + 0.5) continue;
                        List<SplitPart> parts = ProfileViewSplit.Plan(tr, v, o, surfaces);
                        if (parts.Count < 2) continue;
                        // All parts there: split before. Only some: an earlier split stopped part way; left for the user to
                        // finish or delete (a second set of parts would be named "(2)").
                        int existing = parts.Count(p => names.Contains(p.Name));
                        if (existing == parts.Count) { already++; continue; }
                        if (existing > 0) { partly.Add($"{v.Name} ({existing} of {parts.Count} parts there: {string.Join(", ", parts.Where(p => !names.Contains(p.Name)).Select(p => p.Name))} missing)"); continue; }
                        todo.Add((vid, v.Name, parts));
                    }
                }
                tr.Commit();
            }
            log.AppendLine($"Split ({s.SplitLength:0.##} m parts named \"{s.SplitName}\"): {todo.Count} long views to split into {todo.Sum(t => t.parts.Count)} parts" +
                           (already > 0 ? $", {already} split before (left alone)" : "") +
                           (partly.Count > 0 ? $", {partly.Count} only partly split (left alone; delete their parts and run Split again): {string.Join("; ", partly)}" : "") + ".");
            foreach (var t in todo)
                log.AppendLine($"  {t.name}: " + string.Join("; ", t.parts.Select(p => $"{p.Name} {p.S0:0.###}-{p.S1:0.###} levels {p.Low:0.#}-{p.High:0.#}")));
            if (!write || todo.Count == 0) return;
            int made = 0;
            foreach (var t in todo)
            {
                var notes = new List<string>();
                try
                {
                    List<SplitPart> done = ProfileViewSplit.Apply(db, civil, t.id, t.parts, o, notes);
                    made += done.Count(p => !p.ViewId.IsNull);
                }
                catch (System.Exception ex) { notes.Add($"failed: {ex.Message}"); }
                foreach (string n in notes) log.AppendLine($"  {t.name}: {n}");
            }
            log.AppendLine($"Split: {made} parts made.");
        }

        private static void PavementStep(Document doc, CivilDocument civil, ProfileViewsSettings s, bool write, StringBuilder log)
        {
            PavementSettings ps = PavementSettings.Load(doc.Name, out string problem);
            if (problem != null) { log.AppendLine($"Pavement: {problem}"); return; }
            if (!ps.Any) { if (write) log.AppendLine("Pavement: no pavement surfaces are set. Open Pro Run, Pavement tab, and add the paved surfaces."); return; }
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var missing = new List<string>();
                List<Func<double, double, bool>> tests = PavementCivil3D.Tests(tr, civil, ps, missing, edgeBands: ps.LabelsUseEdgeBand);
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
                    Autodesk.Civil.DatabaseServices.Profile proposedProfile = ProposedProfileOf(tr, al, s);
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
                                // On the proposed profile; else on the proposed surface; else the view's top line (reported).
                                double elevation = double.NaN;
                                if (proposedProfile != null)
                                    try { elevation = proposedProfile.ElevationAt(e.station); }
                                    catch (System.Exception) { elevation = double.NaN; }
                                if (double.IsNaN(elevation) && proposed != null)
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
                    log.AppendLine($"Pavement: {atTop.Count} labels are on the view's top line: no proposed profile (name containing \"{s.ProposedProfile}\") and no level on the proposed surface \"{s.ProposedSurface}\" there: {string.Join(", ", atTop.Take(10))}.");
                if (write && badStyles.Count == 0) tr.Commit(); else tr.Abort();
            }
        }

        /// <summary>The alignment's surface profile whose name contains the proposed profile text or the proposed surface's name; null when none.</summary>
        private static Autodesk.Civil.DatabaseServices.Profile ProposedProfileOf(Transaction tr, Alignment al, ProfileViewsSettings s)
        {
            var keys = new[] { s.ProposedProfile, s.ProposedSurface }.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
            if (keys.Count == 0) return null;
            foreach (ObjectId pid in al.GetProfileIds())
                if (tr.GetObject(pid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Profile p && p.ProfileType == Autodesk.Civil.DatabaseServices.ProfileType.EG
                    && keys.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return p;
            return null;
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

        /// <summary>
        /// The existing and proposed surfaces this run uses: the saved names when the drawing has them; otherwise the drawing's
        /// own surface whose name says what it is, set for this run (not saved) and said in the report. Settings carried over from
        /// another drawing would otherwise find no ground and plan no cover DIMs (MV drawing 460201, 2026-11-21).
        /// </summary>
        private static void ResolveSurfaces(Document doc, ProfileViewsSettings s, StringBuilder log)
        {
            List<string> surfaces = SurfaceNames(doc);
            ProfileRoleRules rules = s.RoleRules() ?? ProfileViewToolsSettings.Load().Rules ?? new ProfileRoleRules();
            string ex = Features.ProfileTool.SurfaceGuess.Pick(true, rules.ExistingSurface, surfaces);
            string pr = Features.ProfileTool.SurfaceGuess.Pick(false, rules.ProposedSurface, surfaces, ex);
            void Use(string role, string saved, string found, Action<string> set)
            {
                if (found != null && found.Equals(saved ?? "", StringComparison.OrdinalIgnoreCase)) return;
                if (found != null)
                {
                    log.AppendLine($"Surfaces: the {role} surface \"{saved}\" is not in this drawing; using \"{found}\" (its name says {role}). Choose another in the Pro Run window if it is wrong.");
                    set(found);
                }
                else log.AppendLine($"Surfaces: the {role} surface \"{saved}\" is not in this drawing and no surface's name says {role}: choose it in the Pro Run window.");
            }
            Use("existing", rules.ExistingSurface, ex, v => s.ExistingSurface = v);
            Use("proposed", rules.ProposedSurface, pr, v => s.ProposedSurface = v);
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
                           " (change them in the Pro Run window, Profiles > Settings)");
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
                               "and the earlier tool DIMs would be lost. Choose the proposed surface in the Pro Run window (Profiles > Settings).");
                return;
            }
            DimWriteResult r = ProfileDimsCivil3D.Write(doc, views, ds);
            log.AppendLine($"DIMs: drew {r.Created}, replaced {r.Removed}, skipped {r.SkippedNearHand} next to hand DIMs.");
            foreach (string p in r.Problems.Take(15)) log.AppendLine($"  {p}");
        }
    }
}
