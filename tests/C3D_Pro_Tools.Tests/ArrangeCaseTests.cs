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
            if (Environment.GetEnvironmentVariable("C3D_MAXD") is string md) rules.MaxDistance = double.Parse(md);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            LabelPlanner.Plan(baseObs, labels, rules);
            timer.Stop();

            if (Environment.GetEnvironmentVariable("C3D_DUMP") is string dumpFile)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var lines = new List<string>();
                foreach (var ob in c.Obstacles) lines.Add("O " + ob.Group + " " + string.Join(" ", ob.Points.Select(p => p[0].ToString("0.0", ci) + "," + p[1].ToString("0.0", ci))));
                lines.Add("V " + string.Join(" ", c.Viewport.Select(p => p[0].ToString("0.0", ci) + "," + p[1].ToString("0.0", ci))));
                foreach (var l in labels)
                {
                    string a = l.Anchor.X.ToString("0.0", ci) + "," + l.Anchor.Y.ToString("0.0", ci);
                    if (!l.Spot.HasValue) { lines.Add("U " + a + " " + l.Key.Replace(' ', '_')); continue; }
                    var s = l.Spot.Value;
                    lines.Add(string.Format(ci, "L {0:0.0},{1:0.0} {2:0.0},{3:0.0} {4:0.0},{5:0.0},{6:0.0},{7:0.0} {8}", l.Start.X, l.Start.Y, s.End.X, s.End.Y, s.Box.MinX, s.Box.MinY, s.Box.MaxX, s.Box.MaxY, l.Key.Replace(' ', '_')));
                }
                File.WriteAllLines(dumpFile, lines);
            }

            int placed = labels.Count(l => l.Spot.HasValue), crossings = 0, slid = labels.Count(l => l.Spot.HasValue && l.Spot.Value.Ratio.HasValue);
            List<PlanLabel> done = labels.Where(l => l.Spot.HasValue).ToList();
            for (int i = 0; i < done.Count; i++)
            {
                LabelSpot a = done[i].Spot.Value;
                Assert.True(done[i].InArea == null || done[i].InArea(a.Box), $"{done[i].Key} is outside the viewport margin");
                double dx = a.End.X - done[i].Start.X, dy = a.End.Y - done[i].Start.Y;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) <= rules.Crowded + 1e-6, $"{done[i].Key} leader is too long");
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
                // 2 labels at the junction manhole still find no place (2026-11-13); the arranger puts them back and lists them.
                Assert.True(placed >= 31, $"placed {placed} of {labels.Count}");
                Assert.Equal(0, crossings);
                Assert.True(timer.Elapsed.TotalSeconds < 20, $"Replay took {timer.Elapsed.TotalSeconds:0.00} s");
                double mean = done.Average(l => Math.Sqrt(Math.Pow(l.Spot.Value.End.X - l.Start.X, 2) + Math.Pow(l.Spot.Value.End.Y - l.Start.Y, 2)));
                Assert.True(mean <= 30, $"Mean leader length {mean:0.00} mm exceeds 30 mm");
                foreach (var l in done)
                {
                    var spot = l.Spot.Value;
                    Assert.False(spot.Covers(l.Start), $"{l.Key} covers its anchor");
                    Assert.True(Math.Sqrt(Math.Pow(spot.End.X - l.Start.X, 2) + Math.Pow(spot.End.Y - l.Start.Y, 2)) >= rules.MinLeader - 1e-6);
                    foreach (var other in done.Where(x => x != l))
                        Assert.False(spot.Box.Grow(rules.Gap - 1e-6).Overlaps(other.Spot.Value.Box), $"{l.Key} is closer than Gap to {other.Key}");
                }
            }
            _out.WriteLine($"{c.Sheet}: {placed} of {labels.Count} placed, {slid} slid along their pipe, {crossings} leader crossing(s), {timer.Elapsed.TotalSeconds:0.00} s.");
        }
    }
}
