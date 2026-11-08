using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Tests
{
    /// <summary>
    /// Label placement on paper, in the viewport's frame: exact outlines, what is covered, where dragged labels go (square to
    /// the viewport first, any angle when needed), and whole-viewport plans without overlaps or crossings.
    /// </summary>
    public class LabelLayoutTests
    {
        // A three-line N/E/IL label: 30 × 12 mm, its leader joining at the middle of its top line (2 mm down from the top).
        private static readonly DraggedShape Nez = new DraggedShape { Right = new LRect(0, -10, 30, 2), Left = new LRect(-30, -10, 0, 2) };
        private static readonly LayoutRules Rules = new LayoutRules { MaxDistance = 40, Gap = 1, MinLeader = 4, Step = 2 };
        private static readonly HashSet<string> Own = new HashSet<string> { "NET-A" };

        /// <summary>The label's own network: a pipe along the sheet's X axis through y = <paramref name="y"/>, 1 mm wide.</summary>
        private static LabelObstacles WithPipe(double y = 100)
        {
            LabelObstacles o = new LabelObstacles();
            o.AddPolygon(new List<(double X, double Y)> { (-300, y - 0.5), (600, y - 0.5), (600, y + 0.5), (-300, y + 0.5) }, ObstacleGroup.Network, "pipe", network: "NET-A", part: "P1");
            return o;
        }

        private static double Area(IReadOnlyList<(double X, double Y)> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) a += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
            return Math.Abs(a / 2);
        }

        // ---------------- Shapes ----------------

        [Fact]
        public void Separated_IsExactForTurnedOutlines()
        {
            var square = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
            // A diamond whose upright box overlaps the square, but which itself stays 1.4 mm off its corner.
            var diamond = new List<(double X, double Y)> { (14, 8), (20, 14), (14, 20), (8, 14) };
            Assert.True(LabelLayout.Separated(square, diamond, 0));
            Assert.True(LRect.Around(diamond).Overlaps(new LRect(0, 0, 10, 10)));
            Assert.False(LabelLayout.Separated(square, new List<(double X, double Y)> { (5, 5), (20, 20) }, 0));
            Assert.True(LabelLayout.Separated(square, new List<(double X, double Y)> { (12, 0), (12, 10) }, 1.5));
            Assert.False(LabelLayout.Separated(square, new List<(double X, double Y)> { (12, 0), (12, 10) }, 2.5));
        }

        [Fact]
        public void Hull_OfATurnedLabel_IsItsTurnedOutline_NotAnUprightBox()
        {
            // Two text lines along a pipe at 45°: the hull is the turned rectangle, far smaller than the upright box round it.
            var pts = LabelLayout.Turn(new List<(double X, double Y)> { (0, 0), (40, 0), (40, 5), (0, 5), (5, 1), (30, 4) }, (0, 0), Math.PI / 4);
            List<(double X, double Y)> hull = LabelLayout.Hull(pts);
            Assert.Equal(4, hull.Count);
            Assert.Equal(200, Area(hull), 6);
            LRect upright = LRect.Around(hull);
            Assert.True(upright.Width * upright.Height > 3 * 200);
        }

        [Fact]
        public void TurnedNormalLabel_AlongItsPipe_IsClearWhenItsUprightBoxWouldNotBe()
        {
            // A label along a 45° pipe, with text 3 mm beside it: clear as drawn, though its upright box covers the text.
            var label = LabelLayout.Turn(new List<(double X, double Y)> { (0, 1), (40, 1), (40, 5), (0, 5) }, (0, 0), Math.PI / 4);
            LabelObstacles o = new LabelObstacles();
            o.AddPolygon(LabelLayout.Turn(new List<(double X, double Y)> { (0, 9), (40, 9), (40, 12), (0, 12) }, (0, 0), Math.PI / 4), ObstacleGroup.Text, "text");
            Assert.Null(o.Blocker(label, 1, Own, "L1"));
            Assert.Equal("text", o.Blocker(LRect.Around(label), 1, Own, "L1"));
        }

        [Fact]
        public void MinAreaRect_FitsATurnedManhole_SquareToIt()
        {
            // A 4.6 × 2.3 chamber outline with a cover square inside, turned 30°: the rectangle is the chamber, turned 30°.
            var chamber = new List<(double X, double Y)> { (0, 0), (4.6, 0), (4.6, 2.3), (0, 2.3), (0.3, 0.3), (1.0, 0.3), (1.0, 1.0), (0.3, 1.0) };
            List<(double X, double Y)> rect = LabelLayout.MinAreaRect(LabelLayout.Turn(chamber, (0, 0), Math.PI / 6));
            Assert.Equal(4, rect.Count);
            double Side(int i) => Math.Sqrt(Math.Pow(rect[(i + 1) % 4].X - rect[i].X, 2) + Math.Pow(rect[(i + 1) % 4].Y - rect[i].Y, 2));
            Assert.Equal(new[] { 2.3, 4.6 }, new[] { Side(0), Side(1) }.OrderBy(x => x).Select(x => Math.Round(x, 6)));
            int longSide = Side(0) > Side(1) ? 0 : 1;
            double angle = Math.Atan2(rect[longSide + 1].Y - rect[longSide].Y, rect[longSide + 1].X - rect[longSide].X) * 180 / Math.PI;
            Assert.Equal(30, ((angle % 180) + 180) % 180, 6);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(30, 30)]
        [InlineData(180, 0)]
        [InlineData(200, 20)]
        [InlineData(-170, 10)]
        [InlineData(95, -85)]
        public void OffUpright_TreatsUpsideDownAsHorizontal(double paperDeg, double offDeg) =>
            Assert.Equal(offDeg, LabelLayout.OffUpright(paperDeg * Math.PI / 180) * 180 / Math.PI, 6);

        // ---------------- One label ----------------

        [Fact]
        public void AtAPipe_LeaderGoesStraightUp_TextToTheRight()
        {
            LabelSpot? s = LabelLayout.Choose(WithPipe(), (100, 100), Nez, Rules, Own, "L1", null);
            Assert.NotNull(s);
            Assert.Equal(0, s.Value.Direction);
            Assert.Equal(100, s.Value.End.X, 9);
            Assert.Equal(100, s.Value.Box.MinX, 9);
            Assert.False(s.Value.Box.Contains((100, 100)));
        }

        [Fact]
        public void PreferDown_GoesStraightDown()
        {
            LayoutRules down = new LayoutRules { MaxDistance = 40, Gap = 1, MinLeader = 4, Step = 2, PreferUp = false };
            LabelSpot? s = LabelLayout.Choose(WithPipe(), (100, 100), Nez, down, Own, "L1", null);
            Assert.Equal(1, s.Value.Direction);
        }

        [Fact]
        public void TextAbove_TheLabelGoesElsewhere_NeverCoveringIt_OrCrossingIt()
        {
            LabelObstacles o = WithPipe();
            o.AddRect(new LRect(70, 104, 160, 120), ObstacleGroup.Text, "text");
            LabelSpot? s = LabelLayout.Choose(o, (100, 100), Nez, Rules, Own, "L1", null);
            Assert.NotNull(s);
            Assert.Null(o.Blocker(s.Value.Box, Rules.Gap, Own, "L1"));
            Assert.False(o.LeaderCrossesText((100, 100), s.Value.End));
            Assert.Equal(1, s.Value.Direction);
        }

        [Fact]
        public void BlockedSquare_AnyAngleIsUsed()
        {
            // Text close above and below the pipe: square leaders cross it or land on the pipe; a shallow slanting one fits.
            LabelObstacles o = WithPipe();
            o.AddRect(new LRect(80, 102, 120, 135), ObstacleGroup.Text, "text");
            o.AddRect(new LRect(80, 65, 120, 98), ObstacleGroup.Text, "text");
            LabelSpot? s = LabelLayout.Choose(o, (100, 100), Nez, new LayoutRules { MaxDistance = 60, Gap = 1, MinLeader = 4, Step = 2 }, Own, "L1", null);
            Assert.NotNull(s);
            Assert.Equal(3, s.Value.Direction);
            Assert.Null(o.Blocker(s.Value.Box, 1, Own, "L1"));
            Assert.False(o.LeaderCrossesText((100, 100), s.Value.End));
        }

        [Fact]
        public void OwnNetworkIsAvoided_OtherNetworksMayBeCovered()
        {
            LabelObstacles o = new LabelObstacles();
            o.AddSegment((0, 110), (300, 110), ObstacleGroup.Network, "pipe", network: "NET-A", part: "P2");
            o.AddSegment((0, 120), (300, 120), ObstacleGroup.Network, "pipe", network: "NET-B", part: "P9");
            LabelSpot? s = LabelLayout.Choose(o, (100, 100), Nez, Rules, Own, "L1", null);
            Assert.NotNull(s);
            Assert.False(s.Value.Box.Hits((0, 110), (300, 110)));
            Assert.Null(o.Blocker(new LRect(90, 115, 120, 125), 0.5, Own));
            Assert.Equal("pipe", o.Blocker(new LRect(90, 115, 120, 125), 0.5, new HashSet<string> { "NET-A", "NET-B" }));
        }

        [Fact]
        public void StructureOutline_IsKeptClear_ButANormalLabelMaySitOnItsOwnPart()
        {
            LabelObstacles o = new LabelObstacles();
            var chamber = new List<(double X, double Y)> { (95, 95), (105, 95), (105, 105), (95, 105) };
            o.AddPolygon(chamber, ObstacleGroup.Network, "structure", network: "NET-A", part: "MH1");
            Assert.Equal("structure", o.Blocker(new LRect(90, 90, 110, 110), 0, Own));
            Assert.Null(o.Blocker(new LRect(90, 90, 110, 110), 0, Own, alsoOwn: "MH1"));
            Assert.Equal("structure", o.Blocker(new LRect(99, 99, 101, 101), 0, Own));
        }

        [Fact]
        public void NoClearPlace_GivesNothing_RatherThanAnOverlap()
        {
            LabelObstacles o = new LabelObstacles();
            o.AddRect(new LRect(-200, -200, 200, 200), ObstacleGroup.Text, "text");
            Assert.Null(LabelLayout.Choose(o, (0, 0), Nez, Rules, Own, "L1", null));
        }

        [Fact]
        public void Area_KeepsTheLabelInsideTheViewport()
        {
            LRect viewport = new LRect(0, 0, 300, 110);
            LabelSpot? s = LabelLayout.Choose(WithPipe(), (100, 100), Nez, Rules, Own, "L1", b => b.Inside(viewport));
            Assert.NotNull(s);
            Assert.True(s.Value.Box.Inside(viewport));
            Assert.NotEqual(0, s.Value.Direction);
        }

        [Fact]
        public void Spots_SquareToTheViewportFirst_SlantingCostsMoreTheMoreOffSquare()
        {
            List<LabelSpot> spots = LabelLayout.Spots((0, 0), Nez, Rules);
            // Right by the anchor a sideways leader is the cheapest square one (straight up, the text would sit on the anchor).
            Assert.True(spots[0].Direction < 3);
            Assert.All(spots, s => Assert.True(Math.Sqrt(s.End.X * s.End.X + s.End.Y * s.End.Y) <= Rules.MaxDistance + 1e-9));
            // At the same length a square leader is cheaper than a slanting one.
            LabelSpot square = spots.First(s => s.Direction == 2 && Math.Abs(s.End.X - 20) < 1e-9);
            LabelSpot slant = spots.First(s => s.Direction == 3 && Math.Abs(Math.Sqrt(s.End.X * s.End.X + s.End.Y * s.End.Y) - 20) < 0.2);
            Assert.True(square.Cost < slant.Cost);
            Assert.Equal(0, LabelLayout.OffAxis(0, 5), 9);
            Assert.Equal(45, LabelLayout.OffAxis(-3, 3), 9);
        }

        // ---------------- Whole viewports ----------------

        private static PlanLabel Label(string key, double x, double y) => new PlanLabel { Key = key, Anchor = (x, y), Shape = Nez, Networks = Own };

        private static void AssertClean(LabelObstacles baseObs, List<PlanLabel> labels)
        {
            Assert.All(labels, l => Assert.True(l.Spot.HasValue, l.Key + " has no place"));
            for (int i = 0; i < labels.Count; i++)
            {
                LabelSpot a = labels[i].Spot.Value;
                Assert.Null(baseObs.Blocker(a.Box, Rules.Gap - 1e-6, Own, labels[i].Key));
                for (int j = 0; j < labels.Count; j++)
                {
                    if (i == j) continue;
                    LabelSpot b = labels[j].Spot.Value;
                    Assert.False(a.Box.Overlaps(b.Box), $"{labels[i].Key} overlaps {labels[j].Key}");
                    Assert.False(b.Box.Hits(labels[i].Anchor, a.End), $"leader of {labels[i].Key} crosses {labels[j].Key}");
                    if (j > i) Assert.False(LabelLayout.Cross(labels[i].Anchor, a.End, labels[j].Anchor, b.End), $"leaders of {labels[i].Key} and {labels[j].Key} cross");
                }
            }
        }

        [Fact]
        public void Row_OfEndLabels_AllGoTheSameWay_AtOneHeight()
        {
            LabelObstacles o = WithPipe(0);
            List<PlanLabel> labels = new[] { 0.0, 40, 80, 120 }.Select(x => Label("L" + x, x, 0)).ToList();
            foreach (PlanLabel l in labels) LabelLayout.ReserveAnchor(o, l.Anchor, l.Key);
            LabelPlanner.Plan(o, labels, Rules);
            AssertClean(o, labels);
            Assert.Single(labels.Select(l => l.Spot.Value.Direction).Distinct());
            Assert.Single(labels.Select(l => Math.Round(l.Spot.Value.End.Y, 6)).Distinct());
        }

        [Fact]
        public void CrowdedManhole_SixLabels_NoOverlapNoCrossing()
        {
            // A manhole on a pipe with six labels within 6 mm of each other (as in the sheet with IF-MVDB-PIPE-38 / 56 / 57).
            LabelObstacles o = WithPipe();
            o.AddPolygon(new List<(double X, double Y)> { (96, 97), (106, 97), (106, 103), (96, 103) }, ObstacleGroup.Network, "structure", network: "NET-A", part: "MH");
            List<PlanLabel> labels = Enumerable.Range(0, 6).Select(i => Label("L" + i, 98 + i * 1.2, i % 2 == 0 ? 100.3 : 99.7)).ToList();
            foreach (PlanLabel l in labels) LabelLayout.ReserveAnchor(o, l.Anchor, l.Key);
            LabelPlanner.Plan(o, labels, new LayoutRules { MaxDistance = 80, Gap = 1, MinLeader = 4, Step = 2 });
            AssertClean(o, labels);
        }

        [Fact]
        public void Untangle_SwapsTwoCrossingLeaders()
        {
            LabelObstacles o = new LabelObstacles();
            PlanLabel a = Label("A", 0, 0), b = Label("B", 20, 0);
            a.Spot = new LabelSpot { End = (20, 20), Box = Nez.Right.Offset(20, 20) };
            b.Spot = new LabelSpot { End = (0, 20), Box = Nez.Left.Offset(0, 20) };
            List<PlanLabel> labels = new List<PlanLabel> { a, b };
            Assert.True(LabelLayout.Cross(a.Anchor, a.Spot.Value.End, b.Anchor, b.Spot.Value.End));
            Assert.Equal(1, LabelPlanner.Untangle(o, labels, Rules));
            Assert.False(LabelLayout.Cross(a.Anchor, a.Spot.Value.End, b.Anchor, b.Spot.Value.End));
            Assert.False(a.Spot.Value.Box.Overlaps(b.Spot.Value.Box));
        }

        // ---------------- Stage 2: quadrants, sliding, crowds, cases ----------------

        [Fact]
        public void DraggedShape_UsesTheBoxMeasuredBelow_WhenThereIsOne()
        {
            DraggedShape s = new DraggedShape { Right = new LRect(0, -10, 30, 2), Left = new LRect(-30, -10, 0, 2), DownRight = new LRect(0, -2, 30, 10) };
            Assert.Equal(-10, s.BoxFor(5, 5, true).MinY, 9);
            Assert.Equal(-2, s.BoxFor(5, -5, true).MinY, 9);
            Assert.Equal(-10, s.BoxFor(-5, -5, false).MinY, 9);
        }

        [Fact]
        public void PipeLabel_BoxedInAtItsAnchor_SlidesAlongThePipe()
        {
            // Text all round the middle of the pipe; 40 mm along it the sheet is clear.
            LabelObstacles o = WithPipe();
            o.AddRect(new LRect(60, 60, 140, 99), ObstacleGroup.Text, "text");
            o.AddRect(new LRect(60, 101, 140, 140), ObstacleGroup.Text, "text");
            PlanLabel l = new PlanLabel
            {
                Key = "L", Anchor = (100, 100), Shape = Nez, Networks = Own, Ratio = 0.5,
                AltAnchors = new List<(double X, double Y, double Ratio)> { (160, 100, 0.8), (40, 100, 0.2) }
            };
            LabelSpot? s = LabelPlanner.ChooseFor(o, l, Rules);
            Assert.NotNull(s);
            Assert.True(s.Value.Ratio.HasValue);
            Assert.Null(o.Blocker(s.Value.Box, Rules.Gap, Own, "L"));
            Assert.False(o.LeaderCrossesText(s.Value.Anchor, s.Value.End, "L"));
            // Without anywhere to slide to, nothing fits.
            Assert.Null(LabelPlanner.ChooseFor(o, new PlanLabel { Key = "L", Anchor = (100, 100), Shape = Nez, Networks = Own }, Rules));
        }

        [Fact]
        public void WaitingPoints_AreNotWalledIn_ByTheFirstLabel()
        {
            LabelObstacles o = WithPipe();
            LabelSpot? alone = LabelLayout.Choose(o, (100, 100), Nez, Rules, Own, "A", null);
            LabelSpot? considerate = LabelLayout.Choose(o, (100, 100), Nez, Rules, Own, "A", null, unplaced: new List<(double X, double Y)> { (104, 100) });
            Assert.NotNull(alone);
            Assert.NotNull(considerate);
            Assert.True(LabelLayout.Distance(considerate.Value.Box, (104, 100)) >= LabelLayout.Distance(alone.Value.Box, (104, 100)));
        }

        [Fact]
        public void VerticalRun_CrowdedPoints_FanOutCleanly()
        {
            // A pipe up the sheet with five labelled points 2 mm apart.
            LabelObstacles o = new LabelObstacles();
            o.AddPolygon(new List<(double X, double Y)> { (99.5, 0), (100.5, 0), (100.5, 300), (99.5, 300) }, ObstacleGroup.Network, "pipe", network: "NET-A", part: "P1");
            List<PlanLabel> labels = Enumerable.Range(0, 5).Select(i => Label("V" + i, 100, 100 + i * 2)).ToList();
            foreach (PlanLabel l in labels) LabelLayout.ReserveAnchor(o, l.Anchor, l.Key);
            LabelPlanner.Plan(o, labels, new LayoutRules { MaxDistance = 80, Gap = 1, MinLeader = 4, Step = 2 });
            AssertClean(o, labels);
            // They fan out both ways from the middle of the group.
            Assert.Contains(labels, l => l.Spot.Value.End.Y > 104);
            Assert.Contains(labels, l => l.Spot.Value.End.Y < 104);
        }

        [Fact]
        public void Case_RoundTripsThroughJson_AndPlansTheSame()
        {
            LabelObstacles o = WithPipe();
            o.AddRect(new LRect(70, 104, 160, 120), ObstacleGroup.Text, "text");
            List<PlanLabel> labels = new[] { 0.0, 40, 80 }.Select(x => Label("L" + x, 100 + x, 100)).ToList();
            ArrangeCase c = new ArrangeCase { Sheet = "test", MaxDistance = 40, Gap = 1, Margin = 0 };
            c.Viewport = new List<double[]> { new[] { 0.0, 0 }, new[] { 400.0, 0 }, new[] { 400.0, 300 }, new[] { 0.0, 300 } };
            c.AddObstacles(o);
            foreach (PlanLabel l in labels) c.AddLabel(l, null);
            ArrangeCase back = ArrangeCase.FromJson(c.ToJson());
            Assert.Equal(c.Obstacles.Count, back.Obstacles.Count);
            List<PlanLabel> replay = back.BuildLabels();
            LabelPlanner.Plan(back.BuildObstacles(), replay, back.Rules());
            LabelPlanner.Plan(o, labels, back.Rules());
            Assert.Equal(labels.Select(l => l.Spot?.End), replay.Select(l => l.Spot?.End));
        }
    }
}
