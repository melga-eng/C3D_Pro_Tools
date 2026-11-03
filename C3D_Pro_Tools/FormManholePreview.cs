using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using FlowDirection = System.Windows.Forms.FlowDirection;
using Font = System.Drawing.Font;

namespace C3DProTools
{
    /// <summary>One pipe's planned Mode 3 change: its new ends and which loose ends get attached to a manhole.</summary>
    internal sealed class ManholeAdjustment
    {
        public ObjectId PipeId;
        public string Name;
        public ObjectId StartStructure, EndStructure;
        public string StartStructureName = "", EndStructureName = "";
        /// <summary>True when that end is not yet connected and will be attached to the manhole it sits inside.</summary>
        public bool ConnectStart, ConnectEnd;
        public Point3d OrigStart, OrigEnd, NewStart, NewEnd;
        /// <summary>False when nothing can be done for the pipe (see <see cref="Note"/>).</summary>
        public bool Actionable;
        public string Note = "";

        public static double PlanLength(Point3d a, Point3d b) => new Point2d(a.X, a.Y).GetDistanceTo(new Point2d(b.X, b.Y));
        /// <summary>Plan distance an end moves: positive when the pipe gets longer at that end, negative when it is trimmed.</summary>
        public double StartChange => Signed(OrigStart, NewStart, OrigEnd);
        public double EndChange => Signed(OrigEnd, NewEnd, OrigStart);

        private static double Signed(Point3d from, Point3d to, Point3d otherEnd)
        {
            double moved = PlanLength(from, to);
            return PlanLength(to, otherEnd) >= PlanLength(from, otherEnd) ? moved : -moved;
        }
    }

    /// <summary>
    /// Mode 3 preview: every mapped pipe with how far each end moves (+ longer / - trimmed), its new length and slope.
    /// Ticked rows are applied on OK; pipes that cannot be adjusted are listed unticked with the reason.
    /// </summary>
    internal sealed class FormManholePreview : Form
    {
        private readonly List<ManholeAdjustment> _plan;
        private readonly ListView lvRows = new ListView();
        private readonly TransientMarkers _markers = new TransientMarkers();

        public List<ManholeAdjustment> Chosen { get; } = new List<ManholeAdjustment>();

        /// <param name="what">What the ends are fitted to, e.g. "Fit to the inner wall face".</param>
        public FormManholePreview(List<ManholeAdjustment> plan, string what)
        {
            _plan = plan;
            int actionable = plan.FindAll(a => a.Actionable).Count;
            int connects = 0;
            foreach (ManholeAdjustment a in plan) connects += (a.ConnectStart ? 1 : 0) + (a.ConnectEnd ? 1 : 0);

            Label lblSummary = new Label
            {
                Text = $"{what}: {actionable} of {plan.Count} pipe(s) can be adjusted, " +
                       $"{connects} loose end(s) will be connected. Slopes are kept.\n" +
                       "Moves: + = pipe gets longer at that end, - = trimmed. Untick rows to leave pipes unchanged. Double-click a row to zoom to it.",
                Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 6, 8, 0)
            };

