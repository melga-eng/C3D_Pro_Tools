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
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Networks from Feature Lines: gravity pipes along feature lines (and 3D polylines / polylines), one line or a batch.
    /// The options on the left apply to every line; the list on the right shows each line's network, part and pipes, with
    /// the network name and the part changeable per line. One UNDO reverses a run.
    /// </summary>
    internal sealed class FormNetworkFromLines : Form
    {
        private const int CUse = 0, CSource = 1, CNetwork = 2, CPart = 3, CPipes = 4, CLength = 5, CSlope = 6, CNotes = 7;

        private readonly NetworkFromLinesSettings _s = NetworkFromLinesSettings.Load();
        private readonly List<LineRow> _rows = new List<LineRow>();
        private readonly Dictionary<string, ObjectId> _surfaces = new Dictionary<string, ObjectId>();
        private readonly HashSet<string> _networkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ObjectIdCollection> _featureLineSets = new Dictionary<string, ObjectIdCollection>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly System.Windows.Forms.Timer _replan = new System.Windows.Forms.Timer { Interval = 300 };
        private bool _loading = true, _filling;

        private readonly ComboBox _cbSet = ToolUi.Combo(200);
        private readonly Label _lblLines = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(10, 9, 3, 3) };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) };
        private readonly DataGridView _grid = ToolUi.Grid();

        // Elevation
        private readonly RadioButton _rbLineZ = new RadioButton { Text = "The line's own elevations", AutoSize = true };
        private readonly RadioButton _rbSurface = new RadioButton { Text = "The surface, less a depth of", AutoSize = true };
        private readonly TextBox _txtDepth = ToolUi.Number(1.0);
        private readonly ComboBox _cbSurface = ToolUi.Combo(260);
        private readonly ComboBox _cbReference = ToolUi.Combo(260);
        // Geometry
        private readonly CheckBox _chkMerge = ToolUi.Check("Join straight spans that run on in a straight line, within");
        private readonly TextBox _txtMergeTol = ToolUi.Number(0.005, 60), _txtMinLength = ToolUi.Number(0.05, 60), _txtMaxLength = ToolUi.Number(0, 60);
        private readonly ComboBox _cbDirection = ToolUi.Combo(260);
        // Parts
        private readonly PartChooser _part = new PartChooser(DomainType.Pipe);
        private readonly CheckBox _chkJoints = ToolUi.Check("A structure at every joint between two pipes");
        private readonly PartChooser _jointPart = new PartChooser(DomainType.Structure);
        private readonly CheckBox _chkEnds = ToolUi.Check("A structure at both ends of every line");
        private readonly PartChooser _endPart = new PartChooser(DomainType.Structure);
        private readonly CheckBox _chkConnect = ToolUi.Check("Connect ends lying inside a structure already in the network");
        // Networks
        private readonly RadioButton _rbPerLine = new RadioButton { Text = "One network per line, named", AutoSize = true };
        private readonly RadioButton _rbOne = new RadioButton { Text = "All lines in one network:", AutoSize = true };
        private readonly TextBox _txtPrefix = new TextBox { Width = 120 };
        private readonly ComboBox _cbOneNetwork = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckBox _chkErase = ToolUi.Check("Erase the source lines once their pipes are made");

        /// <summary>The pipes made, when the user asked to carry on with them in the Network Editor.</summary>
        public List<ObjectId> HandOff { get; private set; }

        public FormNetworkFromLines()
        {
            Text = "C3D Pro Tools - Networks from Feature Lines";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(980, 620);
            ClientSize = new Size(_s.Width >= 980 ? _s.Width : 1180, _s.Height >= 620 ? _s.Height : 760);

            ReadDrawing();
            Controls.Add(BuildList());
            Controls.Add(BuildOptions());
            Controls.Add(BuildSourceBar());
            Controls.Add(ToolUi.Header("Networks from Feature Lines", "Gravity pipes along feature lines or polylines, at their elevations or below a surface.",
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _replan.Tick += (s, e) => { _replan.Stop(); Replan(); };
            _loading = false;
            Shown += (s, e) => PickImplied();
            FormClosing += (s, e) => { SaveSettings(); _s.Width = ClientSize.Width; _s.Height = ClientSize.Height; _s.Save(); };
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private Control BuildSourceBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = StyleUi.Panel, Padding = new Padding(10, 6, 10, 6) };
            bar.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            bar.Controls.Add(new Label { Text = "Lines", Font = StyleUi.Bold, AutoSize = true, Margin = new Padding(3, 9, 8, 3) });
            Button pick = StyleUi.Primary("Select lines…", (s, e) => SelectLines(false));
            Button add = StyleUi.Secondary("Add more…", (s, e) => SelectLines(true));
            _tips.SetToolTip(pick, "Pick feature lines, 3D polylines or polylines one by one or with a window; they replace the list.");
            _tips.SetToolTip(add, "Pick more lines; they are added to the list.");
            bar.Controls.Add(pick);
            bar.Controls.Add(add);
            bar.Controls.Add(new Label { Text = "or add the feature lines of", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(10, 9, 4, 3) });
            _cbSet.Margin = new Padding(3, 6, 3, 3);
            bar.Controls.Add(_cbSet);
            bar.Controls.Add(StyleUi.Secondary("Add", (s, e) => AddSet()));
            bar.Controls.Add(StyleUi.Secondary("Clear", (s, e) => { _rows.Clear(); Replan(); }));
            bar.Controls.Add(_lblLines);
            return bar;
        }

        private Control BuildOptions()
        {
            Panel side = new Panel { Dock = DockStyle.Left, Width = 500, BackColor = StyleUi.Surface };
            side.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            side.Controls.Add(host);

            TableLayoutPanel elev = ToolUi.Card("Elevation",
                "Where the pipe's level comes from, and which part of the pipe it is. For a duct bank with no wall, inner and outer are the same.");
            FlowLayoutPanel radios = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            radios.Controls.Add(_rbLineZ);
            radios.Controls.Add(ToolUi.Inline(_rbSurface, ToolUi.WithUnit(_txtDepth, "m")));
            ToolUi.Wide(elev, radios);
            ToolUi.Field(elev, "Surface", _cbSurface, _tips, "The surface the levels come from (when chosen above). New networks also take it as their reference surface, and new structures get their rims on it.");
            _cbReference.Items.AddRange(new object[] { "Centre of the pipe", "Invert (inner bottom)", "Inner top (crown)", "Outer top (top of wall / encasement)", "Outer bottom" });
            ToolUi.Field(elev, "The level is the", _cbReference);
            ToolUi.Add(stack, elev);

            TableLayoutPanel geo = ToolUi.Card("Pipes along the line",
                "Every straight span between two points of the line becomes a pipe; every arc becomes one curved pipe from its start to its end " +
                "(elevation points on an arc are ignored, the pipe keeps one grade).");
            ToolUi.Wide(geo, ToolUi.Inline(_chkMerge, ToolUi.WithUnit(_txtMergeTol, "m")));
            ToolUi.Field(geo, "Shortest pipe", ToolUi.WithUnit(_txtMinLength, "m"), _tips, "A straight span shorter than this joins its straight neighbour.");
            ToolUi.Field(geo, "Longest pipe", ToolUi.WithUnit(_txtMaxLength, "m (0 = no limit)"), _tips,
                "Straight pipes longer than this are split into equal parts. With levels from a surface, every split point follows the surface.");
            _cbDirection.Items.AddRange(new object[] { "As the line was drawn", "Downhill (from the higher end)" });
            ToolUi.Field(geo, "Pipe direction", _cbDirection);
            ToolUi.Add(stack, geo);

            TableLayoutPanel part = ToolUi.Card("Pipe part", "The part for every line, unless a line in the list has its own (select rows and use \"Give selected this part\").");
            ToolUi.Wide(part, _part);
            ToolUi.Add(stack, part);

            TableLayoutPanel st = ToolUi.Card("Structures", "Leave both off for pipes only (they are then not connected to each other).");
            ToolUi.Wide(st, _chkJoints);
            ToolUi.Wide(st, _jointPart);
            ToolUi.Wide(st, _chkEnds);
            ToolUi.Wide(st, _endPart);
            ToolUi.Wide(st, _chkConnect, _tips, "When the network already exists: a line end inside one of its structures is connected to that structure (instead of getting a new one).");
            ToolUi.Add(stack, st);

            TableLayoutPanel net = ToolUi.Card("Networks",
                "A network named like an existing one gets the pipes added to it. New networks use the pipe part's parts list. Every name can be changed in the list.");
            FlowLayoutPanel nradios = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            nradios.Controls.Add(ToolUi.Inline(_rbPerLine, _txtPrefix, new Label { Text = "+ the line's name", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 0, 0) }));
            nradios.Controls.Add(ToolUi.Inline(_rbOne, _cbOneNetwork));
            ToolUi.Wide(net, nradios);
            ToolUi.Wide(net, _chkErase);
            ToolUi.Add(stack, net);

            ToolUi.Exclusive(_rbLineZ, _rbSurface);
            ToolUi.Exclusive(_rbPerLine, _rbOne);
            foreach (Control c in new Control[] { _txtDepth, _txtMergeTol, _txtMinLength, _txtMaxLength, _txtPrefix, _cbOneNetwork })
                c.TextChanged += (s, e) => Changed();
            foreach (ComboBox c in new[] { _cbSurface, _cbReference, _cbDirection, _cbOneNetwork }) c.SelectedIndexChanged += (s, e) => Changed();
            foreach (CheckBox c in new[] { _chkMerge, _chkJoints, _chkEnds, _chkConnect }) c.CheckedChanged += (s, e) => Changed();
            foreach (RadioButton r in new[] { _rbLineZ, _rbSurface, _rbPerLine, _rbOne }) r.CheckedChanged += (s, e) => { if (r.Checked) Changed(); };
            _part.Changed += (s, e) => Changed();
            return side;
        }

        private Control BuildList()
        {
            Panel area = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Surface, Padding = new Padding(10, 8, 10, 6) };
            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            Button give = StyleUi.Secondary("Give selected this part", (s, e) => SetPart(true));
            Button reset = StyleUi.Secondary("Use the default part", (s, e) => SetPart(false));
            Button remove = StyleUi.Secondary("Remove selected", (s, e) => RemoveSelected());
            _tips.SetToolTip(give, "The selected lines get the part chosen under \"Pipe part\" now; changing that part later no longer changes them.");
            _tips.SetToolTip(reset, "The selected lines follow the part chosen under \"Pipe part\" again.");
            tools.Controls.Add(give);
            tools.Controls.Add(reset);
            tools.Controls.Add(remove);
            tools.Controls.Add(new Label { Text = "Select several rows with Ctrl / Shift. Network names can be typed in the list.", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(8, 9, 3, 3) });

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Use", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 44 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Line", ReadOnly = true, FillWeight = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Network", FillWeight = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part", ReadOnly = true, FillWeight = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pipes", ReadOnly = true, FillWeight = 45 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Length (m)", ReadOnly = true, FillWeight = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Grades (%)", ReadOnly = true, FillWeight = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Notes", ReadOnly = true, FillWeight = 150 });
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => CellChanged(e.RowIndex, e.ColumnIndex);

            area.Controls.Add(_grid);
            area.Controls.Add(tools);
            return area;
        }

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Panel, Padding = new Padding(12, 6, 12, 8) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            Button run = StyleUi.Primary("Create networks…", (s, e) => Run());
            run.MinimumSize = new Size(190, 34);
            footer.Controls.Add(_lblStatus, 0, 0);
            footer.Controls.Add(run, 1, 0);
            return footer;
        }

        // =====================================================================================
        // Drawing data and settings
        // =====================================================================================

        private void ReadDrawing()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in doc.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface sf) _surfaces[sf.Name] = id;
                foreach (ObjectId id in doc.GetPipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Network n) _networkNames.Add(n.Name);
                ObjectIdCollection all = new ObjectIdCollection();
                ObjectIdCollection siteless = doc.GetSitelessFeatureLineIds();
                foreach (ObjectId id in siteless) all.Add(id);
                List<(string, ObjectIdCollection)> sites = new List<(string, ObjectIdCollection)>();
                foreach (ObjectId siteId in doc.GetSiteIds())
                    if (tr.GetObject(siteId, OpenMode.ForRead) is Site site)
                    {
                        ObjectIdCollection ids = site.GetFeatureLineIds();
                        foreach (ObjectId id in ids) all.Add(id);
                        sites.Add(($"Site: {site.Name}", ids));
                    }
                _featureLineSets[$"the whole drawing ({all.Count})"] = all;
                if (siteless.Count > 0) _featureLineSets[$"no site ({siteless.Count})"] = siteless;
                foreach ((string name, ObjectIdCollection ids) in sites.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase))
                    if (ids.Count > 0) _featureLineSets[$"{name} ({ids.Count})"] = ids;
            }
            foreach (string k in _featureLineSets.Keys) _cbSet.Items.Add(k);
            if (_cbSet.Items.Count > 0) _cbSet.SelectedIndex = 0;
            _cbSurface.Items.Add("(none)");
            foreach (string n in _surfaces.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbSurface.Items.Add(n);
            foreach (string n in _networkNames.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbOneNetwork.Items.Add(n);
        }

        private void ApplySettings()
        {
            _rbSurface.Checked = _s.FromSurface;
            _rbLineZ.Checked = !_s.FromSurface;
            _txtDepth.Text = ToolUi.Format(_s.Depth);
            ToolUi.Select(_cbSurface, _s.Surface != null && _surfaces.ContainsKey(_s.Surface) ? _s.Surface : "(none)");
            _cbReference.SelectedIndex = Math.Max(0, Math.Min(4, _s.Reference));
            _chkMerge.Checked = _s.MergeStraight;
            _txtMergeTol.Text = ToolUi.Format(_s.MergeTolerance);
            _txtMinLength.Text = ToolUi.Format(_s.MinLength);
            _txtMaxLength.Text = ToolUi.Format(_s.MaxLength);
            _cbDirection.SelectedIndex = _s.Downhill ? 1 : 0;
            _part.SetContext(ObjectId.Null, _s.PartsList, _s.Family, _s.Size);
            _chkJoints.Checked = _s.AddJoints;
            _jointPart.SetContext(ObjectId.Null, _s.JointList, _s.JointFamily, _s.JointSize);
            _chkEnds.Checked = _s.AddEnds;
            _endPart.SetContext(ObjectId.Null, _s.EndList, _s.EndFamily, _s.EndSize);
            _chkConnect.Checked = _s.ConnectExisting;
            _rbOne.Checked = _s.OneNetwork;
            _rbPerLine.Checked = !_s.OneNetwork;
            _txtPrefix.Text = _s.Prefix ?? "";
            _cbOneNetwork.Text = _s.NetworkName ?? "";
            _chkErase.Checked = _s.EraseSource;
            UpdateEnabled();
        }

        private void SaveSettings()
        {
            _s.FromSurface = _rbSurface.Checked;
            _s.Surface = _cbSurface.SelectedItem as string;
            if (ToolUi.TryNumber(_txtDepth, out double depth)) _s.Depth = depth;
            _s.Reference = _cbReference.SelectedIndex;
            _s.MergeStraight = _chkMerge.Checked;
            if (ToolUi.TryNumber(_txtMergeTol, out double tol) && tol >= 0) _s.MergeTolerance = tol;
            if (ToolUi.TryNumber(_txtMinLength, out double min) && min >= 0) _s.MinLength = min;
            if (ToolUi.TryNumber(_txtMaxLength, out double max) && max >= 0) _s.MaxLength = max;
            _s.Downhill = _cbDirection.SelectedIndex == 1;
            _s.PartsList = _part.ListName; _s.Family = _part.FamilyName; _s.Size = _part.SizeName;
            _s.AddJoints = _chkJoints.Checked;
            _s.JointList = _jointPart.ListName; _s.JointFamily = _jointPart.FamilyName; _s.JointSize = _jointPart.SizeName;
            _s.AddEnds = _chkEnds.Checked;
            _s.EndList = _endPart.ListName; _s.EndFamily = _endPart.FamilyName; _s.EndSize = _endPart.SizeName;
            _s.ConnectExisting = _chkConnect.Checked;
            _s.OneNetwork = _rbOne.Checked;
            _s.Prefix = _txtPrefix.Text;
            _s.NetworkName = _cbOneNetwork.Text;
            _s.EraseSource = _chkErase.Checked;
            _s.Save();
        }

        private void UpdateEnabled()
        {
            _txtDepth.Enabled = _rbSurface.Checked;
            _jointPart.Enabled = _chkJoints.Checked;
            _endPart.Enabled = _chkEnds.Checked;
            _txtPrefix.Enabled = _rbPerLine.Checked;
            _cbOneNetwork.Enabled = _rbOne.Checked;
            _grid.Columns[CNetwork].ReadOnly = _rbOne.Checked;
            _txtMergeTol.Enabled = _chkMerge.Checked;
        }

        private void Changed()
        {
            if (_loading) return;
            UpdateEnabled();
            _replan.Stop();
            _replan.Start();
        }

        // =====================================================================================
        // Choosing lines
        // =====================================================================================

        /// <summary>Lines selected before the tool opened are listed straight away.</summary>
        private void PickImplied()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            PromptSelectionResult psr = acDoc.Editor.SelectImplied();
            if (psr.Status == PromptStatus.OK) AddLines(psr.Value.GetObjectIds(), false);
            else Replan();
        }

        private void SelectLines(bool add)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect feature lines, 3D polylines or polylines: " });
                if (psr.Status == PromptStatus.OK) AddLines(psr.Value.GetObjectIds(), add);
            }
            finally { Show(); }
        }

        private void AddSet()
        {
            if (_cbSet.SelectedItem is string key && _featureLineSets.TryGetValue(key, out ObjectIdCollection ids)) AddLines(ids.Cast<ObjectId>(), true);
        }

        private void AddLines(IEnumerable<ObjectId> ids, bool add)
        {
            if (!add) _rows.Clear();
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in ids)
                {
                    if (id.IsErased || _rows.Any(r => r.SourceId == id)) continue;
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (!NetworkFromLines.IsSource(obj)) continue;
                    _rows.Add(new LineRow { SourceId = id, SourceName = NetworkFromLines.NameOf(obj), Kind = NetworkFromLines.KindOf(obj) });
                }
            Replan();
        }

        private List<LineRow> SelectedRows() =>
            _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as LineRow).Where(r => r != null).ToList();

        private void SetPart(bool give)
        {
            List<LineRow> chosen = SelectedRows();
            if (chosen.Count == 0) { MessageBox.Show(this, "Select one or more lines in the list first.", Text); return; }
            PartPick pick = give ? _part.Pick : null;
            if (give && pick == null) { MessageBox.Show(this, "Choose a parts list, a pipe family and a size under \"Pipe part\" first.", Text); return; }
            foreach (LineRow r in chosen) r.Part = pick;
            Fill();
        }

        private void RemoveSelected()
        {
            foreach (LineRow r in SelectedRows()) _rows.Remove(r);
            Fill();
        }

        // =====================================================================================
        // Planning and the list
        // =====================================================================================

        private LineNetworkOptions Options(out string error)
        {
            error = null;
            LineNetworkOptions o = new LineNetworkOptions
            {
                FromSurface = _rbSurface.Checked,
                SurfaceId = _cbSurface.SelectedItem is string sn && _surfaces.TryGetValue(sn, out ObjectId sid) ? sid : ObjectId.Null,
                Reference = (LineElevationRef)Math.Max(0, _cbReference.SelectedIndex),
                MergeStraight = _chkMerge.Checked,
                Downhill = _cbDirection.SelectedIndex == 1,
                Joint = _chkJoints.Checked ? _jointPart.Pick : null,
                Ends = _chkEnds.Checked ? _endPart.Pick : null,
                ConnectExisting = _chkConnect.Checked,
                EraseSource = _chkErase.Checked
            };
            if (o.FromSurface && (!ToolUi.TryNumber(_txtDepth, out o.Depth))) error = "Enter the depth below the surface as a number.";
            if (o.FromSurface && o.SurfaceId.IsNull) error = "Choose the surface the levels come from.";
            if (!ToolUi.TryNumber(_txtMergeTol, out o.MergeTolerance) || o.MergeTolerance < 0) error = "Enter the straightness tolerance as a positive number.";
            if (!ToolUi.TryNumber(_txtMinLength, out o.MinLength) || o.MinLength < 0) error = "Enter the shortest pipe as a positive number (or 0).";
            if (!ToolUi.TryNumber(_txtMaxLength, out o.MaxLength) || o.MaxLength < 0) error = "Enter the longest pipe as a positive number (or 0 for no limit).";
            return o;
        }

        private void Replan()
        {
            LineNetworkOptions o = Options(out string error);
            if (error == null)
            {
                Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                    foreach (LineRow r in _rows)
                    {
                        if (r.SourceId.IsErased) { r.Error = "Erased."; r.Pieces.Clear(); continue; }
                        try { NetworkFromLines.Plan(tr, r, o); }
                        catch (Exception ex) { r.Error = ex.Message; r.Pieces.Clear(); }
                    }
            }
            Fill();
            if (error != null) _lblStatus.Text = error;
        }

        private string NetworkFor(LineRow r) => _rbOne.Checked ? _cbOneNetwork.Text.Trim() : r.NetworkEdited ? r.Network : (_txtPrefix.Text ?? "") + r.SourceName;

        private void Fill()
        {
            HashSet<ObjectId> selected = new HashSet<ObjectId>(SelectedRows().Select(r => r.SourceId));
            foreach (LineRow r in _rows) r.Network = NetworkFor(r);
            Dictionary<string, int> shared = _rows.Where(r => r.Use && r.Error == null).GroupBy(r => r.Network ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            PartPick main = _part.Pick;
            _filling = true;
            _grid.Rows.Clear();
            foreach (LineRow r in _rows)
            {
                List<string> notes = new List<string>();
                if (r.Error != null) notes.Add(r.Error);
                else
                {
                    int arcs = r.Pieces.Count(p => p.Curved);
                    if (arcs > 0) notes.Add($"{arcs} curved");
                    notes.AddRange(r.Notes);
                    if (_networkNames.Contains(r.Network ?? "")) notes.Add("adds to the existing network");
                    if (!_rbOne.Checked && shared.TryGetValue(r.Network ?? "", out int n) && n > 1) notes.Add($"shares its network with {n - 1} other line(s)");
                }
                string grades = r.Pieces.Count == 0 ? "" : $"{r.Pieces.Min(p => p.Slope):0.00} … {r.Pieces.Max(p => p.Slope):0.00}";
                PartPick pick = r.Part ?? main;
                int i = _grid.Rows.Add(r.Use, r.SourceName, r.Network, pick == null ? "(choose a part)" : (r.Part != null ? "★ " : "") + pick.Label,
                    r.Pieces.Count, ToolUi.Format(Math.Round(r.Length, 2)), grades, string.Join("; ", notes));
                DataGridViewRow row = _grid.Rows[i];
                row.Tag = r;
                row.Cells[CSource].ToolTipText = r.Kind;
                if (r.Error != null) row.DefaultCellStyle.ForeColor = Color.FromArgb(185, 28, 28);
                else if (!r.Use) row.DefaultCellStyle.ForeColor = StyleUi.Muted;
                if (r.Part != null) row.Cells[CPart].ToolTipText = "This line's own part.";
                row.Selected = selected.Contains(r.SourceId);
            }
            _filling = false;
            if (_grid.Rows.Count > 0 && selected.Count == 0) _grid.ClearSelection();

            int usable = _rows.Count(r => r.Use && r.Error == null && r.Pieces.Count > 0);
            int nets = _rows.Where(r => r.Use && r.Error == null && r.Pieces.Count > 0).Select(r => r.Network).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _lblLines.Text = _rows.Count == 0 ? "No lines chosen yet." : $"{_rows.Count} line(s) listed.";
            _lblStatus.Text = usable == 0 ? "Choose the lines at the top."
                : $"{usable} line(s) → {_rows.Where(r => r.Use && r.Error == null).Sum(r => r.Pieces.Count)} pipe(s) in {nets} network(s).";
        }

        private void CellChanged(int rowIndex, int column)
        {
            if (_filling || rowIndex < 0 || !(_grid.Rows[rowIndex].Tag is LineRow r)) return;
            object value = _grid.Rows[rowIndex].Cells[column].Value;
            if (column == CUse) r.Use = value is bool b && b;
            else if (column == CNetwork)
            {
                string name = (value as string ?? "").Trim();
                r.NetworkEdited = name.Length > 0 && name != (_txtPrefix.Text ?? "") + r.SourceName;
                r.Network = name.Length > 0 ? name : (_txtPrefix.Text ?? "") + r.SourceName;
            }
            else return;
            BeginInvoke((Action)Fill);
        }

        // =====================================================================================
        // Creating
        // =====================================================================================

        private void Run()
        {
            SaveSettings();
            try
            {
                Replan();
                LineNetworkOptions o = Options(out string error);
                if (error != null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                PartPick main = _part.Pick;
                List<LineRow> rows = _rows.Where(r => r.Use && r.Error == null && r.Pieces.Count > 0).ToList();
                if (rows.Count == 0) { MessageBox.Show(this, "Choose some lines with a usable shape first (Select lines at the top).", Text); return; }
                if (rows.Any(r => r.Part == null) && main == null) { MessageBox.Show(this, "Choose the pipe part (parts list, family and size).", Text); return; }
                if (_chkJoints.Checked && o.Joint == null) { MessageBox.Show(this, "Choose the structure for the joints, or turn joint structures off.", Text); return; }
                if (_chkEnds.Checked && o.Ends == null) { MessageBox.Show(this, "Choose the structure for the line ends, or turn end structures off.", Text); return; }
                if (_rbOne.Checked && string.IsNullOrWhiteSpace(_cbOneNetwork.Text)) { MessageBox.Show(this, "Enter the network's name.", Text); return; }

                int pipes = rows.Sum(r => r.Pieces.Count);
                int nets = rows.Select(r => r.Network).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int skipped = _rows.Count(r => r.Use && (r.Error != null || r.Pieces.Count == 0));
                string ask = $"Create {pipes} pipe(s) from {rows.Count} line(s) in {nets} network(s)?" +
                             (skipped > 0 ? $"\n\n{skipped} line(s) with problems are left out (see Notes)." : "") +
                             (o.EraseSource ? "\n\nThe source lines are erased." : "");
                if (MessageBox.Show(this, ask, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

                Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
                LineNetworkResult res;
                using (acDoc.LockDocument())
                using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                {
                    res = NetworkFromLines.Create(tr, CivilApplication.ActiveDocument, rows, main, o, acDoc.Editor);
                    tr.Commit();
                }
                acDoc.Editor.Regen();
                foreach (LineRow r in rows) _networkNames.Add(r.Network);
                if (o.EraseSource) _rows.RemoveAll(r => r.SourceId.IsErased);
                Replan();

                string text = $"Created {res.Pipes} pipe(s) from {res.Lines} line(s); {res.Networks} new network(s)." +
                              (res.Structures > 0 ? $"\nStructures added: {res.Structures}" : "") +
                              (res.Connected > 0 ? $"\nEnds and joints sharing a structure (existing, or made for a line met there): {res.Connected}" : "") +
                              (res.Chorded > 0 ? $"\nArcs Civil 3D would not take as one curved pipe, drawn as straight pipes along the arc: {res.Chorded} (see the command line)" : "") +
                              (res.Erased > 0 ? $"\nSource lines erased: {res.Erased}" : "") +
                              (res.Failed > 0 ? $"\nFailed, nothing made for them: {string.Join(", ", res.FailedLines.Take(8))}{(res.FailedLines.Count > 8 ? $" and {res.FailedLines.Count - 8} more" : "")}; the reasons are on the command line." : "") +
                              (res.Log.Count > 0 ? "\n\nParts lists:\n• " + string.Join("\n• ", res.Log) : "") +
                              "\n\nUNDO reverses the whole run.";
                _lblStatus.Text = $"Created {res.Pipes} pipe(s) from {res.Lines} line(s).";
                if (res.PipeIds.Count == 0) { MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (MessageBox.Show(this, text + "\n\nOpen the new pipes in the Network Editor (split, fit, swap…)?", Text, MessageBoxButtons.YesNo,
                        res.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    HandOff = res.PipeIds;
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The run stopped with an error and nothing was changed.\n\n{ex.GetType().Name}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. Choose the lines at the top: feature lines, 3D polylines or polylines (selected, or every feature line of a site).\n" +
                "2. Set where the levels come from and which part of the pipe they are:\n" +
                "    • the line's own elevations, or a surface less a depth (e.g. the cover, with \"Outer top\");\n" +
                "    • centre, invert, inner top, outer top (top of wall or encasement) or outer bottom. Outer uses the part's outermost size.\n" +
                "3. Choose the pipe part, and optionally structures at the joints and at the ends (or none at all).\n" +
                "4. One network per line (prefix + the line's name) or all in one network; names can be typed in the list.\n" +
                "5. Select rows in the list to give them their own part, or to leave them out.\n\n" +
                "Straight spans between two points become pipes; an arc becomes one curved pipe from its start to its end, with one grade " +
                "(elevation points on it are ignored). Straight spans that continue in a straight line are joined.\n\n" +
                "One UNDO reverses a run. Afterwards the new pipes can be opened in the Network Editor; levels and cover are in the " +
                "Profile & Cover Designer.",
                "Networks from Feature Lines - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
