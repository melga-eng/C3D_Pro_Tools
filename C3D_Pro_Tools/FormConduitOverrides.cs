using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using C3DProTools.Features.Networks.Contracts;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Modal dialog to configure per-end exceptions to structure trimming and Civil 3D connection for Generate Conduits.
    /// </summary>
    internal sealed class FormConduitOverrides : Form
    {
        public sealed class EncasementItem
        {
            public DrawingObjectRef Ref { get; }
            public string Name { get; }
            public EncasementItem(DrawingObjectRef r, string name) { Ref = r; Name = name; }
            public override string ToString() => Name;
        }

        private readonly List<ConduitEndOverride> _overrides;
        private readonly List<EncasementItem> _pipes;
        private readonly ComboBox _cbPipe = ToolUi.Combo(240);
        private readonly ComboBox _cbEnd = ToolUi.Combo(120);
        private readonly CheckBox _chkOverrideTreatment = ToolUi.Check("Override trimming:");
        private readonly ComboBox _cbTreatment = ToolUi.Combo(180);
        private readonly CheckBox _chkOverrideConnect = ToolUi.Check("Override connection:");
        private readonly CheckBox _chkConnect = ToolUi.Check("Connect conduits to structure");
        private readonly ListView _list = new ListView();
        private readonly Label _lblEmpty = ToolUi.Note("No per-end overrides configured. Defaults apply to all ends.");

        public IReadOnlyList<ConduitEndOverride> Overrides => _overrides.AsReadOnly();

        public FormConduitOverrides(IEnumerable<EncasementItem> pipes, IEnumerable<ConduitEndOverride> existing)
        {
            _pipes = (pipes ?? Enumerable.Empty<EncasementItem>()).ToList();
            _overrides = (existing ?? Enumerable.Empty<ConduitEndOverride>()).ToList();

            Text = "Network Editor - Conduit End Overrides";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(680, 500);
            MinimumSize = new Size(580, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;

            Panel head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 12, 16, 10), BackColor = StyleUi.Panel };
            Label lblTitle = new Label { Text = "Per-End Conduit Overrides", Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading };
            Label lblIntro = new Label
            {
                Text = "Set specific structure trimming or Civil 3D connection behaviour for an individual encasement pipe end. Unchecked options keep the request default.",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = StyleUi.Muted,
                Padding = new Padding(0, 4, 0, 0),
                Tag = "note"
            };
            head.Controls.Add(lblIntro);
            head.Controls.Add(lblTitle);
            head.Resize += (s, e) => lblIntro.MaximumSize = new Size(Math.Max(200, head.ClientSize.Width - 32), 0);

            // Inputs card
            TableLayoutPanel card = ToolUi.Card("Configure Override", "Select an encasement and end, then choose which settings to override.");
            foreach (EncasementItem p in _pipes) _cbPipe.Items.Add(p);
            if (_cbPipe.Items.Count > 0) _cbPipe.SelectedIndex = 0;

            _cbEnd.Items.AddRange(new object[] { "Start", "End" });
            _cbEnd.SelectedIndex = 0;

            ToolUi.Field(card, "Pipe", _cbPipe);
            ToolUi.Field(card, "End", _cbEnd);

            _cbTreatment.Items.AddRange(new object[]
            {
                "Trim to inner wall",
                "Trim to outer wall",
                "Extend to structure centre",
                "Keep encasement end"
            });
            _cbTreatment.SelectedIndex = 0;
            _cbTreatment.Enabled = false;
            _chkOverrideTreatment.CheckedChanged += (s, e) => _cbTreatment.Enabled = _chkOverrideTreatment.Checked;
            ToolUi.Field(card, "Trimming", ToolUi.Inline(_chkOverrideTreatment, _cbTreatment));

            _chkConnect.Enabled = false;
            _chkOverrideConnect.CheckedChanged += (s, e) => _chkConnect.Enabled = _chkOverrideConnect.Checked;
            ToolUi.Field(card, "Connection", ToolUi.Inline(_chkOverrideConnect, _chkConnect));

            Button btnAdd = StyleUi.Secondary("Add / Update", (s, e) => AddOrUpdateCurrent());
            Button btnRemove = StyleUi.Secondary("Remove", (s, e) => RemoveSelected());
            Button btnClearAll = StyleUi.Secondary("Clear all", (s, e) => { _overrides.Clear(); RefreshList(); });
            ToolUi.Wide(card, ToolUi.Inline(btnAdd, btnRemove, btnClearAll));

            // List of configured overrides
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Dock = DockStyle.Fill;
            _list.Columns.Add("Pipe", 160);
            _list.Columns.Add("End", 70);
            _list.Columns.Add("Trimming override", 180);
            _list.Columns.Add("Connection override", 160);

            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _overrides.Count)
                    LoadOverrideIntoFields(_overrides[_list.SelectedIndices[0]]);
            };

            Panel listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6), BackColor = StyleUi.Surface };
            listPanel.Controls.Add(_list);

            // Bottom buttons
            Button btnOk = StyleUi.Primary("Save overrides");
            btnOk.DialogResult = DialogResult.OK;
            Button btnCancel = StyleUi.Secondary("Cancel");
            btnCancel.DialogResult = DialogResult.Cancel;
            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                Padding = new Padding(12, 8, 12, 10),
                BackColor = StyleUi.Surface
            };
            bottom.Controls.Add(btnCancel);
            bottom.Controls.Add(btnOk);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.Add(listPanel);
            Controls.Add(card);
            Controls.Add(head);
            Controls.Add(bottom);

            RefreshList();
            Shown += (s, e) => AutoSizeListColumns();
        }

        private void AutoSizeListColumns()
        {
            foreach (ColumnHeader c in _list.Columns)
            {
                c.Width = -2;
                if (c.Width < 80) c.Width = 80;
            }
        }

        private void RefreshList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (ConduitEndOverride ov in _overrides)
            {
                string pipeName = _pipes.FirstOrDefault(p => p.Ref == ov.Encasement)?.Name ?? $"Pipe {ov.Encasement.Handle}";
                string endName = ov.End == PipeEnd.Start ? "Start" : "End";
                string trim = ov.Treatment.HasValue ? ov.Treatment.Value switch
                {
                    StructureEndTreatment.InnerWall => "Inner wall",
                    StructureEndTreatment.OuterWall => "Outer wall",
                    StructureEndTreatment.StructureCentre => "Structure centre",
                    StructureEndTreatment.EncasementEnd => "Keep encasement end",
                    _ => ov.Treatment.Value.ToString()
                } : "(default)";
                string conn = ov.Connect.HasValue ? (ov.Connect.Value ? "Connect" : "Do not connect") : "(default)";

                ListViewItem item = new ListViewItem(new[] { pipeName, endName, trim, conn }) { Tag = ov };
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            AutoSizeListColumns();
        }

        private void LoadOverrideIntoFields(ConduitEndOverride ov)
        {
            int pipeIdx = _pipes.FindIndex(p => p.Ref == ov.Encasement);
            if (pipeIdx >= 0) _cbPipe.SelectedIndex = pipeIdx;
            _cbEnd.SelectedIndex = ov.End == PipeEnd.Start ? 0 : 1;

            if (ov.Treatment.HasValue)
            {
                _chkOverrideTreatment.Checked = true;
                _cbTreatment.Enabled = true;
                _cbTreatment.SelectedIndex = (int)ov.Treatment.Value;
            }
            else
            {
                _chkOverrideTreatment.Checked = false;
                _cbTreatment.Enabled = false;
            }

            if (ov.Connect.HasValue)
            {
                _chkOverrideConnect.Checked = true;
                _chkConnect.Enabled = true;
                _chkConnect.Checked = ov.Connect.Value;
            }
            else
            {
                _chkOverrideConnect.Checked = false;
                _chkConnect.Enabled = false;
            }
        }

        private void AddOrUpdateCurrent()
        {
            if (!(_cbPipe.SelectedItem is EncasementItem pipe))
            {
                MessageBox.Show(this, "Select an encasement pipe.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_chkOverrideTreatment.Checked && !_chkOverrideConnect.Checked)
            {
                MessageBox.Show(this, "Tick at least one override (trimming or connection).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PipeEnd end = _cbEnd.SelectedIndex == 1 ? PipeEnd.End : PipeEnd.Start;
            StructureEndTreatment? treatment = _chkOverrideTreatment.Checked
                ? (StructureEndTreatment)_cbTreatment.SelectedIndex
                : (StructureEndTreatment?)null;
            bool? connect = _chkOverrideConnect.Checked
                ? _chkConnect.Checked
                : (bool?)null;

            ConduitEndOverride ov = new ConduitEndOverride(pipe.Ref, end, treatment, connect);

            int existingIdx = _overrides.FindIndex(x => x.Encasement == pipe.Ref && x.End == end);
            if (existingIdx >= 0)
                _overrides[existingIdx] = ov;
            else
                _overrides.Add(ov);

            RefreshList();
        }

        private void RemoveSelected()
        {
            if (_list.SelectedIndices.Count == 0) return;
            int idx = _list.SelectedIndices[0];
            if (idx >= 0 && idx < _overrides.Count)
            {
                _overrides.RemoveAt(idx);
                RefreshList();
            }
        }
    }
}
