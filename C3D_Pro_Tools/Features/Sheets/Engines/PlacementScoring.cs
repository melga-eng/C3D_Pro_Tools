using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Engines
{
    /// <summary>Scores completed placements, in paper millimetres (contract section 9).</summary>
    internal static class PlacementScoring
    {
        private const double AreaTolerance = 1e-6;
        private const double Epsilon = 1e-9;

        public static PlacementScore Score(PlacementInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            Validate(input);
            ScoredLabel[] placed = input.Labels.Where(l => l.Placed).ToArray();
            var viewport = input.ViewportMm.Select(p => (p.X, p.Y)).ToArray();
            int overlaps = 0, outside = 0, crossings = 0, overText = 0;
            List<double> lengths = new();
            for (int i = 0; i < placed.Length; i++)
            {
                ScoredLabel label = placed[i];
                Rect2 box = label.BoxMm.Value;
                if (Corners(box).Any(p => !ArrangeCase.Inside(viewport, input.MarginMm, (p.X, p.Y))
                    || (label.RegionMm.HasValue && !label.RegionMm.Value.Contains(p)))) outside++;
                for (int j = i + 1; j < placed.Length; j++)
                    if (SharedArea(box, placed[j].BoxMm.Value) > AreaTolerance) overlaps++;
                foreach (ScoredObstacle obstacle in input.Obstacles)
                {
                    if (obstacle.Group == ScoredObstacleGroup.Network
                        && (obstacle.Network == null || !label.KeepClearOf.Contains(obstacle.Network))) continue;
                    bool hit = obstacle.OutlineMm.Count == 2
                        ? SegmentHitsPolygon(obstacle.OutlineMm[0], obstacle.OutlineMm[1], Corners(box))
                        : ClippedArea(obstacle.OutlineMm, box) > AreaTolerance;
                    if (hit) overlaps++;
                }
                if (!label.LeaderStartMm.HasValue) continue;
                Point2 a = label.LeaderStartMm.Value, b = label.LeaderEndMm.Value;
                lengths.Add(a.DistanceTo(b));
                for (int j = i + 1; j < placed.Length; j++)
                    if (placed[j].LeaderStartMm.HasValue
                        && LeadersCross(a, b, placed[j].LeaderStartMm.Value, placed[j].LeaderEndMm.Value)) crossings++;
                // Count a leader once, even when it crosses several text obstacles or other label boxes.
                bool textHit = input.Obstacles.Any(o => o.Group == ScoredObstacleGroup.Text && SegmentHitsPolygon(a, b, o.OutlineMm));
                if (!textHit)
                    for (int j = 0; j < placed.Length; j++)
                        if (j != i && SegmentHitsPolygon(a, b, Corners(placed[j].BoxMm.Value))) { textHit = true; break; }
                if (textHit) overText++;
            }
            return new PlacementScore(input.Labels.Count - placed.Length, overlaps, outside, crossings, overText,
                lengths.Count == 0 ? 0 : lengths.Average(), lengths.Count == 0 ? 0 : lengths.Max());
        }

        public static PlacementInput FromPlanned(ArrangeCase arrangeCase, IReadOnlyList<PlanLabel> planned)
        {
            ArgumentNullException.ThrowIfNull(arrangeCase);
            ArgumentNullException.ThrowIfNull(planned);
            var caseLabels = arrangeCase.Labels.ToDictionary(l => l.Key, StringComparer.Ordinal);
            List<ScoredLabel> labels = new();
            foreach (PlanLabel label in planned)
            {
                if (label == null || label.Key == null || !caseLabels.TryGetValue(label.Key, out ArrangeCase.Label source))
                    throw new ArgumentException("Every planned label must have a matching case label key.", nameof(planned));
                Rect2? region = source.Region == null ? null : Box(source.Region);
                LabelSpot? spot = label.Spot;
                labels.Add(new ScoredLabel(label.Key, new Point2(label.Anchor.X, label.Anchor.Y),
                    label.Networks == null ? null : new HashSet<string>(label.Networks, StringComparer.OrdinalIgnoreCase), region,
                    spot.HasValue, spot.HasValue ? Box(spot.Value.Box) : null,
                    spot.HasValue ? new Point2(label.Start.X, label.Start.Y) : null,
                    spot.HasValue ? new Point2(spot.Value.End.X, spot.Value.End.Y) : null));
            }
            var obstacles = arrangeCase.Obstacles.Select(o =>
            {
                if (!Enum.TryParse(o.Group, out ScoredObstacleGroup group) || !Enum.IsDefined(group))
                    throw new ArgumentException($"Unknown obstacle group '{o.Group}'.", nameof(arrangeCase));
                return new ScoredObstacle(group, o.Network, o.Points.Select(Point).ToArray());
            }).ToArray();
            return new PlacementInput(arrangeCase.Viewport.Select(Point).ToArray(), arrangeCase.Margin, obstacles, labels);
        }

        public static ScoreComparison Compare(PlacementScore baseline, PlacementScore candidate)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateScore(baseline);
            ValidateScore(candidate);
            List<string> worse = new();
            if (candidate.Unplaced > baseline.Unplaced) worse.Add(nameof(PlacementScore.Unplaced));
            if (candidate.Overlaps > baseline.Overlaps) worse.Add(nameof(PlacementScore.Overlaps));
            if (candidate.OutsideArea > baseline.OutsideArea) worse.Add(nameof(PlacementScore.OutsideArea));
            if (candidate.LeaderCrossings > baseline.LeaderCrossings) worse.Add(nameof(PlacementScore.LeaderCrossings));
            if (candidate.LeaderOverText > baseline.LeaderOverText) worse.Add(nameof(PlacementScore.LeaderOverText));
            if (candidate.MeanLeaderMm > baseline.MeanLeaderMm * 1.10 + 0.5 + Epsilon) worse.Add(nameof(PlacementScore.MeanLeaderMm));
            return new ScoreComparison(worse.Count == 0, worse);
        }

        private static void ValidateScore(PlacementScore score)
        {
            if (score.Unplaced < 0 || score.Overlaps < 0 || score.OutsideArea < 0 || score.LeaderCrossings < 0 || score.LeaderOverText < 0
                || !double.IsFinite(score.MeanLeaderMm) || score.MeanLeaderMm < 0 || !double.IsFinite(score.MaxLeaderMm) || score.MaxLeaderMm < 0)
                throw new ArgumentException("Placement scores must have nonnegative counts and finite, nonnegative leader lengths.", nameof(score));
        }
        private static Point2 Point(double[] p)
        {
            if (p == null || p.Length < 2) throw new ArgumentException("A case point needs X and Y coordinates.");
            return new Point2(p[0], p[1]);
        }
        private static Rect2 Box(double[] b)
        {
            if (b.Length != 4) throw new ArgumentException("A case region needs min X, min Y, max X and max Y.");
            return new Rect2(b[0], b[1], b[2], b[3]);
        }
        private static Rect2 Box(LRect b) => new(b.MinX, b.MinY, b.MaxX, b.MaxY);
        private static Point2[] Corners(Rect2 b) => new[] { new Point2(b.MinX, b.MinY), new Point2(b.MaxX, b.MinY),
            new Point2(b.MaxX, b.MaxY), new Point2(b.MinX, b.MaxY) };
        private static double SharedArea(Rect2 a, Rect2 b) => Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX))
            * Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));

        private static double ClippedArea(IReadOnlyList<Point2> polygon, Rect2 box)
        {
            List<Point2> clipped = polygon.ToList();
            for (int edge = 0; edge < 4 && clipped.Count > 0; edge++)
            {
                bool xAxis = edge < 2;
                double bound = edge switch { 0 => box.MinX, 1 => box.MaxX, 2 => box.MinY, _ => box.MaxY };
                double Coordinate(Point2 p) => xAxis ? p.X : p.Y;
                bool Inside(Point2 p) => edge % 2 == 0 ? Coordinate(p) >= bound : Coordinate(p) <= bound;
                List<Point2> next = new();
                Point2 previous = clipped[^1];
                foreach (Point2 current in clipped)
                {
                    bool wasInside = Inside(previous), isInside = Inside(current);
                    if (wasInside != isInside)
                    {
                        double ratio = (bound - Coordinate(previous)) / (Coordinate(current) - Coordinate(previous));
                        next.Add(new Point2(xAxis ? bound : previous.X + ratio * (current.X - previous.X),
                            xAxis ? previous.Y + ratio * (current.Y - previous.Y) : bound));
                    }
                    if (isInside) next.Add(current);
                    previous = current;
                }
                clipped = next;
            }
            double area = 0;
            if (clipped.Count > 2)
                for (int i = 1; i < clipped.Count - 1; i++) area += Cross(Sub(clipped[i], clipped[0]), Sub(clipped[i + 1], clipped[0]));
            return Math.Abs(area) / 2;
        }

        private static bool SegmentHitsPolygon(Point2 a, Point2 b, IReadOnlyList<Point2> polygon)
        {
            if (a.DistanceTo(b) <= Epsilon) return false;
            if (polygon.Count == 2) return LeadersCross(a, b, polygon[0], polygon[1]);
            return !LabelLayout.Separated(new[] { (a.X, a.Y), (b.X, b.Y) }, polygon.Select(p => (p.X, p.Y)).ToArray(), 0);
        }
        private static bool LeadersCross(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            Point2 r = Sub(b, a), s = Sub(d, c), w = Sub(c, a);
            double rr = Dot(r, r), ss = Dot(s, s);
            if (rr <= Epsilon * Epsilon || ss <= Epsilon * Epsilon) return false;
            double divisor = Cross(r, s);
            if (Math.Abs(divisor) <= Epsilon)
            {
                if (Math.Abs(Cross(w, r)) > Epsilon) return false;
                double t0 = Dot(w, r) / rr, t1 = Dot(Sub(d, a), r) / rr;
                double low = Math.Max(0, Math.Min(t0, t1)), high = Math.Min(1, Math.Max(t0, t1));
                if (high < low - Epsilon) return false;
                if ((high - low) * Math.Sqrt(rr) > Epsilon) return true; // Shared interior, even for identical leaders.
                Point2 contact = new(a.X + low * r.X, a.Y + low * r.Y);
                return !(Endpoint(contact, a, b) && Endpoint(contact, c, d));
            }
            double t = Cross(w, s) / divisor, u = Cross(w, r) / divisor;
            if (t < -Epsilon || t > 1 + Epsilon || u < -Epsilon || u > 1 + Epsilon) return false;
            Point2 intersection = new(a.X + t * r.X, a.Y + t * r.Y);
            return !(Endpoint(intersection, a, b) && Endpoint(intersection, c, d));
        }
        private static bool Endpoint(Point2 p, Point2 a, Point2 b) => p.DistanceTo(a) <= Epsilon || p.DistanceTo(b) <= Epsilon;
        private static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
        private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
        private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

        // The score has no issue channel: malformed scoring data is a programming error, rather than a zero score.
        private static void Validate(PlacementInput input)
        {
            if (!double.IsFinite(input.MarginMm) || input.MarginMm < 0 || input.ViewportMm.Any(p => !p.IsFinite))
                throw new ArgumentException("Viewport coordinates and margin must be finite, with a nonnegative margin.", nameof(input));
            foreach (ScoredLabel label in input.Labels)
            {
                if (label == null) throw new ArgumentException("A scoring label is null.", nameof(input));
                if (!label.Placed) continue;
                if (input.ViewportMm.Count < 3 || !label.AnchorMm.IsFinite || !label.BoxMm.HasValue || !label.BoxMm.Value.IsValid
                    || (label.RegionMm.HasValue && !label.RegionMm.Value.IsValid)
                    || label.LeaderStartMm.HasValue != label.LeaderEndMm.HasValue
                    || (label.LeaderStartMm.HasValue && (!label.LeaderStartMm.Value.IsFinite || !label.LeaderEndMm.Value.IsFinite
                        || !double.IsFinite(label.LeaderStartMm.Value.DistanceTo(label.LeaderEndMm.Value)))))
                    throw new ArgumentException("A placed label needs a valid box, area and paired finite leader endpoints.", nameof(input));
            }
            foreach (ScoredObstacle obstacle in input.Obstacles)
                if (obstacle == null || !Enum.IsDefined(obstacle.Group) || obstacle.OutlineMm.Count < 2 || obstacle.OutlineMm.Any(p => !p.IsFinite))
                    throw new ArgumentException("An obstacle needs a known group and a finite outline of at least two points.", nameof(input));
        }
    }
}
