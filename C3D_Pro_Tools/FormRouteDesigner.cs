using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Profile &amp; Cover Designer: everything vertical about pipe routes in one window.
    /// <list type="bullet">
    /// <item>Cover: check and optimise existing routes, several at once (picked, from a selection or whole networks); parallel
    /// pipes through the same structures stay separate routes.</item>
    /// <item>Draw: new pipes along polylines, alignments or feature lines, levelled by the same optimiser, into a new or an
    /// existing network.</item>
    /// <item>Profiles: add the parts along alignments to their profile views, or create an alignment and profile view from parts.</item>
    /// </list>
    /// The options on the right are shared, and show only what the current section uses.
    /// </summary>
    internal sealed partial class FormRouteDesigner : Form
    {
        public const int SCover = 0, SDraw = 1, SProfiles = 2;

        private readonly CoverOptimizerSettings _s = CoverOptimizerSettings.Load();
        private readonly PartsToProfileSettings _settings = PartsToProfileSettings.Load();
        private readonly NavList _nav = new NavList();
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6, BackColor = StyleUi.Border };
        private readonly List<Control> _pages = new List<Control>();
        private readonly List<(Control card, int[] sections)> _optionCards = new List<(Control, int[])>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) };
        /// <summary>Shown while routes are optimised (in the background): stops the search.</summary>
        private readonly Button _btnCancelSolve = StyleUi.Secondary("Cancel");
        private System.Threading.CancellationTokenSource _solving;

        private readonly Dictionary<string, ObjectId> _surfaces = new Dictionary<string, ObjectId>();
        private readonly Dictionary<string, ObjectId> _gravityNetworks = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The network the manhole chooser opens on (the first route's).</summary>
        private ObjectId _manholeNetwork = ObjectId.Null;

        // Shared options
        private readonly RadioButton _rbDry = new RadioButton { Text = "Dry (cables, ducts): may rise and fall with the ground", AutoSize = true };
        private readonly RadioButton _rbGravity = new RadioButton { Text = "Gravity: continuous fall in the flow direction", AutoSize = true };
        private readonly ComboBox _cbSurface = ToolUi.Combo(), _cbReference = ToolUi.Combo();
        private readonly TextBox _txtMinCover = ToolUi.Number(0.6), _txtMaxCover = ToolUi.Number(1.5), _txtSpacing = ToolUi.Number(1.0);
        private readonly TextBox _txtMinLength = ToolUi.Number(20), _txtMaxLength = ToolUi.Number(0);
        private readonly TextBox _txtMinSlope = ToolUi.Number(0), _txtMaxSlope = ToolUi.Number(10), _txtMaxSlopeChange = ToolUi.Number(5);
        private readonly ComboBox _cbPrefer = ToolUi.Combo();
        private readonly TextBox _txtMaxDrop = ToolUi.Number(0.5), _txtClearance = ToolUi.Number(0);
        private readonly CheckBox _chkCreateProfile = ToolUi.Check("Create an alignment and profile view for each route after applying");
        // Existing routes
        private readonly ComboBox _cbFlow = ToolUi.Combo(), _cbStart = ToolUi.Combo(), _cbEnd = ToolUi.Combo();
        private readonly PartChooser _manholePart = new PartChooser(DomainType.Structure);
        // Reading routes
        private readonly TextBox _txtGap = ToolUi.Number(1.0), _txtCenterTol = ToolUi.Number(0.05);
        private CoverMode _shownMode;

        public FormRouteDesigner(int section = SCover)
        {
            Text = "C3D Pro Tools - Profile & Cover Designer";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            KeyPreview = true;
            MinimumSize = new Size(1200, 680);
            ClientSize = new Size(_s.Width >= 1080 ? _s.Width : 1280, _s.Height >= 680 ? _s.Height : 800);

            ReadDrawing();
            _nav.Add("Cover", "Check & optimise existing routes");
            _nav.Add("Draw along paths", "New pipes on polylines / alignments");
            _nav.Add("Profiles", "Parts in profile views, new alignments");
            _pages.Add(BuildCoverPage());
            _pages.Add(BuildDrawPage());
            _pages.Add(BuildProfilesPage());
            foreach (Control p in _pages) { p.Visible = false; _split.Panel1.Controls.Add(p); }
            _split.Panel1.BackColor = _split.Panel2.BackColor = StyleUi.Surface;
            _split.Panel2.Controls.Add(BuildOptions());

            Controls.Add(_split);
            Controls.Add(_nav);
            Controls.Add(ToolUi.Header("Profile & Cover Designer", "Levels, cover and profiles of pipe routes: existing networks and new pipes along paths.",
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _nav.SelectedChanged += (s, e) => ShowSection();
            Shown += (s, e) =>
            {
                _split.Panel1MinSize = 480;
                _split.Panel2MinSize = 340;
                _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Width - (int)Math.Round(400 * DeviceDpi / 96.0));
                _nav.Selected = section >= 0 ? section : _s.Section;
                ShowSection();
            };
            FormClosing += (s, e) =>
            {
                // A search still running is stopped first; the window stays until it has finished.
                if (_solving != null)
                {
                    _solving.Cancel();
                    e.Cancel = true;
                    SetStatus("Stopping the optimisation… close again when it has stopped.");
                    return;
                }
                TryReadOptions(out _, false);
                _s.Section = _nav.Selected;
                _s.Width = ClientSize.Width;
                _s.Height = ClientSize.Height;
                _s.Save();
                SaveProfileSettings();
            };
        }

        private void ShowSection()
        {
            int section = _nav.Selected;
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == section;
            bool any = false;
            foreach ((Control card, int[] sections) in _optionCards)
            {
                card.Visible = sections.Contains(section);
                any |= card.Visible;
            }
            _split.Panel2Collapsed = !any;
            _lblStatus.Text = section == SCover ? CoverSummary() : section == SDraw ? DrawSummary() : "";
        }

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Panel, Padding = new Padding(12, 4, 12, 6) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            footer.Controls.Add(_lblStatus, 0, 0);
            _btnCancelSolve.Visible = false;
            _btnCancelSolve.Click += (s, e) => { _solving?.Cancel(); SetStatus("Stopping…"); };
            footer.Controls.Add(_btnCancelSolve, 1, 0);
            return footer;
        }

        private void SetStatus(string text) { _lblStatus.Text = text; _lblStatus.Refresh(); }

        // =====================================================================================
        // Shared options (right panel)
        // =====================================================================================

        private Control BuildOptions()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            host.Padding = new Padding(10, 10, 12, 4);

            foreach (Control c in BuildDrawOptions()) AddCard(stack, c, SDraw);

            TableLayoutPanel type = ToolUi.Card("Network type");
            ToolUi.Wide(type, _rbDry);
            ToolUi.Wide(type, _rbGravity);
            AddCard(stack, type, SCover, SDraw);

            TableLayoutPanel cover = ToolUi.Card("Surface and cover");
            ToolUi.Field(cover, "Surface", _cbSurface);
            _cbReference.Items.AddRange(new object[] { "Outside top of pipe", "Pipe centreline", "Pipe invert" });
            ToolUi.Field(cover, "Cover to", _cbReference, _tips, "The point of the pipe the cover is measured to.");
            ToolUi.Field(cover, "Cover", ToolUi.Inline(_txtMinCover, new Label { Text = "to", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _txtMaxCover,
                new Label { Text = "m", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(2, 7, 0, 0) }));
            ToolUi.Field(cover, "Check every", ToolUi.WithUnit(_txtSpacing, "m"), _tips, "Ground is sampled (and pipes may break) at this spacing.");
            AddCard(stack, cover, SCover, SDraw);

            TableLayoutPanel limits = ToolUi.Card("Pipe limits");
            ToolUi.Field(limits, "Length", ToolUi.Inline(_txtMinLength, new Label { Text = "to", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _txtMaxLength,
                new Label { Text = "m (0 = any)", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(2, 7, 0, 0) }), _tips,
                "Shortest and longest pipe the optimiser may make, e.g. a stock length; on gravity networks every break gets a manhole, so the maximum is also a manhole spacing.");
            ToolUi.Field(limits, "Slope", ToolUi.Inline(_txtMinSlope, new Label { Text = "to", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _txtMaxSlope,
                new Label { Text = "%", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(2, 7, 0, 0) }));
            ToolUi.Field(limits, "Slope change", ToolUi.WithUnit(_txtMaxSlopeChange, "% (0 = any)"));
            _cbPrefer.Items.AddRange(new object[] { "Gentlest bends", "Shallowest pipes" });
            ToolUi.Field(limits, "Prefer", _cbPrefer, _tips, "Between layouts with equally few pipes: the one with the gentlest sharpest bend, or the shallowest.");
            AddCard(stack, limits, SCover, SDraw);

            TableLayoutPanel gravity = ToolUi.Card("Gravity networks");
            ToolUi.Field(gravity, "Max drop", ToolUi.WithUnit(_txtMaxDrop, "m"));
            ToolUi.Wide(gravity, ToolUi.Note("A branch is never laid to arrive below the main line it joins; main lines are solved first."));
            AddCard(stack, gravity, SCover, SDraw);

            TableLayoutPanel routes = ToolUi.Card("Existing routes");
            _cbFlow.Items.AddRange(new object[] { "Auto (most of the pipes)", "Route direction", "Against the route direction" });
            ToolUi.Field(routes, "Flow (gravity)", _cbFlow);
            foreach (ComboBox cb in new[] { _cbStart, _cbEnd }) cb.Items.AddRange(new object[] { "Keep its level", "Free (within the limits)" });
            ToolUi.Field(routes, "Route start", _cbStart);
            ToolUi.Field(routes, "Route end", _cbEnd);
            ToolUi.Wide(routes, ToolUi.Note("New manholes (gravity: at every new break) — taken from each route network's own parts list; a part from another list is added to it."));
            ToolUi.Wide(routes, _manholePart);
            AddCard(stack, routes, SCover);

            TableLayoutPanel crossings = ToolUi.Card("Crossing pipes");
            ToolUi.Field(crossings, "Keep", ToolUi.WithUnit(_txtClearance, "m (0 = ignore)"), _tips,
                "Pipes of other routes and networks crossing the route are passed under (or over, when under breaks the cover limits) with this clear distance. " +
                "With the clearance matrix on (Design rules), each crossing takes the clearance for its pair of utilities instead.");
            AddCard(stack, crossings, SCover, SDraw);

            TableLayoutPanel rules = ToolUi.Card("Design rules",
                "Junction matching, minimum drops and backdrops, slopes by size, cover margin and zones, dry utility bends and pulls, crossing clearances.");
            ToolUi.Wide(rules, StyleUi.Secondary("Design rules…", (s, e) => EditRules()));
            AddCard(stack, rules, SCover, SDraw);

            TableLayoutPanel reading = ToolUi.Card("Reading routes");
            ToolUi.Field(reading, "Max gap", ToolUi.WithUnit(_txtGap, "m"), _tips, "Pipe ends without a structure count as joined up to this distance apart.");
            ToolUi.Field(reading, "Centre tolerance", ToolUi.WithUnit(_txtCenterTol, "m"), _tips, "Pipe axes passing this close to a manhole centre run through it.");
            AddCard(stack, reading, SCover, SProfiles);

            TableLayoutPanel after = ToolUi.Card("After applying");
            ToolUi.Wide(after, _chkCreateProfile);
            AddCard(stack, after, SCover, SDraw);

            _rbDry.CheckedChanged += (s, e) => { if (_rbDry.Checked) SwitchMode(CoverMode.Dry); };
            _rbGravity.CheckedChanged += (s, e) => { if (_rbGravity.Checked) SwitchMode(CoverMode.Gravity); };
            return host;
        }

        private void AddCard(TableLayoutPanel stack, Control card, params int[] sections)
        {
            ToolUi.Add(stack, card);
            _optionCards.Add((card, sections));
        }

        private void ReadDrawing()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in doc.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface s && !_surfaces.ContainsKey(s.Name)) { _surfaces[s.Name] = id; _cbSurface.Items.Add(s.Name); }
                foreach (ObjectId id in doc.GetPipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Network n) _gravityNetworks[n.Name] = id;
            }
        }

        /// <summary>Opens the manhole chooser on the first route's network (new manholes come from each route network's own list).</summary>
        private void UpdateManholeContext(bool force = false)
        {
            ObjectId net = _coverJobs.FirstOrDefault(j => !j.NetworkId.IsNull)?.NetworkId ?? ObjectId.Null;
            if (net == _manholeNetwork && !force) return;
            _manholeNetwork = net;
            _manholePart.SetContext(net, net.IsNull ? (force ? _s.StructureList : _manholePart.ListName) : null,
                force ? _s.StructureFamily : _manholePart.FamilyName, force ? _s.StructureSize : _manholePart.SizeName);
        }

        private void ApplySettings()
        {
            _shownMode = _s.Mode;
            _rbGravity.Checked = _s.Mode == CoverMode.Gravity;
            _rbDry.Checked = _s.Mode == CoverMode.Dry;
            ToolUi.Select(_cbSurface, _s.Surface);
            _cbReference.SelectedIndex = Math.Max(0, Math.Min(2, (int)_s.Reference));
            _txtMinCover.Text = ToolUi.Format(_s.MinCover);
            _txtMaxCover.Text = ToolUi.Format(_s.MaxCover);
            _txtSpacing.Text = ToolUi.Format(_s.Spacing > 0 ? _s.Spacing : 1.0);
            _txtMinLength.Text = ToolUi.Format(_s.MinLength);
            _txtMaxLength.Text = ToolUi.Format(_s.MaxLength);
            _txtMinSlope.Text = ToolUi.Format(_s.Mode == CoverMode.Gravity ? _s.GravityMinSlope : _s.DryMinSlope);
            _txtMaxSlope.Text = ToolUi.Format(_s.MaxSlope);
            _txtMaxSlopeChange.Text = ToolUi.Format(_s.MaxSlopeChange);
            _cbPrefer.SelectedIndex = _s.PreferGentleBends ? 0 : 1;
            _txtMaxDrop.Text = ToolUi.Format(_s.MaxDrop);
            _txtClearance.Text = ToolUi.Format(_s.CrossingClearance);
            _cbFlow.SelectedIndex = Math.Max(0, Math.Min(2, _s.FlowDirection));
            _cbStart.SelectedIndex = _s.StartFixed ? 0 : 1;
            _cbEnd.SelectedIndex = _s.EndFixed ? 0 : 1;
            UpdateManholeContext(true);
            _txtGap.Text = ToolUi.Format(_settings.GapLimit);
            _txtCenterTol.Text = ToolUi.Format(_settings.CenterTolerance);
            _chkCreateProfile.Checked = _s.CreateProfile;
            ApplyDrawSettings();
            ApplyProfileSettings();
        }

        /// <summary>Each network type keeps its own minimum slope (dry 0 %, gravity 0.3 % by default).</summary>
        private void SwitchMode(CoverMode mode)
        {
            if (mode == _shownMode) return;
            if (ToolUi.TryNumber(_txtMinSlope, out double current))
            {
                if (_shownMode == CoverMode.Gravity) _s.GravityMinSlope = current; else _s.DryMinSlope = current;
            }
            _shownMode = mode;
            _txtMinSlope.Text = ToolUi.Format(mode == CoverMode.Gravity ? _s.GravityMinSlope : _s.DryMinSlope);
            InvalidateResults();
        }

        /// <summary>Reads (and saves) the shared options; with <paramref name="show"/> a bad value is reported.</summary>
        private bool TryReadOptions(out CoverOptions o, bool show = true)
        {
            o = null;
            bool Bad(string message) { if (show) MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            if (!ToolUi.TryNumber(_txtMinCover, out double minCover) || !ToolUi.TryNumber(_txtMaxCover, out double maxCover) ||
                !ToolUi.TryNumber(_txtMinLength, out double minLength) || !ToolUi.TryNumber(_txtMaxLength, out double maxLength) ||
                !ToolUi.TryNumber(_txtMinSlope, out double minSlope) || !ToolUi.TryNumber(_txtMaxSlope, out double maxSlope) ||
                !ToolUi.TryNumber(_txtMaxSlopeChange, out double maxChange) || !ToolUi.TryNumber(_txtMaxDrop, out double maxDrop) ||
                !ToolUi.TryNumber(_txtClearance, out double clearance) || !ToolUi.TryNumber(_txtSpacing, out double spacing) ||
                !ToolUi.TryNumber(_txtGap, out double gap) || !ToolUi.TryNumber(_txtCenterTol, out double center))
                return Bad("Every option must be a number.");
            if (maxCover <= minCover) return Bad("The maximum cover must be more than the minimum.");
            if (maxSlope <= 0 || minSlope > maxSlope || minSlope < 0) return Bad("Slopes: the maximum must be positive and not below the minimum.");
            if (minLength <= 0 || (maxLength > 0 && maxLength < minLength)) return Bad("Lengths: the minimum must be positive, and the maximum (when set) at least the minimum.");
            if (spacing <= 0 || spacing > minLength) return Bad("The check spacing must be positive and not longer than the minimum pipe length.");
            if (gap <= 0 || center <= 0 || clearance < 0 || maxDrop < 0) return Bad("The gap, centre tolerance, drop and clearance cannot be negative.");

            CoverMode mode = _rbGravity.Checked ? CoverMode.Gravity : CoverMode.Dry;
            o = new CoverOptions
            {
                Mode = mode, Reference = (CoverReference)Math.Max(0, _cbReference.SelectedIndex),
                MinCover = minCover, MaxCover = maxCover, MinLength = minLength, MaxLength = maxLength,
                MinSlope = minSlope / 100.0, MaxSlope = maxSlope / 100.0, MaxSlopeChange = maxChange / 100.0,
                MaxDrop = maxDrop, StartFixed = _cbStart.SelectedIndex == 0, EndFixed = _cbEnd.SelectedIndex == 0,
                PreferGentleBends = _cbPrefer.SelectedIndex == 0, CrossingClearance = clearance,
                Rules = _s.Rules ?? new CoverRules()
            };
            _s.Mode = mode;
            _s.Surface = _cbSurface.Text;
            _s.Reference = o.Reference;
            _s.MinCover = minCover; _s.MaxCover = maxCover; _s.MinLength = minLength; _s.MaxLength = maxLength;
            if (mode == CoverMode.Gravity) _s.GravityMinSlope = minSlope; else _s.DryMinSlope = minSlope;
            _s.MaxSlope = maxSlope; _s.MaxSlopeChange = maxChange; _s.MaxDrop = maxDrop; _s.CrossingClearance = clearance;
            _s.Spacing = spacing;
            _s.PreferGentleBends = o.PreferGentleBends;
            _s.FlowDirection = _cbFlow.SelectedIndex;
            _s.StartFixed = o.StartFixed; _s.EndFixed = o.EndFixed;
            _s.StructureList = _manholePart.ListName; _s.StructureFamily = _manholePart.FamilyName; _s.StructureSize = _manholePart.SizeName;
            _s.CreateProfile = _chkCreateProfile.Checked;
            _settings.GapLimit = gap;
            _settings.CenterTolerance = center;
            SaveDrawSettings();
            _s.Save();
            _settings.Save();
            return true;
        }

        private void EditRules()
        {
            using (FormCoverRules f = new FormCoverRules(_s.Rules))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _s.Rules = f.Rules;
            }
            _s.Save();
            InvalidateResults();
            SetStatus("Design rules saved. Check or optimise the routes again to use them.");
        }

        private CoverJobs.Setup ReadSetup()
        {
            ToolUi.TryNumber(_txtSpacing, out double spacing);
            return new CoverJobs.Setup { Spacing = spacing, Gap = _settings.GapLimit, CenterTolerance = _settings.CenterTolerance, FlowDirection = _cbFlow.SelectedIndex };
        }

        private Autodesk.Civil.DatabaseServices.Surface OpenSurface(Transaction tr) =>
            _surfaces.TryGetValue(_cbSurface.Text, out ObjectId id) ? tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface : null;

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Cover: add routes of existing pipes (pick a start and an end pipe, select pipes, or add whole networks; parallel pipes running " +
                "through the same structures are kept as separate routes). Check shows the current cover; Optimize finds the layout with the " +
                "fewest pipes that keeps every limit; Preview shows a route's profile; Apply changes the ticked routes. On gravity networks main " +
                "lines are solved first and branches never arrive below them.\n\n" +
                "Draw along paths: select polylines, 3D polylines, lines, arcs, splines, alignments or feature lines. Choose the network (new " +
                "or existing) and the parts; the same optimiser levels the new pipes, with structures at bends, ends and the spacing you set. " +
                "Paths ending inside an existing structure of the chosen network connect to it.\n\n" +
                "Profiles: add the network parts lying along alignments to their profile views, or create an alignment, surface profiles and " +
                "a profile view from parts you select.\n\n" +
                "The options on the right are shared by Cover and Draw. Every change is previewed first.",
                "Profile & Cover Designer - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
