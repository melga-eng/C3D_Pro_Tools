using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using DomainType = Autodesk.Civil.DatabaseServices.DomainType;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Asked when Navisworks Import opens: how to make sizes the catalog does not have.
    /// Method A adds C3D Pro any-size families (every size exact); Method B adds the missing sizes to the stock families;
    /// or neither (nearest size, exact solid when it is too far off).
    /// </summary>
    internal sealed class FormNavisSizeMode : Form
    {
        private readonly RadioButton _rbA = Radio("Method A — C3D Pro any-size families (recommended)");
        private readonly RadioButton _rbB = Radio("Method B — Add the missing sizes to the catalog's own families");
        private readonly RadioButton _rbNone = Radio("Neither — use the nearest catalog size");
        private readonly CheckBox _chkAsk = new CheckBox { AutoSize = true, Text = "Ask me each time Navisworks Import opens", Margin = new Padding(4, 8, 3, 3) };
        // The options grow to their content; the host around them scrolls only when the window is shorter than that.
        private readonly TableLayoutPanel _options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14, 12, 14, 4) };
        private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        public CatalogMode Mode { get; private set; }
        public bool AskAgain => _chkAsk.Checked;

        public FormNavisSizeMode(CatalogMode current, bool askAgain)
        {
            Text = "Navisworks Import - Missing sizes";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MaximizeBox = false;

            NavisCatalogContext ctx = NavisCatalogContext.Resolve();
            string pipeCat = ctx.Pipe?.Name ?? "unknown", structCat = ctx.Structure?.Name ?? "unknown";
            bool writable = NavisCatalogContext.Writable(ctx.Pipe) && NavisCatalogContext.Writable(ctx.Structure);
            bool anyInstalled = NavisCatalogEditor.AnySize.All(f => (f.Pipe ? ctx.Pipe : ctx.Structure)?.PartIds.Contains(f.Guid) == true);
            bool anyLoaded = SafeLoaded();

            // Header: title and the catalogs this drawing uses.
            TableLayoutPanel head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = StyleUi.Panel, Padding = new Padding(18, 14, 18, 12) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.Controls.Add(new Label { AutoSize = true, Font = StyleUi.Title, ForeColor = StyleUi.Text, Text = "How should missing sizes be made?", Margin = new Padding(0) });
            Label intro = Wrap("Civil 3D catalogs only hold fixed sizes, so many Navisworks pipes, ducts and manholes have no exact match. " +
                               $"This drawing uses the \"{pipeCat}\" pipe catalog and the \"{structCat}\" structure catalog" +
                               (writable ? "." : " — its folder is read-only, so methods A and B cannot change it."), StyleUi.Muted);
            intro.Margin = new Padding(0, 6, 0, 0);
            head.Controls.Add(intro);

            _options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddOption(_rbA,
                "Adds four C3D Pro families (box culvert, circular pipe, rectangular and cylindrical structure) whose sizes are free, " +
                "so every import gets its exact width, height, diameter and wall. Autodesk's own families are not touched, and everything imported is easy to recognise.",
                anyLoaded ? "Installed and loaded." : anyInstalled ? "Installed — restart Civil 3D once to load them." : "Set up once; Civil 3D then needs one restart to list them.");
            // Method B writes into Autodesk's shared catalog files: not offered in the public edition.
            if (!Edition.IsPublic)
                AddOption(_rbB,
                    "Writes each missing size (for example 800 × 1000 wall 0) as a new row or list value into the stock family it belongs to " +
                    "(Concrete Box Culvert, Rectangular Junction Structure…). Works at once, without a restart.",
                    "Changes the shared catalog for every drawing that uses it; a backup of each file is kept next to it.");
            AddOption(_rbNone,
                "Nothing in the catalog changes. Rows use the nearest catalog size, or come in as exact 3D solids when that size is too far off " +
                "(the limit is in Options). You can still choose any part yourself.", "");
            _options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _options.Controls.Add(_chkAsk);

            Button ok = StyleUi.Primary("Continue", (s, e) =>
            {
                Mode = _rbA.Checked ? CatalogMode.AnySize : _rbB.Checked ? CatalogMode.Extend : CatalogMode.None;
                DialogResult = DialogResult.OK;
            });
            ok.MinimumSize = new Size(110, 32);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(14, 8, 14, 12), BackColor = StyleUi.Surface };
            buttons.Controls.Add(ok);
            AcceptButton = ok;

            _scroll.Controls.Add(_options);
            Controls.Add(_scroll);
            Controls.Add(head);
            Controls.Add(buttons);

            // Each option sits in its own card, so the radio buttons are made exclusive here.
            RadioButton[] all = { _rbA, _rbB, _rbNone };
            foreach (RadioButton rb in all)
                rb.CheckedChanged += (s, e) =>
                {
                    if (rb.Checked) foreach (RadioButton other in all) if (other != rb) other.Checked = false;
                    foreach (RadioButton r in all) r.Parent?.Invalidate();
                };
            _rbA.Enabled = writable || anyInstalled;
            _rbB.Enabled = writable;
            CatalogMode start = current;
            if (start == CatalogMode.Extend && (!writable || Edition.IsPublic)) start = CatalogMode.None;
            if (start == CatalogMode.AnySize && !_rbA.Enabled) start = CatalogMode.None;
            (start == CatalogMode.AnySize ? _rbA : start == CatalogMode.Extend ? _rbB : _rbNone).Checked = true;
            _chkAsk.Checked = askAgain;
            Mode = start;

            // Wrapped labels need their width before they can work out their height.
            Resize += (s, e) => FitText(head, intro);
            Load += (s, e) => FitText(head, intro);
        }

        private static RadioButton Radio(string text) => new RadioButton { AutoSize = true, Font = StyleUi.Heading, Text = text, Margin = new Padding(0, 0, 0, 2) };

        private static Label Wrap(string text, Color color) => new Label { AutoSize = true, Text = text, ForeColor = color };

        /// <summary>A white card: the radio button as its title, the explanation and a status line under it; clicking anywhere selects it.</summary>
        private void AddOption(RadioButton rb, string text, string status)
        {
            TableLayoutPanel card = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
                BackColor = StyleUi.Panel, Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 10)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label info = Wrap(text, StyleUi.Muted);
            info.Margin = new Padding(22, 4, 0, 0);
            card.Controls.Add(rb);
            card.Controls.Add(info);
            Label note = null;
            if (status.Length > 0)
            {
                note = Wrap(status, StyleUi.Text);
                note.Font = StyleUi.Bold;
                note.Margin = new Padding(22, 6, 0, 0);
                card.Controls.Add(note);
            }
            foreach (Control c in card.Controls) if (c != rb) c.Click += (s, e) => { if (rb.Enabled) rb.Checked = true; };
            card.Click += (s, e) => { if (rb.Enabled) rb.Checked = true; };
            card.Paint += (s, e) =>
            {
                using (Pen p = new Pen(rb.Checked ? StyleUi.Accent : StyleUi.Border, rb.Checked ? 2f : 1f))
                    e.Graphics.DrawRectangle(p, 1, 1, card.Width - 2, card.Height - 2);
            };
            _options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _options.Controls.Add(card);
            Resize += (s, e) => FitText(card, info, note);
            Load += (s, e) => FitText(card, info, note);
        }

        /// <summary>Gives wrapped labels the width of their card so they break into lines instead of one long line.</summary>
        private void FitText(Control container, params Label[] labels)
        {
            int width = Math.Max(200, _scroll.ClientSize.Width - _options.Padding.Horizontal - 4);
            foreach (Label l in labels)
                if (l != null) l.MaximumSize = new Size(Math.Max(150, width - l.Margin.Horizontal - container.Padding.Horizontal), 0);
        }

        private static bool SafeLoaded()
        {
            try
            {
                var loaded = PartsList.GetAvailablePartFamilies(DomainType.Pipe).Concat(PartsList.GetAvailablePartFamilies(DomainType.Structure)).Select(f => f.GUID).ToList();
                return NavisCatalogEditor.AnySize.All(f => loaded.Any(g => g.Equals(f.Guid, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception) { return false; }
        }
    }
}
