using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.Engines;

namespace C3DProTools.Tests.Sheets
{
    public class DimStationsTests
    {
        private static StationElevation[] Line(params double[] xy) => Enumerable.Range(0, xy.Length / 2)
            .Select(i => new StationElevation(xy[2 * i], xy[2 * i + 1])).ToArray();
        private static DimInput Input(double from = 0, double to = 100) => new()
        {
            DuctTop = Line(from, 10, to, 10), DuctBottom = Line(from, 9, to, 9),
            ProposedSurface = new[] { Line(from, 20, to, 20) }, OwnedFromM = from, OwnedToM = to,
        };
        private static DimInput WithTop(params double[] xy)
        {
            var top = Line(xy);
            return Input(top[0].StationM, top[^1].StationM) with
            { DuctTop = top, DuctBottom = top.Select(p => p with { ElevationM = p.ElevationM - 1 }).ToArray() };
        }
        // One face in the view, with the other face beyond its end.
        private static StructureSpan[] Faces(params double[] stations) => stations.Select((s, i) => new StructureSpan($"s{i}", s, 10000)).ToArray();
        private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-9);
        private static void Issue(DimPlan plan, string code, IssueSeverity severity)
        {
            OperationIssue issue = Assert.Single(plan.Issues);
            Assert.Equal(code, issue.Code);
            Assert.Equal(severity, issue.Severity);
        }
        private static void Failure(DimInput input, string code)
        {
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Dims);
            Issue(plan, code, IssueSeverity.Error);
        }

        [Fact]
        public void DefaultsAreTwoPercentAndTenMetres()
        {
            var rules = new DimRules();
            Assert.Equal(2, rules.MajorGradeChangePct);
            Assert.Equal(10, rules.MinSpacingM);
            var input = WithTop(0, 10, 20, 10, 40, 10.4);
            var dim = Assert.Single(DimStations.Plan(input, rules).Dims);
            Near(20, dim.StationM); // Exactly 2% is major.
            Assert.Equal(DimReasons.GradeBreak, dim.Reason);
            Assert.Equal("", dim.SourceKey);
        }

        [Fact]
        public void SpanClustering_ZeroFourEightDoesNotChain()
        {
            var input = WithTop(-10, 10, 0, 10, 4, 10.12, 8, 10.4, 20, 11.6);
            var plan = DimStations.Plan(input, new DimRules { MinSpacingM = 0 });
            Assert.Empty(plan.Issues);
            Assert.Equal(new[] { 4d, 8d }, plan.Dims.Select(d => d.StationM));
        }

        [Fact]
        public void ClusterRepresentative_IsLargestSingleChange_AndLowerStationOnTie()
        {
            var input = WithTop(-10, 10, 0, 10, 4, 10.12, 8, 10.36, 20, 11.44);
            var plan = DimStations.Plan(input, new DimRules { MinSpacingM = 0 });
            Near(0, plan.Dims[0].StationM); // Changes at 0 and 4 are both 3%.
            Assert.Equal(new[] { 0d, 8d }, plan.Dims.Select(d => d.StationM));
        }

        [Fact]
        public void ClusterUsesOuterGrades_NotLargestSingleChange()
        {
            var input = WithTop(0, 10, 10, 10, 14, 10.2, 30, 10.2); // +5%, then -5%, in one cluster.
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Dims);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void StructureFacesIncludeViewEndpoints_AndCanBeDisabled()
        {
            var input = Input() with { Structures = new[] { new StructureSpan("mh", 0, 100) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Issues);
            Assert.Equal(new[] { 0d, 100d }, plan.Dims.Select(d => d.StationM));
            Assert.All(plan.Dims, d => { Assert.Equal("mh", d.SourceKey); Assert.Equal(DimReasons.StructureFace, d.Reason); Near(10, d.ValueM); });
            Assert.Empty(DimStations.Plan(input, new DimRules { AtStructureFaces = false }).Dims);
        }

        [Fact]
        public void NearEndpointFacesAreClampedWithinTheExplicitTolerance()
        {
            var input = Input() with { Structures = new[] { new StructureSpan("mh", -5e-7, 100 + 5e-7) } };
            Assert.Equal(new[] { 0d, 100d }, DimStations.Plan(input, new DimRules()).Dims.Select(d => d.StationM));
        }

        [Fact]
        public void SplitOwnership_IsHalfOpen_AndEarlierPieceOwnsOverlap()
        {
            var crossings = new[] { new CrossingPipe("a", 90, 12, 11), new CrossingPipe("b", 100, 12, 11) };
            var first = Input(0, 100) with { OwnedToM = 100, OwnsEnd = false, Structures = Faces(90, 100), Crossings = crossings };
            var last = Input(90, 150) with { OwnedFromM = 100, OwnsEnd = true, Structures = Faces(90, 100), Crossings = crossings };
            var a = DimStations.Plan(first, new DimRules());
            var b = DimStations.Plan(last, new DimRules());
            Assert.Empty(a.Issues); Assert.Empty(b.Issues);
            Assert.Equal(2, a.Dims.Count); Assert.All(a.Dims, d => Near(90, d.StationM));
            Assert.Equal(2, b.Dims.Count); Assert.All(b.Dims, d => Near(100, d.StationM));
            Assert.Equal(4, a.Dims.Concat(b.Dims).Count());
        }

        [Fact]
        public void NonOwnedCandidatesDoNotSuppressOrWarnAboutOwnedDimensions()
        {
            var input = Input() with { OwnedFromM = 50, Structures = Faces(49, 51),
                Crossings = new[] { new CrossingPipe("unownedClash", 49, 10, 9) } };
            var plan = DimStations.Plan(input, new DimRules());
            Near(51, Assert.Single(plan.Dims).StationM);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void SpacingPrefersFaces_ThenLargerGradeChange_ThenLowerStation()
        {
            var input = WithTop(0, 10, 20, 10, 27, 10.21, 34, 10.7, 60, 11.74);
            var plan = DimStations.Plan(input, new DimRules { MergeWithinM = 0, MinSpacingM = 10 });
            Assert.Equal(new[] { 27d }, plan.Dims.Select(d => d.StationM)); // 4% beats 3% at 20 and 34.
            Assert.Equal(2, plan.Issues.Count);
            Assert.All(plan.Issues, i => { Assert.Equal(DimCodes.CoverSpacing, i.Code); Assert.Equal(IssueSeverity.Info, i.Severity); });
            plan = DimStations.Plan(input with { Structures = Faces(21) }, new DimRules { MergeWithinM = 0, MinSpacingM = 10 });
            Assert.Equal(new[] { 21d, 34d }, plan.Dims.Select(d => d.StationM)); // Face displaces larger break at 27.
            Assert.Equal(DimReasons.StructureFace, plan.Dims[0].Reason);
            plan = DimStations.Plan(Input() with { Structures = Faces(25, 20, 30) }, new DimRules());
            Assert.Equal(new[] { 20d, 30d }, plan.Dims.Select(d => d.StationM)); // Equal priority: lower first; exactly 10 allowed.
        }

        [Theory]
        [InlineData(12, 11, 10, 11, "crossingAbove")]
        [InlineData(8, 7, 8, 9, "crossingBelow")]
        [InlineData(11, 10, 10, 10, "crossingAbove")]
        [InlineData(9, 8, 9, 9, "crossingBelow")]
        public void ClearanceAboveAndBelow_UsesOutsideLevels(double crossingTop, double crossingBottom, double lower, double upper, string reason)
        {
            var input = Input() with { Crossings = new[] { new CrossingPipe("x", 25, crossingTop, crossingBottom) } };
            var plan = DimStations.Plan(input, new DimRules { MinClearanceM = 0 });
            Assert.Empty(plan.Issues);
            var dim = Assert.Single(plan.Dims);
            Assert.Equal(DimKind.Clearance, dim.Kind); Assert.Equal(reason, dim.Reason); Assert.Equal("x", dim.SourceKey);
            Near(lower, dim.LowerElevM); Near(upper, dim.UpperElevM); Near(upper - lower, dim.ValueM);
        }

        [Theory]
        [InlineData(11, 9.5)]
        [InlineData(9.5, 8)]
        [InlineData(10.5, 8.5)]
        public void CrossingClash_HasNoDimension(double upper, double lower)
        {
            var plan = DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", 20, upper, lower) } }, new DimRules());
            Assert.Empty(plan.Dims); Issue(plan, DimCodes.ClearanceClash, IssueSeverity.Error);
        }

        [Theory]
        [InlineData(10.2, true)]
        [InlineData(10.3, false)]
        public void MinimumClearance_KeepsDimension_AndWarnsOnlyBelowThreshold(double bottom, bool warning)
        {
            var plan = DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", 20, 12, bottom) } }, new DimRules());
            Assert.Single(plan.Dims);
            if (warning) Issue(plan, DimCodes.ClearanceBelowMinimum, IssueSeverity.Warning); else Assert.Empty(plan.Issues);
        }

        [Fact]
        public void ClearancesDoNotParticipateInCoverSpacing_AndOutputSortsByKindThenKey()
        {
            var input = Input() with { Structures = Faces(20), Crossings = new[]
            { new CrossingPipe("z", 20, 12, 11), new CrossingPipe("a", 20, 12, 11), new CrossingPipe("b", 21, 8, 7) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Issues); Assert.Equal(4, plan.Dims.Count);
            Assert.Equal(DimKind.Cover, plan.Dims[0].Kind);
            Assert.Equal(new[] { "a", "z", "b" }, plan.Dims.Skip(1).Select(d => d.SourceKey));
        }

        [Fact]
        public void SurfaceInterpolationAndGaps_DoNotExtrapolateOrBridgeRuns()
        {
            var input = Input() with { Structures = Faces(5, 35, 70), ProposedSurface = new[] { Line(0, 12, 10, 14), Line(60, 16, 80, 18) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Equal(new[] { 5d, 70d }, plan.Dims.Select(d => d.StationM));
            Near(3, plan.Dims[0].ValueM); Near(7, plan.Dims[1].ValueM);
            Issue(plan, DimCodes.SurfaceMissing, IssueSeverity.Warning);
        }

        [Fact]
        public void EmptySurfaceIsMissingOnlyWhenACoverCandidateNeedsIt()
        {
            var input = Input() with { ProposedSurface = Array.Empty<IReadOnlyList<StationElevation>>() };
            Assert.Empty(DimStations.Plan(input, new DimRules()).Issues);
            var plan = DimStations.Plan(input with { Structures = Faces(20) }, new DimRules());
            Assert.Empty(plan.Dims); Issue(plan, DimCodes.SurfaceMissing, IssueSeverity.Warning);
        }

        [Theory]
        [InlineData(10)]
        [InlineData(9)]
        public void NonPositiveCoverIsAnError(double surface)
        {
            var input = Input() with { Structures = Faces(20), ProposedSurface = new[] { Line(0, surface, 100, surface) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Dims); Issue(plan, DimCodes.CoverNegative, IssueSeverity.Error);
        }

        [Fact]
        public void ConsistencyChecksVerticesOfBothLines_AndTheirRanges()
        {
            Failure(Input() with { DuctBottom = Line(1, 9, 100, 9) }, DimCodes.InputDuctInconsistent);
            Failure(Input() with { DuctBottom = Line(0, 9, 50, 11, 100, 9) }, DimCodes.InputDuctInconsistent);
            Failure(Input() with { DuctTop = Line(0, 10, 50, 8, 100, 10) }, DimCodes.InputDuctInconsistent);
            Failure(Input() with { DuctBottom = Line(0, 10, 100, 10) }, DimCodes.InputDuctInconsistent);
            var good = DimStations.Plan(Input() with { DuctBottom = Line(5e-7, 9, 100 - 5e-7, 9) }, new DimRules());
            Assert.Empty(good.Issues);
        }

        [Fact]
        public void SurfaceRunsMayTouchOnlyAtOneSharedElevation()
        {
            Failure(Input() with { ProposedSurface = new[] { Line(0, 20, 60, 20), Line(50, 20, 100, 20) } }, DimCodes.InputSurfaceOverlap);
            Failure(Input() with { ProposedSurface = new[] { Line(0, 20, 50, 20), Line(50, 21, 100, 20) } }, DimCodes.InputSurfaceOverlap);
            Failure(Input() with { ProposedSurface = new[] { Line(60, 20, 100, 20), Line(0, 20, 40, 20) } }, DimCodes.InputSurfaceOverlap);
            var plan = DimStations.Plan(Input() with { Structures = Faces(50), ProposedSurface = new[] { Line(0, 20, 50, 20), Line(50, 20, 100, 20) } }, new DimRules());
            Assert.Single(plan.Dims); Assert.Empty(plan.Issues);
        }

        [Fact]
        public void DuplicateVerticesMerge_ButVerticalStepsAreRejected()
        {
            var input = Input() with { DuctTop = Line(0, 10, 5e-7, 10 + 5e-7, 100, 10), Structures = Faces(20) };
            Assert.Single(DimStations.Plan(input, new DimRules()).Dims);
            Failure(input with { DuctTop = Line(0, 10, 0, 11, 100, 10) }, DimCodes.InputVerticalStep);
            Failure(input with { DuctBottom = Line(0, 9, 0, 8, 100, 9) }, DimCodes.InputVerticalStep);
            Failure(input with { ProposedSurface = new[] { Line(0, 20, 0, 21, 100, 20) } }, DimCodes.InputVerticalStep);
        }

        public static IEnumerable<object[]> BadRules()
        {
            yield return new object[] { null };
            yield return new object[] { new DimRules { MajorGradeChangePct = 0 } };
            yield return new object[] { new DimRules { MajorGradeChangePct = -1 } };
            yield return new object[] { new DimRules { MajorGradeChangePct = double.NaN } };
            yield return new object[] { new DimRules { MergeWithinM = -1 } };
            yield return new object[] { new DimRules { MergeWithinM = double.PositiveInfinity } };
            yield return new object[] { new DimRules { MinSpacingM = -1 } };
            yield return new object[] { new DimRules { MinSpacingM = double.NaN } };
            yield return new object[] { new DimRules { MinClearanceM = -1 } };
            yield return new object[] { new DimRules { MinClearanceM = double.PositiveInfinity } };
        }
        [Theory]
        [MemberData(nameof(BadRules))]
        public void InvalidRulesReturnAnEmptyPlan(object rules)
        {
            var plan = DimStations.Plan(Input(), (DimRules)rules);
            Assert.Empty(plan.Dims); Issue(plan, DimCodes.RulesInvalid, IssueSeverity.Error);
        }

        [Fact]
        public void InvalidInputReturnsAnEmptyPlan()
        {
            Failure(null, DimCodes.InputInvalid);
            Failure(Input() with { DuctTop = Line(0, 10) }, DimCodes.InputInvalid);
            Failure(Input() with { DuctBottom = Line(0, 9, 100, double.NaN) }, DimCodes.InputInvalid);
            Failure(Input() with { DuctTop = Line(0, 10, 50, 10, 40, 10, 100, 10) }, DimCodes.InputInvalid);
            Failure(Input() with { ProposedSurface = new[] { Line(0, 20) } }, DimCodes.InputInvalid);
            Failure(Input() with { OwnedFromM = double.NaN }, DimCodes.InputInvalid);
            Failure(Input() with { OwnedToM = 101 }, DimCodes.InputInvalid);
            Failure(Input() with { OwnedFromM = 100, OwnedToM = 100 }, DimCodes.InputInvalid);
            Failure(Input() with { Structures = new[] { new StructureSpan("x", 30, 20) } }, DimCodes.InputInvalid);
        }

        [Fact]
        public void InvalidAndOutsideCrossingsAreSkipped_WithoutLosingValidOnes()
        {
            var input = Input() with { Crossings = new[] { new CrossingPipe("nan", double.NaN, 12, 11),
                new CrossingPipe("inverted", 20, 11, 12), new CrossingPipe("outside", 101, 12, 11),
                new CrossingPipe("ok", 25, 12, 11) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Equal("ok", Assert.Single(plan.Dims).SourceKey);
            Assert.Equal(new[] { DimCodes.CrossingInvalid, DimCodes.CrossingInvalid, DimCodes.CrossingOutsideView }, plan.Issues.Select(i => i.Code));
            Assert.Equal(new[] { IssueSeverity.Error, IssueSeverity.Error, IssueSeverity.Info }, plan.Issues.Select(i => i.Severity));
        }

        [Fact]
        public void EveryIssueCodeHasARepresentativeOutcome()
        {
            var outcomes = new[]
            {
                DimStations.Plan(Input(), new DimRules { MinSpacingM = -1 }),
                DimStations.Plan(null, new DimRules()),
                DimStations.Plan(Input() with { DuctTop = Line(0, 10, 0, 11, 100, 10) }, new DimRules()),
                DimStations.Plan(Input() with { DuctBottom = Line(0, 11, 100, 11) }, new DimRules()),
                DimStations.Plan(Input() with { ProposedSurface = new[] { Line(0, 20, 60, 20), Line(50, 20, 100, 20) } }, new DimRules()),
                DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", 20, 10, 11) } }, new DimRules()),
                DimStations.Plan(Input() with { Structures = Faces(20), ProposedSurface = Array.Empty<IReadOnlyList<StationElevation>>() }, new DimRules()),
                DimStations.Plan(Input() with { Structures = Faces(20), ProposedSurface = new[] { Line(0, 9, 100, 9) } }, new DimRules()),
                DimStations.Plan(Input() with { Structures = Faces(20, 21) }, new DimRules()),
                DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", -1, 12, 11) } }, new DimRules()),
                DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", 20, 10, 9) } }, new DimRules()),
                DimStations.Plan(Input() with { Crossings = new[] { new CrossingPipe("x", 20, 11, 10.1) } }, new DimRules()),
            };
            string[] defined = typeof(DimCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()).OrderBy(s => s).ToArray();
            Assert.Equal(defined, outcomes.SelectMany(p => p.Issues).Select(i => i.Code).Distinct().OrderBy(s => s).ToArray());
        }

        [Fact]
        public void LastPieceOwnsItsEnd_ForCoverAndClearance()
        {
            var input = Input() with { Structures = Faces(100), Crossings = new[] { new CrossingPipe("end", 100, 12, 11) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Issues);
            Assert.Equal(2, plan.Dims.Count);
            Assert.All(plan.Dims, d => Near(100, d.StationM));
            Assert.Empty(DimStations.Plan(input with { OwnsEnd = false }, new DimRules()).Dims);
        }

        [Fact]
        public void ThresholdsUseTheContractsAbsoluteTolerance()
        {
            var input = Input() with { Structures = Faces(20, 30 - 5e-10),
                Crossings = new[] { new CrossingPipe("x", 20, 12, 10.3 - 5e-10) } };
            var plan = DimStations.Plan(input, new DimRules());
            Assert.Empty(plan.Issues);
            Assert.Equal(3, plan.Dims.Count);
            var almostMajor = WithTop(0, 10, 20, 10, 40, 10.4 - 1e-10);
            Assert.Single(DimStations.Plan(almostMajor, new DimRules()).Dims);
        }

        [Fact]
        public void UnrepresentableArithmeticReturnsAnInputError_InsteadOfInfiniteDimensions()
        {
            Failure(Input() with { DuctTop = Line(-1e308, 10, 1e308, 10) }, DimCodes.InputInvalid);
            var input = Input() with { DuctTop = Line(0, -1e308, 100, -1e308),
                DuctBottom = Line(0, -1.1e308, 100, -1.1e308),
                ProposedSurface = new[] { Line(0, 1e308, 100, 1e308) }, Structures = Faces(20) };
            Failure(input, DimCodes.InputInvalid);
        }
        [Fact]
        public void PlanIsDeterministic_AndDoesNotMutateTheInput()
        {
            var input = WithTop(-10, 10, 0, 10, 4, 10.12, 8, 10.4, 20, 11.6) with
            { Structures = Faces(0), Crossings = new[] { new CrossingPipe("x", 12, 15, 14) } };
            string before = JsonSerializer.Serialize(input);
            string first = JsonSerializer.Serialize(DimStations.Plan(input, new DimRules()));
            Assert.Equal(first, JsonSerializer.Serialize(DimStations.Plan(input, new DimRules())));
            Assert.Equal(before, JsonSerializer.Serialize(input));
        }
    }
}
