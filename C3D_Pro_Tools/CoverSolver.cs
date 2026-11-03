using System;
using System.Collections.Generic;
using System.Threading;

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
        /// <summary>Most a manhole may drop the outlet below its matched level (the junction rule's highest level, less the minimum drop).</summary>
        public double MaxDrop;
        public bool StartFixed, EndFixed;
        /// <summary>Between layouts with equally few pipes: true = smallest slope changes at joints, false = shallowest.</summary>
        public bool PreferGentleBends;
        /// <summary>Longest pipe allowed (0 = no limit), e.g. a stock length or a manhole spacing rule on gravity networks.</summary>
        public double MaxLength;
        /// <summary>Vertical clear distance kept to pipes crossing the route (outside to outside) when a crossing names none of its own; 0 = such crossings are ignored.</summary>
        public double CrossingClearance;
        /// <summary>Junction matching, drops, slopes by size, margins, dry utility bends and spans, crossing clearances.</summary>
        public CoverRules Rules = new CoverRules();
        /// <summary>Stops a long search (the designer's Cancel button).</summary>
        public CancellationToken Cancel;
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
        /// <summary>True when the layout needed a backdrop manhole (none fitted without one).</summary>
        public bool UsedBackdrops;
        /// <summary>True when the layout keeps only the protection-slab clearance to some crossing (none fitted with the full clearance).</summary>
        public bool UsedProtectionClearance;
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
    /// <para>
    /// The rules in <see cref="CoverOptions.Rules"/> apply on top: the junction rule and minimum drop at manholes, slope
    /// limits by pipe size, a cover safety margin and cover zones, and for dry utilities a minimum bend radius and a bend
    /// budget per pull. Backdrop manholes and the reduced (protection slab) clearance are last resorts: they are tried only
    /// when no layout meets the limits without them.
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
        // A backdrop costs as much as this much extra depth × length (m²), so the fewest are used.
        private const double BackdropPenalty = 1e5;
        // The vertical bending of a pull is grouped into at most this many steps of its allowance (and none finer than
        // MinTurnBucketDeg) for merging search states; the limit itself is checked on the exact angle.
        private const int TurnBucketsPerPull = 8;
        private const double MinTurnBucketDeg = 5.0;

        private readonly CoverRoute _route;
        private readonly CoverOptions _o;
        private readonly CoverRules _rules;
        private readonly double _zStart, _zEnd, _zRef, _bucket, _slopeOffset;
        // Breaks are allowed on every n-th station (cover is still checked at every one): about a tenth of the minimum length.
        private readonly int _breakEvery;
        private readonly double[] _relaxLo, _relaxHi;
        // Per section: slope limits, minimum vertical bend radius, its pull (span) and the vertical bending that pull allows.
        private readonly double[] _secMinSlope, _secMaxSlope, _secBendRadius, _spanVerticalTurn;
        private readonly int[] _spanOf;
        private readonly bool _budget;

        // The phase being solved: backdrops allowed, reduced (protection slab) clearance.
        private bool _allowBackdrop, _reducedClearance;

        // Per record: allowed level index range, limits and depth prefix sums for the current pass.
        private int[] _kLo, _kHi;
        private double[] _lo, _hi, _prefix;
        private readonly Dictionary<(long, int), Entry> _entries = new Dictionary<(long, int), Entry>();
        private int _furthest;

        /// <summary>A search state: a level at a station, reached with an incoming slope (bucketed).</summary>
        private sealed class Entry
        {
            public int R, K, Layer;
            /// <summary>Cost = depth below the shallowest allowed level (+ backdrop penalties); Bend = sharpest slope change at a joint so far.</summary>
            public double Cost, InSlope, Bend;
            /// <summary>Length of the incoming pipe (for the bend radius) and the vertical bending since the last structure (degrees).</summary>
            public double InLength, Turned;
            public (long, int) Key;
            public Entry Parent;
            public bool Queued;
        }

        public CoverSolver(CoverRoute route, CoverOptions options)
        {
            _route = route;
            _o = options;
            _rules = options.Rules ?? new CoverRules();
            _zStart = route.Pipes[0].Z0;
            _zEnd = route.Pipes[route.Pipes.Count - 1].Z1;
            // With a fixed start the grid passes exactly through its level.
            _zRef = options.StartFixed ? _zStart : Math.Floor(_zStart);
            _breakEvery = Math.Max(1, (int)Math.Round(options.MinLength / 10.0));
            _relaxLo = new double[route.Records.Count];
            _relaxHi = new double[route.Records.Count];

            int sections = route.Sections.Count;
            _secMinSlope = new double[sections];
            _secMaxSlope = new double[sections];
            _secBendRadius = new double[sections];
            _slopeOffset = options.MaxSlope;
            double radiusLimit = double.PositiveInfinity;
            for (int i = 0; i < sections; i++)
            {
                RouteSection sec = route.Sections[i];
                (_secMinSlope[i], _secMaxSlope[i]) = _rules.SlopesFor(sec.InnerHeight, options.MinSlope, options.MaxSlope);
                _slopeOffset = Math.Max(_slopeOffset, _secMaxSlope[i]);
                _secBendRadius[i] = _rules.BendRadiusFor(options.Mode, sec.InnerHeight);
                if (_secBendRadius[i] > 0) radiusLimit = Math.Min(radiusLimit, Math.Max(options.MinLength, 1.0) / _secBendRadius[i]);
            }
            double bucket = options.MaxSlopeChange > 0 ? options.MaxSlopeChange / SlopeBucketsPerLimit
                          : options.PreferGentleBends ? GentleBucket : double.PositiveInfinity;
            // A bend radius limits the slope change at joints like a slope-change limit does.
            _bucket = Math.Min(bucket, radiusLimit / SlopeBucketsPerLimit);

            // Pulls: the vertical bending allowed is the budget less the plan turning, which the levels cannot change.
            _spanOf = new int[sections];
            List<(int first, int last, double length, double planTurn)> spans = route.Spans();
            _spanVerticalTurn = new double[spans.Count];
            double budget = _rules.BendBudgetFor(options.Mode);
            for (int s = 0; s < spans.Count; s++)
            {
                for (int i = spans[s].first; i <= spans[s].last; i++) _spanOf[i] = s;
                // A pull whose plan alone uses the budget up is reported, not solved for (the levels cannot help it).
                _spanVerticalTurn[s] = budget > 0 && spans[s].planTurn < budget ? budget - spans[s].planTurn : double.PositiveInfinity;
            }
            _budget = budget > 0;
        }

        public CoverSolution Solve()
        {
            // Strict first; then, only if that cannot meet the limits, with backdrops; then with the protection-slab clearance.
            bool backdrops = _rules.Backdrop == BackdropMode.CostedBackdropWithFlag && _rules.JunctionFor(_o.Mode) != JunctionMatchRule.None;
            bool reduced = _rules.ProtectionMinClearance > 0 && _route.Crossings.Count > 0;
            List<(bool backdrop, bool reduced)> phases = new List<(bool, bool)> { (false, false) };
            if (backdrops) phases.Add((true, false));
            if (reduced) phases.Add((false, true));
            if (backdrops && reduced) phases.Add((true, true));

            CoverSolution best = null;
            foreach ((bool backdrop, bool reducedClearance) in phases)
            {
                _allowBackdrop = backdrop;
                _reducedClearance = reducedClearance;
                Array.Clear(_relaxLo, 0, _relaxLo.Length);
                Array.Clear(_relaxHi, 0, _relaxHi.Length);
                CoverSolution solution = SolveRelaxing();
                if (solution == null) continue;
                solution.UsedBackdrops = backdrop;
                solution.UsedProtectionClearance = reducedClearance;
                if (solution.Relaxation <= 1e-9) return solution;
                if (best == null || solution.Relaxation < best.Relaxation - 1e-9) best = solution;
            }
            // Leave the crossings showing the sides of the layout returned.
            if (best != null)
            {
                _allowBackdrop = best.UsedBackdrops;
                _reducedClearance = best.UsedProtectionClearance;
                DecideCrossingSides();
            }
            return best;
        }

        private CoverSolution SolveRelaxing()
        {
            int n = _route.Records.Count;
            double radius = 2 * Math.Max(_o.MinLength, 10.0);
            int bestFurthest = -1, stuck = 0;
            DecideCrossingSides();

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                _o.Cancel.ThrowIfCancellationRequested();
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
                        double excess = Math.Max((rec.Ground - _o.MaxCover - off) - z, z - (rec.Ground - _route.MinCoverAt(rec.S, _o.MinCover) - off));
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

        /// <summary>The clearance kept to a crossing in the phase being solved.</summary>
        private double ClearanceTo(RouteCrossing c)
        {
            double full = c.ClearanceOr(_o.CrossingClearance);
            return _reducedClearance && _rules.ProtectionMinClearance > 0 ? Math.Min(full, _rules.ProtectionMinClearance) : full;
        }

        /// <summary>
        /// Each crossing is passed under when that stays within the cover limits (deeper is the usual rule), else over, and
        /// under again when neither fits. It is decided once, at the station where the pipes cross (or are closest), and holds
        /// along the crossing's whole width.
        /// </summary>
        private void DecideCrossingSides()
        {
            Dictionary<RouteCrossing, double> decidedAt = new Dictionary<RouteCrossing, double>();
            foreach (RouteRecord rec in _route.Records)
            {
                foreach (CrossingLevel level in rec.Crossings)
                {
                    RouteCrossing c = level.Crossing;
                    double distance = Math.Abs(rec.S - c.S);
                    if (decidedAt.TryGetValue(c, out double best) && best <= distance) continue;
                    decidedAt[c] = distance;
                    RouteSection sec = _route.Sections[rec.Section];
                    double off = CoverRoute.ReferenceOffset(_o.Reference, sec.InnerHeight, sec.OuterHeight);
                    double lo = rec.Ground - _o.MaxCover - off, hi = rec.Ground - _route.MinCoverAt(rec.S, _o.MinCover) - off;
                    double clearance = ClearanceTo(c);
                    double belowHi = level.Bottom - clearance - sec.OuterHeight / 2.0;
                    double aboveLo = level.Top + clearance + sec.OuterHeight / 2.0;
                    c.PassBelow = belowHi >= lo || aboveLo > hi;
                }
            }
        }

        private double Z(int k) => _zRef + k * ElevationStep;

        internal int Bucket(double slope)
        {
            if (double.IsNaN(slope)) return NoSlopeBucket;
            if (double.IsPositiveInfinity(_bucket)) return 0;
            // Clamped as a double: a huge quotient cast to int first would wrap round to a negative bucket.
            return (int)Math.Max(0.0, Math.Min(NoSlopeBucket - 1, Math.Floor((slope + _slopeOffset) / _bucket)));
        }

        private (long, int) KeyOf(int r, int k, int bucket, double turned)
        {
            double limit = _spanVerticalTurn[_spanOf[_route.Records[r].Section]];
            int turn = _budget && !double.IsPositiveInfinity(limit) ? (int)(turned / Math.Max(MinTurnBucketDeg, limit / TurnBucketsPerPull)) : 0;
            return (((long)r << (2 * KeyBits)) | ((long)(k - _kLo[r]) << KeyBits) | (long)bucket, turn);
        }

        /// <summary>The cover limits at a record (zone minimum and safety margin included), as centreline levels.</summary>
        private (double lo, double hi) CoverBand(RouteRecord rec, RouteSection sec)
        {
            double off = CoverRoute.ReferenceOffset(_o.Reference, sec.InnerHeight, sec.OuterHeight);
            double minCover = _route.MinCoverAt(rec.S, _o.MinCover), maxCover = _o.MaxCover;
            double margin = Math.Max(0, _rules.CoverSafetyEpsilon);
            // The margin keeps the levels clear of the limits' rounding; never so far that the band closes.
            if (maxCover - minCover > 2 * margin) { minCover += margin; maxCover -= margin; }
            return (rec.Ground - maxCover - off, rec.Ground - minCover - off);
        }

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
                (double lo, double hi) = CoverBand(rec, sec);
                lo -= _relaxLo[r];
                hi += _relaxHi[r];
                // Crossing pipes are hard limits: the cover limits may be widened, the clearance never.
                foreach (CrossingLevel level in rec.Crossings)
                {
                    double clearance = ClearanceTo(level.Crossing);
                    if (clearance <= 0) continue;
                    if (level.Crossing.PassBelow) hi = Math.Min(hi, level.Bottom - clearance - sec.OuterHeight / 2.0);
                    else lo = Math.Max(lo, level.Top + clearance + sec.OuterHeight / 2.0);
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
            for (int k = _kLo[0]; k <= _kHi[0]; k++) Mark(0, k, 0, 0.0, 0.0, null, double.NaN, 0, 0, frontier);

            for (int layer = 0; frontier.Count > 0 && layer <= n; layer++)
            {
                _o.Cancel.ThrowIfCancellationRequested();
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
            return CoverBand(rec, _route.Sections[rec.Section]).hi;
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

        private void Mark(int r, int k, int layer, double cost, double bend, Entry parent, double inSlope, double inLength, double turned, List<Entry> list)
        {
            if (k < _kLo[r] || k > _kHi[r]) return;
            (long, int) key = KeyOf(r, k, Bucket(inSlope), turned);
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
            e.InLength = inLength;
            e.Turned = turned;
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
                Mark(r2, e.K, layer, e.Cost, e.Bend, e, e.InSlope, e.InLength, e.Turned, list);
                return;
            }

            // A structure: the bend radius and bend budget start again; levels follow the junction rule.
            JunctionMatchRule rule = _rules.JunctionFor(_o.Mode);
            double zIn = Z(e.K), dIn = sec.InnerHeight, dOut = nextSec.InnerHeight;
            if (rule == JunctionMatchRule.None)
            {
                // Free, but the invert steps at most this far up or down through the structure.
                double step = _rules.FreeStepTolerance(_o.Mode), invIn = zIn - dIn / 2.0;
                if (double.IsPositiveInfinity(step))
                    for (int k2 = _kLo[r2]; k2 <= _kHi[r2]; k2++) Mark(r2, k2, layer, e.Cost, e.Bend, e, double.NaN, 0, 0, list);
                else MarkBand(r2, invIn - step + dOut / 2.0, invIn + step + dOut / 2.0, layer, e.Cost, e, list);
                return;
            }

            // The matched level less the minimum drop for the turn here; standard drops reach MaxDrop below it.
            double zMax = CoverRules.MaxOutletCentre(rule, zIn, dIn, dOut) - _rules.MinDropFor(_o.Mode, nextSec.DeflectionIn);
            bool unrestricted = _rules.Backdrop == BackdropMode.UnrestrictedDrop;
            double zMin = unrestricted ? double.NegativeInfinity : zMax - _o.MaxDrop;
            MarkBand(r2, zMin, zMax, layer, e.Cost, e, list);

            if (_allowBackdrop && !unrestricted)
            {
                // A backdrop: an invert-to-invert drop from the minimum backdrop height to its maximum, below the matched level too.
                double invertIn = zIn - dIn / 2.0;
                double hi = Math.Min(zMax, invertIn - _rules.MinBackdropHeight + dOut / 2.0);
                double lo = invertIn - _rules.MaxBackdropDrop + dOut / 2.0;
                MarkBand(r2, lo, hi, layer, e.Cost + BackdropPenalty, e, list);
            }
        }

        private void MarkBand(int r2, double zLo, double zHi, int layer, double cost, Entry from, List<Entry> list)
        {
            int k2Lo = double.IsNegativeInfinity(zLo) ? _kLo[r2] : Math.Max(_kLo[r2], (int)Math.Ceiling((zLo - _zRef) / ElevationStep - Eps));
            int k2Hi = Math.Min(_kHi[r2], (int)Math.Floor((zHi - _zRef) / ElevationStep + Eps));
            for (int k2 = k2Lo; k2 <= k2Hi; k2++) Mark(r2, k2, layer, cost, from.Bend, from, double.NaN, 0, 0, list);
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
            List<(double lo, double hi)> slopes = SlopeIntervals(sec, from.InSlope);
            double bandLo = double.NegativeInfinity, bandHi = double.PositiveInfinity;
            double radius = _secBendRadius[sec.Index];
            double turnLimit = _spanVerticalTurn[_spanOf[sec.Index]];

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

                // Minimum bend radius: a joint may turn the slope by no more than the shorter pipe's length over the radius.
                double radiusChange = radius > 0 && !double.IsNaN(from.InSlope) ? Math.Min(from.InLength, L) / radius : double.PositiveInfinity;

                foreach ((double sLo, double sHi) in slopes)
                {
                    double mLo = Math.Max(sLo, bandLo), mHi = Math.Min(sHi, bandHi);
                    if (!double.IsPositiveInfinity(radiusChange)) { mLo = Math.Max(mLo, from.InSlope - radiusChange); mHi = Math.Min(mHi, from.InSlope + radiusChange); }
                    if (mLo > mHi + Eps) continue;
                    int kA = Math.Max(_kLo[r2], (int)Math.Ceiling((z + mLo * L - _zRef) / ElevationStep - Eps));
                    int kB = Math.Min(_kHi[r2], (int)Math.Floor((z + mHi * L - _zRef) / ElevationStep + Eps));
                    for (int k2 = kA; k2 <= kB; k2++)
                    {
                        double z2 = Z(k2);
                        double slope = (z2 - z) / L;
                        double turned = from.Turned;
                        if (!double.IsNaN(from.InSlope))
                        {
                            turned += Math.Abs(Math.Atan(slope) - Math.Atan(from.InSlope)) * 180.0 / Math.PI;
                            if (turned > turnLimit + 1e-9) continue;
                        }
                        double cost = from.Cost + (_prefix[r2] - _prefix[r]) - (z + z2) / 2.0 * L;
                        double bend = double.IsNaN(from.InSlope) ? from.Bend : Math.Max(from.Bend, Math.Abs(slope - from.InSlope));
                        Mark(r2, k2, layer, cost, bend, from, slope, L, turned, list);
                    }
                }
            }
        }

        private List<(double, double)> SlopeIntervals(RouteSection sec, double inSlope)
        {
            double min = _secMinSlope[sec.Index], max = _secMaxSlope[sec.Index];
            List<(double lo, double hi)> intervals = new List<(double, double)>();
            if (_o.Mode == CoverMode.Gravity)
            {
                // The route runs in the flow direction, so the pipes fall.
                intervals.Add((-max, -min));
            }
            else if (min <= 0)
            {
                intervals.Add((-max, max));
            }
            else
            {
                intervals.Add((-max, -min));
                intervals.Add((min, max));
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
            for (Entry e = goal; e != null; e = e.Parent) chain.Add(e);
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
