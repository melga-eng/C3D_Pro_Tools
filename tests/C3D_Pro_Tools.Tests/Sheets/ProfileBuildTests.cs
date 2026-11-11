using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    /// <summary>
    /// The ProfileLabels step in the build: which profile sheet shows which view, one item per profile sheet (labels and
    /// Arrange), the stamp covering the views, and the profile sheets' states in the overview.
    /// </summary>
    public class ProfileBuildTests
    {
        private static readonly DrawingIdentity Drawing = new DrawingIdentity("doc-1", "{0F8E1C2A-0000-4000-8000-000000000001}", "tile.dwg", "m");
        private static DrawingObjectRef R(string h) => DrawingObjectRef.Of(h);

        private static ViewportSnapshot Vp(string h, double x0, double y0, double x1, double y1) => new ViewportSnapshot(R(h), 1.333, 0,
            new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) }, new Rect2(0, 0, 700, 560), true);

        // Two profile sheets over model areas 0–1000 and 1000–2000 (x), a plan sheet elsewhere.
        private static readonly SheetSnapshot P1 = new SheetSnapshot("SW-P1", 2, SheetKind.Profile, "", "", "SW-P1", new[] { Vp("E1", 0, 0, 1000, 500) });
        private static readonly SheetSnapshot P2 = new SheetSnapshot("SW-P2", 3, SheetKind.Profile, "", "", "SW-P2", new[] { Vp("E2", 1000, 0, 2000, 500) });
        private static readonly SheetSnapshot A = new SheetSnapshot("SW-A", 1, SheetKind.Plan, "124", "A", "SW-A", new[] { Vp("E3", 5000, 5000, 5100, 5100) });

        private static ProfileViewSnapshot View(string h, double x0, double x1, double pipeEnd = 40, string alignment = "A0") =>
            new ProfileViewSnapshot(R(h), "View " + h, R(alignment), 0, 100, 605, 615, "", new[]
            {
                new ViewPart(R("21"), ViewPartKind.Pipe, true, 0, pipeEnd, 611, 610, 611, 610),
                new ViewPart(R("31"), ViewPartKind.Structure, true, 0, 2, 613, 610, 613, 610),
            }, 10) { ModelBox = new Rect2(x0, 100, x1, 300) };

        private static SheetScene Scene(IReadOnlyList<ProfileViewSnapshot> views = null, IReadOnlyList<LabelSnapshot> labels = null) => new SheetScene
        {
            Drawing = Drawing,
            Sheets = new[] { A, P1, P2 },
            Networks = new[]
            {
                new NetworkSnapshot(R("10"), "ICT",
                    new[] { new ScenePipe(R("21"), "IC-1", "ICT", "IC-3A", new Point3(0, 0, 610), new Point3(40, 0, 610), 0.79, 1.11) },
                    new[] { new SceneStructure(R("31"), "MH-1", "ICT", "Type C", new Point3(0, 0, 613)) })
            },
            ProfileViews = views ?? new[] { View("B1", 100, 400), View("B2", 1200, 1500, alignment: "A9") },
            LabelStyles = new[]
            {
                new SceneLabelStyle(LabelStyleKind.Pipe, "Segment", R("51"), "f1"),
                new SceneLabelStyle(LabelStyleKind.Structure, "MH Profile", R("52"), "f2"),
            },
            Labels = labels ?? Array.Empty<LabelSnapshot>(),
        };

        private static readonly LabelRuleSet Rules = new LabelRuleSet("KSIA", 1, new[]
        {
            new LabelRule("ict.profile.segment", ViewKind.Profile, new TargetSelector(TargetKind.Pipe, "ICT"), new[] { "Segment" }, LabelAnchor.Middle),
            new LabelRule("ict.profile.manhole", ViewKind.Profile, new TargetSelector(TargetKind.Structure, "ICT"), new[] { "MH Profile" }, LabelAnchor.Insertion),
        });

        private static BuildSheetsRequest Request(params BuildStep[] steps) => new BuildSheetsRequest
        {
            Drawing = Drawing, Steps = new HashSet<BuildStep>(steps.Length == 0 ? new[] { BuildStep.ProfileLabels, BuildStep.Arrange } : steps)
        };

        [Fact]
        public void Map_PutsEachViewOnTheProfileSheetHoldingItsCentre()
        {
            List<ProfileViewLayout> map = ProfileViewSheets.Map(Scene(new[] { View("B1", 100, 400), View("B2", 1200, 1500), View("B3", 9000, 9100) }));
            Assert.Equal(new[] { ("B1", "SW-P1"), ("B2", "SW-P2") }, map.Select(m => (m.View.Handle, m.Layout)));
            Assert.Empty(ProfileViewSheets.Map(Scene(new[] { View("B1", 100, 400) with { ModelBox = null } })));
        }

        [Fact]
        public void ProfileLabels_GiveOneItemPerProfileSheet_WithArrange()
        {
            BuildSheetsPreview p = BuildSheetsPlanner.Preview(Scene(new[] { View("B1", 100, 400) }), Request(), Rules, "ctx");
            Assert.Equal(OperationStatus.Succeeded, p.Status);
            BuildItem item = Assert.Single(p.Items);
            Assert.Equal("profile:SW-P1", item.ItemKey);
            Assert.Equal(BuildItemKind.ProfileSheetSet, item.Kind);
            Assert.Equal(new[] { BuildStep.ProfileLabels, BuildStep.ProfileLabels, BuildStep.Arrange }, item.Changes.Select(c => c.Step));
            Assert.All(item.Changes.Where(c => c.Step == BuildStep.ProfileLabels), c => Assert.Equal(R("B1"), ProfileLabelPlanner.ViewOf(c.SourceKey)));
            Assert.DoesNotContain(p.Issues, i => i.Code == SheetCodes.StepNotBuilt);
        }

        [Fact]
        public void PlanOnlyBuilds_LeaveProfileSheetsAlone()
        {
            BuildSheetsPreview p = BuildSheetsPlanner.Preview(Scene(), Request(BuildStep.PlanLabels), Rules, "ctx");
            Assert.Empty(p.Items);
        }

        [Fact]
        public void TheStamp_CoversTheViewsOnTheProfileSheets()
        {
            string stamp = BuildSheetsPlanner.Preview(Scene(new[] { View("B1", 100, 400) }), Request(BuildStep.ProfileLabels), Rules).SceneStamp;
            string longerPipe = BuildSheetsPlanner.Preview(Scene(new[] { View("B1", 100, 400, pipeEnd: 45) }), Request(BuildStep.ProfileLabels), Rules).SceneStamp;
            string movedView = BuildSheetsPlanner.Preview(Scene(new[] { View("B1", 110, 410) }), Request(BuildStep.ProfileLabels), Rules).SceneStamp;
            Assert.NotEqual(stamp, longerPipe);
            Assert.NotEqual(stamp, movedView);
            Assert.Equal(stamp, BuildSheetsPlanner.Preview(Scene(new[] { View("B1", 100, 400) }), Request(BuildStep.ProfileLabels), Rules).SceneStamp);
        }

        // ---- Codex review of feature/profile-labels ----------------------------------------------------------------------

        [Fact]
        public void TheStamp_CoversProfileElevations()
        {
            ProfileViewSnapshot view = View("B1", 100, 400);
            ProfileViewSnapshot rimRaised = view with
            {
                DrawnParts = view.DrawnParts.Select(p => p.Kind == ViewPartKind.Structure ? p with { StartTopM = 613.5, EndTopM = 613.5 } : p).ToList()
            };
            string stamp = BuildSheetsPlanner.Preview(Scene(new[] { view }), Request(BuildStep.ProfileLabels), Rules).SceneStamp;
            Assert.NotEqual(stamp, BuildSheetsPlanner.Preview(Scene(new[] { rimRaised }), Request(BuildStep.ProfileLabels), Rules).SceneStamp);
            Assert.NotEqual(stamp, BuildSheetsPlanner.Preview(Scene(new[] { view with { VerticalExaggeration = 5 } }), Request(BuildStep.ProfileLabels), Rules).SceneStamp);
        }

        [Fact]
        public void AViewMovedToAnotherSheet_LosesItsOldLabels_AndGetsNewOnes()
        {
            // B1 was built on SW-P1 and now shows on SW-P2.
            ProfileViewSnapshot moved = View("B1", 1100, 1400);
            string seg = ProfileLabelPlanner.SourceKey(R("21"), R("B1")), mh = ProfileLabelPlanner.SourceKey(R("31"), R("B1"));
            RecordedPlacement at = new RecordedPlacement("Segment", false, 0, 0, 0.5, 0);
            LabelSnapshot Old(string h, string rule, string source, string style, PinState pin = PinState.Auto) =>
                new LabelSnapshot(R(h), R(source.Split(':')[0]), "", new BuildTag(rule, source, "SW-P1", style, pin, at), at);
            LabelSnapshot[] labels = { Old("AA1", "ict.profile.segment", seg, "Segment"), Old("AA2", "ict.profile.manhole", mh, "MH Profile", PinState.Pinned) };

            foreach (string[] layouts in new[] { Array.Empty<string>(), new[] { "SW-P1" }, new[] { "SW-P2" } })
            {
                BuildSheetsRequest req = Request(BuildStep.ProfileLabels) with { Layouts = layouts };
                BuildSheetsPreview p = BuildSheetsPlanner.Preview(Scene(new[] { moved }, labels), req, Rules);
                List<PlannedChange> all = p.Items.SelectMany(i => i.Changes).ToList();
                PlannedChange removal = Assert.Single(all, c => c.Kind == ChangeKind.Remove);
                Assert.Equal(R("AA1"), removal.Existing);
                Assert.Contains(p.Issues, i => i.Code == SheetCodes.LabelOrphanPinned && i.Object == R("AA2"));
                Assert.Equal(layouts.Contains("SW-P1") ? 0 : 2, all.Count(c => c.Kind == ChangeKind.Add));
            }
        }

        [Fact]
        public void Overview_ShowsTheProfileLabelsState_OfProfileSheets()
        {
            SheetOverview o = BuildSheetsPlanner.Overview(Scene(), Rules);
            Assert.Equal(StepState.NotBuilt, o.Sheets.Single(s => s.Layout == "SW-P1").Steps[BuildStep.ProfileLabels]);
            Assert.Equal(StepState.NotBuilt, o.Sheets.Single(s => s.Layout == "SW-P2").Steps[BuildStep.ProfileLabels]);
            Assert.Equal(StepState.NotApplicable, o.Sheets.Single(s => s.Layout == "SW-A").Steps[BuildStep.ProfileLabels]);
        }
    }
}
