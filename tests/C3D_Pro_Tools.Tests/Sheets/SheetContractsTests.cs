using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    /// <summary>
    /// The sheet production contracts (docs/sheet-production-contract.md v0.3): validation, pin tolerances, selector patterns,
    /// frozen lists, and defaults that must equal the contract's numbers.
    /// </summary>
    public class SheetContractsTests
    {
        private static readonly DrawingIdentity Drawing = new DrawingIdentity("doc-1", "{0F8E1C2A-0000-4000-8000-000000000001}", @"D:\x\tile-124.dwg", "m");

        /// <summary>The starting rule set of contract section 4.</summary>
        private static LabelRuleSet StartingSet() => new LabelRuleSet("KSIA ICT", 1, new[]
        {
            new LabelRule("ict.plan.manhole", ViewKind.Plan, new TargetSelector(TargetKind.Structure, "ICT*"), new[] { "MH Plan" }, LabelAnchor.Insertion, Priority: 2),
            new LabelRule("ict.plan.joint", ViewKind.Plan, new TargetSelector(TargetKind.Joint, "ICT*"), new[] { "Joint Plan" }, LabelAnchor.EachJoint, Priority: 3),
            new LabelRule("ict.plan.alignment", ViewKind.Plan, new TargetSelector(TargetKind.Alignment, "ICT*"), new[] { "Alignment Name" }, LabelAnchor.Insertion, LabelFrequency.OncePerViewport),
            new LabelRule("ict.plan.stations", ViewKind.Plan, new TargetSelector(TargetKind.AlignmentStations, "ICT*"), new[] { "Chainage 20m" }, LabelAnchor.Insertion, Leader: false),
            new LabelRule("ict.profile.manhole", ViewKind.Profile, new TargetSelector(TargetKind.Structure, "ICT*"), new[] { "MH Profile" }, LabelAnchor.Insertion),
            new LabelRule("ict.profile.segment", ViewKind.Profile, new TargetSelector(TargetKind.Pipe, "ICT*"), new[] { "Segment L S" }, LabelAnchor.Middle),
            new LabelRule("ict.profile.joint", ViewKind.Profile, new TargetSelector(TargetKind.Joint, "ICT*"), new[] { "Joint Profile" }, LabelAnchor.EachJoint),
            new LabelRule("other.profile.crossing", ViewKind.Profile, new TargetSelector(TargetKind.Pipe, OtherNetworksOnly: true), new[] { "Crossing" }, LabelAnchor.Middle),
            new LabelRule("other.profile.structure", ViewKind.Profile, new TargetSelector(TargetKind.Structure, OtherNetworksOnly: true), new[] { "Other Structure" }, LabelAnchor.Insertion),
        });

        private static BuildSheetsRequest ValidRequest() => new BuildSheetsRequest
        {
            Drawing = Drawing,
            Layouts = new[] { "SW-102054", "SW-434132" },
            Steps = new HashSet<BuildStep> { BuildStep.PlanLabels, BuildStep.Arrange },
            Rules = StartingSet(),
        };

        private static string[] Codes(IEnumerable<OperationIssue> issues) => issues.Select(i => i.Code).ToArray();

        // ---- Defaults are the contract's numbers -------------------------------------------------------------------------

        [Fact]
        public void Defaults_MatchTheContract()
        {
            PackingRules p = new PackingRules();
            Assert.Equal(450, p.RowCapacityM);
            Assert.Equal(2, p.RowsPerSheet);
            Assert.Equal(10, p.SplitRoundM);
            Assert.Equal(100, p.MinPieceM);
            Assert.Equal(0, p.OverlapM);
            Assert.Equal(0, p.TitleWidthM);
            Assert.Equal(0, p.GapM);
            Assert.Equal(PackingOrder.Preserve, p.Order);

            DimRules d = new DimRules();
            Assert.Equal(2.0, d.MajorGradeChangePct);
            Assert.Equal(5.0, d.MergeWithinM);
            Assert.Equal(10.0, d.MinSpacingM);
            Assert.True(d.AtStructureFaces);
            Assert.Equal(0.30, d.MinClearanceM);
            Assert.True(new DimInput().OwnsEnd);

            ContinuationRules c = new ContinuationRules();
            Assert.Equal(0.05, c.ToleranceM);
            Assert.Equal(1.0, c.MinSharedM);

            Assert.Equal(20, BuildSettings.Default.MinAlignmentShownM);
            Assert.Equal("0.3", SheetContract.Version);
        }

        // ---- Pinning (section 4) ----------------------------------------------------------------------------------------

        private static readonly RecordedPlacement Placed = new RecordedPlacement("Joint Plan", true, 673579.205, 2761884.733, 0.25, 0.0);

        [Fact]
        public void Placement_WithinTolerances_IsTheSame()
        {
            Assert.False(Placed.DiffersFrom(Placed with { TextXM = Placed.TextXM + 0.0009, TextYM = Placed.TextYM - 0.0009 }));
            Assert.False(Placed.DiffersFrom(Placed with { AnchorRatio = 0.25 + 9e-7 }));
            Assert.False(Placed.DiffersFrom(Placed with { RotationRad = 2 * Math.PI - 5e-5 }), "a full turn is the same rotation");
        }

        [Theory]
        [InlineData("move")]
        [InlineData("style")]
        [InlineData("styleCase")]
        [InlineData("dragged")]
        [InlineData("ratio")]
        [InlineData("ratioNull")]
        [InlineData("rotation")]
        public void Placement_AnyChangeBeyondTolerance_Differs(string change)
        {
            RecordedPlacement now = change switch
            {
                "move" => Placed with { TextXM = Placed.TextXM + 0.002 },
                "style" => Placed with { StyleName = "Joint Plan Small" },
                "styleCase" => Placed with { StyleName = "joint plan" },
                "dragged" => Placed with { Dragged = false },
                "ratio" => Placed with { AnchorRatio = 0.26 },
                "ratioNull" => Placed with { AnchorRatio = null },
                "rotation" => Placed with { RotationRad = 0.01 },
                _ => throw new ArgumentException(change)
            };
            Assert.True(Placed.DiffersFrom(now));
            Assert.True(Placed.DiffersFrom(null));
        }

        [Fact]
        public void BuildTag_IsPinned_ByFlagOrByUserChange()
        {
            BuildTag auto = new BuildTag("ict.plan.joint", "2A3F:start", "SW-102054", "Joint Plan", PinState.Auto, Placed);
            Assert.False(auto.IsPinned(Placed));
            Assert.True(auto.IsPinned(Placed with { TextYM = Placed.TextYM + 1 }));
            Assert.True((auto with { Pin = PinState.Pinned }).IsPinned(Placed));
            Assert.True((auto with { Placement = null }).IsPinned(Placed), "a tag without a recorded placement is never moved");

            Assert.False(new LabelSnapshot(DrawingObjectRef.Of("1A"), DrawingObjectRef.Of("2A3F"), "SW-102054", null, Placed).IsPinned,
                "a hand-placed label is not a pinned tool label (it is never touched at all)");
        }

        [Fact]
        public void BuildTag_Identity_SeparatesStyleSlots()
        {
            BuildTag name = new BuildTag("ict.plan.manhole", "3A1F", "SW-102054", "MH Name", PinState.Auto, Placed);
            BuildTag data = name with { StyleSlot = "MH Data NE" };
            Assert.NotEqual(name.IdentityKey, data.IdentityKey);

            BuildTag copy = name with { StyleSlot = " mh name ", RuleId = "ICT.PLAN.MANHOLE", Pin = PinState.Pinned };
            Assert.Equal(name.IdentityKey, copy.IdentityKey);

            Assert.NotEqual(name.IdentityKey, (name with { Layout = "SW-102053" }).IdentityKey);
            Assert.NotEqual(name.IdentityKey, (name with { SourceKey = "3A20" }).IdentityKey);
            Assert.NotEqual((name with { RuleId = "a", SourceKey = "b|c" }).IdentityKey, (name with { RuleId = "a|b", SourceKey = "c" }).IdentityKey);
        }

        // ---- Selectors (section 4) --------------------------------------------------------------------------------------

        [Theory]
        [InlineData("IC-3A", "IC-3?", true)]
        [InlineData("ic-3b", "IC-3?", true)]
        [InlineData("IC-30X", "IC-3?", false)]
        [InlineData("ICT 124", "ICT*", true)]
        [InlineData("MV 5x1", "ICT*", false)]
        [InlineData("ICx3", "IC.3", false)]
        [InlineData("IC.3", "IC.3", true)]
        [InlineData("", "*", true)]
        [InlineData(null, "IC*", false)]
        [InlineData("anything", null, true)]
        [InlineData("anything", "", true)]
        public void Selector_Like_UsesWildcardsOnly(string value, string pattern, bool expected) =>
            Assert.Equal(expected, TargetSelector.Like(value, pattern));

        [Fact]
        public void Selector_Matches_NeedsBothPatterns()
        {
            TargetSelector s = new TargetSelector(TargetKind.Pipe, "ICT*", "IC-3?");
            Assert.True(s.Matches("ICT Network", "IC-3B"));
            Assert.False(s.Matches("ICT Network", "IC-2B"));
            Assert.False(s.Matches("LV Network", "IC-3B"));
        }

        // ---- Rule set validation ----------------------------------------------------------------------------------------

        [Fact]
        public void RuleSet_TheStartingSet_IsValid() => Assert.Empty(LabelRuleValidation.Validate(StartingSet()));

        [Fact]
        public void RuleSet_Missing_IsAnError() => Assert.Equal(new[] { SheetCodes.RulesMissing }, Codes(LabelRuleValidation.Validate(null)));

        [Fact]
        public void RuleSet_DuplicateIds_IgnoringCase_AreAnError()
        {
            LabelRuleSet set = StartingSet();
            set = set with { Rules = set.Rules.Append(set.Rules[0] with { Id = "ICT.Plan.Manhole" }).ToList() };
            Assert.Equal(new[] { SheetCodes.RuleIdDuplicate }, Codes(LabelRuleValidation.Validate(set)));
        }

        [Theory]
        [InlineData((int)TargetKind.Structure, (int)LabelAnchor.Middle, (int)LabelFrequency.PerPart, SheetCodes.RuleAnchor)]
        [InlineData((int)TargetKind.Pipe, (int)LabelAnchor.Insertion, (int)LabelFrequency.PerPart, SheetCodes.RuleAnchor)]
        [InlineData((int)TargetKind.Joint, (int)LabelAnchor.Start, (int)LabelFrequency.PerPart, SheetCodes.RuleAnchor)]
        [InlineData((int)TargetKind.Structure, (int)LabelAnchor.Insertion, (int)LabelFrequency.PerRun, SheetCodes.RuleFrequency)]
        [InlineData((int)TargetKind.Joint, (int)LabelAnchor.EachJoint, (int)LabelFrequency.OncePerViewport, SheetCodes.RuleFrequency)]
        public void Rule_AnchorAndFrequency_MustSuitTheTarget(int kindValue, int atValue, int everyValue, string code)
        {
            TargetKind kind = (TargetKind)kindValue;
            LabelAnchor at = (LabelAnchor)atValue;
            LabelFrequency every = (LabelFrequency)everyValue;
            LabelRuleSet set = new LabelRuleSet("s", 1, new[] { new LabelRule("r", ViewKind.Plan, new TargetSelector(kind), new[] { "Style" }, at, every) });
            Assert.Equal(new[] { code }, Codes(LabelRuleValidation.Validate(set)));
        }

        [Fact]
        public void Rule_SeveralStyles_GiveSeveralLabels_AndMustBeDistinct()
        {
            LabelRule two = new LabelRule("ict.plan.manhole", ViewKind.Plan, new TargetSelector(TargetKind.Structure), new[] { "MH Name", "MH Data NE" }, LabelAnchor.Insertion);
            Assert.Empty(LabelRuleValidation.Validate(new LabelRuleSet("s", 1, new[] { two })));
            Assert.Equal(2, two.LabelStyles.Count);

            LabelRule dup = two with { LabelStyles = new[] { "MH Name", "mh name " } };
            Assert.Equal(new[] { SheetCodes.RuleStyleDuplicate }, Codes(LabelRuleValidation.Validate(new LabelRuleSet("s", 1, new[] { dup }))));

            LabelRule none = two with { LabelStyles = Array.Empty<string>() };
            Assert.Equal(new[] { SheetCodes.RuleStyleEmpty }, Codes(LabelRuleValidation.Validate(new LabelRuleSet("s", 1, new[] { none }))));
            Assert.Equal(new[] { SheetCodes.RuleStyleEmpty }, Codes(LabelRuleValidation.Validate(new LabelRuleSet("s", 1, new[] { two with { LabelStyles = null } }))));
        }

        [Fact]
        public void Rule_PerRun_IsAllowedForPipes()
        {
            LabelRuleSet set = new LabelRuleSet("s", 1, new[] { new LabelRule("r", ViewKind.Plan, new TargetSelector(TargetKind.Pipe), new[] { "Style" }, LabelAnchor.Middle, LabelFrequency.PerRun) });
            Assert.Empty(LabelRuleValidation.Validate(set));
        }

        [Fact]
        public void Rule_EmptyIdStyleUnknownEnumAndPlanCrossing_AreErrors()
        {
            LabelRuleSet set = new LabelRuleSet(" ", 1, new[]
            {
                new LabelRule("", ViewKind.Plan, new TargetSelector(TargetKind.Structure), new[] { " " }, LabelAnchor.Insertion),
                new LabelRule("bad.enum", (ViewKind)9, new TargetSelector(TargetKind.Structure), new[] { "S" }, LabelAnchor.Insertion),
                new LabelRule("no.selector", ViewKind.Plan, null, new[] { "S" }, LabelAnchor.Insertion),
                new LabelRule("plan.crossing", ViewKind.Plan, new TargetSelector(TargetKind.Pipe, OtherNetworksOnly: true), new[] { "S" }, LabelAnchor.Middle),
                null,
            });
            Assert.Equal(
                new[] { SheetCodes.RulesName, SheetCodes.RuleId, SheetCodes.RuleStyleEmpty, SheetCodes.RuleEnum, SheetCodes.RuleEnum, SheetCodes.RuleEnum, SheetCodes.RuleId },
                Codes(LabelRuleValidation.Validate(set)));
        }

        // ---- Build request validation (section 10) ----------------------------------------------------------------------

        [Fact]
        public void Request_Valid_HasNoIssues() => Assert.Empty(BuildValidation.ValidateRequest(ValidRequest()));

        [Fact]
        public void Request_EachProblem_IsReported()
        {
            BuildSheetsRequest r = ValidRequest() with
            {
                Drawing = new DrawingIdentity("", "", null),
                Steps = new HashSet<BuildStep>(),
                Layouts = new[] { "A", "a", " " },
                Settings = BuildSettings.Default with { Dims = null },
                Rules = null,
            };
            Assert.Equal(
                new[] { SheetCodes.RequestDrawing, SheetCodes.RequestNoSteps, SheetCodes.RequestLayout, SheetCodes.RequestLayout, SheetCodes.RequestSettings, SheetCodes.RulesMissing },
                Codes(BuildValidation.ValidateRequest(r)));
            Assert.Equal(new[] { SheetCodes.RequestDrawing }, Codes(BuildValidation.ValidateRequest(null)));
        }

        [Fact]
        public void Request_NegativeAlignmentLength_IsASettingsError()
        {
            BuildSheetsRequest r = ValidRequest() with { Settings = BuildSettings.Default with { MinAlignmentShownM = double.NaN } };
            Assert.Equal(new[] { SheetCodes.RequestSettings }, Codes(BuildValidation.ValidateRequest(r)));
        }

        private static BuildSheetsPreview Preview(OperationStatus status) => new BuildSheetsPreview(status, "p1", Drawing, "stamp", ValidRequest(),
            new[] { new BuildItem("plan:SW-102054", BuildItemKind.PlanSheet, new[] { "SW-102054" }, Array.Empty<PlannedChange>()) }, null);

        [Theory]
        [InlineData((int)OperationStatus.NoChange)]
        [InlineData((int)OperationStatus.Rejected)]
        [InlineData((int)OperationStatus.Cancelled)]
        public void Apply_OnlyASucceededPreview_IsApplicable(int statusValue)
        {
            OperationStatus status = (OperationStatus)statusValue;
            Assert.Equal(new[] { SheetCodes.PreviewNotApplicable }, Codes(BuildValidation.ValidateApply(new BuildSheetsApplyRequest { Preview = Preview(status) })));
            Assert.Equal(new[] { SheetCodes.PreviewNotApplicable }, Codes(BuildValidation.ValidateApply(new BuildSheetsApplyRequest())));
        }

        [Fact]
        public void Apply_ItemKeys_MustBeItemsOfThePreview()
        {
            BuildSheetsPreview p = Preview(OperationStatus.Succeeded);
            Assert.Empty(BuildValidation.ValidateApply(new BuildSheetsApplyRequest { Preview = p }));
            Assert.Empty(BuildValidation.ValidateApply(new BuildSheetsApplyRequest { Preview = p, ItemKeys = new[] { "plan:SW-102054" } }));
            Assert.Equal(new[] { SheetCodes.ApplyUnknownItem, SheetCodes.ApplyUnknownItem },
                Codes(BuildValidation.ValidateApply(new BuildSheetsApplyRequest { Preview = p, ItemKeys = new[] { "plan:SW-102054", "plan:SW-102054", "plan:nope" } })));
        }

        [Fact]
        public void Issue_Layout_IsOptional_AndKeptByWith()
        {
            OperationIssue drawingWide = OperationIssue.Warning(SheetCodes.UnitsNotMetres, "Not metres.");
            Assert.Null(drawingWide.Layout);
            OperationIssue onSheet = OperationIssue.Error(SheetCodes.LabelOrphanPinned, "Orphan.", obj: DrawingObjectRef.Of("1B")) with { Layout = "SW-102054" };
            Assert.Equal("SW-102054", onSheet.Layout);
            Assert.Equal("SW-102054", (onSheet with { Message = "Changed." }).Layout);
            Assert.NotEqual(onSheet, onSheet with { Layout = "SW-102053" });
        }

        [Fact]
        public void EmptyPreview_CarriesItsStatusAndNoItems()
        {
            BuildSheetsPreview p = BuildSheetsPreview.Empty(OperationStatus.Rejected, Drawing, ValidRequest(), new[] { OperationIssue.Error(SheetCodes.Busy, "busy") });
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Empty(p.Items);
            Assert.Equal(new[] { SheetCodes.Busy }, Codes(p.Issues));
            Assert.False(string.IsNullOrEmpty(p.PreviewId));
        }

        // ---- Frozen: what the user previewed cannot change underneath ---------------------------------------------------

        [Fact]
        public void Lists_AreCopiedWhenSet()
        {
            List<LabelRule> rules = StartingSet().Rules.ToList();
            LabelRuleSet set = new LabelRuleSet("s", 1, rules);
            rules.Clear();
            Assert.Equal(9, set.Rules.Count);

            List<StationElevation> run = new List<StationElevation> { new(0, 10), new(10, 10) };
            List<IReadOnlyList<StationElevation>> runs = new List<IReadOnlyList<StationElevation>> { run };
            DimInput input = new DimInput { ProposedSurface = runs };
            run.Add(new StationElevation(20, 10));
            runs.Add(run);
            Assert.Single(input.ProposedSurface);
            Assert.Equal(2, input.ProposedSurface[0].Count);

            HashSet<BuildStep> steps = new HashSet<BuildStep> { BuildStep.Dims };
            BuildSheetsRequest r = new BuildSheetsRequest { Steps = steps };
            steps.Add(BuildStep.Arrange);
            Assert.Single(r.Steps);

            Dictionary<BuildStep, StepState> states = new Dictionary<BuildStep, StepState> { [BuildStep.Dims] = StepState.Built };
            SheetStatus status = new SheetStatus("L", SheetKind.Profile, "", "", "D", states, 0);
            states[BuildStep.Dims] = StepState.OutOfDate;
            Assert.Equal(StepState.Built, status.Steps[BuildStep.Dims]);

            HashSet<string> keep = new HashSet<string> { "ICT" };
            ScoredLabel label = new ScoredLabel("k", new Point2(0, 0), keep, null, false, null, null, null);
            keep.Add("LV");
            Assert.True(label.KeepClearOf.Contains("ict"), "names compare ignoring case");
            Assert.False(label.KeepClearOf.Contains("LV"));
        }

        [Fact]
        public void SceneGeometry_IsFrozen_AndEqualOnlyWhenTheSame()
        {
            List<StationElevation> run = new List<StationElevation> { new(0, 612.5), new(10, 612.7) };
            SceneProfile fg = new SceneProfile(DrawingObjectRef.Of("5A"), "FG", SceneProfileRole.Proposed, new[] { run });
            run.Add(new StationElevation(20, 612.9));
            Assert.Single(fg.Runs);
            Assert.Equal(2, fg.Runs[0].Count);

            SceneProfile regraded = fg with { Runs = new[] { new[] { new StationElevation(0, 612.5), new StationElevation(10, 612.8) } } };
            Assert.Equal(612.8, regraded.Runs[0][1].ElevationM);
            Assert.Equal(612.7, fg.Runs[0][1].ElevationM);

            List<ViewPart> parts = new List<ViewPart> { new(DrawingObjectRef.Of("6B"), ViewPartKind.Pipe, true, 0, 44.58, 611.8, 611.0, 611.6, 610.8) };
            ProfileViewSnapshot view = new ProfileViewSnapshot(DrawingObjectRef.Of("7C"), "ICT-ALI-124-1-89-01", DrawingObjectRef.Of("8D"),
                0, 313.56, 605, 615, "KSIA bands", parts, 10);
            parts.Clear();
            Assert.Single(view.DrawnParts);
            Assert.Equal(611.0, view.DrawnParts[0].StartBottomM);
        }

        [Fact]
        public void NullLists_BecomeEmpty()
        {
            Assert.Empty(new SceneProfile(DrawingObjectRef.Of("5A"), "FG", SceneProfileRole.Proposed, null).Runs);
            Assert.Empty(new DimInput().ProposedSurface);
            Assert.Empty(new ProfileSheetPlan(null, null).Sheets);
            Assert.Empty(new DimPlan(null, null).Issues);
            Assert.Empty(new ContinuationPlan(null, null).Edges);
            Assert.Empty(new SheetScene().Labels);
            Assert.Empty(new BuildSheetsApplyRequest().ItemKeys);
        }

        // ---- 2D values --------------------------------------------------------------------------------------------------

        [Fact]
        public void Rect2_ValidityAndContainment()
        {
            Rect2 r = new Rect2(0, 0, 10, 5);
            Assert.True(r.IsValid);
            Assert.True(r.Contains(new Point2(10, 5)));
            Assert.False(r.Contains(new Point2(10.001, 5)));
            Assert.False(new Rect2(1, 0, 0, 1).IsValid);
            Assert.False(new Rect2(0, 0, double.NaN, 1).IsValid);
            Assert.Equal(5.0, new Point2(0, 0).DistanceTo(new Point2(3, 4)), 12);
        }
    }
}
