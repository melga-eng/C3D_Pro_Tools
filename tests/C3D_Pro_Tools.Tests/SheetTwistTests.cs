using System;
using System.Collections.Generic;
using C3DProTools.Features.CadExport;
using Xunit;

namespace C3D_Pro_Tools.Tests
{
    public class SheetTwistTests
    {
        // Tile 124: plan viewports turned 59.379°, profile viewports north up.
        private static readonly double Plan = 59.379 * Math.PI / 180;

        private static ViewportWindow PlanWindow() =>
            // 520 x 260 mm paper at 1:500 (view height 130 m), centred on model (1000, 2000).
            ViewportWindow.Of("102054", 520, 260, 0, 0, 130, 1000, 2000, Plan);

        private static ViewportWindow ProfileWindow() =>
            ViewportWindow.Of("434132", 560, 420, 0, 0, 420, 5000, 0, 0);

        [Fact]
        public void PlanWindowIsTurnedAroundItsTarget()
        {
            ViewportWindow w = PlanWindow();
            Assert.True(w.Contains(1000, 2000));
            // 120 m along the sheet's own horizontal is model direction -59.379°, inside the 260 m wide window.
            Assert.True(w.Contains(1000 + 120 * Math.Cos(-Plan), 2000 + 120 * Math.Sin(-Plan)));
            // 200 m along model Y is outside it (the window is turned, not axis aligned).
            Assert.False(w.Contains(1000, 2000 + 200));
        }

        [Fact]
        public void PlanLabelTakesItsViewportTwist()
        {
            var windows = new List<ViewportWindow> { PlanWindow(), ProfileWindow() };
            Assert.Equal(Plan, SheetTwist.For(1000, 2000, false, false, windows, SheetTwist.PlanTwist(windows)), 9);
        }

        [Fact]
        public void ProfileLabelsAreNorthUpEvenInsideAPlanWindow()
        {
            var windows = new List<ViewportWindow> { PlanWindow() };
            Assert.Equal(0, SheetTwist.For(1000, 2000, true, false, windows, Plan));
            Assert.Equal(0, SheetTwist.For(1000, 2000, false, true, windows, Plan));
        }

        [Fact]
        public void LabelOnNoSheetTakesThePlanTwist()
        {
            var windows = new List<ViewportWindow> { PlanWindow(), PlanWindow(), ProfileWindow() };
            Assert.Equal(Plan, SheetTwist.For(-9999, -9999, false, false, windows, SheetTwist.PlanTwist(windows)), 9);
        }

        [Fact]
        public void LabelOnlyOnANorthUpChosenSheetStaysNorthUp()
        {
            // Sheet A shows the area north up and is the one exported; sheet B (turned) is not chosen, so it is not in the list.
            var windows = new List<ViewportWindow> { ViewportWindow.Of("A", 520, 260, 0, 0, 130, 1000, 2000, 0) };
            Assert.Equal(0, SheetTwist.For(1000, 2000, false, false, windows, Plan));
        }

        [Fact]
        public void NorthUpDrawingHasNoPlanTwist()
        {
            Assert.Equal(0, SheetTwist.PlanTwist(new[] { ProfileWindow() }));
            Assert.Equal(0, SheetTwist.Normal(2 * Math.PI - 1e-9));
        }

        [Theory]
        [InlineData("AECC_PIPE_PROFILE_LABEL", true)]
        [InlineData("AECC_PROFILEDATABAND_LABELGROUP", true)]
        [InlineData("AECC_STRUCTURE_PROFILE_LABEL", true)]
        [InlineData("AECC_PIPE_LABEL", false)]
        [InlineData("AECC_ALIGNMENT_STATION_LABEL_GROUP", false)]
        public void ProfileKindByObjectName(string name, bool profile) => Assert.Equal(profile, SheetTwist.IsProfileKind(name));

        [Fact]
        public void FileNameIsTheLayoutName()
        {
            Assert.Equal("SW00670060000C-C00110-FCC-ZZ-DWG-IC-XX-102054.dwg", SheetTwist.FileName("SW00670060000C-C00110-FCC-ZZ-DWG-IC-XX-102054"));
            Assert.Equal("MNH 121_123.dwg", SheetTwist.FileName("MNH 121/123"));
        }
    }
}
