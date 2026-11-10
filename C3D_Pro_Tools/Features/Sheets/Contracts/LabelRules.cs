using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// Label rules (docs/sheet-production-contract.md, section 4): which targets get which label style, where, and how often. Saved
// in the project settings file (C3DPro.project.json, "labelRules"). Whether a style exists in the drawing is the adapter's check
// (sheets.rule.styleMissing); everything a rule set says about itself is checked here.
namespace C3DProTools.Features.Sheets.Contracts
{
    internal enum ViewKind { Plan = 0, Profile = 1 }

    internal enum TargetKind
    {
        Structure = 0,
        Pipe = 1,
        /// <summary>A pipe end, where two pipes meet or a pipe meets a structure.</summary>
        Joint = 2,
        Alignment = 3,
        /// <summary>An alignment's station labels, set from the label set the rule names.</summary>
        AlignmentStations = 4
    }

    internal enum LabelAnchor { Insertion = 0, Start = 1, Middle = 2, End = 3, EachJoint = 4 }

    internal enum LabelFrequency { PerPart = 0, PerRun = 1, OncePerViewport = 2 }

    internal enum SidePreference { Alternate = 0, Left = 1, Right = 2, AwayFromRoute = 3 }

    /// <summary>
    /// Which targets a rule applies to. Patterns use <c>*</c> (any run of characters) and <c>?</c> (one character), compared
    /// ignoring case; null or empty matches everything.
    /// </summary>
    /// <param name="OtherNetworksOnly">Profile rules only: the part must belong to another network than the view's own (crossings).</param>
    internal sealed record TargetSelector(TargetKind Kind, string NetworkPattern = null, string TypeCodePattern = null, bool OtherNetworksOnly = false)
    {
        public bool Matches(string network, string typeCode) => Like(network, NetworkPattern) && Like(typeCode, TypeCodePattern);

        /// <summary>True when <paramref name="value"/> matches the wildcard pattern (null or empty pattern: always).</summary>
        public static bool Like(string value, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            string rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(value ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <param name="Id">Stable, unique in its set (ignoring case), e.g. "ict.plan.joint". Build tags carry it.</param>
    /// <param name="LabelStyle">The label style (or, for <see cref="TargetKind.AlignmentStations"/>, the label set) by name.</param>
    /// <param name="Priority">Placement order: higher first (crowded rules first).</param>
    internal sealed record LabelRule(string Id, ViewKind View, TargetSelector Select, string LabelStyle, LabelAnchor At,
        LabelFrequency Every = LabelFrequency.PerPart, bool Leader = true, SidePreference Side = SidePreference.Alternate, int Priority = 0);

    internal sealed record LabelRuleSet(string Name, int Version, IReadOnlyList<LabelRule> Rules)
    {
        private readonly IReadOnlyList<LabelRule> _rules = Frozen.List(Rules);
        public IReadOnlyList<LabelRule> Rules { get => _rules; init => _rules = Frozen.List(value); }
    }

    internal static class LabelRuleValidation
    {
        /// <summary>The anchors each target kind accepts.</summary>
        public static IReadOnlyCollection<LabelAnchor> AnchorsFor(TargetKind kind) => kind switch
        {
            TargetKind.Structure => new[] { LabelAnchor.Insertion },
            TargetKind.Pipe => new[] { LabelAnchor.Start, LabelAnchor.Middle, LabelAnchor.End },
            TargetKind.Joint => new[] { LabelAnchor.EachJoint },
            TargetKind.Alignment => new[] { LabelAnchor.Insertion },
            TargetKind.AlignmentStations => new[] { LabelAnchor.Insertion },
            _ => Array.Empty<LabelAnchor>()
        };

        /// <summary>The frequencies each target kind accepts: a run is a run of pipes; once per viewport suits alignments.</summary>
        public static IReadOnlyCollection<LabelFrequency> FrequenciesFor(TargetKind kind) => kind switch
        {
            TargetKind.Pipe => new[] { LabelFrequency.PerPart, LabelFrequency.PerRun },
            TargetKind.Alignment => new[] { LabelFrequency.PerPart, LabelFrequency.OncePerViewport },
            TargetKind.AlignmentStations => new[] { LabelFrequency.PerPart },
            TargetKind.Structure or TargetKind.Joint => new[] { LabelFrequency.PerPart },
            _ => Array.Empty<LabelFrequency>()
        };

        /// <summary>
        /// Every problem the rule set has on its own (errors only). A set with errors is not used: the build is rejected, since
        /// a half-applied rule set would label some sheets one way and others another.
        /// </summary>
        public static IReadOnlyList<OperationIssue> Validate(LabelRuleSet set)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (set == null) { issues.Add(OperationIssue.Error(SheetCodes.RulesMissing, "No label rule set is chosen.", "Rules")); return issues; }
            if (string.IsNullOrWhiteSpace(set.Name)) issues.Add(OperationIssue.Error(SheetCodes.RulesName, "The label rule set has no name.", "Rules"));

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < set.Rules.Count; i++)
            {
                LabelRule r = set.Rules[i];
                string where = $"Rules[{i}]";
                if (r == null) { issues.Add(OperationIssue.Error(SheetCodes.RuleId, $"Rule {i + 1} is empty.", where)); continue; }
                string name = string.IsNullOrWhiteSpace(r.Id) ? $"Rule {i + 1}" : $"Rule \"{r.Id}\"";
                if (string.IsNullOrWhiteSpace(r.Id)) issues.Add(OperationIssue.Error(SheetCodes.RuleId, $"{name} has no id.", where));
                else if (!ids.Add(r.Id.Trim())) issues.Add(OperationIssue.Error(SheetCodes.RuleIdDuplicate, $"{name} uses an id another rule already has.", where));

                if (!Enum.IsDefined(r.View) || r.Select == null || !Enum.IsDefined(r.Select.Kind) || !Enum.IsDefined(r.At)
                    || !Enum.IsDefined(r.Every) || !Enum.IsDefined(r.Side))
                {
                    issues.Add(OperationIssue.Error(SheetCodes.RuleEnum, $"{name} has a view, target, anchor, frequency or side that is not known.", where));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(r.LabelStyle)) issues.Add(OperationIssue.Error(SheetCodes.RuleStyleEmpty, $"{name} names no label style.", where));
                if (!AnchorsFor(r.Select.Kind).Contains(r.At))
                    issues.Add(OperationIssue.Error(SheetCodes.RuleAnchor, $"{name}: a {r.Select.Kind} label cannot be anchored at {r.At}.", where));
                if (!FrequenciesFor(r.Select.Kind).Contains(r.Every))
                    issues.Add(OperationIssue.Error(SheetCodes.RuleFrequency, $"{name}: a {r.Select.Kind} label cannot be placed {r.Every}.", where));
                if (r.Select.OtherNetworksOnly && r.View != ViewKind.Profile)
                    issues.Add(OperationIssue.Error(SheetCodes.RuleEnum, $"{name}: \"other networks only\" applies to profile rules only.", where));
            }
            return issues;
        }
    }
}
