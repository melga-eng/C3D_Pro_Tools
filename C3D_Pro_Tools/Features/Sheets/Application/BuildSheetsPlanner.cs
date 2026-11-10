using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

// The build, planned from the scene (docs/sheet-production-contract.md, sections 5 and 10): the steps that exist so far,
// PlanLabels and Arrange, joined into one item per plan sheet, with the preview digest that Apply checks, and the per-sheet,
// per-step states the Sheets panel shows. Plain data in and out, tested without Civil 3D.
namespace C3DProTools.Features.Sheets.Application
{
    internal static class BuildSheetsPlanner
    {
        /// <summary>The steps this version builds; the others are reported as not built yet.</summary>
        public static readonly IReadOnlyCollection<BuildStep> Built = new[] { BuildStep.PlanLabels, BuildStep.ProfileLabels, BuildStep.Arrange };

        /// <summary>The source key of a sheet's Arrange change.</summary>
        public const string ArrangeSource = "arrange";

        /// <summary>
        /// The preview of a build. <paramref name="rules"/> is the rule set to use (the service resolves it: the request's, or
        /// the project's). The preview's SceneStamp is the digest Apply must match.
        /// </summary>
        public static BuildSheetsPreview Preview(SheetScene scene, BuildSheetsRequest request, LabelRuleSet rules)
        {
            BuildSheetsRequest effective = request with { Rules = rules };
            List<OperationIssue> issues = new List<OperationIssue>(BuildValidation.ValidateRequest(effective));
            if (issues.Any(i => i.Severity == IssueSeverity.Error))
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request.Drawing, effective, issues);

            foreach (BuildStep step in request.Steps.Where(s => !Built.Contains(s)).OrderBy(s => s))
                issues.Add(OperationIssue.Info(SheetCodes.StepNotBuilt, $"The {step} step is not built yet; it is left out of this build."));

