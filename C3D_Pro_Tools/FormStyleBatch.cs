using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>Runs the style clean-up over a folder of drawings without opening them. Results fill in next to each file.</summary>
    internal sealed class FormStyleBatch : Form
    {
        private readonly StyleManagerSettings _settings;
        private readonly RenameRule _rule;
        private readonly TextBox _txtFolder = StyleUi.Box("Folder with drawings");
        private readonly CheckBox _chkSub = new CheckBox { Text = "Include subfolders", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        private readonly ListView _files = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, BorderStyle = BorderStyle.None };
        private readonly CheckBox _chkImport = Step("Import styles missing from the template");
        private readonly CheckBox _chkUpdate = Step("Update matching styles from the template (overwrite definitions)");
        private readonly CheckBox _chkMapping = Step("Apply the mapping file (merge non-standard styles)");
        private readonly CheckBox _chkDuplicates = Step("Merge duplicates (\"Name (1)\", \"Copy of Name\"...) into the original");
        private readonly CheckBox _chkRename = Step("Apply the current rename rule to all styles");
        private readonly CheckBox _chkPurge = Step("Purge unused styles (repeat until nothing more frees up)");
        private readonly TextBox _txtTemplate = StyleUi.Box("Template (.dwt/.dwg)");
        private readonly TextBox _txtMapping = StyleUi.Box("Mapping file (.json)");
        private readonly RadioButton _rbOverwrite = new RadioButton { Text = "Save over each drawing (keeps a .bak copy)", AutoSize = true };
        private readonly RadioButton _rbFolder = new RadioButton { Text = "Save copies into:", AutoSize = true };
        private readonly TextBox _txtOutput = StyleUi.Box("Output folder");
        private readonly ProgressBar _progress = new ProgressBar { Width = 260, Height = 18, Margin = new Padding(3, 10, 3, 3) };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 10, 3, 3) };
        private readonly Button _btnRun, _btnStop, _btnExport;
        /// <summary>The drawings, steps and output cards: locked while a batch runs.</summary>
        private readonly TableLayoutPanel _body;
        private bool _stop, _running;

        public FormStyleBatch(StyleManagerSettings settings, RenameRule rule, string templatePath)
        {
            _settings = settings;
            _rule = rule;
            Text = "Style Manager - Batch processor";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1180, 680);
            MinimumSize = new Size(900, 520);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            // Drawings card
            _files.Columns.Add("Drawing", 220);
            _files.Columns.Add("Folder", 200);
            _files.Columns.Add("Result", 70);
            _files.Columns.Add("Imported", 70, HorizontalAlignment.Right);
            _files.Columns.Add("Merged", 64, HorizontalAlignment.Right);
            _files.Columns.Add("Renamed", 70, HorizontalAlignment.Right);
            _files.Columns.Add("Deleted", 64, HorizontalAlignment.Right);
            _files.Columns.Add("Note", 220);
            TableLayoutPanel pick = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.Controls.Add(_txtFolder, 0, 0);
            pick.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFolder(_txtFolder)), 1, 0);
            pick.Controls.Add(_chkSub, 2, 0);
            pick.Controls.Add(StyleUi.Primary("Find drawings", (s, e) => FindFiles()), 3, 0);
            Panel drawingsBody = new Panel();
            drawingsBody.Controls.Add(_files);
            drawingsBody.Controls.Add(StyleUi.Row(
                StyleUi.Secondary("Tick all", (s, e) => SetAll(true)),
                StyleUi.Secondary("Untick all", (s, e) => SetAll(false)),
                StyleUi.Secondary("Add files…", (s, e) => AddFiles())));
            drawingsBody.Controls.Add(pick);
            StyleCard drawings = new StyleCard("Drawings", "Drawings that are open in Civil 3D are skipped; close them first.", drawingsBody);

            // Steps card
            TableLayoutPanel paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            paths.Controls.Add(StyleUi.Caption("Template"), 0, 0);
            paths.Controls.Add(_txtTemplate, 1, 0);
            paths.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFile(_txtTemplate, "Drawings and templates (*.dwt;*.dwg)|*.dwt;*.dwg")), 2, 0);
            paths.Controls.Add(StyleUi.Caption("Mapping"), 0, 1);
            paths.Controls.Add(_txtMapping, 1, 1);
            paths.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFile(_txtMapping, "Style mapping (*.json)|*.json")), 2, 1);
            FlowLayoutPanel steps = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            steps.Controls.AddRange(new Control[] { _chkImport, _chkUpdate, _chkMapping, _chkDuplicates, _chkRename, _chkPurge });
            steps.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 8, 3, 3),
                Text = rule.IsEmpty ? "Rename rule: none set on the Rename tab." : "Rename rule: " + Describe(rule)
            });
            Panel stepsBody = new Panel();
            stepsBody.Controls.Add(steps);
            stepsBody.Controls.Add(paths);
            StyleCard stepCard = new StyleCard("Steps", "They run in this order on each drawing.", stepsBody);

            // Output card
            TableLayoutPanel output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            output.Controls.Add(_rbOverwrite, 0, 0);
            output.SetColumnSpan(_rbOverwrite, 3);
            output.Controls.Add(_rbFolder, 0, 1);
            output.Controls.Add(_txtOutput, 1, 1);
            output.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFolder(_txtOutput)), 2, 1);
            StyleCard outCard = new StyleCard("Output", "", output);

            TableLayoutPanel right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            right.Controls.Add(stepCard, 0, 0);
            right.Controls.Add(outCard, 0, 1);

            _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _body.Controls.Add(drawings, 0, 0);
            _body.Controls.Add(right, 1, 0);

            _btnRun = StyleUi.Primary("Run batch", (s, e) => Run());
            _btnStop = StyleUi.Secondary("Stop after this drawing", (s, e) => _stop = true);
            _btnStop.Enabled = false;
            Button close = StyleUi.Secondary("Close", (s, e) => Close());
            _btnExport = StyleUi.Secondary("Export results…", (s, e) => ExportResults());
            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 10) };
            bottom.Controls.AddRange(new Control[] { close, _btnRun, _btnStop, _btnExport, _status, _progress });

            Controls.Add(_body);
            Controls.Add(bottom);
            FormClosing += (s, e) => { if (_running) { e.Cancel = true; _stop = true; } else SaveChoices(); };

            _txtFolder.Text = settings.BatchFolder ?? "";
            _chkSub.Checked = settings.BatchSubfolders;
            _chkImport.Checked = settings.BatchImportMissing;
            _chkUpdate.Checked = settings.BatchOverwriteFromTemplate;
            _chkMapping.Checked = settings.BatchApplyMapping;
            _chkDuplicates.Checked = settings.BatchMergeDuplicates;
            _chkRename.Checked = settings.BatchRename && !rule.IsEmpty;
            _chkRename.Enabled = !rule.IsEmpty;
            _chkPurge.Checked = settings.BatchDeleteUnused;
            _txtTemplate.Text = string.IsNullOrEmpty(templatePath) ? settings.TemplatePath ?? "" : templatePath;
            _txtMapping.Text = settings.MappingPath ?? "";
            _rbOverwrite.Checked = settings.BatchOverwrite;
            _rbFolder.Checked = !settings.BatchOverwrite;
            _txtOutput.Text = settings.BatchOutputFolder ?? "";
            if (Directory.Exists(_txtFolder.Text)) FindFiles();
        }

        private static CheckBox Step(string text) => new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 5, 3, 3) };

        private static string Describe(RenameRule r)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(r.Find)) parts.Add($"replace \"{r.Find}\" with \"{r.Replace}\"");
            if (!string.IsNullOrEmpty(r.RemovePrefix)) parts.Add($"remove prefix \"{r.RemovePrefix}\"");
            if (!string.IsNullOrEmpty(r.RemoveSuffix)) parts.Add($"remove suffix \"{r.RemoveSuffix}\"");
            if (!string.IsNullOrEmpty(r.AddPrefix)) parts.Add($"add prefix \"{r.AddPrefix}\"");
            if (!string.IsNullOrEmpty(r.AddSuffix)) parts.Add($"add suffix \"{r.AddSuffix}\"");
            if (r.CaseMode > 0) parts.Add(new[] { "", "upper case", "lower case", "title case" }[r.CaseMode]);
            return (r.TargetDescription ? "descriptions: " : "names: ") + string.Join(", ", parts);
        }

        private void FindFiles()
        {
            string folder = _txtFolder.Text.Trim();
            if (!Directory.Exists(folder)) { _status.Text = "Folder not found."; return; }
            SearchOption opt = _chkSub.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (string f in Directory.EnumerateFiles(folder, "*.dwg", opt).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) AddFile(f);
            _files.EndUpdate();
            _status.Text = $"{_files.Items.Count} drawing(s) found.";
        }

        private void AddFile(string path)
        {
            if (_files.Items.Cast<ListViewItem>().Any(i => string.Equals((string)i.Tag, path, StringComparison.OrdinalIgnoreCase))) return;
            _files.Items.Add(new ListViewItem(new[] { Path.GetFileName(path), Path.GetDirectoryName(path), "", "", "", "", "", "" }) { Tag = path, Checked = true });
        }

        private void AddFiles()
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Multiselect = true, Filter = "Drawings (*.dwg;*.dwt)|*.dwg;*.dwt" })
                if (ofd.ShowDialog(this) == DialogResult.OK) foreach (string f in ofd.FileNames) AddFile(f);
        }

        private void SetAll(bool value) { foreach (ListViewItem i in _files.Items) i.Checked = value; }

        private void BrowseFolder(TextBox target)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { SelectedPath = Directory.Exists(target.Text) ? target.Text : "" })
                if (fbd.ShowDialog(this) == DialogResult.OK) target.Text = fbd.SelectedPath;
        }

        private void BrowseFile(TextBox target, string filter)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = filter })
                if (ofd.ShowDialog(this) == DialogResult.OK) target.Text = ofd.FileName;
        }

        private void SaveChoices()
        {
            _settings.BatchFolder = _txtFolder.Text.Trim();
            _settings.BatchSubfolders = _chkSub.Checked;
            _settings.BatchImportMissing = _chkImport.Checked;
            _settings.BatchOverwriteFromTemplate = _chkUpdate.Checked;
            _settings.BatchApplyMapping = _chkMapping.Checked;
            _settings.BatchMergeDuplicates = _chkDuplicates.Checked;
            _settings.BatchRename = _chkRename.Checked;
            _settings.BatchDeleteUnused = _chkPurge.Checked;
            _settings.BatchOverwrite = _rbOverwrite.Checked;
            _settings.BatchOutputFolder = _txtOutput.Text.Trim();
            _settings.MappingPath = _txtMapping.Text.Trim();
            if (_txtTemplate.Text.Trim().Length > 0) _settings.TemplatePath = _txtTemplate.Text.Trim();
        }

        private async void Run()
        {
            List<ListViewItem> rows = _files.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList();
            if (rows.Count == 0) { _status.Text = "Tick the drawings to process."; return; }
            BatchOptions o = new BatchOptions
            {
                ImportMissing = _chkImport.Checked, OverwriteFromTemplate = _chkUpdate.Checked, ApplyMapping = _chkMapping.Checked,
                MergeDuplicates = _chkDuplicates.Checked, Rename = _chkRename.Checked, DeleteUnused = _chkPurge.Checked,
                TemplatePath = _txtTemplate.Text.Trim(), MappingPath = _txtMapping.Text.Trim(), Rule = _rule,
                Overwrite = _rbOverwrite.Checked, OutputFolder = _txtOutput.Text.Trim()
            };
            if (!(o.ImportMissing || o.OverwriteFromTemplate || o.ApplyMapping || o.MergeDuplicates || o.Rename || o.DeleteUnused)) { _status.Text = "Tick at least one step."; return; }
            if ((o.ImportMissing || o.OverwriteFromTemplate) && !File.Exists(o.TemplatePath)) { _status.Text = "The template file is needed for the template steps."; return; }
            if (o.ApplyMapping && !File.Exists(o.MappingPath)) { _status.Text = "The mapping file is needed for the mapping step."; return; }
            if (!o.Overwrite && o.OutputFolder.Length == 0) { _status.Text = "Choose the output folder."; return; }
            BatchTargetPlan targets = null;
            if (!o.Overwrite)
            {
                targets = BatchTargets.Plan(rows.Select(r => (string)r.Tag).ToList(), o.OutputFolder);
                if (!targets.Ok)
                {
                    MessageBox.Show(this, "No drawing was processed:\n\n" + string.Join("\n", targets.Problems.Take(8)) + (targets.Problems.Count > 8 ? $"\n…and {targets.Problems.Count - 8} more." : ""),
                        "Batch processor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _status.Text = "The copies' paths clash; choose another output folder.";
                    return;
                }
            }
            string warning = o.Overwrite ? "Each drawing will be saved over (a .bak copy is kept next to it)."
                : $"Copies will be saved into {o.OutputFolder}, keeping their subfolders." + (targets.Existing > 0 ? $"\n{targets.Existing} copy(ies) already there will be replaced." : "");
            if (MessageBox.Show(this, $"Process {rows.Count} drawing(s)?\n\n{warning}", "Batch processor", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SaveChoices();

            HashSet<string> open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Autodesk.AutoCAD.ApplicationServices.Document d in AcadCoreApp.DocumentManager) open.Add(d.Name);

            StyleTemplate template = null;
            List<StyleMapEntry> mapping = null;
            try
            {
                if (o.ImportMissing || o.OverwriteFromTemplate) template = new StyleTemplate(o.TemplatePath);
                if (o.ApplyMapping) mapping = StyleOperations.LoadMapping(o.MappingPath);
            }
            catch (Exception ex) { template?.Dispose(); _status.Text = "Could not read the template or mapping: " + ex.Message; return; }

            _running = true;
            _stop = false;
            // Only Stop (and Close, which stops) stay live while the drawings are processed.
            _body.Enabled = false;
            _btnRun.Enabled = false;
            _btnExport.Enabled = false;
            _btnStop.Enabled = true;
            _progress.Maximum = rows.Count;
            _progress.Value = 0;
            int done = 0, failed = 0;
            try
            {
                foreach (ListViewItem row in rows)
                {
                    if (_stop) break;
                    string path = (string)row.Tag;
                    row.EnsureVisible();
                    if (open.Contains(path)) { Fill(row, new BatchFileResult { Status = "Skipped", Note = "Open in Civil 3D" }); continue; }
                    row.SubItems[2].Text = "Working…";
                    // Between drawings the window takes its messages (Stop, repaint). A short delay rather than Task.Yield,
                    // which would queue ahead of mouse input so Stop could not be clicked.
                    await System.Threading.Tasks.Task.Delay(1);
                    BatchFileResult r = StyleBatch.Process(path, targets?.Targets[Path.GetFullPath(path)], o, template, mapping, s => { _status.Text = $"{Path.GetFileName(path)}: {s}"; _status.Refresh(); });
                    Fill(row, r);
                    if (r.Status.StartsWith("Done")) done++; else failed++;
                    _progress.Value++;
                }
            }
            catch (Exception ex)
            {
                _status.Text = "The batch stopped: " + ex.Message;
            }
            finally
            {
                template?.Dispose();
                _running = false;
                _body.Enabled = true;
                _btnRun.Enabled = true;
                _btnExport.Enabled = true;
                _btnStop.Enabled = false;
            }
            _status.Text = $"Finished: {done} done, {failed} failed" + (_stop ? " (stopped)." : ".");
        }

        private static void Fill(ListViewItem row, BatchFileResult r)
        {
            row.SubItems[2].Text = r.Status;
            row.SubItems[3].Text = r.Imported.ToString();
            row.SubItems[4].Text = r.Merged.ToString();
            row.SubItems[5].Text = r.Renamed.ToString();
            row.SubItems[6].Text = r.Deleted.ToString();
            row.SubItems[7].Text = r.Note;
            row.ForeColor = r.Status.StartsWith("Failed") ? StyleUi.Danger : r.Status == "Done with problems" ? StyleUi.Danger : r.Status == "Skipped" ? StyleUi.Muted : StyleUi.Text;
        }

        private void ExportResults()
        {
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = "StyleBatch_Results.csv" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder("Drawing,Folder,Result,Imported,Merged,Renamed,Deleted,Note\r\n");
                foreach (ListViewItem i in _files.Items)
                    sb.AppendLine(string.Join(",", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text.Contains(",") ? "\"" + s.Text.Replace("\"", "\"\"") + "\"" : s.Text)));
                try { File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true)); }
                catch (IOException ex) { MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
