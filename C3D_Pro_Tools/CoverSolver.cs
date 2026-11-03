using System;
using System.Collections.Generic;

namespace C3DProTools
{
    internal enum CoverMode
    {
        /// <summary>Cables / ducts: pipes may rise and fall with the ground; levels may jump freely at manholes / pits.</summary>
        Dry,
        /// <summary>Gravity: continuous fall along the route (oriented in the flow direction), limited drops at manholes, new manholes at new breaks.</summary>
        Gravity
    }

    internal sealed class CoverOptions
    {
        public CoverMode Mode;
        public CoverReference Reference;
        public double MinCover, MaxCover, MinLength;
        /// <summary>Slopes as fractions (0.003 = 0.3%). MaxSlopeChange 0 = no limit.</summary>
        public double MinSlope, MaxSlope, MaxSlopeChange;
        public double MaxDrop;
        public bool StartFixed, EndFixed;
        /// <summary>Between layouts with equally few pipes: true = smallest slope changes at joints, false = shallowest.</summary>
        public bool PreferGentleBends;
        /// <summary>Longest pipe allowed (0 = no limit), e.g. a stock length or a manhole spacing rule on gravity networks.</summary>
        public double MaxLength;
        /// <summary>Vertical clear distance kept to pipes crossing the route (outside to outside); 0 = crossings are ignored.</summary>
        public double CrossingClearance;
        /// <summary>
        /// Per route, set by the caller: lowest centreline level allowed at the route's last station and highest at its first
        /// (NaN = none). A gravity branch must not arrive below the main line it joins.
        /// </summary>
        public double EndMinZ = double.NaN, EndMaxZ = double.NaN, StartMaxZ = double.NaN;

        public CoverOptions ForRoute(double endMinZ, double startMaxZ)
        {
            CoverOptions o = (CoverOptions)MemberwiseClone();
            o.EndMinZ = endMinZ;
            o.StartMaxZ = startMaxZ;
            return o;
        }
    }

    /// <summary>One pipe of a profile along the route: centreline levels at its two stations, inside one section.</summary>
    internal sealed class ProfilePipe
    {
        public int Section;
        public double S0, S1, Z0, Z1;
        public string Name;
        /// <summary>The original pipe this one replaces / copies (its size, style and layer).</summary>
        public RoutePipe Source;

        public double Length => S1 - S0;
        public double Slope => Length > 1e-9 ? (Z1 - Z0) / Length : 0.0;
        public double ZAt(double s) => Length > 1e-9 ? Z0 + (Z1 - Z0) * (s - S0) / Length : Z0;
    }

    internal sealed class CoverSolution
    {
        public List<ProfilePipe> Pipes = new List<ProfilePipe>();
        /// <summary>The most the cover limits had to be widened anywhere to find a layout (0 = all limits met).</summary>
        public double Relaxation;
    }

    /// <summary>
    /// Finds the layout with the fewest pipes that keeps the cover between the limits along every pipe's whole length,
    /// respecting minimum pipe length, slope limits, slope change at joints and manhole drops; among layouts with
    /// equally few pipes it takes the shallowest. Levels are searched on a grid of <see cref="ElevationStep"/>.
    /// <para>
    /// When no layout meets the limits, they are widened only around the station where the search gets stuck, deeper
    /// first (more cover is the safer breach) and shallower only once extra depth alone has not helped, so pipes elsewhere
    /// keep the full limits. Its pipes then show as failing in the report.
    /// </para>
    /// </summary>
    internal sealed class CoverSolver
    {
        public const double ElevationStep = 0.025;
        private const double Eps = 1e-9;
        // Local widening when no layout exists: first step and cap. Extra depth is tried up to the cap before any
        // shallower level is allowed.
        private const double RelaxStep = 0.05, MaxRelax = 10.0;
        private const int MaxIterations = 400;
        // With a slope-change limit, incoming slopes closer than (limit / this) share one search state; without a limit,
        // "gentlest bends" groups them in steps of GentleBucket.
        private const int SlopeBucketsPerLimit = 2;
        private const double GentleBucket = 0.01;
        // "Gentlest bends" compares the sharpest bend so far in steps of this (0.05%); closer bends are decided by depth.
        private const double BendResolution = 0.0005;
        // A state key holds the record (high bits), the level above the record's lowest (20 bits) and the slope bucket
        // (20 bits): a million levels and buckets, so fine slope-change limits with steep slopes never share a bucket.
        private const int KeyBits = 20;
        private const int NoSlopeBucket = (1 << KeyBits) - 1;

