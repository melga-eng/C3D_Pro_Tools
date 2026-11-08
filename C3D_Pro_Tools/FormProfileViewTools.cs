using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Profile View Tools: style by example for profile views (match a finished view, or a saved template, onto others by
    /// role), followers that keep following their master, a health check of the views' bands and ranges, and a splitter that
    /// breaks a long view into parts of a set length (or the length a sheet's viewport holds), with profile sheets.
    /// </summary>
    internal sealed class FormProfileViewTools : Form
    {
        private const int PMatch = 0, PTemplates = 1, PFollowers = 2, PCheck = 3, PSplit = 4;
        private static readonly string[] RunText = { "Preview && apply…", "Apply template…", "Sync all followers…", "Check views", "Create parts…" };

        private sealed class ViewItem
        {
            public ObjectId Id;
            public string Name = "", Alignment = "", Master = "";
            public override string ToString() => $"{Name}   ({Alignment}){(Master.Length > 0 ? $"   follows {Master}" : "")}";
        }

        private readonly ProfileViewToolsSettings _s = ProfileViewToolsSettings.Load();
        private readonly NavList _nav = new NavList();
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Surface };
        private readonly List<Control> _pages = new List<Control>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) };
        private Button _btnRun;
        private List<ViewItem> _views = new List<ViewItem>();
        private List<string> _surfaceNames = new List<string>();

        // Match
        private readonly RadioButton _rbFromView = new RadioButton { Text = "A profile view:", AutoSize = true, Checked = true };
        private readonly RadioButton _rbFromTemplate = new RadioButton { Text = "A saved template:", AutoSize = true };
        private readonly ComboBox _cbSource = ToolUi.Combo(420), _cbTemplate = ToolUi.Combo(300);
        private readonly ComboBox _cbExisting = ToolUi.Combo(380), _cbProposed = ToolUi.Combo(380);
        private readonly TextBox _txtTopWords = new TextBox { Width = 160 };
        private readonly Label _lblRoles = ToolUi.Note("");
        private readonly CheckedListBox _clbTargets = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Height = 210, Width = 620, BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox _chkViewStyle = ToolUi.Check("View style"), _chkBands = ToolUi.Check("Bands, and the profiles each band shows (by role)");
        private readonly CheckBox _chkProfileStyles = ToolUi.Check("Profile styles"), _chkLabels = ToolUi.Check("Profile labels");
        private readonly CheckBox _chkElevation = ToolUi.Check("Elevation range: fit the profiles and parts, plus");
        private readonly CheckBox _chkInView = ToolUi.Check("Set profile styles in these views only (view overrides), not on the profiles");
        private readonly TextBox _txtMargin = ToolUi.Number(1, 50);
        /// <summary>Elevation ranges round outward to whole steps of this (m); 1, 2 or 5 offered, any positive number typed.</summary>
        private readonly ComboBox _cbDatum = new ComboBox { Width = 50, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckBox _chkMatchZones = ToolUi.Check("Cover zone lines (the layers set on Split views)");
        private readonly CheckBox _chkCreate = ToolUi.Check("Create missing role profiles (ground from its surface, top from the parts in the view)");
        private readonly TextBox _txtTopPrefix = new TextBox { Width = 120 };
        private readonly RadioButton _rbLeave = new RadioButton { Text = "Leave", AutoSize = true }, _rbHide = new RadioButton { Text = "Hide in the view", AutoSize = true }, _rbDelete = new RadioButton { Text = "Delete", AutoSize = true };
        private readonly CheckBox _chkLink = ToolUi.Check("Make the targets followers of the source view (Sync applies it again later)");

        // Templates, followers, check
        private readonly DataGridView _gridTemplates = ToolUi.Grid(), _gridFollowers = ToolUi.Grid(), _gridCheck = ToolUi.Grid();
        private readonly RadioButton _rbCheckAll = new RadioButton { Text = "Every profile view in the drawing", AutoSize = true, Checked = true };
        private readonly RadioButton _rbCheckTargets = new RadioButton { Text = "The views ticked on Match views", AutoSize = true };
        private List<ViewIssue> _issues = new List<ViewIssue>();

        // Split
        private readonly ComboBox _cbSplitView = ToolUi.Combo(420);
        private readonly RadioButton _rbEvery = new RadioButton { Text = "Every", AutoSize = true }, _rbFitSheet = new RadioButton { Text = "What fits the viewport of the sheet", AutoSize = true };
        private readonly TextBox _txtLength = ToolUi.Number(450, 70), _txtRound = ToolUi.Number(10, 50), _txtOverlap = ToolUi.Number(0, 50), _txtMinLast = ToolUi.Number(100, 60);
        private readonly ComboBox _cbViewportSheet = ToolUi.Combo(420), _cbSheetTemplate = ToolUi.Combo(420);
        private readonly Label _lblFit = ToolUi.Note("");
        private readonly RadioButton _rbBelow = new RadioButton { Text = "One under another, below the view", AutoSize = true }, _rbRight = new RadioButton { Text = "In a row, right of the view", AutoSize = true };
        private readonly TextBox _txtGap = ToolUi.Number(30, 50), _txtName = new TextBox { Width = 260 }, _txtFirstSheet = new TextBox { Width = 420 };
        private readonly CheckBox _chkTrim = ToolUi.Check("Draw only the parts in each part's stations (pipes, structures, crossings, pressure parts)");
        private readonly CheckBox _chkSplitLabels = ToolUi.Check("Copy the view's part labels onto them");
        private readonly CheckBox _chkSplitFit = ToolUi.Check("Fit each part's elevation range to its own profiles and parts");
        private readonly CheckBox _chkDeleteSource = ToolUi.Check("Delete the long view afterwards");
        private readonly CheckBox _chkSheets = ToolUi.Check("Create a profile sheet for each part, from this sheet:");
        private readonly CheckBox _chkPartTitle = ToolUi.Check("Number the \"PART n\" in each sheet's title");
        private readonly TextBox _txtSheetNo = ToolUi.Number(1, 50);
        private readonly CheckBox _chkSplitZones = ToolUi.Check("Mark cover zones on each part: a line where the alignment enters and leaves");
        private readonly TextBox _txtZoneLayers = new TextBox { Width = 220 }, _txtZoneLabel = new TextBox { Width = 120 };
        private readonly DataGridView _gridSplit = ToolUi.Grid();
        private List<SplitPart> _plan;

        public FormProfileViewTools()
        {
            Text = "C3D Pro Tools - Profile View Tools";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(900, 640);
            ClientSize = new Size(_s.Width >= 900 ? _s.Width : 1040, _s.Height >= 640 ? _s.Height : 780);

            ReadDrawing();
            _nav.Add("Match views", "Style by example, by role");
            _nav.Add("Templates", "Saved finished views");
            _nav.Add("Followers", "Views that follow a master");
            _nav.Add("Health check", "Bands, profiles, ranges");
            _nav.Add("Split view", "Long view into sheet parts");
            _pages.Add(BuildMatch());
            _pages.Add(BuildTemplates());
            _pages.Add(BuildFollowers());
            _pages.Add(BuildCheck());
            _pages.Add(BuildSplit());
            foreach (Control p in _pages) { p.Visible = false; _content.Controls.Add(p); }

            Controls.Add(_content);
            Controls.Add(_nav);
            Controls.Add(ToolUi.Header("Profile View Tools", "Finish one profile view, then make the others like it; split long views into sheet parts.",
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _nav.SelectedChanged += (s, e) => ShowPage();
            _nav.Selected = Math.Max(0, Math.Min(_pages.Count - 1, _s.Page));
            ShowPage();
            FormClosing += (s, e) => { SaveSettings(); _s.Width = ClientSize.Width; _s.Height = ClientSize.Height; _s.Save(); };
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Panel, Padding = new Padding(12, 6, 12, 8) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            _btnRun = StyleUi.Primary("Run…", (s, e) => Run());
            _btnRun.MinimumSize = new Size(180, 34);
            footer.Controls.Add(_lblStatus, 0, 0);
            footer.Controls.Add(_btnRun, 1, 0);
            return footer;
        }

        private static Label Muted(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) };

        private Control BuildMatch()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel src = ToolUi.Card("Source", "The finished view (or a template saved from one). Its view style, bands, profile styles and labels are read by role.");
            ToolUi.Wide(src, ToolUi.Inline(_rbFromView, _cbSource, StyleUi.Secondary("Pick…", (s, e) => PickSource())));
            ToolUi.Wide(src, ToolUi.Inline(_rbFromTemplate, _cbTemplate, StyleUi.Secondary("Save the source view as a template…", (s, e) => SaveTemplate())));
            ToolUi.Add(stack, src);

            TableLayoutPanel roles = ToolUi.Card("Roles",
                "Every view shows three profiles: the existing and the proposed ground, told by the surface they are sampled from (checked against the " +
                "surfaces, whatever the profile is called), and the top of pipe / duct, told by a word in its name.");
            ToolUi.Field(roles, "Existing ground", _cbExisting);
            ToolUi.Field(roles, "Proposed ground", _cbProposed);
            ToolUi.Field(roles, "Top of pipe / duct", ToolUi.Inline(_txtTopWords, Muted("words in the name, comma separated (TOP, TOD)")));
            ToolUi.Wide(roles, _lblRoles);
            ToolUi.Add(stack, roles);

            TableLayoutPanel targets = ToolUi.Card("Targets");
            ToolUi.Wide(targets, _clbTargets);
            ToolUi.Wide(targets, ToolUi.Inline(StyleUi.Secondary("Pick in the drawing…", (s, e) => PickTargets()),
                StyleUi.Secondary("All", (s, e) => SetAllTargets(true)), StyleUi.Secondary("None", (s, e) => SetAllTargets(false)),
                StyleUi.Secondary("The source's followers", (s, e) => TickFollowers())));
            ToolUi.Add(stack, targets);

            TableLayoutPanel what = ToolUi.Card("What to match");
            ToolUi.Wide(what, _chkViewStyle);
            ToolUi.Wide(what, _chkBands);
            ToolUi.Wide(what, _chkProfileStyles);
            ToolUi.Wide(what, _chkLabels);
            ToolUi.Wide(what, ToolUi.Inline(_chkElevation, _txtMargin, Muted("m above and below, rounded to whole"), _cbDatum, Muted("m")));
            ToolUi.Wide(what, _chkInView);
            ToolUi.Wide(what, _chkMatchZones);
            ToolUi.Add(stack, what);

            TableLayoutPanel profiles = ToolUi.Card("Profiles", "You are asked about these again at the start of each run.");
            ToolUi.Wide(profiles, _chkCreate);
            ToolUi.Field(profiles, "New top profiles", ToolUi.Inline(_txtTopPrefix, Muted("+ the view's name")));
            ToolUi.Field(profiles, "Other profiles", ToolUi.Inline(_rbLeave, _rbHide, _rbDelete), _tips,
                "Profiles of no role, and second profiles of a role (\"… (1)\" copies). Delete keeps profiles that other views' bands use, and data references: those are hidden.");
            ToolUi.Wide(profiles, _chkLink);
            ToolUi.Add(stack, profiles);

            ToolUi.Exclusive(_rbFromView, _rbFromTemplate);
            ToolUi.Exclusive(_rbLeave, _rbHide, _rbDelete);
            _cbSource.SelectedIndexChanged += (s, e) => { _rbFromView.Checked = true; ShowSourceRoles(); };
            _cbTemplate.SelectedIndexChanged += (s, e) => { if (_cbTemplate.SelectedIndex >= 0) _rbFromTemplate.Checked = true; ShowSourceRoles(); };
            _cbExisting.SelectedIndexChanged += (s, e) => ShowSourceRoles();
            _cbProposed.SelectedIndexChanged += (s, e) => ShowSourceRoles();
            _txtTopWords.Leave += (s, e) => ShowSourceRoles();
            _clbTargets.ItemCheck += (s, e) => BeginInvoke((Action)UpdateStatus);
            return host;
        }

        private Control BuildTemplates()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Saved templates",
                "A template is a finished view recorded by role. It is kept with your settings, so it works in any drawing with the same style names. " +
                "Apply one from here to the views ticked on Match views, or choose it as the source there.");
            GridSize(_gridTemplates, 300);
            foreach ((string name, string header, int w) in new[] { ("name", "Template", 30), ("source", "Recorded from", 30), ("when", "Saved", 15), ("what", "Contents", 60) })
                _gridTemplates.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, SortMode = DataGridViewColumnSortMode.NotSortable });
            ToolUi.Wide(card, _gridTemplates);
            ToolUi.Wide(card, ToolUi.Inline(StyleUi.Secondary("Rename…", (s, e) => RenameTemplate()), StyleUi.Secondary("Delete", (s, e) => DeleteTemplate())));
            ToolUi.Add(stack, card);
            return host;
        }

        private Control BuildFollowers()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Followers",
                "A follower remembers its master view. After you change the master, Sync makes every follower like it again, with the choices on Match views.");
            GridSize(_gridFollowers, 330);
            foreach ((string name, string header, int w) in new[] { ("view", "View", 50), ("master", "Follows", 50) })
                _gridFollowers.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, SortMode = DataGridViewColumnSortMode.Automatic });
            ToolUi.Wide(card, _gridFollowers);
            ToolUi.Wide(card, ToolUi.Inline(StyleUi.Secondary("Sync the selected…", (s, e) => SyncFollowers(true)),
                StyleUi.Secondary("Unlink the selected", (s, e) => UnlinkFollowers()), StyleUi.Secondary("Refresh", (s, e) => { ReadDrawing(); FillFollowers(); })));
            ToolUi.Add(stack, card);
            return host;
        }

        private Control BuildCheck()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel scope = ToolUi.Card("Views to check", "Uses the roles on Match views. Finds bands showing a profile of another alignment (they draw nothing), " +
                "bands with no profile, missing role profiles, second profiles of a role, and elevation ranges that cut off profiles or parts.");
            ToolUi.Wide(scope, _rbCheckAll);
            ToolUi.Wide(scope, _rbCheckTargets);
            ToolUi.Add(stack, scope);
            TableLayoutPanel card = ToolUi.Card("Problems");
            GridSize(_gridCheck, 330);
            _gridCheck.Columns.Add(new DataGridViewCheckBoxColumn { Name = "fix", HeaderText = "Fix", FillWeight = 6 });
            foreach ((string name, string header, int w) in new[] { ("view", "View", 26), ("problem", "Problem", 50), ("action", "Fix", 26) })
                _gridCheck.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
            _gridCheck.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _gridCheck.Rows[e.RowIndex].Tag is ViewIssue i) ZoomTo(i.ViewId); };
            ToolUi.Wide(card, _gridCheck);
            ToolUi.Wide(card, ToolUi.Inline(StyleUi.Secondary("Fix the ticked", (s, e) => FixIssues()), StyleUi.Secondary("Zoom to the view", (s, e) =>
            {
                if (_gridCheck.CurrentRow?.Tag is ViewIssue i) ZoomTo(i.ViewId);
            }), Muted("Double-click a row to zoom to its view.")));
            ToolUi.Add(stack, card);
            ToolUi.Exclusive(_rbCheckAll, _rbCheckTargets);
            return host;
        }

        private Control BuildSplit()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel src = ToolUi.Card("Long view", "Each part is a new view of the same alignment with the long view's style, bands and profiles, " +
                "its own stations and elevation range, and the parts and labels that fall in its stations.");
            ToolUi.Wide(src, ToolUi.Inline(_cbSplitView, StyleUi.Secondary("Pick…", (s, e) => PickSplitView())));
            ToolUi.Add(stack, src);

            TableLayoutPanel len = ToolUi.Card("Length of each part");
            ToolUi.Wide(len, ToolUi.Inline(_rbEvery, _txtLength, Muted("m")));
            ToolUi.Wide(len, ToolUi.Inline(_rbFitSheet, _cbViewportSheet));
            ToolUi.Wide(len, _lblFit);
            ToolUi.Field(len, "Breaks at", ToolUi.Inline(_txtRound, Muted("m round stations;  overlap"), _txtOverlap, Muted("m;  a last part under"), _txtMinLast, Muted("m joins the one before")));
            ToolUi.Add(stack, len);

            TableLayoutPanel place = ToolUi.Card("Parts");
            ToolUi.Wide(place, ToolUi.Inline(_rbBelow, _rbRight, Muted("gap"), _txtGap, Muted("m")));
            ToolUi.Field(place, "Names", ToolUi.Inline(_txtName, Muted("{view} {n} {from} {to} {AlignmentName} {SheetNo}, first {SheetNo}"), _txtSheetNo));
            _tips.SetToolTip(_txtName, "Tokens: {view} or {ViewName}, {n} or {PartIndex}, {from} or {StartSta}, {to} or {EndSta}, {AlignmentName}, {SheetNo}. " +
                                       "Add a format after a colon, e.g. PROFILE SHEET {SheetNo:000} - PART - {PartIndex}. Stations follow the alignment's station equations.");
            ToolUi.Wide(place, _chkTrim);
            ToolUi.Wide(place, _chkSplitLabels);
            ToolUi.Wide(place, _chkSplitFit);
            ToolUi.Wide(place, _chkDeleteSource);
            ToolUi.Wide(place, _chkSplitZones);
            ToolUi.Field(place, "Zone polylines", ToolUi.Inline(_txtZoneLayers, Muted("layers (comma-separated, * allowed), labelled"), _txtZoneLabel), _tips,
                "Closed polylines on these layers (paving, roads, taxiways) are the zones; lines go on layer " + ProfileViewZones.Layer + ".");
            ToolUi.Wide(place, ToolUi.Note("Objects projected into the view (COGO points, blocks) cannot be projected by a .NET tool: the report lists how many fall in each part, " +
                                           "to project with Civil 3D's Project Objects to Profile View."));
            ToolUi.Add(stack, place);

            TableLayoutPanel sheets = ToolUi.Card("Sheets", "A copy of the sheet per part, after it in the tabs: the next numbers of its series, its viewport centred on the part at the sheet's scale.");
            ToolUi.Wide(sheets, ToolUi.Inline(_chkSheets, _cbSheetTemplate));
            ToolUi.Field(sheets, "First sheet name", ToolUi.Inline(_txtFirstSheet, StyleUi.Secondary("Next free", (s, e) => SuggestFirstSheet())));
            ToolUi.Wide(sheets, _chkPartTitle);
            ToolUi.Add(stack, sheets);

            TableLayoutPanel plan = ToolUi.Card("Plan");
            GridSize(_gridSplit, 200);
            foreach ((string name, string header, int w) in new[] { ("name", "Part", 40), ("stations", "Stations", 22), ("levels", "Elevations", 16), ("size", "Size (m)", 16), ("fits", "Fits the viewport", 14) })
                _gridSplit.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, SortMode = DataGridViewColumnSortMode.NotSortable });
            ToolUi.Wide(plan, _gridSplit);
            ToolUi.Wide(plan, ToolUi.Inline(StyleUi.Secondary("Show the plan", (s, e) => PlanSplit(true))));
            ToolUi.Add(stack, plan);

            ToolUi.Exclusive(_rbEvery, _rbFitSheet);
            ToolUi.Exclusive(_rbBelow, _rbRight);
            _rbFitSheet.CheckedChanged += (s, e) => UpdateFitLength();
            _cbViewportSheet.SelectedIndexChanged += (s, e) => { _rbFitSheet.Checked = true; UpdateFitLength(); };
            _cbSplitView.SelectedIndexChanged += (s, e) => UpdateFitLength();
            _cbSheetTemplate.SelectedIndexChanged += (s, e) => { _chkSheets.Checked = true; if (_txtFirstSheet.Text.Length == 0) SuggestFirstSheet(); };
            return host;
        }

        private static void GridSize(DataGridView g, int height)
        {
            g.Dock = DockStyle.None;
            g.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            g.Height = height;
            g.Width = 700;
        }

        private void ShowPage()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _nav.Selected;
            _btnRun.Text = RunText[_nav.Selected];
            if (_nav.Selected == PTemplates) FillTemplates();
            if (_nav.Selected == PFollowers) FillFollowers();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            int ticked = _clbTargets.CheckedItems.Count;
            _lblStatus.Text = _nav.Selected == PMatch || _nav.Selected == PTemplates ? $"{ticked} target view(s) ticked of {_views.Count}."
                : _nav.Selected == PFollowers ? $"{_views.Count(v => v.Master.Length > 0)} follower view(s)."
                : _nav.Selected == PCheck ? (_issues.Count > 0 ? $"{_issues.Count} problem(s) found." : "")
                : _plan != null ? $"{_plan.Count} part(s) planned." : "";
        }

        // =====================================================================================
        // Drawing and settings
        // =====================================================================================

        private void ReadDrawing()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            CivilDocument cdoc = CivilApplication.ActiveDocument;
            List<ViewItem> views = new List<ViewItem>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ProfileViewMatch.AllViews(tr, db))
                {
                    ProfileView v = (ProfileView)tr.GetObject(id, OpenMode.ForRead);
                    ViewItem item = new ViewItem { Id = id, Name = v.Name, Alignment = v.AlignmentName ?? "" };
                    ObjectId master = ProfileViewMatch.MasterOf(tr, db, id);
                    if (!master.IsNull && tr.GetObject(master, OpenMode.ForRead) is ProfileView m) item.Master = m.Name;
                    views.Add(item);
                }
                _surfaceNames = new ProfileViewMatch.Surfaces(tr, cdoc).All.Select(s => s.name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                tr.Commit();
            }
            _views = views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

            HashSet<ObjectId> ticked = new HashSet<ObjectId>(_clbTargets.CheckedItems.Cast<ViewItem>().Select(v => v.Id));
            ObjectId source = (_cbSource.SelectedItem as ViewItem)?.Id ?? ObjectId.Null, split = (_cbSplitView.SelectedItem as ViewItem)?.Id ?? ObjectId.Null;
            _clbTargets.Items.Clear(); _cbSource.Items.Clear(); _cbSplitView.Items.Clear();
            foreach (ViewItem v in _views)
            {
                _clbTargets.Items.Add(v, ticked.Contains(v.Id));
                _cbSource.Items.Add(v);
                _cbSplitView.Items.Add(v);
            }
            _cbSource.SelectedItem = _views.FirstOrDefault(v => v.Id == source);
            _cbSplitView.SelectedItem = _views.FirstOrDefault(v => v.Id == split);

            List<string> sheets = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SheetRecord r in SheetManager.Read(tr, db, SheetManagerSettings.Load().Profile ?? new SheetProfile()))
                    if (!r.ViewportId.IsNull) sheets.Add(r.Layout);
                tr.Commit();
            }
            string vs = _cbViewportSheet.SelectedItem as string, ts = _cbSheetTemplate.SelectedItem as string;
            _cbViewportSheet.Items.Clear(); _cbSheetTemplate.Items.Clear();
            foreach (string s in sheets) { _cbViewportSheet.Items.Add(s); _cbSheetTemplate.Items.Add(s); }
            ToolUi.Select(_cbViewportSheet, vs);
            ToolUi.Select(_cbSheetTemplate, ts);
        }

        private void ApplySettings()
        {
            foreach (string n in _surfaceNames) { _cbExisting.Items.Add(n); _cbProposed.Items.Add(n); }
            // Unknown surfaces stay unchosen: the source view's own surfaces are offered once it is chosen.
            _cbExisting.SelectedIndex = _cbExisting.Items.IndexOf(_s.Rules.ExistingSurface ?? "");
            _cbProposed.SelectedIndex = _cbProposed.Items.IndexOf(_s.Rules.ProposedSurface ?? "");
            _txtTopWords.Text = string.Join(", ", _s.Rules.TopWords ?? new List<string>());
            FillTemplateCombo();
            _chkViewStyle.Checked = _s.MatchViewStyle;
            _chkBands.Checked = _s.MatchBands;
            _chkProfileStyles.Checked = _s.MatchProfileStyles;
            _chkLabels.Checked = _s.MatchProfileLabels;
            _chkElevation.Checked = _s.MatchElevation;
            _chkInView.Checked = _s.StylesInView;
            _txtMargin.Text = ToolUi.Format(_s.Margin);
            if (_cbDatum.Items.Count == 0) _cbDatum.Items.AddRange(new object[] { "1", "2", "5" });
            _cbDatum.Text = ToolUi.Format(_s.DatumIncrement > 0 ? _s.DatumIncrement : 1);
            _chkMatchZones.Checked = _s.MatchZones;
            _chkCreate.Checked = _s.CreateMissing;
            _txtTopPrefix.Text = _s.TopPrefix;
            _rbLeave.Checked = _s.Others == OtherProfileAction.Leave;
            _rbHide.Checked = _s.Others == OtherProfileAction.Hide;
            _rbDelete.Checked = _s.Others == OtherProfileAction.Delete;
            _chkLink.Checked = _s.LinkFollowers;

            _txtLength.Text = ToolUi.Format(_s.SplitLength);
            _rbFitSheet.Checked = _s.SplitFitViewport;
            _rbEvery.Checked = !_s.SplitFitViewport;
            ToolUi.Select(_cbViewportSheet, _s.SplitViewportLayout);
            _txtRound.Text = ToolUi.Format(_s.SplitRound);
            _txtOverlap.Text = ToolUi.Format(_s.SplitOverlap);
            _txtMinLast.Text = ToolUi.Format(_s.SplitMinLast);
            _rbBelow.Checked = _s.SplitBelow;
            _rbRight.Checked = !_s.SplitBelow;
            _txtGap.Text = ToolUi.Format(_s.SplitGap);
            _txtName.Text = _s.SplitName;
            _chkTrim.Checked = _s.SplitTrimParts;
            _chkSplitLabels.Checked = _s.SplitLabels;
            _chkSplitFit.Checked = _s.SplitFit;
            _chkDeleteSource.Checked = _s.SplitDeleteSource;
            ToolUi.Select(_cbSheetTemplate, _s.SplitSheetTemplate);
            _chkSheets.Checked = _s.SplitSheets;
            _txtFirstSheet.Text = _s.SplitSheetFirstName;
            _chkPartTitle.Checked = _s.SplitSheetPartTitle;
            _txtSheetNo.Text = _s.SplitFirstSheetNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _chkSplitZones.Checked = _s.SplitZones;
            _txtZoneLayers.Text = _s.ZoneLayers;
            _txtZoneLabel.Text = _s.ZoneLabel;
        }

        private void SaveSettings()
        {
            _s.Page = _nav.Selected;
            _s.Rules = Rules();
            _s.MatchViewStyle = _chkViewStyle.Checked;
            _s.MatchBands = _chkBands.Checked;
            _s.MatchProfileStyles = _chkProfileStyles.Checked;
            _s.MatchProfileLabels = _chkLabels.Checked;
            _s.MatchElevation = _chkElevation.Checked;
            _s.StylesInView = _chkInView.Checked;
            if (ToolUi.TryNumber(_txtMargin, out double m) && m >= 0) _s.Margin = m;
            _s.DatumIncrement = Datum();
            _s.MatchZones = _chkMatchZones.Checked;
            _s.CreateMissing = _chkCreate.Checked;
            _s.TopPrefix = _txtTopPrefix.Text;
            _s.Others = _rbDelete.Checked ? OtherProfileAction.Delete : _rbHide.Checked ? OtherProfileAction.Hide : OtherProfileAction.Leave;
            _s.LinkFollowers = _chkLink.Checked;

            if (ToolUi.TryNumber(_txtLength, out double len) && len > 0) _s.SplitLength = len;
            _s.SplitFitViewport = _rbFitSheet.Checked;
            _s.SplitViewportLayout = _cbViewportSheet.SelectedItem as string ?? "";
            if (ToolUi.TryNumber(_txtRound, out double r) && r >= 0) _s.SplitRound = r;
            if (ToolUi.TryNumber(_txtOverlap, out double ov) && ov >= 0) _s.SplitOverlap = ov;
            if (ToolUi.TryNumber(_txtMinLast, out double ml) && ml >= 0) _s.SplitMinLast = ml;
            _s.SplitBelow = _rbBelow.Checked;
            if (ToolUi.TryNumber(_txtGap, out double gap) && gap >= 0) _s.SplitGap = gap;
            _s.SplitName = _txtName.Text;
            _s.SplitTrimParts = _chkTrim.Checked;
            _s.SplitLabels = _chkSplitLabels.Checked;
            _s.SplitFit = _chkSplitFit.Checked;
            _s.SplitDeleteSource = _chkDeleteSource.Checked;
            _s.SplitSheets = _chkSheets.Checked;
            _s.SplitSheetTemplate = _cbSheetTemplate.SelectedItem as string ?? "";
            _s.SplitSheetFirstName = _txtFirstSheet.Text;
            _s.SplitSheetPartTitle = _chkPartTitle.Checked;
            if (int.TryParse(_txtSheetNo.Text, out int sheetNo)) _s.SplitFirstSheetNo = sheetNo;
            _s.SplitZones = _chkSplitZones.Checked;
            _s.ZoneLayers = _txtZoneLayers.Text.Trim();
            _s.ZoneLabel = _txtZoneLabel.Text.Trim();
        }

        /// <summary>The datum increment typed or picked (1 m when not a positive number).</summary>
        private double Datum() =>
            double.TryParse(_cbDatum.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0 ? d : 1.0;

        private string ZoneLabel() => string.IsNullOrWhiteSpace(_txtZoneLabel.Text) ? "zone" : _txtZoneLabel.Text.Trim();

        private ProfileRoleRules Rules() => new ProfileRoleRules
        {
            ExistingSurface = _cbExisting.SelectedItem as string ?? "",
            ProposedSurface = _cbProposed.SelectedItem as string ?? "",
            TopWords = _txtTopWords.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).Where(w => w.Length > 0).ToList()
        };

        private MatchOptions Options()
        {
            ToolUi.TryNumber(_txtMargin, out double margin);
            return new MatchOptions
            {
                ViewStyle = _chkViewStyle.Checked, Bands = _chkBands.Checked, ProfileStyles = _chkProfileStyles.Checked, ProfileLabels = _chkLabels.Checked,
                Elevation = _chkElevation.Checked, StylesInView = _chkInView.Checked, CreateMissing = _chkCreate.Checked,
                Others = _rbDelete.Checked ? OtherProfileAction.Delete : _rbHide.Checked ? OtherProfileAction.Hide : OtherProfileAction.Leave,
                Margin = Math.Max(0, margin), DatumIncrement = Datum(), TopPrefix = _txtTopPrefix.Text
            };
        }

        // =====================================================================================
        // Picking
        // =====================================================================================

        private ObjectId PickView(string prompt)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                PromptEntityOptions peo = new PromptEntityOptions("\n" + prompt);
                peo.SetRejectMessage("\nSelect a profile view.");
                peo.AddAllowedClass(typeof(ProfileView), false);
                PromptEntityResult r = doc.Editor.GetEntity(peo);
                return r.Status == PromptStatus.OK ? r.ObjectId : ObjectId.Null;
            }
            finally { Show(); }
        }

        private void PickSource()
        {
            ObjectId id = PickView("Select the finished profile view: ");
            if (id.IsNull) return;
            _cbSource.SelectedItem = _views.FirstOrDefault(v => v.Id == id);
        }

        private void PickSplitView()
        {
            ObjectId id = PickView("Select the long profile view: ");
            if (!id.IsNull) _cbSplitView.SelectedItem = _views.FirstOrDefault(v => v.Id == id);
        }

        private void PickTargets()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            List<ObjectId> picked = new List<ObjectId>();
            Hide();
            try
            {
                PromptSelectionResult r = doc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect the profile views to make like the source (other objects are ignored): " });
                if (r.Status == PromptStatus.OK) picked.AddRange(r.Value.GetObjectIds());
            }
            finally { Show(); }
            if (picked.Count == 0) return;
            for (int i = 0; i < _clbTargets.Items.Count; i++)
                if (picked.Contains(((ViewItem)_clbTargets.Items[i]).Id)) _clbTargets.SetItemChecked(i, true);
            UpdateStatus();
        }

        private void SetAllTargets(bool on)
        {
            ObjectId source = (_cbSource.SelectedItem as ViewItem)?.Id ?? ObjectId.Null;
            for (int i = 0; i < _clbTargets.Items.Count; i++) _clbTargets.SetItemChecked(i, on && ((ViewItem)_clbTargets.Items[i]).Id != source);
            UpdateStatus();
        }

        private void TickFollowers()
        {
            if (!(_cbSource.SelectedItem is ViewItem source)) { MessageBox.Show(this, "Choose the source view first.", Text); return; }
            for (int i = 0; i < _clbTargets.Items.Count; i++) _clbTargets.SetItemChecked(i, ((ViewItem)_clbTargets.Items[i]).Master == source.Name);
            UpdateStatus();
        }

        private List<ViewItem> Targets() => _clbTargets.CheckedItems.Cast<ViewItem>().ToList();

        private void ZoomTo(ObjectId viewId)
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!viewId.IsErased && tr.GetObject(viewId, OpenMode.ForRead) is ProfileView v) ViewHelper.ZoomTo(v.GeometricExtents, 1.1, 20);
                tr.Commit();
            }
        }

        // =====================================================================================
        // Match
        // =====================================================================================

        /// <summary>The source's roles as read with the current rules, so a wrong surface choice shows at once.</summary>
        private void ShowSourceRoles()
        {
            if (_rbFromTemplate.Checked && TemplateChosen() is ProfileViewRecipe t)
            {
                _lblRoles.Text = $"Template \"{t.Name}\": {t.Summary}.";
                return;
            }
            if (!(_cbSource.SelectedItem is ViewItem source)) { _lblRoles.Text = "Choose the source view to see its profiles by role."; return; }
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, CivilApplication.ActiveDocument);
                ProfileView v = (ProfileView)tr.GetObject(source.Id, OpenMode.ForRead);
                // First use: the surfaces the source's ground profiles come from are offered as existing / proposed.
                if (_cbExisting.SelectedIndex < 0 || _cbProposed.SelectedIndex < 0)
                {
                    List<string> shown = ProfileViewMatch.SurfacesShown(tr, v, surfaces);
                    string eg = shown.FirstOrDefault(n => System.Text.RegularExpressions.Regex.IsMatch(n, "NGL|EXIST|\\bEG\\b|GROUND|SURVEY", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (_cbExisting.SelectedIndex < 0 && eg != null) ToolUi.Select(_cbExisting, eg);
                    string fg = shown.FirstOrDefault(n => n != (_cbExisting.SelectedItem as string));
                    if (_cbProposed.SelectedIndex < 0 && fg != null) ToolUi.Select(_cbProposed, fg);
                }
                List<ViewProfile> profiles = ProfileViewMatch.Classify(tr, v, Rules(), surfaces);
                List<string> parts = new List<string>();
                foreach (ProfileRole r in ProfileViewMatch.Roles)
                {
                    ViewProfile p = profiles.FirstOrDefault(x => x.Role == r && x.Chosen);
                    parts.Add($"{ProfileViewMatch.RoleName(r)}: {(p == null ? "NOT FOUND" : p.Name)}");
                }
                int others = profiles.Count(x => !x.Chosen);
                _lblRoles.Text = string.Join("   ·   ", parts) + (others > 0 ? $"   ·   {others} other profile(s)" : "");
                tr.Commit();
            }
        }

        private ProfileViewRecipe TemplateChosen() => _s.Templates.FirstOrDefault(t => t.Name == _cbTemplate.SelectedItem as string);

        /// <summary>The recipe to apply: the source view read now, or the chosen template (with the page's roles).</summary>
        private ProfileViewRecipe Recipe(out string problem)
        {
            problem = null;
            if (_rbFromTemplate.Checked)
            {
                ProfileViewRecipe t = TemplateChosen();
                if (t == null) { problem = "Choose a saved template (or a source view)."; return null; }
                ProfileViewRecipe copy = System.Text.Json.JsonSerializer.Deserialize<ProfileViewRecipe>(System.Text.Json.JsonSerializer.Serialize(t));
                copy.Rules = Rules();
                return copy;
            }
            if (!(_cbSource.SelectedItem is ViewItem source)) { problem = "Choose the source view (the finished one)."; return null; }
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, CivilApplication.ActiveDocument);
                ProfileViewRecipe r = ProfileViewMatch.Capture(tr, (ProfileView)tr.GetObject(source.Id, OpenMode.ForRead), Rules(), surfaces);
                tr.Commit();
                if (r.Roles.Count == 0) problem = "None of the source view's profiles has a role: check the surfaces and the top words under Roles.";
                return r;
            }
        }

        private void RunMatch(List<ViewItem> targets, ProfileViewRecipe recipe, ObjectId master)
        {
            if (targets.Count == 0) { MessageBox.Show(this, "Tick the views to change under Targets.", Text); return; }
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            CivilDocument cdoc = CivilApplication.ActiveDocument;
            MatchOptions o = Options();

            // A first dry run counts what is missing and extra, for the questions asked before anything changes.
            List<MatchChange> changes = new List<MatchChange>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, cdoc);
                MatchOptions ask = new MatchOptions { ViewStyle = false, Bands = false, ProfileStyles = false, ProfileLabels = false, Elevation = false, CreateMissing = true, Others = OtherProfileAction.Hide };
                foreach (ViewItem t in targets) ProfileViewMatch.Apply(tr, cdoc, recipe, t.Id, ask, surfaces, false, changes, new List<string>());
                tr.Commit();
            }
            int missing = changes.Count(c => c.From == "missing" && !c.To.StartsWith("cannot")), others = changes.Count(c => c.To == "hide");
            if ((missing > 0 || others > 0) && !AskProfiles(missing, others, o)) return;

            changes.Clear();
            List<string> log = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, cdoc);
                foreach (ViewItem t in targets) ProfileViewMatch.Apply(tr, cdoc, recipe, t.Id, o, surfaces, false, changes, log);
                tr.Commit();
            }
            List<PreviewRow> rows = changes.Select(c => new PreviewRow { Cells = new[] { c.View, c.What, c.From, c.To }, Tag = c.View }).ToList();
            if (rows.Count == 0 && master.IsNull) { MessageBox.Show(this, "The ticked views already match the source.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            HashSet<string> chosenViews = new HashSet<string>(targets.Select(t => t.Name));
            if (rows.Count > 0)
            {
                using (FormStylePreview preview = new FormStylePreview("Match profile views",
                    $"{changes.Select(c => c.View).Distinct().Count()} of the {targets.Count} view(s) change. Untick a view's rows to leave that view out. One UNDO reverses the whole run.",
                    new[] { "View", "What", "Now", "Becomes" }, rows, "Apply", false, "Profile View Tools - Match"))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) return;
                    HashSet<object> chosen = new HashSet<object>(preview.Chosen);
                    foreach (PreviewRow r in rows) if (!chosen.Contains(r.Tag)) chosenViews.Remove((string)r.Tag);
                }
            }
            List<ViewItem> apply = targets.Where(t => chosenViews.Contains(t.Name)).ToList();

            changes.Clear();
            log.Clear();
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, cdoc);
                    List<IReadOnlyList<(double X, double Y)>> zones = _chkMatchZones.Checked && _txtZoneLayers.Text.Trim().Length > 0
                        ? ProfileViewZones.ReadZones(tr, db, _txtZoneLayers.Text.Trim()) : null;
                    if (_chkMatchZones.Checked && zones == null) log.Add("Cover zone lines: no zone layers are set (on Split views).");
                    foreach (ViewItem t in apply)
                    {
                        try
                        {
                            ProfileViewMatch.Apply(tr, cdoc, recipe, t.Id, o, surfaces, true, changes, log);
                            if (zones != null) ProfileViewZones.Mark(tr, db, t.Id, zones, ZoneLabel());
                            if (!master.IsNull && t.Id != master) ProfileViewMatch.Link(tr, t.Id, master);
                        }
                        catch (Exception ex) { log.Add($"{t.Name}: {ex.Message}"); }
                    }
                    tr.Commit();
                }
                ViewHelper.Regen(doc);
            }
            catch (Exception ex) { MessageBox.Show(this, "Matching stopped with an error and nothing was changed.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            ReadDrawing();
            Report($"Matched {apply.Count} view(s): {changes.Count} change(s)" + (!master.IsNull ? ", each now following the source." : "."), log);
        }

        /// <summary>The questions asked at the start of a run: create the missing role profiles, and what to do with the others.</summary>
        private bool AskProfiles(int missing, int others, MatchOptions o)
        {
            using (Form f = FormPartsLists.SmallDialog("Profiles in the target views", out TableLayoutPanel body, out Button ok))
            {
                CheckBox create = ToolUi.Check($"Create the {missing} missing role profile(s)", o.CreateMissing);
                RadioButton leave = new RadioButton { Text = "Leave them", AutoSize = true, Checked = o.Others == OtherProfileAction.Leave };
                RadioButton hide = new RadioButton { Text = "Hide them in the view", AutoSize = true, Checked = o.Others == OtherProfileAction.Hide };
                RadioButton delete = new RadioButton { Text = "Delete them", AutoSize = true, Checked = o.Others == OtherProfileAction.Delete };
                if (missing > 0) ToolUi.Wide(body, create);
                if (others > 0)
                {
                    ToolUi.Wide(body, new Label { Text = $"{others} profile(s) have no role (or are a second profile of one):", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
                    FlowLayoutPanel radios = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, Margin = new Padding(12, 0, 0, 0) };
                    radios.Controls.AddRange(new Control[] { leave, hide, delete });
                    ToolUi.Wide(body, radios);
                    ToolUi.Exclusive(leave, hide, delete);
                }
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                o.CreateMissing = missing > 0 ? create.Checked : o.CreateMissing;
                if (others > 0) o.Others = delete.Checked ? OtherProfileAction.Delete : hide.Checked ? OtherProfileAction.Hide : OtherProfileAction.Leave;
                _chkCreate.Checked = o.CreateMissing;
                _rbLeave.Checked = o.Others == OtherProfileAction.Leave; _rbHide.Checked = o.Others == OtherProfileAction.Hide; _rbDelete.Checked = o.Others == OtherProfileAction.Delete;
                return true;
            }
        }

        private void Report(string what, List<string> log)
        {
            _lblStatus.Text = what;
            string text = what + (log.Count > 0 ? "\n\n" + string.Join("\n", log.Take(30)) + (log.Count > 30 ? $"\n… and {log.Count - 30} more on the command line." : "") : "");
            Editor ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
            foreach (string l in log) ed.WriteMessage("\n" + l);
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================================
        // Templates and followers
        // =====================================================================================

        private void FillTemplateCombo(string select = null)
        {
            _cbTemplate.Items.Clear();
            foreach (ProfileViewRecipe t in _s.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) _cbTemplate.Items.Add(t.Name);
            if (select != null) ToolUi.Select(_cbTemplate, select);
        }

        private void FillTemplates()
        {
            _gridTemplates.Rows.Clear();
            foreach (ProfileViewRecipe t in _s.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                int i = _gridTemplates.Rows.Add(t.Name, t.SourceView, t.Captured.ToString("yyyy-MM-dd"), t.Summary);
                _gridTemplates.Rows[i].Tag = t;
            }
        }

        private void SaveTemplate()
        {
            if (!(_cbSource.SelectedItem is ViewItem source)) { MessageBox.Show(this, "Choose the source view first.", Text); return; }
            ProfileViewRecipe r = Recipe(out string problem);
            if (r == null || problem != null) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string name = FormPartsLists.AskText(this, "Save profile view template", "Name of the template:", source.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            r.Name = name.Trim();
            int i = _s.Templates.FindIndex(t => t.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (MessageBox.Show(this, $"Replace the template \"{_s.Templates[i].Name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _s.Templates[i] = r;
            }
            else _s.Templates.Add(r);
            _s.Save();
            FillTemplateCombo(r.Name);
            _rbFromView.Checked = true;
            MessageBox.Show(this, $"Saved \"{r.Name}\": {r.Summary}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RenameTemplate()
        {
            if (!(_gridTemplates.CurrentRow?.Tag is ProfileViewRecipe t)) return;
            string name = FormPartsLists.AskText(this, "Rename template", "New name:", t.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (_s.Templates.Any(x => x != t && x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "A template has that name.", Text); return; }
            t.Name = name.Trim();
            _s.Save();
            FillTemplates();
            FillTemplateCombo();
        }

        private void DeleteTemplate()
        {
            if (!(_gridTemplates.CurrentRow?.Tag is ProfileViewRecipe t)) return;
            if (MessageBox.Show(this, $"Delete the template \"{t.Name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _s.Templates.Remove(t);
            _s.Save();
            FillTemplates();
            FillTemplateCombo();
        }

        private void FillFollowers()
        {
            _gridFollowers.Rows.Clear();
            foreach (ViewItem v in _views.Where(v => v.Master.Length > 0))
            {
                int i = _gridFollowers.Rows.Add(v.Name, v.Master);
                _gridFollowers.Rows[i].Tag = v;
            }
        }

        private void SyncFollowers(bool selectedOnly)
        {
            List<ViewItem> followers = selectedOnly
                ? _gridFollowers.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as ViewItem).Where(v => v != null).ToList()
                : _views.Where(v => v.Master.Length > 0).ToList();
            if (followers.Count == 0) { MessageBox.Show(this, selectedOnly ? "Select follower rows first." : "No view follows a master yet: match views with \"Make the targets followers\" on.", Text); return; }
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            Dictionary<ObjectId, List<ViewItem>> byMaster = new Dictionary<ObjectId, List<ViewItem>>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ViewItem f in followers)
                {
                    ObjectId m = ProfileViewMatch.MasterOf(tr, db, f.Id);
                    if (m.IsNull) continue;
                    if (!byMaster.TryGetValue(m, out List<ViewItem> list)) byMaster[m] = list = new List<ViewItem>();
                    list.Add(f);
                }
                tr.Commit();
            }
            foreach (KeyValuePair<ObjectId, List<ViewItem>> g in byMaster)
            {
                ProfileViewRecipe recipe;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    recipe = ProfileViewMatch.Capture(tr, (ProfileView)tr.GetObject(g.Key, OpenMode.ForRead), Rules(), new ProfileViewMatch.Surfaces(tr, CivilApplication.ActiveDocument));
                    tr.Commit();
                }
                RunMatch(g.Value, recipe, g.Key);
            }
            FillFollowers();
        }

        private void UnlinkFollowers()
        {
            List<ViewItem> sel = _gridFollowers.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as ViewItem).Where(v => v != null).ToList();
            if (sel.Count == 0) return;
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewItem v in sel) ProfileViewMatch.Unlink(tr, v.Id);
                tr.Commit();
            }
            ReadDrawing();
            FillFollowers();
        }

        // =====================================================================================
        // Health check
        // =====================================================================================

        private void RunCheck()
        {
            List<ObjectId> views = _rbCheckTargets.Checked ? Targets().Select(t => t.Id).ToList() : _views.Select(v => v.Id).ToList();
            if (views.Count == 0) { MessageBox.Show(this, "There are no views to check.", Text); return; }
            ToolUi.TryNumber(_txtMargin, out double margin);
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            _issues = new List<ViewIssue>();
            Cursor = Cursors.WaitCursor;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileViewMatch.Surfaces surfaces = new ProfileViewMatch.Surfaces(tr, CivilApplication.ActiveDocument);
                    foreach (ObjectId id in views)
                        try { _issues.AddRange(ProfileViewMatch.Check(tr, CivilApplication.ActiveDocument, id, Rules(), surfaces, Math.Max(0, margin), Datum())); }
                        catch (Exception ex) { _issues.Add(new ViewIssue { ViewId = id, View = _views.FirstOrDefault(v => v.Id == id)?.Name ?? "?", Text = "Could not be checked: " + ex.Message }); }
                    tr.Commit();
                }
            }
            finally { Cursor = Cursors.Default; }
            _gridCheck.Rows.Clear();
            foreach (ViewIssue i in _issues)
            {
                int r = _gridCheck.Rows.Add(i.Fix != null, i.View, i.Text, i.FixText ?? "—");
                _gridCheck.Rows[r].Tag = i;
                if (i.Fix == null) _gridCheck.Rows[r].Cells["fix"].ReadOnly = true;
            }
            _lblStatus.Text = _issues.Count == 0 ? $"No problems in {views.Count} view(s)." : $"{_issues.Count} problem(s) in {_issues.Select(i => i.View).Distinct().Count()} of {views.Count} view(s).";
        }

        private void FixIssues()
        {
            _gridCheck.EndEdit();
            List<ViewIssue> fix = _gridCheck.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["fix"].Value is true && r.Tag is ViewIssue i && i.Fix != null).Select(r => (ViewIssue)r.Tag).ToList();
            if (fix.Count == 0) { MessageBox.Show(this, "Tick the problems to fix (only those with a fix can be ticked).", Text); return; }
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            List<string> log = new List<string>();
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewIssue i in fix)
                    try { i.Fix(tr); } catch (Exception ex) { log.Add($"{i.View}: {ex.Message}"); }
                tr.Commit();
            }
            ViewHelper.Regen(doc);
            RunCheck();
            Report($"Fixed {fix.Count - log.Count} problem(s).", log);
        }

        // =====================================================================================
        // Split
        // =====================================================================================

        /// <summary>The chosen sheet's viewport size in model units (0, 0 when none).</summary>
        private (double width, double height) ViewportSize(Transaction tr, Database db, string layout)
        {
            if (string.IsNullOrEmpty(layout)) return (0, 0);
            SheetRecord r = SheetManager.Read(tr, db, SheetManagerSettings.Load().Profile ?? new SheetProfile()).FirstOrDefault(s => s.Layout == layout);
            if (r == null || r.ViewportId.IsNull || !(tr.GetObject(r.ViewportId, OpenMode.ForRead) is Viewport vp) || vp.CustomScale <= 0) return (0, 0);
            return (vp.Width / vp.CustomScale, vp.Height / vp.CustomScale);
        }

        private void UpdateFitLength()
        {
            _lblFit.Text = "";
            if (!(_cbSplitView.SelectedItem is ViewItem v) || !(_cbViewportSheet.SelectedItem is string layout)) return;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    (double w, double h) = ViewportSize(tr, db, layout);
                    ProfileView pv = (ProfileView)tr.GetObject(v.Id, OpenMode.ForRead);
                    ProfileViewSplit.Frame f = ProfileViewSplit.Measure(pv);
                    ToolUi.TryNumber(_txtRound, out double round);
                    double len = ProfileViewSplit.LengthForWidth(f, w, round);
                    _lblFit.Text = w > 0 ? $"The viewport shows {w:0} × {h:0} m of the model: parts of up to {len:0} m fit across (the view's title and axes take {f.Left + f.Right:0.#} m)." : "That sheet has no scaled viewport.";
                    if (_rbFitSheet.Checked && len > 0) _txtLength.Text = ToolUi.Format(len);
                    tr.Commit();
                }
            }
            catch (Exception ex) { _lblFit.Text = ex.Message; }
        }

        private SplitOptions SplitChoices(out string problem)
        {
            problem = null;
            SplitOptions o = new SplitOptions { Rules = Rules(), Below = _rbBelow.Checked, NamePattern = _txtName.Text, TrimParts = _chkTrim.Checked, CopyLabels = _chkSplitLabels.Checked,
                FitElevation = _chkSplitFit.Checked, DeleteSource = _chkDeleteSource.Checked, Sheets = _chkSheets.Checked, SheetTemplate = _cbSheetTemplate.SelectedItem as string ?? "",
                FirstSheetName = _txtFirstSheet.Text.Trim(), NumberPartTitle = _chkPartTitle.Checked, DatumIncrement = Datum(),
                ZoneLayers = _chkSplitZones.Checked ? _txtZoneLayers.Text.Trim() : "", ZoneLabel = ZoneLabel() };
            o.FirstSheetNo = int.TryParse(_txtSheetNo.Text, out int firstNo) ? firstNo : 1;
            if (!ToolUi.TryNumber(_txtLength, out o.Length) || o.Length <= 0) problem = "Enter the length of each part.";
            else if (!ToolUi.TryNumber(_txtRound, out o.Round) || o.Round < 0) problem = "Enter the station rounding (0 for none).";
            else if (!ToolUi.TryNumber(_txtOverlap, out o.Overlap) || o.Overlap < 0 || o.Overlap >= o.Length) problem = "Enter an overlap smaller than the part length.";
            else if (!ToolUi.TryNumber(_txtMinLast, out o.MinLast) || o.MinLast < 0) problem = "Enter the shortest last part.";
            else if (!ToolUi.TryNumber(_txtGap, out o.Gap) || o.Gap < 0) problem = "Enter the gap between parts.";
            else if (o.Sheets && o.SheetTemplate.Length == 0) problem = "Choose the sheet to copy for each part.";
            else if (_chkSplitZones.Checked && _txtZoneLayers.Text.Trim().Length == 0) problem = "Enter the layers of the zone polylines, or untick the cover zones.";
            ToolUi.TryNumber(_txtMargin, out o.Margin);
            return o;
        }

        private bool PlanSplit(bool show)
        {
            if (!(_cbSplitView.SelectedItem is ViewItem v)) { MessageBox.Show(this, "Choose the long view.", Text); return false; }
            SplitOptions o = SplitChoices(out string problem);
            if (problem != null) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            if (_rbFitSheet.Checked) UpdateFitLength();
            ToolUi.TryNumber(_txtLength, out o.Length);
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    (o.ViewportWidth, o.ViewportHeight) = ViewportSize(tr, db, _rbFitSheet.Checked ? _cbViewportSheet.SelectedItem as string : (o.Sheets ? o.SheetTemplate : null));
                    _plan = ProfileViewSplit.Plan(tr, (ProfileView)tr.GetObject(v.Id, OpenMode.ForRead), o, new ProfileViewMatch.Surfaces(tr, CivilApplication.ActiveDocument));
                    tr.Commit();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "The plan could not be made: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            _gridSplit.Rows.Clear();
            foreach (SplitPart p in _plan)
            {
                int i = _gridSplit.Rows.Add(p.Name, $"{ProfileViewSplit.Station(p.S0)} – {ProfileViewSplit.Station(p.S1)}", $"{p.Low:0} – {p.High:0}",
                    $"{p.Width:0} × {p.Height:0}", o.ViewportWidth > 0 ? (p.Fits ? "Yes" : "NO") : "—");
                if (!p.Fits) _gridSplit.Rows[i].DefaultCellStyle.ForeColor = Color.Firebrick;
            }
            UpdateStatus();
            return true;
        }

        private void RunSplit()
        {
            if (!PlanSplit(true)) return;
            ViewItem v = (ViewItem)_cbSplitView.SelectedItem;
            SplitOptions o = SplitChoices(out _);
            ToolUi.TryNumber(_txtLength, out o.Length);
            int tooBig = _plan.Count(p => !p.Fits);
            if (MessageBox.Show(this, $"Split {v.Name} into {_plan.Count} view(s)" + (o.Sheets ? $" with a sheet each (copies of {o.SheetTemplate})" : "") + "?" +
                                      (tooBig > 0 ? $"\n\n{tooBig} part(s) do not fit the viewport (red in the plan)." : "") +
                                      (o.DeleteSource ? "\n\nThe long view is deleted afterwards." : ""), Text, MessageBoxButtons.OKCancel,
                                      tooBig > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            List<string> log = new List<string>();
            try
            {
                using (doc.LockDocument()) ProfileViewSplit.Apply(doc.Database, CivilApplication.ActiveDocument, v.Id, _plan, o, log);
                ViewHelper.Regen(doc);
            }
            catch (Exception ex) { log.Add("Stopped: " + ex.Message); }
            ReadDrawing();
            Report($"Created {_plan.Count(p => !p.ViewId.IsNull)} view(s)" + (o.Sheets ? $" and {_plan.Count(p => p.Sheet.Length > 0)} sheet(s)." : "."), log);
        }

        private void SuggestFirstSheet()
        {
            if (!(_cbSheetTemplate.SelectedItem is string template)) return;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            SheetProfile profile = SheetManagerSettings.Load().Profile ?? new SheetProfile();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<SheetRecord> sheets = SheetManager.Read(tr, db, profile);
                long n = SheetManager.NumberOf(template, profile);
                _txtFirstSheet.Text = n >= 0 ? SheetManager.NextFreeName(template, n + 1, sheets, null, profile) : template + " (2)";
                tr.Commit();
            }
        }

        // =====================================================================================
        // Running
        // =====================================================================================

        private void Run()
        {
            SaveSettings();
            try
            {
                switch (_nav.Selected)
                {
                    case PMatch:
                    case PTemplates:
                        {
                            if (_nav.Selected == PTemplates)
                            {
                                if (!(_gridTemplates.CurrentRow?.Tag is ProfileViewRecipe t)) { MessageBox.Show(this, "Select a template.", Text); return; }
                                _cbTemplate.SelectedItem = t.Name;
                                _rbFromTemplate.Checked = true;
                            }
                            ProfileViewRecipe recipe = Recipe(out string problem);
                            if (recipe == null || (problem != null && recipe.Roles.Count == 0)) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                            ObjectId master = _rbFromView.Checked && _chkLink.Checked && _cbSource.SelectedItem is ViewItem s ? s.Id : ObjectId.Null;
                            List<ViewItem> targets = Targets().Where(t => !(_rbFromView.Checked && _cbSource.SelectedItem is ViewItem src && t.Id == src.Id)).ToList();
                            RunMatch(targets, recipe, master);
                            break;
                        }
                    case PFollowers: SyncFollowers(false); break;
                    case PCheck: RunCheck(); break;
                    case PSplit: RunSplit(); break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The task stopped with an error.\n\n{ex.GetType().Name}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateStatus();
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Match views: finish one profile view, choose it as the source, tick the other views and apply. Profiles are matched by role, not by " +
                "name: the existing and proposed ground by the surface each is sampled from, the top of pipe / duct by a word in its name. Bands show " +
                "the same roles as in the source, on each view's own profiles. At the start you are asked whether to create missing role profiles and " +
                "what to do with the others; then every change is listed before it is made, and one UNDO reverses it.\n\n" +
                "Templates: save a finished view as a template and apply it in any drawing with the same style names.\n\n" +
                "Followers: views matched with \"Make the targets followers\" remember their source; after changing the source, Sync makes them like it again.\n\n" +
                "Health check: finds bands pointing at another alignment's profiles (a copied view's usual fault), missing or doubled role profiles " +
                "and elevation ranges that cut off profiles or parts, and fixes the ticked ones.\n\n" +
                "Split view: breaks a long view into parts of a set length or of what a sheet's viewport holds, each with its own stations, elevation " +
                "range, parts and labels, optionally with a profile sheet each.",
                "Profile View Tools - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
