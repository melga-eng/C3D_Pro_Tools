using System.IO;
using System.Runtime.CompilerServices;

namespace C3DProTools.Tests.Helpers
{
    /// <summary>
    /// Approval snapshots kept next to the tests in Snapshots\. A missing snapshot is written from the current result and
    /// the test fails once, so a new baseline is always looked at before it is trusted. Delete a file to re-baseline it.
    /// </summary>
    internal static class Snapshot
    {
        public static void Match(string name, string actual, [CallerFilePath] string caller = "")
        {
            string folder = Path.Combine(Path.GetDirectoryName(caller), "Snapshots");
            string path = Path.Combine(folder, name + ".txt");
            actual = actual.Replace("\r\n", "\n");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, actual);
                Assert.Fail($"Snapshot {name} did not exist and was written to {path}. Review it, then run the tests again.");
            }
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(expected, actual);
        }
    }
}
