using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.Engines;

namespace C3DProTools.Tests.Sheets
{
    public class PlacementScoringTests
    {
        private static Point2[] Polygon(params double[] xy) => Enumerable.Range(0, xy.Length / 2)
            .Select(i => new Point2(xy[2 * i], xy[2 * i + 1])).ToArray();
        private static Point2[] Viewport => Polygon(0, 0, 100, 0, 100, 100, 0, 100);
        private static ScoredLabel Label(string key, Rect2 box, Point2? start = null, Point2? end = null) =>
            new(key, start ?? new Point2(0, 0), null, null, true, box, start, end);
        private static PlacementInput Input(IEnumerable<ScoredLabel> labels, params ScoredObstacle[] obstacles) =>
            new(Viewport, 0, obstacles, labels.ToArray());
        private static ScoredObstacle Obstacle(ScoredObstacleGroup group, params double[] xy) => new(group, null, Polygon(xy));
        private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-9);
        private static PlacementScore Score(params ScoredLabel[] labels) => PlacementScoring.Score(Input(labels));

        [Fact]
        public void EmptyAndUnplacedOnlyInputsHaveZeroGeometryMetrics()
        {
            var empty = PlacementScoring.Score(new PlacementInput(null, 0, null, null));
            Assert.Equal(new PlacementScore(0, 0, 0, 0, 0, 0, 0), empty);
            var label = Label("a", new Rect2(-10, -10, -5, -5), new Point2(0, 0), new Point2(100, 100)) with { Placed = false };
            Assert.Equal(new PlacementScore(1, 0, 0, 0, 0, 0, 0), Score(label));
        }

