using Autodesk.AutoCAD.EditorInput;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace C3DProTools
{
    /// <summary>
    /// Lists gaps and level mismatches between pipe ends and zooms the drawing to each one.
    /// OK = ignore the issues and continue, Cancel = stop so the user can fix them.
    /// </summary>
    internal sealed class FormPipeEndIssues : Form
    {
        // Smallest zoom window around an issue (drawing units), so a 2 mm gap still shows its surroundings.
        private const double MinZoomSize = 10.0;

        private readonly List<PipeEndIssue> _issues;
        private readonly ListView lvIssues = new ListView();

        public FormPipeEndIssues(List<PipeEndIssue> issues)
        {
            _issues = issues;

            int gaps = issues.FindAll(i => i.Kind == PipeEndIssueKind.Gap).Count;
            Label lblSummary = new Label
            {
                Text = $"Found {gaps} gap(s) under {PipeFlowAnalysis.GapLimit:0.##} m and {issues.Count - gaps} level mismatch(es) between pipe ends.\n" +
                       "Select a row and Zoom To it (or double-click) to check it in the drawing.",
                Location = new Point(10, 10),
                Size = new Size(640, 34)
            };

            lvIssues.View = View.Details;
            lvIssues.FullRowSelect = true;
            lvIssues.MultiSelect = false;
            lvIssues.HideSelection = false;
            lvIssues.Location = new Point(10, 50);
            lvIssues.Size = new Size(640, 260);
            lvIssues.Columns.Add("#", 35);
            lvIssues.Columns.Add("Issue", 105);
            lvIssues.Columns.Add("Pipe A", 165);
            lvIssues.Columns.Add("Pipe B", 165);
            lvIssues.Columns.Add("Gap (m)", 75, HorizontalAlignment.Right);
            lvIssues.Columns.Add("ΔInvert (m)", 85, HorizontalAlignment.Right);
            for (int i = 0; i < issues.Count; i++)
            {
                PipeEndIssue issue = issues[i];
                lvIssues.Items.Add(new ListViewItem(new[]
                {
                    (i + 1).ToString(),
                    issue.Kind == PipeEndIssueKind.Gap ? "Gap" : "Level mismatch",
                    issue.LabelA,
                    issue.LabelB,
                    issue.PlanDistance.ToString("0.000"),
                    issue.InvertDifference.ToString("0.000")
                }) { Tag = issue });
            }
            if (lvIssues.Items.Count > 0) lvIssues.Items[0].Selected = true;
            lvIssues.DoubleClick += (s, e) => ZoomToSelected();

            Button btnZoom = new Button { Text = "Zoom To", Location = new Point(10, 320), Size = new Size(110, 30) };
            btnZoom.Click += (s, e) => ZoomToSelected();

            Button btnStop = new Button { Text = "Stop && Fix", DialogResult = DialogResult.Cancel, Location = new Point(400, 320), Size = new Size(120, 30) };
            Button btnContinue = new Button { Text = "Ignore && Continue", DialogResult = DialogResult.OK, Location = new Point(530, 320), Size = new Size(120, 30) };

            Controls.AddRange(new Control[] { lblSummary, lvIssues, btnZoom, btnStop, btnContinue });
            AcceptButton = btnZoom;
            CancelButton = btnStop;

            Text = "Pipe Connectivity Issues";
            ClientSize = new Size(660, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            // Keep the dialog in a corner so the zoomed area of the drawing stays visible.
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Top + 80);
        }

        /// <summary>Writes the issue list to the command line so it can be worked through after the dialog closes.</summary>
        public static void WriteToCommandLine(Editor ed, List<PipeEndIssue> issues)
        {
            ed.WriteMessage($"\n--- Pipe connectivity issues ({issues.Count}) ---");
            for (int i = 0; i < issues.Count; i++)
            {
                PipeEndIssue issue = issues[i];
                string kind = issue.Kind == PipeEndIssueKind.Gap ? "Gap" : "Level mismatch";
                ed.WriteMessage($"\n{i + 1}. {kind}: {issue.LabelA} / {issue.LabelB}  gap {issue.PlanDistance:0.000} m, ΔInvert {issue.InvertDifference:0.000} m  at ({issue.Location.X:0.###}, {issue.Location.Y:0.###})");
            }
        }

        private void ZoomToSelected()
        {
            if (lvIssues.SelectedItems.Count == 0) return;
            PipeEndIssue issue = (PipeEndIssue)lvIssues.SelectedItems[0].Tag;
            ViewHelper.ZoomTo(issue.Location, Math.Max(MinZoomSize, issue.PlanDistance * 10.0));
        }
    }
}
