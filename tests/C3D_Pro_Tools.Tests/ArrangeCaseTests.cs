using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit.Abstractions;

namespace C3DProTools.Tests
{
    /// <summary>
    /// Real sheets replayed: every arrangement case in Cases\ (exported from the Annotation Studio) is planned again, and the
    /// result must be clean: no label over another or over what it keeps clear of, no leader through text. How many labels
    /// found a place, and how many leaders cross, is written to the test output to compare changes by.
    /// </summary>
    public class ArrangeCaseTests
    {
        private readonly ITestOutputHelper _out;
        public ArrangeCaseTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Cases()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Cases");
            List<string> files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList() : new List<string>();
            if (files.Count == 0) yield return new object[] { "" };
            foreach (string f in files) yield return new object[] { Path.GetFileName(f) };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void RealSheet_IsArrangedCleanly(string file)
        {
            if (file.Length == 0) { _out.WriteLine("No arrangement cases in Cases\\ yet."); return; }
            ArrangeCase c = ArrangeCase.Load(Path.Combine(AppContext.BaseDirectory, "Cases", file));
            LabelObstacles baseObs = c.BuildObstacles();
            List<PlanLabel> labels = c.BuildLabels();
            LayoutRules rules = c.Rules();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            LabelPlanner.Plan(baseObs, labels, rules);
            timer.Stop();

            int placed = labels.Count(l => l.Spot.HasValue), crossings = 0, slid = labels.Count(l => l.Spot.HasValue && l.Spot.Value.Ratio.HasValue);
            List<PlanLabel> done = labels.Where(l => l.Spot.HasValue).ToList();
            for (int i = 0; i < done.Count; i++)
            {
                LabelSpot a = done[i].Spot.Value;
                Assert.True(done[i].InArea == null || done[i].InArea(a.Box), $"{done[i].Key} is outside the viewport margin");
                double dx = a.End.X - done[i].Start.X, dy = a.End.Y - done[i].Start.Y;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) <= rules.MaxDistance + 1e-6, $"{done[i].Key} leader is too long");
                Assert.True(baseObs.Blocker(a.Box, rules.Gap - 1e-6, done[i].Networks, done[i].Key) == null, $"{done[i].Key} covers something it must keep clear of");
                Assert.False(baseObs.LeaderCrossesText(done[i].Start, a.End, done[i].Key), $"leader of {done[i].Key} crosses text");
                for (int j = 0; j < done.Count; j++)
                {
                    if (i == j) continue;
                    LabelSpot b = done[j].Spot.Value;
                    Assert.False(a.Box.Overlaps(b.Box), $"{done[i].Key} overlaps {done[j].Key}");
                    Assert.False(b.Box.Hits(done[i].Start, a.End), $"leader of {done[i].Key} crosses {done[j].Key}");
                    if (j > i && LabelLayout.Cross(done[i].Start, a.End, done[j].Start, b.End)) crossings++;
                }
            }
            if (file == "SW00670060000C-C00110-FCC-ZZ-DWG-IC-XX-102054-vp1.json")
            {
                Assert.Equal(33, labels.Count);
                Assert.Equal(labels.Count, placed);
            }
            _out.WriteLine($"{c.Sheet}: {placed} of {labels.Count} placed, {slid} slid along their pipe, {crossings} leader crossing(s), {timer.Elapsed.TotalSeconds:0.00} s.");
        }
    }
}
