using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

// A small diagnostic log shared by the Civil 3D plugin (.NET 10) and the Navisworks add-in (.NET Framework 4.8): plain text
// under %LocalAppData%\C3DProTools\logs, one file per host, rolled over at 2 MB. Logging never throws and never stops a tool.
namespace C3DProTools
{
    internal static class C3DProLogger
    {
        /// <summary>The log is rolled over (the previous one kept as *.old.log) when it reaches this size.</summary>
        public const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static readonly Assembly Own = typeof(C3DProLogger).Assembly;

        /// <summary>The folder the logs are written to.</summary>
        public static string Folder
        {
            get
            {
                try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "C3DProTools", "logs"); }
                catch (Exception) { return Path.Combine(Path.GetTempPath(), "C3DProTools", "logs"); }
            }
        }

        /// <summary>This host's log: c3dpro.log for the Civil 3D plugin, c3dpro-navis.log for the Navisworks add-in.</summary>
        public static string LogPath => Path.Combine(Folder, Own.GetName().Name.IndexOf("Navis", StringComparison.OrdinalIgnoreCase) >= 0 ? "c3dpro-navis.log" : "c3dpro.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warn(string message, Exception ex = null) => Write("WARN", message, ex);

        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                StringBuilder line = new StringBuilder();
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
                if (ex != null) line.Append(Environment.NewLine).Append("    ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message)
                    .Append(Environment.NewLine).Append(ex.StackTrace);
                line.Append(Environment.NewLine);
                lock (Gate)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length >= MaxBytes)
                    {
                        string old = Path.ChangeExtension(path, ".old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    // Shared for writing: another session of the same host may be logging too.
                    using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line.ToString());
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception)
            {
                // Logging is best effort: a log that cannot be written must never stop a tool.
            }
        }

        /// <summary>
        /// Filter for the try / catch blocks that probe Civil 3D / AutoCAD properties a part, style or version may not have:
        /// true (swallow) for whatever the CAD API throws, false (let it through, logged) for a programming error raised in
        /// this assembly's own code (null reference, index or key out of range, bad cast), which a probe must not hide.
        /// </summary>
        public static bool IsProbeFailure(Exception ex)
        {
            try
            {
                if (!(ex is NullReferenceException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException ||
                      ex is InvalidCastException || ex is System.Collections.Generic.KeyNotFoundException || ex is DivideByZeroException)) return true;
                // Where it was raised: the first frame outside the .NET libraries.
                StackFrame[] frames = new StackTrace(ex, false).GetFrames();
                if (frames != null)
                    foreach (StackFrame f in frames)
                    {
                        Assembly a = f.GetMethod()?.DeclaringType?.Assembly;
                        if (a == null || IsFramework(a)) continue;
                        if (a != Own) return true;
                        Error("A programming error escaped a property probe", ex);
                        return false;
                    }
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool IsFramework(Assembly a)
        {
            string name = a.GetName().Name ?? "";
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal);
        }
    }
}
