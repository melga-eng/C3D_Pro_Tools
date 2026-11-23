using Xunit;

namespace C3DProTools.Tests
{
    /// <summary>Type codes for names, from the values in the project's MV and ICT drawings.</summary>
    public class NameCodesTests
    {
        [Theory]
        // MV trenches and duct banks: the number after T is the trench type.
        [InlineData("1,500.00 X 1,700.00 Medium Voltage Trench - Type 01", "T1")]
        [InlineData("1,500.00 X 1,700.00 Medium Voltage Trench - Type 02", "T2")]
        [InlineData("500.00 X 1,700.00 Medium Voltage Trench - Type 04", "T4")]
        [InlineData("TYPE - 04 - 5 X 1 Duct Bank (Under Road Crossing)", "T4")]
        [InlineData("MV 5х3 WAY DUCT BANK TYPE-1", "T1")]
        // MV manholes: loading and type.
        [InlineData("Medium Voltage Manhole For Aircraft Loading - Type 02", "ALMH-T2")]
        [InlineData("Medium Voltage Manhole For Vehicular  Loading - TYPE 02", "VLMH-T2")]
        [InlineData("MV Air Craft Manhole T1", "ALMH-T1")]
        public void MvValuesGiveTheTrenchType(string value, string code) => Assert.Equal(code, NameCodes.Suggest(value));

        [Theory]
        [InlineData("MV 5x3 way Duct Bank (Type-1 & 2)")]   // two types: no single code
        [InlineData("Prop ICT Ductbank (6x4 110 mm TYPE-3B)")] // ICT letter type
        [InlineData("TYPE C TYPE 2 MNH")]                    // not MV
        [InlineData("MV Direct Buried 3C x 500 mm2 Cable - D=103.4 mm")]
        public void NoMvTypeWhenThereIsNone(string value) => Assert.Null(NameCodes.MvTypeNumber(value));

        [Theory]
        [InlineData("Duct IC-3B", "IC3B")]
        [InlineData("KSIA ICT 6x4 - 3B", "3B")]
        public void IctCodesAsBefore(string value, string code) => Assert.Equal(code, NameCodes.Suggest(value));
    }
}
