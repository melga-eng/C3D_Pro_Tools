using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    /// <summary>
    /// The PlanLabels step (docs/sheet-production-contract.md, section 4): which sheet labels a part, one label per selected
    /// style, reconciliation with existing tool labels, pinned and hand-placed labels left alone, missing styles.
    /// </summary>
    public class PlanLabelPlannerTests
    {
        // Two plan sheets side by side: A covers x 0–100, B covers x 100–200 (y 0–100), tab order A then B.
        private static ViewportSnapshot Vp(string handle, double x0, double x1) => new ViewportSnapshot(DrawingObjectRef.Of(handle), 2.0, 0,
            new[] { new Point2(x0, 0), new Point2(x1, 0), new Point2(x1, 100), new Point2(x0, 100) }, new Rect2(0, 0, 200, 200), true);

        private static readonly SheetSnapshot SheetA = new SheetSnapshot("SW-A", 1, SheetKind.Plan, "124", "A", "SW-A", new[] { Vp("A1", 0, 100) });
        private static readonly SheetSnapshot SheetB = new SheetSnapshot("SW-B", 2, SheetKind.Plan, "124", "B", "SW-B", new[] { Vp("B1", 100, 200) });
        private static readonly SheetSnapshot Profile = new SheetSnapshot("SW-P", 3, SheetKind.Profile, "", "", "SW-P", new[] { Vp("C1", 0, 200) });

        private static ScenePipe Pipe(string h, string name, double x0, double x1, string type = "IC-3A") =>
            new ScenePipe(DrawingObjectRef.Of(h), name, "ICT", type, new Point3(x0, 50, 610), new Point3(x1, 50, 610), 0.79, 1.11);

        private static SceneStructure Mh(string h, string name, double x) => new SceneStructure(DrawingObjectRef.Of(h), name, "ICT", "Type C MH", new Point3(x, 50, 613));

        private static SheetScene Scene(IReadOnlyList<LabelSnapshot> labels = null, IReadOnlyList<ScenePipe> pipes = null) => new SheetScene
        {
            Sheets = new[] { SheetA, SheetB, Profile },
            Networks = new[]
            {
                new NetworkSnapshot(DrawingObjectRef.Of("10"), "ICT",
                    pipes ?? new[] { Pipe("21", "IC-1", 10, 60), Pipe("22", "IC-2", 60, 170), Pipe("23", "IC-3", 170, 190, "IC-2B") },
                    new[] { Mh("31", "MH-1", 10), Mh("32", "MH-2", 150) }),
                new NetworkSnapshot(DrawingObjectRef.Of("11"), "LV", new[] { new ScenePipe(DrawingObjectRef.Of("41"), "LV-1", "LV", "", new Point3(20, 50, 611), new Point3(30, 50, 611), 0.3, 0.3) }, null),
            },
            LabelStyles = new[]
            {
                new SceneLabelStyle(LabelStyleKind.Pipe, "Joint", DrawingObjectRef.Of("51"), "f1"),
                new SceneLabelStyle(LabelStyleKind.Pipe, "Name", DrawingObjectRef.Of("52"), "f2"),
                new SceneLabelStyle(LabelStyleKind.Structure, "MH Data", DrawingObjectRef.Of("53"), "f3"),
                new SceneLabelStyle(LabelStyleKind.Structure, "MH Name", DrawingObjectRef.Of("54"), "f4"),
            },
            Labels = labels ?? Array.Empty<LabelSnapshot>(),
        };

        private static LabelRuleSet Rules(params LabelRule[] rules) => new LabelRuleSet("KSIA ICT", 1, rules);
        private static LabelRule Manholes(params string[] styles) => new LabelRule("ict.plan.manhole", ViewKind.Plan, new TargetSelector(TargetKind.Structure, "ICT"), styles, LabelAnchor.Insertion);
        private static LabelRule Joints(params string[] styles) => new LabelRule("ict.plan.joint", ViewKind.Plan, new TargetSelector(TargetKind.Joint, "ICT", "IC-3?"), styles, LabelAnchor.EachJoint);
        private static LabelRule Pipes(params string[] styles) => new LabelRule("ict.plan.pipe", ViewKind.Plan, new TargetSelector(TargetKind.Pipe, "ICT"), styles, LabelAnchor.Middle);

        private static readonly RecordedPlacement Placed = new RecordedPlacement("MH Name", false, 10, 50, null, 0);

        private static LabelSnapshot Tool(string handle, string rule, string source, string layout, string slot, string target, PinState pin = PinState.Auto, RecordedPlacement now = null) =>
            new LabelSnapshot(DrawingObjectRef.Of(handle), target == null ? default : DrawingObjectRef.Of(target), layout,
                new BuildTag(rule, source, layout, slot, pin, Placed), now ?? Placed);

        private static List<PlannedChange> All(PlanLabelsPlan p) => p.Items.SelectMany(i => i.Changes).ToList();

        // ---- Which sheet labels a part -----------------------------------------------------------------------------------

        [Fact]
        public void Structures_AreLabelledOnTheSheetHoldingTheirInsertion()
        {
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Manholes("MH Name")), null);
            Assert.Equal(new[] { ("SW-A", "31"), ("SW-B", "32") }, All(plan).Select(c => (c.Layout, c.SourceKey)).OrderBy(x => x.Item2));
            Assert.All(All(plan), c => Assert.Equal(ChangeKind.Add, c.Kind));
        }

        [Fact]
        public void Pipes_AreLabelledWhereMostOfThemShow_AtTheirAnchor()
        {
            // IC-2 runs 60–170: 40 m on A, 70 m on B, so B labels it. LV pipes do not match the ICT selector.
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Pipes("Name")), null);
            Dictionary<string, PlannedChange> bySource = All(plan).ToDictionary(c => c.SourceKey);
            Assert.Equal(new[] { "21", "22", "23" }, bySource.Keys.OrderBy(k => k));
            Assert.Equal("SW-A", bySource["21"].Layout);
            Assert.Equal("SW-B", bySource["22"].Layout);
            Assert.All(bySource.Values, c => Assert.Equal(0.5, c.Ratio));
        }

        [Fact]
        public void Joints_AreOnePerPipeAtItsStart_AndFollowTheTypeCodePattern()
        {
            // IC-3 is type IC-2B: not selected by "IC-3?". IC-2 starts at x 60, on sheet A.
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Joints("Joint")), null);
            Assert.Equal(new[] { ("21:start", "SW-A"), ("22:start", "SW-A") }, All(plan).Select(c => (c.SourceKey, c.Layout)).OrderBy(x => x.Item1));
            Assert.All(All(plan), c => { Assert.Equal(0.0, c.Ratio); Assert.Equal("Joint", c.StyleSlot); });
        }

        [Fact]
        public void PartsOffEverySheet_GetNoLabel()
        {
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(pipes: new[] { Pipe("21", "IC-1", 300, 400) }), Rules(Pipes("Name"), Joints("Joint")), null);
            Assert.Empty(All(plan));
        }

        [Fact]
        public void OnlyTheRequestedPlanSheets_AreBuilt()
        {
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Manholes("MH Name")), new[] { "SW-B", "SW-P" });
            Assert.Equal(new[] { "plan:SW-B" }, plan.Items.Select(i => i.ItemKey));
            Assert.Contains(plan.Issues, i => i.Code == SheetCodes.RequestLayout && i.Layout == "SW-P");
        }

        // ---- One label per selected style --------------------------------------------------------------------------------

        [Fact]
        public void TwoStyles_GiveTwoLabelsPerTarget_WithTheirSlots()
        {
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Manholes("MH Name", "MH Data")), null);
            Assert.Equal(4, All(plan).Count);
            Assert.Equal(new[] { "MH Data", "MH Name" }, All(plan).Where(c => c.SourceKey == "31").Select(c => c.StyleSlot).OrderBy(s => s));
            Assert.All(All(plan), c => Assert.Equal(c.Part?.Handle, c.SourceKey));
        }

        // ---- Reconciliation ------------------------------------------------------------------------------------------

        [Fact]
        public void ExistingToolLabels_AreKept_SoRunningAgainChangesNothing()
        {
            LabelSnapshot[] labels =
            {
                Tool("A01", "ict.plan.manhole", "31", "SW-A", "MH Name", "31"),
                Tool("A02", "ict.plan.manhole", "32", "SW-B", "MH Name", "32"),
            };
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(labels), Rules(Manholes("MH Name")), null);
            Assert.Empty(plan.Items);
        }

        [Fact]
        public void ARemovedStyle_RemovesItsLabels_ButKeepsPinnedOnes()
        {
            LabelSnapshot[] labels =
            {
                Tool("A01", "ict.plan.manhole", "31", "SW-A", "MH Name", "31"),
                Tool("A02", "ict.plan.manhole", "31", "SW-A", "MH Data", "31"),
                Tool("A03", "ict.plan.manhole", "32", "SW-B", "MH Data", "32", PinState.Pinned),
                Tool("A04", "ict.plan.manhole", "32", "SW-B", "MH Name", "32"),
            };
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(labels), Rules(Manholes("MH Name")), null);
            PlannedChange remove = Assert.Single(All(plan));
            Assert.Equal(ChangeKind.Remove, remove.Kind);
            Assert.Equal("A02", remove.Existing?.Handle);
            OperationIssue kept = Assert.Single(plan.Issues, i => i.Code == SheetCodes.LabelOrphanPinned);
            Assert.Equal("SW-B", kept.Layout);
            Assert.Equal("A03", kept.Object?.Handle);
        }

        [Fact]
        public void LabelsMovedByTheUser_CountAsPinned_AndAreNeverRemoved()
        {
            LabelSnapshot moved = Tool("A05", "ict.plan.old", "31", "SW-A", "MH Name", "31", now: Placed with { TextXM = 25 });
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(new[] { moved }), Rules(Manholes("MH Name")), new[] { "SW-A" });
            Assert.DoesNotContain(All(plan), c => c.Kind == ChangeKind.Remove);
            Assert.Contains(plan.Issues, i => i.Code == SheetCodes.LabelOrphanPinned && i.Object?.Handle == "A05");
        }

        [Fact]
        public void OrphanToolLabels_AreRemoved()
        {
            LabelSnapshot orphan = Tool("A06", "ict.plan.manhole", "99", "SW-A", "MH Name", null);
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(new[] { orphan }), Rules(Manholes("MH Name")), new[] { "SW-A" });
            PlannedChange remove = Assert.Single(All(plan), c => c.Kind == ChangeKind.Remove);
            Assert.Equal("A06", remove.Existing?.Handle);
            Assert.Contains("no longer exists", remove.Description);
        }

        [Fact]
        public void HandPlacedLabels_AndOtherSheets_AreNeverTouched()
        {
            LabelSnapshot byHand = new LabelSnapshot(DrawingObjectRef.Of("A07"), DrawingObjectRef.Of("31"), "SW-A", null, Placed);
            LabelSnapshot otherSheet = Tool("A08", "ict.plan.gone", "31", "SW-B", "MH Name", "31");
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(new[] { byHand, otherSheet }), Rules(), new[] { "SW-A" });
            Assert.Empty(plan.Items);
        }

        [Fact]
        public void CopiedTags_KeepTheLowestHandle_AndWarnForTheRest()
        {
            LabelSnapshot[] labels =
            {
                Tool("B10", "ict.plan.manhole", "31", "SW-A", "MH Name", "31"),
                Tool("A10", "ict.plan.manhole", "31", "SW-A", "MH Name", "31"),
            };
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(labels), Rules(Manholes("MH Name")), new[] { "SW-A" });
            Assert.Empty(plan.Items);
            OperationIssue dup = Assert.Single(plan.Issues, i => i.Code == SheetCodes.LabelDuplicateTag);
            Assert.Equal("B10", dup.Object?.Handle);
        }

        // ---- Styles and unsupported rules -------------------------------------------------------------------------------

        [Fact]
        public void AMissingStyle_SkipsTheWholeRule_AndLeavesItsLabels()
        {
            LabelSnapshot existing = Tool("A11", "ict.plan.manhole", "31", "SW-A", "MH Old", "31");
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(new[] { existing }), Rules(Manholes("MH Name", "MH Missing")), null);
            Assert.Empty(plan.Items);
            OperationIssue e = Assert.Single(plan.Issues, i => i.Code == SheetCodes.RuleStyleMissing);
            Assert.Contains("MH Missing", e.Message);
        }

        [Fact]
        public void StyleNames_MustMatchTheirKind()
        {
            // "Name" is a pipe label style: a structure rule cannot use it.
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(Manholes("Name")), null);
            Assert.Empty(plan.Items);
            Assert.Contains(plan.Issues, i => i.Code == SheetCodes.RuleStyleMissing);
        }

        [Fact]
        public void ProfileAndAlignmentRules_AreNotPartOfThisStep()
        {
            LabelRule profile = new LabelRule("ict.profile.manhole", ViewKind.Profile, new TargetSelector(TargetKind.Structure), new[] { "MH Name" }, LabelAnchor.Insertion);
            LabelRule alignment = new LabelRule("ict.plan.alignment", ViewKind.Plan, new TargetSelector(TargetKind.Alignment), new[] { "Align" }, LabelAnchor.Insertion, LabelFrequency.OncePerViewport);
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(), Rules(profile, alignment), null);
            Assert.Empty(plan.Items);
            Assert.Contains(plan.Issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("ict.plan.alignment"));
        }

        [Fact]
        public void Changes_AreNumberedPerSheet_InAStableOrder()
        {
            PlanLabelsPlan a = PlanLabelPlanner.Plan(Scene(), Rules(Joints("Joint"), Manholes("MH Name")), null);
            PlanLabelsPlan b = PlanLabelPlanner.Plan(Scene(), Rules(Manholes("MH Name"), Joints("Joint")), null);
            Assert.Equal(a.Items.SelectMany(i => i.Changes).Select(c => c.ChangeId + c.SourceKey), b.Items.SelectMany(i => i.Changes).Select(c => c.ChangeId + c.SourceKey));
            Assert.Equal("SW-A#1", a.Items[0].Changes[0].ChangeId);
        }

        // ---- Codex review of feature/label-rules ---------------------------------------------------------------------------

        [Fact]
        public void Ownership_IsDecidedOverEveryPlanSheet_EvenWhenBuildingOne()
        {
            // IC-2 shows 40 m on A and 70 m on B: B owns it. Building A alone must neither give it to A nor keep A's copy.
            LabelSnapshot wrongSheet = Tool("A20", "ict.plan.pipe", "22", "SW-A", "Name", "22");
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(Scene(new[] { wrongSheet }), Rules(Pipes("Name")), new[] { "SW-A" });
            List<PlannedChange> changes = All(plan);
            Assert.DoesNotContain(changes, c => c.Kind == ChangeKind.Add && c.SourceKey == "22");
            Assert.Contains(changes, c => c.Kind == ChangeKind.Remove && c.Existing?.Handle == "A20");
            Assert.All(changes, c => Assert.Equal("SW-A", c.Layout));
        }

        [Fact]
        public void AnIncompleteScene_PlansNothing()
        {
            SheetScene scene = Scene(new[] { Tool("A21", "ict.plan.manhole", "31", "SW-A", "MH Name", "31") }) with
            {
                Issues = new[] { OperationIssue.Warning(SheetCodes.SceneReadFailed, "Pipe IC-9 could not be read.", obj: DrawingObjectRef.Of("29")) }
            };
            PlanLabelsPlan plan = PlanLabelPlanner.Plan(scene, Rules(Pipes("Name")), null);
            Assert.Empty(plan.Items);
            Assert.Contains(plan.Issues, i => i.Code == SheetCodes.SceneIncomplete && i.Severity == IssueSeverity.Error);
            Assert.Contains(plan.Issues, i => i.Code == SheetCodes.SceneReadFailed);

            SheetScene wrongUnits = Scene() with { Issues = new[] { OperationIssue.Error(SheetCodes.UnitsNotMetres, "Not metres.") } };
            Assert.Empty(PlanLabelPlanner.Plan(wrongUnits, Rules(Manholes("MH Name")), null).Items);

            SheetScene onlyWarnings = Scene() with { Issues = new[] { OperationIssue.Warning(SheetCodes.SceneRoleUnmatched, "No existing ground.") } };
            Assert.NotEmpty(PlanLabelPlanner.Plan(onlyWarnings, Rules(Manholes("MH Name")), null).Items);
        }

        private static string DigestOf(SheetScene scene, LabelRuleSet rules) => PlanLabelPlanner.Digest(PlanLabelPlanner.Plan(scene, rules, null), scene, rules);

        [Fact]
        public void Digest_IsTheSameForTheSamePlan_AndChangesWithAnyChange()
        {
            string a = DigestOf(Scene(), Rules(Manholes("MH Name")));
            Assert.Equal(a, DigestOf(Scene(), Rules(Manholes("MH Name"))));
            Assert.NotEqual(a, DigestOf(Scene(), Rules(Manholes("MH Data"))));
            Assert.NotEqual(a, DigestOf(Scene(new[] { Tool("A22", "ict.plan.gone", "31", "SW-A", "MH Name", "31") }), Rules(Manholes("MH Name"))));
            Assert.NotEqual(a, PlanLabelPlanner.Digest(new PlanLabelsPlan(null, null), Scene(), Rules(Manholes("MH Name"))));
        }

        [Fact]
        public void Digest_Changes_WhenAUsedStyleIsEditedUnderTheSameName()
        {
            SheetScene before = Scene();
            SheetScene edited = before with { LabelStyles = before.LabelStyles.Select(s => s.Name == "MH Name" ? s with { Fingerprint = "f4-edited" } : s).ToList() };
            SheetScene unusedEdited = before with { LabelStyles = before.LabelStyles.Select(s => s.Name == "Joint" ? s with { Fingerprint = "f1-edited" } : s).ToList() };
            LabelRuleSet rules = Rules(Manholes("MH Name"));
            Assert.Equal(PlanLabelPlanner.Plan(before, rules, null).Items.SelectMany(i => i.Changes).Count(),
                PlanLabelPlanner.Plan(edited, rules, null).Items.SelectMany(i => i.Changes).Count());   // same changes…
            Assert.NotEqual(DigestOf(before, rules), DigestOf(edited, rules));                         // …but a stale preview
            Assert.Equal(DigestOf(before, rules), DigestOf(unusedEdited, rules));                      // a style the plan does not use
        }

        [Fact]
        public void AStyleThatCannotBeFingerprinted_SkipsItsRule_SoNoEditCanSlipPastTwoIncompleteReads()
        {
            // Two reads, before and after an edit, both incomplete for "MH Name": nothing must be planned (so nothing applied).
            LabelSnapshot existing = Tool("A30", "ict.plan.manhole", "31", "SW-A", "MH Name", "31");
            SheetScene incomplete = Scene(new[] { existing }) with
            {
                LabelStyles = Scene().LabelStyles.Select(s => s.Name == "MH Name" ? s with { Fingerprint = "" } : s).ToList()
            };
            LabelRuleSet rules = Rules(Manholes("MH Name"), Joints("Joint"));
            PlanLabelsPlan before = PlanLabelPlanner.Plan(incomplete, rules, null);
            PlanLabelsPlan after = PlanLabelPlanner.Plan(incomplete, rules, null);
            Assert.DoesNotContain(before.Items.SelectMany(i => i.Changes), c => c.RuleId == "ict.plan.manhole");
            Assert.DoesNotContain(after.Items.SelectMany(i => i.Changes), c => c.RuleId == "ict.plan.manhole");
            Assert.Contains(before.Issues, i => i.Code == SheetCodes.FingerprintIncomplete && i.Severity == IssueSeverity.Error && i.Message.Contains("MH Name"));
            Assert.Contains(before.Items.SelectMany(i => i.Changes), c => c.RuleId == "ict.plan.joint");   // other rules still plan
        }

        [Fact]
        public void Digest_Changes_WhenALabelledPartMovesWithinItsSheet()
        {
            LabelRuleSet rules = Rules(Manholes("MH Name"));
            SheetScene moved = Scene() with
            {
                Networks = Scene().Networks.Select(n => n with { Structures = n.Structures.Select(s => s.Ref.Handle == "31" ? s with { Insertion = new Point3(12, 55, 613) } : s).ToList() }).ToList()
            };
            Assert.Equal(PlanLabelPlanner.Plan(Scene(), rules, null).Items.SelectMany(i => i.Changes).Select(c => (c.Layout, c.SourceKey)),
                PlanLabelPlanner.Plan(moved, rules, null).Items.SelectMany(i => i.Changes).Select(c => (c.Layout, c.SourceKey)));
            Assert.NotEqual(DigestOf(Scene(), rules), DigestOf(moved, rules));
        }

        [Fact]
        public void Digest_Changes_WithTheRulesOrAViewport_EvenWhenTheChangesDoNot()
        {
            LabelRuleSet rules = Rules(Manholes("MH Name"));
            LabelRuleSet reprioritised = Rules(Manholes("MH Name") with { Priority = 9 });
            Assert.NotEqual(DigestOf(Scene(), rules), DigestOf(Scene(), reprioritised));
            SheetScene widened = Scene() with { Sheets = new[] { SheetA with { Viewports = new[] { Vp("A1", 0, 99) } }, SheetB, Profile } };
            Assert.NotEqual(DigestOf(Scene(), rules), DigestOf(widened, rules));
        }

        // ---- Geometry ------------------------------------------------------------------------------------------------

        [Fact]
        public void LengthInside_ClipsToThePolygon()
        {
            Point2[] square = { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
            Assert.Equal(10, Geometry2.LengthInside(square, new Point2(-5, 5), new Point2(15, 5)), 9);
            Assert.Equal(5, Geometry2.LengthInside(square, new Point2(5, 5), new Point2(5, 20)), 9);
            Assert.Equal(0, Geometry2.LengthInside(square, new Point2(20, 5), new Point2(30, 5)), 9);
            Point2[] l = { new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10) };   // an L: the segment leaves and re-enters
            Assert.Equal(4 + 0, Geometry2.LengthInside(l, new Point2(0, 7), new Point2(10, 7)), 9);
            Assert.True(Geometry2.Contains(square, new Point2(10, 5)), "on the edge counts as inside");
        }

        // ---- Project settings -----------------------------------------------------------------------------------------

        [Fact]
        public void ProjectSettings_RoundTripTheRules()
        {
            ProjectSettings s = new ProjectSettings { LabelRules = { Rules(Manholes("MH Name", "MH Data"), Joints("Joint")) } };
            ProjectSettings back = ProjectSettings.FromJson(s.ToJson());
            LabelRuleSet set = Assert.Single(back.LabelRules);
            Assert.Equal("KSIA ICT", set.Name);
            Assert.Equal(new[] { "MH Name", "MH Data" }, set.Rules[0].LabelStyles);
            Assert.Equal(TargetKind.Joint, set.Rules[1].Select.Kind);
            Assert.Equal("IC-3?", set.Rules[1].Select.TypeCodePattern);
            Assert.Empty(LabelRuleValidation.Validate(set));
            Assert.Same(set, back.RuleSet("ksia ict"));
            Assert.Equal(450, back.Build.Packing.RowCapacityM);
        }
    }
}
