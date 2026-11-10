using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    public class ProfileLabelPlannerTests
    {
        private static DrawingObjectRef R(string h) => DrawingObjectRef.Of(h);
        private static ViewPart Part(string h, ViewPartKind kind, bool own, double a, double b) => new(R(h), kind, own, a, b, 10, 9, 10, 9);
        private static readonly ProfileViewLayout[] Map = { new(R("A1"), "P1"), new(R("A2"), "P2") };
        private static SheetScene Scene() => new()
        {
            Sheets = new[] { new SheetSnapshot("P1", 1, SheetKind.Profile, "T", "A", "1", null), new SheetSnapshot("P2", 2, SheetKind.Profile, "T", "B", "2", null) },
            Networks = new[] {
                new NetworkSnapshot(R("10"), "ICT", new[] { new ScenePipe(R("21"), "Pipe", "ICT", "IC-3A", new Point3(0,0,0), new Point3(180,0,0), 1, 1) }, new[] { new SceneStructure(R("31"), "MH", "ICT", "MH", new Point3(95,0,0)) }),
                new NetworkSnapshot(R("11"), "LV", new[] { new ScenePipe(R("22"), "Cross", "LV", "Cable", new Point3(0,0,0), new Point3(1,0,0), 1, 1) }, new[] { new SceneStructure(R("32"), "Other", "LV", "MH", new Point3(40,0,0)) }) },
            ProfileViews = new[] { View("A1", 0, 100), View("A2", 90, 180) },
            Crossings = new[] { new CrossingSnapshot(R("A1"), R("22"), "LV", "Cross", 40, 10, 9, true) },
            LabelStyles = new[] { new SceneLabelStyle(LabelStyleKind.Pipe, "Name", R("51"), "f"), new SceneLabelStyle(LabelStyleKind.Pipe, "Joint", R("52"), "f"), new SceneLabelStyle(LabelStyleKind.Structure, "MH", R("53"), "f") }
        };
        private static ProfileViewSnapshot View(string h, double a, double b) => new(R(h), h, R("40"), a, b, 0, 20, "", new[] { Part("21", ViewPartKind.Pipe, true, 0, 180), Part("31", ViewPartKind.Structure, true, 94, 96), Part("32", ViewPartKind.Structure, false, 40, 40) }, 1);
        private static LabelRule Rule(TargetKind kind = TargetKind.Pipe, bool other = false, params string[] styles) => new("rule", ViewKind.Profile, new TargetSelector(kind, other ? "LV" : "ICT", null, other), styles.Length == 0 ? new[] { kind == TargetKind.Structure ? "MH" : "Name" } : styles, kind == TargetKind.Structure ? LabelAnchor.Insertion : kind == TargetKind.Joint ? LabelAnchor.EachJoint : LabelAnchor.Middle);
        private static LabelRuleSet Rules(params LabelRule[] rules) => new("test", 1, rules);
        private static ProfileLabelsPlan Plan(SheetScene scene = null, LabelRule rule = null, IReadOnlyCollection<string> layouts = null) => ProfileLabelPlanner.Plan(scene ?? Scene(), Rules(rule ?? Rule()), Map, layouts);
        private static List<PlannedChange> All(ProfileLabelsPlan p) => p.Views.SelectMany(v => v.Changes).ToList();
        private static readonly RecordedPlacement Placement = new("Name", false, 0, 0, null, 0);
        private static LabelSnapshot Tool(string h = "B1", string style = "Name", PinState pin = PinState.Auto, string source = null) => new(R(h), R("21"), "P1", new BuildTag("rule", source ?? ProfileLabelPlanner.SourceKey(R("21"), R("A1")), "P1", style, pin, Placement), Placement);

        [Fact] public void SegmentsArePlannedPerViewInOwnedVisiblePortion()
        {
            var p = Plan(); Assert.Equal(2, p.Views.Count);
            Assert.Equal(.5, All(p)[0].Ratio); Assert.Equal(140d / 180, All(p)[1].Ratio);
            Assert.All(All(p), c => { Assert.Equal(BuildStep.ProfileLabels, c.Step); Assert.Equal(R("21"), c.Part); });
        }
        [Fact] public void SameLayoutViewsHaveDistinctIdentities()
        {
            var p = ProfileLabelPlanner.Plan(Scene(), Rules(Rule()), new[] { Map[0], new ProfileViewLayout(R("A2"), "P1") });
            Assert.Equal(2, All(p).Select(c => c.SourceKey).Distinct().Count()); Assert.Equal(2, All(p).Select(c => c.ChangeId).Distinct().Count());
        }
        [Fact] public void StructuresInOverlapBelongToEarlierViewEvenWhenOnlyLaterSelected()
        { Assert.Single(All(Plan(rule: Rule(TargetKind.Structure)))); Assert.Empty(All(Plan(rule: Rule(TargetKind.Structure), layouts: new[] { "P2" }))); }
        [Fact] public void JointIsAtPhysicalStartOnly()
        { var c = Assert.Single(All(Plan(rule: Rule(TargetKind.Joint)))); Assert.Equal(0d, c.Ratio); Assert.Contains(":start:view:", c.SourceKey); }
        [Fact] public void JointAtSharedBoundaryBelongsToEarlierView()
        {
            var s = Scene(); s = s with { ProfileViews = s.ProfileViews.Select(v => v with { DrawnParts = new[] { Part("21", ViewPartKind.Pipe, true, 100, 150) } }).ToArray() };
            var c = Assert.Single(All(Plan(s, Rule(TargetKind.Joint)))); Assert.Equal("P1", c.Layout);
        }
        [Fact] public void ReversedPipeStartRemainsPhysicalStart()
        {
            var s = Scene(); s = s with { ProfileViews = s.ProfileViews.Select(v => v with { DrawnParts = new[] { Part("21", ViewPartKind.Pipe, true, 180, 0) } }).ToArray() };
            var c = Assert.Single(All(Plan(s, Rule(TargetKind.Joint)))); Assert.Equal("P2", c.Layout); Assert.Equal(0d, c.Ratio);
        }
        [Fact] public void CrossingUsesShownSnapshotAndOtherNetworkSelector()
        { var c = Assert.Single(All(Plan(rule: Rule(other: true)))); Assert.Equal(R("22"), c.Part); Assert.Equal("P1", c.Layout); }
        [Fact] public void HiddenCrossingsAndCrossingJointsAreNotPlanned()
        {
            var s = Scene(); Assert.Empty(All(Plan(s with { Crossings = s.Crossings.Select(c => c with { ShownInView = false }).ToArray() }, Rule(other: true))));
            Assert.Empty(All(Plan(rule: Rule(TargetKind.Joint, true))));
        }
        [Fact] public void OtherStructuresUseDrawnPartsAndExcludeOwnStructures()
        { var c = Assert.Single(All(Plan(rule: Rule(TargetKind.Structure, true)))); Assert.Equal(R("32"), c.Part); }
        [Fact] public void NetworkAndTypePatternsFilterTargets()
        { Assert.Empty(All(Plan(rule: Rule() with { Select = new TargetSelector(TargetKind.Pipe, "ICT", "NO*") }))); }
        [Fact] public void MultipleStylesFollowUserOrder()
        { var p = Plan(rule: Rule(styles: new[] { "Joint", "Name" })); Assert.Equal(new[] { "Joint", "Name", "Joint", "Name" }, All(p).Select(c => c.StyleSlot)); }
        [Fact] public void ExistingIdentityIsKeptRegardlessOfStyleOrder()
        { var s = Scene() with { Labels = new[] { Tool(), Tool("B2", "Joint") } }; Assert.Empty(All(Plan(s, Rule(styles: new[] { "Joint", "Name" }), new[] { "P1" }))); }
        [Fact] public void RemovedStyleRemovesOnlyItsUnpinnedLabel()
        { var s = Scene() with { Labels = new[] { Tool(), Tool("B2", "Joint") } }; var c = Assert.Single(All(Plan(s, layouts: new[] { "P1" }))); Assert.Equal(ChangeKind.Remove, c.Kind); Assert.Equal("Joint", c.StyleSlot); }
        [Fact] public void PinnedObsoleteLabelIsKeptAndWarned()
        { var p = Plan(Scene() with { Labels = new[] { Tool(style: "Joint", pin: PinState.Pinned) } }); Assert.DoesNotContain(All(p), c => c.Kind == ChangeKind.Remove); Assert.Contains(p.Issues, i => i.Code == SheetCodes.LabelOrphanPinned); }
        [Fact] public void MovedObsoleteLabelIsImplicitlyPinned()
        { var label = Tool(style: "Joint") with { Current = Placement with { TextXM = 5 } }; Assert.Contains(Plan(Scene() with { Labels = new[] { label } }).Issues, i => i.Code == SheetCodes.LabelOrphanPinned); }
        [Fact] public void ManualAndPlanLabelsAreUntouched()
        { var s = Scene() with { Labels = new[] { Tool(source: "21"), Tool("B2") with { Tag = null } } }; Assert.DoesNotContain(All(Plan(s)), c => c.Kind == ChangeKind.Remove); }
        [Fact] public void DuplicateTagKeepsLowestNumericHandleAndNeverRemovesCopy()
        {
            var p = Plan(Scene() with { Labels = new[] { Tool("100", "Joint"), Tool("FF", "Joint") } });
            Assert.Equal(R("FF"), Assert.Single(All(p), c => c.Kind == ChangeKind.Remove).Existing); Assert.Contains(p.Issues, i => i.Code == SheetCodes.LabelDuplicateTag && i.Object == R("100"));
        }
        [Theory] [InlineData(false)] [InlineData(true)] public void UnavailableStyleSkipsWholeRuleAndPreservesExisting(bool incomplete)
        {
            var s = Scene(); s = s with { Labels = new[] { Tool(style: "Joint") }, LabelStyles = incomplete ? s.LabelStyles.Select(x => x with { Fingerprint = "" }).ToArray() : Array.Empty<SceneLabelStyle>() };
            var p = Plan(s); Assert.Empty(All(p)); Assert.Contains(p.Issues, i => i.Code == (incomplete ? SheetCodes.FingerprintIncomplete : SheetCodes.RuleStyleMissing));
        }
        [Fact] public void WrongKindStyleIsUnavailable()
        { Assert.Contains(Plan(rule: Rule(styles: new[] { "MH" })).Issues, i => i.Code == SheetCodes.RuleStyleMissing); }
        [Theory] [InlineData(false)] [InlineData(true)] public void IncompleteScenePlansNothing(bool error)
        {
            var issue = error ? OperationIssue.Error("test", "bad") : OperationIssue.Warning(SheetCodes.SceneReadFailed, "bad");
            var p = Plan(Scene() with { Issues = new[] { issue } }); Assert.Empty(All(p)); Assert.Contains(p.Issues, i => i.Code == SheetCodes.SceneIncomplete);
        }
        [Fact] public void NonblockingWarningDoesNotSuppressLabels()
        { Assert.Equal(2, All(Plan(Scene() with { Issues = new[] { OperationIssue.Warning("sheets.scene.roleUnmatched", "warning") } })).Count); }
        [Fact] public void UnknownRequestedLayoutReportsIssue()
        { var p = Plan(layouts: new[] { "missing" }); Assert.Empty(All(p)); Assert.Contains(p.Issues, i => i.Code == SheetCodes.RequestLayout && i.Layout == "missing"); }
        [Fact] public void InvalidOrDuplicateMappingRejectsPlanning()
        {
            foreach (var map in new[] { new[] { Map[0], Map[0] }, new[] { new ProfileViewLayout(R("FF"), "P1") }, new[] { new ProfileViewLayout(R("A1"), "missing") } })
            { var p = ProfileLabelPlanner.Plan(Scene(), Rules(Rule()), map); Assert.Empty(All(p)); Assert.Contains(p.Issues, i => i.Code == SheetCodes.SceneIncomplete); }
        }
        [Fact] public void MissingPartMetadataRejectsPlanning()
        { var p = Plan(Scene() with { Networks = Array.Empty<NetworkSnapshot>() }); Assert.Empty(All(p)); Assert.Contains(p.Issues, i => i.Code == SheetCodes.SceneIncomplete); }
        [Fact] public void PlanRulesAreNotExecuted()
        { Assert.Empty(All(Plan(rule: Rule() with { View = ViewKind.Plan }))); }
        [Fact] public void OrphanToolLabelsAreRemovedUnlessPinned()
        {
            var orphan = Tool(style: "Joint") with { Target = default };
            Assert.Contains(All(Plan(Scene() with { Labels = new[] { orphan } })), c => c.Kind == ChangeKind.Remove);
            var pinned = orphan with { Tag = orphan.Tag with { Pin = PinState.Pinned } };
            Assert.DoesNotContain(All(Plan(Scene() with { Labels = new[] { pinned } })), c => c.Kind == ChangeKind.Remove);
        }
        [Fact] public void BadStyleDoesNotSuppressIndependentValidRule()
        {
            var p = ProfileLabelPlanner.Plan(Scene(), Rules(Rule(styles: new[] { "missing" }), Rule(TargetKind.Structure) with { Id = "structures" }), Map);
            Assert.Single(All(p)); Assert.Contains(p.Issues, i => i.Code == SheetCodes.RuleStyleMissing);
        }
        [Fact] public void OutputAndIdentifiersAreDeterministic()
        { var a = All(Plan()); var b = All(Plan()); Assert.Equal(a, b); Assert.Equal(a.Count, a.Select(c => c.ChangeId).Distinct().Count()); }
    }
}
