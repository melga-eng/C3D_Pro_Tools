using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.Features.Sheets.Civil3D.SheetCommands))]

namespace C3DProTools.Features.Sheets.Civil3D
{
    /// <summary>Sheet production commands. Phase 1 so far: a read-only report of what the scene reader sees.</summary>
    public class SheetCommands
    {
        /// <summary>
        /// Reads the sheet scene of the active drawing and writes a summary to the command line and to
        /// %TEMP%\C3DProSceneReport.txt, with the content fingerprints of the label, view and dimension styles. Changes nothing.
        /// </summary>
        [CommandMethod("C3DProSceneReport")]
        public void SceneReport()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                DateTime t0 = DateTime.Now;
                SheetScene scene = new SheetSceneCivil3D().Read(new SheetSceneRequest { Drawing = DrawingRefs3D.Identity(doc) }, CancellationToken.None);
                string report = Report(scene, doc, DateTime.Now - t0);
                string file = Path.Combine(Path.GetTempPath(), "C3DProSceneReport.txt");
                File.WriteAllText(file, report);
                doc.Editor.WriteMessage("\n" + string.Join("\n", report.Split('\n').Take(40)) + $"\n… full report: {file}\n");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("Scene report failed", ex);
                doc.Editor.WriteMessage($"\nScene report failed: {ex.Message}\n");
            }
        }

        /// <summary>
        /// The PlanLabels step on its own, with the rules in C3DPro.project.json beside the drawing. "Preview" (the default)
        /// writes the plan to %TEMP%\C3DProPlanLabels.txt and changes nothing; "Apply" then writes it, one sheet at a time, each
        /// sheet whole or not at all. Optional sheet names after the keyword limit it to those sheets. The full build (arranging,
        /// profile views, DIMs) comes with the workspace's Build.
        /// </summary>
        [CommandMethod("C3DProPlanLabels")]
        public void PlanLabels()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            try
            {
                var kw = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions("\nPlan labels [Preview/Apply] <Preview>: ") { AllowNone = true };
                kw.Keywords.Add("Preview"); kw.Keywords.Add("Apply");
                kw.Keywords.Default = "Preview";
                var r = ed.GetKeywords(kw);
                if (r.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK && r.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.None) return;
                bool apply = r.StringResult == "Apply";
                var sheetsPrompt = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\nSheets (comma separated, Enter for every plan sheet): ") { AllowSpaces = true };
                var sr = ed.GetString(sheetsPrompt);
                if (sr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                List<string> layouts = (sr.StringResult ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

                Application.ProjectSettings settings = Application.ProjectSettings.Load(doc.Name, out string problem);
                StringBuilder b = new StringBuilder();
                b.AppendLine($"Plan labels for {doc.Name} ({(apply ? "apply" : "preview")}), rules from {Application.ProjectSettings.PathFor(doc.Name)}");
                if (problem != null) b.AppendLine("Problem: " + problem);

                // Several rule sets: the user names one; never a silent first pick.
                LabelRuleSet rules = settings.RuleSet(null);
                if (rules == null && settings.LabelRules.Count > 1)
                {
                    string names = string.Join(", ", settings.LabelRules.Select(r => $"\"{r?.Name}\""));
                    var rp = new Autodesk.AutoCAD.EditorInput.PromptStringOptions($"\nRule set ({names}): ") { AllowSpaces = true };
                    var rr = ed.GetString(rp);
                    if (rr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                    rules = settings.RuleSet(rr.StringResult);
                    if (rules == null)
                    {
                        b.AppendLine($"{SheetCodes.RulesAmbiguous}: \"{rr.StringResult}\" is not one of {names}. Nothing planned.");
                        Finish(doc, b, "C3DProPlanLabels.txt");
                        return;
                    }
                }
                IReadOnlyList<OperationIssue> ruleIssues = LabelRuleValidation.Validate(rules);
                if (rules == null || ruleIssues.Count > 0)
                {
                    b.AppendLine(rules == null ? "No label rule set in the settings file: nothing planned." : "The rule set has errors: nothing planned.");
                    foreach (OperationIssue i in ruleIssues) b.AppendLine($"  {i.Code}: {i.Message}");
                    Finish(doc, b, "C3DProPlanLabels.txt");
                    return;
                }

                // Every sheet is read, so which sheet owns a part is decided over all of them; the plan then changes only the
                // chosen sheets.
                DrawingIdentity identity = DrawingRefs3D.Identity(doc);
                SheetScene scene = new SheetSceneCivil3D().Read(new SheetSceneRequest { Drawing = identity }, CancellationToken.None);
                int sceneWarnings = scene.Issues.Count(i => i.Severity == IssueSeverity.Warning);
                if (sceneWarnings > 0) b.AppendLine($"Reading the drawing gave {sceneWarnings} warning(s); run C3DProSceneReport for the list.");
                Application.PlanLabelsPlan plan = Application.PlanLabelPlanner.Plan(scene, rules, layouts);
                string digest = Application.PlanLabelPlanner.Digest(plan, scene, rules);
                string previewKey = $"{identity.SessionKey}|{rules.Name}|{string.Join(",", layouts.Select(l => l.ToUpperInvariant()).OrderBy(l => l))}";
                b.AppendLine($"Rule set \"{rules.Name}\" v{rules.Version}: {plan.Items.Count} sheet(s) to change, "
                    + $"{plan.Items.Sum(i => i.Changes.Count(c => c.Kind == ChangeKind.Add))} label(s) to add, {plan.Items.Sum(i => i.Changes.Count(c => c.Kind == ChangeKind.Remove))} to remove.");
                foreach (OperationIssue i in plan.Issues) b.AppendLine($"  {i.Severity} {i.Code} {i.Layout} {i.Object}: {i.Message}");
                foreach (BuildItem item in plan.Items)
                {
                    b.AppendLine($"{item.ItemKey}: {item.Changes.Count} change(s)");
                    foreach (PlannedChange c in item.Changes.Take(200)) b.AppendLine($"  {c.ChangeId}  {c.Kind}  {c.RuleId}  {c.SourceKey}  {c.Description}");
                }

                // Apply writes only the plan the user reviewed: the same changes as the last preview for this drawing, rule set
                // and sheets. Anything else becomes the new preview, to be reviewed first.
                bool reviewed = PreviewedPlans.TryGetValue(previewKey, out string previewed) && previewed == digest;
                if (!apply || !reviewed)
                {
                    if (apply)
                        b.AppendLine($"{SheetCodes.PlanChanged}: nothing written. "
                            + (PreviewedPlans.ContainsKey(previewKey) ? "The plan is not the one you previewed (the rules or the drawing changed)." : "There is no preview of this plan yet.")
                            + " The plan above is the new preview: review it, then run Apply again.");
                    PreviewedPlans[previewKey] = digest;
                }
                else if (plan.Items.Count > 0)
                {
                    PreviewedPlans.Remove(previewKey);
                    b.AppendLine();
                    b.AppendLine("Apply (the previewed plan):");
                    using (doc.LockDocument())
                        foreach (BuildItem item in plan.Items)
                        {
                            BuildItemOutcome o = PlanLabelsCivil3D.Apply(doc, item);
                            b.AppendLine($"  {item.ItemKey}: {o.Status}, added {o.Added}, removed {o.Removed}");
                            foreach (OperationIssue i in o.Issues) b.AppendLine($"    {i.Code}: {i.Message}");
                        }
                }
                Finish(doc, b, "C3DProPlanLabels.txt");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("Plan labels failed", ex);
                ed.WriteMessage($"\nPlan labels failed: {ex.Message}\n");
            }
        }

        /// <summary>The digest of the last previewed plan, by drawing session, rule set and sheets (this Civil 3D session only).</summary>
        private static readonly Dictionary<string, string> PreviewedPlans = new Dictionary<string, string>(StringComparer.Ordinal);

        private static void Finish(Document doc, StringBuilder b, string fileName)
        {
            string file = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(file, b.ToString());
            doc.Editor.WriteMessage("\n" + string.Join("\n", b.ToString().Split('\n').Take(30)) + $"\n… full report: {file}\n");
        }

        private static string Report(SheetScene s, Document doc, TimeSpan took)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine($"Sheet scene of {doc.Name} (contract {SheetContract.Version}), read in {took.TotalSeconds:0.0} s");
            b.AppendLine($"Sheets: {s.Sheets.Count} (plan {s.Sheets.Count(x => x.Kind == SheetKind.Plan)}, profile {s.Sheets.Count(x => x.Kind == SheetKind.Profile)}, other {s.Sheets.Count(x => x.Kind == SheetKind.Other)})");
            b.AppendLine($"Networks: {s.Networks.Count}; pipes {s.Networks.Sum(n => n.Pipes.Count)} ({s.Networks.Sum(n => n.Pipes.Count(p => p.TypeCode.Length > 0))} with a type code); structures {s.Networks.Sum(n => n.Structures.Count)}");
            b.AppendLine($"Alignments: {s.Alignments.Count}; profiles {s.Alignments.Sum(a => a.Profiles.Count)} (top {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Top))}, proposed {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Proposed))}, existing {s.Alignments.Sum(a => a.Profiles.Count(p => p.Role == SceneProfileRole.Existing))})");
            b.AppendLine($"Profile views: {s.ProfileViews.Count}; crossings {s.Crossings.Count} ({s.Crossings.Count(c => c.ShownInView)} drawn in their view)");
            b.AppendLine($"Labels: {s.Labels.Count} ({s.Labels.Count(l => l.IsToolLabel)} tool labels, {s.Labels.Count(l => l.Layout.Length > 0)} on a sheet); build records {s.BuildRecords.Count}");
            b.AppendLine($"Issues: {s.Issues.Count}");
            foreach (OperationIssue i in s.Issues.Take(20)) b.AppendLine($"  {i.Severity} {i.Code} {i.Object}: {i.Message}");

            b.AppendLine();
            b.AppendLine("Sheets (layout, kind, tile-part, viewports as scale / twist / locked):");
            foreach (SheetSnapshot sh in s.Sheets.OrderBy(x => x.TabOrder))
                b.AppendLine($"  {sh.Layout}  {sh.Kind}  {(sh.Tile.Length > 0 ? sh.Tile + "-" + sh.Part : "")}  "
                    + string.Join("; ", sh.Viewports.Select(v => $"1:{1000 / v.PaperMmPerModelM:0} / {v.TwistDeg:0.#}° / {(v.Locked ? "locked" : "unlocked")}")));

            b.AppendLine();
            b.AppendLine("Profile views (name, stations, exaggeration, parts own/other, crossings):");
            foreach (ProfileViewSnapshot v in s.ProfileViews.Take(60))
                b.AppendLine($"  {v.Name}  {v.StartStationM:0.00}–{v.EndStationM:0.00}  ×{v.VerticalExaggeration:0.#}  {v.DrawnParts.Count(p => p.IsOwnNetwork)}/{v.DrawnParts.Count(p => !p.IsOwnNetwork)}  {s.Crossings.Count(c => c.ProfileView == v.Ref)}");

            b.AppendLine();
            b.AppendLine("Style fingerprints (label, profile view and dimension styles):");
            foreach (string line in Fingerprints(doc)) b.AppendLine("  " + line);
            return b.ToString();
        }

        /// <summary>The fingerprints of the drawing's styles, to check live that an edited style gets a new one.</summary>
        private static IEnumerable<string> Fingerprints(Document doc)
        {
            List<string> lines = new List<string>();
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                void Add(string kind, ObjectId id)
                {
                    if (id.IsNull || id.IsErased) return;
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string name = (o as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name ?? (o as SymbolTableRecord)?.Name ?? o.GetType().Name;
                    lines.Add($"{kind}  {name}  {(StyleFingerprint.Of(tr, o) is string f && f.Length > 0 ? f : "(incomplete: see the log)")}");
                }
                try { foreach (ObjectId id in civil.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles) Add("pipe label", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { foreach (ObjectId id in civil.Styles.LabelStyles.StructureLabelStyles.LabelStyles) Add("structure label", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { foreach (ObjectId id in civil.Styles.ProfileViewStyles) Add("profile view style", id); } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                DimStyleTable dims = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dims) Add("dim style", id);
                tr.Commit();
            }
            return lines;
        }
    }
}
