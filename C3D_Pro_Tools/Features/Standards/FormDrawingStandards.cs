using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Font = System.Drawing.Font;
using FlowDirection = System.Windows.Forms.FlowDirection;

namespace C3DProTools.Features.Standards
{
    /// <summary>Drawing Standards window: every KSIA CAD rule finding, its items below; the safe fixes ticked and done in one UNDO.</summary>
    internal sealed class FormDrawingStandards : Form
    {
        private readonly Document _doc;
        private List<StandardFinding> _found = new List<StandardFinding>();
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter
        };
        private readonly TextBox _items = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font("Consolas", 9f), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        private readonly System.Windows.Forms.Label _status = new System.Windows.Forms.Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };

        public FormDrawingStandards(Document doc)
        {
            _doc = doc;
            Text = "Drawing Standards";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(720, 480);
            ShowInTaskbar = false;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "fix", HeaderText = "Fix", FillWeight = 5 });
            foreach ((string n, string h, int w) in new[] { ("kind", "", 8), ("area", "Area", 10), ("rule", "Rule", 14), ("text", "Finding", 63) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = n, HeaderText = h, ReadOnly = true, FillWeight = w });
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.SelectionChanged += (s, e) => ShowItems();

            Button check = StyleUi.Secondary("Check again", (s, e) => Read());
            Button fix = StyleUi.Primary("Fix ticked", (s, e) => Fix());
            Button close = StyleUi.Secondary("Close", (s, e) => Close());
            CancelButton = close;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 10, 12, 6) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            head.Controls.Add(check);
            head.Controls.Add(_status);
            body.Controls.Add(head, 0, 0);
            body.Controls.Add(_grid, 0, 1);
            body.Controls.Add(StyleUi.Caption("Items of the finding selected"), 0, 2);
            body.Controls.Add(_items, 0, 3);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8), BackColor = StyleUi.Surface };
            footer.Controls.Add(close);
            footer.Controls.Add(fix);
            footer.Controls.Add(ToolUi.Note("Only two rules have a fix here (units, xref paths); the rest is listed for you. Layers of network parts: Network Layers. Sheets: Sheet Manager. Purge: Drawing Cleaner."));

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("Drawing Standards", "The drawing checked against the KSIA CAD standard"));
            Shown += (s, e) => Read();
        }

        private void Read()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _found = DrawingStandards.Check(_doc);
                _grid.Rows.Clear();
                foreach (StandardFinding f in _found)
                {
                    int i = _grid.Rows.Add(f.Fix != null, f.IsNote ? "note" : "problem", f.Area, f.Rule, f.Text + (f.Fix != null ? $"  [fix: {f.FixText}]" : ""));
                    _grid.Rows[i].Tag = f;
                    if (f.Fix == null) _grid.Rows[i].Cells["fix"].ReadOnly = true;
                    if (f.IsNote) _grid.Rows[i].DefaultCellStyle.ForeColor = StyleUi.Muted;
                }
                int problems = _found.Count(f => !f.IsNote);
                _status.Text = _found.Count == 0 ? "The drawing meets every rule checked." : $"{problems} problems ({_found.Count(f => f.Fix != null)} with a fix), {_found.Count - problems} notes";
                ShowItems();
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Drawing Standards: the check failed", ex);
                _status.Text = "The check failed: " + ex.Message;
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ShowItems()
        {
            if (_grid.CurrentRow?.Tag is StandardFinding f) _items.Text = string.Join(Environment.NewLine, f.Items.Count > 0 ? f.Items : new List<string> { f.Text });
            else _items.Text = "";
        }

        private void Fix()
        {
            List<StandardFinding> chosen = _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["fix"].Value is bool b && b).Select(r => (StandardFinding)r.Tag).Where(f => f.Fix != null).ToList();
            if (chosen.Count == 0) { MessageBox.Show(this, "Nothing with a fix is ticked.", Text); return; }
            var log = new StringBuilder();
            try { DrawingStandards.Fix(_doc, chosen, log); }
            catch (Exception ex) { C3DProLogger.Error("Drawing Standards: fix failed", ex); log.AppendLine($"Fix failed: {ex.Message}. Nothing was changed."); }
            string summary = RunReport.Write(log.ToString(), "C3DPROSTANDARDS", "Fix");
            Read();
            MessageBox.Show(this, summary, Text);
        }
    }
}
