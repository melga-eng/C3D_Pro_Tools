using System.Collections.Generic;
using System.IO;

namespace C3DProTools.Tests
{
    /// <summary>The diagnostic log, and the filter that keeps property probes from hiding programming errors.</summary>
    public class C3DProLoggerTests
    {
        private static Exception Thrown(Action a)
        {
            try { a(); }
            catch (Exception ex) { return ex; }
            throw new InvalidOperationException("nothing was thrown");
        }

        [Fact]
        public void ProbeFilter_SwallowsWhatAnApiThrows()
        {
            Assert.True(C3DProLogger.IsProbeFailure(new InvalidOperationException("property not set")));
            Assert.True(C3DProLogger.IsProbeFailure(new NotSupportedException()));
            Assert.True(C3DProLogger.IsProbeFailure(new ArgumentException("bad value")));
        }

        [Fact]
        public void ProbeFilter_LetsProgrammingErrorsInThisCodeThrough()
        {
            // The test project compiles the logger in, so "this code" is the test assembly here.
            string missing = null;
            Assert.False(C3DProLogger.IsProbeFailure(Thrown(() => _ = missing.Length)));
            int[] none = new int[0];
            Assert.False(C3DProLogger.IsProbeFailure(Thrown(() => _ = none[3])));
            // Raised inside the .NET libraries on this code's behalf: still this code's error.
            Assert.False(C3DProLogger.IsProbeFailure(Thrown(() => _ = new List<int>()[2])));
            Assert.False(C3DProLogger.IsProbeFailure(Thrown(() => _ = new Dictionary<string, int>()["x"])));
        }

        [Fact]
        public void ProbeFilter_IsUsableInACatchClause()
        {
            bool swallowed = false;
            try { throw new InvalidOperationException(); }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { swallowed = true; }
            Assert.True(swallowed);
            Assert.Throws<NullReferenceException>(() =>
            {
                string missing = null;
                try { _ = missing.Length; }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            });
        }

        [Fact]
        public void Log_WritesUnderLocalAppData_AndNeverThrows()
        {
            Assert.EndsWith(Path.Combine("C3DProTools", "logs"), C3DProLogger.Folder);
            Assert.EndsWith(".log", C3DProLogger.LogPath);
            C3DProLogger.Info("test entry");
            C3DProLogger.Warn("test warning", new InvalidOperationException("example"));
            Assert.True(File.Exists(C3DProLogger.LogPath));
        }
    }
}
