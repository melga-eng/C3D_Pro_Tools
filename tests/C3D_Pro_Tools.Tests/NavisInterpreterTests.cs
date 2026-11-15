using System.Collections.Generic;
using C3DProTools.NavisLink;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    /// <summary>What each Navisworks item is read as: Civil 3D pipes and structures by their properties, the rest by geometry.</summary>
    public class NavisInterpreterTests
    {
        private static NavisProp Civil(string name, string value, double? number = null) =>
            new NavisProp { Category = "Civil3D", Name = name, Value = value, Number = number };

        private static NavisElement CivilPipe(V3 a, V3 b, double inner, double wall, double[] mesh) => new NavisElement
        {
            Guid = "pipe-1", Name = "Pipe - (1)", ClassName = "AECC_PIPE", Triangles = mesh,
            Props = new List<NavisProp>
            {
                Civil("Pipe Start Easting", "", a.X), Civil("Pipe Start Northing", "", a.Y), Civil("Start Centerline Elevation", "", a.Z),
                Civil("Pipe End Easting", "", b.X), Civil("Pipe End Northing", "", b.Y), Civil("End Centerline Elevation", "", b.Z),
                Civil("Cross Sectional Shape", "Circular"),
                Civil("Inner Diameter or Width", $"{inner * 1000:0.000}mm"),
                Civil("Wall Thickness", $"{wall * 1000:0.000}mm"),
                Civil("Outer Diameter or Width", $"{(inner + 2 * wall) * 1000:0.000}mm"),
                Civil("Network name", "Storm"),
                Civil("Material", "Reinforced Concrete")
            }
        };

        [Theory]
        [InlineData("Reinforced Concrete", "concrete")]
        [InlineData("RCP", "concrete")]
        [InlineData("uPVC SN8", "pvc")]
        [InlineData("Corrugated HDPE", "corrugated hdpe")]
        [InlineData("CMP", "corrugated metal")]
        [InlineData("Ductile Iron K9", "ductile iron")]
        [InlineData("GRP", "grp")]
        [InlineData("Vitrified Clay", "clay")]
        [InlineData("", "")]
        [InlineData("Unobtainium", "unobtainium")]
        public void MaterialKey_Normalises(string material, string key)
        {
            Assert.Equal(key, NavisInterpreter.MaterialKey(material));
        }

        [Fact]
        public void CivilPipe_MatchingMesh_IsExactAndHighConfidence()
        {
            V3 a = new V3(1000, 2000, 50), b = new V3(1040, 2000, 49.8);
            ImportCandidate c = NavisInterpreter.Read(CivilPipe(a, b, 0.6, 0.075, Meshes.Cylinder(a, b, 0.375)));

            Assert.Equal("Civil 3D", c.Origin);
            Assert.Equal(ImportKind.Pipe, c.Action);
            Assert.Equal(0.6, c.InnerWidth, 6);
            Assert.Equal(0.075, c.Wall, 6);
            Assert.Equal(0.75, c.OuterWidth, 6);
            Assert.Equal("Storm", c.Network);
            Assert.Equal("Reinforced Concrete", c.Material);
            Assert.Equal(ImportConfidence.High, c.Confidence);
        }

        [Fact]
        public void CivilPipe_MeshMovedAway_UsesTheMeasuredCentreline()
        {
            V3 a = new V3(1000, 2000, 50), b = new V3(1040, 2000, 49.8), shift = new V3(0, 0.2, 0);
            ImportCandidate c = NavisInterpreter.Read(CivilPipe(a, b, 0.6, 0.075, Meshes.Cylinder(a + shift, b + shift, 0.375)));

            Assert.Contains(c.Notes, n => n.Contains("the measured centreline is used"));
            Assert.Equal(2000.2, c.Start.Y, 3);
            Assert.Equal(ImportConfidence.Low, c.Confidence);
        }

        [Fact]
        public void GeometryOnly_LongCylinder_IsAPipeMeasuredFromTheMesh()
        {
            V3 a = new V3(10, 10, 5), b = new V3(10, 40, 4.7);
            ImportCandidate c = NavisInterpreter.Read(new NavisElement { Name = "IFC pipe", ClassName = "IfcPipeSegment", Triangles = Meshes.Cylinder(a, b, 0.2) });

            Assert.Equal("Geometry", c.Origin);
            Assert.Equal(ImportKind.Pipe, c.Action);
            Assert.Equal(0.4, c.OuterWidth, 2);
            Assert.Equal(ImportConfidence.Medium, c.Confidence);
            Assert.Contains(c.Notes, n => n.Contains("Inner size unknown"));
        }

        [Fact]
        public void GeometryOnly_VerticalShaft_IsAStructureNotAPipe()
        {
            ImportCandidate c = NavisInterpreter.Read(new NavisElement { Name = "Chamber", Triangles = Meshes.RoundChamber(10, 10, 95, 100, 1.2) });

            Assert.Equal(ImportKind.Structure, c.Action);
            Assert.True(c.StructCircular);
            Assert.Equal(1.2, c.StructDiameter, 2);
            Assert.Equal(100.0, c.Rim, 6);
            Assert.Equal(95.0, c.Sump, 6);
        }

        [Fact]
        public void NoGeometry_IsSkipped()
        {
            ImportCandidate c = NavisInterpreter.Read(new NavisElement { Name = "Empty" });
            Assert.Equal(ImportKind.Skip, c.Action);
        }

        [Theory]
        [InlineData("300.000mm", 0.300)]
        [InlineData("30 cm", 0.300)]
        [InlineData("0.3 m", 0.300)]
        [InlineData("12 in", 0.3048)]
        [InlineData("1 ft", 0.3048)]
        [InlineData("300", 0.300)]
        [InlineData("1,200 mm", 1.200)]
        public void CivilPipe_LengthTextInAnyUnit(string text, double metres)
        {
            V3 a = new V3(0, 0, 0), b = new V3(20, 0, 0);
            NavisElement e = CivilPipe(a, b, 0.6, 0.075, new double[0]);
            e.Props.RemoveAll(p => p.Name == "Inner Diameter or Width");
            e.Props.Add(Civil("Inner Diameter or Width", text));
            Assert.Equal(metres, NavisInterpreter.Read(e).InnerWidth, 6);
        }

        // ---------------- Curved pipes ----------------

        [Fact]
        public void Arc_QuarterCircle_TurnsTheShortWay()
        {
            // Centre (0,0), radius 10: from east (10,0) to north (0,10) is a quarter turn counter-clockwise.
            Assert.True(NavisInterpreter.Arc(new V3(10, 0, 100), new V3(0, 10, 99), 0, 0, 10, out V3 mid, out bool cw, out _));
            Assert.False(cw);
            Assert.Equal(10 / System.Math.Sqrt(2), mid.X, 6);
            Assert.Equal(10 / System.Math.Sqrt(2), mid.Y, 6);
            Assert.Equal(99.5, mid.Z, 6);
            // The other way round turns clockwise through the same middle.
            Assert.True(NavisInterpreter.Arc(new V3(0, 10, 99), new V3(10, 0, 100), 0, 0, 10, out V3 back, out bool cw2, out _));
            Assert.True(cw2);
            Assert.Equal(mid.X, back.X, 6);
        }

        [Fact]
        public void Arc_LongWay_GoesRoundTheOtherSide()
        {
            Assert.True(NavisInterpreter.Arc(new V3(10, 0, 0), new V3(0, 10, 0), 0, 0, 10, out V3 mid, out bool cw, out _, longWay: true));
            Assert.True(cw);
            Assert.Equal(-10 / System.Math.Sqrt(2), mid.X, 6);
            Assert.Equal(-10 / System.Math.Sqrt(2), mid.Y, 6);
        }

        [Fact]
        public void Arc_GentleCurve_KeepsItsRadius_WhenTheEndsAreAMillimetreOff()
        {
            // 100 m radius, 1 m of arc; the end is 1 mm further out than the start. The circle through the three points stays ~100 m.
            V3 a = new V3(100, 0, 0), b = new V3(100.001 * System.Math.Cos(0.01), 100.001 * System.Math.Sin(0.01), 0);
            Assert.True(NavisInterpreter.Arc(a, b, 0, 0, 100, out V3 m, out _, out _));
            double A = Dist(b, m), B = Dist(a, m), C = Dist(a, b);
            double area = System.Math.Abs((b.X - a.X) * (m.Y - a.Y) - (m.X - a.X) * (b.Y - a.Y)) / 2;
            Assert.InRange(A * B * C / (4 * area), 99, 101);
        }

        private static double Dist(V3 p, V3 q) => System.Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

        [Fact]
        public void CivilPipe_BentMoreThanHalfACircle_FollowsItsMesh()
        {
            V3 a = new V3(10, 0, 100), b = new V3(0, 10, 100);
            // Mesh points along the long way round (from 0° clockwise to 90°, through 180° and 270°).
            var pts = new List<double>();
            for (int k = 0; k <= 30; k++) { double t = -k * 1.5 * System.Math.PI / 30; pts.AddRange(new[] { 10 * System.Math.Cos(t), 10 * System.Math.Sin(t), 100.0 }); }
            NavisElement e = CivilPipe(a, b, 0.3, 0.05, pts.ToArray());
            e.Props.Add(Civil("Pipe Radius", "10m"));
            e.Props.Add(Civil("Radius Point Easting", "", 0));
            e.Props.Add(Civil("Radius Point Northing", "", 0));
            ImportCandidate c = NavisInterpreter.Read(e);
            Assert.True(c.IsCurved);
            Assert.True(c.CurveClockwise);
            Assert.True(c.CurveMid.X < 0 && c.CurveMid.Y < 0);
        }

        [Fact]
        public void Arc_EndsOffTheRadius_OrHalfCircle_AreRefused()
        {
            Assert.False(NavisInterpreter.Arc(new V3(10, 0, 0), new V3(0, 12, 0), 0, 0, 10, out _, out _, out string off));
            Assert.Contains("from the radius point", off);
            Assert.False(NavisInterpreter.Arc(new V3(10, 0, 0), new V3(-10, 0, 0), 0, 0, 10, out _, out _, out string half));
            Assert.Contains("half circle", half);
        }

        [Fact]
        public void CivilPipe_WithRadiusAndRadiusPoint_IsReadCurved()
        {
            V3 a = new V3(510, 200, 100), b = new V3(500, 210, 99.8);
            NavisElement e = CivilPipe(a, b, 0.3, 0.05, Meshes.Cylinder(a, b, 0.2));
            e.Props.Add(Civil("Pipe Radius", "10.000m"));
            e.Props.Add(Civil("Radius Point Easting", "", 500));
            e.Props.Add(Civil("Radius Point Northing", "", 200));
            ImportCandidate c = NavisInterpreter.Read(e);
            Assert.True(c.IsCurved);
            Assert.Equal(10, c.CurveRadius, 6);
            Assert.False(c.CurveClockwise);
            Assert.Contains(c.Notes, n => n.Contains("Curved pipe"));
        }

        [Fact]
        public void CivilPipe_WithBareRadius_ReadsMetres_AndZeroRadiusIsStraight()
        {
            V3 a = new V3(550, 200, 100), b = new V3(500, 250, 99);
            NavisElement e = CivilPipe(a, b, 0.3, 0.05, Meshes.Cylinder(a, b, 0.2));
            e.Props.Add(Civil("Pipe Radius", "50"));
            e.Props.Add(Civil("Radius Point East", "500"));
            e.Props.Add(Civil("Radius Point North", "200"));
            Assert.Equal(50, NavisInterpreter.Read(e).CurveRadius, 6);

            NavisElement straight = CivilPipe(a, b, 0.3, 0.05, Meshes.Cylinder(a, b, 0.2));
            straight.Props.Add(Civil("Pipe Radius", "0"));
            Assert.False(NavisInterpreter.Read(straight).IsCurved);
        }
    }
}
