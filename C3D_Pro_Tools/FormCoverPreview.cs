using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Font = System.Drawing.Font;

namespace C3DProTools
{
    /// <summary>
    /// Profile graph of the route (ground, cover limits, current pipes, proposed pipes) with a per-pipe table.
    /// With <c>canApply</c> it is the optimiser preview (OK = apply); otherwise a read-only cover report.
    /// The failed pipe names can be copied, and the whole table exported as CSV.
    /// </summary>
    internal sealed class FormCoverPreview : Form
    {
        private readonly CoverRoute _route;
        private readonly CoverOptions _o;
        private readonly List<CoverRow> _existing;
        private readonly List<CoverRow> _proposed;
        private readonly List<CoverRow> _table;
        private readonly Panel pnlGraph = new Panel();

        public FormCoverPreview(string title, string summary, CoverRoute route, CoverOptions options,
            List<CoverRow> existing, List<CoverRow> proposed, bool canApply, string applyText = "Apply Changes")
        {
            _route = route;
            _o = options;
            // Callers may have no rows on one side (or none at all); a missing list or row is shown as empty, never dereferenced.
            _existing = existing?.FindAll(r => r?.Pipe != null);
            _proposed = proposed?.FindAll(r => r?.Pipe != null);
            _table = _proposed ?? _existing ?? new List<CoverRow>();

            ListView lvRows = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
            lvRows.Columns.Add("#", 35);
            lvRows.Columns.Add("Pipe", 200);
            lvRows.Columns.Add("Station From", 90, HorizontalAlignment.Right);
            lvRows.Columns.Add("Station To", 90, HorizontalAlignment.Right);
            lvRows.Columns.Add("Length (m)", 80, HorizontalAlignment.Right);
            lvRows.Columns.Add("Slope (%)", 75, HorizontalAlignment.Right);
            lvRows.Columns.Add("Min Cover (m)", 90, HorizontalAlignment.Right);
            lvRows.Columns.Add("Max Cover (m)", 90, HorizontalAlignment.Right);
            lvRows.Columns.Add("Status", 260);
            for (int i = 0; i < _table.Count; i++)
            {
                CoverRow row = _table[i];
                ProfilePipe p = row.Pipe;
                lvRows.Items.Add(new ListViewItem(new[]
                {
                    (i + 1).ToString(), p.Name, p.S0.ToString("0.00"), p.S1.ToString("0.00"), p.Length.ToString("0.00"),
                    (p.Slope * 100).ToString("0.000"), row.MinCover.ToString("0.000"), row.MaxCover.ToString("0.000"), row.Status
                }) { ForeColor = row.Failed ? Color.Firebrick : SystemColors.WindowText });
            }

            Label lblSummary = new Label { Text = summary, Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 6, 8, 0) };

            pnlGraph.Dock = DockStyle.Top;
            pnlGraph.Height = 320;
            pnlGraph.BackColor = Color.White;
            pnlGraph.Paint += PnlGraph_Paint;
            pnlGraph.Resize += (s, e) => pnlGraph.Invalidate();

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button btnClose = new Button { Text = canApply ? "Cancel" : "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
            buttons.Controls.Add(btnClose);
            if (canApply)
            {
                Button btnApply = new Button { Text = applyText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(130, 30) };
                btnApply.Font = new Font(btnApply.Font, FontStyle.Bold);
                buttons.Controls.Add(btnApply);
                AcceptButton = btnApply;
            }
            Button btnExport = new Button { Text = "Export CSV...", Size = new Size(110, 30) };
            btnExport.Click += (s, e) => ExportCsv();
            buttons.Controls.Add(btnExport);
            Button btnCopy = new Button { Text = "Copy Failed Pipe Names", Size = new Size(170, 30) };
            btnCopy.Click += (s, e) => CopyFailed();
            buttons.Controls.Add(btnCopy);
            CancelButton = btnClose;

            // Docking is applied from the last control added, so the filling list goes in first.
            Controls.Add(lvRows);
            Controls.Add(lblSummary);
            Controls.Add(pnlGraph);
            Controls.Add(buttons);

            Text = title;
            ClientSize = new Size(1120, 720);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
        }

