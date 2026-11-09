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
    /// Modal dialog displayed when unconnected encasement ends match more than one nearby structure candidate.
    /// Lets the user choose the target structure for each ambiguous end before re-running preview.
    /// </summary>
    internal sealed class FormConduitAmbiguousChoice : Form
    {
        public sealed class AmbiguousItem
        {
            public DrawingObjectRef Encasement { get; }
            public string EncasementName { get; }
            public PipeEnd End { get; }
            public IReadOnlyList<StructureSnapshot> Candidates { get; }

            public AmbiguousItem(DrawingObjectRef encasement, string name, PipeEnd end, IReadOnlyList<StructureSnapshot> candidates)
            {
                Encasement = encasement;
                EncasementName = name;
                End = end;
                Candidates = candidates;
            }
        }

        private readonly List<AmbiguousItem> _items;
        private readonly List<ComboBox> _pickers = new List<ComboBox>();

        public List<StructureChoice> Choices { get; } = new List<StructureChoice>();

        public FormConduitAmbiguousChoice(IEnumerable<AmbiguousItem> items)
        {
            _items = (items ?? Enumerable.Empty<AmbiguousItem>()).ToList();

            Text = "Network Editor - Pick Manholes for Unconnected Ends";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(680, 480);
            MinimumSize = new Size(580, 360);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;

            Panel head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 12, 16, 10), BackColor = StyleUi.Panel };
            Label lblTitle = new Label { Text = "Ambiguous Manhole Matches", Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading };
            Label lblIntro = new Label
            {
                Text = "More than one structure was found within the search distance for each encasement end below. Select which structure each end connects to, then preview again.",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = StyleUi.Muted,
                Padding = new Padding(0, 4, 0, 0),
                Tag = "note"
            };
            head.Controls.Add(lblIntro);
            head.Controls.Add(lblTitle);
            head.Resize += (s, e) => lblIntro.MaximumSize = new Size(Math.Max(200, head.ClientSize.Width - 32), 0);

            Panel scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 10, 16, 10), BackColor = StyleUi.Surface };
            TableLayoutPanel stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Margin = new Padding(0)
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scrollHost.Controls.Add(stack);

            foreach (AmbiguousItem item in _items)
            {
                string endText = item.End == PipeEnd.Start ? "Start" : "End";
                TableLayoutPanel card = ToolUi.Card($"{item.EncasementName} ({endText} end)",
                    $"{item.Candidates.Count} candidate structures found within the search distance. Select the target structure:");

                ComboBox cb = ToolUi.Combo(400);
                foreach (StructureSnapshot s in item.Candidates)
                {
                    string shape = s.Round ? "Round" : $"{ToolUi.Format(s.InnerLengthM)}×{ToolUi.Format(s.InnerWidthM)} m";
                    cb.Items.Add($"{s.Name} ({s.PartSizeName}, {shape}) at ({ToolUi.Format(s.Location.X)}, {ToolUi.Format(s.Location.Y)})");
                }
                if (cb.Items.Count > 0) cb.SelectedIndex = 0;

                _pickers.Add(cb);
                ToolUi.Field(card, "Structure", cb);
                ToolUi.Add(stack, card);
            }

            Button btnOk = StyleUi.Primary("Apply choices", (s, e) =>
            {
                Choices.Clear();
                for (int i = 0; i < _items.Count; i++)
                {
                    int sel = _pickers[i].SelectedIndex;
                    if (sel >= 0 && sel < _items[i].Candidates.Count)
                    {
                        StructureSnapshot chosen = _items[i].Candidates[sel];
                        Choices.Add(new StructureChoice(_items[i].Encasement, _items[i].End, chosen.Structure));
                    }
                }
                DialogResult = DialogResult.OK;
            });

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

            Controls.Add(scrollHost);
            Controls.Add(head);
            Controls.Add(bottom);

            void UpdateWrapping()
            {
                ToolUi.FitNotes(head, ClientSize.Width);
                ToolUi.FitNotes(stack, ClientSize.Width - 40);
            }
            Resize += (s, e) => UpdateWrapping();
            Shown += (s, e) => UpdateWrapping();
        }
    }
}
