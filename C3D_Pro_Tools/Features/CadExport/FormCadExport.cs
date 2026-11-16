using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace C3DProTools.Features.CadExport
{
    /// <summary>The CAD sheets window: the layouts to export (ticked, filterable, all or none) and the folder they go to.</summary>
    internal sealed class FormCadExport : Form
    {
        private readonly List<string> _layouts;
        private readonly HashSet<string> _ticked;
        private readonly CheckedListBox _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox _filter = StyleUi.Box("Filter sheets, e.g. 1020 or 434*");
        private readonly TextBox _folder = StyleUi.Box("Folder for the CAD sheets");
        private readonly Label _count = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private bool _filling;
        private readonly string _source;

        public List<string> Chosen => _layouts.Where(_ticked.Contains).ToList();
        public string Folder => _folder.Text.Trim().Trim('"');

        public FormCadExport(List<string> layouts, IEnumerable<string> lastTicked, string folder, string source)
        {
            _layouts = layouts;
            _source = source ?? "";
            _ticked = new HashSet<string>((lastTicked ?? Enumerable.Empty<string>()).Where(layouts.Contains), StringComparer.Ordinal);
            Text = "CAD Sheets";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 600);
            MinimumSize = new Size(460, 420);
            ShowInTaskbar = false;

            Button export = StyleUi.Primary("Make CAD sheets", (s, e) => Accept());
            Button cancel = StyleUi.Secondary("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            AcceptButton = export;
            CancelButton = cancel;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 10, 12, 6) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var tools = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 4) };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tools.Controls.Add(_filter, 0, 0);
            tools.Controls.Add(StyleUi.Secondary("Tick shown", (s, e) => TickShown(true)), 1, 0);
            tools.Controls.Add(StyleUi.Secondary("Clear", (s, e) => TickShown(false)), 2, 0);
            tools.Controls.Add(_count, 3, 0);
            body.Controls.Add(tools, 0, 0);
            body.Controls.Add(_list, 0, 1);

            var folderRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.Controls.Add(StyleUi.Caption("Save to"), 0, 0);
            folderRow.Controls.Add(_folder, 1, 0);
            folderRow.Controls.Add(StyleUi.Secondary("Browse...", (s, e) => Browse()), 2, 0);
            body.Controls.Add(folderRow, 0, 2);
            body.Controls.Add(ToolUi.Note("Each sheet becomes <sheet name>.dwg (AutoCAD 2018): the whole model, that sheet only, labels exploded as the sheet " +
                                          "shows them, unused items purged. It runs in the background on a copy of the saved drawing; your drawing is not changed."), 0, 3);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8), BackColor = StyleUi.Surface };
            footer.Controls.Add(export);
            footer.Controls.Add(cancel);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("CAD Sheets", "One AutoCAD drawing per sheet, ready to submit"));

            _folder.Text = folder ?? "";
            _filter.TextChanged += (s, e) => Fill();
            _list.ItemCheck += (s, e) =>
            {
                if (_filling) return;
                string name = (string)_list.Items[e.Index];
                if (e.NewValue == CheckState.Checked) _ticked.Add(name); else _ticked.Remove(name);
                BeginInvoke(new Action(UpdateCount));
            };
            Fill();
            Resize += (s, e) => ToolUi.FitNotes(body, body.ClientSize.Width - body.Padding.Horizontal - 6);
            Shown += (s, e) => ToolUi.FitNotes(body, body.ClientSize.Width - body.Padding.Horizontal - 6);
        }

        /// <summary>The layouts the filter shows: wildcards and commas as at the command line, plain text as "name contains".</summary>
        private IEnumerable<string> ShownLayouts()
        {
            string f = _filter.Text.Trim();
            if (f.Length == 0) return _layouts;
            bool pattern = f.IndexOfAny(new[] { '*', '?', ',' }) >= 0;
            return _layouts.Where(n => pattern ? NamePatterns.Matches(f, n) : n.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void Fill()
        {
            _filling = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (string n in ShownLayouts()) _list.Items.Add(n, _ticked.Contains(n));
            _list.EndUpdate();
            _filling = false;
            UpdateCount();
        }

        private void TickShown(bool on)
        {
            foreach (string n in ShownLayouts()) if (on) _ticked.Add(n); else _ticked.Remove(n);
            Fill();
        }

        private void UpdateCount() => _count.Text = $"{_ticked.Count} of {_layouts.Count} ticked";

        private void Browse()
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder for the CAD sheets", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(Folder)) dlg.SelectedPath = Folder;
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        }

        private void Accept()
        {
            List<string> chosen = Chosen;
            if (chosen.Count == 0) { MessageBox.Show(this, "Tick at least one sheet.", Text); return; }
            if (string.IsNullOrWhiteSpace(Folder)) { MessageBox.Show(this, "Choose the folder for the CAD sheets.", Text); return; }
            try { Directory.CreateDirectory(Folder); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                MessageBox.Show(this, $"The folder cannot be used: {ex.Message}", Text);
                return;
            }
            // Never over the drawing the sheets are made from (review finding: a sheet named like the drawing, in its folder).
            string clash = chosen.Select(SheetTwist.FileName).FirstOrDefault(f => _source.Length > 0 &&
                string.Equals(Path.GetFullPath(Path.Combine(Folder, f)), Path.GetFullPath(_source), StringComparison.OrdinalIgnoreCase));
            if (clash != null) { MessageBox.Show(this, $"{clash} would replace the drawing the sheets are made from. Choose another folder.", Text); return; }
            List<string> existing = chosen.Select(SheetTwist.FileName).Where(f => File.Exists(Path.Combine(Folder, f))).ToList();
            if (existing.Count > 0 && MessageBox.Show(this,
                    $"{existing.Count} of these files are already in the folder and will be replaced:\n\n" + string.Join("\n", existing.Take(8)) +
                    (existing.Count > 8 ? $"\n... and {existing.Count - 8} more" : "") + "\n\nReplace them?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
