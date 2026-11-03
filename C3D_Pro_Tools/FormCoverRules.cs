using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace C3DProTools
{
    /// <summary>
    /// The Profile &amp; Cover Designer's design rules: junction matching, drops and backdrops, slopes by size, cover margin
    /// and zones, dry utility bends and pulls, and crossing clearances. Edits a copy; <see cref="Rules"/> holds it after OK.
    /// </summary>
    internal sealed class FormCoverRules : Form
    {
        private static readonly (string text, JunctionMatchRule value)[] Junctions =
        {
            ("Crown to crown (soffit)", JunctionMatchRule.CrownToCrown), ("0.8 depth", JunctionMatchRule.Match08Depth),
            ("Invert to invert", JunctionMatchRule.InvertToInvert), ("Crowns and inverts", JunctionMatchRule.StrictCrownAndInvert),
            ("Free (no rule)", JunctionMatchRule.None)
        };
        private static readonly (string text, BackdropMode value)[] Backdrops =
        {
            ("Backdrop only when nothing else fits (flagged)", BackdropMode.CostedBackdropWithFlag),
            ("Never (standard drops only)", BackdropMode.Disallow), ("Any drop, not flagged", BackdropMode.UnrestrictedDrop)
        };
        private static readonly (string text, SlopeSource value)[] SlopeSources =
        {
            ("The job's slope limits", SlopeSource.Flat), ("Table by diameter (below)", SlopeSource.Table), ("Minimum 1 in DN (e.g. DN300 → 1:300)", SlopeSource.OneInDiameter)
        };

        private readonly ToolTip _tips = new ToolTip();
        private readonly ComboBox _cbGravityJunction = ToolUi.Combo(), _cbDryJunction = ToolUi.Combo(), _cbBackdrop = ToolUi.Combo(), _cbSlopes = ToolUi.Combo();
        private readonly TextBox _txtDropStraight = ToolUi.Number(0), _txtDropMedium = ToolUi.Number(0), _txtDropSharp = ToolUi.Number(0);
        private readonly TextBox _txtStraightDeg = ToolUi.Number(0, 50), _txtMediumDeg = ToolUi.Number(0, 50);
        private readonly TextBox _txtMinBackdrop = ToolUi.Number(0), _txtMaxBackdrop = ToolUi.Number(0), _txtDryStep = ToolUi.Number(0);
        private readonly TextBox _txtSlopeTable = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 260, Height = 90, Font = new Font(FontFamily.GenericMonospace, 9F) };
        private readonly TextBox _txtMargin = ToolUi.Number(0), _txtZoneLayers = new TextBox { Width = 220 }, _txtZoneCover = ToolUi.Number(0), _txtZoneLabel = new TextBox { Width = 120 };
        private readonly TextBox _txtRadiusMultiple = ToolUi.Number(0), _txtRadius = ToolUi.Number(0), _txtBudget = ToolUi.Number(0), _txtSpan = ToolUi.Number(0);
        private readonly CheckBox _chkMatrix = ToolUi.Check("Use the clearance matrix (by utility class)");
        private readonly TextBox _txtDryDry = ToolUi.Number(0), _txtWet = ToolUi.Number(0), _txtSlab = ToolUi.Number(0), _txtPadding = ToolUi.Number(0);
        private readonly TextBox _txtHorizontal = ToolUi.Number(0), _txtCollar = ToolUi.Number(0);
        private readonly CheckBox _chkParallel = ToolUi.Check("Also clear pipes alongside vertically (not only report them)");
        private readonly TextBox _txtIct = new TextBox { Width = 300 }, _txtPower = new TextBox { Width = 300 }, _txtGravity = new TextBox { Width = 300 }, _txtPressure = new TextBox { Width = 300 };

        public CoverRules Rules { get; private set; }

        public FormCoverRules(CoverRules rules)
        {
            Text = "Profile & Cover Designer - Design rules";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(680, 760);
            MinimumSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;

            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            host.Padding = new Padding(10, 10, 12, 4);

            foreach (var j in Junctions) { _cbGravityJunction.Items.Add(j.text); _cbDryJunction.Items.Add(j.text); }
            foreach (var b in Backdrops) _cbBackdrop.Items.Add(b.text);
            foreach (var s in SlopeSources) _cbSlopes.Items.Add(s.text);

            TableLayoutPanel junction = ToolUi.Card("Manholes and pits", "How the outgoing pipe may sit against the incoming one, and how far it may drop.");
            ToolUi.Field(junction, "Gravity networks", _cbGravityJunction, _tips, "Crown to crown: a larger outlet drops so its crown is not above the inlet's.");
            ToolUi.Field(junction, "Dry utilities", _cbDryJunction, _tips, "Free: cables may rise or fall through a pit, within the step below.");
            ToolUi.Field(junction, "Dry pit step", ToolUi.WithUnit(_txtDryStep, "m up or down at most (invert to invert; 0 = flush)"), _tips,
                "With a free junction, how far a conduit's invert may step up or down through one pit.");
            ToolUi.Field(junction, "Minimum drop", ToolUi.Inline(_txtDropStraight, Unit("m up to"), _txtStraightDeg, Unit("°,"), _txtDropMedium, Unit("m up to"), _txtMediumDeg, Unit("°,"), _txtDropSharp, Unit("m sharper")),
                _tips, "Gravity only: by the turn the flow makes at the manhole.");
            ToolUi.Field(junction, "Deeper drops", _cbBackdrop, _tips, "The standard maximum drop is set in the main window.");
            ToolUi.Field(junction, "Backdrop drop", ToolUi.Inline(_txtMinBackdrop, Unit("to"), _txtMaxBackdrop, Unit("m (invert to invert)")));
            ToolUi.Add(stack, junction);

            TableLayoutPanel slopes = ToolUi.Card("Slopes by pipe size");
            ToolUi.Field(slopes, "Slope limits from", _cbSlopes);
            ToolUi.Field(slopes, "Table", _txtSlopeTable, _tips, "One row per line: inner diameter (mm), minimum slope (%), maximum slope (% or 0 = the job's). " +
                                                                    "A pipe takes the row of the largest diameter not above its own. Enter your authority's values.");
            ToolUi.Wide(slopes, ToolUi.Note("Rows: DN mm, min %, max %  —  e.g.  300, 0.5, 8"));
            Button preset = StyleUi.Secondary("Load Standard Gravity Preset (v >= 0.75 m/s)", (s, e) => LoadGravityPreset());
            _tips.SetToolTip(preset, "Fills the table with a self-cleansing preset (DN150 to DN1200) and takes the slope limits from it. " +
                                     "Check the values against your authority's standard before relying on them.");
            ToolUi.Wide(slopes, preset);
            ToolUi.Add(stack, slopes);

            TableLayoutPanel cover = ToolUi.Card("Cover");
            ToolUi.Field(cover, "Safety margin", ToolUi.WithUnit(_txtMargin, "m inside the limits"), _tips, "So a 0.600 m minimum is laid at 0.602 m and never shows as 0.599 m after rounding.");
            ToolUi.Field(cover, "Zone layers", _txtZoneLayers, _tips, "Closed polylines on these layers (comma-separated, * allowed), e.g. paving, roads, taxiways.");
            ToolUi.Field(cover, "Zone minimum cover", ToolUi.Inline(_txtZoneCover, Unit("m, labelled"), _txtZoneLabel));
            ToolUi.Add(stack, cover);

            TableLayoutPanel dry = ToolUi.Card("Dry utilities (cables and ducts)");
            ToolUi.Field(dry, "Minimum bend radius", ToolUi.Inline(_txtRadiusMultiple, Unit("× inner diameter, or"), _txtRadius, Unit("m (0 = use the multiple)")));
            ToolUi.Field(dry, "Bend budget per pull", ToolUi.WithUnit(_txtBudget, "° in plan and profile (0 = no limit)"), _tips, "Total bending allowed between two pits, for pulling cables.");
            ToolUi.Field(dry, "Longest pull", ToolUi.WithUnit(_txtSpan, "m between structures (0 = no limit)"));
            ToolUi.Add(stack, dry);

            TableLayoutPanel crossings = ToolUi.Card("Crossings", "Vertical clearance is kept where pipes cross or overlap in plan; a pipe only alongside is reported.");
            ToolUi.Wide(crossings, _chkMatrix);
            ToolUi.Field(crossings, "Dry to dry", ToolUi.WithUnit(_txtDryDry, "m"));
            ToolUi.Field(crossings, "With a wet utility", ToolUi.WithUnit(_txtWet, "m"), _tips, "Wet to dry and wet to wet; pipes of an unknown class count as wet.");
            ToolUi.Field(crossings, "Protection slab / HDD", ToolUi.WithUnit(_txtSlab, "m least clearance (0 = full clearance only)"), _tips,
                "Used only where the full clearance cannot be kept; those crossings are flagged.");
            ToolUi.Field(crossings, "Encasement", ToolUi.WithUnit(_txtPadding, "m on each side of dry utilities"), _tips,
                "Concrete not drawn in the model: part of the utility's outside, so cover is to its top and clearances are kept from its faces.");
            ToolUi.Field(crossings, "Horizontal clearance", ToolUi.WithUnit(_txtHorizontal, "m edge to edge"));
            ToolUi.Wide(crossings, _chkParallel);
            ToolUi.Field(crossings, "Structure collar", ToolUi.WithUnit(_txtCollar, "m around the route's structures (branches there)"));
            ToolUi.Wide(crossings, ToolUi.Note("Utility classes come from network names (regular expressions, case ignored):"));
            ToolUi.Field(crossings, "ICT / telecom", _txtIct);
            ToolUi.Field(crossings, "Power", _txtPower);
            ToolUi.Field(crossings, "Gravity (wet)", _txtGravity);
            ToolUi.Field(crossings, "Pressure (wet)", _txtPressure);
            ToolUi.Add(stack, crossings);

            Button ok = StyleUi.Primary("OK", (s, e) => { if (TryRead(out CoverRules r)) { Rules = r; DialogResult = DialogResult.OK; } });
            Button cancel = StyleUi.Secondary("Cancel", (s, e) => DialogResult = DialogResult.Cancel);
            Button defaults = StyleUi.Secondary("Defaults", (s, e) => Show(new CoverRules()));
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 10) };
            buttons.Controls.AddRange(new Control[] { ok, cancel, defaults });
            Controls.Add(host);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            Resize += (s, e) => ToolUi.FitNotes(host, host.ClientSize.Width);
            Shown += (s, e) => ToolUi.FitNotes(host, host.ClientSize.Width);

            Show(rules ?? new CoverRules());
        }

        private void LoadGravityPreset()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            _txtSlopeTable.Text = string.Join(Environment.NewLine, CoverRules.GravityPresetV075().Select(row =>
                $"{row.DiameterMm.ToString("0.###", c)}, {row.MinSlopePercent.ToString("0.####", c)}, {row.MaxSlopePercent.ToString("0.####", c)}"));
            _cbSlopes.SelectedIndex = Array.FindIndex(SlopeSources, s => s.value == SlopeSource.Table);
        }

        private static Label Unit(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) };

        private void Show(CoverRules r)
        {
            _cbGravityJunction.SelectedIndex = Array.FindIndex(Junctions, j => j.value == r.GravityJunction);
            _cbDryJunction.SelectedIndex = Array.FindIndex(Junctions, j => j.value == r.DryJunction);
            _cbBackdrop.SelectedIndex = Array.FindIndex(Backdrops, b => b.value == r.Backdrop);
            _cbSlopes.SelectedIndex = Array.FindIndex(SlopeSources, s => s.value == r.Slopes);
            _txtDropStraight.Text = ToolUi.Format(r.MinDropStraight); _txtDropMedium.Text = ToolUi.Format(r.MinDropMedium); _txtDropSharp.Text = ToolUi.Format(r.MinDropSharp);
            _txtStraightDeg.Text = ToolUi.Format(r.StraightMaxDeg); _txtMediumDeg.Text = ToolUi.Format(r.MediumMaxDeg);
            _txtMinBackdrop.Text = ToolUi.Format(r.MinBackdropHeight); _txtMaxBackdrop.Text = ToolUi.Format(r.MaxBackdropDrop);
            _txtDryStep.Text = ToolUi.Format(r.DryMaxStep);
            CultureInfo c = CultureInfo.InvariantCulture;
            _txtSlopeTable.Text = string.Join(Environment.NewLine, (r.SlopeTable ?? new List<SlopeRow>()).Select(row =>
                $"{row.DiameterMm.ToString("0.###", c)}, {row.MinSlopePercent.ToString("0.####", c)}, {row.MaxSlopePercent.ToString("0.####", c)}"));
            _txtMargin.Text = ToolUi.Format(r.CoverSafetyEpsilon);
            _txtZoneLayers.Text = r.ZoneLayers ?? "";
            _txtZoneCover.Text = ToolUi.Format(r.ZoneMinCover);
            _txtZoneLabel.Text = r.ZoneLabel ?? "";
            _txtRadiusMultiple.Text = ToolUi.Format(r.MinBendRadiusMultiple); _txtRadius.Text = ToolUi.Format(r.MinBendRadius);
            _txtBudget.Text = ToolUi.Format(r.BendBudgetDeg); _txtSpan.Text = ToolUi.Format(r.MaxStructureSpan);
            _chkMatrix.Checked = r.UseClearanceMatrix;
            _txtDryDry.Text = ToolUi.Format(r.DryToDryClearance); _txtWet.Text = ToolUi.Format(r.WetClearance);
            _txtSlab.Text = ToolUi.Format(r.ProtectionMinClearance); _txtPadding.Text = ToolUi.Format(r.EncasementPadding);
            _txtHorizontal.Text = ToolUi.Format(r.HorizontalClearance); _txtCollar.Text = ToolUi.Format(r.StructureCollar);
            _chkParallel.Checked = r.EnforceVerticalOnParallel;
            _txtIct.Text = r.IctPattern; _txtPower.Text = r.PowerPattern; _txtGravity.Text = r.GravityPattern; _txtPressure.Text = r.PressurePattern;
        }

        private bool TryRead(out CoverRules r)
        {
            r = null;
            bool Bad(string message) { MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            TextBox[] numbers = { _txtDropStraight, _txtDropMedium, _txtDropSharp, _txtStraightDeg, _txtMediumDeg, _txtMinBackdrop, _txtMaxBackdrop, _txtMargin, _txtZoneCover,
                                  _txtRadiusMultiple, _txtRadius, _txtBudget, _txtSpan, _txtDryDry, _txtWet, _txtSlab, _txtPadding, _txtHorizontal, _txtCollar, _txtDryStep };
            double[] v = new double[numbers.Length];
            for (int i = 0; i < numbers.Length; i++)
                if (!ToolUi.TryNumber(numbers[i], out v[i]) || v[i] < 0) { numbers[i].Focus(); return Bad("Every rule must be a number, zero or more."); }
            if (v[4] < v[3]) return Bad("The medium turn angle must be at least the straight one.");
            if (v[6] < v[5]) return Bad("The largest backdrop drop must be at least the smallest.");
            if (v[15] > 0 && v[15] > Math.Max(v[13], v[14])) return Bad("The protection slab clearance must be less than the full clearances.");

            List<SlopeRow> table = new List<SlopeRow>();
            string[] lines = _txtSlopeTable.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
                if (parts.Length < 2 || parts.Length > 3 || parts.Any(p => !double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || x < 0))
                    return Bad($"Slope table line {i + 1}: write diameter (mm), minimum slope (%), and optionally maximum slope (%), e.g. 300, 0.5, 8");
                double[] n = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                table.Add(new SlopeRow { DiameterMm = n[0], MinSlopePercent = n[1], MaxSlopePercent = n.Length > 2 ? n[2] : 0 });
            }
            SlopeSource source = SlopeSources[Math.Max(0, _cbSlopes.SelectedIndex)].value;
            if (source == SlopeSource.Table && table.Count == 0) return Bad("Enter the slope table, or take the slope limits from somewhere else.");
            foreach (TextBox t in new[] { _txtIct, _txtPower, _txtGravity, _txtPressure })
            {
                try { System.Text.RegularExpressions.Regex.IsMatch("", t.Text); }
                catch (ArgumentException ex) { t.Focus(); return Bad("Not a valid pattern: " + ex.Message); }
            }

            r = new CoverRules
            {
                GravityJunction = Junctions[Math.Max(0, _cbGravityJunction.SelectedIndex)].value,
                DryJunction = Junctions[Math.Max(0, _cbDryJunction.SelectedIndex)].value,
                MinDropStraight = v[0], MinDropMedium = v[1], MinDropSharp = v[2], StraightMaxDeg = v[3], MediumMaxDeg = v[4],
                Backdrop = Backdrops[Math.Max(0, _cbBackdrop.SelectedIndex)].value, MinBackdropHeight = v[5], MaxBackdropDrop = v[6],
                Slopes = source, SlopeTable = table,
                CoverSafetyEpsilon = v[7], ZoneLayers = _txtZoneLayers.Text.Trim(), ZoneMinCover = v[8], ZoneLabel = _txtZoneLabel.Text.Trim(),
                MinBendRadiusMultiple = v[9], MinBendRadius = v[10], BendBudgetDeg = v[11], MaxStructureSpan = v[12],
                UseClearanceMatrix = _chkMatrix.Checked, DryToDryClearance = v[13], WetClearance = v[14], ProtectionMinClearance = v[15], EncasementPadding = v[16],
                HorizontalClearance = v[17], StructureCollar = v[18], DryMaxStep = v[19], EnforceVerticalOnParallel = _chkParallel.Checked,
                IctPattern = _txtIct.Text, PowerPattern = _txtPower.Text, GravityPattern = _txtGravity.Text, PressurePattern = _txtPressure.Text
            };
            return true;
        }
    }
}
