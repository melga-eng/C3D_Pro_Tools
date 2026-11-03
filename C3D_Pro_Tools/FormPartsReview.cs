using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Font = System.Drawing.Font;

namespace C3DProTools
{
    /// <summary>
    /// Lists the parts detected on one alignment so the user can check them (they are highlighted in plan),
    /// untick any to leave out, and zoom to each. OK = continue to the profile views, Ignore = skip this alignment,
    /// Cancel = stop the whole run.
    /// </summary>
    internal sealed class FormPartsReview : Form
    {
        private readonly List<DetectedPart> _parts;
        private readonly PartHighlighter _highlighter;
        private readonly ListView lvParts = new ListView();
        private readonly CheckBox chkAllViews = new CheckBox();

        /// <summary>True when the user chose to add the parts to every profile view of the alignment instead of picking.</summary>
        public bool UseAllProfileViews => chkAllViews.Checked;

        public FormPartsReview(string alignmentName, int alignmentIndex, int alignmentCount, int profileViewCount,
            List<DetectedPart> parts, PartHighlighter highlighter)
        {
            _parts = parts;
            _highlighter = highlighter;

            int along = 0, partial = 0, crossing = 0;
            foreach (DetectedPart p in parts)
            {
                if (p.Category == ProfilePartCategory.Along) along++;
                else if (p.Category == ProfilePartCategory.Partial) partial++;
                else crossing++;
            }

            Label lblSummary = new Label
            {
                Text = $"Alignment \"{alignmentName}\" ({alignmentIndex} of {alignmentCount}): {along} along, {partial} partial, {crossing} crossing.\n" +
                       "Highlighted in plan: green = along, orange = partial, yellow = crossing, grey = left out.\n" +
                       "Ticked parts will be drawn. Partial parts start unticked: tick the ones you want.",
                Location = new Point(10, 10),
                Size = new Size(760, 48)
            };

            lvParts.View = View.Details;
            lvParts.CheckBoxes = true;
            lvParts.FullRowSelect = true;
            lvParts.MultiSelect = false;
            lvParts.HideSelection = false;
            lvParts.Location = new Point(10, 62);
            lvParts.Size = new Size(760, 300);
            lvParts.Columns.Add("Part", 150);
            lvParts.Columns.Add("Type", 95);
            lvParts.Columns.Add("Network", 140);
            lvParts.Columns.Add("Detection", 75);
            lvParts.Columns.Add("Station", 150);
            lvParts.Columns.Add("Max Offset (m)", 90, HorizontalAlignment.Right);
            lvParts.Columns.Add("Already In", 55, HorizontalAlignment.Right);
            foreach (DetectedPart p in parts)
            {
                lvParts.Items.Add(new ListViewItem(new[]
                {
                    p.Name,
                    p.Kind,
                    p.NetworkName,
                    p.Category.ToString(),
                    p.StationText,
                    p.Category == ProfilePartCategory.Crossing ? "-" : p.MaxOffset.ToString("0.000"),
                    $"{p.AlreadyShownIn}/{profileViewCount}"
                }) { Checked = p.Include });
            }
            // Attached after the rows are filled, so the initial Checked values don't count as user changes.
            lvParts.ItemChecked += LvParts_ItemChecked;
            lvParts.DoubleClick += (s, e) => ZoomToSelected();

            Button btnZoom = new Button { Text = "Zoom To", Location = new Point(10, 370), Size = new Size(90, 28) };
            btnZoom.Click += (s, e) => ZoomToSelected();
            Button btnZoomAll = new Button { Text = "Zoom All", Location = new Point(105, 370), Size = new Size(90, 28) };
            btnZoomAll.Click += (s, e) => ZoomToParts(_parts);
            Button btnTickAll = new Button { Text = "Tick All", Location = new Point(200, 370), Size = new Size(90, 28) };
            btnTickAll.Click += (s, e) => SetAllChecked(true);
            Button btnUntickAll = new Button { Text = "Untick All", Location = new Point(295, 370), Size = new Size(90, 28) };
            btnUntickAll.Click += (s, e) => SetAllChecked(false);

            chkAllViews.Text = $"Add to all {profileViewCount} profile view(s) of this alignment (don't pick them)";
            chkAllViews.Location = new Point(10, 410);
            chkAllViews.AutoSize = true;

            Button btnCancel = new Button { Text = "Cancel Run", DialogResult = DialogResult.Cancel, Location = new Point(440, 440), Size = new Size(100, 32) };
            Button btnSkip = new Button { Text = "Skip Alignment", DialogResult = DialogResult.Ignore, Location = new Point(545, 440), Size = new Size(110, 32) };
            Button btnContinue = new Button { Text = "Continue >", DialogResult = DialogResult.OK, Location = new Point(660, 440), Size = new Size(110, 32) };
            btnContinue.Font = new Font(btnContinue.Font, FontStyle.Bold);

            Controls.AddRange(new Control[] { lblSummary, lvParts, btnZoom, btnZoomAll, btnTickAll, btnUntickAll, chkAllViews, btnCancel, btnSkip, btnContinue });
            AcceptButton = btnContinue;
            CancelButton = btnCancel;

            Text = "Parts to Profile - Review Detected Parts";
            ClientSize = new Size(780, 482);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            // Keep the dialog in a corner so the highlighted parts stay visible.
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
        }

        private void LvParts_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            int index = e.Item.Index;
            _parts[index].Include = e.Item.Checked;
            _highlighter.Refresh(index);
        }

        private void SetAllChecked(bool value)
        {
            lvParts.BeginUpdate();
            foreach (ListViewItem item in lvParts.Items) item.Checked = value;
            lvParts.EndUpdate();
        }

        private void ZoomToSelected()
        {
            if (lvParts.SelectedItems.Count == 0) return;
            ZoomToParts(new[] { _parts[lvParts.SelectedItems[0].Index] });
        }

        private static void ZoomToParts(IEnumerable<DetectedPart> parts)
        {
            // A default Extents3d already contains the origin, so start from the first point.
            Extents3d? extents = null;
            foreach (DetectedPart part in parts)
            {
                foreach (Point3d p in part.Path)
                {
                    Extents3d e = extents ?? new Extents3d(p, p);
                    e.AddPoint(p);
                    extents = e;
                }
            }
            if (extents.HasValue) ViewHelper.ZoomTo(extents.Value, 1.5, 15.0);
        }
    }
}
