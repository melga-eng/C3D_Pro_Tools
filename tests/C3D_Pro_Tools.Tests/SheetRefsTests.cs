using System.Collections.Generic;

namespace C3DProTools.Tests
{
    /// <summary>Match line references rewritten when sheets are renamed.</summary>
    public class SheetRefsTests
    {
        [Fact]
        public void SwappingTwoSheets_SwapsTheirReferences()
        {
            var swap = new Dictionary<string, string> { ["S-01"] = "S-02", ["S-02"] = "S-01" };
            Assert.Equal("SEE S-02", SheetRefs.Replace("SEE S-01", swap));
            Assert.Equal("SEE S-01", SheetRefs.Replace("SEE S-02", swap));
            Assert.Equal("S-02 / S-01", SheetRefs.Replace("S-01 / S-02", swap));
        }

        [Fact]
        public void ANameInsideALongerName_IsLeftAlone()
        {
            var map = new Dictionary<string, string> { ["S-1"] = "S-9" };
            Assert.Equal("SEE S-10", SheetRefs.Replace("SEE S-10", map));
            Assert.Equal("SEE S-1-A", SheetRefs.Replace("SEE S-1-A", map));
            Assert.Equal("see S-9.", SheetRefs.Replace("see s-1.", map));
        }

        [Fact]
        public void TheLongerOfTwoOverlappingNames_Wins()
        {
            var map = new Dictionary<string, string> { ["S-01"] = "X", ["S-01A"] = "Y" };
            Assert.Equal("SEE Y AND X", SheetRefs.Replace("SEE S-01A AND S-01", map));
        }
    }
}
