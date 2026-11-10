using System;

// Plain 2D values for contracts: model plan coordinates (metres) or paper coordinates (millimetres). Each field that carries one
// says which, by its name (…M or …Mm). No Autodesk types. Named Rect2, not Box2: the root namespace already has a Box2
// (AnnotSheets.cs), and code in a C3DProTools.* namespace would find that one first.
namespace C3DProTools.Features.Shared.Contracts
{
    /// <summary>A point in a plane: model plan (X east, Y north) or paper, as the field holding it says.</summary>
    internal readonly record struct Point2(double X, double Y)
    {
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

        public double DistanceTo(Point2 other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    }

    /// <summary>An axis-aligned rectangle, in the units of the field holding it.</summary>
    internal readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        /// <summary>Finite, and not inverted (a zero-width rectangle is valid).</summary>
        public bool IsValid => double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY)
                               && MaxX >= MinX && MaxY >= MinY;

        /// <summary>True when the point lies inside or on the edge.</summary>
        public bool Contains(Point2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    }
}