            bool plan = request.Steps.Contains(BuildStep.PlanLabels), profile = request.Steps.Contains(BuildStep.ProfileLabels),
                arrange = request.Steps.Contains(BuildStep.Arrange);
            PlanLabelsPlan labels = PlanLabelPlanner.Plan(scene, rules, request.Layouts);
            issues.AddRange(labels.Issues);
            if (labels.Issues.Any(i => i.Code == SheetCodes.SceneIncomplete))
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request.Drawing, effective, issues);
            ProfileLabelsPlan profiles = profile ? ProfileLabelPlanner.Plan(scene, rules, ProfileViews(scene), request.Layouts) : new ProfileLabelsPlan(null, null);
            issues.AddRange(profiles.Issues.Where(i => !issues.Contains(i)));
            if (profiles.Issues.Any(i => i.Code == SheetCodes.SceneIncomplete))
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request.Drawing, effective, issues);

            HashSet<string> wanted = new HashSet<string>(request.Layouts, StringComparer.OrdinalIgnoreCase);
            List<BuildItem> items = new List<BuildItem>();
            foreach (SheetSnapshot sheet in scene.Sheets.Where(s => s.Kind != SheetKind.Other && (wanted.Count == 0 || wanted.Contains(s.Layout))).OrderBy(s => s.TabOrder))
            {
                List<PlannedChange> changes = new List<PlannedChange>();
                bool isPlan = sheet.Kind == SheetKind.Plan;
                if (isPlan && plan) changes.AddRange(labels.Items.Where(i => i.Layouts.Contains(sheet.Layout, StringComparer.OrdinalIgnoreCase)).SelectMany(i => i.Changes));
                if (!isPlan && profile) changes.AddRange(profiles.Views.Where(v => string.Equals(v.Layout, sheet.Layout, StringComparison.OrdinalIgnoreCase)).SelectMany(v => v.Changes));
                if (arrange)
                {
                    int movable = Movable(scene, sheet.Layout).Count + changes.Count(c => c.Kind == ChangeKind.Add);
                    if (movable > 0)
                        changes.Add(new PlannedChange("", BuildStep.Arrange, ChangeKind.Update, ChangeTarget.Label, sheet.Layout, null, ArrangeSource, null,
                            $"Arrange {movable} tool label(s) clear of each other; pinned and hand-placed labels stay where they are"));
                }
                if (changes.Count == 0) continue;
                List<PlannedChange> numbered = changes.Select((c, i) => c with { ChangeId = $"{sheet.Layout}#{i + 1}" }).ToList();
                items.Add(new BuildItem((isPlan ? "plan:" : "profile:") + sheet.Layout, isPlan ? BuildItemKind.PlanSheet : BuildItemKind.ProfileSheetSet,
                    new[] { sheet.Layout }, numbered));
            }

            string digest = Digest(scene, effective, rules, items);
            return new BuildSheetsPreview(items.Count > 0 ? OperationStatus.Succeeded : OperationStatus.NoChange, Guid.NewGuid().ToString("N"),
                request.Drawing, digest, effective, items, issues);
        }

        /// <summary>The profile views shown on profile sheets, each with its sheet, keeping only views the planner accepts.</summary>
        public static List<ProfileViewLayout> ProfileViews(SheetScene scene)
        {
            Dictionary<string, ProfileViewSnapshot> views = scene.ProfileViews.GroupBy(v => v.Ref.Handle).ToDictionary(g => g.Key, g => g.First());
            return ProfileViewSheets.Map(scene).Where(m => views.TryGetValue(m.View.Handle, out ProfileViewSnapshot v) && v.Alignment.IsValid
                && double.IsFinite(v.StartStationM) && double.IsFinite(v.EndStationM) && v.EndStationM > v.StartStationM
                && v.DrawnParts.All(p => double.IsFinite(p.StartStationM) && double.IsFinite(p.EndStationM))).ToList();
        }

        /// <summary>The unpinned tool labels on a sheet: what Arrange may move.</summary>
        public static List<LabelSnapshot> Movable(SheetScene scene, string layout) =>
            scene.Labels.Where(l => l.IsToolLabel && !l.IsPinned && string.Equals(l.Tag.Layout, layout, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Everything the build depends on (section 10): the PlanLabels digest of the joined items (rules, changes, part
        /// geometry, style fingerprints, viewports), plus the requested steps and, for each sheet that is arranged, the current
        /// placement of every label there (tool, pinned and hand-placed alike: they are what Arrange works around).
        /// </summary>
        public static string Digest(SheetScene scene, BuildSheetsRequest request, LabelRuleSet rules, IReadOnlyList<BuildItem> items)
        {
            string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            b.Append(PlanLabelPlanner.Digest(new PlanLabelsPlan(items, null), scene, rules)).Append('\u001E');
            b.Append("steps\u001F").Append(string.Join(",", request.Steps.OrderBy(s => s))).Append('\u001E');
            foreach (string layout in items.Where(i => i.Changes.Any(c => c.Step == BuildStep.Arrange)).SelectMany(i => i.Layouts).OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
                foreach (LabelSnapshot l in scene.Labels.Where(l => string.Equals(l.Layout, layout, StringComparison.OrdinalIgnoreCase)).OrderBy(l => l.Ref.Handle, StringComparer.Ordinal))
                    b.Append(string.Join("\u001F", "arrangeInput", layout, l.Ref.Handle, l.Current?.StyleName, l.Current?.Dragged,
                        R(l.Current?.TextXM ?? 0), R(l.Current?.TextYM ?? 0), l.Current?.AnchorRatio.HasValue == true ? R(l.Current.AnchorRatio.Value) : "", l.IsToolLabel, l.IsPinned)).Append('\u001E');
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(b.ToString()))).ToLowerInvariant();
        }

        /// <summary>
        /// The Sheets panel's view of the drawing: for every plan and profile sheet, the state of each step.
        /// PlanLabels: NotBuilt (changes planned, no tool label yet), OutOfDate (changes planned, built before), Built (nothing to
        /// change), NotApplicable (no rule labels anything there). Arrange: Built when a build arranged it, else NotBuilt when it
        /// has tool labels. Profile sheets and the other steps: NotApplicable until they are built. Issues keep their sheet.
        /// </summary>
        public static SheetOverview Overview(SheetScene scene, LabelRuleSet rules, IEnumerable<OperationIssue> extraIssues = null)
        {
            List<OperationIssue> issues = new List<OperationIssue>(scene.Issues);
            if (extraIssues != null) issues.AddRange(extraIssues);
            PlanLabelsPlan plan = rules == null ? new PlanLabelsPlan(null, null) : PlanLabelPlanner.Plan(scene, rules, null);
            issues.AddRange(plan.Issues.Where(i => !scene.Issues.Contains(i)));
            HashSet<string> recorded = new HashSet<string>(scene.BuildRecords.Select(r => r.Layout + "|" + r.Step), StringComparer.OrdinalIgnoreCase);

            List<SheetStatus> sheets = new List<SheetStatus>();
            foreach (SheetSnapshot s in scene.Sheets.Where(s => s.Kind != SheetKind.Other).OrderBy(s => s.TabOrder))
            {
                Dictionary<BuildStep, StepState> steps = Enum.GetValues(typeof(BuildStep)).Cast<BuildStep>().ToDictionary(st => st, _ => StepState.NotApplicable);
                if (s.Kind == SheetKind.Plan && rules != null)
                {
                    bool changes = plan.Items.Any(i => i.Layouts.Contains(s.Layout, StringComparer.OrdinalIgnoreCase));
                    bool hasToolLabels = scene.Labels.Any(l => l.IsToolLabel && string.Equals(l.Tag.Layout, s.Layout, StringComparison.OrdinalIgnoreCase));
                    steps[BuildStep.PlanLabels] = changes ? (hasToolLabels ? StepState.OutOfDate : StepState.NotBuilt) : hasToolLabels ? StepState.Built : StepState.NotApplicable;
                    steps[BuildStep.Arrange] = !hasToolLabels && !changes ? StepState.NotApplicable
                        : recorded.Contains(s.Layout + "|" + BuildStep.Arrange) && !changes ? StepState.Built : StepState.NotBuilt;
                }
                int count = issues.Count(i => string.Equals(i.Layout, s.Layout, StringComparison.OrdinalIgnoreCase));
                if (count > 0 && issues.Any(i => i.Severity != IssueSeverity.Info && string.Equals(i.Layout, s.Layout, StringComparison.OrdinalIgnoreCase)))
                    foreach (BuildStep st in steps.Keys.ToList().Where(st => steps[st] == StepState.Built)) steps[st] = StepState.HasIssues;
                sheets.Add(new SheetStatus(s.Layout, s.Kind, s.Tile, s.Part, s.DrawingNumber, steps, count));
            }
            return new SheetOverview(scene.Drawing, sheets, issues);
        }
    }
}
