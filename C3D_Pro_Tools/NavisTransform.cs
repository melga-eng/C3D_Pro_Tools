using System;

namespace C3DProTools
{
    /// <summary>
    /// A shift and turn from Navisworks coordinates to the drawing's (both in metres): turned about the origin by
    /// <see cref="RotationDeg"/> (counter-clockwise), then shifted by (<see cref="Dx"/>, <see cref="Dy"/>, <see cref="Dz"/>).
    /// For models placed on a local or shared grid. No AutoCAD types, so it is tested on its own.
    /// </summary>
    internal sealed class NavisTransform
    {
        public double Dx, Dy, Dz, RotationDeg;

        public bool IsIdentity => Math.Abs(Dx) < 1e-12 && Math.Abs(Dy) < 1e-12 && Math.Abs(Dz) < 1e-12 && Math.Abs(RotationDeg) < 1e-12;

        private double Angle => RotationDeg * Math.PI / 180.0;

        public V3 Point(V3 p)
        {
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return new V3(p.X * c - p.Y * s + Dx, p.X * s + p.Y * c + Dy, p.Z + Dz);
        }

        /// <summary>A direction: turned only.</summary>
        public V3 Direction(V3 v)
        {
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return new V3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        /// <summary>
        /// Moves a read item into the drawing's coordinates: its ends and barrel directions, a structure's position, turn and
        /// levels, and its mesh and box (the exact solid is built from them). Apply once, right after reading.
        /// </summary>
        public void Apply(ImportCandidate c)
        {
            if (IsIdentity) return;
            c.Start = Point(c.Start);
            c.End = Point(c.End);
            c.BarrelAxis = Direction(c.BarrelAxis);
            c.BarrelUp = Direction(c.BarrelUp);
            V3 at = Point(new V3(c.X, c.Y, 0));
            c.X = at.X;
            c.Y = at.Y;
            c.Rotation += Angle;
            c.Rim += Dz;
            c.Sump += Dz;
            c.Bottom += Dz;
            if (c.Footprint != null)
            {
                V3 f = Point(new V3(c.Footprint.CenterX, c.Footprint.CenterY, 0));
                c.Footprint.CenterX = f.X;
                c.Footprint.CenterY = f.Y;
                c.Footprint.Rotation += Angle;
                c.Footprint.Top += Dz;
                c.Footprint.Bottom += Dz;
            }
            if (c.Fit != null)
            {
                c.Fit.Start = Point(c.Fit.Start);
                c.Fit.End = Point(c.Fit.End);
                c.Fit.U = Direction(c.Fit.U);
                c.Fit.W = Direction(c.Fit.W);
            }
            if (c.Source == null) return;
            double[] t = c.Source.Triangles ?? new double[0];
            double[] moved = new double[t.Length];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                V3 p = Point(new V3(t[i], t[i + 1], t[i + 2]));
                moved[i] = p.X; moved[i + 1] = p.Y; moved[i + 2] = p.Z;
            }
            c.Source.Triangles = moved;
            if (c.Source.Box != null && c.Source.Box.Length == 6)
            {
                double[] b = c.Source.Box;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { b[0], b[3] })
                    foreach (double y in new[] { b[1], b[4] })
                    {
                        V3 p = Point(new V3(x, y, 0));
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }
                c.Source.Box = new[] { minX, minY, b[2] + Dz, maxX, maxY, b[5] + Dz };
            }
        }
    }
}
