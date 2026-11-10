using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Engines
{
    /// <summary>Vertical cover and clearance DIMs in true metres (contract section 7).</summary>
    internal static class DimStations
    {
        private const double Epsilon = 1e-9;
        private const double VertexTolerance = 1e-6;
        private sealed record Candidate(double Station, bool Face, double Change, string Key);

        public static DimPlan Plan(DimInput input, DimRules rules)
        {
            if (!ValidRules(rules)) return Failure(DimCodes.RulesInvalid, "DIM rules must be finite, with a positive grade threshold and nonnegative distances.");
            if (input == null) return Failure(DimCodes.InputInvalid, "There is no DIM input.");
            string error = Normalize(input.DuctTop, out List<StationElevation> top);
            if (error != null) return Failure(error, "The duct top needs finite, increasing stations and at least two distinct vertices.");
            error = Normalize(input.DuctBottom, out List<StationElevation> bottom);
            if (error != null) return Failure(error, "The duct bottom needs finite, increasing stations and at least two distinct vertices.");
            List<List<StationElevation>> surfaces = new();
            foreach (var raw in input.ProposedSurface)
            {
                error = Normalize(raw, out List<StationElevation> run);
                if (error != null) return Failure(error, "Each surface run needs finite, increasing stations and at least two distinct vertices.");
                if (surfaces.Count > 0)
                {
                    StationElevation previous = surfaces[^1][^1];
                    double gap = run[0].StationM - previous.StationM;
                    if (gap < -VertexTolerance || (Math.Abs(gap) <= VertexTolerance
                        && Math.Abs(run[0].ElevationM - previous.ElevationM) > VertexTolerance))
                        return Failure(DimCodes.InputSurfaceOverlap, "Surface runs must be ordered, must not overlap, and must agree at touching endpoints.");
                }
                surfaces.Add(run);
            }
            double from = top[0].StationM, to = top[^1].StationM;
            if (!double.IsFinite(input.OwnedFromM) || !double.IsFinite(input.OwnedToM)
                || input.OwnedToM - input.OwnedFromM <= Epsilon
                || input.OwnedFromM < from - Epsilon || input.OwnedToM > to + Epsilon
                || input.Structures.Any(s => s == null || !double.IsFinite(s.StartStationM) || !double.IsFinite(s.EndStationM)
                    || s.EndStationM < s.StartStationM - Epsilon))
                return Failure(DimCodes.InputInvalid, "The owned range and structure faces must be finite, ordered and valid for this view.");
            if (Math.Abs(bottom[0].StationM - from) > VertexTolerance || Math.Abs(bottom[^1].StationM - to) > VertexTolerance)
                return Failure(DimCodes.InputDuctInconsistent, "Duct top and bottom must cover the same station range.");
            foreach (double s in top.Concat(bottom).Select(p => p.StationM))
                if (!ValueAt(top, s, out double t, VertexTolerance) || !ValueAt(bottom, s, out double b, VertexTolerance)
                    || b > t - VertexTolerance + Epsilon)
                    return Failure(DimCodes.InputDuctInconsistent, "The duct bottom must remain below its top at every vertex of either profile.");

            double[] grades = new double[top.Count - 1];
            for (int i = 0; i < grades.Length; i++)
            {
                grades[i] = (top[i + 1].ElevationM - top[i].ElevationM) / (top[i + 1].StationM - top[i].StationM) * 100;
                if (!double.IsFinite(grades[i])) return Failure(DimCodes.InputInvalid, "Duct grades must be finite.");
            }
            List<OperationIssue> issues = new();
            List<CrossingPipe> crossings = new();
            for (int i = 0; i < input.Crossings.Count; i++)
            {
                CrossingPipe c = input.Crossings[i];
                if (c == null || !double.IsFinite(c.StationM) || !double.IsFinite(c.TopElevM) || !double.IsFinite(c.BottomElevM)
                    || c.BottomElevM > c.TopElevM + Epsilon)
                    issues.Add(OperationIssue.Error(DimCodes.CrossingInvalid, "A crossing has invalid stations or outside elevations; it was skipped.", $"Crossings[{i}]"));
                else crossings.Add(c);
            }

            bool Owned(double s) => s >= input.OwnedFromM - Epsilon
                && (input.OwnsEnd ? s <= input.OwnedToM + Epsilon : s < input.OwnedToM - Epsilon);
            List<Candidate> candidates = new();
            // Clusters include all interior vertices, and are bounded by their first station, never by chaining.
            for (int first = 1; first < top.Count - 1;)
            {
                int last = first;
                while (last + 1 < top.Count - 1 && top[last + 1].StationM - top[first].StationM <= rules.MergeWithinM + Epsilon) last++;
                int selected = first;
                double largest = Math.Abs(grades[first] - grades[first - 1]);
                for (int i = first + 1; i <= last; i++)
                {
                    double change = Math.Abs(grades[i] - grades[i - 1]);
                    if (change > largest + Epsilon) { largest = change; selected = i; }
                }
                double merged = Math.Abs(grades[last] - grades[first - 1]);
                if (merged >= rules.MajorGradeChangePct - Epsilon && Owned(top[selected].StationM))
                    candidates.Add(new Candidate(top[selected].StationM, false, merged, ""));
                first = last + 1;
            }
            if (rules.AtStructureFaces)
                foreach (StructureSpan structure in input.Structures)
                    foreach (double face in new[] { structure.StartStationM, structure.EndStationM })
                        if (face >= from - VertexTolerance && face <= to + VertexTolerance)
                        {
                            double s = Math.Clamp(face, from, to);
                            if (Owned(s)) candidates.Add(new Candidate(s, true, 0, structure.Key ?? ""));
                        }

            List<PlannedDim> dims = new();
            List<double> accepted = new();
            foreach (Candidate c in candidates.OrderByDescending(c => c.Face).ThenByDescending(c => c.Change)
                .ThenBy(c => c.Station).ThenBy(c => c.Key, StringComparer.Ordinal))
            {
                ValueAt(top, c.Station, out double lower);
                double upper = 0;
                bool found = false;
                foreach (var run in surfaces)
                    if (ValueAt(run, c.Station, out upper)) { found = true; break; }
                if (!found)
                {
                    issues.Add(OperationIssue.Warning(DimCodes.SurfaceMissing, $"No proposed surface at station {c.Station:R}; the cover DIM was skipped."));
                    continue;
                }
                double value = upper - lower;
                if (!double.IsFinite(value)) return Failure(DimCodes.InputInvalid, "DIM values must be representable finite distances.");
                if (value <= Epsilon)
                {
                    issues.Add(OperationIssue.Error(DimCodes.CoverNegative, $"The proposed surface is not above the duct at station {c.Station:R}."));
                    continue;
                }
                if (accepted.Any(s => Math.Abs(s - c.Station) < rules.MinSpacingM - Epsilon))
                {
                    issues.Add(OperationIssue.Info(DimCodes.CoverSpacing, $"The cover DIM at station {c.Station:R} was dropped to maintain spacing."));
                    continue;
                }
                accepted.Add(c.Station);
                dims.Add(new PlannedDim(DimKind.Cover, c.Station, lower, upper, value, c.Key,
                    c.Face ? DimReasons.StructureFace : DimReasons.GradeBreak));
            }
            foreach (CrossingPipe c in crossings)
            {
                if (c.StationM < from - Epsilon || c.StationM > to + Epsilon)
                {
                    issues.Add(OperationIssue.Info(DimCodes.CrossingOutsideView, $"Crossing '{c.Key}' is outside the view."));
                    continue;
                }
                if (!Owned(c.StationM)) continue;
                ValueAt(top, c.StationM, out double t);
                ValueAt(bottom, c.StationM, out double b, VertexTolerance);
                double lower, upper;
                string reason;
                if (c.BottomElevM >= t - Epsilon)
                {
                    lower = t; upper = Math.Max(t, c.BottomElevM); reason = DimReasons.CrossingAbove;
                }
                else if (c.TopElevM <= b + Epsilon)
                {
                    lower = Math.Min(c.TopElevM, b); upper = b; reason = DimReasons.CrossingBelow;
                }
                else
                {
                    issues.Add(OperationIssue.Error(DimCodes.ClearanceClash, $"Crossing '{c.Key}' intersects the duct."));
                    continue;
                }
                double value = upper - lower;
                if (!double.IsFinite(value)) return Failure(DimCodes.InputInvalid, "DIM values must be representable finite distances.");
                dims.Add(new PlannedDim(DimKind.Clearance, c.StationM, lower, upper, value, c.Key ?? "", reason));
                if (value < rules.MinClearanceM - Epsilon)
                    issues.Add(OperationIssue.Warning(DimCodes.ClearanceBelowMinimum, $"Crossing '{c.Key}' has clearance below the project minimum."));
            }
            return new DimPlan(dims.OrderBy(d => d.StationM).ThenBy(d => d.Kind).ThenBy(d => d.SourceKey, StringComparer.Ordinal).ToArray(), issues);
        }

        private static bool ValidRules(DimRules r) => r != null
            && double.IsFinite(r.MajorGradeChangePct) && r.MajorGradeChangePct > 0
            && double.IsFinite(r.MergeWithinM) && r.MergeWithinM >= 0
            && double.IsFinite(r.MinSpacingM) && r.MinSpacingM >= 0
            && double.IsFinite(r.MinClearanceM) && r.MinClearanceM >= 0;

        private static DimPlan Failure(string code, string message) =>
            new(Array.Empty<PlannedDim>(), new[] { OperationIssue.Error(code, message) });

        private static string Normalize(IReadOnlyList<StationElevation> input, out List<StationElevation> line)
        {
            line = new();
            foreach (StationElevation p in input)
            {
                if (!double.IsFinite(p.StationM) || !double.IsFinite(p.ElevationM)) return DimCodes.InputInvalid;
                if (line.Count > 0)
                {
                    double delta = p.StationM - line[^1].StationM;
                    if (!double.IsFinite(delta) || delta < -VertexTolerance) return DimCodes.InputInvalid;
                    if (Math.Abs(delta) <= VertexTolerance)
                    {
                        if (Math.Abs(p.ElevationM - line[^1].ElevationM) > VertexTolerance) return DimCodes.InputVerticalStep;
                        continue; // Keep the first station and elevation; do not mutate the caller's snapshot.
                    }
                }
                line.Add(p);
            }
            return line.Count < 2 ? DimCodes.InputInvalid : null;
        }

        private static bool ValueAt(IReadOnlyList<StationElevation> line, double station, out double value, double tolerance = Epsilon)
        {
            value = 0;
            if (station < line[0].StationM - tolerance || station > line[^1].StationM + tolerance) return false;
            if (station <= line[0].StationM) { value = line[0].ElevationM; return true; }
            if (station >= line[^1].StationM) { value = line[^1].ElevationM; return true; }
            int lo = 0, hi = line.Count - 1;
            while (hi - lo > 1)
            {
                int mid = lo + (hi - lo) / 2;
                if (line[mid].StationM <= station) lo = mid; else hi = mid;
            }
            double ratio = (station - line[lo].StationM) / (line[hi].StationM - line[lo].StationM);
            value = line[lo].ElevationM * (1 - ratio) + line[hi].ElevationM * ratio;
            return true;
        }
    }
}
