using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.Engines;

namespace C3DProTools.Tests.Sheets
{
    public class ProfileSheetPackingTests
    {
        private static ProfileViewItem View(string key, double length, int order = 0, double start = 0) =>
            new(key, key, start, start + length, order);
        private static PackedRow[] Rows(ProfileSheetPlan plan) => plan.Sheets.SelectMany(s => s.Rows).ToArray();
        private static PackedSlot[] Slots(ProfileSheetPlan plan) => Rows(plan).SelectMany(r => r.Slots).ToArray();
        private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-9);

        public static IEnumerable<object[]> Examples()
        {
            yield return new object[] { 0d, 400d, 0d, new[] { 0d, 400d } }; // S1
            yield return new object[] { 0d, 500d, 0d, new[] { 0d, 400d, 400d, 500d } }; // S2
            yield return new object[] { 0d, 1000d, 0d, new[] { 0d, 450d, 450d, 900d, 900d, 1000d } }; // S3
            yield return new object[] { 12.5, 950d, 0d, new[] { 12.5, 460d, 460d, 850d, 850d, 950d } }; // S4
            yield return new object[] { 0d, 900d, 10d, new[] { 0d, 450d, 440d, 810d, 800d, 900d } }; // S5
            yield return new object[] { 0d, 60d, 0d, new[] { 0d, 60d } }; // S6
        }

        [Theory]
        [MemberData(nameof(Examples))]
        public void WorkedExamples(double start, double end, double overlap, double[] expected)
        {
            foreach (PackingOrder order in Enum.GetValues<PackingOrder>())
            {
                ProfileSheetPlan plan = ProfileSheetPacking.Pack(new[] { View("v", end - start, start: start) },
                    new PackingRules { OverlapM = overlap, Order = order });
                Assert.Empty(plan.Issues);
                PackedSlot[] slots = Slots(plan);
                Assert.Equal(expected.Length / 2, slots.Length);
                Assert.Equal(slots.Length, Rows(plan).Length);
                for (int i = 0; i < slots.Length; i++)
                {
                    Near(expected[2 * i], slots[i].StartStationM);
                    Near(expected[2 * i + 1], slots[i].EndStationM);
                    Assert.Equal(i + 1, slots[i].PieceNo);
                    Assert.Equal(slots.Length, slots[i].PieceCount);
                    Near(0, slots[i].OffsetM);
                }
                Assert.Equal(Enumerable.Range(1, plan.Sheets.Count), plan.Sheets.Select(s => s.Index));
                Assert.All(plan.Sheets, s => Assert.Equal(Enumerable.Range(1, s.Rows.Count), s.Rows.Select(r => r.Index)));
            }
        }

