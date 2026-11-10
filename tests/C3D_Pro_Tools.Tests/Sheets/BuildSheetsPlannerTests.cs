using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    /// <summary>
    /// The build planner (docs/sheet-production-contract.md, sections 5 and 10): PlanLabels and Arrange joined per sheet, steps
    /// not built yet reported, the preview digest Apply checks, and the Sheets panel's per-step states.
    /// </summary>
    public class BuildSheetsPlannerTests
    {
        private static readonly DrawingIdentity Drawing = new DrawingIdentity("doc-1", "{0F8E1C2A-0000-4000-8000-000000000001}", "tile.dwg", "m");

        private static ViewportSnapshot Vp(string h, double x0, double x1) => new ViewportSnapshot(DrawingObjectRef.Of(h), 2.0, 0,
            new[] { new Point2(x0, 0), new Point2(x1, 0), new Point2(x1, 100), new Point2(x0, 100) }, new Rect2(0, 0, 200, 200), true);

        private static readonly SheetSnapshot A = new SheetSnapshot("SW-A", 1, SheetKind.Plan, "124", "A", "SW-A", new[] { Vp("A1", 0, 100) });
        private static readonly SheetSnapshot B = new SheetSnapshot("SW-B", 2, SheetKind.Plan, "124", "B", "SW-B", new[] { Vp("B1", 100, 200) });
        private static readonly SheetSnapshot P = new SheetSnapshot("SW-P", 3, SheetKind.Profile, "", "", "SW-P", new[] { Vp("C1", 0, 200) });

        private static readonly RecordedPlacement At = new RecordedPlacement("MH Name", true, 10, 50, null, 0);

        private static LabelSnapshot Tool(string h, string source, string layout, PinState pin = PinState.Auto, RecordedPlacement now = null) =>
            new LabelSnapshot(DrawingObjectRef.Of(h), DrawingObjectRef.Of(source), layout, new BuildTag("ict.plan.manhole", source, layout, "MH Name", pin, At), now ?? At);

        private static LabelSnapshot ByHand(string h, string layout, double x) =>
            new LabelSnapshot(DrawingObjectRef.Of(h), DrawingObjectRef.Of("99"), layout, null, At with { TextXM = x });

        private static SheetScene Scene(IReadOnlyList<LabelSnapshot> labels = null, IReadOnlyList<BuildRecord> records = null) => new SheetScene
        {
            Drawing = Drawing,
            Sheets = new[] { A, B, P },
            Networks = new[]
            {
                new NetworkSnapshot(DrawingObjectRef.Of("10"), "ICT", null, new[]
                {
                    new SceneStructure(DrawingObjectRef.Of("31"), "MH-1", "ICT", "Type C", new Point3(10, 50, 613)),
                    new SceneStructure(DrawingObjectRef.Of("32"), "MH-2", "ICT", "Type C", new Point3(150, 50, 613)),
                })
            },
            LabelStyles = new[] { new SceneLabelStyle(LabelStyleKind.Structure, "MH Name", DrawingObjectRef.Of("54"), "f4") },
            Labels = labels ?? Array.Empty<LabelSnapshot>(),
            BuildRecords = records ?? Array.Empty<BuildRecord>(),
        };

        private static readonly LabelRuleSet Rules = new LabelRuleSet("KSIA", 1, new[]
        {
            new LabelRule("ict.plan.manhole", ViewKind.Plan, new TargetSelector(TargetKind.Structure, "ICT"), new[] { "MH Name" }, LabelAnchor.Insertion)
        });

        private static BuildSheetsRequest Request(params BuildStep[] steps) => new BuildSheetsRequest
        {
            Drawing = Drawing, Steps = new HashSet<BuildStep>(steps.Length == 0 ? new[] { BuildStep.PlanLabels, BuildStep.Arrange } : steps)
        };

        [Fact]
        public void LabelsAndArrange_AreJoinedPerSheet_AndNumberedInOrder()
        {
            BuildSheetsPreview p = BuildSheetsPlanner.Preview(Scene(), Request(), Rules);
            Assert.Equal(OperationStatus.Succeeded, p.Status);
            BuildItem a = Assert.Single(p.Items, i => i.ItemKey == "plan:SW-A");
            Assert.Equal(new[] { BuildStep.PlanLabels, BuildStep.Arrange }, a.Changes.Select(c => c.Step));
            Assert.Equal(new[] { "SW-A#1", "SW-A#2" }, a.Changes.Select(c => c.ChangeId));
            Assert.Contains("1 tool label", a.Changes[1].Description);
            Assert.Equal(2, p.Items.Count);
            Assert.False(string.IsNullOrEmpty(p.SceneStamp));
        }

        [Fact]
        public void ArrangeAlone_MovesOnlyUnpinnedToolLabels()
        {
            BuildSheetsPreview some = BuildSheetsPlanner.Preview(Scene(new[] { Tool("AB1", "31", "SW-A"), Tool("AB2", "31", "SW-A", PinState.Pinned) }), Request(BuildStep.Arrange), Rules);
            PlannedChange arrange = Assert.Single(Assert.Single(some.Items).Changes);
            Assert.Equal(BuildStep.Arrange, arrange.Step);
            Assert.Contains("1 tool label", arrange.Description);

            BuildSheetsPreview none = BuildSheetsPlanner.Preview(Scene(new[] { Tool("AB2", "31", "SW-A", PinState.Pinned), ByHand("CD1", "SW-A", 20) }), Request(BuildStep.Arrange), Rules);
            Assert.Equal(OperationStatus.NoChange, none.Status);
            Assert.Empty(none.Items);
        }

        [Fact]
        public void StepsNotBuiltYet_AreReported_AndLeftOut()
        {
            BuildSheetsPreview p = BuildSheetsPlanner.Preview(Scene(), Request(BuildStep.PlanLabels, BuildStep.Dims, BuildStep.ProfileViews), Rules);
            Assert.Equal(2, p.Issues.Count(i => i.Code == SheetCodes.StepNotBuilt));
            Assert.All(p.Items.SelectMany(i => i.Changes), c => Assert.Equal(BuildStep.PlanLabels, c.Step));
        }

        [Fact]
        public void InvalidRequests_AndIncompleteScenes_AreRejected()
        {
            Assert.Equal(OperationStatus.Rejected, BuildSheetsPlanner.Preview(Scene(), Request() with { Steps = new HashSet<BuildStep>() }, Rules).Status);
            Assert.Equal(OperationStatus.Rejected, BuildSheetsPlanner.Preview(Scene(), Request(), null).Status);
            SheetScene broken = Scene() with { Issues = new[] { OperationIssue.Warning(SheetCodes.SceneReadFailed, "A pipe could not be read.") } };
            BuildSheetsPreview p = BuildSheetsPlanner.Preview(broken, Request(), Rules);
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Contains(p.Issues, i => i.Code == SheetCodes.SceneIncomplete);
        }

        [Fact]
        public void TheStamp_CoversTheLabelsArrangeWorksAround()
        {
            LabelSnapshot[] before = { Tool("AB1", "31", "SW-A"), ByHand("CD1", "SW-A", 20), ByHand("CD2", "SW-B", 120) };
            string stamp = BuildSheetsPlanner.Preview(Scene(before), Request(BuildStep.Arrange), Rules).SceneStamp;
            LabelSnapshot[] handMovedOnA = { before[0], ByHand("CD1", "SW-A", 25), before[2] };
            LabelSnapshot[] handMovedOnB = { before[0], before[1], ByHand("CD2", "SW-B", 125) };
            Assert.Equal(stamp, BuildSheetsPlanner.Preview(Scene(before), Request(BuildStep.Arrange), Rules).SceneStamp);
            Assert.NotEqual(stamp, BuildSheetsPlanner.Preview(Scene(handMovedOnA), Request(BuildStep.Arrange), Rules).SceneStamp);
            Assert.Equal(stamp, BuildSheetsPlanner.Preview(Scene(handMovedOnB), Request(BuildStep.Arrange), Rules).SceneStamp);   // B is not arranged
            Assert.NotEqual(stamp, BuildSheetsPlanner.Preview(Scene(before), Request(BuildStep.Arrange, BuildStep.PlanLabels), Rules).SceneStamp);
        }

        [Fact]
        public void Overview_ShowsEachSheetsStepStates()
        {
            // A: built (its label exists) and arranged before; B: nothing built yet; P: profile, not built yet.
            SheetScene scene = Scene(new[] { Tool("AB1", "31", "SW-A") }, new[] { new BuildRecord("SW-A", BuildStep.Arrange, "x") });
            SheetOverview o = BuildSheetsPlanner.Overview(scene, Rules);
            SheetStatus a = o.Sheets.Single(s => s.Layout == "SW-A"), b = o.Sheets.Single(s => s.Layout == "SW-B"), p = o.Sheets.Single(s => s.Layout == "SW-P");
            Assert.Equal(StepState.Built, a.Steps[BuildStep.PlanLabels]);
            Assert.Equal(StepState.Built, a.Steps[BuildStep.Arrange]);
            Assert.Equal(StepState.NotBuilt, b.Steps[BuildStep.PlanLabels]);
            Assert.Equal(StepState.NotBuilt, b.Steps[BuildStep.Arrange]);
            Assert.All(p.Steps.Values, s => Assert.Equal(StepState.NotApplicable, s));
            Assert.Equal(StepState.NotApplicable, a.Steps[BuildStep.Dims]);
        }

        [Fact]
        public void Overview_MarksOutOfDate_AndIssues_PerSheet()
        {
            // A's label belongs to MH-1, but a second style was added: A has changes planned although it was built before.
            LabelRuleSet twoStyles = new LabelRuleSet("KSIA", 1, new[]
            {
                new LabelRule("ict.plan.manhole", ViewKind.Plan, new TargetSelector(TargetKind.Structure, "ICT"), new[] { "MH Name", "MH Data" }, LabelAnchor.Insertion)
            });
            SheetScene scene = Scene(new[] { Tool("AB1", "31", "SW-A") }) with
            {
                LabelStyles = new[]
                {
                    new SceneLabelStyle(LabelStyleKind.Structure, "MH Name", DrawingObjectRef.Of("54"), "f4"),
                    new SceneLabelStyle(LabelStyleKind.Structure, "MH Data", DrawingObjectRef.Of("55"), "f5"),
                },
                Issues = new[] { OperationIssue.Warning(SheetCodes.LabelOrphanPinned, "kept", obj: DrawingObjectRef.Of("AB9")) with { Layout = "SW-A" } }
            };
            SheetOverview o = BuildSheetsPlanner.Overview(scene, twoStyles);
            SheetStatus a = o.Sheets.Single(s => s.Layout == "SW-A");
            Assert.Equal(StepState.OutOfDate, a.Steps[BuildStep.PlanLabels]);
            Assert.Equal(1, a.IssueCount);
            Assert.Equal(0, o.Sheets.Single(s => s.Layout == "SW-B").IssueCount);
        }
    }
}
