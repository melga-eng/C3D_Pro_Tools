using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Label = System.Windows.Forms.Label;

namespace C3DProTools.Features.Standards
{
    /// <summary>
    /// Network Layers window: the networks (ICT ticked), the layer of each part type (learned from the drawing, editable, saved
    /// in it), and what is on the wrong layer (each one can be unticked). Fix moves the ticked ones in one UNDO.
    /// </summary>
    internal sealed class FormLayerAudit : Form
    {
        private readonly Document _doc;
        private LayerAudit.Scan _scan;
        private List<LayerFinding> _findings = new List<LayerFinding>();
        private readonly CheckedListBox _nets = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly DataGridView _rules = Grid();
        private readonly DataGridView _found = Grid();
        private readonly CheckBox _stop = new CheckBox { Text = "Stop per-network layers for new pipes, structures and labels", AutoSize = true, Checked = true };
        private readonly CheckBox _purge = new CheckBox { Text = "Delete per-network layers left empty", AutoSize = true, Checked = true };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private bool _filling;
        private readonly HashSet<string> _unticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter
        };

        public FormLayerAudit(Document doc)
        {
            _doc = doc;
            Text = "Network Layers";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(800, 520);
            ShowInTaskbar = false;

            _rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "Part type (from description)", ReadOnly = true, FillWeight = 40 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "Parts", ReadOnly = true, FillWeight = 8 });
            _rules.Columns.Add(new DataGridViewComboBoxColumn { Name = "layer", HeaderText = "Layer", FillWeight = 52, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
            _rules.CellValueChanged += (s, e) => RuleChanged(e.RowIndex);
            _rules.CurrentCellDirtyStateChanged += (s, e) => { if (_rules.IsCurrentCellDirty) _rules.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _rules.DataError += (s, e) => e.ThrowException = false;

            _found.Columns.Add(new DataGridViewCheckBoxColumn { Name = "fix", HeaderText = "Fix", FillWeight = 5 });
            foreach ((string n, string h, int w) in new[] { ("kind", "Object", 10), ("name", "Name", 16), ("net", "Network", 14), ("from", "Layer now", 22), ("to", "Goes to", 22), ("why", "Why", 16) })
                _found.Columns.Add(new DataGridViewTextBoxColumn { Name = n, HeaderText = h, ReadOnly = true, FillWeight = w });
            _found.CurrentCellDirtyStateChanged += (s, e) => { if (_found.IsCurrentCellDirty) _found.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _found.CellValueChanged += (s, e) =>
            {
                if (_filling || e.RowIndex < 0 || !(_found.Rows[e.RowIndex].Tag is LayerFinding f)) return;
                if (_found.Rows[e.RowIndex].Cells["fix"].Value is bool on && on) _unticked.Remove(f.Item.Handle); else _unticked.Add(f.Item.Handle);
                UpdateStatus();
            };

            Button check = StyleUi.Secondary("Check", (s, e) => Check());
            Button fix = StyleUi.Primary("Fix ticked", (s, e) => Fix());
            Button close = StyleUi.Secondary("Close", (s, e) => Close());
            CancelButton = close;

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(StyleUi.Caption("Networks"), 0, 0);
            left.Controls.Add(_nets, 0, 1);
            left.Controls.Add(check, 0, 2);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            right.Controls.Add(StyleUi.Caption("Layer of each part type (learned from the drawing; change one and the list below follows)"), 0, 0);
            right.Controls.Add(_rules, 0, 1);
            var foundHead = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            foundHead.Controls.Add(StyleUi.Caption("On the wrong layer"));
            foundHead.Controls.Add(StyleUi.Secondary("Tick all", (s, e) => TickAll(true)));
            foundHead.Controls.Add(StyleUi.Secondary("Clear", (s, e) => TickAll(false)));
            foundHead.Controls.Add(_status);
            right.Controls.Add(foundHead, 0, 2);
            right.Controls.Add(_found, 0, 3);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            split.Panel1.Padding = new Padding(12, 10, 6, 6);
            split.Panel2.Padding = new Padding(6, 10, 12, 6);

            var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 2, 12, 0), BackColor = StyleUi.Surface };
            options.Controls.Add(_stop);
            options.Controls.Add(_purge);
            options.Controls.Add(ToolUi.Note("Labels on other layers (tile layers frozen per viewport) are left alone. Other networks are not touched. One UNDO."));
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8), BackColor = StyleUi.Surface };
            footer.Controls.Add(close);
            footer.Controls.Add(fix);

            Controls.Add(split);
            Controls.Add(options);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("Network Layers", "Pipes and structures on the layer of their type, labels on their labelling layer"));

            Shown += (s, e) => { ReadDrawing(null); };
        }

        /// <summary>Reads the drawing: the networks ticked (null: the ICT ones), their parts and labels.</summary>
        private void ReadDrawing(List<string> ticked)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _scan = LayerAudit.Read(_doc, "", ticked);
                _filling = true;
                _nets.Items.Clear();
                foreach (string n in _scan.AllNetworks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) _nets.Items.Add(n, _scan.Networks.Contains(n));
                var layerCol = (DataGridViewComboBoxColumn)_rules.Columns["layer"];
                layerCol.Items.Clear();
                layerCol.Items.Add("");
                foreach (string l in _scan.Layers) layerCol.Items.Add(l);
                foreach (string l in _scan.Rules.Values.Where(v => !layerCol.Items.Contains(v))) layerCol.Items.Add(l);
                _rules.Rows.Clear();
                foreach (var g in _scan.Items.Where(i => !i.IsLabel).GroupBy(i => LayerRules.TypeKey(i.Description, i.Size), StringComparer.OrdinalIgnoreCase)
                                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    _rules.Rows.Add(g.Key.Length == 0 ? "(no description or size)" : g.Key, g.Count(), _scan.Rules.TryGetValue(g.Key, out string to) ? to : "");
                _filling = false;
                Recheck();
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Network Layers: reading the drawing failed", ex);
                _status.Text = "Reading the drawing failed: " + ex.Message;
            }
            finally { _filling = false; Cursor = Cursors.Default; }
        }

        private void Check() => ReadDrawing(_nets.CheckedItems.Cast<string>().ToList());

        private void RuleChanged(int row)
        {
            if (_filling || row < 0 || _scan == null) return;
            string key = (string)_rules.Rows[row].Cells["type"].Value;
            string layer = _rules.Rows[row].Cells["layer"].Value as string ?? "";
            _scan.Rules[key] = layer;
            _scan.Saved[key] = layer;
            Recheck();
        }

        private void Recheck()
        {
            _findings = _scan.Check();
            _filling = true;
            _found.Rows.Clear();
            foreach (LayerFinding f in _findings)
            {
                // What the user unticked stays unticked when a rule changes (review finding).
                int i = _found.Rows.Add(f.To != null && !_unticked.Contains(f.Item.Handle),f.Item.Kind, f.Item.Name, f.Item.Network, f.Item.Layer, f.To ?? "(set the layer of its type above)", f.Why);
                _found.Rows[i].Tag = f;
                if (f.To == null) { _found.Rows[i].Cells["fix"].ReadOnly = true; _found.Rows[i].DefaultCellStyle.ForeColor = StyleUi.Muted; }
            }
            _filling = false;
            UpdateStatus();
        }

        private void TickAll(bool on)
        {
            foreach (DataGridViewRow r in _found.Rows) if (((LayerFinding)r.Tag).To != null) r.Cells["fix"].Value = on;  // each change updates _unticked
            UpdateStatus();
        }

        private List<LayerFinding> Ticked() =>
            _found.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["fix"].Value is bool b && b).Select(r => (LayerFinding)r.Tag).Where(f => f.To != null).ToList();

        private void UpdateStatus()
        {
            if (_filling || _scan == null) return;
            int parts = _scan.Items.Count(i => !i.IsLabel);
            _status.Text = $"{_findings.Count} of {parts} parts and {_scan.Items.Count - parts} labels; {Ticked().Count} ticked";
        }

        private void Fix()
        {
            if (_scan == null) return;
            // The list shows the networks of the last check; a network ticked or unticked since must be checked first (review finding).
            var nowTicked = new HashSet<string>(_nets.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            if (!nowTicked.SetEquals(_scan.Networks))
            {
                ReadDrawing(nowTicked.ToList());
                MessageBox.Show(this, "The networks ticked changed, so the list was checked again. Look it over, then press Fix ticked.", Text);
                return;
            }
            List<LayerFinding> chosen = Ticked();
            if (chosen.Count == 0 && !_stop.Checked) { MessageBox.Show(this, "Nothing is ticked.", Text); return; }
            var log = new StringBuilder();
            Cursor = Cursors.WaitCursor;
            try
            {
                LayerAudit.Fix(_doc, _scan, chosen, new LayerAudit.FixOptions { StopPerNetworkLayers = _stop.Checked, DeleteEmptyLayers = _purge.Checked }, log);
                ViewHelper.Regen(_doc);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Network Layers: fix failed", ex);
                log.AppendLine($"Fix failed: {ex.Message}. Nothing was changed.");
            }
            finally { Cursor = Cursors.Default; }
            string summary = RunReport.Write(log.ToString(), "C3DPROLAYERS", "Fix");
            ReadDrawing(_nets.CheckedItems.Cast<string>().ToList());
            MessageBox.Show(this, summary, Text);
        }
    }
}
