using System;
using System.IO;
using C3DProTools.Features.Shared.Contracts;
using Xunit;

namespace C3DProTools.Tests
{
    /// <summary>Where the settings come from: the test file when one is set, else the drawing, else the built-in defaults.</summary>
    [Collection("SettingsSource")]
    public class SettingsSourceTests
    {
        [Fact]
        public void TestFile_WinsOverTheDrawing_AndAMissingOneGivesTheDefaults()
        {
            string file = Path.Combine(Path.GetTempPath(), $"c3dpro-settings-{Guid.NewGuid():N}.json");
            Func<string, string> before = SettingsSource.FromDrawing;
            try
            {
                SettingsSource.FromDrawing = _ => "{ \"from\": \"drawing\" }";
                File.WriteAllText(file, "{ \"from\": \"file\" }");
                Environment.SetEnvironmentVariable(SettingsSource.TestFileVariable, file);
                Assert.Contains("file", SettingsSource.Text("x.dwg", out _));
                File.Delete(file);
                Assert.Equal(SettingsSource.Defaults, SettingsSource.Text("x.dwg", out _));

                Environment.SetEnvironmentVariable(SettingsSource.TestFileVariable, null);
                Assert.Contains("drawing", SettingsSource.Text("x.dwg", out string source));
                Assert.Contains("saved in this drawing", source);
            }
            finally
            {
                Environment.SetEnvironmentVariable(SettingsSource.TestFileVariable, null);
                SettingsSource.FromDrawing = before;
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [Fact]
        public void Defaults_AreValidJson_WithEverySection()
        {
            using var d = System.Text.Json.JsonDocument.Parse(SettingsSource.Defaults);
            foreach (string s in new[] { "profileViews", "networks", "pavement", "labels", "labelRules" })
                Assert.True(d.RootElement.TryGetProperty(s, out _), s);
        }
    }
}
