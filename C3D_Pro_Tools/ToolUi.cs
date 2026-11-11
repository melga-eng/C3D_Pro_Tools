using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Layout pieces shared by the Network Editor and the Profile &amp; Cover Designer: the dark header, the side navigation,
    /// option cards with label / field rows, and number boxes that read the same in every locale.
    /// </summary>
    internal static class ToolUi
    {
        public static Control Header(string title, string subtitle, params Button[] actions)
        {
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Header, Padding = new Padding(16, 10, 12, 10) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            FlowLayoutPanel titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 16, 0) };
            titles.Controls.Add(new Label { Text = title, Font = StyleUi.Title, ForeColor = StyleUi.HeaderText, AutoSize = true, Margin = new Padding(0), UseMnemonic = false });
            titles.Controls.Add(new Label { Text = subtitle, ForeColor = StyleUi.HeaderMuted, AutoSize = true, Margin = new Padding(2, 0, 0, 0), UseMnemonic = false });
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
            foreach (Button b in actions) buttons.Controls.Add(b);
            header.Controls.Add(titles, 0, 0);
            header.Controls.Add(buttons, 1, 0);
            return header;
        }

        /// <summary>A card: a heading, an optional wrapped explanation, then label / field rows (see <see cref="Field"/>).</summary>
        public static TableLayoutPanel Card(string title, string info = null)
        {
            TableLayoutPanel card = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
                BackColor = StyleUi.Panel, Padding = new Padding(12, 8, 12, 10), Margin = new Padding(0, 0, 0, 8)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
            Label heading = new Label { Text = title, Font = StyleUi.Bold, ForeColor = StyleUi.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false };
            card.RowCount = 1;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(heading, 0, 0);
            card.SetColumnSpan(heading, 2);
            if (!string.IsNullOrEmpty(info)) Wide(card, Note(info));
            return card;
        }

        /// <summary>Adds a "label  field" row; the label lines up with the field's text.</summary>
        public static Control Field(TableLayoutPanel card, string label, Control field, ToolTip tips = null, string tip = null)
        {
            Label l = new Label { Text = label, AutoSize = true, ForeColor = StyleUi.Text, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 7, 10, 3) };
            if (field.Margin == Padding.Empty || field.Margin == new Padding(3)) field.Margin = new Padding(0, 3, 0, 3);
            // Lists fill the row (up to a readable width); number boxes and inline groups keep their own size.
            if (field is ComboBox)
            {
                field.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                field.MaximumSize = new Size(460, 0);
            }
            else field.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            int row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(l, 0, row);
            card.Controls.Add(field, 1, row);
            if (tips != null && tip != null) { tips.SetToolTip(l, tip); tips.SetToolTip(field, tip); }
            return field;
        }

        /// <summary>Adds a control across both columns (a check box, a note, a row of buttons).</summary>
        public static Control Wide(TableLayoutPanel card, Control c, ToolTip tips = null, string tip = null)
        {
            int row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (c is CheckBox) c.Margin = new Padding(0, 5, 0, 3);
            card.Controls.Add(c, 0, row);
            card.SetColumnSpan(c, 2);
            if (tips != null && tip != null) tips.SetToolTip(c, tip);
            return c;
        }

        /// <summary>A muted wrapped label; <see cref="FitNotes"/> gives it the width of its card.</summary>
        public static Label Note(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(0, 2, 0, 6), Tag = "note" };

        /// <summary>Gives every wrapped note in <paramref name="root"/> the width it has, so it breaks into lines instead of running off.</summary>
        public static void FitNotes(Control root, int width)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Label l && Equals(l.Tag, "note")) l.MaximumSize = new Size(Math.Max(120, width - 28), 0);
                else if (c.HasChildren) FitNotes(c, width);
            }
        }

        public static CheckBox Check(string text, bool value = false) => new CheckBox { Text = text, AutoSize = true, Checked = value };

        /// <summary>Makes radio buttons that sit in different containers (e.g. one inline with a text box) behave as one group.</summary>
        public static void Exclusive(params RadioButton[] group)
        {
            foreach (RadioButton rb in group)
                rb.CheckedChanged += (s, e) => { if (rb.Checked) foreach (RadioButton other in group) if (other != rb) other.Checked = false; };
        }

        public static ComboBox Combo(int width = 0)
        {
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 0, 3), MinimumSize = new Size(140, 0) };
            if (width > 0) { c.Width = width; c.MinimumSize = new Size(width, 0); }
            return c;
        }

        public static TextBox Number(double value, int width = 70)
        {
            TextBox box = new TextBox { Width = width, Text = Format(value), Margin = new Padding(0, 3, 0, 3), TextAlign = HorizontalAlignment.Right };
            box.KeyPress += (s, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '.' && e.KeyChar != '-' && e.KeyChar != ',' && e.KeyChar != ' ') e.Handled = true;
            };
            return box;
        }

        /// <summary>A number box with its unit after it, sized to its content (for use as a field).</summary>
        public static FlowLayoutPanel WithUnit(Control box, string unit) => Inline(box, new Label { Text = unit, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 0, 0) });

        public static FlowLayoutPanel Inline(params Control[] controls)
        {
            // One line: a wrapping row inside a table is measured at a narrower width than it gets, leaving a gap under it.
            FlowLayoutPanel f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            foreach (Control c in controls) { if (c.Margin == new Padding(3)) c.Margin = new Padding(0, 3, 6, 3); f.Controls.Add(c); }
            return f;
        }

        public static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static bool TryNumber(TextBox box, out double value) =>
            double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        /// <summary>Reads a list such as "-0.4, -0.2, 0, 0.2" (commas, semicolons or spaces between values).</summary>
        public static bool TryNumberList(string text, out List<double> values)
        {
            values = new List<double>();
            foreach (string part in text.Split(new[] { ';', ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries))
                foreach (string piece in part.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!double.TryParse(piece.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
                    values.Add(v);
                }
            return values.Count > 0;
        }

        public static void Select(ComboBox box, string preferred)
        {
            int index = preferred == null ? -1 : box.Items.IndexOf(preferred);
            box.SelectedIndex = index >= 0 ? index : (box.Items.Count > 0 ? 0 : -1);
        }

        /// <summary>A scrolling page that stacks cards from the top.</summary>
        public static (Panel host, TableLayoutPanel stack) Page()
        {
            Panel host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = StyleUi.Surface, Padding = new Padding(12, 10, 12, 4) };
            TableLayoutPanel stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0) };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.Controls.Add(stack);
            // The width ignores the scroll bar, so a note re-wrapping can never toggle the scroll bar and resize the page again.
            int last = -1;
            host.Resize += (s, e) =>
            {
                int width = host.Width - SystemInformation.VerticalScrollBarWidth - host.Padding.Horizontal - 2;
                if (width == last) return;
                last = width;
                FitNotes(stack, width);
            };
            return (host, stack);
        }

        public static void Add(TableLayoutPanel stack, Control c)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Top;
            stack.Controls.Add(c, 0, stack.RowCount++);
        }

        /// <summary>A plain DataGridView in the tools' look: full-row selection, no row headers, light grid.</summary>
        public static DataGridView Grid()
        {
            DataGridView g = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = StyleUi.Panel, BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = StyleUi.Border, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 30,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ShowCellToolTips = true, MultiSelect = true
            };
            g.RowTemplate.Height = 26;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 229, 220); // #EAE5DC (Direction 2 grid header)
            g.ColumnHeadersDefaultCellStyle.ForeColor = StyleUi.Muted;
            g.ColumnHeadersDefaultCellStyle.Font = StyleUi.Bold;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(234, 229, 220);
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = StyleUi.Muted;
            g.DefaultCellStyle.SelectionBackColor = StyleUi.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = StyleUi.Text;
            g.DataError += (s, e) => { e.ThrowException = false; };
            return g;
        }
    }

    /// <summary>The side navigation: one entry per task, a title and a short line under it, the current one highlighted.</summary>
    internal sealed class NavList : Control
    {
        private readonly List<(string title, string info)> _items = new List<(string, string)>();
        private int _selected, _hover = -1;
        private const int ItemHeight = 54;

        public event EventHandler SelectedChanged;

        public NavList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(234, 229, 220); // #EAE5DC (Direction 2 vellum nav background)
            Dock = DockStyle.Left;
            Width = 236;
            Cursor = Cursors.Hand;
        }

        public void Add(string title, string info) { _items.Add((title, info)); Invalidate(); }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Selected
        {
            get => _selected;
            set
            {
                int v = Math.Max(0, Math.Min(_items.Count - 1, value));
                if (v == _selected && IsHandleCreated) return;
                _selected = v;
                Invalidate();
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private int Scaled(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        private int IndexAt(Point p) { int i = (p.Y - Scaled(8)) / Scaled(ItemHeight); return i >= 0 && i < _items.Count ? i : -1; }

        protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.Location); if (i != _hover) { _hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int i = IndexAt(e.Location); if (i >= 0) Selected = i; base.OnMouseClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            using (Pen line = new Pen(StyleUi.Border)) g.DrawLine(line, Width - 1, 0, Width - 1, Height);
            int h = Scaled(ItemHeight), top = Scaled(8);
            for (int i = 0; i < _items.Count; i++)
            {
                Rectangle r = new Rectangle(Scaled(8), top + i * h, Width - Scaled(16), h - Scaled(4));
                if (i == _selected || i == _hover)
                {
                    using (GraphicsPath path = StyleUi.Rounded(r, 6))
                    using (SolidBrush b = new SolidBrush(i == _selected ? StyleUi.Panel : Color.FromArgb(223, 217, 206))) // #DFD9CE (Direction 2 nav hover)
                        g.FillPath(b, path);
                    if (i == _selected)
                        using (SolidBrush accent = new SolidBrush(StyleUi.Accent)) g.FillRectangle(accent, r.X, r.Y + Scaled(8), Scaled(3), r.Height - Scaled(16));
                }
                using (SolidBrush t = new SolidBrush(i == _selected ? StyleUi.Accent : StyleUi.Text))
                    g.DrawString(_items[i].title, StyleUi.Bold, t, r.X + Scaled(12), r.Y + Scaled(7));
                RectangleF info = new RectangleF(r.X + Scaled(12), r.Y + Scaled(26), r.Width - Scaled(16), Scaled(20));
                using (SolidBrush m = new SolidBrush(StyleUi.Muted))
                using (StringFormat f = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(_items[i].info, StyleUi.Small, m, info, f);
            }
        }
    }
}