        private void CopyFailed()
        {
            StringBuilder sb = new StringBuilder();
            foreach (CoverRow row in _table) if (row.Failed) sb.AppendLine(row.Pipe.Name);
            if (sb.Length == 0)
            {
                MessageBox.Show(this, "No pipe fails the limits.", "Cover Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Clipboard.SetText(sb.ToString());
            MessageBox.Show(this, "The failed pipe names were copied to the clipboard.", "Cover Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportCsv()
        {
            using (SaveFileDialog dlg = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv|Text file (*.txt)|*.txt", FileName = "CoverReport.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, CoverEvaluation.ToCsv(_table));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not write the file:\n{ex.Message}", "Cover Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void PnlGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_route?.Records == null || _route.Records.Count == 0 || _o == null) return;

            // Everything is drawn at the cover reference level (e.g. pipe crown), so pipes compare directly with the limit lines.
            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (RouteRecord rec in _route.Records)
            {
                yMax = Math.Max(yMax, rec.Ground);
                yMin = Math.Min(yMin, rec.Ground - _o.MaxCover);
            }
            foreach (List<CoverRow> rows in new[] { _existing, _proposed })
            {
                if (rows == null) continue;
                foreach (CoverRow row in rows)
                {
                    double off = Offset(row.Pipe);
                    yMin = Math.Min(yMin, Math.Min(row.Pipe.Z0, row.Pipe.Z1) + off);
                    yMax = Math.Max(yMax, Math.Max(row.Pipe.Z0, row.Pipe.Z1) + off);
                }
            }
            double pad = Math.Max(0.5, (yMax - yMin) * 0.08);
            yMin -= pad; yMax += pad;

            Rectangle area = new Rectangle(70, 28, Math.Max(10, pnlGraph.Width - 90), Math.Max(10, pnlGraph.Height - 58));
            double sMax = Math.Max(1e-6, _route.Length);
            PointF P(double s, double z) => new PointF(
                (float)(area.Left + s / sMax * area.Width),
                (float)(area.Bottom - (z - yMin) / (yMax - yMin) * area.Height));

            using (Pen frame = new Pen(Color.Gainsboro))
            using (Font font = new Font("Segoe UI", 8f))
            using (Brush text = new SolidBrush(Color.DimGray))
            {
                g.DrawRectangle(frame, area);
                g.DrawString($"{yMax:0.00}", font, text, 4, area.Top - 6);
                g.DrawString($"{yMin:0.00}", font, text, 4, area.Bottom - 8);
                g.DrawString("0+000", font, text, area.Left - 10, area.Bottom + 4);
                g.DrawString($"{_route.Length:0.00} m", font, text, area.Right - 50, area.Bottom + 4);

                // Manholes on the route.
                using (Pen manhole = new Pen(Color.LightSlateGray) { DashStyle = DashStyle.Dash })
                {
                    foreach (RouteSection sec in _route.Sections)
                        if (sec.AfterManhole) g.DrawLine(manhole, P(sec.S0, yMin), P(sec.S0, yMax));
                }

                DrawLimitLine(g, P, _o.MinCover, Color.ForestGreen);
                DrawLimitLine(g, P, _o.MaxCover, Color.DarkOrange);

                using (Pen ground = new Pen(Color.SaddleBrown, 2f))
                {
                    foreach (RouteSection sec in _route.Sections)
                    {
                        List<PointF> pts = new List<PointF>();
                        for (int r = sec.FirstRecord; r <= sec.LastRecord; r++) pts.Add(P(_route.Records[r].S, _route.Records[r].Ground));
                        if (pts.Count > 1) g.DrawLines(ground, pts.ToArray());
                    }
                }

                DrawPipes(g, P, _existing, _proposed == null, Color.Gray, _proposed == null ? 2.5f : 1.5f);
                if (_proposed != null) DrawPipes(g, P, _proposed, true, Color.RoyalBlue, 2.5f);

                // Legend.
                float x = area.Left + 6;
                x = Legend(g, font, x, area.Top - 20, Color.SaddleBrown, "Ground");
                x = Legend(g, font, x, area.Top - 20, Color.ForestGreen, $"Min cover {_o.MinCover:0.00} m");
                x = Legend(g, font, x, area.Top - 20, Color.DarkOrange, $"Max cover {_o.MaxCover:0.00} m");
                if (_existing != null) x = Legend(g, font, x, area.Top - 20, Color.Gray, "Current pipes");
                if (_proposed != null) x = Legend(g, font, x, area.Top - 20, Color.RoyalBlue, "Proposed pipes");
                Legend(g, font, x, area.Top - 20, Color.Firebrick, "Failing");
            }
        }

        // A proposed pipe with no source pipe to take its size from is drawn at its centreline.
        private double Offset(ProfilePipe p) => p.Source == null ? 0 : CoverRoute.ReferenceOffset(_o.Reference, p.Source.InnerHeight, p.Source.OuterHeight);

        private void DrawLimitLine(Graphics g, Func<double, double, PointF> P, double cover, Color color)
        {
            using (Pen pen = new Pen(color, 1.2f) { DashStyle = DashStyle.Dash })
            {
                foreach (RouteSection sec in _route.Sections)
                {
                    List<PointF> pts = new List<PointF>();
                    for (int r = sec.FirstRecord; r <= sec.LastRecord; r++) pts.Add(P(_route.Records[r].S, _route.Records[r].Ground - cover));
                    if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                }
            }
        }

        private void DrawPipes(Graphics g, Func<double, double, PointF> P, List<CoverRow> rows, bool showFailures, Color color, float width)
        {
            if (rows == null) return;
            foreach (CoverRow row in rows)
            {
                double off = Offset(row.Pipe);
                using (Pen pen = new Pen(showFailures && row.Failed ? Color.Firebrick : color, width))
                {
                    PointF a = P(row.Pipe.S0, row.Pipe.Z0 + off), b = P(row.Pipe.S1, row.Pipe.Z1 + off);
                    g.DrawLine(pen, a, b);
                    if (showFailures) g.FillEllipse(Brushes.Black, a.X - 2, a.Y - 2, 4, 4);
                }
            }
        }

        private static float Legend(Graphics g, Font font, float x, float y, Color color, string label)
        {
            using (Pen pen = new Pen(color, 2.5f)) g.DrawLine(pen, x, y + 7, x + 18, y + 7);
            g.DrawString(label, font, Brushes.Black, x + 22, y);
            return x + 30 + g.MeasureString(label, font).Width;
        }
    }
}
