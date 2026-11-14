using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Tests
{
    public class LabelPatternTests
    {
        private static PlanLabel Label(int i, double x, double y) => new PlanLabel
        {
            Key = "P" + i, Anchor = (x, y), Networks = new HashSet<string>(), IsStructure = false,
            Shape = new DraggedShape { Right = new LRect(0, -3, 10, 3), Left = new LRect(-10, -3, 0, 3) }
        };

        private static void Clean(List<PlanLabel> labels, LayoutRules r)
        {
            Assert.All(labels, l => Assert.True(l.Spot.HasValue, l.Key));
            foreach (var l in labels)
            {
                var s = l.Spot.Value;
                Assert.Null(s.Hull);
                Assert.False(s.Box.Contains(l.Anchor));
                double length = Math.Sqrt(Math.Pow(s.End.X - l.Anchor.X, 2) + Math.Pow(s.End.Y - l.Anchor.Y, 2));
                Assert.InRange(length, r.MinLeader - 1e-6, r.MaxDistance + 1e-6);
                foreach (var other in labels.Where(x => x != l))
                {
                    var b = other.Spot.Value;
                    Assert.False(s.Box.Grow(r.Gap - 1e-6).Overlaps(b.Box));
                    Assert.False(b.Box.Hits(l.Anchor, s.End));
                    Assert.False(LabelLayout.Cross(l.Anchor, s.End, other.Anchor, b.End));
                }
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(90)]
        [InlineData(35)]
        public void SparseRun_AlternatesSquareLeaders_InAnchorOrder(double degrees)
        {
            double a = degrees * Math.PI / 180, ux = Math.Cos(a), uy = Math.Sin(a);
            var labels = Enumerable.Range(0, 5).Select(i => Label(i, 30 * i * ux, 30 * i * uy)).ToList();
            var r = new LayoutRules { MaxDistance = 40, Gap = 2, MinLeader = 3 };
            LabelPlanner.Plan(new LabelObstacles(), labels, r);
            Clean(labels, r);
            for (int i = 0; i < labels.Count; i++)
            {
                double dx = labels[i].Spot.Value.End.X - labels[i].Anchor.X;
                double dy = labels[i].Spot.Value.End.Y - labels[i].Anchor.Y;
                Assert.Equal(0, dx * ux + dy * uy, 6);
                Assert.Equal(i % 2 == 0 ? 1 : -1, Math.Sign(-dx * uy + dy * ux));
                Assert.Equal(8, Math.Sqrt(dx * dx + dy * dy), 6);
            }
        }

        [Fact]
        public void SparseRun_UsesOtherSide_WhenAlternatingSideIsBlocked()
        {
            var labels = Enumerable.Range(0, 3).Select(i => Label(i, i * 30, 0)).ToList();
            var o = new LabelObstacles();
            o.AddRect(new LRect(-15, 1, 15, 60), ObstacleGroup.Text, "note");
            var r = new LayoutRules { MaxDistance = 40, Gap = 2 };
            LabelPlanner.Plan(o, labels, r);
            Clean(labels, r);
            Assert.True(labels[0].Spot.Value.End.Y < 0);
            Assert.True(labels[1].Spot.Value.End.Y < 0);
            Assert.True(labels[2].Spot.Value.End.Y > 0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CrowdedRun_UsesStaircaseOrAlternatesForVerticalRuns(bool vertical)
        {
            var labels = Enumerable.Range(0, 5).Select(i => Label(i, vertical ? 0 : 4 * i, vertical ? 4 * i : 0)).ToList();
            var r = new LayoutRules { MaxDistance = 50, Gap = 2 };
            LabelPlanner.Plan(new LabelObstacles(), labels, r);
            Clean(labels, r);
            if (vertical)
            {
                for (int i = 1; i < labels.Count; i++)
                    Assert.Equal(-Math.Sign(labels[i - 1].Spot.Value.End.X), Math.Sign(labels[i].Spot.Value.End.X));
                return;
            }
            Assert.Single(labels.Select(l => Math.Sign(l.Spot.Value.End.Y)).Distinct());
            for (int i = 1; i < labels.Count; i++)
            {
                var prev = labels[i - 1].Spot.Value.Box;
                var next = labels[i].Spot.Value.Box;
                Assert.True(next.MinX >= prev.MaxX + r.Gap - 1e-6 || !next.Grow(r.Gap - 1e-6).Overlaps(prev));
            }
        }

        [Fact]
        public void Pattern_UsesMeasuredAngleBox_ButKeepsUprightHullNull()
        {
            var labels = Enumerable.Range(0, 3).Select(i => Label(i, i * 30, 0)).ToList();
            foreach (var l in labels)
                l.Shape.Samples.Add((Math.PI / 2, new LRect(-4, -2, 4, 2), new List<(double X, double Y)> { (-1, 0), (0, 1), (1, 0) }));
            var r = new LayoutRules { MaxDistance = 40, Gap = 2 };
            LabelPlanner.Plan(new LabelObstacles(), labels, r);
            Clean(labels, r);
            Assert.All(labels, l => { Assert.Equal(8, l.Spot.Value.Box.Width, 6); Assert.Equal(4, l.Spot.Value.Box.Height, 6); });
        }

        [Fact]
        public void StructureFlag_RoundTrips_AndLegacySizeStillWorks()
        {
            var l = Label(0, 50, 50);
            l.Shape.Right = new LRect(0, -30, 35, 3.5);
            l.Shape.Left = new LRect(-35, -30, 0, 3.5);
            var legacy = new ArrangeCase.Label { Key = l.Key, Anchor = new[] { 50.0, 50 }, Right = new[] { 0.0, -30, 35, 3.5 }, Left = new[] { -35.0, -30, 0, 3.5 } };
            var c = new ArrangeCase { Labels = new List<ArrangeCase.Label> { legacy } };
            Assert.True(c.BuildLabels()[0].IsStructure);
            legacy.IsStructure = false;
            Assert.False(ArrangeCase.FromJson(c.ToJson()).BuildLabels()[0].IsStructure);
            l.IsStructure = true;
            c.Labels.Clear();
            c.AddLabel(l, null);
            Assert.True(ArrangeCase.FromJson(c.ToJson()).BuildLabels()[0].IsStructure);
        }
    }
}