        private readonly CoverRoute _route;
        private readonly CoverOptions _o;
        private readonly double _zStart, _zEnd, _zRef, _bucket;
        // Breaks are allowed on every n-th station (cover is still checked at every one): about a tenth of the minimum length.
        private readonly int _breakEvery;
        private readonly double[] _relaxLo, _relaxHi;

        // Per record: allowed level index range, limits and depth prefix sums for the current pass.
        private int[] _kLo, _kHi;
        private double[] _lo, _hi, _prefix;
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private int _furthest;

        /// <summary>A search state: a level at a station, reached with an incoming slope (bucketed).</summary>
        private sealed class Entry
        {
            public int R, K, Layer;
            /// <summary>Cost = depth below the shallowest allowed level; Bend = sharpest slope change at a joint so far.</summary>
            public double Cost, InSlope, Bend;
            public long Key, Parent;
            public bool Queued;
        }

        public CoverSolver(CoverRoute route, CoverOptions options)
        {
            _route = route;
            _o = options;
            _zStart = route.Pipes[0].Z0;
            _zEnd = route.Pipes[route.Pipes.Count - 1].Z1;
            // With a fixed start the grid passes exactly through its level.
            _zRef = options.StartFixed ? _zStart : Math.Floor(_zStart);
            _bucket = options.MaxSlopeChange > 0 ? options.MaxSlopeChange / SlopeBucketsPerLimit
                    : options.PreferGentleBends ? GentleBucket : double.PositiveInfinity;
            _breakEvery = Math.Max(1, (int)Math.Round(options.MinLength / 10.0));
            _relaxLo = new double[route.Records.Count];
            _relaxHi = new double[route.Records.Count];

            // Each crossing is passed under when that stays within the cover limits (deeper is the usual rule), else over,
            // and under again when neither fits. It is decided once, at the station where the pipes cross (or are closest),
            // and holds along the crossing's whole width.
            Dictionary<RouteCrossing, double> decidedAt = new Dictionary<RouteCrossing, double>();
            foreach (RouteRecord rec in route.Records)
            {
                foreach (CrossingLevel level in rec.Crossings)
                {
                    RouteCrossing c = level.Crossing;
                    double distance = Math.Abs(rec.S - c.S);
                    if (decidedAt.TryGetValue(c, out double best) && best <= distance) continue;
                    decidedAt[c] = distance;
                    RouteSection sec = route.Sections[rec.Section];
                    double off = CoverRoute.ReferenceOffset(options.Reference, sec.InnerHeight, sec.OuterHeight);
                    double lo = rec.Ground - options.MaxCover - off, hi = rec.Ground - options.MinCover - off;
                    double belowHi = level.Bottom - options.CrossingClearance - sec.OuterHeight / 2.0;
                    double aboveLo = level.Top + options.CrossingClearance + sec.OuterHeight / 2.0;
                    c.PassBelow = belowHi >= lo || aboveLo > hi;
                }
            }
        }

        public CoverSolution Solve()
        {
            int n = _route.Records.Count;
            double radius = 2 * Math.Max(_o.MinLength, 10.0);
            int bestFurthest = -1, stuck = 0;

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                CoverSolution solution = SolveOnce();
                if (solution != null)
                {
                    // Report how far the layout really goes past the cover limits, not how far the search was allowed to widen them.
                    for (int r = 0; r < n; r++)
                    {
                        if (_relaxLo[r] <= 0 && _relaxHi[r] <= 0) continue;
                        RouteRecord rec = _route.Records[r];
                        ProfilePipe p = solution.Pipes.Find(x => x.Section == rec.Section && rec.S >= x.S0 - 1e-6 && rec.S <= x.S1 + 1e-6);
                        if (p == null) continue;
                        RouteSection sec = _route.Sections[rec.Section];
                        double off = CoverRoute.ReferenceOffset(_o.Reference, sec.InnerHeight, sec.OuterHeight);
                        double z = p.ZAt(rec.S);
                        double excess = Math.Max((rec.Ground - _o.MaxCover - off) - z, z - (rec.Ground - _o.MinCover - off));
                        solution.Relaxation = Math.Max(solution.Relaxation, excess);
                    }
                    return solution;
                }

                // Widen the limits around the first station the search could not reach; spread wider if that does not help.
                if (_furthest > bestFurthest) { bestFurthest = _furthest; stuck = 0; }
                else if (++stuck % 20 == 0) radius *= 2;

                double center = _route.Records[Math.Min(n - 1, _furthest + 1)].S;
                bool widened = false;
                for (int r = 0; r < n; r++)
                {
                    if (Math.Abs(_route.Records[r].S - center) > radius) continue;
                    if (_relaxLo[r] < MaxRelax)
                    {
                        _relaxLo[r] = Math.Min(MaxRelax, Math.Max(_relaxLo[r] + RelaxStep, _relaxLo[r] * 1.5));
                        widened = true;
                    }
                    else if (_relaxHi[r] < MaxRelax)
                    {
                        _relaxHi[r] = Math.Min(MaxRelax, _relaxHi[r] + RelaxStep);
                        widened = true;
                    }
                }
                if (!widened) return null;
            }
            return null;
        }

