using System.Collections.Generic;
using System.Globalization;
using C3DProTools.Tests.Helpers;

namespace C3DProTools.Tests
{
    public class CoverEvaluationTests
    {
        // The status messages are formatted with the current culture; the tests read them with a '.' decimal point.
        public CoverEvaluationTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        private static ProfilePipe Pipe(CoverRoute route, double z0, double z1) =>
            new ProfilePipe { Section = 0, S0 = 0, S1 = route.Length, Z0 = z0, Z1 = z1, Name = "P1", Source = route.Pipes[0] };

        [Fact]
        public void CoverBelowMin_IsFlagged()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            // Outside top at 99.52 + 0.18 = 99.70: 0.30 m cover.
            CoverRow row = CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 99.52, 99.52) }, o)[0];
            Assert.True(row.Failed);
            Assert.Contains("Cover below min (0.30 m)", row.Status);
            Assert.Equal(0.30, row.MinCover, 6);
        }

        [Fact]
        public void CoverWithinOneMillimetreOfTheLimit_Passes()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            // 0.5995 m cover against a 0.6 m minimum: inside the 1 mm tolerance today.
            CoverRow row = CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 99.2205, 99.2205) }, o)[0];
            Assert.False(row.Failed, row.Status);
        }

        [Fact]
        public void Gravity_AdverseAndFlatSlopes_AreFlagged()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            CoverOptions o = Routes.Options(CoverMode.Gravity);
            Assert.Contains("Adverse slope", CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 98.0, 98.1) }, o)[0].Status);
            Assert.Contains("Slope below min", CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 98.0, 98.0) }, o)[0].Status);
            Assert.False(CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 98.1, 98.0) }, o)[0].Failed);
        }

        [Fact]
        public void Crossing_ClashAndShortClearance_AreNamed()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            Routes.AddCrossing(route, "W-12", 10, 99.0, 98.8);
            CoverOptions o = Routes.Options(CoverMode.Dry);
            o.MaxCover = 5;
            o.CrossingClearance = 0.3;

            Assert.Contains("Clashes with W-12", CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 98.9, 98.9) }, o)[0].Status);
            // Outside bottom of the route 99.2 - 0.18 = 99.02, 0.02 m above the crossing's top: clear, but closer than 0.3 m.
            Assert.Contains("Only 0.02 m clear of W-12", CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 99.2, 99.2) }, o)[0].Status);
        }

        [Fact]
        public void Csv_UsesInvariantNumbersWhateverTheCulture()
        {
            CoverRoute route = Routes.Build(new[] { new SectionSpec { Length = 20 } }, s => 100.0);
            List<CoverRow> rows = CoverEvaluation.Evaluate(route, new List<ProfilePipe> { Pipe(route, 99.0, 98.9) }, Routes.Options(CoverMode.Dry));
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string csv = CoverEvaluation.ToCsv(rows);
                Assert.Contains("\"P1\",0.000,20.000,20.000,-0.500,", csv);
            }
            finally { CultureInfo.CurrentCulture = before; }
        }
    }
}
