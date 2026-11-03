using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Network Editor: plan edits on the pipes chosen at the top (a selection or whole networks). One task at a time from the
    /// side list: split, parallel copies, duct bank conduits, fit ends to structures, flow direction, merge,
    /// swap part and pressure to gravity. Every change is previewed or confirmed first, and one UNDO reverses it.
    /// </summary>
    internal sealed partial class FormNetworkEditor : Form
    {
        private const int ASplit = 0, AParallel = 1, AConduits = 2, AFit = 3, AFlow = 4, AMerge = 5, ASwap = 6, AGravity = 7;
        private static readonly string[] RunText = { "Split pipes…", "Create copies…", "Create conduits…", "Fit pipe ends…", "Change flow direction…", "Merge pipes…", "Swap parts…", "Convert to gravity…" };

        private readonly NetworkEditorSettings _s = NetworkEditorSettings.Load();
        private readonly List<ObjectId> _pipes = new List<ObjectId>();
        private readonly Dictionary<string, ObjectId> _networks = new Dictionary<string, ObjectId>();
        /// <summary>The network the part choosers open on (the first chosen gravity pipe's).</summary>
        private ObjectId _partNetwork = ObjectId.Null;
        private readonly NavList _nav = new NavList();
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Surface };
        private readonly List<Control> _pages = new List<Control>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label _lblPipes = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(10, 9, 3, 3) };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) };
        private readonly ComboBox _cbNetwork = ToolUi.Combo(240);
        private Button _btnRun;

        // Split
        private readonly ComboBox _cbSplitMode = ToolUi.Combo(260);
        private readonly NumericUpDown _numParts = new NumericUpDown { Minimum = 2, Maximum = 100, Width = 70 };
        private readonly TextBox _txtEvery = ToolUi.Number(20), _txtPickTol = ToolUi.Number(1);
        private readonly CheckBox _chkSplitStructure = ToolUi.Check("Add a structure at every split point, connected to both pieces");
        private readonly PartChooser _splitPart = new PartChooser(DomainType.Structure);
        // Parallel copies
        private readonly ComboBox _cbPreset = ToolUi.Combo();
        private readonly TextBox _txtH = new TextBox(), _txtV = new TextBox();
        private readonly NumericUpDown _numCols = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 5, Width = 56 }, _numRows = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 3, Width = 56 };
        private readonly TextBox _txtSpX = ToolUi.Number(0.2, 60), _txtSpZ = ToolUi.Number(0.15, 60);
        private readonly RadioButton _rbSameNet = new RadioButton { Text = "Each source pipe's own network", AutoSize = true }, _rbNewNet = new RadioButton { Text = "A new network:", AutoSize = true };
        private readonly TextBox _txtNewNet = new TextBox { Width = 220 };
        private readonly RadioButton _rbOwnSize = new RadioButton { Text = "Same part as each source pipe", AutoSize = true }, _rbChosenSize = new RadioButton { Text = "This part:", AutoSize = true };
        private readonly PartChooser _parPart = new PartChooser(DomainType.Pipe);
        private readonly CheckBox _chkParFit = ToolUi.Check("End straight copies at the source pipe's structures");
        private readonly ComboBox _cbParFace = ToolUi.Combo(160);
        private readonly TextBox _txtParBoxL = ToolUi.Number(6, 60), _txtParBoxW = ToolUi.Number(3, 60);
        private FlowLayoutPanel _parBoxRow;
        // Fit to structures
        private const string FitOutlineLayer = "C3D-PRO-FIT-OUTLINES";
        private readonly ComboBox _cbFitFace = ToolUi.Combo(260);
        private readonly TextBox _txtFitBoxL = ToolUi.Number(6, 60), _txtFitBoxW = ToolUi.Number(3, 60);
        private readonly CheckBox _chkFitConnect = ToolUi.Check("Connect loose ends lying inside a structure to it");
        // Flow
        private readonly RadioButton _rbFlowVerify = new RadioButton { Text = "Check the runs first, then make each flow one way", AutoSize = true };
        private readonly RadioButton _rbFlowAll = new RadioButton { Text = "Reverse every chosen pipe as it is", AutoSize = true };
        // Swap
        private readonly PartChooser _swapPart = new PartChooser(DomainType.Pipe);
        private readonly PartChooser _swapStructure = new PartChooser(DomainType.Structure);
        private sealed class PressureItem { public ObjectId ListId; public string Guid, Name, Family; public double Diameter; public override string ToString() => Name; }
        private readonly ComboBox _cbSwapPList = ToolUi.Combo(), _cbSwapPSize = ToolUi.Combo();
        private TableLayoutPanel _swapPressure;
        private readonly CheckBox _chkSwapStructures = ToolUi.Check("Also the structures at the chosen pipes' ends");
        private readonly CheckBox _chkSwapFittings = ToolUi.Check("Also the fittings and appurtenances at the chosen pressure pipes' ends");
        private readonly RadioButton _rbSwapInvert = new RadioButton { Text = "Invert levels", AutoSize = true }, _rbSwapCentre = new RadioButton { Text = "Centre elevations", AutoSize = true };
        private readonly RadioButton _rbSwapExact = new RadioButton { Text = "This size", AutoSize = true };
        private readonly RadioButton _rbSwapNearest = new RadioButton { Text = "The size in this family nearest each row's inside size", AutoSize = true };
        private readonly DataGridView _swapGrid = ToolUi.Grid();
        private readonly Label _lblSwapSel = ToolUi.Note(""), _lblSwapSummary = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(8, 9, 3, 3) };
        private readonly ComboBox _cbSwapSet = ToolUi.Combo(260);
        private List<SwapGroup> _swapGroups = new List<SwapGroup>();
        /// <summary>Structures, fittings and appurtenances chosen as they are (pipes are in <see cref="_pipes"/>).</summary>
        private readonly List<ObjectId> _parts = new List<ObjectId>();
        // Pressure to gravity
        private readonly PartChooser _gPart = new PartChooser(DomainType.Pipe);
        private readonly TextBox _txtGNet = new TextBox { Width = 240 };

        /// <param name="pipes">Pipes to start with (e.g. just made by another tool); null = none.</param>
        public FormNetworkEditor(IEnumerable<ObjectId> pipes = null)
        {
            if (pipes != null) _pipes.AddRange(pipes);
            Text = "C3D Pro Tools - Network Editor";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(860, 600);
            ClientSize = new Size(_s.Width >= 860 ? _s.Width : 1000, _s.Height >= 600 ? _s.Height : 700);

            ReadDrawing();
            _nav.Add("Split pipes", "At points, in parts or every N m");
            _nav.Add("Parallel copies", "Copies at plan / vertical offsets");
            _nav.Add("Duct bank conduits", "Conduit grid inside the encasement");
            _nav.Add("Fit to structures", "Ends to walls, centres or a box of your size");
            _nav.Add("Flow direction", "Reverse pipes, or make runs consistent");
            _nav.Add("Merge pipes", "Join straight pipes end to end");
            _nav.Add("Swap parts", "Pipes, structures, fittings: many at once");
            _nav.Add("Pressure to gravity", "Gravity copies of pressure pipes");
            _pages.Add(BuildSplit());
            _pages.Add(BuildParallel());
            _pages.Add(BuildConduits());
            _pages.Add(BuildFit());
            _pages.Add(BuildFlow());
            _pages.Add(BuildMerge());
            _pages.Add(BuildSwap());
            _pages.Add(BuildGravity());
            foreach (Control p in _pages) { p.Visible = false; _content.Controls.Add(p); }

            Controls.Add(_content);
            Controls.Add(BuildSourceBar());
            Controls.Add(_nav);
            Controls.Add(ToolUi.Header("Network Editor", "Plan edits on existing pipes: choose the pipes, pick a task, preview, apply.",
                StyleUi.HeaderButton("Repair network pipes…", (s, e) => NetworkRepair.Run(this)),
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _nav.SelectedChanged += (s, e) => ShowAction();
            _nav.Selected = Math.Max(0, Math.Min(_pages.Count - 1, _s.Action));
            ShowAction();
            UpdatePipeSummary();
            FormClosing += (s, e) => { SaveSettings(); _s.Width = ClientSize.Width; _s.Height = ClientSize.Height; _s.Save(); };
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private Control BuildSourceBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = StyleUi.Panel, Padding = new Padding(10, 6, 10, 6) };
            bar.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            bar.Controls.Add(new Label { Text = "Pipes", Font = StyleUi.Bold, AutoSize = true, Margin = new Padding(3, 9, 8, 3) });
            Button pipes = StyleUi.Primary("Select pipes…", (s, e) => SelectPipes());
            Button networks = StyleUi.Secondary("Select network(s)…", (s, e) => SelectNetworks());
            Button path = StyleUi.Secondary("Select path (start && end)…", (s, e) => SelectPaths());
            _tips.SetToolTip(pipes, "Pick the pipes one by one or with a window; they replace the current choice.");
            _tips.SetToolTip(networks, "Pick any part of each network: all its pipes are chosen (they replace the current choice).");
            _tips.SetToolTip(path, "Pick a start pipe and an end pipe: the connected route between them is chosen (parallel pipes kept apart). " +
                                   "Pick more start / end pairs for more paths; Enter when done. Gravity pipes.");
            bar.Controls.Add(pipes);
            bar.Controls.Add(networks);
            bar.Controls.Add(path);
            bar.Controls.Add(new Label { Text = "or add a network", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(10, 9, 4, 3) });
            _cbNetwork.Margin = new Padding(3, 6, 3, 3);
            bar.Controls.Add(_cbNetwork);
            bar.Controls.Add(StyleUi.Secondary("Add", (s, e) => AddNetwork()));
            bar.Controls.Add(StyleUi.Secondary("Show", (s, e) => ShowPipes()));
            bar.Controls.Add(StyleUi.Secondary("Clear", (s, e) => { _pipes.Clear(); _parts.Clear(); UpdatePipeSummary(); }));
            bar.Controls.Add(_lblPipes);
            return bar;
        }

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Panel, Padding = new Padding(12, 6, 12, 8) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            _btnRun = StyleUi.Primary("Run…", (s, e) => Run());
            _btnRun.MinimumSize = new Size(170, 34);
            footer.Controls.Add(_lblStatus, 0, 0);
            footer.Controls.Add(_btnRun, 1, 0);
            return footer;
        }

        private Control BuildSplit()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Where to split",
                "Straight pipes keep their first piece; the others are copies with the same family, size, style and layer on the same 3D line, " +
                "and the original end connection moves to the last piece. Curved pipes are broken by Civil 3D's own break.");
            _cbSplitMode.Items.AddRange(new object[] { "At points I pick on the pipes", "Into equal parts", "Every fixed length (from the start)" });
            ToolUi.Field(card, "Split", _cbSplitMode);
            ToolUi.Field(card, "Parts", _numParts);
            ToolUi.Field(card, "Every", ToolUi.WithUnit(_txtEvery, "m"));
            ToolUi.Field(card, "Pick within", ToolUi.WithUnit(_txtPickTol, "m of a pipe"), _tips, "A picked point splits the nearest pipe when it lies within this distance of it.");
            ToolUi.Add(stack, card);

            TableLayoutPanel st = ToolUi.Card("Structures at the split points", "Leave this off for cable ducts that simply continue; turn it on to place manholes or pits there.");
            ToolUi.Wide(st, _chkSplitStructure);
            ToolUi.Wide(st, _splitPart);
            ToolUi.Add(stack, st);

            _cbSplitMode.SelectedIndexChanged += (s, e) => { _numParts.Enabled = _cbSplitMode.SelectedIndex == 1; _txtEvery.Enabled = _cbSplitMode.SelectedIndex == 2; _txtPickTol.Enabled = _cbSplitMode.SelectedIndex == 0; UpdatePipeSummary(); };
            _chkSplitStructure.CheckedChanged += (s, e) => _splitPart.Enabled = _chkSplitStructure.Checked;
            return host;
        }

        private Control BuildParallel()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Offsets",
                "One copy is made at every combination of a horizontal and a vertical offset (the source position itself is skipped). " +
                "Horizontal: + is right of the pipe's direction. Vertical: + is up. For a duct bank's conduits use the Duct bank conduits task.");
            ToolUi.Field(card, "Preset", _cbPreset);
            _txtH.Width = _txtV.Width = 360;
            ToolUi.Field(card, "Horizontal (m)", _txtH, _tips, "Plan offsets, comma separated, e.g. -0.4, -0.2, 0, 0.2, 0.4");
            ToolUi.Field(card, "Vertical (m)", _txtV, _tips, "Vertical offsets, comma separated, e.g. -0.15, 0, 0.15 (0 = at the source pipe's level)");
            Button fill = StyleUi.Secondary("Fill grid", (s, e) => FillGrid());
            ToolUi.Field(card, "Grid", ToolUi.Inline(_numCols, new Label { Text = "columns ×", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _numRows,
                new Label { Text = "rows, spacing", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _txtSpX, new Label { Text = "×", AutoSize = true, Margin = new Padding(2, 7, 2, 0) }, _txtSpZ,
                new Label { Text = "m, centred", AutoSize = true, Margin = new Padding(2, 7, 8, 0) }, fill));
            ToolUi.Add(stack, card);

            TableLayoutPanel part = ToolUi.Card("Copies");
            ToolUi.Wide(part, _rbOwnSize);
            ToolUi.Wide(part, _rbChosenSize);
            ToolUi.Wide(part, _parPart);
            ToolUi.Wide(part, ToolUi.Inline(_chkParFit, new Label { Text = "at their", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, _cbParFace));
            _parBoxRow = ToolUi.Inline(new Label { Text = "Box", AutoSize = true, Margin = new Padding(20, 7, 8, 0) }, _txtParBoxL,
                new Label { Text = "long ×", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) }, _txtParBoxW,
                new Label { Text = "m wide, centred on each structure, length along its rotation", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 0, 0) });
            ToolUi.Wide(part, _parBoxRow);
            ToolUi.Add(stack, part);

            TableLayoutPanel net = ToolUi.Card("Network", "Gravity and pressure copies each go into a network of their own type. A new gravity network uses the chosen part's parts list (or the source network's).");
            Panel group = new Panel { AutoSize = true, Margin = new Padding(0) };
            FlowLayoutPanel radios = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            radios.Controls.Add(_rbSameNet);
            radios.Controls.Add(ToolUi.Inline(_rbNewNet, _txtNewNet));
            group.Controls.Add(radios);
            ToolUi.Wide(net, group);
            ToolUi.Add(stack, net);

            ToolUi.Exclusive(_rbOwnSize, _rbChosenSize);
            ToolUi.Exclusive(_rbSameNet, _rbNewNet);
            _cbParFace.Items.AddRange(new object[] { "inner walls", "outer walls", "centres", "box of my size" });
            _cbParFace.SelectedIndexChanged += (s, e) => UpdateBoxFields();
            foreach (ParallelPreset p in _s.Presets) _cbPreset.Items.Add(p.Name);
            _cbPreset.SelectedIndexChanged += (s, e) =>
            {
                ParallelPreset p = _s.Presets.ElementAtOrDefault(_cbPreset.SelectedIndex);
                if (p != null) { _txtH.Text = p.Horizontal; _txtV.Text = p.Vertical; }
            };
            _rbNewNet.CheckedChanged += (s, e) => { _txtNewNet.Enabled = _rbNewNet.Checked; _parPart.SetContext(_rbNewNet.Checked ? ObjectId.Null : _partNetwork); };
            _rbChosenSize.CheckedChanged += (s, e) => _parPart.Enabled = _rbChosenSize.Checked;
            _chkParFit.CheckedChanged += (s, e) => { _cbParFace.Enabled = _chkParFit.Checked; UpdateBoxFields(); };
            return host;
        }

        private Control BuildFit()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Fit pipe ends to their structures",
                "Each straight pipe end at a structure is trimmed (inside it) or extended (short of it) to the chosen face, using every structure's own " +
                "size and wall, or a box of your size. The pipe keeps its slope. You review every change before it is applied.");
            _cbFitFace.Items.AddRange(new object[] { "Inner wall face", "Outer wall face", "Structure centre", "A box of my size (length × width)" });
            ToolUi.Field(card, "Fit ends to", _cbFitFace);
            ToolUi.Field(card, "Box", ToolUi.Inline(_txtFitBoxL, new Label { Text = "long ×", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) }, _txtFitBoxW,
                new Label { Text = "m wide", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 0, 0) }), _tips,
                "The box is centred on each structure and turned with it: its length runs along the structure's own X axis (the direction its rotation gives), its width across.");
            ToolUi.Wide(card, _chkFitConnect);
            Button outlines = StyleUi.Secondary("Draw the outlines to check…", (s, e) => DrawFitOutlines());
            _tips.SetToolTip(outlines, "Draws the faces or boxes the chosen pipes' ends would be fitted to as closed polylines, to compare with the structures before fitting.");
            ToolUi.Wide(card, ToolUi.Inline(outlines, new Label { Text = "on layer " + FitOutlineLayer + " (not plotted)", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(6, 9, 0, 0) }));
            ToolUi.Wide(card, ToolUi.Note("Civil 3D keeps a pipe end connected only while it lies inside the structure's own size, so fitted ends sit 2 mm inside the face. " +
                                          "An end fitted outside its structure (a box larger than the part) keeps its fitted point and is left unconnected; the report names each one."));
            ToolUi.Add(stack, card);
            _cbFitFace.SelectedIndexChanged += (s, e) => UpdateBoxFields();
            return host;
        }

        private void UpdateBoxFields()
        {
            _txtFitBoxL.Enabled = _txtFitBoxW.Enabled = _cbFitFace.SelectedIndex == 3;
            _parBoxRow.Enabled = _chkParFit.Checked && _cbParFace.SelectedIndex == 3;
        }

        private Control BuildFlow()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Flow direction",
                "A pipe is reversed by swapping its start and end; every 3D point stays where it is. Checking first finds gaps and level steps between " +
                "pipe ends, and pipes pointing against the rest of their run.");
            ToolUi.Wide(card, _rbFlowVerify);
            ToolUi.Wide(card, _rbFlowAll);
            ToolUi.Add(stack, card);
            return host;
        }

        private Control BuildMerge()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Merge pipes",
                "Chosen straight pipes of the same family and size that meet end to end with no structure between them and run on in a straight line " +
                "become one pipe: the first keeps its start, runs to the last one's end and takes over its connection; the others are erased. " +
                "The list shows every chain before anything changes.");
            ToolUi.Add(stack, card);
            return host;
        }

        private Control BuildSwap()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel what = ToolUi.Card("Swap parts",
                "The chosen parts are listed by the part they are now, across every network: gravity pipes, structures, pressure pipes, fittings and " +
                "appurtenances. Give each row a target, or select several rows and give them one; rows without a target are left as they are. " +
                "A part from another list or the catalog is added to each network's own parts list when applied.");
            ToolUi.Wide(what, _chkSwapStructures);
            ToolUi.Wide(what, _chkSwapFittings);
            ToolUi.Field(what, "Gravity pipes keep their", ToolUi.Inline(_rbSwapInvert, _rbSwapCentre));
            ToolUi.Wide(what, ToolUi.Note("Structures keep their rim, sump, rotation and position. Pressure pipes keep their centreline."));
            ToolUi.Add(stack, what);

            TableLayoutPanel rows = ToolUi.Card("The chosen parts, by their current part");
            _swapGrid.Dock = DockStyle.None;
            _swapGrid.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _swapGrid.Height = 250;
            _swapGrid.ReadOnly = true;
            foreach ((string name, string header, int weight) in new[] { ("kind", "Kind", 13), ("current", "Current part", 34), ("count", "Parts", 7), ("networks", "Networks", 18), ("target", "Swap to", 26), ("gets", "Size it gets", 24) })
                _swapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
            _swapGrid.Columns["count"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _swapGrid.SelectionChanged += (s, e) => UpdateSwapTargetChooser();
            ToolUi.Wide(rows, _swapGrid);
            ToolUi.Wide(rows, ToolUi.Inline(StyleUi.Secondary("Refresh", (s, e) => RefreshSwap()), StyleUi.Secondary("Select all", (s, e) => _swapGrid.SelectAll()), _lblSwapSummary));
            ToolUi.Add(stack, rows);

            TableLayoutPanel target = ToolUi.Card("Target for the selected rows");
            ToolUi.Wide(target, _lblSwapSel);
            _swapPressure = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, MinimumSize = new Size(320, 0) };
            _swapPressure.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _swapPressure.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ToolUi.Field(_swapPressure, "Parts list", _cbSwapPList);
            ToolUi.Field(_swapPressure, "Size", _cbSwapPSize);
            _cbSwapPList.SelectedIndexChanged += (s, e) => FillPressureSizes();
            foreach (Control c in new Control[] { _swapPart, _swapStructure, _swapPressure }) { ToolUi.Wide(target, c); c.Visible = false; }
            FlowLayoutPanel sizeMode = ToolUi.Inline(_rbSwapExact, _rbSwapNearest);
            sizeMode.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;
            ToolUi.Field(target, "Size", sizeMode);
            ToolUi.Wide(target, ToolUi.Inline(StyleUi.Primary("Give the selected rows this target", (s, e) => SetSwapTarget(true)),
                                              StyleUi.Secondary("Leave them as they are", (s, e) => SetSwapTarget(false))));
            ToolUi.Add(stack, target);

            TableLayoutPanel sets = ToolUi.Card("Saved swap sets",
                "A set remembers the targets by kind, family and size (a nearest-size target by family, for any of its sizes) to reuse on other " +
                "selections and drawings. Its parts lists must be in the drawing.");
            ToolUi.Field(sets, "Set", _cbSwapSet);
            ToolUi.Wide(sets, ToolUi.Inline(StyleUi.Secondary("Give the rows the set's targets", (s, e) => ApplySwapSet()),
                                            StyleUi.Secondary("Save the targets as a set…", (s, e) => SaveSwapSet()),
                                            StyleUi.Secondary("Delete the set", (s, e) => DeleteSwapSet())));
            ToolUi.Add(stack, sets);
            return host;
        }

        // =====================================================================================
        // Swap: rows and targets
        // =====================================================================================

        private void RefreshSwap()
        {
            Dictionary<string, SwapTarget> keep = _swapGroups.Where(g => g.Target != null).ToDictionary(g => $"{g.Kind}|{g.Family}|{g.Size}", g => g.Target, StringComparer.OrdinalIgnoreCase);
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                _swapGroups = PartSwap.Collect(tr, _pipes, _parts, _chkSwapStructures.Checked, _chkSwapFittings.Checked);
            foreach (SwapGroup g in _swapGroups) if (keep.TryGetValue($"{g.Kind}|{g.Family}|{g.Size}", out SwapTarget t)) g.Target = t;
            ResolveSwap(_swapGroups);
            FillSwapGrid();
        }

        /// <summary>Works out the size each target gives its rows (families are read without keeping anything in the drawing).</summary>
        private void ResolveSwap(IEnumerable<SwapGroup> groups)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                foreach (SwapGroup g in groups)
                    try { PartSwap.Resolve(tr, acDoc.Database, g); }
                    catch (System.Exception ex) { g.Problem = ex.Message; }
                tr.Abort();
            }
        }

        private void FillSwapGrid()
        {
            HashSet<SwapGroup> selected = new HashSet<SwapGroup>(_swapGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as SwapGroup).Where(g => g != null));
            _swapGrid.Rows.Clear();
            foreach (SwapGroup g in _swapGroups)
            {
                string networks = g.Networks.Count == 1 ? g.Networks.First() : $"{g.Networks.Count} networks";
                string change = double.IsNaN(g.ToChange) || Math.Abs(g.ToChange) < 0.0005 ? "" : $"  ({g.ToChange * 1000:+0;-0} mm)";
                string gets = g.Target == null ? "" : g.Problem != null ? "Cannot: " + g.Problem : g.ToSize + change;
                int i = _swapGrid.Rows.Add(SwapGroup.KindText(g.Kind), g.Current, g.Ids.Count, networks, g.Target?.Label ?? "", gets);
                DataGridViewRow row = _swapGrid.Rows[i];
                row.Tag = g;
                row.Cells["networks"].ToolTipText = string.Join("\n", g.Networks.Take(30));
                if (g.Problem != null) row.Cells["gets"].Style.ForeColor = Color.Firebrick;
                // A size far from the current one (more than a quarter wider or narrower inside) is worth a second look.
                else if (!double.IsNaN(g.ToChange) && g.Width > 0 && Math.Abs(g.ToChange) > 0.25 * g.Width)
                {
                    row.Cells["gets"].Style.ForeColor = Color.DarkOrange;
                    row.Cells["gets"].ToolTipText = $"{g.ToSize}\nInside {g.Width * 1000:0} mm now; this size is {g.ToChange * 1000:+0;-0} mm. The family may have no closer size.";
                }
                else if (g.Target == null) row.DefaultCellStyle.ForeColor = StyleUi.Muted;
                row.Selected = selected.Contains(g);
            }
            int targeted = _swapGroups.Count(g => g.Target != null && g.Problem == null);
            _lblSwapSummary.Text = _swapGroups.Count == 0 ? "Choose pipes or parts at the top."
                : $"{_swapGroups.Sum(g => g.Ids.Count)} part(s) in {_swapGroups.Count} row(s); {_swapGroups.Where(g => g.Target != null && g.Problem == null).Sum(g => g.Ids.Count)} part(s) in {targeted} row(s) will be swapped.";
            UpdateSwapTargetChooser();
        }

        private List<SwapGroup> SelectedSwapGroups() => _swapGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as SwapGroup).Where(g => g != null).ToList();

        /// <summary>Shows the chooser for the kind of the selected rows (one kind at a time), opened on the first row's network.</summary>
        private void UpdateSwapTargetChooser()
        {
            List<SwapGroup> sel = SelectedSwapGroups();
            List<SwapKind> kinds = sel.Select(g => g.Kind).Distinct().ToList();
            bool gravityPipes = kinds.Count == 1 && kinds[0] == SwapKind.Pipe, structures = kinds.Count == 1 && kinds[0] == SwapKind.Structure;
            bool pressure = kinds.Count > 0 && kinds.All(k => k >= SwapKind.PressurePipe) && kinds.Distinct().Count() == 1;
            _swapPart.Visible = gravityPipes;
            _swapStructure.Visible = structures;
            _swapPressure.Visible = pressure;
            _lblSwapSel.Text = sel.Count == 0 ? "Select one or more rows above (Ctrl or Shift for several)."
                : kinds.Count > 1 ? "The selected rows are of different kinds; select rows of one kind to give them a target."
                : $"{sel.Count} row(s) of {SwapGroup.KindText(kinds[0]).ToLowerInvariant()}s, {sel.Sum(g => g.Ids.Count)} part(s).";
            if (pressure) FillPressureSizes();
        }

        private void FillPressureLists()
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            List<(ObjectId id, string name)> lists;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) lists = PartSwap.PressureLists(tr, db);
            _cbSwapPList.Items.Clear();
            foreach ((ObjectId id, string name) l in lists) _cbSwapPList.Items.Add(new PressureItem { ListId = l.id, Name = l.name });
            PressureItem want = _cbSwapPList.Items.Cast<PressureItem>().FirstOrDefault(i => i.Name == _s.SwapPressureList);
            if (want != null) _cbSwapPList.SelectedItem = want;
            else if (_cbSwapPList.Items.Count > 0) _cbSwapPList.SelectedIndex = 0;
        }

        /// <summary>The chosen pressure list's sizes of the selected rows' domain (pipes, fittings or appurtenances).</summary>
        private void FillPressureSizes()
        {
            SwapGroup first = SelectedSwapGroups().FirstOrDefault(g => g.IsPressure);
            if (first == null || !(_cbSwapPList.SelectedItem is PressureItem list)) { _cbSwapPSize.Items.Clear(); return; }
            string keep = (_cbSwapPSize.SelectedItem as PressureItem)?.Guid;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            List<PressureItem> items = new List<PressureItem>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (Autodesk.Civil.DatabaseServices.Styles.PressurePartSize s in PartSwap.PressureSizes(tr, list.ListId, first.PressureDomain))
                {
                    string guid = "", name = "";
                    try { guid = s.PartSizeGuid; name = s.Description; } catch (System.Exception) { continue; }
                    items.Add(new PressureItem { ListId = list.ListId, Guid = guid, Name = string.IsNullOrWhiteSpace(name) ? guid : name, Family = PartSwap.PressureFamily(s),
                                                 Diameter = PartSwap.PressureDiameter(s, null, first.Kind == SwapKind.PressurePipe) });
                }
            _cbSwapPSize.Items.Clear();
            foreach (PressureItem i in items.OrderBy(i => i.Family, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Diameter).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)) _cbSwapPSize.Items.Add(i);
            // Open on the size chosen before, else the first row's own, else the one nearest its inside size.
            PressureItem pick = _cbSwapPSize.Items.Cast<PressureItem>().FirstOrDefault(i => i.Guid == keep)
                                ?? _cbSwapPSize.Items.Cast<PressureItem>().FirstOrDefault(i => string.Equals(i.Name, first.Size, StringComparison.OrdinalIgnoreCase))
                                ?? (first.Width > 0 ? _cbSwapPSize.Items.Cast<PressureItem>().Where(i => i.Diameter > 0).OrderBy(i => Math.Abs(i.Diameter - first.Width)).FirstOrDefault() : null);
            if (pick != null) _cbSwapPSize.SelectedItem = pick;
            else if (_cbSwapPSize.Items.Count > 0) _cbSwapPSize.SelectedIndex = 0;
        }

        private void SetSwapTarget(bool give)
        {
            List<SwapGroup> sel = SelectedSwapGroups();
            if (sel.Count == 0) { MessageBox.Show(this, "Select one or more rows in the list first.", Text); return; }
            SwapTarget target = null;
            if (give)
            {
                if (sel.Select(g => g.Kind).Distinct().Count() > 1) { MessageBox.Show(this, "Select rows of one kind (e.g. only pipes or only structures) to give them one target.", Text); return; }
                SwapKind kind = sel[0].Kind;
                bool nearest = _rbSwapNearest.Checked;
                if (kind == SwapKind.Pipe || kind == SwapKind.Structure)
                {
                    PartPick pick = (kind == SwapKind.Pipe ? _swapPart : _swapStructure).Pick;
                    if (pick == null) { MessageBox.Show(this, "Choose a parts list, a family and a size.", Text); return; }
                    target = new SwapTarget { Nearest = nearest, Gravity = pick };
                }
                else
                {
                    if (!(_cbSwapPSize.SelectedItem is PressureItem size) || !(_cbSwapPList.SelectedItem is PressureItem list)) { MessageBox.Show(this, "Choose a pressure parts list and a size.", Text); return; }
                    target = new SwapTarget { Nearest = nearest, PressureListId = list.ListId, PressureListName = list.Name, PressureFamily = size.Family ?? "", PressureSizeGuid = size.Guid, PressureSizeName = size.Name };
                }
            }
            foreach (SwapGroup g in sel) g.Target = target;
            ResolveSwap(sel);
            FillSwapGrid();
        }

        private void FillSwapSets(string select = null)
        {
            _cbSwapSet.Items.Clear();
            foreach (SwapSet set in _s.SwapSets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) _cbSwapSet.Items.Add(set.Name);
            ToolUi.Select(_cbSwapSet, select);
        }

        private void ApplySwapSet()
        {
            SwapSet set = _s.SwapSets.FirstOrDefault(x => x.Name == _cbSwapSet.SelectedItem as string);
            if (set == null) { MessageBox.Show(this, "There is no saved set yet: give rows their targets, then \"Save the targets as a set\".", Text); return; }
            if (_swapGroups.Count == 0) RefreshSwap();
            List<string> missing = new List<string>();
            int count;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) count = PartSwap.ApplySet(tr, db, set, _swapGroups, missing);
            ResolveSwap(_swapGroups);
            FillSwapGrid();
            MessageBox.Show(this, $"The set \"{set.Name}\" gave {count} of the {_swapGroups.Count} row(s) a target." +
                                  (missing.Count > 0 ? $"\n\nNot in this drawing, so those rules were left out: {string.Join(", ", missing)}." : ""), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveSwapSet()
        {
            List<SwapRule> rules = PartSwap.RulesOf(_swapGroups);
            if (rules.Count == 0) { MessageBox.Show(this, "No row has a target yet.", Text); return; }
            string name = FormPartsLists.AskText(this, "Save swap set", "Name of the set:", _cbSwapSet.SelectedItem as string ?? "My swaps");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            SwapSet existing = _s.SwapSets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                DialogResult answer = MessageBox.Show(this, $"The set \"{existing.Name}\" exists. Add these targets to it (Yes) or replace it (No)?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No) existing.Rules.Clear();
                foreach (SwapRule r in rules)
                {
                    existing.Rules.RemoveAll(x => x.Kind == r.Kind && string.Equals(x.Family, r.Family, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Size ?? "", r.Size ?? "", StringComparison.OrdinalIgnoreCase));
                    existing.Rules.Add(r);
                }
            }
            else _s.SwapSets.Add(new SwapSet { Name = name, Rules = rules });
            _s.Save();
            FillSwapSets(existing?.Name ?? name);
        }

        private void DeleteSwapSet()
        {
            if (!(_cbSwapSet.SelectedItem is string name)) return;
            if (MessageBox.Show(this, $"Delete the swap set \"{name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _s.SwapSets.RemoveAll(x => x.Name == name);
            _s.Save();
            FillSwapSets();
        }

        private Control BuildGravity()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Pressure to gravity",
                "Each chosen pressure pipe gets a gravity copy (straight or curved) in a new network. Ends at fittings run on to the fitting's centre " +
                "when it lies on the pipe's line, so the gravity pipes meet.");
            ToolUi.Field(card, "New network", _txtGNet);
            ToolUi.Wide(card, ToolUi.Note("The new network uses the parts list chosen here."));
            ToolUi.Wide(card, _gPart);
            ToolUi.Add(stack, card);
            return host;
        }

        private void ShowAction()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _nav.Selected;
            _btnRun.Text = RunText[_nav.Selected];
            UpdatePipeSummary();
        }

        // =====================================================================================
        // Drawing data
        // =====================================================================================

        private void ReadDrawing()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in doc.GetPipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Network n) _networks[n.Name] = id;
                foreach (ObjectId id in doc.GetPressurePipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is PressurePipeNetwork n) _networks[n.Name + " [Pressure]"] = id;
            }
            foreach (string n in _networks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbNetwork.Items.Add(n);
            if (_cbNetwork.Items.Count > 0) _cbNetwork.SelectedIndex = 0;
        }

        /// <summary>
        /// Opens the part choosers on the network of the first chosen gravity pipe (a pipe can only use its network's parts
        /// list); the new-network choosers keep their own list.
        /// </summary>
        private void UpdatePartContexts(bool force = false)
        {
            ObjectId net = ObjectId.Null;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in _pipes)
                    if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Pipe p) { net = p.NetworkId; break; }
            if (net == _partNetwork && !force) return;
            _partNetwork = net;
            _splitPart.SetContext(net, net.IsNull ? _s.SplitStructureList : null, _splitPart.FamilyName ?? _s.SplitStructureFamily, _splitPart.SizeName ?? _s.SplitStructureSize);
            _swapPart.SetContext(net, net.IsNull ? _s.SwapPartsList : null, _swapPart.FamilyName ?? _s.SwapFamily, _swapPart.SizeName ?? _s.SwapSize);
            _swapStructure.SetContext(net, net.IsNull ? _s.SwapStructureList : null, _swapStructure.FamilyName ?? _s.SwapStructureFamily, _swapStructure.SizeName ?? _s.SwapStructureSize);
            if (!_rbConNewNet.Checked) _conPart.SetContext(net, net.IsNull ? _s.ConduitPartsList : null, _conPart.FamilyName ?? _s.ConduitFamily, _conPart.SizeName ?? _s.ConduitSize);
            if (!_rbNewNet.Checked) _parPart.SetContext(net, net.IsNull ? _s.ParallelPartsList : null, _parPart.FamilyName ?? _s.ParallelFamily, _parPart.SizeName ?? _s.ParallelSize);
        }

        private void ApplySettings()
        {
            _cbSplitMode.SelectedIndex = Math.Max(0, Math.Min(2, _s.SplitMode));
            _numParts.Value = Math.Max(2, Math.Min(100, _s.SplitParts));
            _txtEvery.Text = ToolUi.Format(_s.SplitEvery);
            _txtPickTol.Text = ToolUi.Format(_s.SplitPickTolerance);
            _chkSplitStructure.Checked = _s.SplitAddStructure;
            _splitPart.Enabled = _s.SplitAddStructure;

            _txtH.Text = _s.ParallelHorizontal;
            _txtV.Text = _s.ParallelVertical;
            _rbSameNet.Checked = _s.ParallelSameNetwork;
            _rbNewNet.Checked = !_s.ParallelSameNetwork;
            _txtNewNet.Text = _s.ParallelNetworkName;
            _txtNewNet.Enabled = !_s.ParallelSameNetwork;
            _rbOwnSize.Checked = _s.ParallelOwnSize;
            _rbChosenSize.Checked = !_s.ParallelOwnSize;
            _parPart.Enabled = !_s.ParallelOwnSize;
            _parPart.SetContext(ObjectId.Null, _s.ParallelPartsList, _s.ParallelFamily, _s.ParallelSize);
            _chkParFit.Checked = _s.ParallelFit;
            _cbParFace.SelectedIndex = Math.Max(0, Math.Min(3, _s.ParallelFace));
            _cbParFace.Enabled = _s.ParallelFit;
            _txtParBoxL.Text = ToolUi.Format(_s.ParallelBoxLength);
            _txtParBoxW.Text = ToolUi.Format(_s.ParallelBoxWidth);

            ApplyConduitSettings();
            _cbFitFace.SelectedIndex = Math.Max(0, Math.Min(3, _s.FitFace));
            _chkFitConnect.Checked = _s.FitConnect;
            _txtFitBoxL.Text = ToolUi.Format(_s.FitBoxLength);
            _txtFitBoxW.Text = ToolUi.Format(_s.FitBoxWidth);
            UpdateBoxFields();
            _rbFlowVerify.Checked = _s.FlowVerify;
            _rbFlowAll.Checked = !_s.FlowVerify;
            _txtGNet.Text = _s.GravityNetworkName;
            _gPart.SetContext(ObjectId.Null, _s.GravityPartsList, _s.GravityFamily, _s.GravitySize);
            _chkSwapStructures.Checked = _s.SwapStructures;
            _chkSwapFittings.Checked = _s.SwapFittings;
            _rbSwapCentre.Checked = _s.SwapHoldCentre;
            _rbSwapInvert.Checked = !_s.SwapHoldCentre;
            _rbSwapNearest.Checked = _s.SwapNearest;
            _rbSwapExact.Checked = !_s.SwapNearest;
            _chkSwapStructures.CheckedChanged += (s, e) => RefreshSwap();
            _chkSwapFittings.CheckedChanged += (s, e) => RefreshSwap();
            FillPressureLists();
            FillSwapSets();
            UpdatePartContexts(true);
        }

        private void SaveSettings()
        {
            _s.Action = _nav.Selected;
            _s.SplitMode = _cbSplitMode.SelectedIndex;
            _s.SplitParts = (int)_numParts.Value;
            if (ToolUi.TryNumber(_txtEvery, out double every) && every > 0) _s.SplitEvery = every;
            if (ToolUi.TryNumber(_txtPickTol, out double tol) && tol > 0) _s.SplitPickTolerance = tol;
            _s.SplitAddStructure = _chkSplitStructure.Checked;
            _s.SplitStructureList = _splitPart.ListName;
            _s.SplitStructureFamily = _splitPart.FamilyName;
            _s.SplitStructureSize = _splitPart.SizeName;
            _s.ParallelHorizontal = _txtH.Text;
            _s.ParallelVertical = _txtV.Text;
            _s.ParallelSameNetwork = _rbSameNet.Checked;
            _s.ParallelNetworkName = _txtNewNet.Text;
            _s.ParallelPartsList = _parPart.ListName;
            _s.ParallelOwnSize = _rbOwnSize.Checked;
            _s.ParallelFamily = _parPart.FamilyName;
            _s.ParallelSize = _parPart.SizeName;
            _s.ParallelFit = _chkParFit.Checked;
            _s.ParallelFace = _cbParFace.SelectedIndex;
            if (ToolUi.TryNumber(_txtParBoxL, out double pl) && pl > 0) _s.ParallelBoxLength = pl;
            if (ToolUi.TryNumber(_txtParBoxW, out double pw) && pw > 0) _s.ParallelBoxWidth = pw;
            SaveConduitSettings();
            _s.FitFace = _cbFitFace.SelectedIndex;
            _s.FitConnect = _chkFitConnect.Checked;
            if (ToolUi.TryNumber(_txtFitBoxL, out double fl) && fl > 0) _s.FitBoxLength = fl;
            if (ToolUi.TryNumber(_txtFitBoxW, out double fw) && fw > 0) _s.FitBoxWidth = fw;
            _s.FlowVerify = _rbFlowVerify.Checked;
            _s.SwapPartsList = _swapPart.ListName;
            _s.SwapFamily = _swapPart.FamilyName;
            _s.SwapSize = _swapPart.SizeName;
            _s.SwapStructureList = _swapStructure.ListName;
            _s.SwapStructureFamily = _swapStructure.FamilyName;
            _s.SwapStructureSize = _swapStructure.SizeName;
            if (_cbSwapPList.SelectedItem is PressureItem pressureList) _s.SwapPressureList = pressureList.Name;
            _s.SwapNearest = _rbSwapNearest.Checked;
            _s.SwapStructures = _chkSwapStructures.Checked;
            _s.SwapFittings = _chkSwapFittings.Checked;
            _s.SwapHoldCentre = _rbSwapCentre.Checked;
            _s.GravityNetworkName = _txtGNet.Text;
            _s.GravityPartsList = _gPart.ListName;
            _s.GravityFamily = _gPart.FamilyName;
            _s.GravitySize = _gPart.SizeName;
            _s.Save();
        }

        private void FillGrid()
        {
            if (!ToolUi.TryNumber(_txtSpX, out double sx) || !ToolUi.TryNumber(_txtSpZ, out double sz)) { MessageBox.Show(this, "Enter the spacing as numbers.", Text); return; }
            string Centred(int n, double spacing) => string.Join(", ", Enumerable.Range(0, n).Select(i => ToolUi.Format((i - (n - 1) / 2.0) * spacing)));
            _txtH.Text = Centred((int)_numCols.Value, sx);
            _txtV.Text = Centred((int)_numRows.Value, sz);
        }

        // =====================================================================================
        // Choosing pipes
        // =====================================================================================

        private (int gravity, int pressure) CountPipes()
        {
            int g = 0, p = 0;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in _pipes)
                {
                    if (id.IsErased) continue;
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Pipe) g++; else if (obj is PressurePipe) p++;
                }
            return (g, p);
        }

        private void UpdatePipeSummary()
        {
            _pipes.RemoveAll(id => id.IsErased || !id.IsValid);
            _parts.RemoveAll(id => id.IsErased || !id.IsValid);
            UpdatePartContexts();
            (int g, int p) = CountPipes();
            _lblPipes.Text = (_pipes.Count == 0 ? "No pipes chosen yet." : $"{g} gravity and {p} pressure pipe(s) chosen.") +
                             (_parts.Count > 0 ? $" Also {_parts.Count} structure(s), fitting(s) or appurtenance(s) (used by Swap parts)." : "");
            if (_nav.Selected == ASwap) RefreshSwap();
            RefreshConduitSection();
            int a = _nav.Selected;
            string usable = a == AGravity ? $"{p} pressure pipe(s) will be converted."
                : a == AParallel ? $"{g + p} pipe(s) will be copied."
                : a == AConduits ? ConduitStatus(g)
                : a == ASwap ? "Rows with a target are swapped; the others are left as they are."
                : a == ASplit && _cbSplitMode.SelectedIndex == 0 ? (g > 0 ? $"Points split the nearest of the {g} chosen gravity pipe(s)." : "With no pipes chosen, points split the nearest gravity pipe in the drawing.")
                : $"{g} gravity pipe(s) will be used.";
            _lblStatus.Text = usable;
        }

        private void SelectPipes()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect pipes (gravity or pressure); structures, fittings and appurtenances are taken for Swap parts: " });
                if (psr.Status != PromptStatus.OK) return;
                _pipes.Clear();
                _parts.Clear();
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Pipe || obj is PressurePipe) _pipes.Add(id);
                        else if (obj is Structure || obj is PressureFitting || obj is PressureAppurtenance) _parts.Add(id);
                    }
            }
            finally { Show(); UpdatePipeSummary(); }
        }

        private void SelectNetworks()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect any pipe, structure or fitting of each network: " });
                if (psr.Status != PromptStatus.OK) return;
                _pipes.Clear();
                _parts.Clear();
                int count = 0;
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    HashSet<ObjectId> nets = new HashSet<ObjectId>();
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Part part) nets.Add(part.NetworkId);
                        else if (obj is PressurePart pp) nets.Add(pp.NetworkId);
                    }
                    foreach (ObjectId netId in nets) AddNetworkParts(tr, netId);
                    count = nets.Count;
                }
                acDoc.Editor.WriteMessage($"\n{count} network(s), {_pipes.Count} pipe(s) chosen.");
            }
            finally { Show(); UpdatePipeSummary(); }
        }

        /// <summary>Start and end pipe pairs: each adds the connected route between them (the straightest one, as for cover).</summary>
        private void SelectPaths()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            PromptEntityOptions Pick(string message, bool allowNone)
            {
                PromptEntityOptions o = new PromptEntityOptions(message) { AllowNone = allowNone };
                o.SetRejectMessage("\nPick a gravity pipe.");
                o.AddAllowedClass(typeof(Pipe), false);
                return o;
            }
            Hide();
            try
            {
                List<ObjectId> chosen = new List<ObjectId>();
                int paths = 0;
                while (true)
                {
                    PromptEntityResult a = ed.GetEntity(Pick(paths == 0 ? "\nPick the start pipe of the path: " : "\nPick the start pipe of the next path, or Enter when done: ", paths > 0));
                    if (a.Status != PromptStatus.OK) break;
                    PromptEntityResult b = ed.GetEntity(Pick("\nPick the end pipe of the path: ", false));
                    if (b.Status != PromptStatus.OK) break;
                    using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    {
                        CoverJob job = CoverJobs.FromPick(tr, a.ObjectId, b.ObjectId, 1.0, CoverJobs.CenterToleranceDefault, false, out string error);
                        if (job == null) { ed.WriteMessage("\n" + error); continue; }
                        foreach ((ObjectId id, bool _) in job.Pipes) if (!chosen.Contains(id)) chosen.Add(id);
                        paths++;
                        ed.WriteMessage($"\nPath {paths}: {job.Pipes.Count} pipe(s).");
                    }
                }
                if (paths == 0) return;
                _pipes.Clear();
                _parts.Clear();
                _pipes.AddRange(chosen);
            }
            finally { Show(); UpdatePipeSummary(); }
        }

        private void AddNetwork()
        {
            if (!(_cbNetwork.SelectedItem is string name) || !_networks.TryGetValue(name, out ObjectId netId)) return;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) AddNetworkParts(tr, netId);
            UpdatePipeSummary();
        }

        /// <summary>A network's pipes, and its structures, fittings and appurtenances (for Swap parts, which also reaches ones no pipe meets).</summary>
        private void AddNetworkParts(Transaction tr, ObjectId netId)
        {
            DBObject net = tr.GetObject(netId, OpenMode.ForRead);
            ObjectIdCollection pipes = net is Network n ? n.GetPipeIds() : net is PressurePipeNetwork pn ? pn.GetPipeIds() : new ObjectIdCollection();
            foreach (ObjectId id in pipes) if (!_pipes.Contains(id)) _pipes.Add(id);
            List<ObjectId> others = new List<ObjectId>();
            if (net is Network gn) others.AddRange(gn.GetStructureIds().Cast<ObjectId>());
            else if (net is PressurePipeNetwork pnet) { others.AddRange(pnet.GetFittingIds().Cast<ObjectId>()); others.AddRange(pnet.GetAppurtenanceIds().Cast<ObjectId>()); }
            HashSet<ObjectId> have = new HashSet<ObjectId>(_parts);
            foreach (ObjectId id in others) if (have.Add(id)) _parts.Add(id);
        }

        private void ShowPipes()
        {
            if (_pipes.Count == 0) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                using (TransientMarkers markers = new TransientMarkers())
                {
                    Extents3d? ext = null;
                    using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                        foreach (ObjectId id in _pipes)
                        {
                            List<Point3d> path = PathOf(tr.GetObject(id, OpenMode.ForRead));
                            if (path == null) continue;
                            markers.AddPath(path, 4, 0.3);
                            foreach (Point3d p in path) { Extents3d e = ext ?? new Extents3d(p, p); e.AddPoint(p); ext = e; }
                        }
                    if (ext.HasValue) ViewHelper.ZoomTo(ext.Value, 1.2, 20.0);
                    acDoc.Editor.UpdateScreen();
                    acDoc.Editor.GetString(new PromptStringOptions($"\n{_pipes.Count} chosen pipe(s) highlighted. Press Enter to return: ") { AllowSpaces = true });
                }
            }
            finally { Show(); }
        }

        private static List<Point3d> PathOf(DBObject obj)
        {
            if (obj is Pipe p)
            {
                ArcInfo? arc = null;
                if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
                return PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 1.0, 200);
            }
            if (obj is PressurePipe pp)
            {
                ArcInfo? arc = null;
                if (pp.IsCurve && PipeArcGeometry.TryGetPressureArc(pp, out ArcInfo a)) arc = a;
                return PipeArcGeometry.SamplePath(pp.StartPoint, pp.EndPoint, arc, 1.0, 200);
            }
            return null;
        }

        private List<ObjectId> GravityPipes()
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                return _pipes.Where(id => !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Pipe).ToList();
        }

        private List<ObjectId> PressurePipes()
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                return _pipes.Where(id => !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is PressurePipe).ToList();
        }

        // =====================================================================================
        // Running a task
        // =====================================================================================

        private void Run()
        {
            SaveSettings();
            try
            {
                switch (_nav.Selected)
                {
                    case ASplit: RunSplit(); break;
                    case AParallel: RunParallel(); break;
                    case AConduits: RunConduits(); break;
                    case AFit: RunFit(); break;
                    case AFlow: RunFlow(); break;
                    case AMerge: RunMerge(); break;
                    case ASwap: RunSwap(); break;
                    case AGravity: RunGravity(); break;
                }
            }
            catch (System.Exception ex)
            {
                // An exception escaping a modal form's handler surfaces as AutoCAD's unhandled-exception dialog.
                MessageBox.Show(this, $"The task stopped with an error and nothing was changed.\n\n{ex.GetType().Name}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdatePipeSummary();
        }

        private bool NeedGravity(List<ObjectId> ids)
        {
            if (ids.Count > 0) return true;
            MessageBox.Show(this, "Choose some gravity pipes first (Select pipes, Select network(s) or Select path at the top).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        /// <summary>Runs a change in one locked transaction (one UNDO) and reports it.</summary>
        private NetworkEditOps.Counts Change(Func<Transaction, Editor, NetworkEditOps.Counts> work)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            NetworkEditOps.Counts c;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                c = work(tr, acDoc.Editor);
                tr.Commit();
            }
            acDoc.Editor.Regen();
            return c;
        }

        private void Report(string what, NetworkEditOps.Counts c, string extra = "")
        {
            if (c.Log.Count > 0) extra = (extra + "\n\nParts lists:\n• " + string.Join("\n• ", c.Log)).Trim();
            string text = $"{what}\n\n{extra}" + (c.Failed + c.Skipped > 0 ? $"\nSkipped: {c.Skipped}   Failed: {c.Failed}\nSee the command line for details." : "") + "\n\nUNDO reverses the whole change.";
            _lblStatus.Text = what;
            MessageBox.Show(this, text.Replace("\n\n\n", "\n\n"), Text, MessageBoxButtons.OK, c.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private bool Confirm(string title, string intro, string[] columns, List<PreviewRow> rows, string apply)
        {
            using (FormStylePreview preview = new FormStylePreview(title, intro, columns, rows, apply, false, "Network Editor - " + title))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return false;
                HashSet<object> chosen = new HashSet<object>(preview.Chosen);
                rows.RemoveAll(r => !chosen.Contains(r.Tag));
                return rows.Count > 0;
            }
        }

        private void RunSplit()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            List<ObjectId> gravity = GravityPipes();
            if (_chkSplitStructure.Checked && _splitPart.Pick == null) { MessageBox.Show(this, "Choose the structure for the split points (parts list, family, size).", Text); return; }
            List<NetworkEditOps.SplitPlan> plans;
            int missed = 0;
            if (_cbSplitMode.SelectedIndex == 0)
            {
                if (!ToolUi.TryNumber(_txtPickTol, out double tol) || tol <= 0) { MessageBox.Show(this, "Enter a positive pick distance.", Text); return; }
                List<ObjectId> candidates = gravity;
                if (candidates.Count == 0)
                    using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                        foreach (ObjectId netId in CivilApplication.ActiveDocument.GetPipeNetworkIds())
                            if (tr.GetObject(netId, OpenMode.ForRead) is Network n) candidates.AddRange(n.GetPipeIds().Cast<ObjectId>());
                List<Point3d> picks = new List<Point3d>();
                Hide();
                try
                {
                    using (TransientMarkers markers = new TransientMarkers())
                    {
                        while (true)
                        {
                            PromptPointOptions ppo = new PromptPointOptions($"\nPick split point {picks.Count + 1} on a pipe (Enter when done): ") { AllowNone = true };
                            PromptPointResult r = acDoc.Editor.GetPoint(ppo);
                            if (r.Status == PromptStatus.None) break;
                            if (r.Status != PromptStatus.OK) { picks.Clear(); break; }
                            picks.Add(r.Value);
                            markers.AddRing(r.Value, 0.5, 1, 0.12);
                            acDoc.Editor.UpdateScreen();
                        }
                    }
                }
                finally { Show(); }
                if (picks.Count == 0) return;
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    plans = NetworkEditOps.PlanSplitAtPoints(tr, candidates, picks, tol, out missed);
            }
            else
            {
                if (!NeedGravity(gravity)) return;
                double every = 0;
                if (_cbSplitMode.SelectedIndex == 2 && (!ToolUi.TryNumber(_txtEvery, out every) || every <= 0)) { MessageBox.Show(this, "Enter a positive length.", Text); return; }
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    plans = NetworkEditOps.PlanSplit(tr, gravity, (int)_numParts.Value, _cbSplitMode.SelectedIndex == 2 ? every : 0);
            }
            if (plans.Count == 0) { MessageBox.Show(this, missed > 0 ? $"None of the {missed} picked point(s) lies on a pipe (within the pick distance, and not at an end)." : "No pipe is long enough to split that way.", Text); return; }

            List<PreviewRow> rows = plans.Select(p => new PreviewRow
            {
                Tag = p,
                Cells = new[] { p.Name, p.Curved ? "Curved" : "Straight", ToolUi.Format(Math.Round(p.Length, 2)), (p.Points.Count + 1).ToString(),
                    string.Join("; ", p.Points.Take(4).Select(pt => $"{pt.X:0.00}, {pt.Y:0.00}")) + (p.Points.Count > 4 ? " …" : "") }
            }).ToList();
            string intro = $"{plans.Count} pipe(s) will be split{(missed > 0 ? $"; {missed} picked point(s) were not on a pipe and are ignored" : "")}." +
                           (_chkSplitStructure.Checked ? $" A {_splitPart.Pick?.Label} joins the pieces at every split point." : " The pieces are left touching, without structures.");
            if (!Confirm("Split pipes", intro, new[] { "Pipe", "Type", "Length (m)", "Pieces", "Split points" }, rows, "Split")) return;

            List<NetworkEditOps.SplitPlan> chosen = rows.Select(r => (NetworkEditOps.SplitPlan)r.Tag).ToList();
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplySplit(tr, chosen, _chkSplitStructure.Checked ? _splitPart.Pick : null, ed));
            Report($"Split {c.Done} pipe(s) into {c.Done + c.Created} pieces.", c, c.Structures > 0 ? $"Structures added: {c.Structures}" : "");
        }

        private void RunParallel()
        {
            if (!ToolUi.TryNumberList(_txtH.Text, out List<double> h) || !ToolUi.TryNumberList(string.IsNullOrWhiteSpace(_txtV.Text) ? "0" : _txtV.Text, out List<double> v))
            {
                MessageBox.Show(this, "Enter the offsets as numbers separated by commas, for example -0.4, -0.2, 0, 0.2, 0.4.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_pipes.Count == 0) { MessageBox.Show(this, "Choose some pipes first.", Text); return; }
            int perPipe = h.Count * v.Count - (h.Any(x => Math.Abs(x) < 1e-9) && v.Any(x => Math.Abs(x) < 1e-9) ? 1 : 0);
            if (perPipe <= 0) { MessageBox.Show(this, "Every offset is the source position itself; nothing would be copied.", Text); return; }
            NetworkEditOps.ParallelOptions o = new NetworkEditOps.ParallelOptions
            {
                Horizontal = h, Vertical = v, SameNetwork = _rbSameNet.Checked, NewNetworkName = string.IsNullOrWhiteSpace(_txtNewNet.Text) ? "Parallel copies" : _txtNewNet.Text.Trim(),
                Part = _rbChosenSize.Checked ? _parPart.Pick : null,
                PartsListId = _rbChosenSize.Checked && _parPart.Pick != null ? _parPart.Pick.PartsListId : ObjectId.Null,
                FitToStructures = _chkParFit.Checked, Face = _cbParFace.SelectedIndex == 3 ? StructureFace.InnerWall : (StructureFace)Math.Max(0, _cbParFace.SelectedIndex)
            };
            if (_chkParFit.Checked && _cbParFace.SelectedIndex == 3)
            {
                if (!ToolUi.TryNumber(_txtParBoxL, out o.BoxLength) || !ToolUi.TryNumber(_txtParBoxW, out o.BoxWidth) || o.BoxLength <= 0 || o.BoxWidth <= 0)
                { MessageBox.Show(this, "Enter the box's length and width as positive numbers.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            if (MessageBox.Show(this, $"Create {perPipe} cop{(perPipe == 1 ? "y" : "ies")} of each of the {_pipes.Count} chosen pipe(s) ({perPipe * _pipes.Count} new pipes)" +
                                      (o.SameNetwork ? " in their own networks?" : $" in the new network \"{o.NewNetworkName}\"?"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyParallel(tr, CivilApplication.ActiveDocument, acDoc.Database, new List<ObjectId>(_pipes), o, ed));
            Report($"Created {c.Created} copies of {c.Done} pipe(s).", c);
        }

        /// <summary>The fit chosen on the page: the face, and the box (length, width; 0 = each structure's own size). False when the box is not valid.</summary>
        private bool FitChoice(out StructureFace face, out double boxLength, out double boxWidth, out string what)
        {
            boxLength = boxWidth = 0;
            int i = Math.Max(0, _cbFitFace.SelectedIndex);
            face = i == 3 ? StructureFace.InnerWall : (StructureFace)i;
            what = new[] { "the inner wall face", "the outer wall face", "the structure centre" }.ElementAtOrDefault(i) ?? "";
            if (i != 3) return true;
            if (!ToolUi.TryNumber(_txtFitBoxL, out boxLength) || !ToolUi.TryNumber(_txtFitBoxW, out boxWidth) || boxLength <= 0 || boxWidth <= 0)
            {
                MessageBox.Show(this, "Enter the box's length and width as positive numbers.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            what = $"a {ToolUi.Format(boxLength)} × {ToolUi.Format(boxWidth)} m box on each structure";
            return true;
        }

        private void RunFit()
        {
            List<ObjectId> gravity = GravityPipes();
            if (!NeedGravity(gravity)) return;
            if (!FitChoice(out StructureFace face, out double boxL, out double boxW, out string what)) return;
            List<ManholeAdjustment> plan;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                plan = NetworkEditOps.PlanFit(tr, gravity, face, _chkFitConnect.Checked, boxL, boxW);
            if (plan.Count == 0) { MessageBox.Show(this, "None of the chosen pipes is a straight gravity pipe.", Text); return; }
            List<ManholeAdjustment> chosen;
            Hide();
            try
            {
                using (FormManholePreview preview = new FormManholePreview(plan, "Fit to " + what))
                {
                    if (preview.ShowDialog() != DialogResult.OK) return;
                    chosen = preview.Chosen;
                }
            }
            finally { Show(); }
            if (chosen.Count == 0) return;
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyFit(tr, chosen, ed));
            string extra = (c.Structures > 0 ? $"Loose ends connected: {c.Structures}\n" : "") +
                           (c.Unconnected > 0 ? $"Ends left unconnected (fitted outside their structure's own size): {c.Unconnected}; see the command line." : "");
            Report($"Fitted {c.Done} pipe(s) to {what}.", c, extra.Trim());
        }

        /// <summary>Draws the faces or boxes the chosen pipes' ends would be fitted to, to check them before fitting.</summary>
        private void DrawFitOutlines()
        {
            List<ObjectId> gravity = GravityPipes();
            if (!NeedGravity(gravity)) return;
            if (!FitChoice(out StructureFace face, out double boxL, out double boxW, out string what)) return;
            if (face == StructureFace.Center && boxL <= 0) { MessageBox.Show(this, "Centres have no outline; choose a wall face or a box.", Text); return; }
            List<ObjectId> structures;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                structures = NetworkEditOps.PlanFit(tr, gravity, face, _chkFitConnect.Checked, boxL, boxW)
                    .SelectMany(a => new[] { a.StartStructure, a.EndStructure }).Where(id => !id.IsNull).Distinct().ToList();
            if (structures.Count == 0) { MessageBox.Show(this, "None of the chosen pipes ends at a structure.", Text); return; }
            int drawn = 0;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                drawn = NetworkEditOps.DrawFitOutlines(tr, acDoc.Database, structures, face, boxL, boxW, FitOutlineLayer);
                tr.Commit();
            }
            acDoc.Editor.Regen();
            _lblStatus.Text = $"Drew {drawn} outline(s) on layer {FitOutlineLayer}.";
            MessageBox.Show(this, $"Drew {drawn} outline(s) of {what} on layer {FitOutlineLayer} (not plotted).\n\nCheck them against the structures, " +
                                  "then erase them (e.g. LAYDEL on that layer) when done. UNDO also removes them.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RunFlow()
        {
            List<ObjectId> targets = GravityPipes();
            if (!NeedGravity(targets)) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            List<ObjectId> toReverse = targets;
            string outcome = "Every chosen pipe was reversed.";
            if (_rbFlowVerify.Checked)
            {
                PipeFlowReport report;
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    report = PipeFlowAnalysis.Analyze(tr, targets);
                if (report.Issues.Count > 0)
                {
                    DialogResult choice;
                    Hide();
                    try { using (FormPipeEndIssues issues = new FormPipeEndIssues(report.Issues)) choice = issues.ShowDialog(); }
                    finally { Show(); }
                    if (choice != DialogResult.OK)
                    {
                        FormPipeEndIssues.WriteToCommandLine(acDoc.Editor, report.Issues);
                        MessageBox.Show(this, "Stopped; nothing was changed. The issues are listed on the command line.", Text);
                        return;
                    }
                }
                if (report.HasMixedDirections)
                {
                    using (FormFlowDirectionChoice direction = new FormFlowDirectionChoice(report.Runs))
                    {
                        if (direction.ShowDialog(this) != DialogResult.OK) return;
                        toReverse = direction.PipesToReverse;
                    }
                    outcome = "The runs now flow the chosen way.";
                }
                else if (MessageBox.Show(this, "The chosen pipes already flow one way along their runs. Reverse all of them?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            if (toReverse.Count == 0) { MessageBox.Show(this, "No pipe needs reversing.", Text); return; }
            NetworkEditOps.Counts c = Change((tr, ed) =>
            {
                NetworkEditOps.Counts r = new NetworkEditOps.Counts();
                foreach (ObjectId id in toReverse)
                {
                    Pipe pipe = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
                    try { PipeFlowAnalysis.ReversePipe(pipe); r.Done++; }
                    catch (System.Exception ex) { ed.WriteMessage($"\n[Error] Reversing pipe <{pipe.Name}>: {ex.Message}"); r.Failed++; }
                }
                return r;
            });
            Report($"Reversed {c.Done} pipe(s). {outcome}", c);
        }

        private void RunMerge()
        {
            List<ObjectId> gravity = GravityPipes();
            if (!NeedGravity(gravity)) return;
            List<NetworkEditOps.MergePlan> plans;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                plans = NetworkEditOps.PlanMerge(tr, gravity);
            if (plans.Count == 0) { MessageBox.Show(this, "No chosen pipes meet end to end in a straight line with the same part and no structure between them.", Text); return; }
            List<PreviewRow> rows = plans.Select(p => new PreviewRow { Tag = p, Cells = new[] { p.Names, p.Pipes.Count.ToString(), ToolUi.Format(Math.Round(p.Length, 2)) } }).ToList();
            if (!Confirm("Merge pipes", $"{plans.Count} chain(s) of pipes can be merged; each becomes its first pipe.", new[] { "Pipes", "Count", "Length (m)" }, rows, "Merge")) return;
            List<NetworkEditOps.MergePlan> chosen = rows.Select(r => (NetworkEditOps.MergePlan)r.Tag).ToList();
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyMerge(tr, chosen, ed));
            c.Skipped = 0;
            Report($"Merged {c.Done} chain(s) into single pipes.", c);
        }

        private void RunSwap()
        {
            RefreshSwap();
            List<SwapGroup> todo = _swapGroups.Where(g => g.Target != null && g.Problem == null).ToList();
            if (_swapGroups.Count == 0) { MessageBox.Show(this, "Choose pipes or parts first (Select pipes, Select network(s) or Select path at the top).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (todo.Count == 0) { MessageBox.Show(this, "Give one or more rows a target first: select them in the list, choose the part, then \"Give the selected rows this target\".", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            int cannot = _swapGroups.Count(g => g.Target != null && g.Problem != null);
            bool switching = todo.Any(g => g.Target.Gravity?.SwitchNetwork == true);
            string lines = string.Join("\n", todo.Take(12).Select(g => $"• {g.Ids.Count} × {g.Current}  →  {g.ToSize}")) + (todo.Count > 12 ? $"\n• … and {todo.Count - 12} more row(s)" : "");
            string text = $"Swap {todo.Sum(g => g.Ids.Count)} part(s) in {todo.Count} row(s)?\n\n{lines}\n\n" +
                          $"Gravity pipes keep their {(_rbSwapCentre.Checked ? "centre elevations" : "invert levels")}." +
                          (switching ? "\nSome networks will be switched to another parts list." : "") +
                          (cannot > 0 ? $"\n{cannot} row(s) with a target that cannot be applied are left as they are." : "");
            if (MessageBox.Show(this, text, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool holdCentre = _rbSwapCentre.Checked;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            NetworkEditOps.Counts c = Change((tr, ed) => PartSwap.Apply(tr, db, todo, holdCentre, ed));
            Report($"Swapped {c.Done} part(s).", c, c.Skipped > 0 ? "Skipped parts are already that part, or their network's list could not take it." : "");
            foreach (SwapGroup g in _swapGroups) g.Target = null;
            UpdatePartContexts(true);
            RefreshSwap();
        }

        private void RunGravity()
        {
            List<ObjectId> pressure = PressurePipes();
            if (pressure.Count == 0) { MessageBox.Show(this, "Choose some pressure pipes first.", Text); return; }
            PartPick pick = _gPart.Pick;
            if (pick == null) { MessageBox.Show(this, "Choose a parts list, a pipe family and a size.", Text); return; }
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyPressureToGravity(tr, CivilApplication.ActiveDocument, pressure, _txtGNet.Text, pick, ed));
            Report($"Created {c.Created} gravity pipe(s) from {pressure.Count} pressure pipe(s).", c);
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. Choose the pipes at the top: select pipes, select networks (any part of each), or select a path by its start and end pipe " +
                "(the connected route between them; several paths in a row).\n" +
                "2. Pick a task on the left and set its options.\n" +
                "3. Press the button at the bottom right. Every task shows what it will do (or asks) before it changes the drawing, " +
                "and one UNDO reverses it.\n\n" +
                "Split pipes: at points you pick, into equal parts or every fixed length; optionally with a structure at each split.\n" +
                "Parallel copies: copies at plan / vertical offsets, e.g. a parallel pipe; presets fill the offsets.\n" +
                "Duct bank conduits: the chosen duct bank (encasement) pipes get a grid of conduits inside them, every position including the " +
                "centre, from a saved layout (columns × rows at centre spacings, centred or measured from the bottom).\n" +
                "Fit to structures: trims or extends ends to each structure's inner / outer wall or centre (from its own size), or to a box of a " +
                "length × width you give, centred on each structure and turned with it. \"Draw the outlines\" shows the faces or boxes first.\n" +
                "Flow direction: reverses pipes, or checks the runs and makes each flow one way.\n" +
                "Merge pipes: joins straight pipes that continue each other into one.\n" +
                "Swap parts: lists the chosen pipes, structures, fittings and appurtenances by their current part, across networks; give each row a " +
                "target (a size, or the nearest inside size in a family) and swap them all at once. Targets can be saved as sets.\n" +
                "Pressure to gravity: gravity copies of pressure pipes in a new network.\n\n" +
                "Levels, cover and profiles are in the Profile & Cover Designer.",
                "Network Editor - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
