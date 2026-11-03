using Autodesk.AutoCAD.DatabaseServices;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace C3DProTools
{
    /// <summary>
    /// Asks which direction the pipes should follow when some of them point against the rest of their run.
    /// <see cref="PipesToReverse"/> holds the pipes the chosen option reverses.
    /// </summary>
    internal sealed class FormFlowDirectionChoice : Form
    {
        private readonly RadioButton rbMajority = new RadioButton();
        private readonly RadioButton rbOpposite = new RadioButton();
        private readonly RadioButton rbDownhill = new RadioButton();
        private readonly List<ObjectId> _majorityFlips = new List<ObjectId>();
        private readonly List<ObjectId> _oppositeFlips = new List<ObjectId>();
        private readonly List<ObjectId> _downhillFlips = new List<ObjectId>();

        public List<ObjectId> PipesToReverse =>
            rbOpposite.Checked ? _oppositeFlips : rbDownhill.Checked ? _downhillFlips : _majorityFlips;

        public FormFlowDirectionChoice(List<FlowRun> runs)
        {
            int mixedRuns = 0, opposing = 0, total = 0;
            foreach (FlowRun run in runs)
            {
                _majorityFlips.AddRange(run.Minority);
                _oppositeFlips.AddRange(run.Majority);
                _downhillFlips.AddRange(run.Uphill);
                total += run.WithSeed.Count + run.AgainstSeed.Count;
                if (run.IsMixed) { mixedRuns++; opposing += run.Minority.Count; }
            }

            Label lblSummary = new Label
            {
                Text = $"The pipes are connected, but {opposing} of {total} pipe(s) point against the rest of their run " +
                       $"({mixedRuns} of {runs.Count} run(s) are mixed).\n\nWhich direction should all pipes follow?",
                Location = new Point(12, 12),
                Size = new Size(470, 60)
            };

            rbMajority.Text = $"Current majority direction  (reverses {_majorityFlips.Count} pipe(s))";
            rbMajority.Location = new Point(20, 80); rbMajority.AutoSize = true; rbMajority.Checked = true;
            rbOpposite.Text = $"Opposite of the majority direction  (reverses {_oppositeFlips.Count} pipe(s))";
            rbOpposite.Location = new Point(20, 105); rbOpposite.AutoSize = true;
            rbDownhill.Text = $"Downhill, from the higher to the lower invert  (reverses {_downhillFlips.Count} pipe(s))";
            rbDownhill.Location = new Point(20, 130); rbDownhill.AutoSize = true;

            Label lblNote = new Label
            {
                Text = "Runs are split at junctions where three or more pipes meet.",
                ForeColor = SystemColors.GrayText,
                Location = new Point(12, 160),
                AutoSize = true
            };

            Button btnOk = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(290, 190), Size = new Size(90, 30) };
            Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(390, 190), Size = new Size(90, 30) };

            Controls.AddRange(new Control[] { lblSummary, rbMajority, rbOpposite, rbDownhill, lblNote, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Text = "Choose Flow Direction";
            ClientSize = new Size(494, 232);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
        }
    }
}
