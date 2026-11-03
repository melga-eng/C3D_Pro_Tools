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
    }
}
