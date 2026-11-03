using C3DProTools.NavisLink;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>Navisworks to drawing coordinates: turned about the origin, then shifted.</summary>
    public class NavisTransformTests
    {
        private static readonly NavisTransform QuarterTurnAndShift = new NavisTransform { Dx = 1000, Dy = 2000, Dz = 5, RotationDeg = 90 };

        private static void Near(V3 expected, V3 actual)
        {
            Assert.Equal(expected.X, actual.X, 9);
            Assert.Equal(expected.Y, actual.Y, 9);
            Assert.Equal(expected.Z, actual.Z, 9);
        }

        [Fact]
        public void Point_TurnsThenShifts_DirectionOnlyTurns()
        {
            Near(new V3(1000 - 20, 2000 + 10, 8), QuarterTurnAndShift.Point(new V3(10, 20, 3)));
            Near(new V3(0, 1, 0), QuarterTurnAndShift.Direction(new V3(1, 0, 0)));
        }

        [Fact]
        public void Pipe_EndsAndMeshMove_Together()
        {
            V3 a = new V3(10, 0, 50), b = new V3(40, 0, 49.7);
            ImportCandidate c = NavisInterpreter.Read(new NavisElement
            {
                Name = "Pipe", Triangles = Meshes.Cylinder(a, b, 0.2), Box = new[] { 9.8, -0.2, 49.5, 40.2, 0.2, 50.2 }
            });
            QuarterTurnAndShift.Apply(c);

            Near(QuarterTurnAndShift.Point(a), c.Start.X < c.End.X || c.Start.Y < c.End.Y ? c.Start : c.End);
            // The mesh is moved too (the exact solid is built from it), and still fits the moved centreline.
            LinearFit refit = NavisGeometryFit.FitLinear(c.Source.Triangles);
            Assert.Equal((b - a).Length, refit.Length, 3);
            Assert.True((refit.Start - c.Start).Length < 0.01 || (refit.Start - c.End).Length < 0.01);
            Assert.Equal(1000 - 0.2, c.Source.Box[0], 3);
        }

        [Fact]
        public void Structure_PositionTurnAndLevelsMove()
        {
            ImportCandidate c = NavisInterpreter.Read(new NavisElement { Name = "Chamber", Triangles = Meshes.RectChamber(10, 0, 95, 100, 2.0, 1.2, 0) });
            double rotation = c.Rotation;
            QuarterTurnAndShift.Apply(c);

            Assert.Equal(1000.0, c.X, 6);
            Assert.Equal(2010.0, c.Y, 6);
            Assert.Equal(105.0, c.Rim, 6);
            Assert.Equal(100.0, c.Sump, 6);
            Assert.Equal(rotation + Math.PI / 2, c.Rotation, 9);
            Assert.Equal(c.X, c.Footprint.CenterX, 6);
        }

        [Fact]
        public void NoShift_LeavesEverythingAlone()
        {
            ImportCandidate c = NavisInterpreter.Read(new NavisElement { Name = "Pipe", Triangles = Meshes.Cylinder(new V3(0, 0, 0), new V3(20, 0, 0), 0.2) });
            double[] mesh = c.Source.Triangles;
            new NavisTransform().Apply(c);
            Assert.Same(mesh, c.Source.Triangles);
        }
    }
}
