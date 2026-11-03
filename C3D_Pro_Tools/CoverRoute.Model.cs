using System;
using System.Collections.Generic;

// The route model the cover solver works on: stations, levels, sizes and crossings only. No AutoCAD / Civil 3D types here,
// so CoverSolver and CoverEvaluation can be built and tested without Civil 3D. The drawing side of these classes
// (object ids, plan points, reading a route from the drawing) is in CoverRoute.cs.
namespace C3DProTools
{
    internal enum CoverReference { OutsideTop, Centerline, Invert }

    /// <summary>An original pipe on the route. Elevations are centreline values at its entry / exit in route order.</summary>
    internal sealed partial class RoutePipe
    {
        public string Name;
        /// <summary>True when the route travels the pipe from its start to its end.</summary>
        public bool Forward;
        public bool Curved;
        public int Section;
        public double S0, S1, Z0, Z1;
        public double InnerHeight, OuterHeight;
    }

    /// <summary>
    /// A stretch the optimiser may re-split freely: straight in plan, one pipe size, no structure inside. A curved pipe is
    /// a section of its own that keeps its plan geometry (only its two end levels change).
    /// </summary>
    internal sealed partial class RouteSection
    {
        public int Index;
        public bool Curved;
        public double S0, S1;
        public double InnerHeight, OuterHeight;
        /// <summary>Outside width in plan (wall or encasement included); 0 when not known, and the outer height is used.</summary>
        public double OuterWidth;
        public readonly List<RoutePipe> Pipes = new List<RoutePipe>();
        public int FirstRecord, LastRecord;
        /// <summary>True when the previous section ends at the same structure this one starts from (a manhole between them).</summary>
        public bool AfterManhole;

        public double Length => S1 - S0;
        public RoutePipe Template => Pipes[0];
        public double PlanWidth => OuterWidth > 0 ? OuterWidth : OuterHeight;
    }

    /// <summary>A station on the route where the ground is sampled (and, inside straight sections, a pipe may break).</summary>
    internal sealed partial class RouteRecord
    {
        public double S;
        public double Ground;
        public int Section;
        public bool CanBreak;
        /// <summary>The other pipes this station must clear vertically, each with its outside top and bottom here.</summary>
        public readonly List<CrossingLevel> Crossings = new List<CrossingLevel>();

        /// <summary>Adds a crossing pipe's levels at this station (once per crossing).</summary>
        public void AddCrossing(RouteCrossing crossing, double top, double bottom)
        {
            if (!Crossings.Exists(l => l.Crossing == crossing)) Crossings.Add(new CrossingLevel(crossing, top, bottom));
        }
    }

    /// <summary>
    /// A pipe of another route or network crossing this one in plan, or running so close alongside it that their outsides
    /// overlap in plan; the route must clear it vertically wherever that is so.
    /// </summary>
    internal sealed partial class RouteCrossing
    {
        public string Name;
        /// <summary>The station where the pipes cross, or where they are closest in plan.</summary>
        public double S;
        /// <summary>Outside top and bottom of the crossing pipe at <see cref="S"/>.</summary>
        public double Top, Bottom;
        /// <summary>Set by the solver: whether the route was taken under (true) or over the crossing pipe, the same along its whole width.</summary>
        public bool PassBelow = true;
    }

    /// <summary>A crossing pipe's outside top and bottom at one station of the route.</summary>
    internal readonly struct CrossingLevel
    {
        public readonly RouteCrossing Crossing;
        public readonly double Top, Bottom;

        public CrossingLevel(RouteCrossing crossing, double top, double bottom)
        {
            Crossing = crossing;
            Top = top;
            Bottom = bottom;
        }
    }