        private double Z(int k) => _zRef + k * ElevationStep;

        internal int Bucket(double slope)
        {
            if (double.IsNaN(slope)) return NoSlopeBucket;
            if (double.IsPositiveInfinity(_bucket)) return 0;
            // Clamped as a double: a huge quotient cast to int first would wrap round to a negative bucket.
            return (int)Math.Max(0.0, Math.Min(NoSlopeBucket - 1, Math.Floor((slope + _o.MaxSlope) / _bucket)));
        }

        private long KeyOf(int r, int k, int bucket) => ((long)r << (2 * KeyBits)) | ((long)(k - _kLo[r]) << KeyBits) | (long)bucket;

        private CoverSolution SolveOnce()
        {
            int n = _route.Records.Count;
            _kLo = new int[n]; _kHi = new int[n]; _lo = new double[n]; _hi = new double[n]; _prefix = new double[n];
            _entries.Clear();
            _furthest = 0;

            for (int r = 0; r < n; r++)
            {
                RouteRecord rec = _route.Records[r];
                RouteSection sec = _route.Sections[rec.Section];
                double off = CoverRoute.ReferenceOffset(_o.Reference, sec.InnerHeight, sec.OuterHeight);
                double lo = rec.Ground - _o.MaxCover - off - _relaxLo[r];
                double hi = rec.Ground - _o.MinCover - off + _relaxHi[r];
                // Crossing pipes are hard limits: the cover limits may be widened, the clearance never.
                if (_o.CrossingClearance > 0)
                {
                    foreach (CrossingLevel level in rec.Crossings)
                    {
                        if (level.Crossing.PassBelow) hi = Math.Min(hi, level.Bottom - _o.CrossingClearance - sec.OuterHeight / 2.0);
                        else lo = Math.Max(lo, level.Top + _o.CrossingClearance + sec.OuterHeight / 2.0);
                    }
                }
                if (r == n - 1 && !double.IsNaN(_o.EndMinZ)) lo = Math.Max(lo, _o.EndMinZ);
                if (r == n - 1 && !double.IsNaN(_o.EndMaxZ)) hi = Math.Min(hi, _o.EndMaxZ);
                if (r == 0 && !double.IsNaN(_o.StartMaxZ)) hi = Math.Min(hi, _o.StartMaxZ);
                if (r == 0 && _o.StartFixed) { lo = Math.Min(lo, _zStart); hi = Math.Max(hi, _zStart); }
                if (r == n - 1 && _o.EndFixed) { lo = Math.Min(lo, _zEnd); hi = Math.Max(hi, _zEnd); }
                _lo[r] = lo; _hi[r] = hi;
                _kLo[r] = (int)Math.Ceiling((lo - _zRef) / ElevationStep - Eps);
                _kHi[r] = (int)Math.Floor((hi - _zRef) / ElevationStep + Eps);
                if (r == 0 && _o.StartFixed) _kLo[r] = _kHi[r] = 0;
                if (r == n - 1 && _o.EndFixed) _kLo[r] = _kHi[r] = (int)Math.Round((_zEnd - _zRef) / ElevationStep);

                // Depth below the shallowest allowed level (unwidened), integrated per section: the tie-break between
                // layouts with equally few pipes.
                _prefix[r] = r == sec.FirstRecord ? 0.0
                    : _prefix[r - 1] + (HiUnrelaxed(r) + HiUnrelaxed(r - 1)) / 2.0 * (rec.S - _route.Records[r - 1].S);
            }

            List<Entry> frontier = new List<Entry>();
            for (int k = _kLo[0]; k <= _kHi[0]; k++) Mark(0, k, 0, 0.0, 0.0, -1, double.NaN, frontier);

            for (int layer = 0; frontier.Count > 0 && layer <= n; layer++)
            {
                // Zero-cost moves across section boundaries (plain joints and manholes) join the same layer.
                for (int i = 0; i < frontier.Count; i++) CrossBoundary(frontier[i], layer, frontier);

                Entry best = null;
                foreach (Entry e in frontier)
                {
                    if (e.R == n - 1 && (best == null || IsBetter(e.Bend, e.Cost, best.Bend, best.Cost))) best = e;
                }
                if (best != null) return Reconstruct(best);

                List<Entry> next = new List<Entry>();
                foreach (Entry e in frontier)
                {
                    e.Queued = false;
                    if (e.R != _route.Sections[_route.Records[e.R].Section].LastRecord) Expand(e, layer + 1, next);
                }
                frontier = next;
            }
            return null;
        }

