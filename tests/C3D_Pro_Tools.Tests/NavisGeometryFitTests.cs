using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>Measuring pipes, culverts and chambers from Navisworks triangles (project coordinates, metres).</summary>
    public class NavisGeometryFitTests
    {
        // Project-sized coordinates, as in a real model: precision loss there is the usual failure.
        private const double E = 512345.678, N = 2734567.891;

        [Fact]
        public void UniqueVertices_MergesSharedCorners()
        {
            double[] mesh = Meshes.Box(new V3(0, 0, 0), new V3(10, 0, 0), 1, 1);
            Assert.Equal(8, NavisGeometryFit.UniqueVertices(mesh).Count);
        }

        [Fact]
        public void FitLinear_SlopingCircularPipe()
        {
            V3 a = new V3(E, N, 10.0), b = new V3(E + 30, N + 40, 9.5);
            LinearFit fit = NavisGeometryFit.FitLinear(Meshes.Cylinder(a, b, 0.3));

            Assert.NotNull(fit);
            Assert.Equal(FitShape.Circular, fit.Shape);
            Assert.Equal(0.6, fit.OuterDiameter, 2);
            Assert.Equal((b - a).Length, fit.Length, 3);
            double ends = Math.Min(Math.Max((fit.Start - a).Length, (fit.End - b).Length), Math.Max((fit.Start - b).Length, (fit.End - a).Length));
            Assert.True(ends < 0.005, $"ends off by {ends:0.0000} m");
            Assert.True(fit.Straight);
        }

        [Fact]
        public void FitLinear_RectangularBoxCulvert()
        {
            V3 a = new V3(E, N, 5.0), b = new V3(E, N + 25, 5.0);
            LinearFit fit = NavisGeometryFit.FitLinear(Meshes.Box(a, b, 2.4, 1.5));

            Assert.NotNull(fit);
            Assert.Equal(FitShape.Rectangular, fit.Shape);
            Assert.Equal(2.4, fit.Width, 3);
            Assert.Equal(1.5, fit.Height, 3);
            Assert.Equal(25.0, fit.Length, 3);
            Assert.Equal(0.0, fit.Roll, 3);
        }

        [Fact]
        public void FitLinear_TooFewVertices_ReturnsNull()
        {
            Assert.Null(NavisGeometryFit.FitLinear(new[] { 0.0, 0, 0, 1, 0, 0, 0, 1, 0 }));
        }

        [Fact]
        public void FitFootprint_RoundManhole()
        {
            FootprintFit f = NavisGeometryFit.FitFootprint(Meshes.RoundChamber(E, N, 95.0, 100.0, 1.5));

            Assert.NotNull(f);
            Assert.Equal(FitShape.Circular, f.Shape);
            Assert.Equal(1.5, f.Diameter, 2);
            Assert.Equal(E, f.CenterX, 3);
            Assert.Equal(N, f.CenterY, 3);
            Assert.Equal(100.0, f.Top, 6);
            Assert.Equal(95.0, f.Bottom, 6);
        }

        [Fact]
        public void FitFootprint_RotatedRectangularChamber()
        {
            double rotation = 30 * Math.PI / 180;
            FootprintFit f = NavisGeometryFit.FitFootprint(Meshes.RectChamber(E, N, 96.0, 99.0, 2.0, 1.2, rotation));

            Assert.NotNull(f);
            Assert.Equal(FitShape.Rectangular, f.Shape);
            Assert.Equal(2.0, f.Length, 3);
            Assert.Equal(1.2, f.Width, 3);
            Assert.Equal(E, f.CenterX, 3);
            Assert.Equal(N, f.CenterY, 3);
            // A rectangle's direction is only defined to half a turn.
            double diff = Math.IEEERemainder(f.Rotation - rotation, Math.PI);
            Assert.True(Math.Abs(diff) < 1e-3, $"rotation {f.Rotation * 180 / Math.PI:0.000}°");
        }
    }
}