    /// <summary>Plan geometry for finding crossings, on plain coordinates so it can be tested without Civil 3D.</summary>
    internal static class CrossingGeometry
    {
        /// <summary>
        /// The point of a 3D polyline nearest to (x, y) in plan: the plan distance, the polyline's level there, and how far
        /// along the polyline it is (0 = its start, 1 = its end, by plan length).
        /// </summary>
        public static (double distance, double z, double fraction) Closest(IReadOnlyList<(double X, double Y, double Z)> path, double x, double y)
        {
            if (path.Count == 1) return (Math.Sqrt((path[0].X - x) * (path[0].X - x) + (path[0].Y - y) * (path[0].Y - y)), path[0].Z, 0);
            double total = 0;
            for (int j = 1; j < path.Count; j++) total += Math.Sqrt((path[j].X - path[j - 1].X) * (path[j].X - path[j - 1].X) + (path[j].Y - path[j - 1].Y) * (path[j].Y - path[j - 1].Y));
            double best = double.MaxValue, bestZ = double.NaN, bestFraction = 0, along = 0;
            for (int j = 1; j < path.Count; j++)
            {
                var a = path[j - 1];
                var b = path[j];
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / len2));
                double px = a.X + dx * t - x, py = a.Y + dy * t - y, d = Math.Sqrt(px * px + py * py);
                if (d < best)
                {
                    best = d;
                    bestZ = a.Z + (b.Z - a.Z) * t;
                    bestFraction = (along + t * len) / Math.Max(1e-9, total);
                }
                along += len;
            }
            return (best, bestZ, bestFraction);
        }

        /// <summary>Plan intersection of segments a0-a1 and b0-b1, with the fraction along each.</summary>
        public static bool Intersect(double a0x, double a0y, double a1x, double a1y, double b0x, double b0y, double b1x, double b1y, out double ta, out double tb)
        {
            ta = tb = 0;
            double rx = a1x - a0x, ry = a1y - a0y, sx = b1x - b0x, sy = b1y - b0y;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-12) return false;
            double qx = b0x - a0x, qy = b0y - a0y;
            ta = (qx * sy - qy * sx) / den;
            tb = (qx * ry - qy * rx) / den;
            return ta >= 0 && ta <= 1 && tb >= 0 && tb <= 1;
        }
    }

    internal sealed partial class CoverRoute
    {
        public readonly List<RoutePipe> Pipes = new List<RoutePipe>();
        public readonly List<RouteSection> Sections = new List<RouteSection>();
        public readonly List<RouteRecord> Records = new List<RouteRecord>();
        public readonly List<RouteCrossing> Crossings = new List<RouteCrossing>();
        public int MissingGround;
        public bool IsReference;

        public double Length => Sections.Count == 0 ? 0 : Sections[Sections.Count - 1].S1;

        /// <summary>Offset from the pipe centreline to the point cover is measured to (positive = above the centreline).</summary>
        public static double ReferenceOffset(CoverReference reference, double innerHeight, double outerHeight) =>
            reference == CoverReference.OutsideTop ? outerHeight / 2.0 : reference == CoverReference.Invert ? -innerHeight / 2.0 : 0.0;

        /// <summary>
        /// Gives every station where another pipe's outside comes within <paramref name="horizontalClearance"/> of the route's
        /// outside in plan that pipe's levels there, so the route clears it vertically along the whole stretch: a few metres
        /// for a shallow-skew crossing, the full length for a pipe running alongside. Each unbroken stretch is one crossing,
        /// the one already found where the centrelines cross when there is one, else a new one from <paramref name="create"/>
        /// at the closest station.
        /// </summary>
        /// <param name="plan">Each record's plan position, in record order.</param>
        /// <param name="path">The other pipe's centreline.</param>
        /// <param name="ofThisPipe">Crossings of this pipe found so far; new ones are added to it and to <see cref="Crossings"/>.</param>
        internal void MarkCrossingBands(IReadOnlyList<(double X, double Y)> plan, IReadOnlyList<(double X, double Y, double Z)> path,
            double halfWidth, double halfHeight, double horizontalClearance, List<RouteCrossing> ofThisPipe, Func<RouteCrossing> create)
        {
            List<(int r, double z, double d)> run = new List<(int, double, double)>();
            void Close()
            {
                if (run.Count == 0) return;
                double s0 = Records[run[0].r].S, s1 = Records[run[run.Count - 1].r].S;
                RouteCrossing c = ofThisPipe.Find(x => x.S >= s0 - 1e-6 && x.S <= s1 + 1e-6);
                if (c == null)
                {
                    (int r, double z, double d) closest = run[0];
                    foreach (var hit in run) if (hit.d < closest.d) closest = hit;
                    c = create();
                    c.S = Records[closest.r].S;
                    c.Top = closest.z + halfHeight;
                    c.Bottom = closest.z - halfHeight;
                    Crossings.Add(c);
                    ofThisPipe.Add(c);
                }
                foreach ((int r, double z, double _) in run) Records[r].AddCrossing(c, z + halfHeight, z - halfHeight);
                run.Clear();
            }

            for (int r = 0; r < Records.Count; r++)
            {
                double reach = Sections[Records[r].Section].PlanWidth / 2.0 + halfWidth + horizontalClearance;
                (double d, double z, double fraction) = CrossingGeometry.Closest(path, plan[r].X, plan[r].Y);
                // A pipe whose end is what lies near the route (a branch meeting it) is not a crossing there.
                if (d < reach && fraction > 0.01 && fraction < 0.99) run.Add((r, z, d));
                else Close();
            }
            Close();
        }
    }
}