        private double HiUnrelaxed(int r)
        {
            RouteRecord rec = _route.Records[r];
            RouteSection sec = _route.Sections[rec.Section];
            return rec.Ground - _o.MinCover - CoverRoute.ReferenceOffset(_o.Reference, sec.InnerHeight, sec.OuterHeight);
        }

        /// <summary>Order between layouts with equally few pipes: gentlest sharpest bend first (if chosen), then shallowest.</summary>
        private bool IsBetter(double bend, double cost, double otherBend, double otherCost)
        {
            if (_o.PreferGentleBends)
            {
                long a = (long)Math.Round(bend / BendResolution), b = (long)Math.Round(otherBend / BendResolution);
                if (a != b) return a < b;
            }
            return cost < otherCost;
        }

        private void Mark(int r, int k, int layer, double cost, double bend, long parent, double inSlope, List<Entry> list)
        {
            if (k < _kLo[r] || k > _kHi[r]) return;
            long key = KeyOf(r, k, Bucket(inSlope));
            if (_entries.TryGetValue(key, out Entry e))
            {
                if (e.Layer < layer || !IsBetter(bend, cost, e.Bend, e.Cost)) return;
            }
            else
            {
                e = new Entry { R = r, K = k, Key = key };
                _entries[key] = e;
            }
            e.Layer = layer;
            e.Cost = cost;
            e.Bend = bend;
            e.Parent = parent;
            e.InSlope = inSlope;
            if (r > _furthest) _furthest = r;
            if (!e.Queued)
            {
                e.Queued = true;
                list.Add(e);
            }
        }

        private void CrossBoundary(Entry e, int layer, List<Entry> list)
        {
            RouteSection sec = _route.Sections[_route.Records[e.R].Section];
            if (e.R != sec.LastRecord || sec.Index == _route.Sections.Count - 1) return;
            RouteSection nextSec = _route.Sections[sec.Index + 1];
            int r2 = nextSec.FirstRecord;

            if (!nextSec.AfterManhole)
            {
                // Plain joint: both pipes meet at the same centreline level, and the slope change rule still applies.
                Mark(r2, e.K, layer, e.Cost, e.Bend, e.Key, e.InSlope, list);
                return;
            }

            int k2Lo = _kLo[r2], k2Hi = _kHi[r2];
            if (_o.Mode == CoverMode.Gravity)
            {
                // Invert out must not be above invert in, and the drop is limited.
                double invertIn = Z(e.K) - sec.InnerHeight / 2.0;
                double zOutMax = invertIn + nextSec.InnerHeight / 2.0;
                double zOutMin = zOutMax - _o.MaxDrop;
                k2Lo = Math.Max(k2Lo, (int)Math.Ceiling((zOutMin - _zRef) / ElevationStep - Eps));
                k2Hi = Math.Min(k2Hi, (int)Math.Floor((zOutMax - _zRef) / ElevationStep + Eps));
            }
            for (int k2 = k2Lo; k2 <= k2Hi; k2++) Mark(r2, k2, layer, e.Cost, e.Bend, e.Key, double.NaN, list);
        }

