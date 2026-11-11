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
        public static readonly Color Header = Color.FromArgb(14, 26, 45);        // #0E1A2D (Deep Prussian blueprint navy)
        public static readonly Color HeaderText = Color.FromArgb(244, 247, 251);  // #F4F7FB (Starch white)
        public static readonly Color HeaderMuted = Color.FromArgb(141, 161, 190); // #8DA1BE (Blueprint chalk cyan metadata)
        public static readonly Color Surface = Color.FromArgb(243, 239, 232);    // #F3EFE8 (Warm architectural drafting vellum)
        public static readonly Color Panel = Color.White;                         // #FFFFFF (Clean drafting card & grid background)
        public static readonly Color Border = Color.FromArgb(217, 209, 195);     // #D9D1C3 (Drafting rule buff borders & grid lines)
        public static readonly Color Text = Color.FromArgb(23, 28, 36);          // #171C24 (Drafting ink black)
        public static readonly Color Muted = Color.FromArgb(105, 100, 88);       // #696458 (Pencil lead sepia notes)
        public static readonly Color Accent = Color.FromArgb(27, 88, 156);       // #1B589C (Prussian cobalt blueprint ink)
        public static readonly Color AccentSoft = Color.FromArgb(228, 237, 247); // #E4EDF7 (Blueprint wash row selection)
        public static readonly Color Danger = Color.FromArgb(185, 28, 28);       // #B91C1C (Red correction pen alert)

        private static readonly string FontFamilyName = ResolveFontFamily();

        private static string ResolveFontFamily()
        {
            try
            {
                using (Font test = new Font("Aptos", 9F))
                {
                    if (test.Name == "Aptos")
                        return "Aptos";
                }
            }
            catch
            {
            }
            return "Segoe UI";
        }

        public static readonly Font Base = new Font(FontFamilyName, 9F);
        public static readonly Font Bold = FontFamilyName == "Aptos"
            ? new Font("Aptos", 9F, FontStyle.Bold)
            : new Font("Segoe UI Semibold", 9F);
        public static readonly Font Small = new Font(FontFamilyName, 8F);
        public static readonly Font Title = FontFamilyName == "Aptos"
            ? new Font("Aptos", 15F, FontStyle.Bold)
            : new Font("Segoe UI Semibold", 15F);
        public static readonly Font Heading = FontFamilyName == "Aptos"
            ? new Font("Aptos", 11F, FontStyle.Bold)
            : new Font("Segoe UI Semibold", 11F);
        public static readonly Font Mono = new Font("Consolas", 9F);

        /// <summary>Text and background colours of a status pill.</summary>
        public static (Color fore, Color back) PillColors(string kind)
        {
            switch (kind)
            {
                case "Standard":
                    return (Color.FromArgb(14, 100, 102), Color.FromArgb(219, 241, 241)); // #0E6466, #DBF1F1 (Standard compliance)
                case "High": case "Imported": case "Exact": case "Used":
                    return (Color.FromArgb(21, 101, 53), Color.FromArgb(225, 242, 231)); // #156535, #E1F2E7 (Exact / High survey precision)
                case "Medium": case "Nearest size": case "Review": case "Unused": case "Duplicate":
                    return (Color.FromArgb(146, 64, 14), Color.FromArgb(253, 241, 214)); // #92400E, #FDF1D6 (Ochre review / medium)
                case "Missing":
                    return (Color.FromArgb(27, 73, 133), Color.FromArgb(222, 234, 248)); // #1B4985, #DEEAF8 (Missing ungenerated)
                case "Non-standard": case "Low": case "Failed": case "No part":
                    return (Color.FromArgb(153, 27, 27), Color.FromArgb(252, 226, 226)); // #991B1B, #FCE2E2 (Crimson deviation / alert)
                case "Pipe":
                    return (Color.FromArgb(0, 102, 153), Color.FromArgb(225, 243, 251)); // #006699, #E1F3FB (Conduit run blue)
                case "Box":
                    return (Color.FromArgb(19, 107, 88), Color.FromArgb(221, 242, 237)); // #136B58, #DDF2ED (Duct bank teal)
                case "Checked":
                    return (Accent, AccentSoft);                                          // (#1B589C, #E4EDF7)
                case "Structure": case "Hidden ref":
                    return (Color.FromArgb(90, 58, 139), Color.FromArgb(239, 235, 248)); // #5A3A8B, #EFEBF8 (Blueprint indigo / structure)
                case "Protected": case "Solid":
                    return (Color.FromArgb(87, 83, 74), Color.FromArgb(234, 229, 220));  // #57534A, #EAE5DC (Pencil sepia slate)
                case "Skip": case "Already imported":
                    return (Color.FromArgb(105, 100, 88), Color.FromArgb(234, 229, 220)); // #696458, #EAE5DC (Muted sepia on soft vellum)
                default:
                    return (Muted, Border);
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
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 67, 119); // #144377 (Deep cobalt hover)
            b.Font = Bold;
            return b;
        }

        public static Button Secondary(string text, EventHandler click = null)
        {
            Button b = Basic(text, click);
            b.BackColor = Panel;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 191, 176); // #C8BFB0 (Buff drafting border)
            b.FlatAppearance.MouseOverBackColor = Surface;
            return b;
        }

        public static Button DangerButton(string text, EventHandler click = null)
        {
            Button b = Secondary(text, click);
            b.ForeColor = Danger;
            b.FlatAppearance.BorderColor = Color.FromArgb(238, 180, 180); // #EEB4B4 (Muted red outline)
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(252, 226, 226); // #FCE2E2 (Danger soft hover)
            return b;
        }

        /// <summary>Flat button for the dark header bar.</summary>
        public static Button HeaderButton(string text, EventHandler click = null)
        {
            Button b = Basic(text, click);
            b.BackColor = Color.FromArgb(25, 45, 75); // #192D4B (Prussian navy header button)
            b.ForeColor = HeaderText;
            b.FlatAppearance.BorderColor = Color.FromArgb(40, 68, 110); // #28446E
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 68, 110); // #28446E
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
                using (SolidBrush fill = new SolidBrush(_selected ? soft : _hover ? Color.FromArgb(234, 229, 220) : StyleUi.Panel))
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