        [Fact]
        public void LabelPairsAreCountedOnce_AndOnlyWhenPlaced()
        {
            var a = Label("a", new Rect2(10, 10, 20, 20));
            var b = Label("b", new Rect2(15, 15, 25, 25));
            Assert.Equal(1, Score(a, b).Overlaps);
            Assert.Equal(0, Score(a, b with { Placed = false }).Overlaps);
            Assert.Equal(3, Score(a, b, a with { Key = "c" }).Overlaps);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0.0000005, 0)]
        [InlineData(0.000001, 0)]
        [InlineData(0.000002, 1)]
        public void OverlapAreaThresholdIsStrict_AndTouchingDoesNotCount(double width, int expected)
        {
            var a = Label("a", new Rect2(0, 10, 1, 11));
            var b = Label("b", new Rect2(1 - width, 10, 2, 11));
            // Use a shared strip starting at zero for an exact representation of the threshold itself.
            a = a with { BoxMm = new Rect2(0, 10, width, 11) };
            b = b with { BoxMm = new Rect2(0, 10, 1, 11) };
            Assert.Equal(expected, Score(a, b).Overlaps);
            var obstacle = Obstacle(ScoredObstacleGroup.Text, 0, 10, 1, 10, 1, 11, 0, 11);
            Assert.Equal(expected, PlacementScoring.Score(Input(new[] { a }, obstacle)).Overlaps);
        }

        [Fact]
        public void TouchingBoxesAndNearbyBoxesDoNotAcquireThePlannerGap()
        {
            var a = Label("a", new Rect2(10, 10, 20, 20));
            Assert.Equal(0, Score(a, Label("b", new Rect2(20, 10, 30, 20))).Overlaps);
            Assert.Equal(0, Score(a, Label("b", new Rect2(20.01, 10, 30, 20))).Overlaps);
        }

        [Fact]
        public void ObstaclePolygonsUseActualClippedArea_NotTheirBoundingBoxes()
        {
            var label = Label("a", new Rect2(10, 10, 20, 20));
            var triangle = Obstacle(ScoredObstacleGroup.Text, 19, 21, 21, 19, 25, 25);
            Assert.Equal(0, PlacementScoring.Score(Input(new[] { label }, triangle)).Overlaps);
            var inside = Obstacle(ScoredObstacleGroup.Text, 12, 12, 18, 12, 15, 18);
            Assert.Equal(1, PlacementScoring.Score(Input(new[] { label }, inside)).Overlaps);
            Assert.Equal(1, PlacementScoring.Score(Input(new[] { label }, inside with { OutlineMm = inside.OutlineMm.Reverse().ToArray() })).Overlaps);
        }

        [Theory]
        [InlineData(15, 0, 15, 30, 1)]
        [InlineData(10, 0, 10, 30, 0)]
        [InlineData(0, 0, 10, 10, 0)]
        [InlineData(12, 12, 18, 18, 1)]
        public void SegmentObstaclesCountInteriorCrossings_ButNotBoundaryTouches(double ax, double ay, double bx, double by, int expected)
        {
            var plan = PlacementScoring.Score(Input(new[] { Label("a", new Rect2(10, 10, 20, 20)) },
                Obstacle(ScoredObstacleGroup.Leader, ax, ay, bx, by)));
            Assert.Equal(expected, plan.Overlaps);
        }

        [Fact]
        public void NetworkObstaclesRespectKeepClearOf_WhileTextAndLeadersAlwaysCount()
        {
            var label = Label("a", new Rect2(10, 10, 20, 20)) with { KeepClearOf = new HashSet<string> { "ICT" } };
            var polygon = Polygon(12, 12, 18, 12, 18, 18, 12, 18);
            var obstacles = new[] { new ScoredObstacle(ScoredObstacleGroup.Network, "ict", polygon),
                new ScoredObstacle(ScoredObstacleGroup.Network, "LV", polygon),
                new ScoredObstacle(ScoredObstacleGroup.Network, null, polygon),
                new ScoredObstacle(ScoredObstacleGroup.Text, null, polygon),
                new ScoredObstacle(ScoredObstacleGroup.Leader, null, polygon) };
            Assert.Equal(3, PlacementScoring.Score(Input(new[] { label }, obstacles)).Overlaps);
        }

        [Fact]
        public void OutsideAreaUsesViewportMargin_AndCountsEachLabelOnce()
        {
            var good = Label("good", new Rect2(3, 3, 10, 10));
            var bad = Label("bad", new Rect2(2.9, 3, 10, 10));
            var outside = Label("outside", new Rect2(-10, -10, 110, 110)) with { RegionMm = new Rect2(0, 0, 1, 1) };
            var input = Input(new[] { good, bad, outside }) with { MarginMm = 3 };
            Assert.Equal(2, PlacementScoring.Score(input).OutsideArea);
        }

        [Fact]
        public void WrongProfileRegionCountsEvenWhenTheLabelIsInsideTheViewport()
        {
            var label = Label("row1", new Rect2(20, 60, 30, 65)) with { RegionMm = new Rect2(10, 10, 90, 40) };
            Assert.Equal(1, Score(label).OutsideArea);
            Assert.Equal(0, Score(label with { BoxMm = new Rect2(20, 35, 30, 40) }).OutsideArea);
        }

        [Theory]
        [InlineData(0, 10, 10, 0, 1)] // proper crossing
        [InlineData(10, 10, 20, 0, 0)] // shared endpoint of both
        [InlineData(5, 5, 5, 10, 1)] // T junction: endpoint of only one
        [InlineData(2, 2, 8, 8, 1)] // collinear shared interior
        [InlineData(10, 10, 20, 20, 0)] // collinear shared endpoint only
        [InlineData(11, 11, 20, 20, 0)] // disjoint collinear
        [InlineData(1, 2, 9, 10, 0)] // parallel
        public void LeaderCrossingsExcludeOnlyContactsThatAreEndpointsOfBoth(double cx, double cy, double dx, double dy, int expected)
        {
            var a = Label("a", new Rect2(70, 70, 75, 75), new Point2(0, 0), new Point2(10, 10));
            var b = Label("b", new Rect2(80, 80, 85, 85), new Point2(cx, cy), new Point2(dx, dy));
            Assert.Equal(expected, Score(a, b).LeaderCrossings);
            Assert.Equal(0, Score(a, b with { Placed = false }).LeaderCrossings);
        }

        [Fact]
        public void LeaderOverTextCountsLeaders_NotEachObstacle_AndExcludesItsOwnBox()
        {
            var a = Label("a", new Rect2(80, 80, 90, 90), new Point2(0, 5), new Point2(30, 5));
            var b = Label("b", new Rect2(20, 3, 25, 7));
            var input = Input(new[] { a, b }, Obstacle(ScoredObstacleGroup.Text, 5, 3, 10, 3, 10, 7, 5, 7),
                Obstacle(ScoredObstacleGroup.Text, 12, 3, 15, 3, 15, 7, 12, 7));
            Assert.Equal(1, PlacementScoring.Score(input).LeaderOverText);
            Assert.Equal(1, Score(a, b).LeaderOverText);
            var own = a with { BoxMm = new Rect2(20, 3, 25, 7) };
            Assert.Equal(0, Score(own).LeaderOverText);
            var networkOnly = Input(new[] { a }, Obstacle(ScoredObstacleGroup.Network, 5, 3, 10, 3, 10, 7, 5, 7) with { Network = "ICT" });
            Assert.Equal(0, PlacementScoring.Score(networkOnly).LeaderOverText);
        }

        [Fact]
        public void LeaderLengthsUseEuclideanDistance_AndIgnoreUnplacedLabels()
        {
            var a = Label("a", new Rect2(10, 10, 20, 20), new Point2(0, 0), new Point2(3, 4));
            var b = Label("b", new Rect2(30, 10, 40, 20), new Point2(0, 0), new Point2(0, 10));
            var score = Score(a, b, b with { Key = "unplaced", Placed = false });
            Near(7.5, score.MeanLeaderMm); Near(10, score.MaxLeaderMm); Assert.Equal(1, score.Unplaced);
            Assert.Equal(0, Score(a with { LeaderStartMm = null, LeaderEndMm = null }).MeanLeaderMm);
        }

        private static ArrangeCase Case()
        {
            var c = new ArrangeCase { Margin = 3, Viewport = Viewport.Select(p => new[] { p.X, p.Y }).ToList() };
            c.Labels.Add(new ArrangeCase.Label { Key = "a", Anchor = new[] { 30d, 30d }, Right = new[] { 0d, -3d, 8d, 0d },
                Left = new[] { -8d, -3d, 0d, 0d }, Networks = new List<string> { "ICT" }, Region = new[] { 10d, 10d, 90d, 40d } });
            c.Labels.Add(new ArrangeCase.Label { Key = "b", Anchor = new[] { 60d, 30d }, Right = new[] { 0d, -3d, 8d, 0d },
                Left = new[] { -8d, -3d, 0d, 0d } });
            return c;
        }

        [Fact]
        public void FromPlannedMapsActualSlidingLeaderStart_RegionsByKey_AndUnplacedState()
        {
            var c = Case();
            var labels = c.BuildLabels();
            labels[0].Spot = new LabelSpot { Box = new LRect(20, 60, 30, 65), End = (20, 60), Anchor = (35, 30), Ratio = 0.7 };
            labels.Reverse(); // Region lookup must not depend on list position.
            var input = PlacementScoring.FromPlanned(c, labels);
            Assert.False(input.Labels[0].Placed); Assert.Null(input.Labels[0].BoxMm); Assert.Null(input.Labels[0].LeaderStartMm);
            var a = input.Labels[1];
            Assert.Equal(new Point2(30, 30), a.AnchorMm); Assert.Equal(new Point2(35, 30), a.LeaderStartMm);
            Assert.Equal(new Point2(20, 60), a.LeaderEndMm); Assert.Equal(new Rect2(10, 10, 90, 40), a.RegionMm);
            Assert.True(a.KeepClearOf.Contains("ict"));
            Assert.Equal(1, PlacementScoring.Score(input).OutsideArea);
            Assert.False(labels[1].InArea(labels[1].Spot.Value.Box));
        }

        [Fact]
        public void FromPlannedCopiesCaseGeometry_AndAllObstacleGroups()
        {
            var c = Case();
            foreach (string group in new[] { "Text", "Leader", "Network" })
                c.Obstacles.Add(new ArrangeCase.Obstacle { Group = group, Network = "ICT", Points = new() { new[] { 5d, 5d }, new[] { 10d, 10d } } });
            var input = PlacementScoring.FromPlanned(c, c.BuildLabels());
            Assert.Equal(3, input.Obstacles.Count); Assert.Equal(3, input.MarginMm);
            c.Viewport[0][0] = -99; c.Obstacles[0].Points[0][0] = -99; c.Labels[0].Region[0] = -99;
            Assert.Equal(new Point2(0, 0), input.ViewportMm[0]); Assert.Equal(new Point2(5, 5), input.Obstacles[0].OutlineMm[0]);
            Assert.Equal(10, input.Labels[0].RegionMm.Value.MinX);
        }

        [Fact]
        public void HandBuiltCaseScoresBeforeAndAfterTheExistingPlanner()
        {
            var c = Case();
            var labels = c.BuildLabels();
            foreach (var label in labels) label.Spot = new LabelSpot { Box = new LRect(40, 32, 48, 35), End = (40, 35) };
            var before = PlacementScoring.Score(PlacementScoring.FromPlanned(c, labels));
            Assert.Equal(1, before.Overlaps);
            LabelPlanner.Plan(c.BuildObstacles(), labels, c.Rules());
            var after = PlacementScoring.Score(PlacementScoring.FromPlanned(c, labels));
            Assert.Equal(0, after.Unplaced); Assert.Equal(0, after.Overlaps); Assert.Equal(0, after.OutsideArea);
        }

        [Theory]
        [InlineData(0, "Unplaced")]
        [InlineData(1, "Overlaps")]
        [InlineData(2, "OutsideArea")]
        [InlineData(3, "LeaderCrossings")]
        [InlineData(4, "LeaderOverText")]
        public void EachHigherCountIsWorse(int index, string name)
        {
            var baseline = new PlacementScore(0, 0, 0, 0, 0, 0, 0);
            var candidate = index switch { 0 => baseline with { Unplaced = 1 }, 1 => baseline with { Overlaps = 1 },
                2 => baseline with { OutsideArea = 1 }, 3 => baseline with { LeaderCrossings = 1 }, _ => baseline with { LeaderOverText = 1 } };
            var comparison = PlacementScoring.Compare(baseline, candidate);
            Assert.False(comparison.NoWorse); Assert.Equal(name, Assert.Single(comparison.Worse));
        }

        [Fact]
        public void MeanLeaderThresholdIsTenPercentPlusHalfAMillimetre_MaxIsNotAGate()
        {
            var baseline = new PlacementScore(1, 1, 1, 1, 1, 10, 10);
            Assert.True(PlacementScoring.Compare(baseline, baseline with { MeanLeaderMm = 11.5, MaxLeaderMm = 100 }).NoWorse);
            var worse = PlacementScoring.Compare(baseline, baseline with { MeanLeaderMm = 11.500001 });
            Assert.False(worse.NoWorse); Assert.Equal("MeanLeaderMm", Assert.Single(worse.Worse));
            Assert.True(PlacementScoring.Compare(baseline, new PlacementScore(0, 0, 0, 0, 0, 0, 0)).NoWorse);
        }

        [Fact]
        public void CompareListsAllWorseMetricsInStableOrder()
        {
            var result = PlacementScoring.Compare(new PlacementScore(0, 0, 0, 0, 0, 0, 0), new PlacementScore(1, 1, 1, 1, 1, 1, 10));
            Assert.Equal(new[] { "Unplaced", "Overlaps", "OutsideArea", "LeaderCrossings", "LeaderOverText", "MeanLeaderMm" }, result.Worse);
        }

        [Fact]
        public void MalformedScoringDataCannotProduceASuccessfulZeroScore()
        {
            Assert.Throws<ArgumentNullException>(() => PlacementScoring.Score(null));
            var label = Label("a", new Rect2(10, 10, 20, 20));
            Assert.Throws<ArgumentException>(() => Score(label with { BoxMm = null }));
            Assert.Throws<ArgumentException>(() => Score(label with { LeaderStartMm = new Point2(0, 0) }));
            Assert.Throws<ArgumentException>(() => PlacementScoring.Score(Input(new[] { label }) with { MarginMm = double.NaN }));
            Assert.Throws<ArgumentException>(() => PlacementScoring.FromPlanned(Case(), new[] { new PlanLabel { Key = "unknown" } }));
        }

        [Fact]
        public void InvalidComparisonMetricsCannotPassTheGate()
        {
            var valid = new PlacementScore(0, 0, 0, 0, 0, 0, 0);
            Assert.Throws<ArgumentException>(() => PlacementScoring.Compare(valid, valid with { MeanLeaderMm = double.NaN }));
            Assert.Throws<ArgumentException>(() => PlacementScoring.Compare(valid with { MaxLeaderMm = double.PositiveInfinity }, valid));
            Assert.Throws<ArgumentException>(() => PlacementScoring.Compare(valid, valid with { Overlaps = -1 }));
        }
        [Fact]
        public void ScoringAndConversionAreDeterministic_AndDoNotMutatePlannerData()
        {
            var c = Case(); var labels = c.BuildLabels();
            labels[0].Spot = new LabelSpot { Box = new LRect(40, 32, 48, 35), End = (40, 35) };
            string before = c.ToJson(); var originalSpot = labels[0].Spot;
            var input = PlacementScoring.FromPlanned(c, labels);
            string score = JsonSerializer.Serialize(PlacementScoring.Score(input));
            Assert.Equal(score, JsonSerializer.Serialize(PlacementScoring.Score(input)));
            Assert.Equal(before, c.ToJson()); Assert.Equal(originalSpot, labels[0].Spot);
        }
    }
}
