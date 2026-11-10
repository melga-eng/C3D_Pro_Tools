using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.Engines;

namespace C3DProTools.Tests.Sheets
{
    public class ContinuationNotesTests
    {
        private static Point2[] Polygon(params double[] xy) => Enumerable.Range(0, xy.Length / 2)
            .Select(i => new Point2(xy[2 * i], xy[2 * i + 1])).ToArray();
        private static SheetArea Rect(string key, double x0, double y0, double x1, double y1, string number = null) =>
            new(key, number ?? key, Polygon(x0, y0, x1, y0, x1, y1, x0, y1));
        private static SheetArea Own => Rect("own", 0, 0, 10, 10);
        private static ContinuationRules Exact => new(0, 1);
        private static ContinuationPlan Resolve(SheetArea other, ContinuationRules rules = null) =>
            ContinuationNotes.Resolve(new[] { Own, other }, "own", rules ?? Exact);
        private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-8);
        private static void Point(double x, double y, Point2 point) { Near(x, point.X); Near(y, point.Y); }
        private static void Issue(ContinuationPlan plan, string code, IssueSeverity severity)
        {
            OperationIssue issue = Assert.Single(plan.Issues);
            Assert.Equal(code, issue.Code); Assert.Equal(severity, issue.Severity);
        }

