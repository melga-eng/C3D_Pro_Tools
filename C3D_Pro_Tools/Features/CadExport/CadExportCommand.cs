using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

[assembly: CommandClass(typeof(C3DProTools.Features.CadExport.CadExportCommand))]

// C3DPROCAD: the CAD sheets window. Pick the sheets and the folder; a background Civil 3D makes the files from a copy of the
// saved drawing (C3DPROCADRUN) while you keep working, and you are told when they are ready. The open drawing is never changed.
namespace C3DProTools.Features.CadExport
{
    public sealed class CadExportCommand
    {
        private static Job _running;

        [CommandMethod("C3DPRO", "C3DPROCAD", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (_running != null)
            {
                WinForms.MessageBox.Show($"CAD sheets for {Path.GetFileName(_running.Source)} are still being made (started {_running.Started:HH:mm}). You will be told when they are ready.",
                    "CAD Sheets", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                return;
            }
            string source = doc.Database.Filename;
            if (string.IsNullOrEmpty(source) || !File.Exists(source) || !doc.IsNamedDrawing)
            {
                WinForms.MessageBox.Show("Save the drawing first: the CAD sheets are made from the saved file.", "CAD Sheets",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                return;
            }
            if (Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0 &&
                WinForms.MessageBox.Show("The drawing has changes that are not saved. The CAD sheets are made from the saved file, without them.\n\n" +
                    "Continue with the saved file? (No: cancel, save, and run again.)", "CAD Sheets",
                    WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Question) != WinForms.DialogResult.Yes)
                return;

            List<string> layouts = CadExportRun.LayoutNames(doc.Database);
            if (layouts.Count == 0) { WinForms.MessageBox.Show("The drawing has no layouts.", "CAD Sheets"); return; }
            Remembered last = Remembered.Load(source);
            using var form = new FormCadExport(layouts, last.Layouts, string.IsNullOrWhiteSpace(last.Folder) ? Path.GetDirectoryName(source) : last.Folder);
            if (AcApp.ShowModalDialog(form) != WinForms.DialogResult.OK) return;
            Remembered.Save(source, new Remembered { Folder = form.Folder, Layouts = form.Chosen });

            try
            {
                _running = Job.Start(source, form.Chosen, form.Folder);
                doc.Editor.WriteMessage($"\nC3DPROCAD: making {form.Chosen.Count} CAD sheet(s) in the background into {form.Folder}. Keep working; you will be told when they are ready.\n");
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROCAD: the background Civil 3D could not be started", ex);
                WinForms.MessageBox.Show($"The background Civil 3D could not be started: {ex.Message}", "CAD Sheets",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// One background run on a copy of the saved drawing, in two background Civil 3D sessions one after the other: the
        /// explode (saved), then the export and split. A timer on the Civil 3D thread starts the second and reports the end.
        /// </summary>
        private sealed class Job
        {
            public string Source { get; private init; } = "";
            public DateTime Started { get; private init; }
            private string _dir = "", _folder = "", _console = "", _acadDir = "", _copy = "";
            private string[] _exportScript = Array.Empty<string>();
            private int _step;
            private Process _process = null!;
            private WinForms.Timer _timer = null!;

            private string ReportFile => Path.Combine(_dir, "report.txt");
            private string Log(int step) => Path.Combine(_dir, $"log{step}.txt");

            public static Job Start(string source, List<string> layouts, string folder)
            {
                string acadDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
                string console = Path.Combine(acadDir, "accoreconsole.exe");
                if (!File.Exists(console)) throw new FileNotFoundException("accoreconsole.exe was not found beside Civil 3D", console);
                string dll = "\"" + typeof(CadExportCommand).Assembly.Location.Replace('\\', '/') + "\"";

                string dir = Path.Combine(Path.GetTempPath(), "c3dpro-cad-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path.Combine(dir, "iso"));
                string copy = Path.Combine(dir, Path.GetFileName(source));
                File.Copy(source, copy);
                var job = new Job
                {
                    Source = source, Started = DateTime.Now, _dir = dir, _folder = folder, _console = console, _acadDir = acadDir, _copy = copy,
                };
                job._exportScript = new[]
                {
                    "SECURELOAD", "0", "NETLOAD", dll, CadExportRun.Command, "Export", string.Join("|", layouts), folder, job.ReportFile, "_QUIT", "_Y",
                };
                job.Run(1, new[] { "SECURELOAD", "0", "NETLOAD", dll, CadExportRun.Command, "Explode", job.ReportFile, "_QSAVE", "_QUIT" });
                job._timer = new WinForms.Timer { Interval = 3000 };
                job._timer.Tick += (s, e) => job.Check();
                job._timer.Start();
                return job;
            }

            private void Run(int step, string[] script)
            {
                _step = step;
                string scr = Path.Combine(_dir, $"step{step}.scr");
                File.WriteAllLines(scr, script, System.Text.Encoding.ASCII);
                var psi = new ProcessStartInfo(_console,
                    $"/i \"{_copy}\" /s \"{scr}\" /loadmodule \"{Path.Combine(_acadDir, "AecBase.dbx")}\" /product C3D /l en-US /isolate c3dprocad \"{Path.Combine(_dir, "iso")}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var output = new System.Text.StringBuilder();
                string log = Log(step);
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.Exited += (s, e) => { lock (output) try { File.WriteAllText(log, output.ToString()); } catch (IOException) { } };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
            }

            private void Check()
            {
                if (!_process.HasExited) return;
                string report = File.Exists(ReportFile) ? File.ReadAllText(ReportFile) : null;
                if (_step == 1 && report != null && report.Contains("labels done"))
                {
                    try { Run(2, _exportScript); return; }
                    catch (System.Exception ex) { C3DProLogger.Error("C3DPROCAD: the export step could not be started", ex); }
                }
                _timer.Stop();
                _timer.Dispose();
                _running = null;
                bool done = report != null && report.Contains("CAD sheets saved");
                string reportFile = Path.Combine(_dir, "report.txt");
                if (report != null)
                {
                    reportFile = RunReport.File("C3DPROCAD", "Export");
                    try { File.WriteAllText(reportFile, report); } catch (IOException) { }
                }
                string summary = (report != null ? RunReport.Summary(report) : "") +
                                 (done ? "" : $"\nThe background Civil 3D stopped before the end (step {_step}). Its log: {Log(_step)}");
                try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nC3DPROCAD finished:\n{summary}\n" + (report != null ? $"Full report: {reportFile}\n" : "")); }
                catch (System.Exception ex) { C3DProLogger.Warn("C3DPROCAD: the result could not be written to the command line", ex); }
                if (done)
                    try { Directory.Delete(_dir, true); }
                    catch (System.Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("C3DPROCAD: the work folder could not be removed", ex); }
                if (WinForms.MessageBox.Show(summary + "\n\nOpen the folder?", "CAD Sheets", WinForms.MessageBoxButtons.YesNo,
                        done && !summary.Contains("Not saved") ? WinForms.MessageBoxIcon.Information : WinForms.MessageBoxIcon.Warning) == WinForms.DialogResult.Yes)
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true }); }
                    catch (System.Exception ex) { C3DProLogger.Warn("C3DPROCAD: the folder could not be opened", ex); }
            }
        }

        /// <summary>The sheets and folder last chosen for each drawing (on this computer).</summary>
        private sealed class Remembered
        {
            public string Folder { get; set; } = "";
            public List<string> Layouts { get; set; } = new List<string>();

            private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "CadExport.json");

            public static Remembered Load(string drawing)
            {
                try
                {
                    if (!System.IO.File.Exists(File)) return new Remembered();
                    var all = JsonSerializer.Deserialize<Dictionary<string, Remembered>>(System.IO.File.ReadAllText(File));
                    return all != null && all.TryGetValue(drawing.ToLowerInvariant(), out Remembered r) && r != null ? r : new Remembered();
                }
                catch (System.Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { return new Remembered(); }
            }

            public static void Save(string drawing, Remembered r)
            {
                try
                {
                    Dictionary<string, Remembered> all = null;
                    if (System.IO.File.Exists(File))
                        try { all = JsonSerializer.Deserialize<Dictionary<string, Remembered>>(System.IO.File.ReadAllText(File)); } catch (JsonException) { }
                    all ??= new Dictionary<string, Remembered>();
                    all[drawing.ToLowerInvariant()] = r;
                    Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                    System.IO.File.WriteAllText(File, JsonSerializer.Serialize(all));
                }
                catch (System.Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("C3DPROCAD: the last choices could not be saved", ex); }
            }
        }
    }
}
