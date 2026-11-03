using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    internal sealed class PreviewRow
    {
        public string[] Cells;
        public bool Checked = true;
        /// <summary>False rows are shown greyed with their reason and cannot be ticked.</summary>
        public bool Enabled = true;
        public object Tag;
    }

    /// <summary>Lists what an operation will do, one tickable row per change, before anything in the drawing is touched.</summary>
    internal sealed class FormStylePreview : Form
    {
        private readonly ListView _list = new ListView();
        private readonly List<PreviewRow> _rows;
        private readonly Label _count = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 3, 3) };

        public List<object> Chosen { get; } = new List<object>();

        public FormStylePreview(string title, string intro, string[] columns, List<PreviewRow> rows, string applyText, bool destructive = false, string windowTitle = null)
        {
            _rows = rows;
            Text = windowTitle ?? "Style Manager - " + title;
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(980, 560);
            MinimumSize = new Size(640, 380);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;

            Panel head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 12, 16, 10), BackColor = StyleUi.Panel };
            Label lblIntro = new Label { Text = intro, Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 4, 0, 0) };
            Label lblTitle = new Label { Text = title, Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading };
            head.Controls.Add(lblIntro);
            head.Controls.Add(lblTitle);
            head.Resize += (s, e) => lblIntro.MaximumSize = new Size(Math.Max(200, head.ClientSize.Width - 32), 0);

            TextBox filter = new TextBox { PlaceholderText = "Filter rows…", Width = 240, Margin = new Padding(3, 5, 3, 3) };
            filter.TextChanged += (s, e) => Fill(filter.Text);
            FlowLayoutPanel tools = StyleUi.Row(
                StyleUi.Secondary("Tick all", (s, e) => SetAll(true)),
                StyleUi.Secondary("Untick all", (s, e) => SetAll(false)),
                StyleUi.Secondary("Copy list", (s, e) => CopyList()),
                filter, _count);
            tools.Padding = new Padding(12, 6, 12, 4);
            tools.BackColor = StyleUi.Surface;

            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.BorderStyle = BorderStyle.None;
            _list.Dock = DockStyle.Fill;
            foreach (string c in columns) _list.Columns.Add(c, 160);
            _list.ItemCheck += (s, e) => { if (!((PreviewRow)_list.Items[e.Index].Tag).Enabled) e.NewValue = CheckState.Unchecked; };
            _list.ItemChecked += (s, e) => { ((PreviewRow)e.Item.Tag).Checked = e.Item.Checked; UpdateCount(); };
            Panel listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0), BackColor = StyleUi.Surface };
            listHost.Controls.Add(_list);

            Button apply = destructive ? StyleUi.DangerButton(applyText) : StyleUi.Primary(applyText);
            apply.Click += (s, e) =>
            {
                Chosen.AddRange(_rows.Where(r => r.Checked && r.Enabled).Select(r => r.Tag));
                DialogResult = DialogResult.OK;
            };
            Button cancel = StyleUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                Padding = new Padding(12, 8, 12, 10), BackColor = StyleUi.Surface
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            CancelButton = cancel;

            Controls.Add(listHost);
            Controls.Add(tools);
            Controls.Add(head);
            Controls.Add(buttons);

            Fill("");
            Shown += (s, e) => AutoSizeColumns();
        }

        private void Fill(string filter)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (PreviewRow r in _rows)
            {
                if (filter.Length > 0 && !r.Cells.Any(c => c != null && c.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                ListViewItem item = new ListViewItem(r.Cells) { Tag = r, Checked = r.Checked && r.Enabled };
                if (!r.Enabled) item.ForeColor = SystemColors.GrayText;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            UpdateCount();
        }

        private void AutoSizeColumns()
        {
            foreach (ColumnHeader c in _list.Columns)
            {
                c.Width = -2;
                if (c.Width > 380) c.Width = 380;
                if (c.Width < 70) c.Width = 70;
            }
        }

        private void SetAll(bool value)
        {
            foreach (ListViewItem item in _list.Items) item.Checked = value && ((PreviewRow)item.Tag).Enabled;
        }

        private void UpdateCount() => _count.Text = $"{_rows.Count(r => r.Checked && r.Enabled)} of {_rows.Count} ticked";

        private void CopyList()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", _list.Columns.Cast<ColumnHeader>().Select(c => c.Text)));
            foreach (PreviewRow r in _rows) sb.AppendLine(string.Join("\t", r.Cells));
            Clipboard.SetText(sb.ToString());
        }
    }
}