        [Fact]
        public void TouchingSheets_ReturnTheSharedEdge_InOwnVertexOrder()
        {
            var plan = Resolve(Rect("next", 10, 0, 20, 10, "D-102055"));
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges);
            Assert.Equal("next", edge.NeighbourKey); Assert.Equal("D-102055", edge.NeighbourDrawingNumber);
            Near(10, edge.SharedLengthM); Point(10, 5, edge.MidpointM);
            Assert.Equal(2, edge.PathM.Count); Point(10, 0, edge.PathM[0]); Point(10, 10, edge.PathM[1]);
        }

        [Theory]
        [InlineData(0, 10.10)]
        [InlineData(0.04, 10.02)]
        [InlineData(-0.02, 10.14)]
        public void ToleranceIncludesBoundaryDistance_AndSmallOverlap(double gap, double expectedLength)
        {
            var plan = Resolve(Rect("next", 10 + gap, 0, 20, 10), new ContinuationRules());
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges);
            Near(expectedLength, edge.SharedLengthM); Point(10, 5, edge.MidpointM);
            // The small portions near the corners also lie within the boundary-distance tolerance.
            Assert.Equal(4, edge.PathM.Count);
        }

        [Fact]
        public void OutsideTolerance_IsNotANeighbour()
        {
            var plan = Resolve(Rect("next", 10.06, 0, 20, 10), new ContinuationRules());
            Assert.Empty(plan.Edges); Assert.Empty(plan.Issues);
        }

        [Fact]
        public void SlightOverlap_WithZeroTolerance_UsesTheInsidePortions()
        {
            var edge = Assert.Single(Resolve(Rect("next", 9.98, 0, 20, 10)).Edges);
            Near(10.04, edge.SharedLengthM);
            Point(9.98, 0, edge.PathM[0]); Point(9.98, 10, edge.PathM[^1]);
        }

        private static SheetArea TwoContacts(double secondStart = 7, double secondEnd = 9) => new("next", "N",
            Polygon(10, 1, 14, 1, 14, secondEnd, 10, secondEnd, 10, secondStart, 12, secondStart, 12, 3, 10, 3));

        [Fact]
        public void SameNeighbour_CanHaveTwoSeparateContacts()
        {
            var plan = Resolve(TwoContacts());
            Assert.Empty(plan.Issues); Assert.Equal(2, plan.Edges.Count);
            Assert.All(plan.Edges, edge => { Assert.Equal("next", edge.NeighbourKey); Near(2, edge.SharedLengthM); });
            Point(10, 2, plan.Edges[0].MidpointM); Point(10, 8, plan.Edges[1].MidpointM);
        }

        [Fact]
        public void ContactsSeparatedByLessThanToleranceAlongBoundary_AreJoined()
        {
            var plan = Resolve(TwoContacts(3.12, 5.12), new ContinuationRules());
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges);
            Near(4.22, edge.SharedLengthM); Point(10, 3.06, edge.MidpointM);
        }

        [Fact]
        public void CornerOnlyContact_GivesNoContinuationNote()
        {
            foreach (var rules in new[] { Exact, new ContinuationRules() })
            {
                var plan = Resolve(Rect("corner", 10, 10, 20, 20), rules);
                Assert.Empty(plan.Edges); Assert.Empty(plan.Issues);
            }
        }

        [Fact]
        public void ContactsAcrossClosingVertex_AreOnePath()
        {
            var own = new SheetArea("own", "own", Polygon(10, 5, 10, 10, 0, 10, 0, 0, 10, 0));
            var plan = ContinuationNotes.Resolve(new[] { own, Rect("next", 10, 0, 20, 10) }, "own", Exact);
            var edge = Assert.Single(plan.Edges);
            Near(10, edge.SharedLengthM); Point(10, 5, edge.MidpointM);
            Assert.Equal(3, edge.PathM.Count); Point(10, 0, edge.PathM[0]); Point(10, 10, edge.PathM[^1]);
        }

        [Fact]
        public void RotatedOutlines_WorkAtLargeSurveyCoordinates()
        {
            double angle = 0.73, cos = Math.Cos(angle), sin = Math.Sin(angle);
            Point2 Rotate(Point2 p) => new(673000 + p.X * cos - p.Y * sin, 2761000 + p.X * sin + p.Y * cos);
            SheetArea Transform(SheetArea s) => s with { ModelBoundary = s.ModelBoundary.Select(Rotate).ToArray() };
            var plan = ContinuationNotes.Resolve(new[] { Transform(Own), Transform(Rect("next", 10, 0, 20, 10)) }, "own", Exact);
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges); Near(10, edge.SharedLengthM);
            Point2 expected = Rotate(new Point2(10, 5)); Point(expected.X, expected.Y, edge.MidpointM);
        }

        [Fact]
        public void NonRectangularOutlines_ShareAnObliqueEdge()
        {
            var own = new SheetArea("own", "own", Polygon(0, 0, 10, 0, 0, 10));
            var next = new SheetArea("next", "N", Polygon(10, 0, 10, 10, 0, 10));
            var plan = ContinuationNotes.Resolve(new[] { own, next }, "own", Exact);
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges); Near(Math.Sqrt(200), edge.SharedLengthM); Point(5, 5, edge.MidpointM);
        }

        [Fact]
        public void ConcaveOutline_ProducesBentPath_AndArcLengthMidpoint()
        {
            var own = new SheetArea("own", "own", Polygon(0, 0, 10, 0, 10, 4, 4, 4, 4, 10, 0, 10));
            var plan = ContinuationNotes.Resolve(new[] { own, Rect("next", 4, 4, 10, 10) }, "own", Exact);
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges); Near(12, edge.SharedLengthM); Point(4, 4, edge.MidpointM);
            Assert.Equal(3, edge.PathM.Count);
        }

        [Fact]
        public void ContainingNeighbour_CoversTheWholeClosedBoundary()
        {
            var edge = Assert.Single(Resolve(Rect("outer", -1, -1, 11, 11)).Edges);
            Near(40, edge.SharedLengthM); Assert.Equal(5, edge.PathM.Count);
            Assert.Equal(edge.PathM[0], edge.PathM[^1]); Point(10, 10, edge.MidpointM);
            Assert.Empty(Resolve(Rect("inner", 2, 2, 8, 8)).Edges);
        }

        [Fact]
        public void ExplicitClosure_AndClockwiseVertexOrder_AreAccepted()
        {
            var own = Own with { ModelBoundary = Own.ModelBoundary.Reverse().Concat(new[] { Own.ModelBoundary[^1] }).ToArray() };
            var plan = ContinuationNotes.Resolve(new[] { own, Rect("next", 10, 0, 20, 10) }, "own", Exact);
            Assert.Empty(plan.Issues);
            var edge = Assert.Single(plan.Edges); Point(10, 10, edge.PathM[0]); Point(10, 0, edge.PathM[^1]);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(0.999, 0)]
        public void MinimumLengthDropsShortContacts(double length, int count)
        {
            var plan = Resolve(Rect("next", 10, 2, 12, 2 + length));
            Assert.Equal(count, plan.Edges.Count); Assert.Empty(plan.Issues);
        }

        [Fact]
        public void ResultsUseOrdinalDrawingNumber_ThenBoundaryDistance()
        {
            var plan = ContinuationNotes.Resolve(new[] { Own, TwoContacts() with { DrawingNumber = "2" },
                Rect("left", -10, 0, 0, 10, "10"), Rect("bottom", 0, -10, 10, 0, "A") }, "own", Exact);
            Assert.Empty(plan.Issues);
            Assert.Equal(new[] { "10", "2", "2", "A" }, plan.Edges.Select(e => e.NeighbourDrawingNumber));
            Point(10, 2, plan.Edges[1].MidpointM); Point(10, 8, plan.Edges[2].MidpointM);
        }

        public static IEnumerable<object[]> InvalidBoundaries()
        {
            yield return new object[] { Polygon(0, 0, 1, 0) }; // too few distinct points
            yield return new object[] { Polygon(0, 0, 1, 0, 2, 0) }; // zero area
            yield return new object[] { Polygon(0, 0, 4, 4, 0, 4, 3, 0) }; // crossing, nonzero signed area
            yield return new object[] { Polygon(0, 0, double.NaN, 0, 1, 1) };
            yield return new object[] { Polygon(0, 0, 1, double.PositiveInfinity, 1, 1) };
            yield return new object[] { Polygon(0, 0, 4, 0, 2, 0, 4, 4, 0, 4) }; // adjacent edges double back
            yield return new object[] { Polygon(0, 0, 4, 0, 4, 4, 0, 4, 4, 0) }; // non-adjacent touching edges
        }
        [Theory]
        [MemberData(nameof(InvalidBoundaries))]
        public void InvalidSelectedBoundary_IsAnError_WhileInvalidNeighbourIsAWarning(object boundary)
        {
            var points = (Point2[])boundary;
            var invalidOwn = Own with { ModelBoundary = points };
            var plan = ContinuationNotes.Resolve(new[] { invalidOwn, Rect("next", 10, 0, 20, 10) }, "own", Exact);
            Assert.Empty(plan.Edges); Issue(plan, ContinuationCodes.BoundaryInvalid, IssueSeverity.Error);
            plan = Resolve(new SheetArea("bad", "bad", points));
            Assert.Empty(plan.Edges); Issue(plan, ContinuationCodes.BoundaryInvalid, IssueSeverity.Warning);
        }

        [Fact]
        public void InvalidNeighbourDoesNotHideValidContacts()
        {
            var plan = ContinuationNotes.Resolve(new[] { Own, Rect("good", 10, 0, 20, 10), new SheetArea("bad", "bad", null) }, "own", Exact);
            Assert.Equal("good", Assert.Single(plan.Edges).NeighbourKey);
            Issue(plan, ContinuationCodes.BoundaryInvalid, IssueSeverity.Warning);
        }

        [Fact]
        public void DuplicateKeysSkipLaterOccurrences_BeforeValidation()
        {
            var plan = ContinuationNotes.Resolve(new[] { Own, Rect("next", 10, 0, 20, 10), new SheetArea("next", "bad", null),
                Own with { ModelBoundary = Array.Empty<Point2>() } }, "own", Exact);
            Assert.Equal("next", Assert.Single(plan.Edges).NeighbourKey);
            Assert.Equal(new[] { ContinuationCodes.KeyDuplicate, ContinuationCodes.KeyDuplicate }, plan.Issues.Select(i => i.Code));
            Assert.All(plan.Issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        }

        [Fact]
        public void MissingSelectedSheet_IsAnError()
        {
            var plan = ContinuationNotes.Resolve(new[] { Own }, "missing", Exact);
            Assert.Empty(plan.Edges); Issue(plan, ContinuationCodes.SheetNotFound, IssueSeverity.Error);
            plan = ContinuationNotes.Resolve(null, "own", Exact);
            Assert.Empty(plan.Edges); Issue(plan, ContinuationCodes.SheetNotFound, IssueSeverity.Error);
        }

        [Theory]
        [InlineData(-0.01, 1)]
        [InlineData(double.NaN, 1)]
        [InlineData(double.PositiveInfinity, 1)]
        [InlineData(0.05, 0)]
        [InlineData(0.05, -1)]
        [InlineData(0.05, double.NaN)]
        [InlineData(0.05, double.PositiveInfinity)]
        public void InvalidRules_ReturnAnEmptyPlan(double tolerance, double minimum)
        {
            var plan = Resolve(Rect("next", 10, 0, 20, 10), new ContinuationRules(tolerance, minimum));
            Assert.Empty(plan.Edges); Issue(plan, ContinuationCodes.RulesInvalid, IssueSeverity.Error);
        }

        [Fact]
        public void EveryIssueCodeHasARepresentativeOutcome()
        {
            var plans = new[] { ContinuationNotes.Resolve(new[] { Own }, "own", null),
                ContinuationNotes.Resolve(new[] { Own }, "missing", Exact),
                Resolve(new SheetArea("bad", "bad", null)),
                ContinuationNotes.Resolve(new[] { Own, Own }, "own", Exact) };
            string[] defined = typeof(ContinuationCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (string)f.GetRawConstantValue()).OrderBy(s => s).ToArray();
            Assert.Equal(defined, plans.SelectMany(p => p.Issues).Select(i => i.Code).Distinct().OrderBy(s => s).ToArray());
        }

        [Fact]
        public void RandomRectanglesMatchIndependentBoundaryLengths_AndPlansAreDeterministic()
        {
            Random random = new(102054);
            for (int i = 0; i < 120; i++)
            {
                double x0 = random.Next(-5, 15), y0 = random.Next(-5, 15);
                double x1 = x0 + random.Next(1, 15), y1 = y0 + random.Next(1, 15);
                var neighbour = Rect("next", x0, y0, x1, y1);
                var sheets = new[] { Own, neighbour };
                string before = JsonSerializer.Serialize(sheets);
                var rules = new ContinuationRules(0, 1e-5);
                var plan = ContinuationNotes.Resolve(sheets, "own", rules);
                Assert.Empty(plan.Issues);
                double width = Math.Max(0, Math.Min(10, x1) - Math.Max(0, x0));
                double height = Math.Max(0, Math.Min(10, y1) - Math.Max(0, y0));
                double expected = (y0 <= 0 && y1 >= 0 ? width : 0) + (y0 <= 10 && y1 >= 10 ? width : 0)
                    + (x0 <= 0 && x1 >= 0 ? height : 0) + (x0 <= 10 && x1 >= 10 ? height : 0);
                Near(expected, plan.Edges.Sum(e => e.SharedLengthM));
                foreach (var edge in plan.Edges)
                {
                    Near(edge.SharedLengthM, edge.PathM.Zip(edge.PathM.Skip(1), (a, b) => a.DistanceTo(b)).Sum());
                    Assert.True(Math.Abs(edge.MidpointM.X) < 1e-8 || Math.Abs(edge.MidpointM.X - 10) < 1e-8
                        || Math.Abs(edge.MidpointM.Y) < 1e-8 || Math.Abs(edge.MidpointM.Y - 10) < 1e-8);
                }
                Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(ContinuationNotes.Resolve(sheets, "own", rules)));
                Assert.Equal(before, JsonSerializer.Serialize(sheets));
            }
        }
    }
}
