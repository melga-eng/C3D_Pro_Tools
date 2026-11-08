using System.Collections.Generic;
using System.IO;

namespace C3DProTools.Tests
{
    /// <summary>Where the style batch saves its copies: subfolders kept, never over a source, never two copies on one path.</summary>
    public class BatchTargetsTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "c3dpro-batch");
        private static string P(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

        [Fact]
        public void SameNamesInDifferentFolders_GetDifferentCopies()
        {
            BatchTargetPlan plan = BatchTargets.Plan(new[] { P("jobs", "north", "Plan.dwg"), P("jobs", "south", "Plan.dwg") }, P("out"), _ => false);
            Assert.True(plan.Ok);
            Assert.Equal(P("out", "north", "Plan.dwg"), plan.Targets[P("jobs", "north", "Plan.dwg")]);
            Assert.Equal(P("out", "south", "Plan.dwg"), plan.Targets[P("jobs", "south", "Plan.dwg")]);
        }

        [Fact]
        public void TheSourceFolderAsOutput_IsRefused()
        {
            BatchTargetPlan plan = BatchTargets.Plan(new[] { P("jobs", "A.dwg"), P("jobs", "B.dwg") }, P("jobs"), _ => true);
            Assert.False(plan.Ok);
            Assert.Equal(2, plan.Problems.Count);
        }

        [Fact]
        public void ACopyLandingOnAnotherSource_IsRefused()
        {
            // jobs\A.dwg and jobs\x\A.dwg, output jobs\x: jobs\A.dwg's copy would be jobs\x\A.dwg, a source.
            BatchTargetPlan plan = BatchTargets.Plan(new[] { P("jobs", "A.dwg"), P("jobs", "x", "B.dwg"), P("jobs", "x", "A.dwg") }, P("jobs", "x"), _ => false);
            Assert.False(plan.Ok);
        }

        [Fact]
        public void ExistingCopies_AreCounted()
        {
            var there = new HashSet<string> { P("out", "A.dwg") };
            BatchTargetPlan plan = BatchTargets.Plan(new[] { P("jobs", "A.dwg"), P("jobs", "B.dwg") }, P("out"), there.Contains);
            Assert.True(plan.Ok);
            Assert.Equal(1, plan.Existing);
        }
    }
}
