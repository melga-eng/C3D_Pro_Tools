namespace C3DProTools.Tests
{
    public class AnnotLabelRulesTests
    {
        [Theory]
        [InlineData(0.0, false)]
        [InlineData(0.479, false)]
        [InlineData(0.48, true)]
        [InlineData(0.5, true)]
        [InlineData(0.52, true)]
        [InlineData(0.521, false)]
        [InlineData(1.0, false)]
        public void PipeRequiresSamePosition(double ratio, bool matches)
        {
            var requested = new AnnotLabelPosition(LabelKind.PlanPipe, "Style", "", 0.5);
            Assert.Equal(matches, AnnotLabelRules.TakeExisting(new() { requested with { Ratio = ratio } }, requested));
        }

        [Fact]
        public void TwoStartLabelsDoNotSatisfyBothEnds()
        {
            var start = new AnnotLabelPosition(LabelKind.PlanPipe, "Style", "", 0);
            var existing = new List<AnnotLabelPosition> { start, start };
            Assert.True(AnnotLabelRules.TakeExisting(existing, start));
            Assert.False(AnnotLabelRules.TakeExisting(existing, start with { Ratio = 1 }));
        }

        [Fact]
        public void ExistingLabelIsConsumedOnce()
        {
            var label = new AnnotLabelPosition(LabelKind.ProfilePipe, "Style", "V1", 0.5);
            var existing = new List<AnnotLabelPosition> { label };
            Assert.False(AnnotLabelRules.TakeExisting(existing, label with { View = "V2" }));
            Assert.False(AnnotLabelRules.TakeExisting(existing, label with { Kind = LabelKind.PlanPipe }));
            Assert.False(AnnotLabelRules.TakeExisting(existing, label with { Style = "Other" }));
            Assert.True(AnnotLabelRules.TakeExisting(existing, label));
            Assert.False(AnnotLabelRules.TakeExisting(existing, label));
        }

        [Fact]
        public void UnreadablePipeRatioDoesNotMatch()
        {
            var label = new AnnotLabelPosition(LabelKind.ProfilePipe, "Style", "V1", 0.5);
            Assert.False(AnnotLabelRules.TakeExisting(new() { label with { Ratio = null } }, label));
        }

        [Fact]
        public void OnlyLabelOfItsStyleMatchesWhereverItWasSlid()
        {
            var existing = new List<AnnotLabelPosition> { new AnnotLabelPosition(LabelKind.PlanPipe, "Name", "0", 0.31) };
            Assert.True(AnnotLabelRules.TakeExisting(existing, new AnnotLabelPosition(LabelKind.PlanPipe, "Name", "0", null)));
        }

        [Fact]
        public void StructuresIgnoreRatioButRespectProfileView()
        {
            var label = new AnnotLabelPosition(LabelKind.ProfileStructure, "Style", "V1", null);
            var existing = new List<AnnotLabelPosition> { label };
            Assert.False(AnnotLabelRules.TakeExisting(existing, label with { View = "V2" }));
            Assert.True(AnnotLabelRules.TakeExisting(existing, label with { Ratio = 1 }));
        }

        [Theory]
        [InlineData(0, "PlanPipe:Style")]
        [InlineData(1, "PlanStructure:Style")]
        [InlineData(2, "ProfilePipe:Style")]
        [InlineData(3, "ProfileStructure:Style")]
        public void StyleKeyKeepsItsFormat(int kind, string expected)
            => Assert.Equal(expected, AnnotLabelRules.StyleKey((LabelKind)kind, "Style"));

        [Theory]
        // skip, start at a manhole, end at a manhole, left out
        [InlineData(SkipAtManhole.No, true, true, false)]
        [InlineData(SkipAtManhole.Start, true, false, true)]
        [InlineData(SkipAtManhole.Start, false, true, false)]
        [InlineData(SkipAtManhole.End, false, true, true)]
        [InlineData(SkipAtManhole.End, true, false, false)]
        [InlineData(SkipAtManhole.Both, true, false, true)]
        [InlineData(SkipAtManhole.Both, false, true, true)]
        [InlineData(SkipAtManhole.Both, false, false, false)]
        internal void LabelIsLeftOutOnlyAtTheChosenManholeEnd(SkipAtManhole skip, bool start, bool end, bool leftOut)
            => Assert.Equal(leftOut, AnnotLabelRules.LeftOut(skip, start, end));
    }
}
