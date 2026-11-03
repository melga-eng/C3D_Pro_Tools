using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Font = System.Drawing.Font;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>Colours, fonts and small controls shared by the Style Manager windows. Layouts use docking and auto-size,
    /// so every window resizes and no text is squeezed onto two lines.</summary>
    internal static class StyleUi
    {
        public static readonly Color Header = Color.FromArgb(15, 23, 42);
        public static readonly Color HeaderText = Color.FromArgb(241, 245, 249);
        public static readonly Color HeaderMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color Surface = Color.FromArgb(248, 250, 252);
        public static readonly Color Panel = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color Text = Color.FromArgb(15, 23, 42);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentSoft = Color.FromArgb(219, 234, 254);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);

        public static readonly Font Base = new Font("Segoe UI", 9F);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9F);
        public static readonly Font Small = new Font("Segoe UI", 8F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15F);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 11F);

        /// <summary>Text and background colours of a status pill.</summary>
        public static (Color fore, Color back) PillColors(string kind)
        {
            switch (kind)
            {
                case "Used": return (Color.FromArgb(21, 128, 61), Color.FromArgb(220, 252, 231));
                case "Unused": return (Color.FromArgb(180, 83, 9), Color.FromArgb(254, 243, 199));
                case "Hidden ref": return (Color.FromArgb(109, 40, 217), Color.FromArgb(237, 233, 254));
                case "Protected": return (Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240));
                case "Duplicate": return (Color.FromArgb(194, 65, 12), Color.FromArgb(255, 237, 213));
                case "Non-standard": return (Color.FromArgb(190, 18, 60), Color.FromArgb(255, 228, 230));
                case "Standard": return (Color.FromArgb(15, 118, 110), Color.FromArgb(204, 251, 241));
                case "Missing": return (Color.FromArgb(29, 78, 216), Color.FromArgb(219, 234, 254));
                case "Checked": return (Accent, AccentSoft);
                case "High": case "Imported": case "Exact": return (Color.FromArgb(21, 128, 61), Color.FromArgb(220, 252, 231));
                case "Medium": case "Nearest size": return (Color.FromArgb(180, 83, 9), Color.FromArgb(254, 243, 199));
                case "Low": case "Failed": case "No part": return (Color.FromArgb(185, 28, 28), Color.FromArgb(254, 226, 226));
                case "Pipe": return (Color.FromArgb(29, 78, 216), Color.FromArgb(219, 234, 254));
                case "Box": return (Color.FromArgb(15, 118, 110), Color.FromArgb(204, 251, 241));
                case "Structure": return (Color.FromArgb(109, 40, 217), Color.FromArgb(237, 233, 254));
                case "Solid": return (Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240));
                case "Skip": case "Already imported": return (Color.FromArgb(100, 116, 139), Color.FromArgb(241, 245, 249));
                case "Review": return (Color.FromArgb(180, 83, 9), Color.FromArgb(254, 243, 199));
                default: return (Muted, Border);
            }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Draws a pill and returns its width.</summary>
        public static float DrawPill(Graphics g, string text, float x, float centerY, Font font)
        {
            (Color fore, Color back) = PillColors(text);
            SizeF size = g.MeasureString(text, font);
            RectangleF r = new RectangleF(x, centerY - size.Height / 2 - 1, size.Width + 10, size.Height + 2);
            using (GraphicsPath p = Rounded(r, r.Height / 2))
            using (SolidBrush b = new SolidBrush(back))
                g.FillPath(b, p);
            using (SolidBrush t = new SolidBrush(fore))
                g.DrawString(text, font, t, r.X + 5, r.Y + 1);
            return r.Width;
        }

        public static Button Primary(string text, EventHandler click = null)
        {
            Button b = Basic(text, click);
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 78, 216);
            b.Font = Bold;
            return b;
        }

        public static Button Secondary(string text, EventHandler click = null)
        {
            Button b = Basic(text, click);
            b.BackColor = Panel;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            b.FlatAppearance.MouseOverBackColor = Surface;
            return b;
        }

        public static Button DangerButton(string text, EventHandler click = null)
        {
            Button b = Secondary(text, click);
            b.ForeColor = Danger;
            b.FlatAppearance.BorderColor = Color.FromArgb(252, 165, 165);
            return b;
        }

        /// <summary>Flat button for the dark header bar.</summary>
        public static Button HeaderButton(string text, EventHandler click = null)
        {
            Button b = Basic(text, click);
            b.BackColor = Color.FromArgb(30, 41, 59);
            b.ForeColor = HeaderText;
            b.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 65, 85);
            return b;
        }

        private static Button Basic(string text, EventHandler click)
        {
            Button b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(3),
                MinimumSize = new Size(0, 30),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            if (click != null) b.Click += click;
            return b;
        }

        public static Label Caption(string text, bool muted = false) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = muted ? Muted : Text,
            Margin = new Padding(3, 8, 3, 3),
            Anchor = AnchorStyles.Left
        };

        public static TextBox Box(string placeholder = "") => new TextBox
        {
            PlaceholderText = placeholder,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 4, 3, 4),
            MinimumSize = new Size(120, 0)
        };

        /// <summary>A row of controls that flows left to right and wraps instead of clipping.</summary>
        public static FlowLayoutPanel Row(params Control[] controls)
        {
            FlowLayoutPanel f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0),
                Padding = new Padding(0, 2, 0, 2)
            };
            f.Controls.AddRange(controls);
            return f;
        }
    }

    /// <summary>A white card with a title, a short explanation and its own controls underneath.</summary>
    internal sealed class StyleCard : Panel
    {
        public readonly Label Heading;
        public readonly Label Info;
        public readonly FlowLayoutPanel Body;

        /// <summary>Without <paramref name="content"/> the card stacks controls added to <see cref="Body"/>; with it, that control fills the card.</summary>
        public StyleCard(string title, string info, Control content = null)
        {
            BackColor = StyleUi.Panel;
            Padding = new Padding(14, 10, 14, 10);
            Margin = new Padding(6);
            Dock = DockStyle.Fill;

            Body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            Info = new Label { Text = info, Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 2, 0, 6), Visible = !string.IsNullOrEmpty(info) };
            Heading = new Label { Text = title, Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading, ForeColor = StyleUi.Text };
            if (content != null) { content.Dock = DockStyle.Fill; Controls.Add(content); }
            else Controls.Add(Body);
            Controls.Add(Info);
            Controls.Add(Heading);
            Resize += (s, e) => Info.MaximumSize = new Size(Math.Max(100, ClientSize.Width - Padding.Horizontal), 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(StyleUi.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>A rounded filter chip with a coloured dot, a label and a count.</summary>
    internal sealed class StyleChip : Control
    {
        private int _count;
        private bool _selected, _hover;
        public readonly string Kind;

        public StyleChip(string text, string kind)
        {
            Text = text;
            Kind = kind;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = StyleUi.Base;
            Cursor = Cursors.Hand;
            Margin = new Padding(3, 7, 3, 7);
            Height = 28;
            UpdateWidth();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Count { get => _count; set { _count = value; UpdateWidth(); Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        private void UpdateWidth()
        {
            using (Graphics g = CreateGraphics())
                Width = (int)(g.MeasureString(Text, StyleUi.Base).Width + g.MeasureString(_count.ToString("N0"), StyleUi.Bold).Width) + 38;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? StyleUi.Surface);
            (Color dot, Color soft) = StyleUi.PillColors(Kind);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = StyleUi.Rounded(r, r.Height / 2))
            {
                using (SolidBrush fill = new SolidBrush(_selected ? soft : _hover ? Color.FromArgb(241, 245, 249) : StyleUi.Panel))
                    g.FillPath(fill, path);
                using (Pen border = new Pen(_selected ? dot : StyleUi.Border, _selected ? 1.6f : 1f))
                    g.DrawPath(border, path);
            }
            using (SolidBrush b = new SolidBrush(dot))
                g.FillEllipse(b, 10, Height / 2f - 4, 8, 8);
            using (SolidBrush t = new SolidBrush(StyleUi.Text))
            {
                SizeF label = g.MeasureString(Text, StyleUi.Base);
                g.DrawString(Text, StyleUi.Base, t, 22, (Height - label.Height) / 2);
                using (SolidBrush c = new SolidBrush(dot))
                    g.DrawString(_count.ToString("N0"), StyleUi.Bold, c, 24 + label.Width, (Height - label.Height) / 2);
            }
        }
    }
}
