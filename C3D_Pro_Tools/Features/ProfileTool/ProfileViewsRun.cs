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
        public static ProfileViewsSettings Load(string drawingPath, out string problem)
        {
            problem = null;
            string file = ProjectSettings.PathFor(drawingPath);
            if (!File.Exists(file)) return new ProfileViewsSettings();
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "profileViews", StringComparison.OrdinalIgnoreCase))
                    {
                        ProfileViewsSettings s = p.Value.Deserialize<ProfileViewsSettings>(Json) ?? new ProfileViewsSettings();
                        s.Dims ??= new ProfileDimSettings();
                        return s;
                    }
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

            var kw = new PromptKeywordOptions("\nProfile views [Report/Top/Dims/All] <Report>: ") { AllowNone = true };
            foreach (string k in new[] { "Report", "Top", "Dims", "All" }) kw.Keywords.Add(k);
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Report" : kr.StringResult;

            ProfileViewsSettings s = ProfileViewsSettings.Load(doc.Name, out string problem);
            if (problem != null) { ed.WriteMessage($"\n{problem}\n"); return; }

            var pr = new PromptStringOptions($"\nAlignments (wildcards, comma separated) <{s.Alignments}>: ") { AllowSpaces = true };
            PromptResult ar = ed.GetString(pr);
            if (ar.Status != PromptStatus.OK) return;
            if (!string.IsNullOrWhiteSpace(ar.StringResult)) s.Alignments = ar.StringResult.Trim();

            var log = new StringBuilder();
            log.AppendLine($"C3DPROPV {mode} on {Path.GetFileName(doc.Name)}, alignments \"{s.Alignments}\", settings from " +
                           (File.Exists(ProjectSettings.PathFor(doc.Name)) ? ProjectSettings.PathFor(doc.Name) : "defaults (no project file)"));
            try
            {
                // Offsets, spacings and clearances are metres; a drawing in other units would be fitted with wrong tolerances.
                if (Features.Networks.Civil3D.ConduitCivil3DAccess.Metres(doc.Database.Insunits) != 1.0)
                    throw new InvalidOperationException($"the drawing units are {doc.Database.Insunits}; C3DPROPV works in metre drawings only. Nothing was changed.");
                CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                bool doTop = mode == "Top" || mode == "All", doDims = mode == "Dims" || mode == "All";
                TopStep(doc, civil, s, doTop, log);
                DimStep(doc, s, doDims, log);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROPV failed", ex);
                log.AppendLine($"C3DPROPV failed: {ex.Message}");
            }

            string file = Path.Combine(Path.GetTempPath(), "C3DPROPV.txt");
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