        private void Expand(Entry from, int layer, List<Entry> list)
        {
            int r = from.R;
            RouteSection sec = _route.Sections[_route.Records[r].Section];
            if (sec.Curved && r != sec.FirstRecord) return;
            bool shortSection = sec.Length < _o.MinLength - 1e-6;
            if (shortSection && r != sec.FirstRecord) return;

            double z = Z(from.K);
            double s0 = _route.Records[r].S;
            List<(double lo, double hi)> slopes = SlopeIntervals(from.InSlope);
            double bandLo = double.NegativeInfinity, bandHi = double.PositiveInfinity;

            for (int r2 = r + 1; r2 <= sec.LastRecord; r2++)
            {
                double L = _route.Records[r2].S - s0;
                if (L <= 1e-9) continue;
                // A pipe longer than the limit is never made; curved and short sections keep their length.
                if (_o.MaxLength > 0 && L > _o.MaxLength + 1e-6 && !sec.Curved && !shortSection) break;
                bandLo = Math.Max(bandLo, (_lo[r2] - z) / L);
                bandHi = Math.Min(bandHi, (_hi[r2] - z) / L);
                if (bandLo > bandHi + Eps) break;

                bool isEnd = r2 == sec.LastRecord;
                if (!isEnd && (sec.Curved || shortSection || !_route.Records[r2].CanBreak || (r2 - sec.FirstRecord) % _breakEvery != 0)) continue;
                // Curved pipes and sections shorter than the minimum keep their length; elsewhere every pipe meets the
                // minimum, and a break must leave room for at least one more minimum-length pipe.
                if (!sec.Curved && !shortSection)
                {
                    if (L < _o.MinLength - 1e-6) continue;
                    if (!isEnd && sec.S1 - _route.Records[r2].S < _o.MinLength - 1e-6) continue;
                }

                foreach ((double sLo, double sHi) in slopes)
                {
                    double mLo = Math.Max(sLo, bandLo), mHi = Math.Min(sHi, bandHi);
                    if (mLo > mHi + Eps) continue;
                    int kA = Math.Max(_kLo[r2], (int)Math.Ceiling((z + mLo * L - _zRef) / ElevationStep - Eps));
                    int kB = Math.Min(_kHi[r2], (int)Math.Floor((z + mHi * L - _zRef) / ElevationStep + Eps));
                    for (int k2 = kA; k2 <= kB; k2++)
                    {
                        double z2 = Z(k2);
                        double slope = (z2 - z) / L;
                        double cost = from.Cost + (_prefix[r2] - _prefix[r]) - (z + z2) / 2.0 * L;
                        double bend = double.IsNaN(from.InSlope) ? from.Bend : Math.Max(from.Bend, Math.Abs(slope - from.InSlope));
                        Mark(r2, k2, layer, cost, bend, from.Key, slope, list);
                    }
                }
            }
        }

        private List<(double, double)> SlopeIntervals(double inSlope)
        {
            List<(double lo, double hi)> intervals = new List<(double, double)>();
            if (_o.Mode == CoverMode.Gravity)
            {
                // The route runs in the flow direction, so the pipes fall.
                intervals.Add((-_o.MaxSlope, -_o.MinSlope));
            }
            else if (_o.MinSlope <= 0)
            {
                intervals.Add((-_o.MaxSlope, _o.MaxSlope));
            }
            else
            {
                intervals.Add((-_o.MaxSlope, -_o.MinSlope));
                intervals.Add((_o.MinSlope, _o.MaxSlope));
            }

            if (_o.MaxSlopeChange > 0 && !double.IsNaN(inSlope))
            {
                for (int i = 0; i < intervals.Count; i++)
                    intervals[i] = (Math.Max(intervals[i].lo, inSlope - _o.MaxSlopeChange), Math.Min(intervals[i].hi, inSlope + _o.MaxSlopeChange));
            }
            return intervals;
        }

        private CoverSolution Reconstruct(Entry goal)
        {
            // Walk back from the goal: a parent in the same section is a pipe; one in the previous section is a boundary crossing.
            List<Entry> chain = new List<Entry>();
            for (Entry e = goal; e != null; e = e.Parent < 0 ? null : _entries[e.Parent]) chain.Add(e);
            chain.Reverse();

            CoverSolution solution = new CoverSolution();
            for (int i = 1; i < chain.Count; i++)
            {
                Entry a = chain[i - 1], b = chain[i];
                int secA = _route.Records[a.R].Section;
                if (secA != _route.Records[b.R].Section) continue;

                RouteSection sec = _route.Sections[secA];
                double sA = _route.Records[a.R].S, sB = _route.Records[b.R].S;
                double zA = Z(a.K), zB = Z(b.K);
                if (a.R == 0 && _o.StartFixed) zA = _zStart;
                if (b.R == _route.Records.Count - 1 && _o.EndFixed) zB = _zEnd;
                solution.Pipes.Add(new ProfilePipe { Section = secA, S0 = sA, S1 = sB, Z0 = zA, Z1 = zB, Source = SourceAt(sec, (sA + sB) / 2.0) });
            }
            return solution;
        }

        /// <summary>The original pipe of the section covering station <paramref name="s"/> (its size, style and layer are copied).</summary>
        private static RoutePipe SourceAt(RouteSection sec, double s)
        {
            foreach (RoutePipe p in sec.Pipes) if (s >= p.S0 - 1e-6 && s <= p.S1 + 1e-6) return p;
            return sec.Template;
        }
    }
}
