using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Tests
{
    /// <summary>Network tool geometry: mitred run offsets, duct arrays, rectangular chamber walls, spans and a line's grade.</summary>
    public class PlanGeometryTests
    {
        private static void Near((double X, double Y) expected, (double X, double Y) actual, int digits = 9)
        {
            Assert.Equal(expected.X, actual.X, digits);
            Assert.Equal(expected.Y, actual.Y, digits);
        }

        // ---------------- Mitred offsets ----------------

        [Fact]
        public void OffsetRun_Straight_ShiftsToTheRight()
        {
            var run = new List<(double X, double Y)> { (0, 0), (10, 0), (20, 0) };
            List<(double X, double Y)> o = PlanGeometry.OffsetRun(run, 0.5);
            Near((0, -0.5), o[0]);
            Near((10, -0.5), o[1]);
            Near((20, -0.5), o[2]);
        }

        [Fact]
        public void OffsetRun_RightAngle_MeetsAtTheMitre_WithBothLegsParallel()
        {
            // East then north: the right side is outside the turn, so the corner goes out to (10.5, −0.5).
            var run = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10) };
            List<(double X, double Y)> right = PlanGeometry.OffsetRun(run, 0.5);
            Near((0, -0.5), right[0]);
            Near((10.5, -0.5), right[1]);
            Near((10.5, 10), right[2]);
            // Left (inside the turn): (9.5, 0.5). Every offset leg is exactly 0.5 m from its source leg: no wedge, no gap.
            List<(double X, double Y)> left = PlanGeometry.OffsetRun(run, -0.5);
            Near((9.5, 0.5), left[1]);
            Assert.Equal(0.5, left[0].Y, 9);
            Assert.Equal(9.5, left[2].X, 9);
        }

        [Fact]
        public void OffsetRun_ShallowDeflection_MitreLengthIsOffsetOverCosHalfAngle()
        {
            double turn = 30 * Math.PI / 180;
            var run = new List<(double X, double Y)> { (0, 0), (10, 0), (10 + 10 * Math.Cos(turn), 10 * Math.Sin(turn)) };
            (double X, double Y) corner = PlanGeometry.OffsetRun(run, 1.0)[1];
            double reach = Math.Sqrt((corner.X - 10) * (corner.X - 10) + corner.Y * corner.Y);
            Assert.Equal(1.0 / Math.Cos(turn / 2), reach, 9);
        }

        [Fact]
        public void OffsetRun_HairpinTurn_IsHeldAtTheMitreLimit()
        {
            var run = new List<(double X, double Y)> { (0, 0), (10, 0), (0, 0.2) };
            (double X, double Y) corner = PlanGeometry.OffsetRun(run, 1.0, maxMitre: 4)[1];
            double reach = Math.Sqrt((corner.X - 10) * (corner.X - 10) + corner.Y * corner.Y);
            Assert.Equal(4.0, reach, 9);
        }

        // ---------------- Duct arrays ----------------

        [Fact]
        public void ArrayOffsets_AreCentred_AndPitchFromAClearGap()
        {
            (List<double> h, List<double> v) = PlanGeometry.ArrayOffsets(4, 2, 0.2, 0.15);
            Assert.Equal(new[] { -0.3, -0.1, 0.1, 0.3 }, h);
            Assert.Equal(new[] { -0.075, 0.075 }, v);
            Assert.Equal(new[] { 0.0 }, PlanGeometry.ArrayOffsets(1, 1, 0.2, 0.2).horizontal);
            Assert.Equal(0.16, PlanGeometry.Pitch(0.05, 0.11, clearGap: true), 9);
            Assert.Equal(0.20, PlanGeometry.Pitch(0.20, 0.11, clearGap: false), 9);
        }

        // ---------------- Rectangular chambers ----------------

        // A chamber 1.2 m wide (local X) × 2.0 m long (local Y): its narrow walls (1.2 m) face ±Y.
        private const double HalfX = 0.6, HalfY = 1.0;

        [Fact]
        public void ChamberEntry_SquareThroughTheNarrowWall_IsOk()
        {
            var (wall, skew) = PlanGeometry.ChamberEntry(0, 0, 0, HalfX, HalfY, far: (0, 20), near: (0, 0.5));
            Assert.Equal(PlanGeometry.ChamberWall.Narrow, wall);
            Assert.Equal(0, skew, 6);
        }

        [Fact]
        public void ChamberEntry_ThroughTheLongWall_IsFound()
        {
            var (wall, _) = PlanGeometry.ChamberEntry(0, 0, 0, HalfX, HalfY, far: (20, 0), near: (0.3, 0));
            Assert.Equal(PlanGeometry.ChamberWall.Long, wall);
        }

        [Fact]
        public void ChamberEntry_SkewIsMeasuredFromTheWallsNormal_AndFollowsTheRotation()
        {
            double a = 8 * Math.PI / 180;
            var (wall, skew) = PlanGeometry.ChamberEntry(0, 0, 0, HalfX, HalfY, far: (20 * Math.Sin(a), 20 * Math.Cos(a)), near: (0, 0));
            Assert.Equal(PlanGeometry.ChamberWall.Narrow, wall);
            Assert.Equal(8, skew, 6);

            // The same chamber turned 90°: the narrow walls now face ±X, so a pipe from the east enters one square.
            var turned = PlanGeometry.ChamberEntry(100, 50, Math.PI / 2, HalfX, HalfY, far: (130, 50), near: (100.5, 50));
            Assert.Equal(PlanGeometry.ChamberWall.Narrow, turned.wall);
            Assert.Equal(0, turned.skewDeg, 6);
        }

        [Fact]
        public void AlignedRotation_PutsTheNarrowWallsSquareToThePipe_WithTheLeastTurn()
        {
            // One pipe leaving due east (bearing 0): the length (local Y) must lie east-west, so the rotation is ±90°;
            // from 0.1 rad, +90° is the smaller turn.
            double? r = PlanGeometry.AlignedRotation(0.1, HalfX, HalfY, new[] { 0.0 });
            Assert.NotNull(r);
            Assert.Equal(Math.PI / 2, r.Value, 9);
            var check = PlanGeometry.ChamberEntry(0, 0, r.Value, HalfX, HalfY, far: (20, 0), near: (0.5, 0));
            Assert.Equal(PlanGeometry.ChamberWall.Narrow, check.wall);
            Assert.Equal(0, check.skewDeg, 6);

            // Straight through (in from the west, out to the east, 4° off): the mean axis.
            double? through = PlanGeometry.AlignedRotation(0, HalfX, HalfY, new[] { 0.0, Math.PI + 4 * Math.PI / 180 });
            Assert.NotNull(through);
            Assert.Equal(-Math.PI / 2 + 2 * Math.PI / 180, through.Value, 9);
        }

        [Fact]
        public void AlignedRotation_LeavesBendsAndSquareChambersAlone()
        {
            Assert.Null(PlanGeometry.AlignedRotation(0, HalfX, HalfY, new[] { 0.0, Math.PI / 2 }));
            Assert.Null(PlanGeometry.AlignedRotation(0, 0.6, 0.6, new[] { 0.0 }));
            Assert.Null(PlanGeometry.AlignedRotation(0, HalfX, HalfY, new[] { 0.0, 1.0, 2.0 }));
        }

        // ---------------- Spans ----------------

        [Fact]
        public void Spans_JoinPipesMeetingWithoutAStructure()
        {
            var pipes = new List<(string, bool, string, bool, double)>
            {
                ("MH1", true, "pt-A", false, 90),    // split pipe, no structure at A
                ("pt-A", false, "MH2", true, 80),
                ("MH2", true, "MH3", true, 60)
            };
            List<(List<int> pipes, double length)> spans = PlanGeometry.Spans(pipes);
            Assert.Equal(2, spans.Count);
            Assert.Contains(spans, s => s.pipes.SequenceEqual(new[] { 0, 1 }) && Math.Abs(s.length - 170) < 1e-9);
            Assert.Contains(spans, s => s.pipes.SequenceEqual(new[] { 2 }) && Math.Abs(s.length - 60) < 1e-9);
        }

        // ---------------- Grades ----------------

        [Fact]
        public void TrendSlope_IsTheGradeOfAStraightLine()
        {
            Assert.Equal(-0.02, PlanGeometry.TrendSlope(new List<(double S, double Z)> { (0, 10), (30, 9.4), (100, 8) }), 9);
        }

        [Fact]
        public void TrendSlope_JudgesTheWholeLine_NotItsEnds()
        {
            // The last point is a little higher than the first, but nearly all the line falls: it runs downhill.
            var line = new List<(double S, double Z)> { (0, 10.0), (90, 8.0), (100, 10.1) };
            Assert.True(line[line.Count - 1].Z > line[0].Z);
            Assert.True(PlanGeometry.TrendSlope(line) < 0);
        }

        [Fact]
        public void GradeDeviation_FindsElevationPointsOffTheGrade()
        {
            Assert.Equal(0, PlanGeometry.GradeDeviation(new List<(double S, double Z)> { (0, 10), (5, 9.9), (10, 9.8) }), 9);
            Assert.Equal(0.4, PlanGeometry.GradeDeviation(new List<(double S, double Z)> { (0, 10), (5, 10.3), (10, 9.8) }), 9);
        }

        [Fact]
        public void PaperToModel_UsesTheViewportScale()
        {
            // 1:500 in a metre drawing on millimetre paper: 2 mm of paper per metre.
            Assert.Equal(10.0, PlanGeometry.PaperToModel(20, 2.0), 9);
            Assert.Equal(40.0, PlanGeometry.PaperToModel(20, 0.5), 9);
        }
    }
}
