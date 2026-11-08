using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Networks.Contracts;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
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
        private const int ASplit = 0, AParallel = 1, AConduits = 2, AFit = 3, AFlow = 4, AMerge = 5, ASwap = 6, AGravity = 7, AChecks = 8, ARims = 9;
        private static readonly string[] RunText = { "Split pipes…", "Create copies…", "Create conduits…", "Fit pipe ends…", "Change flow direction…", "Merge pipes…", "Swap parts…", "Convert to gravity…", "Run the checks…", "Set rims…" };

        private readonly NetworkEditorSettings _s = NetworkEditorSettings.Load();
        /// <summary>The chosen pipes and parts, and the reads made on them; the form shows and edits it.</summary>
        private readonly NetworkEditorSession _session = new NetworkEditorSession();
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
        /// <summary>The split operation: the form collects input and renders results; the workflow owns the drawing changes.</summary>
        private readonly ISplitOperation _split = new SplitWorkflow(new SplitCivil3DAccess());
        private bool _isSplitRunning;
        private readonly Label _lblErrParts = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 3, 0, 2) };
        private readonly Label _lblErrEvery = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 3, 0, 2) };
        private readonly Label _lblErrPickTol = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 3, 0, 2) };
        private readonly Label _lblErrPicks = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 3, 0, 2) };
        private readonly Label _lblErrStructure = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 4, 0, 2) };
        private readonly Label _lblErrGeneral = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 4, 0, 2) };
        private readonly Panel _pnlSplitResult = new Panel { AutoSize = true, Dock = DockStyle.Top, Visible = false, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12, 10, 12, 10), BackColor = StyleUi.Panel };
        private readonly Label _lblSplitResultTitle = new Label { AutoSize = true, Font = StyleUi.Bold, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _lblSplitResultDetails = new Label { AutoSize = true, Font = StyleUi.Base, Margin = new Padding(0), Tag = "note" };
        private Panel _hostSplit;
        private TableLayoutPanel _cardSplit;
        private TableLayoutPanel _cardSplitStructure;
        private string _splitStatusText;
        private Color? _splitStatusColor;
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
        private readonly CheckBox _chkParMitre = ToolUi.Check("Copies of pipes joined end to end (no structure) meet at a mitre: one continuous offset run");
        private readonly CheckBox _chkParConnect = ToolUi.Check("Connect copy ends that lie inside the source's structures (same network)");
        private readonly ComboBox _cbSpacing = ToolUi.Combo(150);
        // Chambers and spans
        private readonly TextBox _txtMaxSkew = ToolUi.Number(5, 50), _txtMaxSpan = ToolUi.Number(150, 60);
        private readonly CheckBox _chkSpanWarn = ToolUi.Check("Also warn about long spans after splitting or fitting pipes");
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
        /// <summary>Structures, fittings and appurtenances chosen as they are (pipes are in <see cref="_session.Pipes"/>).</summary>
        // Rims to surface
        private readonly Dictionary<string, ObjectId> _surfaces = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        private readonly ComboBox _cbRimSurface = ToolUi.Combo(280);
        private readonly TextBox _txtRimAbove = ToolUi.Number(0, 70);
        private readonly CheckBox _chkRimFollow = ToolUi.Check("Keep them on the surface when it changes (Civil 3D's Automatic Surface Adjustment)");
        // Pressure to gravity
        private readonly PartChooser _gPart = new PartChooser(DomainType.Pipe);
        private readonly TextBox _txtGNet = new TextBox { Width = 240 };

        /// <param name="pipes">Pipes to start with (e.g. just made by another tool); null = none.</param>
        public FormNetworkEditor(IEnumerable<ObjectId> pipes = null)
        {
            if (pipes != null) _session.Pipes.AddRange(pipes);
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
            _nav.Add("Chambers and spans", "Pipe entry walls, chamber alignment, long spans");
            _nav.Add("Rims to surface", "Set manhole rims on a surface");
            _pages.Add(BuildSplit());
            _pages.Add(BuildParallel());
            _pages.Add(BuildConduits());
            _pages.Add(BuildFit());
            _pages.Add(BuildFlow());
            _pages.Add(BuildMerge());
            _pages.Add(BuildSwap());
            _pages.Add(BuildGravity());
            _pages.Add(BuildChecks());
            _pages.Add(BuildRims());
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
            Shown += (s, e) => UpdateSplitWrapping();
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
            bar.Controls.Add(StyleUi.Secondary("Clear", (s, e) => { _session.Pipes.Clear(); _session.Parts.Clear(); ClearSplitValidation(); UpdatePipeSummary(); }));
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
            _hostSplit = host;
            TableLayoutPanel card = ToolUi.Card("Where to split",
                "Straight pipes keep their first piece; the others are copies with the same family, size, style and layer on the same 3D line, " +
                "and the original end connection moves to the last piece. Curved pipes are broken by Civil 3D's own break.");
            _cardSplit = card;
            _cbSplitMode.Items.AddRange(new object[] { "At points I pick on the pipes", "Into equal parts", "Every fixed length (from the start)" });
            ToolUi.Field(card, "Split", _cbSplitMode);

            FlowLayoutPanel colParts = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            colParts.Controls.Add(_numParts);
            colParts.Controls.Add(_lblErrParts);
            ToolUi.Field(card, "Parts", colParts);

            FlowLayoutPanel colEvery = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            colEvery.Controls.Add(ToolUi.WithUnit(_txtEvery, "m"));
            colEvery.Controls.Add(_lblErrEvery);
            ToolUi.Field(card, "Every", colEvery);

            FlowLayoutPanel colPick = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            colPick.Controls.Add(ToolUi.WithUnit(_txtPickTol, "m of a pipe"));
            colPick.Controls.Add(_lblErrPickTol);
            colPick.Controls.Add(_lblErrPicks);
            ToolUi.Field(card, "Pick within", colPick, _tips, "A picked point splits the nearest pipe when it lies within this distance of it.");

            ToolUi.Wide(card, _lblErrGeneral);
            ToolUi.Add(stack, card);

            TableLayoutPanel st = ToolUi.Card("Structures at the split points", "Leave this off for cable ducts that simply continue; turn it on to place manholes or pits there.");
            _cardSplitStructure = st;
            ToolUi.Wide(st, _chkSplitStructure);
            ToolUi.Wide(st, _splitPart);
            ToolUi.Wide(st, _lblErrStructure);
            ToolUi.Add(stack, st);

            _pnlSplitResult.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawRectangle(p, 0, 0, _pnlSplitResult.Width - 1, _pnlSplitResult.Height - 1); };
            _lblSplitResultTitle.Dock = DockStyle.Top;
            _lblSplitResultDetails.Dock = DockStyle.Top;
            _pnlSplitResult.Controls.Add(_lblSplitResultDetails);
            _pnlSplitResult.Controls.Add(_lblSplitResultTitle);
            ToolUi.Add(stack, _pnlSplitResult);

            void UpdateWrapping() => UpdateSplitWrapping();
            host.Resize += (s, e) => UpdateWrapping();
            card.Resize += (s, e) => UpdateWrapping();
            st.Resize += (s, e) => UpdateWrapping();
            _pnlSplitResult.Resize += (s, e) => UpdateWrapping();
            UpdateSplitWrapping();

            _cbSplitMode.SelectedIndexChanged += (s, e) =>
            {
                ClearSplitValidation();
                _numParts.Enabled = _cbSplitMode.SelectedIndex == 1;
                _txtEvery.Enabled = _cbSplitMode.SelectedIndex == 2;
                _txtPickTol.Enabled = _cbSplitMode.SelectedIndex == 0;
                UpdatePipeSummary();
            };
            _numParts.ValueChanged += (s, e) => { _lblErrParts.Visible = false; };
            _txtEvery.TextChanged += (s, e) => { _lblErrEvery.Visible = false; };
            _txtPickTol.TextChanged += (s, e) => { _lblErrPickTol.Visible = false; _lblErrPicks.Visible = false; };
            _chkSplitStructure.CheckedChanged += (s, e) =>
            {
                _splitPart.Enabled = _chkSplitStructure.Checked;
                _lblErrStructure.Visible = false;
            };
            _splitPart.Changed += (s, e) => { _lblErrStructure.Visible = false; };
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
                new Label { Text = "rows, spacing", AutoSize = true, Margin = new Padding(2, 7, 6, 0) }, _txtSpX, new Label { Text = "×", AutoSize = true, Margin = new Padding(2, 7, 2, 0) }, _txtSpZ, _cbSpacing,
                new Label { Text = "m, centred", AutoSize = true, Margin = new Padding(2, 7, 8, 0) }, fill));
            ToolUi.Add(stack, card);

            TableLayoutPanel part = ToolUi.Card("Copies");
            ToolUi.Wide(part, _rbOwnSize);
            ToolUi.Wide(part, _rbChosenSize);
            ToolUi.Wide(part, _parPart);
            ToolUi.Wide(part, ToolUi.Inline(_chkParFit, new Label { Text = "at their", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, _cbParFace));
            ToolUi.Wide(part, _chkParMitre);
            ToolUi.Wide(part, _chkParConnect);
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
            _cbSpacing.Items.AddRange(new object[] { "m centre to centre", "m clear between outsides" });
            _tips.SetToolTip(_cbSpacing, "Clear gap: the pitch is the gap plus the outside size of the first chosen pipe (or of the chosen part).");
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
                "The box is centred on each structure and turned with it, as the part's own sizes lie: its width along the structure's own X axis (the direction its rotation gives), its length across it.");
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

        private Control BuildRims()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Set structure rims on a surface",
                "Takes the structures at the ends of the chosen gravity pipes, and any structures selected directly. Every rim moves to the surface level at the " +
                "structure's centre. Sumps and pipes stay exactly where they are. You review every structure before anything changes.");
            ToolUi.Field(card, "Surface", _cbRimSurface);
            ToolUi.Field(card, "Rim above surface", ToolUi.WithUnit(_txtRimAbove, "m"), _tips, "0 puts the rim on the surface; a negative value puts it below.");
            ToolUi.Wide(card, _chkRimFollow);
            ToolUi.Wide(card, ToolUi.Note("Left off, the rims are set once and stay there, and any of them on Automatic Surface Adjustment is taken off it. " +
                                          "Turned on, their sumps are also changed to elevation control, so the sumps do not move with the surface."));
            ToolUi.Add(stack, card);
            return host;
        }

        private void RunRims()
        {
            if (!(_cbRimSurface.SelectedItem is string surfaceName) || !_surfaces.TryGetValue(surfaceName, out ObjectId surfaceId))
            {
                MessageBox.Show(this, _surfaces.Count == 0 ? "This drawing has no surfaces." : "Choose the surface.", Text);
                return;
            }
            if (!ToolUi.TryNumber(_txtRimAbove, out double above)) { MessageBox.Show(this, "Enter the rim's height above the surface as a number (0 for on the surface).", Text); return; }
            bool follow = _chkRimFollow.Checked;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            List<RimToSurface.Row> plan;
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                Autodesk.Civil.DatabaseServices.Surface surface = (Autodesk.Civil.DatabaseServices.Surface)tr.GetObject(surfaceId, OpenMode.ForRead);
                plan = RimToSurface.Plan(tr, GravityPipes(), _session.Parts.Where(id => !id.IsErased), surface, above);
                tr.Commit();
            }
            if (plan.Count == 0) { MessageBox.Show(this, "Choose some gravity pipes with structures at their ends, or select structures (at the top).", Text); return; }

            string Level(double v) => double.IsNaN(v) ? "-" : v.ToString("0.000");
            List<PreviewRow> rows = plan.Select(r =>
            {
                bool usable = !r.Reference && r.Check != RimCheck.OffSurface && r.Check != RimCheck.BelowSump && (r.Check != RimCheck.AlreadyMatches || follow);
                string note = r.Reference ? "Data shortcut: change it in its source drawing"
                    : r.Check == RimCheck.OffSurface ? "Outside the surface"
                    : r.Check == RimCheck.BelowSump ? $"Would be at or under its sump ({Level(r.Sump)})"
                    : r.Check == RimCheck.BelowPipe ? $"A pipe top ({Level(r.PipeTop)}) would stand above the rim; tick to set it anyway"
                    : r.Check == RimCheck.AlreadyMatches ? (follow ? "On the surface; will be linked to it" : "Already on the surface")
                    : "";
                return new PreviewRow
                {
                    Tag = r,
                    Enabled = usable,
                    Checked = usable && r.Check != RimCheck.BelowPipe,
                    Cells = new[] { r.Name, r.Network, r.Part, Level(r.Rim), Level(r.Ground), Level(r.NewRim),
                        double.IsNaN(r.NewRim) ? "-" : (r.NewRim - r.Rim).ToString("+0.000;-0.000;0.000"), Level(r.Sump), note }
                };
            }).ToList();
            if (!rows.Any(r => r.Enabled))
            {
                MessageBox.Show(this, $"None of the {plan.Count} structure(s) needs or can take a change: " +
                    $"{plan.Count(r => r.Check == RimCheck.AlreadyMatches)} already on the surface, {plan.Count(r => r.Check == RimCheck.OffSurface)} outside it, " +
                    $"{plan.Count(r => r.Check == RimCheck.BelowSump)} would be under the sump, {plan.Count(r => r.Reference)} from data shortcuts.", Text);
                return;
            }
            string intro = $"Rims on \"{surfaceName}\"" + (Math.Abs(above) > 1e-9 ? $", {ToolUi.Format(above)} m above it" : "") + (follow ? ", staying linked to it" : ", set once") +
                           ". Sumps and pipes stay where they are; greyed rows are left alone.";
            if (!Confirm("Rims to surface", intro, new[] { "Structure", "Network", "Part", "Rim now", "Surface", "New rim", "Change (m)", "Sump", "Note" }, rows, "Set rims")) return;

            List<RimToSurface.Row> chosen = rows.Select(r => (RimToSurface.Row)r.Tag).ToList();
            RimToSurface.Outcome outcome = null;
            NetworkEditOps.Counts c = Change((tr, ed) => (outcome = RimToSurface.Apply(tr, chosen, surfaceId, above, follow, ed)).Counts);
            List<string> notes = new List<string>();
            if (outcome.SumpControlChanged > 0) notes.Add($"{outcome.SumpControlChanged} sump(s) changed from depth to elevation control, so they stay at their level.");
            if (outcome.Unlinked > 0) notes.Add($"{outcome.Unlinked} structure(s) taken off Automatic Surface Adjustment.");
            if (outcome.NotAtTarget > 0) notes.Add($"{outcome.NotAtTarget} rim(s) did not end up at the level asked; see the command line.");
            Report($"Set the rim of {c.Done} structure(s) on {surfaceName}.", c, string.Join("\n", notes));
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
                _swapGroups = PartSwap.Collect(tr, _session.Pipes, _session.Parts, _chkSwapStructures.Checked, _chkSwapFittings.Checked);
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
                foreach (ObjectId id in doc.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface sf) _surfaces[sf.Name] = id;
            }
            foreach (string n in _surfaces.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbRimSurface.Items.Add(n);
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
                foreach (ObjectId id in _session.Pipes)
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
            _chkParMitre.Checked = _s.ParallelMitre;
            _chkParConnect.Checked = _s.ParallelConnect;
            _cbSpacing.SelectedIndex = _s.ParallelClearGap ? 1 : 0;
            _txtMaxSkew.Text = ToolUi.Format(_s.CheckMaxSkew);
            _txtMaxSpan.Text = ToolUi.Format(_s.CheckMaxSpan);
            _chkSpanWarn.Checked = _s.CheckSpanWarn;

            ApplyConduitSettings();
            _cbFitFace.SelectedIndex = Math.Max(0, Math.Min(3, _s.FitFace));
            _chkFitConnect.Checked = _s.FitConnect;
            _txtFitBoxL.Text = ToolUi.Format(_s.FitBoxLength);
            _txtFitBoxW.Text = ToolUi.Format(_s.FitBoxWidth);
            UpdateBoxFields();
            _rbFlowVerify.Checked = _s.FlowVerify;
            _rbFlowAll.Checked = !_s.FlowVerify;
            int rimSurface = _s.RimSurface == null ? -1 : _cbRimSurface.Items.Cast<string>().ToList().FindIndex(n => n.Equals(_s.RimSurface, StringComparison.OrdinalIgnoreCase));
            if (_cbRimSurface.Items.Count > 0) _cbRimSurface.SelectedIndex = Math.Max(0, rimSurface);
            _txtRimAbove.Text = ToolUi.Format(_s.RimAboveSurface);
            _chkRimFollow.Checked = _s.RimFollow;
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
            _s.ParallelMitre = _chkParMitre.Checked;
            _s.ParallelConnect = _chkParConnect.Checked;
            _s.ParallelClearGap = _cbSpacing.SelectedIndex == 1;
            if (ToolUi.TryNumber(_txtMaxSkew, out double skew) && skew >= 0 && skew < 90) _s.CheckMaxSkew = skew;
            if (ToolUi.TryNumber(_txtMaxSpan, out double span) && span >= 0) _s.CheckMaxSpan = span;
            _s.CheckSpanWarn = _chkSpanWarn.Checked;
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
            if (_cbRimSurface.SelectedItem is string rimSurface) _s.RimSurface = rimSurface;
            if (ToolUi.TryNumber(_txtRimAbove, out double rimAbove)) _s.RimAboveSurface = rimAbove;
            _s.RimFollow = _chkRimFollow.Checked;
            _s.GravityNetworkName = _txtGNet.Text;
            _s.GravityPartsList = _gPart.ListName;
            _s.GravityFamily = _gPart.FamilyName;
            _s.GravitySize = _gPart.SizeName;
            _s.Save();
        }

        private void FillGrid()
        {
            if (!ToolUi.TryNumber(_txtSpX, out double sx) || !ToolUi.TryNumber(_txtSpZ, out double sz)) { MessageBox.Show(this, "Enter the spacing as numbers.", Text); return; }
            bool clearGap = _cbSpacing.SelectedIndex == 1;
            double width = 0, height = 0;
            if (clearGap && !CopyOutside(out width, out height))
            {
                MessageBox.Show(this, "Choose a pipe (or the part for the copies) first: a clear gap is measured between the copies' outsides.", Text);
                return;
            }
            (List<double> h, List<double> v) = PlanGeometry.ArrayOffsets((int)_numCols.Value, (int)_numRows.Value,
                PlanGeometry.Pitch(sx, width, clearGap), PlanGeometry.Pitch(sz, height, clearGap));
            _txtH.Text = string.Join(", ", h.Select(ToolUi.Format));
            _txtV.Text = string.Join(", ", v.Select(ToolUi.Format));
        }

        /// <summary>The copies' outside width and height: the chosen part's, else the first chosen gravity pipe's.</summary>
        private bool CopyOutside(out double width, out double height) =>
            _session.CopyOutside(_rbChosenSize.Checked ? _parPart.Pick : null, out width, out height);

        // =====================================================================================
        // Chambers and spans
        // =====================================================================================

        private Control BuildChecks()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Rectangular chambers",
                "Every rectangular structure the chosen pipes meet is checked: a pipe should enter through one of the narrow walls (the two shorter faces), " +
                "square to it. A chamber with one pipe, or two running straight through, can be turned so its narrow walls sit square to the pipe.");
            ToolUi.Field(card, "Off square allowed", ToolUi.WithUnit(_txtMaxSkew, "°"));
            ToolUi.Wide(card, ToolUi.Note("Turning a chamber moves its walls: fit the pipe ends again afterwards (Fit to structures)."));
            ToolUi.Add(stack, card);

            TableLayoutPanel spans = ToolUi.Card("Spans", "Structure-to-structure lengths (centre to centre; pipes joined with no structure between them count as one span) " +
                "in the chosen pipes' networks, for the pipes chosen and the spans next to their structures.");
            ToolUi.Field(spans, "Longest span", ToolUi.WithUnit(_txtMaxSpan, "m (0 = no check)"));
            ToolUi.Wide(spans, _chkSpanWarn);
            ToolUi.Add(stack, spans);
            return host;
        }

        private bool CheckLimits(out double skew, out double span)
        {
            span = 0;
            if (!ToolUi.TryNumber(_txtMaxSkew, out skew) || skew < 0 || skew >= 90 || !ToolUi.TryNumber(_txtMaxSpan, out span) || span < 0)
            {
                MessageBox.Show(this, "Enter the angle off square (0 to 90°) and the longest span (0 or more).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        /// <summary>A warning about long spans around these pipes, when asked for on the Chambers and spans page ("" otherwise).</summary>
        private string SpanWarning(IEnumerable<ObjectId> pipes)
        {
            if (!_chkSpanWarn.Checked || !ToolUi.TryNumber(_txtMaxSpan, out double span) || span <= 0) return "";
            return NetworkEditorSession.SpanWarning(pipes, span);
        }

        private void RunChecks()
        {
            if (_session.Pipes.Count == 0) { MessageBox.Show(this, "Choose some pipes first.", Text); return; }
            if (!CheckLimits(out double skew, out double span)) return;
            (List<ChamberEntryIssue> issues, string spans) = _session.Audit(skew, span);
            string spanText = spans.Length > 0 ? spans : span > 0 ? $"Every span is within {span:0.00} m." : "";
            if (issues.Count == 0)
            {
                _lblStatus.Text = "No chamber entry problems.";
                MessageBox.Show(this, ("Every pipe enters its rectangular chamber through a narrow wall, square to it.\n\n" + spanText).Trim(), Text,
                    MessageBoxButtons.OK, spans.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return;
            }
            List<PreviewRow> rows = issues.Select(i => new PreviewRow
            {
                Tag = i, Enabled = i.AlignedRotation.HasValue, Checked = i.AlignedRotation.HasValue,
                Cells = new[] { i.Structure, i.Pipe, i.Problem,
                    i.AlignedRotation.HasValue ? $"Turn {i.Rotation * 180 / Math.PI:0.#}° → {i.AlignedRotation.Value * 180 / Math.PI:0.#}°" : "— (not one pipe or straight through)" }
            }).ToList();
            string intro = $"{issues.Count} pipe(s) enter a rectangular chamber through a long wall or more than {skew:0.#}° off square. " +
                           "Ticked chambers are turned so their narrow walls sit square to the pipe." + (spanText.Length > 0 ? "\n\n" + spanText : "");
            if (!Confirm("Chambers and spans", intro, new[] { "Structure", "Pipe", "Problem", "Fix" }, rows, "Turn the ticked chambers")) return;
            List<ChamberEntryIssue> chosen = rows.Select(r => (ChamberEntryIssue)r.Tag).ToList();
            NetworkEditOps.Counts c = Change((tr, ed) => new NetworkEditOps.Counts { Done = NetworkChecks.AlignChambers(tr, chosen) });
            Report($"Turned {c.Done} chamber(s) square to their pipes.", c, "Fit the pipe ends to the turned chambers' walls with Fit to structures.");
        }

        // =====================================================================================
        // Choosing pipes
        // =====================================================================================

        private void UpdatePipeSummary()
        {
            _session.Prune();
            UpdatePartContexts();
            (int g, int p) = _session.CountPipes();
            _lblPipes.Text = (_session.Pipes.Count == 0 ? "No pipes chosen yet." : $"{g} gravity and {p} pressure pipe(s) chosen.") +
                             (_session.Parts.Count > 0 ? $" Also {_session.Parts.Count} structure(s), fitting(s) or appurtenance(s) (used by Swap parts and Rims to surface)." : "");
            if (_nav.Selected == ASwap) RefreshSwap();
            RefreshConduitSection();
            int a = _nav.Selected;
            if (a == ASplit && _splitStatusText != null)
            {
                _lblStatus.Text = _splitStatusText;
                _lblStatus.ForeColor = _splitStatusColor ?? StyleUi.Muted;
                return;
            }

            _lblStatus.ForeColor = StyleUi.Muted;
            string usable = a == AGravity ? $"{p} pressure pipe(s) will be converted."
                : a == AParallel ? $"{g + p} pipe(s) will be copied."
                : a == AConduits ? ConduitStatus(g)
                : a == ASwap ? "Rows with a target are swapped; the others are left as they are."
                : a == ARims ? "The structures at the chosen gravity pipes' ends, and those selected directly, are listed before anything changes."
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
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect pipes (gravity or pressure); structures, fittings and appurtenances are taken too (Swap parts, Rims to surface): " });
                if (psr.Status != PromptStatus.OK) return;
                _session.Pipes.Clear();
                _session.Parts.Clear();
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is Pipe || obj is PressurePipe) _session.Pipes.Add(id);
                        else if (obj is Structure || obj is PressureFitting || obj is PressureAppurtenance) _session.Parts.Add(id);
                    }
            }
            finally { Show(); ClearSplitValidation(); UpdatePipeSummary(); }
        }

        private void SelectNetworks()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect any pipe, structure or fitting of each network: " });
                if (psr.Status != PromptStatus.OK) return;
                _session.Pipes.Clear();
                _session.Parts.Clear();
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
                acDoc.Editor.WriteMessage($"\n{count} network(s), {_session.Pipes.Count} pipe(s) chosen.");
            }
            finally { Show(); ClearSplitValidation(); UpdatePipeSummary(); }
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
                _session.Pipes.Clear();
                _session.Parts.Clear();
                _session.Pipes.AddRange(chosen);
            }
            finally { Show(); ClearSplitValidation(); UpdatePipeSummary(); }
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
            foreach (ObjectId id in pipes) if (!_session.Pipes.Contains(id)) _session.Pipes.Add(id);
            List<ObjectId> others = new List<ObjectId>();
            if (net is Network gn) others.AddRange(gn.GetStructureIds().Cast<ObjectId>());
            else if (net is PressurePipeNetwork pnet) { others.AddRange(pnet.GetFittingIds().Cast<ObjectId>()); others.AddRange(pnet.GetAppurtenanceIds().Cast<ObjectId>()); }
            HashSet<ObjectId> have = new HashSet<ObjectId>(_session.Parts);
            foreach (ObjectId id in others) if (have.Add(id)) _session.Parts.Add(id);
        }

        private void ShowPipes()
        {
            if (_session.Pipes.Count == 0) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                using (TransientMarkers markers = new TransientMarkers())
                {
                    Extents3d? ext = null;
                    using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                        foreach (ObjectId id in _session.Pipes)
                        {
                            List<Point3d> path = PathOf(tr.GetObject(id, OpenMode.ForRead));
                            if (path == null) continue;
                            markers.AddPath(path, 4, 0.3);
                            foreach (Point3d p in path) { Extents3d e = ext ?? new Extents3d(p, p); e.AddPoint(p); ext = e; }
                        }
                    if (ext.HasValue) ViewHelper.ZoomTo(ext.Value, 1.2, 20.0);
                    acDoc.Editor.UpdateScreen();
                    acDoc.Editor.GetString(new PromptStringOptions($"\n{_session.Pipes.Count} chosen pipe(s) highlighted. Press Enter to return: ") { AllowSpaces = true });
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

        private List<ObjectId> GravityPipes() => _session.GravityPipes();

        private List<ObjectId> PressurePipes() => _session.PressurePipes();

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
                    case AChecks: RunChecks(); break;
                    case ARims: RunRims(); break;
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
            ViewHelper.Regen(acDoc);
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

        private void ClearSplitValidation()
        {
            _lblErrParts.Text = ""; _lblErrParts.Visible = false;
            _lblErrEvery.Text = ""; _lblErrEvery.Visible = false;
            _lblErrPickTol.Text = ""; _lblErrPickTol.Visible = false;
            _lblErrPicks.Text = ""; _lblErrPicks.Visible = false;
            _lblErrStructure.Text = ""; _lblErrStructure.Visible = false;
            _lblErrGeneral.Text = ""; _lblErrGeneral.Visible = false;
            _pnlSplitResult.Visible = false;
            _splitStatusText = null;
            _splitStatusColor = null;
        }

        private void SetSplitStatus(string text, Color color)
        {
            _splitStatusText = text;
            _splitStatusColor = color;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void UpdateSplitWrapping()
        {
            if (_hostSplit == null) return;
            int hostWidth = _hostSplit.ClientSize.Width - _hostSplit.Padding.Horizontal;
            if (hostWidth <= 0) return;

            int cardWidth = _cardSplit != null && _cardSplit.ClientSize.Width > 0
                ? _cardSplit.ClientSize.Width - _cardSplit.Padding.Horizontal
                : hostWidth - 24;
            cardWidth = Math.Max(120, cardWidth);

            int stWidth = _cardSplitStructure != null && _cardSplitStructure.ClientSize.Width > 0
                ? _cardSplitStructure.ClientSize.Width - _cardSplitStructure.Padding.Horizontal
                : hostWidth - 24;
            stWidth = Math.Max(120, stWidth);

            int pnlWidth = _pnlSplitResult != null && _pnlSplitResult.ClientSize.Width > 0
                ? _pnlSplitResult.ClientSize.Width - _pnlSplitResult.Padding.Horizontal - 4
                : hostWidth - _pnlSplitResult.Padding.Horizontal - 4;
            pnlWidth = Math.Max(120, pnlWidth);

            _lblErrGeneral.MaximumSize = new Size(cardWidth, 0);
            _lblErrStructure.MaximumSize = new Size(stWidth, 0);
            _lblSplitResultTitle.MaximumSize = new Size(pnlWidth, 0);
            _lblSplitResultDetails.MaximumSize = new Size(pnlWidth, 0);

            int[] widths = _cardSplit?.GetColumnWidths();
            int fieldWidth = widths != null && widths.Length >= 2 && widths[1] > 50
                ? widths[1] - 12
                : Math.Max(120, cardWidth - 120);

            _lblErrParts.MaximumSize = new Size(fieldWidth, 0);
            _lblErrEvery.MaximumSize = new Size(fieldWidth, 0);
            _lblErrPickTol.MaximumSize = new Size(fieldWidth, 0);
            _lblErrPicks.MaximumSize = new Size(fieldWidth, 0);
        }

        private void ShowSplitFieldIssues(IEnumerable<OperationIssue> issues)
        {
            if (issues == null) return;
            UpdateSplitWrapping();
            foreach (OperationIssue issue in issues)
            {
                if (string.Equals(issue.Field, nameof(SplitRequest.Parts), StringComparison.OrdinalIgnoreCase))
                {
                    _lblErrParts.Text = issue.Message;
                    _lblErrParts.Visible = true;
                }
                else if (string.Equals(issue.Field, nameof(SplitRequest.Every), StringComparison.OrdinalIgnoreCase))
                {
                    _lblErrEvery.Text = issue.Message;
                    _lblErrEvery.Visible = true;
                }
                else if (string.Equals(issue.Field, nameof(SplitRequest.PickTolerance), StringComparison.OrdinalIgnoreCase))
                {
                    _lblErrPickTol.Text = issue.Message;
                    _lblErrPickTol.Visible = true;
                }
                else if (string.Equals(issue.Field, nameof(SplitRequest.Picks), StringComparison.OrdinalIgnoreCase))
                {
                    _lblErrPicks.Text = issue.Message;
                    _lblErrPicks.Visible = true;
                }
                else if (string.Equals(issue.Field, nameof(SplitRequest.Structure), StringComparison.OrdinalIgnoreCase))
                {
                    _lblErrStructure.Text = issue.Message;
                    _lblErrStructure.Visible = true;
                }
                else if (issue.Object == null && (string.Equals(issue.Field, nameof(SplitRequest.Pipes), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(issue.Field, nameof(SplitRequest.Drawing), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(issue.Field, nameof(SplitRequest.Mode), StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(issue.Field)))
                {
                    _lblErrGeneral.Text = issue.Message;
                    _lblErrGeneral.Visible = true;
                }
            }
        }

        private void RunSplit()
        {
            if (_isSplitRunning) return;
            _isSplitRunning = true;
            if (_btnRun != null) _btnRun.Enabled = false;

            try
            {
                ClearSplitValidation();
                Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
                if (acDoc == null)
                {
                    _lblErrGeneral.Text = "No active document in Civil 3D.";
                    _lblErrGeneral.Visible = true;
                    UpdateSplitWrapping();
                    SetSplitStatus("[Rejected] No active drawing.", StyleUi.Danger);
                    MessageBox.Show(this, "Open a drawing in Civil 3D first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                List<ObjectId> gravity = GravityPipes();
                if (_chkSplitStructure.Checked && _splitPart.Pick == null)
                {
                    const string msg = "Choose the structure for the split points (parts list, family, size).";
                    _lblErrStructure.Text = msg;
                    _lblErrStructure.Visible = true;
                    UpdateSplitWrapping();
                    SetSplitStatus("[Validation] " + msg, StyleUi.Danger);
                    MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                SplitMode mode = (SplitMode)_cbSplitMode.SelectedIndex;
                SplitRequest request = new SplitRequest
                {
                    Drawing = DrawingRefs3D.Identity(acDoc),
                    Mode = mode,
                    Pipes = gravity.Select(DrawingRefs3D.Ref).ToList(),
                    Parts = (int)_numParts.Value,
                    Structure = _chkSplitStructure.Checked ? SplitCivil3DAccess.ToChoice(_splitPart.Pick) : null
                };

                // Quick feedback here; the workflow validates again and is authoritative.
                if (mode == SplitMode.AtPoints)
                {
                    if (!ToolUi.TryNumber(_txtPickTol, out double tol) || tol <= 0 || tol > SplitLimits.MaxPickTolerance)
                    {
                        string msg = $"Enter a pick distance above 0 and at most {SplitLimits.MaxPickTolerance:0} m.";
                        _lblErrPickTol.Text = msg;
                        _lblErrPickTol.Visible = true;
                        UpdateSplitWrapping();
                        SetSplitStatus("[Validation] " + msg, StyleUi.Danger);
                        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    List<Point3d> picks = PickSplitPoints(acDoc);
                    if (picks.Count == 0)
                    {
                        _lblErrPicks.Text = "Pick at least one point on a pipe.";
                        _lblErrPicks.Visible = true;
                        UpdateSplitWrapping();
                        SetSplitStatus("[Cancelled] No split points were picked.", StyleUi.Muted);
                        return;
                    }
                    request = request with { PickTolerance = tol, Picks = picks.Select(DrawingRefs3D.Point).ToList() };
                }
                else
                {
                    if (!NeedGravity(gravity))
                    {
                        _lblErrGeneral.Text = "Choose some gravity pipes first.";
                        _lblErrGeneral.Visible = true;
                        UpdateSplitWrapping();
                        return;
                    }
                    if (mode == SplitMode.EqualParts)
                    {
                        if (_numParts.Value < SplitLimits.MinParts || _numParts.Value > SplitLimits.MaxParts)
                        {
                            string msg = $"Enter {SplitLimits.MinParts} to {SplitLimits.MaxParts} parts.";
                            _lblErrParts.Text = msg;
                            _lblErrParts.Visible = true;
                            UpdateSplitWrapping();
                            SetSplitStatus("[Validation] " + msg, StyleUi.Danger);
                            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    else if (mode == SplitMode.FixedLength)
                    {
                        if (!ToolUi.TryNumber(_txtEvery, out double every) || every < SplitLimits.MinPieceLength)
                        {
                            string msg = $"Enter a length of at least {SplitLimits.MinPieceLength} m.";
                            _lblErrEvery.Text = msg;
                            _lblErrEvery.Visible = true;
                            UpdateSplitWrapping();
                            SetSplitStatus("[Validation] " + msg, StyleUi.Danger);
                            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        request = request with { Every = every };
                    }
                }

                SplitPreview preview = _split.Preview(request, CancellationToken.None);
                ShowSplitFieldIssues(preview.Issues);

                if (preview.Status != OperationStatus.Succeeded)
                {
                    string statusTag = preview.Status switch
                    {
                        OperationStatus.Rejected => "[Rejected]",
                        OperationStatus.NoChange => "[No change]",
                        OperationStatus.Cancelled => "[Cancelled]",
                        _ => $"[{preview.Status}]"
                    };
                    string issueMsg = IssueText(preview.Issues);
                    SetSplitStatus($"{statusTag} {issueMsg}", preview.Status == OperationStatus.Rejected ? StyleUi.Danger : StyleUi.Muted);
                    MessageBox.Show(this, issueMsg, $"{Text} - {statusTag.Trim('[', ']')}", MessageBoxButtons.OK,
                        preview.Status == OperationStatus.Rejected ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                    return;
                }

                string unit = preview.Drawing?.Unit ?? "m";
                List<PreviewRow> rows = preview.Items.Select(p => new PreviewRow
                {
                    Tag = p.Pipe,
                    Checked = true,
                    Enabled = true,
                    Cells = new[]
                    {
                        p.PipeName,
                        p.Curved ? "Curved" : "Straight",
                        ToolUi.Format(Math.Round(p.Length, 2)),
                        p.PieceCount.ToString(),
                        string.Join("; ", p.Points.Take(4).Select(pt => $"{pt.X:0.00}, {pt.Y:0.00}")) + (p.Points.Count > 4 ? " …" : "")
                    }
                }).ToList();

                HashSet<DrawingObjectRef> plannedPipes = new HashSet<DrawingObjectRef>(preview.Items.Select(p => p.Pipe));
                List<OperationIssue> leftOutIssues = preview.Issues
                    .Where(i => i.Object != null && !plannedPipes.Contains(i.Object.Value))
                    .GroupBy(i => i.Object.Value)
                    .Select(g => g.First())
                    .ToList();

                foreach (OperationIssue issue in leftOutIssues)
                {
                    DrawingObjectRef pipeRef = issue.Object.Value;
                    string pipeName = $"Pipe ({pipeRef.Handle})";
                    string reason = issue.Message;
                    rows.Add(new PreviewRow
                    {
                        Tag = pipeRef,
                        Checked = false,
                        Enabled = false,
                        Cells = new[]
                        {
                            pipeName,
                            "Left out",
                            "—",
                            "None",
                            reason
                        }
                    });
                }

                int leftOutCount = leftOutIssues.Count;
                string intro = $"{preview.Items.Count} pipe(s) will be split" +
                    (preview.MissedPicks > 0 ? $"; {preview.MissedPicks} picked point(s) were not on a pipe and are ignored" : "") +
                    (leftOutCount > 0 ? $"; {leftOutCount} pipe(s) are left out (see greyed rows below)" : "") + "." +
                    (request.Structure != null ? $" A {request.Structure.Label} joins the pieces at every split point." : " The pieces are left touching, without structures.");

                if (!Confirm("Split pipes", intro, new[] { "Pipe", "Type", $"Length ({unit})", "Pieces", "Split points" }, rows, "Split")) return;

                List<DrawingObjectRef> chosen = rows.Select(r => (DrawingObjectRef)r.Tag).ToList();
                SplitResult result = _split.Apply(new SplitApplyRequest(preview, chosen), null, CancellationToken.None);
                ViewHelper.Regen(acDoc);
                IEnumerable<DrawingObjectRef> touched = result.Items.Where(i => i.Status == ItemStatus.Committed).SelectMany(i => new[] { i.Pipe }.Concat(i.NewPipes));
                string spanWarning = SpanWarning(DrawingRefs3D.ObjectIds(acDoc.Database, touched));
                ReportSplit(result, preview, spanWarning);
            }
            finally
            {
                _isSplitRunning = false;
                if (_btnRun != null) _btnRun.Enabled = true;
            }
        }

        /// <summary>Points picked on the pipes (the form hides while picking); empty when cancelled or none picked.</summary>
        private List<Point3d> PickSplitPoints(Document acDoc)
        {
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
            return picks;
        }

        /// <summary>Shows what the split did: committed counts only, then each pipe not split and why, then notes.</summary>
        private void ReportSplit(SplitResult r, SplitPreview preview, string spanWarning)
        {
            (string statusTitle, string what, Color statusColor, MessageBoxIcon icon) = r.Status switch
            {
                OperationStatus.Succeeded => (
                    "Success",
                    $"Successfully split {r.PipesSplit} pipe(s) into {r.PiecesTotal} pieces.",
                    Color.FromArgb(21, 128, 61),
                    MessageBoxIcon.Information),
                OperationStatus.PartiallySucceeded => (
                    "Partial Success",
                    $"Partially succeeded: split {r.PipesSplit} pipe(s) into {r.PiecesTotal} pieces; {r.Items.Count - r.PipesSplit} pipe(s) were not split.",
                    Color.FromArgb(180, 83, 9),
                    MessageBoxIcon.Warning),
                OperationStatus.RolledBack => (
                    "Rolled Back",
                    "Rolled back: all split operations failed and were undone. The drawing was restored to its original state.",
                    Color.FromArgb(185, 28, 28),
                    MessageBoxIcon.Warning),
                OperationStatus.Rejected => (
                    "Rejected",
                    "Rejected: nothing was split. The request was invalid or the drawing did not match.",
                    Color.FromArgb(190, 18, 60),
                    MessageBoxIcon.Warning),
                OperationStatus.NoChange => (
                    "No Change",
                    "No change: nothing to split.",
                    StyleUi.Muted,
                    MessageBoxIcon.Information),
                OperationStatus.Cancelled => (
                    "Cancelled",
                    $"Cancelled: operation stopped between pipes. {r.PipesSplit} pipe(s) were committed before cancellation.",
                    StyleUi.Muted,
                    MessageBoxIcon.Information),
                OperationStatus.OutcomeUnknown or _ => (
                    "Outcome Unknown",
                    "Outcome unknown: work started but the drawing's final state could not be verified. Inspect the drawing before retrying; do not retry automatically.",
                    StyleUi.Danger,
                    MessageBoxIcon.Error)
            };

            List<string> sections = new List<string>();

            // Committed counts
            if (r.StructuresAdded > 0)
                sections.Add($"Structures added: {r.StructuresAdded}");

            // Item-level statuses and issues
            List<string> itemNotes = new List<string>();
            foreach (SplitItemOutcome item in r.Items)
            {
                if (item.Status == ItemStatus.Committed)
                {
                    if (item.Issue != null)
                        itemNotes.Add($"• {item.PipeName}: committed ({item.Issue.Message})");
                }
                else
                {
                    string statusLabel = item.Status switch
                    {
                        ItemStatus.RolledBack => "[Rolled back]",
                        ItemStatus.Rejected => "[Rejected]",
                        ItemStatus.NotAttempted => "[Not attempted]",
                        ItemStatus.OutcomeUnknown => "[Outcome unknown]",
                        _ => $"[{item.Status}]"
                    };
                    string detail = item.Issue != null ? item.Issue.Message : "Failed";
                    itemNotes.Add($"• {statusLabel} {item.PipeName}: {detail}");
                }
            }
            if (itemNotes.Count > 0)
            {
                int maxShow = 10;
                sections.Add("Item details:\n" + string.Join("\n", itemNotes.Take(maxShow)) +
                    (itemNotes.Count > maxShow ? $"\n• …and {itemNotes.Count - maxShow} more (see command line)." : ""));
            }

            // Preview warnings that apply
            List<string> previewWarnings = preview?.Issues
                .Where(i => i.Severity == IssueSeverity.Warning)
                .Select(i => "• " + i.Message)
                .Distinct()
                .ToList() ?? new List<string>();
            if (previewWarnings.Count > 0)
            {
                sections.Add("Preview notes:\n" + string.Join("\n", previewWarnings.Take(10)));
            }

            // Operation-level issues: parts list changes
            List<string> partsListChanges = r.Issues
                .Where(i => i.Code == SplitIssueCodes.PartsListChanged)
                .Select(i => "• " + i.Message)
                .ToList();
            if (partsListChanges.Count > 0)
            {
                sections.Add("Parts lists:\n" + string.Join("\n", partsListChanges));
            }

            // Other operation-level issues
            List<string> opIssues = r.Issues
                .Where(i => i.Code != SplitIssueCodes.PartsListChanged)
                .Select(i => "• " + i.Message)
                .ToList();
            if (opIssues.Count > 0)
            {
                sections.Add(string.Join("\n", opIssues));
            }

            // Span warning
            if (!string.IsNullOrWhiteSpace(spanWarning))
                sections.Add(spanWarning.Trim());

            // Reversibility note
            if (r.PipesSplit > 0)
                sections.Add("UNDO reverses the whole change.");

            // Update footer status label
            SetSplitStatus($"[{statusTitle}] {what}", statusColor);

            // Update split-page result card
            _lblSplitResultTitle.Text = statusTitle.ToUpperInvariant();
            _lblSplitResultTitle.ForeColor = statusColor;
            _lblSplitResultDetails.Text = what + (sections.Count > 0 ? "\n\n" + string.Join("\n\n", sections) : "");
            UpdateSplitWrapping();
            _pnlSplitResult.Visible = true;

            string dialogMessage = $"{statusTitle.ToUpperInvariant()}\n\n{what}" +
                (sections.Count > 0 ? "\n\n" + string.Join("\n\n", sections) : "");

            MessageBox.Show(this, dialogMessage.Trim(), $"{Text} - {statusTitle}", MessageBoxButtons.OK, icon);
        }

        private static string IssueText(IEnumerable<OperationIssue> issues)
        {
            if (issues == null) return "Nothing to split.";
            List<string> lines = issues
                .Select(i => (i.Severity == IssueSeverity.Error ? "• Error: " : i.Severity == IssueSeverity.Warning ? "• Warning: " : "• ") + i.Message)
                .Distinct()
                .ToList();
            return lines.Count == 0 ? "Nothing to split." : string.Join("\n", lines);
        }

        private void RunParallel()
        {
            if (!ToolUi.TryNumberList(_txtH.Text, out List<double> h) || !ToolUi.TryNumberList(string.IsNullOrWhiteSpace(_txtV.Text) ? "0" : _txtV.Text, out List<double> v))
            {
                MessageBox.Show(this, "Enter the offsets as numbers separated by commas, for example -0.4, -0.2, 0, 0.2, 0.4.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_session.Pipes.Count == 0) { MessageBox.Show(this, "Choose some pipes first.", Text); return; }
            int perPipe = h.Count * v.Count - (h.Any(x => Math.Abs(x) < 1e-9) && v.Any(x => Math.Abs(x) < 1e-9) ? 1 : 0);
            if (perPipe <= 0) { MessageBox.Show(this, "Every offset is the source position itself; nothing would be copied.", Text); return; }
            NetworkEditOps.ParallelOptions o = new NetworkEditOps.ParallelOptions
            {
                Horizontal = h, Vertical = v, SameNetwork = _rbSameNet.Checked, NewNetworkName = string.IsNullOrWhiteSpace(_txtNewNet.Text) ? "Parallel copies" : _txtNewNet.Text.Trim(),
                Part = _rbChosenSize.Checked ? _parPart.Pick : null,
                PartsListId = _rbChosenSize.Checked && _parPart.Pick != null ? _parPart.Pick.PartsListId : ObjectId.Null,
                FitToStructures = _chkParFit.Checked, Face = _cbParFace.SelectedIndex == 3 ? StructureFace.InnerWall : (StructureFace)Math.Max(0, _cbParFace.SelectedIndex),
                Mitre = _chkParMitre.Checked, ConnectToStructures = _chkParConnect.Checked
            };
            if (_chkParFit.Checked && _cbParFace.SelectedIndex == 3)
            {
                if (!ToolUi.TryNumber(_txtParBoxL, out o.BoxLength) || !ToolUi.TryNumber(_txtParBoxW, out o.BoxWidth) || o.BoxLength <= 0 || o.BoxWidth <= 0)
                { MessageBox.Show(this, "Enter the box's length and width as positive numbers.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            if (MessageBox.Show(this, $"Create {perPipe} cop{(perPipe == 1 ? "y" : "ies")} of each of the {_session.Pipes.Count} chosen pipe(s) ({perPipe * _session.Pipes.Count} new pipes)" +
                                      (o.SameNetwork ? " in their own networks?" : $" in the new network \"{o.NewNetworkName}\"?"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyParallel(tr, CivilApplication.ActiveDocument, acDoc.Database, new List<ObjectId>(_session.Pipes), o, ed));
            Report($"Created {c.Created} copies of {c.Done} pipe(s).", c, c.Joined > 0 ? $"Copy ends connected to structures: {c.Joined}" : "");
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
            extra += "\n" + SpanWarning(gravity);
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
            ViewHelper.Regen(acDoc);
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
                "Pressure to gravity: gravity copies of pressure pipes in a new network.\n" +
                "Rims to surface: sets structure rims on a surface, once or linked to it; sumps and pipes stay where they are.\n\n" +
                "Levels, cover and profiles are in the Profile & Cover Designer.",
                "Network Editor - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
