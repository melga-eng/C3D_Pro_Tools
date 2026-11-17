using System.IO;
using System.Linq;

namespace C3DProTools
{
    /// <summary>The report of a run: the whole text to %TEMP%\&lt;command&gt;-&lt;step&gt;.txt, the summary (unindented lines) back.</summary>
    internal static class RunReport
    {
        public static string File(string command, string mode) => Path.Combine(Path.GetTempPath(), $"{command}-{mode}.txt");

        public static string Summary(string log) =>
            string.Join("\n", (log ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != ' '));

        /// <summary>Writes the full report and returns the summary with the file's path.</summary>
        public static string Write(string log, string command, string mode)
        {
            string file = File(command, mode);
            try { System.IO.File.WriteAllText(file, log); } catch (System.Exception ex) when (ex is IOException || ex is System.UnauthorizedAccessException) { file = null; }
            return Summary(log) + (file != null ? $"\nFull report: {file}" : "");
        }
    }
}
