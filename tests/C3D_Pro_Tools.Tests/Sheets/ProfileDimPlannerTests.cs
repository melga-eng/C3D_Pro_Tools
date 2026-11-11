using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Tests.Sheets
{
    public class ProfileDimPlannerTests
    {
        private static DrawingObjectRef R(string h) => DrawingObjectRef.Of(h);
        private static readonly DrawingObjectRef Al = R("A1"), View = R("B1"), Pipe1 = R("C1"), Pipe2 = R("C2"), Mh = R("D1"), Cross = R("E1");

        private static StationElevation P(double s, double z) => new StationElevation(s, z);

        // TOP: flat at 98 to 40, then down 5 % to 60 (97), then flat to 100. Surface flat at 100. Duct 0.5 m high.
        // A structure spans 48 to 52 in the middle of the slope.
        private static SheetScene Scene(bool withTop = true, bool shown = true, bool ownPipes = true, double crossBottom = 99.0)
        {
            List<SceneProfile> profiles = new List<SceneProfile>
            {
                new SceneProfile(R("F1"), "KSIA_Proposed-Surface", SceneProfileRole.Proposed, new[] { new[] { P(0, 100), P(100, 100) } }),
            };
            if (withTop)
                profiles.Add(new SceneProfile(R("F2"), "AL_TOP", SceneProfileRole.Top, new[] { new[] { P(0, 98), P(40, 98), P(60, 97), P(100, 97) } }));
            AlignmentSnapshot al = new AlignmentSnapshot(Al, "AL", 0, 100, profiles, new[] { View });
            List<ViewPart> parts = new List<ViewPart>
            {
                new ViewPart(Mh, ViewPartKind.Structure, true, 48, 52, 100, 95, 100, 95),
            };
            if (ownPipes)
            {
                parts.Add(new ViewPart(Pipe1, ViewPartKind.Pipe, true, 0, 48, 98, 97.5, 97.6, 97.1));
                // Drawn end to start: the planner turns it round.
                parts.Add(new ViewPart(Pipe2, ViewPartKind.Pipe, true, 100, 52, 97, 96.5, 97.4, 96.9));
            }
            ProfileViewSnapshot view = new ProfileViewSnapshot(View, "V", Al, 0, 100, 90, 105, "", parts, 10);
            return new SheetScene
            {
                Alignments = new[] { al }, ProfileViews = new[] { view },
                Crossings = new[] { new CrossingSnapshot(View, Cross, "STORM", "SW-1", 20, crossBottom + 0.3, crossBottom, shown) },
            };
        }

        private static ViewDimPlan Plan(SheetScene scene, ProfileDimOptions o = null) => ProfileDimPlanner.PlanView(scene, scene.ProfileViews[0], o ?? new ProfileDimOptions());

        [Fact]
        public void CoverDimsAtStructureFacesAndGradeBreaks_UseTopAndProposedSurface()
        {
            ViewDimPlan plan = Plan(Scene());
            Assert.Equal("AL_TOP", plan.TopProfile);
            Assert.Equal("KSIA_Proposed-Surface", plan.SurfaceProfile);
            List<PlacedDim> cover = plan.Dims.Where(d => d.Dim.Kind == DimKind.Cover).ToList();
            // Faces at 48 and 52, grade breaks at 40 and 60 (5 %). Faces go first; 52 and 40 are within 10 m of 48 and drop.
            Assert.Equal(new[] { 48.0, 60.0 }, cover.Select(d => d.Dim.StationM).ToArray());
            Assert.Equal(DimReasons.StructureFace, cover[0].Dim.Reason);
            Assert.Equal(DimReasons.GradeBreak, cover[1].Dim.Reason);
            Assert.Equal(-1, cover[0].Side);   // start face: the DIM line goes back, over the duct
            Assert.Equal(1, cover[1].Side);
            Assert.Equal(100 - 97.6, cover[0].Dim.ValueM, 6);
            Assert.Equal(3.0, cover[1].Dim.ValueM, 6);
        }

        [Fact]
        public void EndFace_GoesAheadWhenSpacingAllows()
        {
            ViewDimPlan plan = Plan(Scene(), new ProfileDimOptions { Rules = new DimRules { MinSpacingM = 2 } });
            PlacedDim end = Assert.Single(plan.Dims, d => d.Dim.StationM == 52);
            Assert.Equal(1, end.Side);
        }

        [Fact]
        public void GradeBreaks_WhenFacesAreOff()
        {
            ViewDimPlan plan = Plan(Scene(), new ProfileDimOptions { Rules = new DimRules { AtStructureFaces = false } });
            Assert.Equal(new[] { 40.0, 60.0 }, plan.Dims.Where(d => d.Dim.Kind == DimKind.Cover).Select(d => d.Dim.StationM).ToArray());
        }

        [Fact]
        public void ClearanceDim_ToAShownCrossingAbove()
        {
            ViewDimPlan plan = Plan(Scene());
            PlacedDim c = Assert.Single(plan.Dims, d => d.Dim.Kind == DimKind.Clearance);
            Assert.Equal(20, c.Dim.StationM, 6);
            Assert.Equal(98, c.Dim.LowerElevM, 6);
            Assert.Equal(1.0, c.Dim.ValueM, 6);
            Assert.Equal("SW-1 (STORM)", c.Dim.SourceKey);
        }

        [Fact]
        public void ClearanceBelow_MeasuresFromTheDuctBottom()
        {
            // Crossing from 96.8 to 97.1; the duct bottom at 20 is the top (98) less the pipe's 0.5 m.
            ViewDimPlan plan = Plan(Scene(crossBottom: 96.8));
            PlacedDim c = Assert.Single(plan.Dims, d => d.Dim.Kind == DimKind.Clearance);
            Assert.Equal(DimReasons.CrossingBelow, c.Dim.Reason);
            Assert.Equal(97.5 - 97.1, c.Dim.ValueM, 6);
        }

        [Fact]
        public void CrossingNotDrawn_IsSkippedUnlessAsked()
        {
            Assert.DoesNotContain(Plan(Scene(shown: false)).Dims, d => d.Dim.Kind == DimKind.Clearance);
            Assert.Contains(Plan(Scene(shown: false), new ProfileDimOptions { CrossingsShownOnly = false }).Dims, d => d.Dim.Kind == DimKind.Clearance);
        }

        [Fact]
        public void NoTopProfile_UsesThePipeTops()
        {
            ViewDimPlan plan = Plan(Scene(withTop: false));
            Assert.Equal("", plan.TopProfile);
            Assert.Contains(plan.Issues, i => i.Code == ProfileDimCodes.TopFromPipes);
            Assert.Contains(plan.Dims, d => d.Dim.Kind == DimKind.Cover && d.Dim.StationM == 48 && System.Math.Abs(d.Dim.ValueM - 2.4) < 1e-6);
        }

        [Fact]
        public void NoTopAndNoPipes_IsAnError()
        {
            ViewDimPlan plan = Plan(Scene(withTop: false, ownPipes: false));
            Assert.Empty(plan.Dims);
            Assert.Contains(plan.Issues, i => i.Code == ProfileDimCodes.NoTop && i.Severity == IssueSeverity.Error);
        }

        [Fact]
        public void NoDuctPipes_SkipsClearanceButKeepsCover()
        {
            ViewDimPlan plan = Plan(Scene(ownPipes: false));
            Assert.Contains(plan.Issues, i => i.Code == ProfileDimCodes.NoDuct);
            Assert.DoesNotContain(plan.Dims, d => d.Dim.Kind == DimKind.Clearance);
            Assert.Contains(plan.Dims, d => d.Dim.Kind == DimKind.Cover);
        }

        [Fact]
        public void CoverOff_LeavesOnlyClearanceAndNoCoverIssues()
        {
            ViewDimPlan plan = Plan(Scene(), new ProfileDimOptions { Cover = false });
            Assert.All(plan.Dims, d => Assert.Equal(DimKind.Clearance, d.Dim.Kind));
            Assert.DoesNotContain(plan.Issues, i => i.Code.StartsWith("dims.cover"));
        }

        [Fact]
        public void ViewRange_ClipsTheDuct()
        {
            SheetScene scene = Scene();
            scene = scene with { ProfileViews = new[] { scene.ProfileViews[0] with { StartStationM = 30, EndStationM = 70 } } };
            ViewDimPlan plan = Plan(scene);
            Assert.All(plan.Dims, d => Assert.InRange(d.Dim.StationM, 30, 70));
            Assert.DoesNotContain(plan.Dims, d => d.Dim.Kind == DimKind.Clearance);   // the crossing at 20 is outside
        }

        [Fact]
        public void Side_FlipsBackAtTheEndOfTheDuct()
        {
            PlannedDim d = new PlannedDim(DimKind.Cover, 99, 97, 100, 3, "", DimReasons.GradeBreak);
            Assert.Equal(-1, ProfileDimPlanner.SideOf(d, new StructureSpan[0], 0, 100, 2));
            Assert.Equal(1, ProfileDimPlanner.SideOf(d with { StationM = 50 }, new StructureSpan[0], 0, 100, 2));
        }
    }
}
