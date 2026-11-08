using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Tests
{
    /// <summary>Merging networks: copies found by place, the most used copy kept, other sizes left to choose, the rest moved.</summary>
    public class NetworkMergeTests
    {
        private static MergePart Pipe(string id, string net, double x0, double x1, string size = "DN300", int uses = 0, bool inTarget = false) =>
            new MergePart { Id = id, Name = id, Network = net, IsPipe = true, A = (x0, 0, 100), B = (x1, 0, 99.5), Size = size, Uses = uses, InTarget = inTarget };

        private static MergePart Structure(string id, string net, double x, string size = "MH-1200", int uses = 0, bool inTarget = false) =>
            new MergePart { Id = id, Name = id, Network = net, IsPipe = false, A = (x, 0, 98), Size = size, Uses = uses, InTarget = inTarget };

        [Fact]
        public void FourAndSixPipesSharingThree_GiveSevenPipes()
        {
            // Network A: pipes at 0-10, 10-20, 20-30, 30-40. Network B (a copy, extended): 10-20, 20-30, 30-40 and 40-50, 50-60, 60-70.
            List<MergePart> parts = new List<MergePart>();
            for (int i = 0; i < 4; i++) parts.Add(Pipe("A" + i, "A", i * 10, i * 10 + 10, inTarget: true));
            for (int i = 1; i < 7; i++) parts.Add(Pipe("B" + i, "B", i * 10, i * 10 + 10));
            List<MergeRow> rows = NetworkMerge.Plan(parts);
            Assert.Equal(10, rows.Count);
            Assert.Equal(3, rows.Count(r => r.Action == MergeAction.DeleteCopy));
            Assert.Equal(7, rows.Count(r => r.Action == MergeAction.Move || r.Action == MergeAction.Stay));
            // The target's copies stay; B's copies of them go; B's own pipes move.
            Assert.All(rows.Where(r => r.Action == MergeAction.DeleteCopy), r => Assert.Equal("B", r.Part.Network));
            Assert.Equal(3, rows.Count(r => r.Action == MergeAction.Move));
        }

        [Fact]
        public void AReversedCopy_IsStillACopy()
        {
            Assert.True(NetworkMerge.SamePlace(Pipe("a", "A", 0, 10), new MergePart { IsPipe = true, A = (10, 0, 99.5), B = (0, 0, 100) }));
            // A pipe at another level in the same plan place is a different pipe.
            Assert.False(NetworkMerge.SamePlace(Pipe("a", "A", 0, 10), new MergePart { IsPipe = true, A = (0, 0, 99), B = (10, 0, 98.5) }));
            Assert.True(NetworkMerge.SamePlace(Structure("m", "A", 5), new MergePart { IsPipe = false, A = (5.0004, 0, 90) }));
        }

        [Fact]
        public void TheMostUsedCopyStays_EvenOutsideTheTarget()
        {
            List<MergeRow> rows = NetworkMerge.Plan(new[] { Pipe("t", "T", 0, 10, inTarget: true), Pipe("labelled", "B", 0, 10, uses: 3) });
            Assert.Equal(MergeAction.Move, rows.Single(r => r.Part.Id == "labelled").Action);
            MergeRow copy = rows.Single(r => r.Part.Id == "t");
            Assert.Equal(MergeAction.DeleteCopy, copy.Action);
            Assert.Equal("labelled", copy.Keeper.Id);
        }

        [Fact]
        public void SamePlaceOtherSize_IsLeftToChoose()
        {
            List<MergeRow> rows = NetworkMerge.Plan(new[] { Pipe("a", "A", 0, 10, "DN300", inTarget: true), Pipe("b", "B", 0, 10, "DN450") });
            Assert.Equal(MergeAction.SamePlaceOtherSize, rows.Single(r => r.Part.Id == "b").Action);
            Assert.Equal(MergeAction.Stay, rows.Single(r => r.Part.Id == "a").Action);
        }

        [Fact]
        public void CopiesWithinOneNetwork_AreFoundToo_AndCountedPerNetwork()
        {
            var parts = new[] { Structure("m1", "A", 0), Structure("m1copy", "A", 0), Structure("m2", "A", 50), Pipe("p", "B", 0, 50) };
            List<MergeRow> rows = NetworkMerge.Plan(parts);
            Assert.Single(rows, r => r.Action == MergeAction.DeleteCopy);
            Assert.Equal(1, NetworkMerge.CopiesByNetwork(parts)["A"]);
            Assert.False(NetworkMerge.CopiesByNetwork(parts).ContainsKey("B"));
        }
    }
}
