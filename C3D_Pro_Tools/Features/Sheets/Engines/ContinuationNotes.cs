using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Engines
{
    /// <summary>Shared paths on a sheet's own boundary, in model metres (contract section 8).</summary>
    internal static class ContinuationNotes
    {
        private const double Epsilon = 1e-9;
        private readonly record struct Interval(double Start, double End);
        private sealed record Outline(List<Point2> Points, double[] Stations);

        public static ContinuationPlan Resolve(IReadOnlyList<SheetArea> sheets, string sheetKey, ContinuationRules rules)
        {
            List<OperationIssue> issues = new();
            if (rules == null || !double.IsFinite(rules.ToleranceM) || rules.ToleranceM < 0
                || !double.IsFinite(rules.MinSharedM) || rules.MinSharedM <= 0)
                return new ContinuationPlan(null, new[] { OperationIssue.Error(ContinuationCodes.RulesInvalid,
                    "Continuation tolerance must be finite and nonnegative; minimum shared length must be finite and positive.") });
            Dictionary<string, SheetArea> unique = new(StringComparer.Ordinal);
            if (sheets != null)
                for (int i = 0; i < sheets.Count; i++)
                {
                    SheetArea sheet = sheets[i];
                    if (sheet == null)
                    {
                        issues.Add(OperationIssue.Warning(ContinuationCodes.BoundaryInvalid, "An empty neighbour was skipped.", $"Sheets[{i}]"));
                        continue;
                    }
                    if (!unique.TryAdd(sheet.Key ?? "", sheet))
                        issues.Add(OperationIssue.Error(ContinuationCodes.KeyDuplicate, $"Later duplicate sheet '{sheet.Key}' was skipped.", $"Sheets[{i}]"));
                }
            if (sheetKey == null || !unique.TryGetValue(sheetKey, out SheetArea selected))
            {
                issues.Add(OperationIssue.Error(ContinuationCodes.SheetNotFound, $"Sheet '{sheetKey}' was not found."));
                return new ContinuationPlan(null, issues);
            }
            if (!TryOutline(selected.ModelBoundary, out Outline own))
            {
                issues.Add(OperationIssue.Error(ContinuationCodes.BoundaryInvalid, $"Sheet '{sheetKey}' has an invalid boundary."));
                return new ContinuationPlan(null, issues);
            }
            List<(ContinuationEdge Edge, double Start)> edges = new();
            foreach (SheetArea neighbour in unique.Values)
            {
                if (ReferenceEquals(neighbour, selected)) continue;
                if (!TryOutline(neighbour.ModelBoundary, out Outline other))
                {
                    issues.Add(OperationIssue.Warning(ContinuationCodes.BoundaryInvalid, $"Neighbour '{neighbour.Key}' has an invalid boundary and was skipped."));
                    continue;
                }
                List<Interval> contacts = new();
                for (int i = 0; i < own.Points.Count; i++)
                {
                    Point2 a = own.Points[i], b = own.Points[(i + 1) % own.Points.Count];
                    double length = own.Stations[i + 1] - own.Stations[i];
                    Point2 direction = Scale(Sub(b, a), 1 / length);
                    List<double> cuts = new() { 0, length };
                    for (int j = 0; j < other.Points.Count; j++)
                    {
                        Point2 c = other.Points[j], d = other.Points[(j + 1) % other.Points.Count];
                        Point2 v = Sub(d, c), w = Sub(c, a);
                        double divisor = Cross(direction, v);
                        if (Math.Abs(divisor) > Epsilon)
                        {
                            double t = Cross(w, v) / divisor, u = Cross(w, direction) / divisor;
                            if (t >= -Epsilon && t <= length + Epsilon && u >= -Epsilon && u <= 1 + Epsilon)
                                cuts.Add(Math.Clamp(t, 0, length));
                        }
                        // Distance to a segment is the union of its strip and its two endpoint disks.
                        AddCapsule(contacts, a, direction, length, c, d, rules.ToleranceM, own.Stations[i]);
                    }
                    cuts.Sort();
                    for (int j = 1; j < cuts.Count; j++)
                        if (cuts[j] - cuts[j - 1] > Epsilon
                            && Inside(Add(a, Scale(direction, (cuts[j] + cuts[j - 1]) / 2)), other.Points))
                            contacts.Add(new Interval(own.Stations[i] + cuts[j - 1], own.Stations[i] + cuts[j]));
                }
                List<Interval> merged = Merge(contacts, rules.ToleranceM);
                double perimeter = own.Stations[^1];
                // The boundary is a ring: join contacts across its closing vertex too. Wrapped paths follow vertex order.
                if (merged.Count > 1 && Joins(perimeter - merged[^1].End + merged[0].Start, rules.ToleranceM))
                {
                    Interval wrap = new(merged[^1].Start, perimeter + merged[0].End);
                    merged.RemoveAt(merged.Count - 1);
                    merged.RemoveAt(0);
                    merged.Add(wrap);
                }
                foreach (Interval contact in merged)
                {
                    double shared = contact.End - contact.Start;
                    if (shared < rules.MinSharedM - Epsilon) continue;
                    edges.Add((new ContinuationEdge(neighbour.Key, neighbour.DrawingNumber, Path(own, contact),
                        At(own, contact.Start + shared / 2), shared), contact.Start));
                }
            }
            return new ContinuationPlan(edges.OrderBy(e => e.Edge.NeighbourDrawingNumber, StringComparer.Ordinal)
                .ThenBy(e => e.Start).ThenBy(e => e.Edge.NeighbourKey, StringComparer.Ordinal).Select(e => e.Edge).ToArray(), issues);
        }

        private static bool Joins(double gap, double tolerance) => gap <= Epsilon || gap < tolerance - Epsilon;
        private static List<Interval> Merge(List<Interval> contacts, double tolerance)
        {
            List<Interval> merged = new();
            foreach (Interval next in contacts.OrderBy(c => c.Start).ThenBy(c => c.End))
            {
                if (next.End - next.Start <= Epsilon) continue;
                if (merged.Count > 0 && Joins(next.Start - merged[^1].End, tolerance))
                    merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, next.End) };
                else merged.Add(next);
            }
            return merged;
        }

        private static void AddCapsule(List<Interval> result, Point2 a, Point2 direction, double length,
            Point2 c, Point2 d, double tolerance, double offset)
        {
            Point2 delta = Sub(d, c);
            double edgeLength = Length(delta);
            Point2 axis = Scale(delta, 1 / edgeLength), relative = Sub(a, c);
            double low = 0, high = length;
            if (Clip(Dot(relative, axis), Dot(direction, axis), 0, edgeLength, ref low, ref high)
                && Clip(Cross(axis, relative), Cross(axis, direction), -tolerance, tolerance, ref low, ref high))
                Store(result, low, high, length, offset);
            if (tolerance <= 0) return;
            foreach (Point2 centre in new[] { c, d })
            {
                Point2 v = Sub(centre, a);
                double distance = Math.Abs(Cross(direction, v));
                if (distance > tolerance + Epsilon) continue;
                double half = Math.Sqrt(Math.Max(0, (tolerance - distance) * (tolerance + distance)));
                double projection = Dot(v, direction);
                Store(result, projection - half, projection + half, length, offset);
            }
        }

        private static bool Clip(double value, double slope, double min, double max, ref double low, ref double high)
        {
            if (Math.Abs(slope) <= 1e-15) return value >= min - Epsilon && value <= max + Epsilon;
            double a = (min - value) / slope, b = (max - value) / slope;
            low = Math.Max(low, Math.Min(a, b));
            high = Math.Min(high, Math.Max(a, b));
            return high >= low - Epsilon;
        }
        private static void Store(List<Interval> result, double low, double high, double length, double offset)
        {
            low = Math.Max(0, low); high = Math.Min(length, high);
            if (high - low > Epsilon) result.Add(new Interval(offset + low, offset + high));
        }

        private static List<Point2> Path(Outline outline, Interval interval)
        {
            List<Point2> path = new() { At(outline, interval.Start) };
            double perimeter = outline.Stations[^1];
            for (int lap = 0; lap <= 1; lap++)
                for (int i = 1; i <= outline.Points.Count; i++)
                {
                    double s = outline.Stations[i] + lap * perimeter;
                    if (s > interval.Start + Epsilon && s < interval.End - Epsilon)
                        path.Add(outline.Points[i % outline.Points.Count]);
                }
            path.Add(At(outline, interval.End));
            return path;
        }
        private static Point2 At(Outline outline, double station)
        {
            double perimeter = outline.Stations[^1];
            station %= perimeter;
            for (int i = 0; i < outline.Points.Count; i++)
                if (station <= outline.Stations[i + 1])
                {
                    double t = (station - outline.Stations[i]) / (outline.Stations[i + 1] - outline.Stations[i]);
                    return Add(outline.Points[i], Scale(Sub(outline.Points[(i + 1) % outline.Points.Count], outline.Points[i]), t));
                }
            return outline.Points[0];
        }

        private static bool TryOutline(IReadOnlyList<Point2> input, out Outline outline)
        {
            outline = null;
            List<Point2> points = new();
            foreach (Point2 p in input)
            {
                if (!p.IsFinite) return false;
                if (points.Count == 0 || Length(Sub(p, points[^1])) > Epsilon) points.Add(p);
            }
            if (points.Count > 1 && Length(Sub(points[0], points[^1])) <= Epsilon) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) return false;
            double[] stations = new double[points.Count + 1];
            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2 a = points[i], b = points[(i + 1) % points.Count], c = points[(i + 2) % points.Count];
                double length = Length(Sub(b, a));
                stations[i + 1] = stations[i] + length;
                area += Cross(Sub(a, points[0]), Sub(b, points[0]));
                if (!double.IsFinite(stations[i + 1]) || length <= Epsilon) return false;
                if (Math.Abs(Cross(Sub(b, a), Sub(c, b))) <= Epsilon && Dot(Sub(b, a), Sub(c, b)) < 0) return false;
                for (int j = i + 1; j < points.Count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == points.Count - 1)) continue;
                    if (SegmentsMeet(a, b, points[j], points[(j + 1) % points.Count])) return false;
                }
            }
            if (!double.IsFinite(area) || Math.Abs(area) <= Epsilon) return false;
            outline = new Outline(points, stations);
            return true;
        }
        private static bool SegmentsMeet(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            Point2 v = Sub(b, a), w = Sub(d, c);
            double x = Cross(v, Sub(c, a)), y = Cross(v, Sub(d, a));
            double z = Cross(w, Sub(a, c)), t = Cross(w, Sub(b, c));
            if (((x > Epsilon && y < -Epsilon) || (x < -Epsilon && y > Epsilon))
                && ((z > Epsilon && t < -Epsilon) || (z < -Epsilon && t > Epsilon))) return true;
            return On(a, b, c) || On(a, b, d) || On(c, d, a) || On(c, d, b);
        }
        private static bool On(Point2 a, Point2 b, Point2 p) => Math.Abs(Cross(Sub(b, a), Sub(p, a))) <= Epsilon
            && p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon
            && p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
        private static bool Inside(Point2 p, List<Point2> polygon)
        {
            bool inside = false;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                if (On(a, b, p)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y)
                    && p.X < a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)) inside = !inside;
            }
            return inside;
        }
        private static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
        private static Point2 Add(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
        private static Point2 Scale(Point2 p, double scale) => new(p.X * scale, p.Y * scale);
        private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
        private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
        private static double Length(Point2 p) => Math.Sqrt(Dot(p, p));
    }
}
