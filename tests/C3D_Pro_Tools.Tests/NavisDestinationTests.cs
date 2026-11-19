using System.Collections.Generic;

namespace C3DProTools.Tests
{
    /// <summary>Where imported parts go: a drawing network they belong to, the one chosen, or a new one.</summary>
    public class NavisDestinationTests
    {
        private static DrawingNetwork Ict() => new DrawingNetwork
        {
            Name = "ICT-124", PartsList = "ICT parts",
            Structures =
            {
                new DrawingStructure { Name = "MH-1", X = 0, Y = 0, Rim = 10, Sump = 8, Circular = true, Diameter = 1.5 },
                new DrawingStructure { Name = "MH-2", X = 50, Y = 0, Rim = 10, Sump = 8, Circular = true, Diameter = 1.5 },
            },
            Pipes = { new DrawingPipe { Name = "P-1", Start = new V3(0.75, 0, 9), End = new V3(49.25, 0, 9) } }
        };

        private static ImportCandidate Pipe(string name, V3 a, V3 b, string network = "") =>
            new ImportCandidate { Name = name, Start = a, End = b, Network = network, Detected = ImportKind.Pipe, Action = ImportKind.Pipe };

        private static Dictionary<ImportCandidate, RowDestination> Decide(DestinationMode mode, string chosen, params ImportCandidate[] rows) =>
            NavisDestination.Decide(rows, new List<DrawingNetwork> { Ict(), new DrawingNetwork { Name = "DREF", Editable = false } }, mode, chosen, r => "NAVIS - " + (r.Network.Length > 0 ? r.Network : "Import"));

        [Fact]
        public void Auto_SameNetworkName_JoinsTheDrawingNetwork()
        {
            ImportCandidate p = Pipe("New", new V3(100, 0, 9), new V3(120, 0, 9), "ICT-124");
            RowDestination d = Decide(DestinationMode.Auto, "", p)[p];
            Assert.True(d.Existing);
            Assert.Equal("ICT-124", d.Network);
            Assert.False(d.InDrawing);
        }

        [Fact]
        public void Auto_PipeEndInAManhole_JoinsItsNetwork()
        {
            // A model from elsewhere: no network name, but it starts inside MH-2.
            ImportCandidate p = Pipe("Duct", new V3(50.6, 0.2, 9), new V3(80, 0, 9));
            RowDestination d = Decide(DestinationMode.Auto, "", p)[p];
            Assert.True(d.Existing);
            Assert.Equal("ICT-124", d.Network);
        }

        [Fact]
        public void Auto_OnlyTheLinkedRunJoins_AFarPartOfTheSameGroupStaysNew()
        {
            // Same layer, so one group: the run from MH-2 (two linked pipes) joins, the far pipe does not.
            ImportCandidate a = Pipe("A", new V3(50.6, 0.2, 9), new V3(80, 0, 9));
            ImportCandidate b = Pipe("B", new V3(80, 0.05, 9), new V3(95, 0, 9));
            ImportCandidate far = Pipe("Far", new V3(5000, 0, 9), new V3(5020, 0, 9));
            var all = Decide(DestinationMode.Auto, "", a, b, far);
            Assert.Equal("ICT-124", all[a].Network);
            Assert.Equal("ICT-124", all[b].Network);
            Assert.False(all[far].Existing);
        }

        [Fact]
        public void Auto_DuctCarriedOnFromAnExistingPipeEnd_JoinsItsNetwork()
        {
            // Starts where P-1 ends (outside the manhole wall), with no network name.
            ImportCandidate p = Pipe("Carry on", new V3(49.3, 0.05, 9), new V3(49.3, -30, 9));
            Assert.Equal("ICT-124", Decide(DestinationMode.Auto, "", p)[p].Network);
        }

        [Fact]
        public void Auto_PartAlreadyDrawnWithoutName_FindsItsNetwork()
        {
            ImportCandidate p = Pipe("Copy", new V3(0.75, 0, 9), new V3(49.25, 0, 9));
            RowDestination d = Decide(DestinationMode.Auto, "", p)[p];
            Assert.Equal("ICT-124", d.Network);
            Assert.True(d.InDrawing);
        }

        [Fact]
        public void Auto_NothingInCommon_MakesANewNetwork()
        {
            ImportCandidate p = Pipe("Far", new V3(500, 0, 9), new V3(520, 0, 9));
            RowDestination d = Decide(DestinationMode.Auto, "", p)[p];
            Assert.False(d.Existing);
            Assert.Equal("NAVIS - Import", d.Network);
        }

        [Fact]
        public void Auto_PartAlreadyDrawn_IsMarkedInDrawing()
        {
            // The same pipe, drawn the other way round and 2 cm off: it is P-1.
            ImportCandidate p = Pipe("P-1", new V3(49.25, 0.02, 9), new V3(0.75, 0, 9), "ICT-124");
            RowDestination d = Decide(DestinationMode.Auto, "", p)[p];
            Assert.True(d.InDrawing);
            Assert.Equal("P-1", d.InDrawingPart);
        }

        [Fact]
        public void Auto_StructureOnAnExistingOne_IsInDrawing()
        {
            ImportCandidate s = new ImportCandidate { Name = "MH", X = 50.05, Y = 0, Rim = 10, Sump = 8, Detected = ImportKind.Structure, Action = ImportKind.Structure };
            RowDestination d = Decide(DestinationMode.Auto, "", s)[s];
            Assert.Equal("ICT-124", d.Network);
            Assert.True(d.InDrawing);
            Assert.Equal("MH-2", d.InDrawingPart);
        }

        [Fact]
        public void OneNetwork_AllGoThere_ButAReferenceIsNeverATarget()
        {
            ImportCandidate p = Pipe("Far", new V3(500, 0, 9), new V3(520, 0, 9));
            Assert.Equal("ICT-124", Decide(DestinationMode.OneNetwork, "ICT-124", p)[p].Network);
            // A data-shortcut reference cannot take parts: the row falls back to a new network.
            Assert.False(Decide(DestinationMode.OneNetwork, "DREF", p)[p].Existing);
        }

        [Fact]
        public void NewNetworks_IgnoresMatches_AndSolidsHaveNoDestination()
        {
            ImportCandidate p = Pipe("Duct", new V3(50.6, 0.2, 9), new V3(80, 0, 9), "ICT-124");
            ImportCandidate solid = new ImportCandidate { Name = "Slab", Detected = ImportKind.Solid, Action = ImportKind.Solid };
            var all = Decide(DestinationMode.NewNetworks, "", p, solid);
            Assert.Equal("NAVIS - ICT-124", all[p].Network);
            Assert.False(all[p].Existing);
            Assert.False(all.ContainsKey(solid));
        }

        [Fact]
        public void RowChoice_WinsOverTheMode()
        {
            ImportCandidate p = Pipe("Far", new V3(500, 0, 9), new V3(520, 0, 9));
            var d = NavisDestination.Decide(new[] { p }, new List<DrawingNetwork> { Ict() }, DestinationMode.NewNetworks, "", r => "NAVIS - Import",
                new Dictionary<ImportCandidate, string> { [p] = "ICT-124" })[p];
            Assert.True(d.Existing);
            Assert.Equal("chosen by you", d.Reason);
        }

        [Fact]
        public void PipeEnd_AboveTheRim_DoesNotMeetTheManhole()
        {
            Assert.Null(NavisDestination.StructureAt(Ict(), new V3(0, 0, 12)));
            Assert.NotNull(NavisDestination.StructureAt(Ict(), new V3(0.8, 0, 9)));
        }
    }
}
