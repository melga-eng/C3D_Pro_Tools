using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Tests
{
    /// <summary>Merging networks: copies found by place, the most used copy kept, other sizes left to choose, the rest moved.</summary>
    public class NetworkMergeTests
    {
        private static MergePart Pipe(string id, string net, double x0, double x1, string size = "DN300", int uses = 0, bool inTarget = false, string family = "Concrete Pipe") =>
            new MergePart { Id = id, Name = id, Network = net, IsPipe = true, A = (x0, 0, 100), B = (x1, 0, 99.5), Family = family, Size = size, Uses = uses, InTarget = inTarget };

        private static MergePart Structure(string id, string net, double x, string size = "MH-1200", int uses = 0, bool inTarget = false, string family = "Manhole") =>
            new MergePart { Id = id, Name = id, Network = net, IsPipe = false, A = (x, 0, 98), Family = family, Size = size, Uses = uses, InTarget = inTarget };

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

        [Fact]
        public void AChainOfNearParts_NeverDeletesAPartFarFromItsKeeper()
        {
            // a–b and b–c are each 0.8 mm apart (within 1 mm), but a–c are 1.6 mm apart: c is not a copy of a.
            MergePart a = Structure("a", "A", 0, uses: 5), b = Structure("b", "B", 0.0008), c = Structure("c", "C", 0.0016);
            List<MergeRow> rows = NetworkMerge.Plan(new[] { a, b, c });
            Assert.All(rows.Where(r => r.Keeper != null), r => Assert.True(NetworkMerge.SamePlace(r.Part, r.Keeper)));
            Assert.Equal(MergeAction.DeleteCopy, rows.Single(r => r.Part.Id == "b").Action);
            Assert.Equal(MergeAction.Move, rows.Single(r => r.Part.Id == "c").Action);
        }

        [Fact]
        public void ACurvedPipe_IsNotACopyOfAStraightOne_BetweenTheSameEnds()
        {
            MergePart straight = Pipe("s", "A", 0, 10, inTarget: true), curved = Pipe("c", "B", 0, 10);
            curved.Bulge = 0.2;
            Assert.False(NetworkMerge.SamePlace(straight, curved));
            Assert.DoesNotContain(NetworkMerge.Plan(new[] { straight, curved }), r => r.Action == MergeAction.DeleteCopy);
            // The same arc drawn the other way round is a copy.
            MergePart reversed = new MergePart { IsPipe = true, A = curved.B, B = curved.A, Bulge = -0.2 };
            Assert.True(NetworkMerge.SamePlace(curved, reversed));
            // A bulge that could not be read matches nothing.
            Assert.False(NetworkMerge.SamePlace(straight, new MergePart { IsPipe = true, A = straight.A, B = straight.B, Bulge = double.NaN }));
        }

        [Fact]
        public void AnotherFamilyOrAnUnknownSize_IsLeftToChoose_NotDeleted()
        {
            MergePart concrete = Pipe("concrete", "A", 0, 10, inTarget: true), pvc = Pipe("pvc", "B", 0, 10), unknown = Pipe("unknown", "C", 0, 10, size: "");
            concrete.Family = "Concrete Pipe"; pvc.Family = "PVC Pipe"; unknown.Family = "Concrete Pipe";
            List<MergeRow> rows = NetworkMerge.Plan(new[] { concrete, pvc, unknown });
            Assert.Equal(MergeAction.SamePlaceOtherSize, rows.Single(r => r.Part.Id == "pvc").Action);
            Assert.Equal(MergeAction.SamePlaceOtherSize, rows.Single(r => r.Part.Id == "unknown").Action);
        }

        [Fact]
        public void PartsWithoutAFamily_AreNeverDeletedAsCopies()
        {
            // Two duct banks (no part family) of the same size name in the same place: left for you to choose, not deleted.
            List<MergeRow> rows = NetworkMerge.Plan(new[] { Pipe("d1", "A", 0, 10, "Duct 2x3", inTarget: true, family: ""), Pipe("d2", "B", 0, 10, "Duct 2x3", family: "") });
            Assert.DoesNotContain(rows, r => r.Action == MergeAction.DeleteCopy);
            Assert.Equal(MergeAction.SamePlaceOtherSize, rows.Single(r => r.Part.Id == "d2").Action);
        }
    }
}
