using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

// The PlanLabels build step, planned from the scene alone (docs/sheet-production-contract.md, section 4): which part gets which
// label on which plan sheet, compared with the tool labels already there. Adds what is missing, removes tool labels no rule asks
// for any more, and never touches a pinned or hand-placed label. Placement is the Arrange step's job, not this one's. Plain data
// in and out, so it is tested without Civil 3D.
namespace C3DProTools.Features.Sheets.Application
{
    /// <summary>The planned PlanLabels changes: one item per plan sheet that changes, and what the plan noticed.</summary>
    internal sealed record PlanLabelsPlan(IReadOnlyList<BuildItem> Items, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<BuildItem> _items = Frozen.List(Items);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<BuildItem> Items { get => _items; init => _items = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    internal static class PlanLabelPlanner
    {
        /// <summary>The source key of a joint label: the pipe whose start it labels.</summary>
        public static string JointKey(DrawingObjectRef pipe) => pipe.Handle + ":start";

        /// <summary>
        /// Plans the plan labels of the plan sheets in <paramref name="layouts"/> (empty: every plan sheet). Rules for profiles,
        /// alignments and station labels belong to other steps and are left out here.
        /// </summary>
        public static PlanLabelsPlan Plan(SheetScene scene, LabelRuleSet rules, IReadOnlyCollection<string> layouts)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            HashSet<string> wanted = new HashSet<string>(layouts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            List<SheetSnapshot> sheets = scene.Sheets.Where(s => s.Kind == SheetKind.Plan && (wanted.Count == 0 || wanted.Contains(s.Layout)))
                .OrderBy(s => s.TabOrder).ToList();
            HashSet<string> inScope = new HashSet<string>(sheets.Select(s => s.Layout), StringComparer.OrdinalIgnoreCase);
            foreach (string w in wanted.Where(w => !scene.Sheets.Any(s => s.Kind == SheetKind.Plan && s.Layout.Equals(w, StringComparison.OrdinalIgnoreCase))))
                issues.Add(OperationIssue.Info(SheetCodes.RequestLayout, $"\"{w}\" is not a plan sheet; it gets no plan labels.") with { Layout = w });

            // ---- What the rules ask for ----------------------------------------------------------------------------------
            List<(LabelRule rule, string slot)> usable = new List<(LabelRule, string)>();
            foreach (LabelRule rule in rules.Rules.Where(r => r != null && r.View == ViewKind.Plan))
            {
                TargetKind kind = rule.Select.Kind;
                if (kind == TargetKind.Alignment || kind == TargetKind.AlignmentStations)
                {
                    issues.Add(OperationIssue.Info(SheetCodes.RuleAnchor, $"Rule \"{rule.Id}\": alignment labels are not built yet; it is left out."));
                    continue;
                }
                if (rule.Every == LabelFrequency.PerRun)
                    issues.Add(OperationIssue.Info(SheetCodes.RuleFrequency, $"Rule \"{rule.Id}\": one label per run is not built yet; each pipe gets its own label."));
                LabelStyleKind styleKind = kind == TargetKind.Structure ? LabelStyleKind.Structure : LabelStyleKind.Pipe;
                List<string> missing = rule.LabelStyles.Where(st => !scene.LabelStyles.Any(x => x.Kind == styleKind && x.Name.Equals(st.Trim(), StringComparison.Ordinal))).ToList();
                if (missing.Count > 0)
                {
                    // The whole rule is skipped, its existing labels included: a half-applied rule would leave sheets inconsistent.
                    issues.Add(OperationIssue.Error(SheetCodes.RuleStyleMissing,
                        $"Rule \"{rule.Id}\": the {styleKind.ToString().ToLowerInvariant()} label style{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing.Select(m => $"\"{m}\""))} {(missing.Count > 1 ? "are" : "is")} not in this drawing. The rule is skipped and its labels are left as they are."));
                    continue;
                }
                foreach (string style in rule.LabelStyles) usable.Add((rule, style.Trim()));
            }
            HashSet<string> skippedRules = new HashSet<string>(rules.Rules.Where(r => r != null && r.View == ViewKind.Plan).Select(r => r.Id.Trim())
                .Where(id => !usable.Any(u => u.rule.Id.Trim().Equals(id, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);

            // Every desired label: identity → what to create.
            Dictionary<string, PlannedChange> desired = new Dictionary<string, PlannedChange>(StringComparer.Ordinal);
            foreach ((LabelRule rule, string slot) in usable)
                foreach ((string source, DrawingObjectRef part, double? ratio, string layout, string what) in Targets(scene, sheets, rule))
                {
                    BuildTag key = new BuildTag(rule.Id, source, layout, slot, PinState.Auto, null);
                    if (desired.ContainsKey(key.IdentityKey)) continue;
                    desired[key.IdentityKey] = new PlannedChange("", BuildStep.PlanLabels, ChangeKind.Add, ChangeTarget.Label, layout, rule.Id, source, null,
                        $"Add {what} label ({slot})") { StyleSlot = slot, Part = part, Ratio = ratio };
                }

            // ---- What is there already -----------------------------------------------------------------------------------
            Dictionary<string, LabelSnapshot> existing = new Dictionary<string, LabelSnapshot>(StringComparer.Ordinal);
            foreach (IGrouping<string, LabelSnapshot> g in scene.Labels.Where(l => l.IsToolLabel && inScope.Contains(l.Tag.Layout ?? ""))
                         .GroupBy(l => l.Tag.IdentityKey, StringComparer.Ordinal))
            {
                List<LabelSnapshot> copies = g.OrderBy(l => HandleValue(l.Ref)).ToList();
                existing[g.Key] = copies[0];
                foreach (LabelSnapshot copy in copies.Skip(1))
                    issues.Add(OperationIssue.Warning(SheetCodes.LabelDuplicateTag,
                        "This label is a copy of another tool label (same rule, part, sheet and style), so it counts as placed by hand and is never moved.", obj: copy.Ref) with { Layout = copy.Tag.Layout });
            }

            List<PlannedChange> changes = new List<PlannedChange>();
            foreach (KeyValuePair<string, PlannedChange> d in desired)
                if (!existing.ContainsKey(d.Key)) changes.Add(d.Value);
            foreach (KeyValuePair<string, LabelSnapshot> e in existing)
            {
                if (desired.ContainsKey(e.Key)) continue;
                LabelSnapshot label = e.Value;
                if (skippedRules.Contains(label.Tag.RuleId?.Trim() ?? "")) continue;   // its rule was skipped (missing style): leave it
                if (label.IsPinned)
                {
                    issues.Add(OperationIssue.Warning(SheetCodes.LabelOrphanPinned,
                        "A pinned label is no longer asked for by its rule (style removed, or part off the sheet). It is kept; release or delete it if it is not needed.", obj: label.Ref) with { Layout = label.Tag.Layout });
                    continue;
                }
                changes.Add(new PlannedChange("", BuildStep.PlanLabels, ChangeKind.Remove, ChangeTarget.Label, label.Tag.Layout, label.Tag.RuleId,
                    label.Tag.SourceKey, label.Ref, label.IsOrphan ? "Remove label of a part that no longer exists" : $"Remove label no rule asks for ({label.Tag.StyleSlot})")
                    { StyleSlot = label.Tag.StyleSlot });
            }

            // One item per sheet, changes in a stable order (adds by rule order, then removes).
            List<BuildItem> items = new List<BuildItem>();
            foreach (SheetSnapshot sheet in sheets)
            {
                List<PlannedChange> mine = changes.Where(c => c.Layout.Equals(sheet.Layout, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Kind).ThenBy(c => c.RuleId, StringComparer.Ordinal).ThenBy(c => c.SourceKey, StringComparer.Ordinal)
                    .ThenBy(c => c.StyleSlot, StringComparer.Ordinal).ToList();
                if (mine.Count == 0) continue;
                List<PlannedChange> numbered = mine.Select((c, i) => c with { ChangeId = $"{sheet.Layout}#{i + 1}" }).ToList();
                items.Add(new BuildItem("plan:" + sheet.Layout, BuildItemKind.PlanSheet, new[] { sheet.Layout }, numbered));
            }
            return new PlanLabelsPlan(items, issues);
        }

        /// <summary>The targets a rule selects, each with the sheet that labels it (section 4, "Which sheet labels a part").</summary>
        private static IEnumerable<(string source, DrawingObjectRef part, double? ratio, string layout, string what)> Targets(SheetScene scene, List<SheetSnapshot> sheets, LabelRule rule)
        {
            TargetSelector sel = rule.Select;
            foreach (NetworkSnapshot net in scene.Networks)
            {
                if (sel.Kind == TargetKind.Structure)
                {
                    foreach (SceneStructure s in net.Structures.Where(s => sel.Matches(net.Name, s.TypeName)))
                        if (SheetOfPoint(sheets, new Point2(s.Insertion.X, s.Insertion.Y)) is string layout)
                            yield return (s.Ref.Handle, s.Ref, null, layout, $"structure {s.Name}");
                }
                else if (sel.Kind == TargetKind.Pipe)
                {
                    double ratio = rule.At == LabelAnchor.Start ? 0 : rule.At == LabelAnchor.End ? 1 : 0.5;
                    foreach (ScenePipe p in net.Pipes.Where(p => sel.Matches(net.Name, p.TypeCode)))
                        if (SheetOfSegment(sheets, new Point2(p.Start.X, p.Start.Y), new Point2(p.End.X, p.End.Y)) is string layout)
                            yield return (p.Ref.Handle, p.Ref, ratio, layout, $"pipe {p.Name}");
                }
                else if (sel.Kind == TargetKind.Joint)
                {
                    // One joint label per pipe, at its start: the label names the pipe that begins there (sheet 102054).
                    foreach (ScenePipe p in net.Pipes.Where(p => sel.Matches(net.Name, p.TypeCode)))
                        if (SheetOfPoint(sheets, new Point2(p.Start.X, p.Start.Y)) is string layout)
                            yield return (JointKey(p.Ref), p.Ref, 0.0, layout, $"joint at the start of {p.Name}");
                }
            }
        }

        /// <summary>The first plan sheet (tab order, then viewport handle) whose viewport contains the point; null when none.</summary>
        public static string SheetOfPoint(IEnumerable<SheetSnapshot> sheets, Point2 p)
        {
            foreach (SheetSnapshot sheet in sheets)
                foreach (ViewportSnapshot v in sheet.Viewports.OrderBy(v => HandleValue(v.Ref)))
                    if (Geometry2.Contains(v.ModelBoundary, p)) return sheet.Layout;
            return null;
        }

        /// <summary>The plan sheet whose viewports show the longest part of the segment (ties: lower tab order); null when none.</summary>
        public static string SheetOfSegment(IEnumerable<SheetSnapshot> sheets, Point2 a, Point2 b)
        {
            string best = null;
            double bestLength = 1e-9;
            foreach (SheetSnapshot sheet in sheets)
            {
                double shown = sheet.Viewports.Max(v => (double?)Geometry2.LengthInside(v.ModelBoundary, a, b)) ?? 0;
                if (shown > bestLength + 1e-9) { best = sheet.Layout; bestLength = shown; }
            }
            return best;
        }

        private static ulong HandleValue(DrawingObjectRef r) =>
            ulong.TryParse(r.Handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v) ? v : ulong.MaxValue;
    }

    /// <summary>Plane geometry on contract points: containment and how much of a segment lies inside a polygon.</summary>
    internal static class Geometry2
    {
        /// <summary>True when the point lies inside the closed polygon or on its edge.</summary>
        public static bool Contains(IReadOnlyList<Point2> polygon, Point2 p)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2 a = polygon[i], b = polygon[j];
                if (OnSegment(a, b, p)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>The length of segment a–b that lies inside the polygon.</summary>
        public static double LengthInside(IReadOnlyList<Point2> polygon, Point2 a, Point2 b)
        {
            if (polygon == null || polygon.Count < 3) return 0;
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-12) return 0;
            List<double> cuts = new List<double> { 0, 1 };
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2 c = polygon[j], d = polygon[i];
                double ex = d.X - c.X, ey = d.Y - c.Y, den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-15) continue;
                double t = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / den, u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / den;
                if (t > 0 && t < 1 && u >= 0 && u <= 1) cuts.Add(t);
            }
            cuts.Sort();
            double inside = 0;
            for (int k = 1; k < cuts.Count; k++)
            {
                double m = (cuts[k - 1] + cuts[k]) / 2;
                if (cuts[k] - cuts[k - 1] > 1e-12 && Contains(polygon, new Point2(a.X + dx * m, a.Y + dy * m))) inside += (cuts[k] - cuts[k - 1]) * length;
            }
            return inside;
        }

        private static bool OnSegment(Point2 a, Point2 b, Point2 p)
        {
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (Math.Abs(cross) > 1e-9 * Math.Max(1, a.DistanceTo(b))) return false;
            return p.X >= Math.Min(a.X, b.X) - 1e-9 && p.X <= Math.Max(a.X, b.X) + 1e-9 && p.Y >= Math.Min(a.Y, b.Y) - 1e-9 && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;
        }
    }
}
