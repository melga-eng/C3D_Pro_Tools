using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>Plan turn (degrees, unsigned) from the previous section's end direction to this one's start; 0 for the first.</summary>
        public double DeflectionIn;
        /// <summary>Plan turn (degrees, unsigned) along a curved section; 0 for straight ones.</summary>
        public double PlanSweep;

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
        /// <summary>Vertical clearance required to this pipe (m); NaN = the job's crossing clearance.</summary>
        public double Clearance = double.NaN;
        public UtilityClass Class;

        public double ClearanceOr(double flat) => double.IsNaN(Clearance) ? flat : Clearance;
    }

    /// <summary>A pipe running alongside the route closer than the horizontal clearance, without touching it in plan: reported, not cleared vertically.</summary>
    internal sealed class ProximityWarning
    {
        public string Name;
        public double S0, S1;
        /// <summary>Least edge-to-edge distance in plan (m) and the clearance asked for.</summary>
        public double MinEdgeDistance, Required;
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

        /// <summary>True when (x, y) lies inside the closed plan polygon (even-odd rule; the closing edge is implied).</summary>
        public static bool Inside(IReadOnlyList<(double X, double Y)> polygon, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
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
        public readonly List<ProximityWarning> Proximities = new List<ProximityWarning>();
        /// <summary>Stretches with their own minimum cover (e.g. under paving).</summary>
        public readonly List<CoverZone> Zones = new List<CoverZone>();
        public UtilityClass Class;
        public int MissingGround;
        public bool IsReference;

        public double Length => Sections.Count == 0 ? 0 : Sections[Sections.Count - 1].S1;

        /// <summary>The cover zone at station <paramref name="s"/> (the one asking most cover when zones overlap), or null.</summary>
        public CoverZone ZoneAt(double s)
        {
            CoverZone best = null;
            foreach (CoverZone z in Zones)
                if (s >= z.S0 - 1e-6 && s <= z.S1 + 1e-6 && (best == null || z.MinCover > best.MinCover)) best = z;
            return best;
        }

        /// <summary>The minimum cover at station <paramref name="s"/>: its zone's, else <paramref name="general"/>.</summary>
        public double MinCoverAt(double s, double general) => ZoneAt(s)?.MinCover ?? general;

        /// <summary>
        /// Adds encasement not drawn in the model (<paramref name="padding"/> on every side) to the route's outside, so cover is
        /// measured to its top and crossing clearances from its faces. Applied once, before the crossings are found.
        /// </summary>
        public void ApplyEncasement(double padding)
        {
            if (padding <= 0) return;
            foreach (RouteSection sec in Sections)
            {
                sec.OuterWidth = sec.PlanWidth + 2 * padding;
                sec.OuterHeight += 2 * padding;
            }
            foreach (RoutePipe p in Pipes) p.OuterHeight += 2 * padding;
        }

        /// <summary>
        /// Adds a cover zone for every stretch of route inside one of the plan polygons (closed polylines of paving, roads...).
        /// A zone runs from the last station before it to the first after it, so its edges are always inside.
        /// </summary>
        /// <param name="plan">Each record's plan position, in record order.</param>
        public void AddZones(IReadOnlyList<(double X, double Y)> plan, IEnumerable<IReadOnlyList<(double X, double Y)>> polygons, double minCover, string label)
        {
            foreach (IReadOnlyList<(double X, double Y)> polygon in polygons)
            {
                if (polygon.Count < 3) continue;
                int start = -1;
                for (int r = 0; r <= Records.Count; r++)
                {
                    bool inside = r < Records.Count && CrossingGeometry.Inside(polygon, plan[r].X, plan[r].Y);
                    if (inside && start < 0) start = r;
                    if (!inside && start >= 0)
                    {
                        Zones.Add(new CoverZone { S0 = Records[Math.Max(0, start - 1)].S, S1 = Records[Math.Min(Records.Count - 1, r)].S, MinCover = minCover, Label = label });
                        start = -1;
                    }
                }
            }
        }

        /// <summary>
        /// Structure-to-structure spans (pulls): runs of sections between manholes, with their length and plan turning
        /// (plain joints and curved sections; degrees).
        /// </summary>
        public List<(int first, int last, double length, double planTurn)> Spans()
        {
            List<(int, int, double, double)> spans = new List<(int, int, double, double)>();
            int start = 0;
            for (int i = 1; i <= Sections.Count; i++)
            {
                if (i < Sections.Count && !Sections[i].AfterManhole) continue;
                double turn = 0;
                for (int k = start; k < i; k++) turn += Sections[k].PlanSweep + (k > start ? Sections[k].DeflectionIn : 0);
                spans.Add((start, i - 1, Sections[i - 1].S1 - Sections[start].S0, turn));
                start = i;
            }
            return spans;
        }

        /// <summary>Offset from the pipe centreline to the point cover is measured to (positive = above the centreline).</summary>
        public static double ReferenceOffset(CoverReference reference, double innerHeight, double outerHeight) =>
            reference == CoverReference.OutsideTop ? outerHeight / 2.0 : reference == CoverReference.Invert ? -innerHeight / 2.0 : 0.0;

        /// <summary>
        /// Sorts out how another pipe lies against the route, station by station, over each unbroken stretch where its
        /// outside comes within <paramref name="horizontalClearance"/> of the route's outside in plan:
        /// <list type="bullet">
        /// <item>where the two outsides overlap in plan (or the centrelines cross), the route must clear it vertically: those
        /// stations take its levels (a few metres for a shallow-skew crossing, the full length of a pipe on top of it);</item>
        /// <item>a stretch where it only comes near, alongside, is reported as a <see cref="ProximityWarning"/> (or cleared
        /// vertically too with <paramref name="enforceOnParallel"/>).</item>
        /// </list>
        /// Stations <paramref name="excluded"/> says to skip (around the route's own structures, where a pipe meeting them is a
        /// branch) are left out. Each stretch is one crossing: the one found where the centrelines cross when there is one,
        /// else a new one from <paramref name="create"/> at the closest station.
        /// </summary>
        /// <param name="plan">Each record's plan position, in record order.</param>
        /// <param name="path">The other pipe's centreline.</param>
        /// <param name="halfWidth">Half the other pipe's outside width, encasement included.</param>
        /// <param name="ofThisPipe">Crossings of this pipe found so far; new ones are added to it and to <see cref="Crossings"/>.</param>
        internal void MarkCrossingBands(IReadOnlyList<(double X, double Y)> plan, IReadOnlyList<(double X, double Y, double Z)> path,
            double halfWidth, double halfHeight, double horizontalClearance, List<RouteCrossing> ofThisPipe, Func<RouteCrossing> create,
            bool enforceOnParallel = false, Func<int, bool> excluded = null, string name = null)
        {
            List<(int r, double z, double edge)> run = new List<(int, double, double)>();
            void Close()
            {
                if (run.Count == 0) return;
                double s0 = Records[run[0].r].S, s1 = Records[run[run.Count - 1].r].S;
                RouteCrossing c = ofThisPipe.Find(x => x.S >= s0 - 1e-6 && x.S <= s1 + 1e-6);
                bool touches = c != null || run.Exists(h => h.edge < 0);
                if (!touches && !enforceOnParallel)
                {
                    Proximities.Add(new ProximityWarning
                    {
                        Name = name ?? create().Name, S0 = s0, S1 = s1, MinEdgeDistance = Math.Max(0, run.Min(h => h.edge)), Required = horizontalClearance
                    });
                    run.Clear();
                    return;
                }
                if (c == null)
                {
                    (int r, double z, double edge) closest = run[0];
                    foreach (var hit in run) if (hit.edge < closest.edge) closest = hit;
                    c = create();
                    c.S = Records[closest.r].S;
                    c.Top = closest.z + halfHeight;
                    c.Bottom = closest.z - halfHeight;
                    Crossings.Add(c);
                    ofThisPipe.Add(c);
                }
                // Vertical clearance only where the outsides overlap in plan, unless asked for along the whole stretch.
                foreach ((int r, double z, double edge) in run)
                    if (enforceOnParallel || edge < 0 || Math.Abs(Records[r].S - c.S) <= 1e-6) Records[r].AddCrossing(c, z + halfHeight, z - halfHeight);
                run.Clear();
            }

            for (int r = 0; r < Records.Count; r++)
            {
                if (excluded != null && excluded(r)) { Close(); continue; }
                double touch = Sections[Records[r].Section].PlanWidth / 2.0 + halfWidth;
                (double d, double z, double fraction) = CrossingGeometry.Closest(path, plan[r].X, plan[r].Y);
                // A pipe whose end is what lies near the route (a branch meeting it) is not a crossing there.
                // edge < 0: the outsides overlap in plan.
                if (d < touch + horizontalClearance && fraction > 0.01 && fraction < 0.99) run.Add((r, z, d - touch));
                else Close();
            }
            Close();
        }
    }
}