            lvRows.View = View.Details;
            lvRows.CheckBoxes = true;
            lvRows.FullRowSelect = true;
            lvRows.MultiSelect = false;
            lvRows.HideSelection = false;
            lvRows.Dock = DockStyle.Fill;
            lvRows.Columns.Add("Pipe", 150);
            lvRows.Columns.Add("Start Manhole", 120);
            lvRows.Columns.Add("Start Move (m)", 95, HorizontalAlignment.Right);
            lvRows.Columns.Add("End Manhole", 120);
            lvRows.Columns.Add("End Move (m)", 95, HorizontalAlignment.Right);
            lvRows.Columns.Add("Length (m)", 130, HorizontalAlignment.Right);
            lvRows.Columns.Add("Slope (%)", 70, HorizontalAlignment.Right);
            lvRows.Columns.Add("Action", 190);
            foreach (ManholeAdjustment a in plan)
            {
                double before = ManholeAdjustment.PlanLength(a.OrigStart, a.OrigEnd);
                double after = ManholeAdjustment.PlanLength(a.NewStart, a.NewEnd);
                double slope = before > 1e-9 ? (a.OrigEnd.Z - a.OrigStart.Z) / before * 100.0 : 0.0;
                lvRows.Items.Add(new ListViewItem(new[]
                {
                    a.Name,
                    a.StartStructureName + (a.ConnectStart ? " (connect)" : ""),
                    a.Actionable && !a.StartStructure.IsNull ? a.StartChange.ToString("+0.000;-0.000;0.000") : "",
                    a.EndStructureName + (a.ConnectEnd ? " (connect)" : ""),
                    a.Actionable && !a.EndStructure.IsNull ? a.EndChange.ToString("+0.000;-0.000;0.000") : "",
                    a.Actionable ? $"{before:0.000} -> {after:0.000}" : before.ToString("0.000"),
                    slope.ToString("0.000"),
                    a.Note
                })
                {
                    Checked = a.Actionable && a.Note != "Already in place",
                    ForeColor = a.Actionable ? SystemColors.WindowText : SystemColors.GrayText,
                    Tag = a
                });
            }
            // Rows that cannot be adjusted stay unticked.
            lvRows.ItemCheck += (s, e) => { if (!((ManholeAdjustment)lvRows.Items[e.Index].Tag).Actionable) e.NewValue = CheckState.Unchecked; };
            lvRows.DoubleClick += (s, e) => ZoomToSelected();

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
            Button btnApply = new Button { Text = "Apply Ticked", Size = new Size(120, 30) };
            btnApply.Font = new Font(btnApply.Font, FontStyle.Bold);
            btnApply.Click += (s, e) =>
            {
                foreach (ListViewItem item in lvRows.Items) if (item.Checked) Chosen.Add((ManholeAdjustment)item.Tag);
                DialogResult = DialogResult.OK;
            };
            Button btnZoom = new Button { Text = "Zoom To", Size = new Size(90, 30) };
            btnZoom.Click += (s, e) => ZoomToSelected();
            Button btnCopy = new Button { Text = "Copy List", Size = new Size(90, 30) };
            btnCopy.Click += (s, e) => CopyList();
            buttons.Controls.AddRange(new Control[] { btnCancel, btnApply, btnZoom, btnCopy });
            AcceptButton = btnApply;
            CancelButton = btnCancel;

            // Docking is applied from the last control added, so the filling list goes in first.
            Controls.Add(lvRows);
            Controls.Add(lblSummary);
            Controls.Add(buttons);

            Text = "Network Editor - Fit Pipe Ends Preview";
            ClientSize = new Size(1010, 520);
            MinimumSize = new Size(700, 320);
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
            FormClosed += (s, e) => _markers.Dispose();
        }

        private void ZoomToSelected()
        {
            if (lvRows.SelectedItems.Count == 0) return;
            ManholeAdjustment a = (ManholeAdjustment)lvRows.SelectedItems[0].Tag;

            // Current pipe in grey, adjusted pipe in green.
            _markers.Clear();
            _markers.AddPath(new[] { a.OrigStart, a.OrigEnd }, 8, 0.3);
            if (a.Actionable) _markers.AddPath(new[] { a.NewStart, a.NewEnd }, 3, 0.15);

            Extents3d ext = new Extents3d(a.OrigStart, a.OrigStart);
            foreach (Point3d p in new[] { a.OrigEnd, a.NewStart, a.NewEnd }) ext.AddPoint(p);
            ViewHelper.ZoomTo(ext, 1.4, 15.0);
        }

        private void CopyList()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Pipe\tStart Manhole\tStart Move (m)\tEnd Manhole\tEnd Move (m)\tLength (m)\tSlope (%)\tAction");
            foreach (ListViewItem item in lvRows.Items)
            {
                List<string> cells = new List<string>();
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems) cells.Add(sub.Text);
                sb.AppendLine(string.Join("\t", cells));
            }
            Clipboard.SetText(sb.ToString());
        }
    }
}
