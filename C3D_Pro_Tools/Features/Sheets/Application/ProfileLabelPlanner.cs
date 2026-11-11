using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Application
{
    /// <summary>Supplied by the adapter: the scene itself has no profile-view-to-layout association. Supply all sheeted views.</summary>
    internal sealed record ProfileViewLayout(DrawingObjectRef View, string Layout);

    /// <summary>Changes for one view, not an Apply transaction boundary. The workflow groups profile sheets by tile.</summary>
    internal sealed record ProfileLabelViewPlan(DrawingObjectRef View, string Layout, IReadOnlyList<PlannedChange> Changes)
    {
        private readonly IReadOnlyList<PlannedChange> _changes = Frozen.List(Changes);
        public IReadOnlyList<PlannedChange> Changes { get => _changes; init => _changes = Frozen.List(value); }
    }

    internal sealed record ProfileLabelsPlan(IReadOnlyList<ProfileLabelViewPlan> Views, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<ProfileLabelViewPlan> _views = Frozen.List(Views);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<ProfileLabelViewPlan> Views { get => _views; init => _views = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>Pure profile-rule reconciliation. Drawing writes and label placement belong to later adapters/Arrange.</summary>
    internal static class ProfileLabelPlanner
    {
        private const double Epsilon = 1e-9;
        private const string ViewMarker = ":view:";
        private sealed record Context(ProfileViewSnapshot View, SheetSnapshot Sheet);
        private sealed record Target(Context Context, DrawingObjectRef Part, string Network, string Type, bool Other,
            bool Pipe, double Start, double End, double Station, bool Crossing);

        /// <summary>
        /// Source identity includes the view because a part can have labels in two views on the same layout. The other identity
        /// fields remain rule/source/layout/style slot. Writers must persist this exact key; no shared contract is changed.
        /// </summary>
        public static string SourceKey(DrawingObjectRef part, DrawingObjectRef view, bool joint = false) =>
            part.Handle + (joint ? ":start" : "") + ViewMarker + view.Handle;

        public static ProfileLabelsPlan Plan(SheetScene scene, LabelRuleSet rules, IReadOnlyList<ProfileViewLayout> viewLayouts,
            IReadOnlyCollection<string> layouts = null)
        {
            List<OperationIssue> issues = new();
            if (scene == null) return Invalid("There is no sheet scene.");
            var blocking = scene.Issues.Where(i => i.Severity == IssueSeverity.Error || i.Code == SheetCodes.SceneReadFailed).ToList();
            if (blocking.Count > 0)
                return new ProfileLabelsPlan(null, blocking.Append(OperationIssue.Error(SheetCodes.SceneIncomplete,
                    "The scene is incomplete, so no profile labels are planned; fix the read problems first.")).ToArray());
            issues.AddRange(LabelRuleValidation.Validate(rules));
            if (issues.Count > 0) return new ProfileLabelsPlan(null, issues);

            Dictionary<string, Context> contexts = new(StringComparer.OrdinalIgnoreCase);
            foreach (ProfileViewLayout binding in viewLayouts ?? Array.Empty<ProfileViewLayout>())
            {
                ProfileViewSnapshot view = binding == null ? null : scene.ProfileViews.FirstOrDefault(v => v.Ref == binding.View);
                SheetSnapshot sheet = binding == null ? null : scene.Sheets.FirstOrDefault(s => s.Kind == SheetKind.Profile
                    && string.Equals(s.Layout, binding.Layout, StringComparison.OrdinalIgnoreCase));
                if (view == null || sheet == null || !view.Ref.IsValid || !view.Alignment.IsValid
                    || !double.IsFinite(view.StartStationM) || !double.IsFinite(view.EndStationM)
                    || view.EndStationM <= view.StartStationM || !contexts.TryAdd(view.Ref.Handle, new Context(view, sheet)))
                    return Invalid("Each profile view must map once to a known profile sheet and have a finite, increasing station range.");
            }
            var all = contexts.Values.OrderBy(c => c.View.StartStationM).ThenBy(c => c.View.EndStationM)
                .ThenBy(c => Handle(c.View.Ref)).ToList();
            HashSet<string> wanted = new(layouts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var scope = all.Where(c => wanted.Count == 0 || wanted.Contains(c.Sheet.Layout)).ToList();
            var scopeViews = new HashSet<string>(scope.Select(c => c.View.Ref.Handle), StringComparer.OrdinalIgnoreCase);
            foreach (string layout in wanted.OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
                if (!all.Any(c => string.Equals(c.Sheet.Layout, layout, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(OperationIssue.Info(SheetCodes.RequestLayout, $"Sheet '{layout}' has no mapped profile view.") with { Layout = layout });

            var pipes = scene.Networks.SelectMany(n => n.Pipes).GroupBy(p => p.Ref.Handle).ToDictionary(g => g.Key, g => g.First());
            var structures = scene.Networks.SelectMany(n => n.Structures).GroupBy(p => p.Ref.Handle).ToDictionary(g => g.Key, g => g.First());
            List<Target> targets = new();
            foreach (Context context in all)
            {
                foreach (ViewPart part in context.View.DrawnParts)
                {
                    if (!double.IsFinite(part.StartStationM) || !double.IsFinite(part.EndStationM))
                        return Invalid("A drawn part has non-finite stations.");
                    if (part.Kind == ViewPartKind.Pipe && part.IsOwnNetwork)
                    {
                        if (!pipes.TryGetValue(part.Part.Handle ?? "", out ScenePipe pipe)) return Invalid("A drawn pipe is missing from the scene's networks.");
                        targets.Add(new Target(context, part.Part, pipe.Network, pipe.TypeCode, false, true,
                            part.StartStationM, part.EndStationM, (part.StartStationM + part.EndStationM) / 2, false));
                    }
                    else if (part.Kind == ViewPartKind.Structure)
                    {
                        if (!structures.TryGetValue(part.Part.Handle ?? "", out SceneStructure structure)) return Invalid("A drawn structure is missing from the scene's networks.");
                        targets.Add(new Target(context, part.Part, structure.Network, structure.TypeName, !part.IsOwnNetwork, false,
                            part.StartStationM, part.EndStationM, (part.StartStationM + part.EndStationM) / 2, false));
                    }
                }
                foreach (CrossingSnapshot crossing in scene.Crossings.Where(c => c.ProfileView == context.View.Ref && c.ShownInView))
                {
                    if (!double.IsFinite(crossing.StationM)) return Invalid("A crossing has a non-finite station.");
                    if (context.View.DrawnParts.Any(p => p.Part == crossing.CrossingPipe && p.IsOwnNetwork)) continue;
                    if (!pipes.TryGetValue(crossing.CrossingPipe.Handle ?? "", out ScenePipe pipe)) return Invalid("A crossing pipe is missing from the scene's networks.");
                    targets.Add(new Target(context, crossing.CrossingPipe, crossing.CrossingNetwork, pipe.TypeCode, true, true,
                        crossing.StationM, crossing.StationM, crossing.StationM, true));
                }
            }

            var usable = new List<LabelRule>();
            HashSet<string> skipped = new(StringComparer.OrdinalIgnoreCase);
            foreach (LabelRule rule in rules.Rules.Where(r => r.View == ViewKind.Profile))
            {
                if (rule.Select.Kind == TargetKind.Alignment || rule.Select.Kind == TargetKind.AlignmentStations
                    || rule.Every != LabelFrequency.PerPart)
                {
                    issues.Add(OperationIssue.Info(rule.Every != LabelFrequency.PerPart ? SheetCodes.RuleFrequency : SheetCodes.RuleAnchor,
                        $"Rule '{rule.Id}' is not a per-part pipe, structure or joint rule; it is skipped."));
                    skipped.Add(rule.Id.Trim()); continue;
                }
                LabelStyleKind kind = StyleKindOf(rule);
                bool missing = rule.LabelStyles.Any(slot => !scene.LabelStyles.Any(s => s.Kind == kind && s.Name == slot.Trim()));
                bool incomplete = rule.LabelStyles.Any(slot => scene.LabelStyles.Any(s => s.Kind == kind && s.Name == slot.Trim() && string.IsNullOrEmpty(s.Fingerprint)));
                if (missing || incomplete)
                {
                    issues.Add(OperationIssue.Error(missing ? SheetCodes.RuleStyleMissing : SheetCodes.FingerprintIncomplete,
                        $"Rule '{rule.Id}' has a missing, wrong-kind or incompletely fingerprinted style. The whole rule is skipped and its labels are kept."));
                    skipped.Add(rule.Id.Trim()); continue;
                }
                usable.Add(rule);
            }

            Dictionary<string, (Context Context, PlannedChange Change)> desired = new(StringComparer.Ordinal);
            foreach (LabelRule rule in usable)
            {
                var matching = targets.Where(t => t.Other == rule.Select.OtherNetworksOnly
                    && t.Pipe == (rule.Select.Kind != TargetKind.Structure)
                    && (!t.Crossing || rule.Select.Kind == TargetKind.Pipe)
                    && rule.Select.Matches(t.Network, t.Type)).ToList();
                foreach (Target target in matching)
                {
                    Context context = target.Context;
                    double from = context.View.StartStationM, to = context.View.EndStationM;
                    var earlier = matching.Where(t => t.Part == target.Part && t.Context.View.Alignment == context.View.Alignment
                        && all.IndexOf(t.Context) < all.IndexOf(context)).ToList();
                    bool joint = rule.Select.Kind == TargetKind.Joint;
                    double station = target.Pipe && !joint && !target.Crossing
                        ? rule.At == LabelAnchor.Start ? target.Start : rule.At == LabelAnchor.End ? target.End : target.Station
                        : joint ? target.Start : target.Station;
                    if (target.Pipe && !joint && !target.Crossing)
                    {
                        double low = Math.Max(from, Math.Min(target.Start, target.End)), high = Math.Min(to, Math.Max(target.Start, target.End));
                        double covered = earlier.Select(t => t.Context.View.EndStationM).DefaultIfEmpty(double.NegativeInfinity).Max();
                        low = Math.Max(low, covered);
                        if (high - low <= Epsilon) continue;
                        // Keep the preferred anchor when visible; otherwise use the centre of the visible, owned portion.
                        if (station < low - Epsilon || station > high + Epsilon || station <= covered + Epsilon) station = (low + high) / 2;
                    }
                    else if (station < from - Epsilon || station > to + Epsilon
                        || earlier.Any(t => station >= t.Context.View.StartStationM - Epsilon && station <= t.Context.View.EndStationM + Epsilon)) continue;
                    if (!scopeViews.Contains(context.View.Ref.Handle)) continue;
                    double? ratio = target.Pipe ? Math.Abs(target.End - target.Start) > Epsilon
                        ? Math.Clamp((station - target.Start) / (target.End - target.Start), 0, 1) : 0.5 : null;
                    if (joint) ratio = 0;
                    foreach (string raw in rule.LabelStyles)
                    {
                        string slot = raw.Trim(), source = SourceKey(target.Part, context.View.Ref, joint);
                        string identity = new BuildTag(rule.Id, source, context.Sheet.Layout, slot, PinState.Auto, null).IdentityKey;
                        desired.TryAdd(identity, (context, new PlannedChange("", BuildStep.ProfileLabels, ChangeKind.Add, ChangeTarget.Label,
                            context.Sheet.Layout, rule.Id, source, null, $"Add profile {rule.Select.Kind} label in '{context.View.Name}' ({slot})")
                            { Part = target.Part, Ratio = ratio, StyleSlot = slot }));
                    }
                }
            }

            // Existing tool labels, by where they are: a profile label sits in its view, so when the view moved to another
            // sheet (or off every sheet) since the build, its labels went with it. Such a label is reconciled when the sheet it
            // was tagged for or the sheet now showing its view is in scope; its tag names the old sheet, so it never matches a
            // desired label and is removed (or, pinned, kept and reported).
            var existing = new Dictionary<string, LabelSnapshot>(StringComparer.Ordinal);
            var located = new Dictionary<LabelSnapshot, Context>(ReferenceEqualityComparer.Instance);
            foreach (LabelSnapshot l in scene.Labels.Where(l => l.IsToolLabel))
            {
                Context c = ContextOf(l.Tag, contexts, scene, wanted, scopeViews);
                if (c != null) located[l] = c;
            }
            foreach (var group in located.Keys.GroupBy(l => l.Tag.IdentityKey, StringComparer.Ordinal))
            {
                var copies = group.OrderBy(l => Handle(l.Ref)).ToList(); existing[group.Key] = copies[0];
                foreach (LabelSnapshot copy in copies.Skip(1))
                    issues.Add(OperationIssue.Warning(SheetCodes.LabelDuplicateTag, "Copied profile tag: this label is treated as hand-placed.", obj: copy.Ref) with { Layout = copy.Tag.Layout });
            }
            var changes = desired.Where(d => !existing.ContainsKey(d.Key)).Select(d => d.Value).ToList();
            foreach (var pair in existing)
            {
                LabelSnapshot label = pair.Value;
                if (desired.ContainsKey(pair.Key) || skipped.Contains((label.Tag.RuleId?.Trim() ?? ""))) continue;
                if (label.IsPinned)
                {
                    issues.Add(OperationIssue.Warning(SheetCodes.LabelOrphanPinned, "A pinned profile label is no longer requested; it is kept.", obj: label.Ref) with { Layout = label.Tag.Layout });
                    continue;
                }
                changes.Add((located[label], new PlannedChange("", BuildStep.ProfileLabels, ChangeKind.Remove, ChangeTarget.Label,
                    label.Tag.Layout, label.Tag.RuleId, label.Tag.SourceKey, label.Ref, "Remove profile label no rule requests") { StyleSlot = label.Tag.StyleSlot }));
            }
            var results = new List<ProfileLabelViewPlan>();
            foreach (Context context in scope.Concat(changes.Select(c => c.Context)).Distinct().OrderBy(c => c.Sheet.TabOrder).ThenBy(c => c.View.StartStationM).ThenBy(c => Handle(c.View.Ref)))
            {
                var mine = changes.Where(c => c.Context == context).Select(c => c.Change).OrderBy(c => c.Kind)
                    .ThenBy(c => c.RuleId, StringComparer.Ordinal).ThenBy(c => c.SourceKey, StringComparer.Ordinal)
                    .ThenBy(c => StyleOrder(c, rules)).ThenBy(c => c.StyleSlot, StringComparer.Ordinal).ToList();
                if (mine.Count > 0) results.Add(new ProfileLabelViewPlan(context.View.Ref, context.Sheet.Layout,
                    mine.Select((c, i) => c with { ChangeId = $"profile:{context.Sheet.Layout}:{context.View.Ref.Handle}#{i + 1}" }).ToArray()));
            }
            return new ProfileLabelsPlan(results, issues);
        }

        /// <summary>
        /// The kind of label style a profile rule uses: crossing pipes (a pipe rule for other networks) take crossing pipe
        /// profile label styles, other pipe and joint rules pipe label styles, structure rules structure label styles.
        /// </summary>
        public static LabelStyleKind StyleKindOf(LabelRule rule) =>
            rule.Select.Kind == TargetKind.Structure ? LabelStyleKind.Structure
            : rule.Select.Kind == TargetKind.Pipe && rule.Select.OtherNetworksOnly ? LabelStyleKind.CrossingPipe : LabelStyleKind.Pipe;

        /// <summary>The profile view a profile label's source key names (see <see cref="SourceKey"/>); default when none.</summary>
        public static DrawingObjectRef ViewOf(string sourceKey)
        {
            string s = sourceKey ?? "";
            int at = s.LastIndexOf(ViewMarker, StringComparison.OrdinalIgnoreCase);
            return at >= 0 && DrawingObjectRef.TryCreate(s.Substring(at + ViewMarker.Length), out DrawingObjectRef r) ? r : default;
        }

        private static ProfileLabelsPlan Invalid(string message) => new(null, new[] { OperationIssue.Error(SheetCodes.SceneIncomplete, message) });
        /// <summary>
        /// Where an existing tool label is reconciled, or null when it is out of scope or not a profile label:
        /// - its view still shows on the sheet it was tagged for: that view and sheet, when in scope;
        /// - its view now shows on another sheet: that sheet when in scope (the label is there now), else the tagged sheet when
        ///   in scope (it was built for it);
        /// - its view shows on no sheet: the tagged sheet, when in scope and still a profile sheet.
        /// A label whose view is gone from the drawing went with it.
        /// </summary>
        private static Context ContextOf(BuildTag tag, Dictionary<string, Context> contexts, SheetScene scene, HashSet<string> wanted, HashSet<string> scopeViews)
        {
            string source = tag.SourceKey ?? "";
            int at = source.LastIndexOf(ViewMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string viewHandle = source.Substring(at + ViewMarker.Length);
            bool InScope(string layout) => wanted.Count == 0 || wanted.Contains(layout ?? "");
            if (contexts.TryGetValue(viewHandle, out Context now))
            {
                if (string.Equals(now.Sheet.Layout, tag.Layout, StringComparison.OrdinalIgnoreCase)) return scopeViews.Contains(viewHandle) ? now : null;
                if (scopeViews.Contains(viewHandle)) return now;
            }
            ProfileViewSnapshot view = now?.View ?? scene.ProfileViews.FirstOrDefault(v => string.Equals(v.Ref.Handle, viewHandle, StringComparison.OrdinalIgnoreCase));
            SheetSnapshot tagged = scene.Sheets.FirstOrDefault(s => s.Kind == SheetKind.Profile && string.Equals(s.Layout, tag.Layout, StringComparison.OrdinalIgnoreCase));
            return view != null && tagged != null && InScope(tagged.Layout) ? new Context(view, tagged) : null;
        }
        private static int StyleOrder(PlannedChange change, LabelRuleSet rules)
        {
            LabelRule rule = rules.Rules.FirstOrDefault(r => string.Equals(r.Id, change.RuleId, StringComparison.OrdinalIgnoreCase));
            return rule == null ? int.MaxValue : rule.LabelStyles.ToList().FindIndex(s => string.Equals(s.Trim(), change.StyleSlot, StringComparison.OrdinalIgnoreCase));
        }
        private static ulong Handle(DrawingObjectRef reference) => ulong.TryParse(reference.Handle, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out ulong value) ? value : ulong.MaxValue;
    }
}