        [Fact]
        public void Sheet434132_UsesTwoRows_WithTitleCostsAndOffsets()
        {
            double[] lengths = { 313.56, 83.54, 54.03, 59.20, 108.21, 44.19 };
            ProfileViewItem[] items = lengths.Select((length, i) => View($"v{i}", length, i)).ToArray();
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules { TitleWidthM = 40, GapM = 10 });
            Assert.Empty(plan.Issues);
            Assert.Single(plan.Sheets);
            PackedRow[] rows = Rows(plan);
            Assert.Equal(2, rows.Length);
            Assert.Equal(new[] { "v0", "v1" }, rows[0].Slots.Select(s => s.Key));
            Assert.Equal(new[] { "v2", "v3", "v4", "v5" }, rows[1].Slots.Select(s => s.Key));
            Near(447.10, rows[0].UsedM);
            Near(415.63, rows[1].UsedM);
            Near(363.56, rows[0].Slots[1].OffsetM);
            double[] offsets = { 0, 104.03, 213.23, 371.44 };
            for (int i = 0; i < offsets.Length; i++) Near(offsets[i], rows[1].Slots[i].OffsetM);
        }

        [Fact]
        public void Preserve_IsNextFit_AndSortsByOrderThenOrdinalKey()
        {
            var items = new[] { View("b", 100, 0), View("z", 400, 2), View("A", 100, 0), View("last", 200, 3) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules());
            Assert.Equal(new[] { "A", "b", "z", "last" }, Slots(plan).Select(s => s.Key));
            Assert.Equal(new[] { 200d, 400d, 200d }, Rows(plan).Select(r => r.UsedM));
            Assert.Equal(2, plan.Sheets.Count); // Does not back-fill the first row with "last".
        }

        [Fact]
        public void FirstFitDecreasing_BackFillsFirstRow_AndUsesTieBreakers()
        {
            var items = new[] { View("b", 200, 1), View("a", 200, 1), View("early", 200, 0), View("small", 50, 9) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules { Order = PackingOrder.FirstFitDecreasing });
            PackedRow[] rows = Rows(plan);
            Assert.Equal(new[] { "early", "a", "small" }, rows[0].Slots.Select(s => s.Key));
            Assert.Equal("b", Assert.Single(rows[1].Slots).Key);
            Near(450, rows[0].UsedM);
        }

        [Theory]
        [InlineData(PackingOrder.Preserve)]
        [InlineData(PackingOrder.FirstFitDecreasing)]
        public void SplitPiecesStartNewRows_AndLastPieceCanShare(object orderValue)
        {
            var items = new[] { View("short", 50, 1), View("split", 500, 0) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules { Order = (PackingOrder)orderValue, TitleWidthM = 10, GapM = 5 });
            PackedRow[] rows = Rows(plan);
            Assert.Equal(2, rows.Length);
            Assert.Equal("split", Assert.Single(rows[0].Slots).Key);
            Assert.Equal(new[] { "split", "short" }, rows[1].Slots.Select(s => s.Key));
            Near(115, rows[1].Slots[1].OffsetM);
            Near(165, rows[1].UsedM);
        }

        [Fact]
        public void FirstFitDecreasing_PutsSplitViewsFirst_InOrderThenKey()
        {
            var items = new[] { View("tiny", 10, -1), View("b", 500, 0), View("a", 500, 0), View("first", 500, -2) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules { Order = PackingOrder.FirstFitDecreasing });
            Assert.Equal(new[] { "first", "first", "a", "a", "b", "b" },
                Slots(plan).Where(s => s.PieceCount > 1).Select(s => s.Key));
            Assert.Equal("tiny", Rows(plan)[0].Slots[1].Key);
        }

        public static IEnumerable<object[]> InvalidRules()
        {
            yield return new object[] { null };
            yield return new object[] { new PackingRules { RowCapacityM = double.NaN } };
            yield return new object[] { new PackingRules { RowCapacityM = 0 } };
            yield return new object[] { new PackingRules { RowsPerSheet = 0 } };
            yield return new object[] { new PackingRules { SplitRoundM = 0 } };
            yield return new object[] { new PackingRules { SplitRoundM = 451 } };
            yield return new object[] { new PackingRules { SplitRoundM = double.PositiveInfinity } };
            yield return new object[] { new PackingRules { TitleWidthM = -1 } };
            yield return new object[] { new PackingRules { TitleWidthM = double.NaN } };
            yield return new object[] { new PackingRules { GapM = -1 } };
            yield return new object[] { new PackingRules { GapM = double.NegativeInfinity } };
            yield return new object[] { new PackingRules { MinPieceM = -1 } };
            yield return new object[] { new PackingRules { MinPieceM = double.NaN } };
            yield return new object[] { new PackingRules { OverlapM = -1 } };
            yield return new object[] { new PackingRules { OverlapM = double.PositiveInfinity } };
            yield return new object[] { new PackingRules { Order = (PackingOrder)99 } };
        }

        [Theory]
        [MemberData(nameof(InvalidRules))]
        public void InvalidRuleValues_ReturnErrorAndEmptyPlan(object rules) =>
            AssertError(ProfileSheetPacking.Pack(new[] { View("v", 100) }, (PackingRules)rules), PackingCodes.RulesInvalid);

        [Theory]
        [InlineData(221, 0, 0, 0)] // 2P + R exceeds capacity
        [InlineData(0, 440, 0, 0)] // overlap is not strictly less than C-R
        [InlineData(100, 0, 400, 50)] // title + gap equals capacity
        [InlineData(100, 0, 400, 51)]
        public void InfeasibleRules_ReturnErrorAndEmptyPlan(double minimum, double overlap, double title, double gap) =>
            AssertError(ProfileSheetPacking.Pack(new[] { View("v", 100) }, new PackingRules
            { MinPieceM = minimum, OverlapM = overlap, TitleWidthM = title, GapM = gap }), PackingCodes.RulesInfeasible);

        private static void AssertError(ProfileSheetPlan plan, string code)
        {
            Assert.Empty(plan.Sheets);
            OperationIssue issue = Assert.Single(plan.Issues);
            Assert.Equal(code, issue.Code);
            Assert.Equal(IssueSeverity.Error, issue.Severity);
        }

        [Fact]
        public void InvalidItemsAreSkipped_AndIssuesKeepInputOrder()
        {
            var items = new[] { View("nan", double.NaN), View("zero", 0), View("reverse", -5),
                View("inf", double.PositiveInfinity), null, View("ok", 100) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules());
            Assert.Equal("ok", Assert.Single(Slots(plan)).Key);
            Assert.Equal(5, plan.Issues.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(PackingCodes.ItemInvalid, plan.Issues[i].Code);
                Assert.Equal(IssueSeverity.Error, plan.Issues[i].Severity);
                Assert.Equal($"Items[{i}]", plan.Issues[i].Field);
            }
        }

        [Fact]
        public void DuplicateKeysKeepFirstInputOccurrence_BeforeSorting()
        {
            var items = new[] { View("same", 100, 9), View("SAME", 200, 0), View("same", 300, -1), View("other", 50) };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, new PackingRules());
            Assert.Equal(new[] { PackingCodes.ItemDuplicateKey, PackingCodes.ItemDuplicateKey }, plan.Issues.Select(i => i.Code));
            Near(100, Slots(plan).Single(s => s.Key == "same").EndStationM);
            Assert.All(plan.Issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        }

        [Fact]
        public void EmptyInput_HasNoSheetsOrIssues()
        {
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(Array.Empty<ProfileViewItem>(), new PackingRules());
            Assert.Empty(plan.Sheets);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void CapacityUsesAbsoluteTolerance_AndNegativeStationsRoundAbsolutely()
        {
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(new[] { View("a", 300), View("b", 150 + 5e-10, 1) }, new PackingRules());
            Assert.Single(Rows(plan));
            plan = ProfileSheetPacking.Pack(new[] { View("v", 500, start: -12.5) }, new PackingRules());
            Near(380, Slots(plan)[0].EndStationM);
            Near(487.5, Slots(plan)[1].EndStationM);
        }

        [Theory]
        [InlineData(PackingOrder.Preserve)]
        [InlineData(PackingOrder.FirstFitDecreasing)]
        public void PackingIsDeterministic_AndPreservesCoverageAndCapacity(object orderValue)
        {
            Random random = new(434132);
            var items = Enumerable.Range(0, 60).Select(i => View($"v{i:D2}", 20 + random.NextDouble() * 1500, i, -32.5)).ToArray();
            var rules = new PackingRules { Order = (PackingOrder)orderValue, OverlapM = 10, TitleWidthM = 20, GapM = 5 };
            ProfileSheetPlan plan = ProfileSheetPacking.Pack(items, rules);
            Assert.Empty(plan.Issues);
            Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(ProfileSheetPacking.Pack(items, rules)));
            Assert.All(Rows(plan), row =>
            {
                Assert.InRange(row.UsedM, 0, 450 + 1e-9);
                double used = 0;
                for (int i = 0; i < row.Slots.Count; i++)
                {
                    PackedSlot slot = row.Slots[i];
                    if (i > 0) used += 25;
                    Near(used, slot.OffsetM);
                    used += slot.EndStationM - slot.StartStationM;
                }
                Near(used, row.UsedM);
            });
            foreach (ProfileViewItem item in items)
            {
                PackedSlot[] pieces = Slots(plan).Where(s => s.Key == item.Key).OrderBy(s => s.PieceNo).ToArray();
                Near(item.StartStationM, pieces[0].StartStationM);
                Near(item.EndStationM, pieces[^1].EndStationM);
                for (int i = 1; i < pieces.Length; i++)
                {
                    Near(10, pieces[i - 1].EndStationM - pieces[i].StartStationM);
                    Assert.InRange(pieces[i].EndStationM - pieces[i].StartStationM, 100 - 1e-9, 450 + 1e-9);
                    Near(Math.Round(pieces[i - 1].EndStationM / 10) * 10, pieces[i - 1].EndStationM);
                }
            }
        }
    }
}

