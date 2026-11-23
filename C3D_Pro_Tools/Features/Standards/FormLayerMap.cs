using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using FlowDirection = System.Windows.Forms.FlowDirection;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// Layer names window: each layer not in the KSIA form, and each kind of object on layer 0 / Defpoints, with the KSIA layer it
    /// goes to. Sure suggestions and your earlier choices come ticked; pick or type the rest. Layers frozen in a viewport are shown
    /// and cannot be ticked. Apply moves the objects of model and sheets (not inside blocks), one UNDO, and remembers the choices.
    /// </summary>
    internal sealed class FormLayerMap : Form
    {
        private readonly Document _doc;
        private List<LayerMapRow> _rows = new List<LayerMapRow>();
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter
        };
        private readonly ComboBox _filter = ToolUi.Combo(260);
        private readonly System.Windows.Forms.Label _status = new System.Windows.Forms.Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private List<string> _targets = new List<string>();

        public FormLayerMap(Document doc)
        {
            _doc = doc;
            Text = "Layer names";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1150, 700);
            MinimumSize = new Size(800, 480);
            ShowInTaskbar = false;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "tick", HeaderText = "Apply", FillWeight = 6 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "layer", HeaderText = "Layer (or kind of object on 0 / Defpoints)", ReadOnly = true, FillWeight = 38 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "objects", HeaderText = "Objects", ReadOnly = true, FillWeight = 7 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "target", HeaderText = "Goes to (KSIA layer: pick or type)", FillWeight = 34 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "why", HeaderText = "Note", ReadOnly = true, FillWeight = 22 });
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.EditingControlShowing += (s, e) =>
            {
                // The target cell offers every KSIA template layer and the drawing's standard layers as you type.
                if (_grid.CurrentCell?.OwningColumn.Name == "target" && e.Control is TextBox box)
                {
                    box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                    box.AutoCompleteSource = AutoCompleteSource.CustomSource;
                    var src = new AutoCompleteStringCollection();
                    src.AddRange(_targets.ToArray());
                    box.AutoCompleteCustomSource = src;
                }
            };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || !(_grid.Rows[e.RowIndex].Tag is LayerMapRow r)) return;
                if (_grid.Columns[e.ColumnIndex].Name == "target")
                {
                    r.Target = (_grid.Rows[e.RowIndex].Cells["target"].Value as string ?? "").Trim();
                    // Choosing a layer ticks the row.
                    if (r.Target.Length > 0 && !r.FrozenInViewport) _grid.Rows[e.RowIndex].Cells["tick"].Value = true;
                }
                if (_grid.Columns[e.ColumnIndex].Name == "tick") r.Tick = _grid.Rows[e.RowIndex].Cells["tick"].Value is bool b && b;
                UpdateStatus();
            };

            _filter.Items.AddRange(new object[] { "Everything", "Layers not in the KSIA form", "Objects on layer 0 / Defpoints", "Ticked", "Not decided (no layer chosen)" });
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += (s, e) => Fill();

            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            head.Controls.Add(new System.Windows.Forms.Label { Text = "Show", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
            head.Controls.Add(_filter);
            head.Controls.Add(StyleUi.Secondary("Read again", (s, e) => Read()));
            head.Controls.Add(_status);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 10, 12, 6) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(head, 0, 0);
            body.Controls.Add(_grid, 0, 1);

            Button apply = StyleUi.Primary("Apply ticked", (s, e) => Apply());
            Button close = StyleUi.Secondary("Close", (s, e) => Close());
            CancelButton = close;
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8), BackColor = StyleUi.Surface };
            footer.Controls.Add(close);
            footer.Controls.Add(apply);
            footer.Controls.Add(ToolUi.Note("Objects drawn in model and on sheets move; what is inside blocks stays. Layers frozen in a viewport stay as they are. " +
                                            "A layer emptied is deleted. Your choices are remembered for the next drawing. One UNDO."));

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("Layer names", "Every layer to the KSIA form (Role-Uniclass code-Presentation-Description)"));
            Shown += (s, e) => Read();
        }

        private void Read()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _rows = DrawingStandards.ScanLayers(_doc);
                var template = LayerAudit.TemplateLayers(LayerAudit.TemplatePath());
                _targets = template.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                Fill();
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Layer names: reading failed", ex);
                _status.Text = "Reading failed: " + ex.Message;
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Fill()
        {
            _grid.Rows.Clear();
            IEnumerable<LayerMapRow> shown = _rows;
            switch (_filter.SelectedIndex)
            {
                case 1: shown = _rows.Where(r => r.Kind == null); break;
                case 2: shown = _rows.Where(r => r.Kind != null); break;
                case 3: shown = _rows.Where(r => r.Tick); break;
                case 4: shown = _rows.Where(r => string.IsNullOrWhiteSpace(r.Target) && !r.FrozenInViewport); break;
            }
            foreach (LayerMapRow r in shown)
            {
                int i = _grid.Rows.Add(r.Tick, r.What, r.Objects, r.Target, r.Why + (r.UsedInBlocks && !r.FrozenInViewport ? (r.Why.Length > 0 ? "; " : "") + "also used inside blocks (stays there)" : ""));
                DataGridViewRow row = _grid.Rows[i];
                row.Tag = r;
                if (r.FrozenInViewport)
                {
                    row.ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = StyleUi.Muted;
                }
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            _status.Text = $"{_rows.Count(r => r.Kind == null)} layers not in the KSIA form, {_rows.Count(r => r.Kind != null)} kinds of objects on 0 / Defpoints; " +
                           $"{_rows.Count(r => r.Tick)} ticked, {_rows.Count(r => r.FrozenInViewport)} frozen in a viewport (left as they are)";
        }

        private void Apply()
        {
            _grid.EndEdit();
            List<LayerMapRow> chosen = _rows.Where(r => r.Tick && !r.FrozenInViewport && !string.IsNullOrWhiteSpace(r.Target)).ToList();
            if (chosen.Count == 0) { MessageBox.Show(this, "Tick the rows to apply and choose the layer each goes to.", Text); return; }
            if (MessageBox.Show(this, $"Move the objects of {chosen.Count} row(s) to their KSIA layers ({chosen.Sum(r => r.Objects)} objects)? UNDO reverses it.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string report;
            Cursor = Cursors.WaitCursor;
            try { report = LayerMap.Apply(_doc, chosen); LayerMap.Remember(chosen); }
            catch (Exception ex) { C3DProLogger.Error("Layer names: apply failed", ex); report = "Layer names failed: " + ex.Message + ". Nothing was changed."; }
            finally { Cursor = Cursors.Default; }
            string summary = RunReport.Write(report, "C3DPROSTANDARDS", "Layers");
            Read();
            MessageBox.Show(this, summary, Text);
        }
    }
}
