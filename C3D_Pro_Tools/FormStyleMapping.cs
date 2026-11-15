using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Maps each non-standard style onto a template style of the same collection. Suggestions come from similar names and a
    /// saved mapping file; the mapping can be saved and reused on other drawings or in batch runs.
    /// </summary>
    internal sealed class FormStyleMapping : Form
    {
        private const string NoTarget = "(leave as is)";
        private readonly DataGridView _grid = new DataGridView();
        private readonly List<StyleItem> _sources;
        private readonly List<StyleItem> _template;
        private readonly Label _summary = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };

        public List<StyleMapEntry> Result { get; } = new List<StyleMapEntry>();
        public string MappingPath { get; private set; }

        public FormStyleMapping(List<StyleItem> sources, List<StyleItem> template, string mappingPath)
        {
            _sources = sources;
            _template = template;
            MappingPath = mappingPath ?? "";
            Text = "Style Manager - Map non-standard styles";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1080, 620);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            Panel head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 12, 16, 10), BackColor = StyleUi.Panel };
            Label intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 4, 0, 0),
                Text = "Choose the template style each one should become. Its objects move to that style and the old style is deleted. " +
                       "Template styles not yet in the drawing are imported first. Rows left as is are not changed."
            };
            head.Controls.Add(intro);
            head.Controls.Add(new Label { Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading, Text = $"{sources.Count} non-standard style(s)" });
            head.Resize += (s, e) => intro.MaximumSize = new Size(Math.Max(200, head.ClientSize.Width - 32), 0);

            FlowLayoutPanel tools = StyleUi.Row(
                StyleUi.Secondary("Suggest from names", (s, e) => Suggest(true)),
                StyleUi.Secondary("Clear all", (s, e) => { foreach (DataGridViewRow r in _grid.Rows) r.Cells[3].Value = NoTarget; UpdateSummary(); }),
                StyleUi.Secondary("Load mapping…", (s, e) => LoadMapping()),
                StyleUi.Secondary("Save mapping…", (s, e) => SaveMapping()),
                _summary);
            tools.Padding = new Padding(12, 6, 12, 4);

            BuildGrid();
            Panel gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            gridHost.Controls.Add(_grid);

            Button apply = StyleUi.Primary("Apply mapping", (s, e) => Apply());
            Button cancel = StyleUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 10) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            CancelButton = cancel;

            Controls.Add(gridHost);
            Controls.Add(tools);
            Controls.Add(head);
            Controls.Add(buttons);

            if (File.Exists(MappingPath)) ApplyEntries(SafeLoad(MappingPath));
            Suggest(false);
        }

        private void BuildGrid()
        {
            DataGridView g = _grid;
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = StyleUi.Panel;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = StyleUi.Surface, ForeColor = StyleUi.Muted, Font = StyleUi.Bold };
            g.ColumnHeadersHeight = 32;
            g.RowTemplate.Height = 28;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.EditMode = DataGridViewEditMode.EditOnEnter;

            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Drawing style", ReadOnly = true, FillWeight = 30 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Category", ReadOnly = true, FillWeight = 30, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Muted } });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Uses", ReadOnly = true, FillWeight = 8 });
            g.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Becomes (template style)", FillWeight = 32, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox });

            Dictionary<string, List<string>> byCollection = _template.Where(t => !t.IsChild).GroupBy(t => t.CollectionKey)
                .ToDictionary(gr => gr.Key, gr => gr.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
            foreach (StyleItem s in _sources)
            {
                int row = g.Rows.Add(s.Name, s.FullCategory, s.Uses.ToString());
                DataGridViewComboBoxCell cell = (DataGridViewComboBoxCell)g.Rows[row].Cells[3];
                cell.Items.Add(NoTarget);
                if (byCollection.TryGetValue(s.CollectionKey, out List<string> names)) foreach (string n in names) cell.Items.Add(n);
                cell.Value = NoTarget;
                g.Rows[row].Tag = s;
            }
            g.CellValueChanged += (s, e) => UpdateSummary();
            g.CurrentCellDirtyStateChanged += (s, e) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            g.DataError += (s, e) => e.ThrowException = false;
        }

        /// <summary>Fills rows that have no target yet (or all rows when <paramref name="overwrite"/>) from similar template names.</summary>
        private void Suggest(bool overwrite)
        {
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (!overwrite && (r.Cells[3].Value as string) != NoTarget) continue;
                StyleItem match = StyleOperations.SuggestTarget((StyleItem)r.Tag, _template);
                if (match != null) r.Cells[3].Value = match.Name;
            }
            UpdateSummary();
        }

        private void ApplyEntries(List<StyleMapEntry> entries)
        {
            foreach (DataGridViewRow r in _grid.Rows)
            {
                StyleItem s = (StyleItem)r.Tag;
                StyleMapEntry e = entries.FirstOrDefault(x => x.Collection == s.CollectionKey && string.Equals(x.From, s.Name, StringComparison.OrdinalIgnoreCase));
                DataGridViewComboBoxCell cell = (DataGridViewComboBoxCell)r.Cells[3];
                if (e != null && cell.Items.Cast<string>().Any(n => string.Equals(n, e.To, StringComparison.OrdinalIgnoreCase)))
                    cell.Value = cell.Items.Cast<string>().First(n => string.Equals(n, e.To, StringComparison.OrdinalIgnoreCase));
            }
            UpdateSummary();
        }

        private List<StyleMapEntry> CurrentEntries()
        {
            List<StyleMapEntry> list = new List<StyleMapEntry>();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                string to = r.Cells[3].Value as string;
                if (string.IsNullOrEmpty(to) || to == NoTarget) continue;
                StyleItem s = (StyleItem)r.Tag;
                list.Add(new StyleMapEntry { Collection = s.CollectionKey, From = s.Name, To = to });
            }
            return list;
        }

        private void UpdateSummary() => _summary.Text = $"{CurrentEntries().Count} of {_grid.Rows.Count} mapped";

        private static List<StyleMapEntry> SafeLoad(string path)
        {
            try { return StyleOperations.LoadMapping(path); } catch (Exception) { return new List<StyleMapEntry>(); }
        }

        private void LoadMapping()
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Style mapping (*.json)|*.json", Title = "Load style mapping" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                MappingPath = ofd.FileName;
                ApplyEntries(SafeLoad(ofd.FileName));
            }
        }

        private void SaveMapping()
        {
            _grid.EndEdit();
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "Style mapping (*.json)|*.json", Title = "Save style mapping", FileName = Path.GetFileName(MappingPath.Length > 0 ? MappingPath : "StyleMapping.json") })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                // Keep lines from other drawings that this one does not have, so one file can grow into the company map.
                List<StyleMapEntry> merged = File.Exists(sfd.FileName) ? SafeLoad(sfd.FileName) : new List<StyleMapEntry>();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    StyleItem source = (StyleItem)row.Tag;
                    merged.RemoveAll(x => x.Collection == source.CollectionKey && string.Equals(x.From, source.Name, StringComparison.OrdinalIgnoreCase));
                }
                merged.AddRange(CurrentEntries());
                try { StyleOperations.SaveMapping(sfd.FileName, merged); MappingPath = sfd.FileName; }
                catch (IOException ex) { MessageBox.Show(this, ex.Message, "Save mapping", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void Apply()
        {
            _grid.EndEdit();
            Result.AddRange(CurrentEntries());
            if (Result.Count == 0) { MessageBox.Show(this, "No style has a target yet.", "Map styles", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            DialogResult = DialogResult.OK;
        }
    }
}
