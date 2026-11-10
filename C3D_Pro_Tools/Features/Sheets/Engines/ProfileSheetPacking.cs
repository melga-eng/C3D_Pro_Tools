using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Engines
{
    /// <summary>Pure station-based packing, as sheet-production-contract.md section 6 defines.</summary>
    internal static class ProfileSheetPacking
    {
        private const double Tolerance = 1e-9;

        public static ProfileSheetPlan Pack(IReadOnlyList<ProfileViewItem> items, PackingRules rules)
        {
            List<OperationIssue> issues = new();
            if (!ValidRules(rules))
                return ErrorPlan(PackingCodes.RulesInvalid, "Packing rules must be finite and within their allowed ranges.");
            double capacity = rules.RowCapacityM;
            double overhead = rules.TitleWidthM + rules.GapM;
            if (capacity + Tolerance < 2 * rules.MinPieceM + rules.SplitRoundM + rules.OverlapM
                || rules.OverlapM >= capacity - rules.SplitRoundM - Tolerance
                || overhead >= capacity - Tolerance)
                return ErrorPlan(PackingCodes.RulesInfeasible, "The capacity cannot accommodate these minimum lengths, rounding, overlap or titles.");

            // Detect duplicates in input order, before sorting. Even an invalid first occurrence reserves its key.
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            List<(ProfileViewItem Item, List<PackedSlot> Pieces)> views = new();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    ProfileViewItem item = items[i];
                    string field = $"Items[{i}]";
                    if (item != null && !keys.Add(item.Key ?? ""))
                    {
                        issues.Add(OperationIssue.Error(PackingCodes.ItemDuplicateKey, $"View key '{item.Key}' occurs more than once; the later item was skipped.", field));
                        continue;
                    }
                    if (item == null || string.IsNullOrWhiteSpace(item.Key)
                        || !double.IsFinite(item.StartStationM) || !double.IsFinite(item.EndStationM)
                        || !double.IsFinite(item.EndStationM - item.StartStationM)
                        || item.EndStationM - item.StartStationM <= Tolerance
                        || !TrySplit(item, rules, out List<PackedSlot> pieces))
                    {
                        issues.Add(OperationIssue.Error(PackingCodes.ItemInvalid, "A view needs a key and finite, increasing, representable stations.", field));
                        continue;
                    }
                    views.Add((item, pieces));
                }
            }

            List<List<PackedSlot>> rows = new();
            List<double> used = new();
            void Add(PackedSlot slot, bool forceNew, bool firstFit)
            {
                double length = slot.EndStationM - slot.StartStationM;
                int row = -1;
                if (!forceNew)
                {
                    int first = firstFit ? 0 : Math.Max(0, rows.Count - 1);
                    for (int r = first; r < rows.Count; r++)
                        if (used[r] + overhead + length <= capacity + Tolerance) { row = r; break; }
                }
                if (row < 0)
                {
                    row = rows.Count;
                    rows.Add(new List<PackedSlot>());
                    used.Add(0);
                }
                double offset = rows[row].Count == 0 ? 0 : used[row] + overhead;
                rows[row].Add(slot with { OffsetM = offset });
                used[row] = offset + length;
            }

            var ordered = views.OrderBy(v => v.Item.Order).ThenBy(v => v.Item.Key, StringComparer.Ordinal);
            if (rules.Order == PackingOrder.Preserve)
            {
                foreach (var view in ordered)
                    foreach (PackedSlot piece in view.Pieces)
                        Add(piece, view.Pieces.Count > 1, false);
            }
            else
            {
                foreach (var view in ordered.Where(v => v.Pieces.Count > 1))
                    foreach (PackedSlot piece in view.Pieces) Add(piece, true, false);
                foreach (var view in views.Where(v => v.Pieces.Count == 1)
                    .OrderByDescending(v => v.Item.EndStationM - v.Item.StartStationM)
                    .ThenBy(v => v.Item.Order).ThenBy(v => v.Item.Key, StringComparer.Ordinal))
                    Add(view.Pieces[0], false, true);
            }

            List<PackedSheet> sheets = new();
            for (int first = 0; first < rows.Count;)
            {
                List<PackedRow> sheetRows = new();
                while (first < rows.Count && sheetRows.Count < rules.RowsPerSheet)
                {
                    sheetRows.Add(new PackedRow(sheetRows.Count + 1, rows[first], used[first]));
                    first++;
                }
                sheets.Add(new PackedSheet(sheets.Count + 1, sheetRows));
            }
            return new ProfileSheetPlan(sheets, issues);
        }

        private static bool ValidRules(PackingRules r) => r != null
            && double.IsFinite(r.RowCapacityM) && r.RowCapacityM > 0
            && r.RowsPerSheet >= 1
            && double.IsFinite(r.SplitRoundM) && r.SplitRoundM > 0 && r.SplitRoundM <= r.RowCapacityM + Tolerance
            && double.IsFinite(r.TitleWidthM) && r.TitleWidthM >= 0
            && double.IsFinite(r.GapM) && r.GapM >= 0
            && double.IsFinite(r.MinPieceM) && r.MinPieceM >= 0
            && double.IsFinite(r.OverlapM) && r.OverlapM >= 0
            && Enum.IsDefined(r.Order);

        private static ProfileSheetPlan ErrorPlan(string code, string message) =>
            new(Array.Empty<PackedSheet>(), new[] { OperationIssue.Error(code, message) });

        // Round in station space, not from the view start. Permit only the contract's absolute station tolerance.
        private static double RoundDown(double limit, double step)
        {
            double quotient = limit / step;
            double upper = Math.Ceiling(quotient) * step;
            return upper <= limit + Tolerance ? upper : Math.Floor(quotient) * step;
        }

        private static bool TrySplit(ProfileViewItem item, PackingRules r, out List<PackedSlot> pieces)
        {
            pieces = new List<PackedSlot>();
            double start = item.StartStationM, end = item.EndStationM;
            while (end - start > r.RowCapacityM + Tolerance)
            {
                double split = RoundDown(start + r.RowCapacityM, r.SplitRoundM);
                double next = split - r.OverlapM;
                // Floating-point stations can become too large to express a forward split. Never loop without progress.
                if (!double.IsFinite(split) || split <= start || next <= start || split >= end) return false;
                pieces.Add(new PackedSlot(item.Key, 0, 0, start, split, 0));
                start = next;
            }
            if (pieces.Count > 0 && end - start < r.MinPieceM - Tolerance)
            {
                double split = RoundDown(end - r.MinPieceM + r.OverlapM, r.SplitRoundM);
                PackedSlot previous = pieces[^1];
                if (split <= previous.StartStationM || split > previous.EndStationM + Tolerance) return false;
                pieces[^1] = previous with { EndStationM = split };
                start = split - r.OverlapM;
            }
            pieces.Add(new PackedSlot(item.Key, 0, 0, start, end, 0));
            for (int i = 0; i < pieces.Count; i++)
            {
                double length = pieces[i].EndStationM - pieces[i].StartStationM;
                if (!double.IsFinite(length) || length <= 0 || length > r.RowCapacityM + Tolerance
                    || (pieces.Count > 1 && length < r.MinPieceM - Tolerance)) return false;
                pieces[i] = pieces[i] with { PieceNo = i + 1, PieceCount = pieces.Count };
            }
            return true;
        }
    }
}
