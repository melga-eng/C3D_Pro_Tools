using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.NavisLink;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Label = System.Windows.Forms.Label;
using Point = System.Drawing.Point;
using Surface = Autodesk.Civil.DatabaseServices.Surface;

namespace C3DProTools
{
    /// <summary>
    /// Navisworks Import: reads the current Navisworks selection over the local link, shows what each item is (measured from
    /// its real geometry and checked against its properties), matches it to Civil 3D parts, and creates pipes, box culverts,
    /// structures or exact solids. Modeless and resizable; everything is previewed before it changes the drawing.
    /// </summary>
    internal sealed class FormNavisImport : Form
    {
        private static FormNavisImport _instance;

        public static void ShowTool()
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new FormNavisImport();
                AcadUiApp.ShowModelessDialog(null, _instance, false);
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
        }

        private const int CCheck = 0, CName = 1, CAction = 2, CSize = 3, CPart = 4, CDev = 5, CConf = 6, CNetwork = 7, CResult = 8;
        private static readonly string[] ActionNames = { "Pipe", "Box culvert", "Structure", "3D solid", "Skip" };

        /// <summary>The rows, plans, chosen parts, transform and bound drawing: everything but the controls.</summary>
        private readonly NavisImportSession _session = new NavisImportSession();
        private readonly List<(string pipe, string label)> _instances = new List<(string, string)>();
        private string _chip = "All";
        private bool _loading, _busy;
        private readonly TransientMarkers _markers = new TransientMarkers();
        /// <summary>Cancelled when the window closes, so a Navisworks request still waiting is given up.</summary>
        private readonly System.Threading.CancellationTokenSource _life = new System.Threading.CancellationTokenSource();
        /// <summary>The "Get selection" request waiting for Navisworks (its Cancel button stops it).</summary>
        private System.Threading.CancellationTokenSource _reading;
        private readonly Button _btnCancelGet = StyleUi.Secondary("Cancel");
        // Navisworks → drawing: turn (°) about the origin, then shift (m).
        private readonly TextBox _txtDx = ToolUi.Number(0, 80), _txtDy = ToolUi.Number(0, 80), _txtDz = ToolUi.Number(0, 60), _txtRot = ToolUi.Number(0, 55);

        private readonly Label _lblLink = new Label { AutoSize = true, ForeColor = StyleUi.HeaderMuted, Margin = new Padding(2, 0, 3, 0) };
        private readonly ComboBox _cbInstance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(3, 9, 3, 3) };
        private readonly Dictionary<string, StyleChip> _chips = new Dictionary<string, StyleChip>();
        private readonly TextBox _txtSearch = new TextBox { PlaceholderText = "Search names, sizes, networks…", Width = 300, Anchor = AnchorStyles.Right, Margin = new Padding(3, 9, 3, 3) };
        private readonly DataGridView _grid = new DataGridView();
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6, BackColor = StyleUi.Border };

        private readonly Label _detName = new Label { Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading };
        private readonly Label _detSub = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 0, 0, 4) };
        private readonly Panel _detPills = new Panel { Dock = DockStyle.Top, Height = 30 };
        private readonly ListView _lvCompare = new ListView { Dock = DockStyle.Top, Height = 170, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.FixedSingle };
        private readonly Label _detNotes = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        private readonly TextBox _txtPropFilter = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Filter properties…" };
        private readonly ListView _lvProps = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle };
        private ImportCandidate _detail;

        private readonly TextBox _txtPartsList = StyleUi.Box();
        private readonly ComboBox _cbGrouping = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly TextBox _txtPrefix = StyleUi.Box();
        private readonly ComboBox _cbSurface = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly CheckBox _chkConnect = new CheckBox { Text = "Connect pipes to the structures they end in", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        private readonly ComboBox _cbExisting = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly TextBox _txtSolidLayer = StyleUi.Box();
        private readonly CheckBox _chkDrawingLists = new CheckBox { Text = "Use exact sizes from other parts lists first", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        private readonly NumericUpDown _numMaxError = new NumericUpDown { Minimum = 1, Maximum = 1000, Width = 70, Margin = new Padding(3, 4, 3, 4) };
        private readonly ComboBox _cbNoSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(3, 4, 3, 4) };

        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel _summary = new ToolStripStatusLabel { ForeColor = StyleUi.Muted };
        private Button _btnImport, _btnSizeMode, _btnOptions;
        private Control _optionsPanel;
        private readonly ToolTip _tips = new ToolTip();

        private FormNavisImport()
        {
            Text = "C3D Pro Tools - Navisworks Import";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1040, 720);
            KeyPreview = true;
            ShowInTaskbar = false;

            BuildGrid();
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(BuildDetails());

            StatusStrip strip = new StatusStrip { BackColor = StyleUi.Panel, SizingGrip = true };
            strip.Items.AddRange(new ToolStripItem[] { _status, _summary });

            _optionsPanel = BuildOptions();
            Controls.Add(_split);
            Controls.Add(_optionsPanel);
            Controls.Add(BuildChipBar());
            Controls.Add(BuildLinkBar());
            Controls.Add(BuildHeader());
            Controls.Add(BuildFooter());
            Controls.Add(strip);
            _optionsPanel.Visible = _session.Settings.OptionsVisible;
            UpdateOptionsToggle();

            _txtSearch.TextChanged += (s, e) => FillGrid();
            _txtPropFilter.TextChanged += (s, e) => FillProps();
            RestoreLayout();
            FormClosing += (s, e) => SaveLayout();
            AcadCoreApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcadCoreApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            FormClosed += (s, e) =>
            {
                AcadCoreApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                AcadCoreApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                _life.Cancel();
                _markers.Dispose();
                _instance = null;
            };
            Shown += async (s, e) =>
            {
                FillSurfaces();
                // Asked at the start: how sizes the catalog lacks are made (Method A, Method B or nearest size).
                if (_session.Settings.AskSizeMode) ChooseSizeMode();
                else if (_session.Settings.SizeMode == (int)CatalogMode.AnySize) SetUpAnySize(false);
                await FindInstances();
            };
            Activated += (s, e) => FillSurfaces();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        /// <summary>Title on the left; secondary actions on the right, wrapping under each other when the window is narrow.</summary>
        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Header, Padding = new Padding(16, 10, 12, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label title = new Label { Text = "Navisworks Import", Font = StyleUi.Title, ForeColor = StyleUi.HeaderText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 16, 0) };

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0) };
            actions.Controls.Add(StyleUi.HeaderButton("How it works", (s, e) => ShowHelp()));
            _btnSizeMode = StyleUi.HeaderButton(SizeModeText(), (s, e) => ChooseSizeMode());
            actions.Controls.Add(_btnSizeMode);
            actions.Controls.Add(StyleUi.HeaderButton("Show in Navisworks", (s, e) => ShowInNavisworks()));
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(actions, 1, 0);
            return header;
        }

        /// <summary>The Navisworks connection: which window to read from, the main "Get selection" action and the link status.</summary>
        private Control BuildLinkBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.FromArgb(30, 41, 59), Padding = new Padding(14, 4, 12, 6) };
            bar.Controls.Add(new Label { Text = "Navisworks", AutoSize = true, ForeColor = StyleUi.HeaderMuted, Margin = new Padding(3, 10, 6, 3) });
            _cbInstance.Margin = new Padding(3, 6, 3, 3);
            bar.Controls.Add(_cbInstance);
            Button refresh = StyleUi.HeaderButton("↻", async (s, e) => await FindInstances());
            refresh.Margin = new Padding(3, 4, 3, 3);
            bar.Controls.Add(refresh);
            Button get = StyleUi.Primary("⬇  Get selection from Navisworks", async (s, e) => await GetSelection());
            get.Margin = new Padding(8, 4, 3, 3);
            bar.Controls.Add(get);
            _btnCancelGet.Margin = new Padding(3, 4, 3, 3);
            _btnCancelGet.Visible = false;
            _btnCancelGet.Click += (s, e) => _reading?.Cancel();
            bar.Controls.Add(_btnCancelGet);
            _lblLink.Margin = new Padding(12, 10, 3, 3);
            bar.Controls.Add(_lblLink);
            return bar;
        }

        private Control BuildChipBar()
        {
            TableLayoutPanel bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Surface, Padding = new Padding(10, 2, 12, 2) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel chips = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            foreach ((string text, string kind) in new[]
            {
                ("All", "All"), ("Pipes", "Pipe"), ("Box culverts", "Box"), ("Structures", "Structure"), ("Solids", "Solid"),
                ("Needs review", "Review"), ("Already imported", "Already imported"), ("Ticked", "Checked")
            })
            {
                StyleChip chip = new StyleChip(text, kind);
                chip.Click += (s, e) => { _chip = kind; foreach (StyleChip c in _chips.Values) c.Selected = c.Kind == kind; FillGrid(); };
                _chips[kind] = chip;
                chips.Controls.Add(chip);
            }
            _chips["All"].Selected = true;
            bar.Controls.Add(chips, 0, 0);
            bar.Controls.Add(_txtSearch, 1, 0);
            return bar;
        }

        private void BuildGrid()
        {
            DataGridView g = _grid;
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = true;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = StyleUi.Panel;
            g.GridColor = Color.FromArgb(241, 245, 249);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34;
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = StyleUi.Surface, ForeColor = StyleUi.Muted, Font = StyleUi.Bold, SelectionBackColor = StyleUi.Surface, SelectionForeColor = StyleUi.Muted, WrapMode = DataGridViewTriState.False };
            g.DefaultCellStyle = new DataGridViewCellStyle { BackColor = StyleUi.Panel, ForeColor = StyleUi.Text, SelectionBackColor = StyleUi.AccentSoft, SelectionForeColor = StyleUi.Text, Padding = new Padding(4, 0, 4, 0), WrapMode = DataGridViewTriState.False };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(250, 251, 253) };
            g.RowTemplate.Height = 28;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.EditMode = DataGridViewEditMode.EditOnEnter;

            g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 34, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Resizable = DataGridViewTriState.False });
            g.Columns.Add(TextCol("Navisworks item", 24));
            DataGridViewComboBoxColumn action = new DataGridViewComboBoxColumn { HeaderText = "Import as", Width = 120, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
            action.Items.AddRange(ActionNames);
            g.Columns.Add(action);
            g.Columns.Add(TextCol("Navisworks size", 22));
            g.Columns.Add(TextCol("Civil 3D part", 34));
            g.Columns.Add(Fixed("Error", 70, DataGridViewContentAlignment.MiddleRight));
            g.Columns.Add(Fixed("Confidence", 100));
            g.Columns.Add(TextCol("Network", 16));
            g.Columns.Add(TextCol("Result", 12));
            g.ShowCellToolTips = true;
            for (int i = 1; i < g.Columns.Count; i++) g.Columns[i].ReadOnly = i != CAction;
            g.Columns[CPart].DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Text };
            g.Columns[CNetwork].DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Muted };

            g.CurrentCellDirtyStateChanged += (s, e) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            g.CellValueChanged += Grid_CellValueChanged;
            g.CellPainting += Grid_CellPainting;
            g.SelectionChanged += (s, e) => ShowDetails(CurrentRow());
            g.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space && g.CurrentCell?.ColumnIndex != CCheck && g.CurrentCell?.ColumnIndex != CAction)
                {
                    List<ImportCandidate> sel = SelectedRows();
                    bool on = !sel.All(r => _session.Ticked.Contains(r));
                    foreach (ImportCandidate r in sel) { if (on) _session.Ticked.Add(r); else _session.Ticked.Remove(r); }
                    RefreshTicks();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            g.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.ColumnIndex != CCheck) return;
                List<ImportCandidate> visible = VisibleRows();
                bool on = !visible.All(r => _session.Ticked.Contains(r));
                foreach (ImportCandidate r in visible) { if (on) _session.Ticked.Add(r); else _session.Ticked.Remove(r); }
                RefreshTicks();
            };
            g.DataError += (s, e) => e.ThrowException = false;
            g.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && (e.ColumnIndex == CPart || e.ColumnIndex == CDev)) ChoosePart(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Choose Civil 3D part…", null, (s, e) => ChoosePart());
            menu.Items.Add("Back to automatic part", null, (s, e) => ClearOverrides(SelectedRows()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Tick selected rows", null, (s, e) => { foreach (ImportCandidate r in SelectedRows()) _session.Ticked.Add(r); RefreshTicks(); });
            menu.Items.Add("Untick selected rows", null, (s, e) => { foreach (ImportCandidate r in SelectedRows()) _session.Ticked.Remove(r); RefreshTicks(); });
            menu.Items.Add(new ToolStripSeparator());
            foreach (string a in ActionNames)
            {
                string name = a;
                menu.Items.Add("Import selected as " + name.ToLowerInvariant(), null, (s, e) => SetAction(SelectedRows(), ActionFromName(name)));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show in Navisworks", null, (s, e) => ShowInNavisworks());
            menu.Items.Add("Zoom in drawing", null, (s, e) => PreviewInDrawing(SelectedRows()));
            g.ContextMenuStrip = menu;
        }

        private static DataGridViewTextBoxColumn TextCol(string header, float weight) => new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, MinimumWidth = 90 };

        private static DataGridViewTextBoxColumn Fixed(string header, int width, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft) =>
            new DataGridViewTextBoxColumn { HeaderText = header, Width = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = new DataGridViewCellStyle { Alignment = align } };

        /// <summary>
        /// Selected row: name, source and pills, the actions right under them, then two tabs: the comparison (Navisworks,
        /// measured, Civil 3D) with its notes, and every Navisworks property. Tabs keep both readable at any window size.
        /// </summary>
        private Control BuildDetails()
        {
            Panel host = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Panel, Padding = new Padding(14, 10, 12, 10) };
            _lvCompare.Columns.Add("", 100);
            _lvCompare.Columns.Add("Navisworks", 110);
            _lvCompare.Columns.Add("Measured", 90);
            _lvCompare.Columns.Add("Civil 3D part", 110);
            _lvCompare.Dock = DockStyle.Fill;
            _lvCompare.ShowItemToolTips = true;
            _lvProps.ShowItemToolTips = true;
            _lvCompare.BorderStyle = BorderStyle.None;
            _lvProps.Columns.Add("Tab", 90);
            _lvProps.Columns.Add("Property", 140);
            _lvProps.Columns.Add("Value", 160);
            _lvProps.BorderStyle = BorderStyle.None;
            _detPills.Dock = DockStyle.Fill;
            _detPills.Margin = new Padding(0, 2, 0, 2);
            _detPills.Paint += (s, e) =>
            {
                if (_detail == null) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float x = 0;
                foreach (string pill in Pills(_detail)) x += StyleUi.DrawPill(e.Graphics, pill, x, 14, StyleUi.Bold) + 6;
            };
            _detName.Dock = _detSub.Dock = DockStyle.None;
            _detName.Margin = new Padding(0);
            _detSub.Margin = new Padding(0, 2, 0, 2);

            FlowLayoutPanel buttons = StyleUi.Row(
                StyleUi.Primary("Choose part…", (s, e) => ChoosePart()),
                StyleUi.Secondary("Show in Navisworks", (s, e) => ShowInNavisworks()),
                StyleUi.Secondary("Zoom", (s, e) => PreviewInDrawing(SelectedRows())));
            buttons.Dock = DockStyle.Fill;
            buttons.WrapContents = true;
            buttons.Margin = new Padding(0, 2, 0, 6);

            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.Controls.Add(_detName);
            top.Controls.Add(_detSub);
            top.Controls.Add(_detPills);
            top.Controls.Add(buttons);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
            TabPage compare = new TabPage("Comparison") { BackColor = StyleUi.Panel, Padding = new Padding(6) };
            _detNotes.Dock = DockStyle.Top;
            _detNotes.Padding = new Padding(2, 2, 2, 8);
            compare.Controls.Add(_lvCompare);
            compare.Controls.Add(_detNotes);
            TabPage props = new TabPage("All properties") { BackColor = StyleUi.Panel, Padding = new Padding(6) };
            Button copy = StyleUi.Secondary("Copy", (s, e) => CopyProps());
            TableLayoutPanel filterRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _txtPropFilter.Dock = DockStyle.Fill;
            _txtPropFilter.Margin = new Padding(0, 5, 6, 5);
            filterRow.Controls.Add(_txtPropFilter, 0, 0);
            filterRow.Controls.Add(copy, 1, 0);
            props.Controls.Add(_lvProps);
            props.Controls.Add(filterRow);
            tabs.TabPages.Add(compare);
            tabs.TabPages.Add(props);

            host.Controls.Add(tabs);
            host.Controls.Add(top);
            host.Resize += (s, e) =>
            {
                Size max = new Size(Math.Max(120, host.ClientSize.Width - host.Padding.Horizontal), 0);
                _detName.MaximumSize = _detSub.MaximumSize = max;
                _detNotes.MaximumSize = new Size(Math.Max(120, max.Width - 24), 0);
            };
            _lvCompare.Resize += (s, e) => Spread(_lvCompare, 0.24, 0.28, 0.20, 0.28);
            _lvProps.Resize += (s, e) => Spread(_lvProps, 0.24, 0.34, 0.42);
            ShowDetails(null);
            return host;
        }

        private static bool _spreading;

        /// <summary>
        /// Shares a list's width between its columns, so nothing is cut off or leaves a gap. The total stays below the width
        /// with a vertical scrollbar, so it never brings up a horizontal one (which would resize the list and start again).
        /// </summary>
        private static void Spread(ListView list, params double[] shares)
        {
            if (_spreading || list.Columns.Count != shares.Length) return;
            int width = list.Width - SystemInformation.VerticalScrollBarWidth - 6;
            if (width <= 0) return;
            _spreading = true;
            try
            {
                for (int i = 0; i < shares.Length; i++)
                {
                    int w = Math.Max(40, (int)(width * shares[i]));
                    if (list.Columns[i].Width != w) list.Columns[i].Width = w;
                }
            }
            finally { _spreading = false; }
        }

        private Control BuildOptions()
        {
            _cbGrouping.Items.AddRange(new object[] { "Source network (from the model)", "Navisworks layer", "One network" });
            _cbExisting.Items.AddRange(new object[] { "Replace the earlier import", "Skip it", "Import again (duplicate)" });

            TableLayoutPanel parts = Grid2(("Parts list", _txtPartsList));
            parts.Controls.Add(_chkDrawingLists, 0, 1);
            parts.SetColumnSpan(_chkDrawingLists, 2);
            _chkDrawingLists.Checked = _session.Settings.SearchDrawingLists;
            _chkDrawingLists.CheckedChanged += (s, e) => { if (_session.Plan.Count > 0) RunPlan(false); };
            StyleCard cParts = new StyleCard("Parts", "", parts);

            FlowLayoutPanel shift = ToolUi.Inline(Small("X"), _txtDx, Small("Y"), _txtDy, Small("Z"), _txtDz, Small("turn"), _txtRot, Small("°"));
            TableLayoutPanel nets = Grid2(("Group by", _cbGrouping), ("Name prefix", _txtPrefix), ("Surface", _cbSurface), ("Shift (m)", shift));
            _tips.SetToolTip(shift, "For a model on a local or shared grid: Navisworks coordinates are turned about the origin (counter-clockwise), then shifted. " +
                                    "Used when the selection is read: get the selection again after changing it.");
            StyleCard cNets = new StyleCard("Networks", "", nets);

            _cbNoSize.Items.AddRange(new object[] { "Import the exact 3D solid", "Use the nearest size anyway" });
            Label mm = new Label { Text = "mm of the Navisworks size", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 3, 3, 3) };
            TableLayoutPanel limit = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            limit.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            limit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _numMaxError.Margin = new Padding(3, 4, 3, 4);
            limit.Controls.Add(_numMaxError, 0, 0);
            limit.Controls.Add(mm, 1, 0);
            TableLayoutPanel opts = Grid2(("Use sizes within", limit), ("Otherwise", _cbNoSize), ("Already imported", _cbExisting), ("Solids layer", _txtSolidLayer));
            opts.Controls.Add(_chkConnect, 0, 4);
            opts.SetColumnSpan(_chkConnect, 2);
            StyleCard cOpts = new StyleCard("Options", "", opts);
            _tips.SetToolTip(_txtPartsList, "Catalog families and sizes are added to this list only as they are used. Double-click a part in the list to choose your own.");
            _tips.SetToolTip(_cbGrouping, "One network per source keeps interference checks against your design network simple.");
            _tips.SetToolTip(_numMaxError, "A wrong-sized part gives false clashes, so sizes further off than this come in as exact solids unless you choose a part.");
            _tips.SetToolTip(_cbNoSize, "What happens when no catalog size is within the limit.");

            // Tall enough for every row at this screen's scaling; each card scrolls rather than clipping when the window is small.
            int height = (int)Math.Round(258 * DeviceDpi / 96.0);
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = height, ColumnCount = 3, Padding = new Padding(6, 4, 6, 0), BackColor = StyleUi.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            row.Controls.Add(cParts, 0, 0);
            row.Controls.Add(cNets, 1, 0);
            row.Controls.Add(cOpts, 2, 0);

            _txtPartsList.Text = _session.Settings.PartsListName;
            _cbGrouping.SelectedIndex = Math.Max(0, Math.Min(2, _session.Settings.Grouping));
            _txtPrefix.Text = _session.Settings.Prefix;
            _chkConnect.Checked = _session.Settings.ConnectToStructures;
            _cbExisting.SelectedIndex = Math.Max(0, Math.Min(2, _session.Settings.Existing));
            _txtSolidLayer.Text = _session.Settings.SolidLayer;
            _numMaxError.Value = Math.Max(1, Math.Min(1000, _session.Settings.MaxPartErrorMm));
            _cbNoSize.SelectedIndex = Math.Max(0, Math.Min(1, _session.Settings.NoSizeMode));
            _numMaxError.ValueChanged += (s, e) => { if (_session.Plan.Count > 0) RunPlan(false); };
            _cbNoSize.SelectedIndexChanged += (s, e) => { if (_session.Plan.Count > 0) RunPlan(false); };
            _cbGrouping.SelectedIndexChanged += (s, e) => RefreshNetworks();
            _txtPrefix.TextChanged += (s, e) => RefreshNetworks();
            _txtPartsList.Leave += (s, e) => InvalidatePlan();
            _txtDx.Text = ToolUi.Format(_session.Settings.TransformDx);
            _txtDy.Text = ToolUi.Format(_session.Settings.TransformDy);
            _txtDz.Text = ToolUi.Format(_session.Settings.TransformDz);
            _txtRot.Text = ToolUi.Format(_session.Settings.TransformRotationDeg);
            foreach (TextBox box in new[] { _txtDx, _txtDy, _txtDz, _txtRot })
                box.TextChanged += (s, e) => { if (_session.Rows.Count > 0) SetStatus("The shift is used when the selection is read: click \"Get selection from Navisworks\" again.", true); };
            return row;
        }

        private static Label Small(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 2, 0) };

        /// <summary>The shift and turn from the option boxes (a box that is not a number counts as 0).</summary>
        private NavisTransform ReadTransform()
        {
            double Value(TextBox box) => ToolUi.TryNumber(box, out double v) ? v : 0;
            return new NavisTransform { Dx = Value(_txtDx), Dy = Value(_txtDy), Dz = Value(_txtDz), RotationDeg = Value(_txtRot) };
        }

        /// <summary>Label / control pairs; labels are centred on their row, controls stretch, and the grid scrolls if the card is too small.</summary>
        private static TableLayoutPanel Grid2(params (string label, Control control)[] rows)
        {
            TableLayoutPanel t = new TableLayoutPanel { ColumnCount = 2, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows.Length; i++)
            {
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.Controls.Add(new Label { Text = rows[i].label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 8, 10, 3) }, 0, i);
                rows[i].control.Dock = DockStyle.Fill;
                t.Controls.Add(rows[i].control, 1, i);
            }
            return t;
        }

        /// <summary>Main actions on the right; on the left a toggle that shows or hides the options (remembered).</summary>
        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 6, 12, 8), BackColor = StyleUi.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _btnOptions = StyleUi.Secondary("Options", (s, e) =>
            {
                _optionsPanel.Visible = !_optionsPanel.Visible;
                _session.Settings.OptionsVisible = _optionsPanel.Visible;
                UpdateOptionsToggle();
            });
            _btnOptions.Anchor = AnchorStyles.Left;
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0) };
            _btnImport = StyleUi.Primary("Import ticked…", (s, e) => ImportTicked());
            actions.Controls.Add(_btnImport);
            actions.Controls.Add(StyleUi.Secondary("Check parts", (s, e) => RunPlan(true)));
            actions.Controls.Add(StyleUi.Secondary("Preview in drawing", (s, e) => PreviewInDrawing(TickedRows())));
            actions.Controls.Add(StyleUi.Secondary("Clear preview", (s, e) => _markers.Clear()));
            footer.Controls.Add(_btnOptions, 0, 0);
            footer.Controls.Add(actions, 1, 0);
            return footer;
        }

        private void UpdateOptionsToggle()
        {
            // The window may not be shown yet (then every child reports hidden), so the saved choice decides the text.
            if (_btnOptions != null) _btnOptions.Text = _session.Settings.OptionsVisible ? "⚙  Hide options  ▾" : "⚙  Show options  ▴";
        }

        private void RestoreLayout()
        {
            Size = new Size(Math.Max(MinimumSize.Width, _session.Settings.WindowWidth), Math.Max(MinimumSize.Height, _session.Settings.WindowHeight));
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            StartPosition = FormStartPosition.Manual;
            if (_session.Settings.WindowX >= 0 && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(_session.Settings.WindowX + 50, _session.Settings.WindowY + 20)))
                Location = new Point(_session.Settings.WindowX, _session.Settings.WindowY);
            else
            {
                Width = Math.Min(Width, area.Width - 40);
                Height = Math.Min(Height, area.Height - 40);
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
            if (_session.Settings.Maximized) WindowState = FormWindowState.Maximized;
            Shown += (s, e) =>
            {
                try
                {
                    _split.Panel1MinSize = 420;
                    _split.Panel2MinSize = 300;
                    _split.SplitterDistance = Math.Max(420, Math.Min(_split.Width - 300, _split.Width - _session.Settings.DetailsWidth));
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            };
        }

        private void SaveLayout()
        {
            _session.Settings.Maximized = WindowState == FormWindowState.Maximized;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _session.Settings.WindowX = b.X; _session.Settings.WindowY = b.Y; _session.Settings.WindowWidth = b.Width; _session.Settings.WindowHeight = b.Height;
            _session.Settings.DetailsWidth = _split.Width - _split.SplitterDistance;
            CaptureOptions();
            _session.Settings.Save();
        }

        private void CaptureOptions()
        {
            _session.Settings.PartsListName = string.IsNullOrWhiteSpace(_txtPartsList.Text) ? "Navis Import" : _txtPartsList.Text.Trim();
            _session.Settings.Grouping = _cbGrouping.SelectedIndex;
            _session.Settings.Prefix = _txtPrefix.Text;
            _session.Settings.ConnectToStructures = _chkConnect.Checked;
            _session.Settings.Existing = _cbExisting.SelectedIndex;
            _session.Settings.SolidLayer = string.IsNullOrWhiteSpace(_txtSolidLayer.Text) ? "NAVIS-SOLIDS" : _txtSolidLayer.Text.Trim();
            _session.Settings.SurfaceName = (_cbSurface.SelectedItem as Choice)?.Text ?? "";
            _session.Settings.MaxPartErrorMm = (int)_numMaxError.Value;
            _session.Settings.NoSizeMode = Math.Max(0, _cbNoSize.SelectedIndex);
            _session.Settings.SearchDrawingLists = _chkDrawingLists.Checked;
            NavisTransform t = ReadTransform();
            _session.Settings.TransformDx = t.Dx; _session.Settings.TransformDy = t.Dy; _session.Settings.TransformDz = t.Dz; _session.Settings.TransformRotationDeg = t.RotationDeg;
        }

        private NavisImportOptions Options()
        {
            CaptureOptions();
            return _session.Options((_cbSurface.SelectedItem as Choice)?.Value is ObjectId id ? id : ObjectId.Null);
        }

        // =====================================================================================
        // Navisworks link
        // =====================================================================================

        // =====================================================================================
        // The drawing the rows are matched against
        // =====================================================================================

        /// <summary>
        /// Makes the matched parts, chosen parts and "already imported" marks those of <paramref name="doc"/>. For another
        /// drawing they are dropped and worked out again: chosen parts are object ids in the previous drawing's parts lists.
        /// Returns true when the drawing changed.
        /// </summary>
        private bool Bind(Document doc)
        {
            if (!_session.Bind(doc, out bool hadChoices)) return false;
            if (hadChoices) SetStatus("Another drawing is active: the parts you chose belonged to the previous drawing and were cleared.", true);
            return true;
        }

        private void OnDocumentActivated(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated || e.Document == null) return;
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                // The preview belongs to the drawing it was drawn in.
                if (_markers.Owner != null && _markers.Owner != e.Document) _markers.Clear();
                if (!Bind(e.Document) || _session.Rows.Count == 0) return;
                try { RunPlan(false); }
                catch (Exception ex) { SetStatus("Part matching failed: " + ex.Message, true); }
            }));
        }

        private void OnDocumentToBeDestroyed(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            // While the drawing still exists: its preview markers are erased from it now.
            if (_markers.Owner == e.Document) _markers.Clear();
            if (!_session.Unbind(e.Document)) return;
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) RefreshRows(); }));
        }

        private async Task FindInstances()
        {
            SetStatus("Looking for Navisworks…");
            List<(string pipe, string label, int selected)> found = await Task.Run(() =>
            {
                List<(string, string, int)> list = new List<(string, string, int)>();
                string[] pipes;
                try { pipes = Directory.GetFiles(@"\\.\pipe\"); } catch (Exception) { pipes = new string[0]; }
                foreach (string full in pipes)
                {
                    string name = Path.GetFileName(full);
                    if (!name.StartsWith(NavisLinkWire.PipePrefix, StringComparison.Ordinal)) continue;
                    try
                    {
                        NavisResponse hello = NavisLinkWire.Send(name, new NavisRequest { Command = "hello" }, 1000, 5000, _life.Token);
                        string doc = hello.Ok ? Path.GetFileName(hello.Document) : "no model open";
                        list.Add((name, $"{doc}  ·  {hello.SelectedCount} selected", hello.SelectedCount));
                    }
                    // Listed, so its "Get selection" says what to do rather than the window never finding it.
                    catch (InvalidDataException) { list.Add((name, "C3D Pro Tools add-in of another release: install the same release", 0)); }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                return list;
            });
            if (IsDisposed) return;

            string previous = _cbInstance.SelectedIndex >= 0 && _cbInstance.SelectedIndex < _instances.Count ? _instances[_cbInstance.SelectedIndex].pipe : null;
            _instances.Clear();
            _cbInstance.Items.Clear();
            foreach ((string pipe, string label, int _) in found) { _instances.Add((pipe, label)); _cbInstance.Items.Add(label); }
            if (_instances.Count > 0)
            {
                int pick = _instances.FindIndex(i => i.pipe == previous);
                if (pick < 0) pick = Math.Max(0, found.FindIndex(f => f.selected > 0));
                _cbInstance.SelectedIndex = pick;
                _lblLink.Text = _instances.Count == 1 ? "● Connected to Navisworks" : $"● {_instances.Count} Navisworks windows found — pick one";
                _lblLink.ForeColor = Color.FromArgb(134, 239, 172);
                SetStatus("Select items in Navisworks, then click \"Get selection from Navisworks\".");
            }
            else
            {
                _lblLink.Text = "● Navisworks not found — open Navisworks Manage (with C3D Pro Tools installed), then press ↻";
                _lblLink.ForeColor = Color.FromArgb(252, 165, 165);
                SetStatus("No Navisworks window with the C3D Pro Tools link is running.", true);
            }
        }

        private string CurrentPipe() => _cbInstance.SelectedIndex >= 0 && _cbInstance.SelectedIndex < _instances.Count ? _instances[_cbInstance.SelectedIndex].pipe : null;

        private async Task GetSelection()
        {
            if (_busy) return;
            if (CurrentPipe() == null) await FindInstances();
            string pipe = CurrentPipe();
            if (pipe == null) return;
            _busy = true;
            UseWaitCursor = true;
            SetStatus("Reading the selection and its geometry from Navisworks… (Cancel stops waiting)");
            NavisResponse response;
            Stopwatch sw = Stopwatch.StartNew();
            _reading = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _btnCancelGet.Visible = true;
            try
            {
                System.Threading.CancellationToken token = _reading.Token;
                response = await Task.Run(() => NavisLinkWire.Send(pipe, new NavisRequest { Command = "selection" }, 3000, 120000, token));
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _busy = false; UseWaitCursor = false;
                SetStatus(ex is OperationCanceledException ? "Stopped waiting for Navisworks." : ex is InvalidDataException ? ex.Message : "Navisworks did not answer: " + ex.Message, true);
                return;
            }
            finally
            {
                _reading?.Dispose();
                _reading = null;
                if (!IsDisposed) { UseWaitCursor = false; _btnCancelGet.Visible = false; }
            }
            // The window may have been closed while Navisworks was answering.
            if (IsDisposed) return;
            _busy = false;
            if (!response.Ok) { SetStatus(response.Error, true); return; }
            if (response.Elements.Count == 0) { SetStatus("Nothing is selected in Navisworks (or the selection has no geometry).", true); return; }

            _session.Transform = ReadTransform();
            _session.Load(response);
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            Bind(doc);
            _session.RefreshExisting(doc);
            _lblLink.Text = $"● {Path.GetFileName(response.Document)}  ·  {response.Elements.Count} item(s) read in {sw.Elapsed.TotalSeconds:0.0} s";
            FillGrid();
            if (response.Warnings.Count > 0) SetStatus(string.Join("  ", response.Warnings.Take(3)), true);
            RunPlan(false);
            FindInstancesQuietly();
        }

        private async void FindInstancesQuietly()
        {
            try { await FindInstances(); }
            catch (Exception ex) { C3DProLogger.Warn("Navisworks Import: looking for instances in the background failed", ex); }
        }

        private void ShowInNavisworks()
        {
            string pipe = CurrentPipe();
            List<string> guids = SelectedRows().Select(r => r.Guid).Where(g => !string.IsNullOrEmpty(g)).ToList();
            if (pipe == null || guids.Count == 0) { SetStatus("Select rows (and make sure Navisworks is connected) first.", true); return; }
            Task.Run(() =>
            {
                string message;
                bool problem;
                try
                {
                    NavisResponse r = NavisLinkWire.Send(pipe, new NavisRequest { Command = "select", Guids = guids }, 2000, 30000, _life.Token);
                    message = r.Ok ? $"Selected {r.SelectedCount} item(s) in Navisworks." : r.Error;
                    problem = !r.Ok;
                }
                catch (Exception ex) { message = ex is InvalidDataException ? ex.Message : "Navisworks did not answer: " + ex.Message; problem = true; }
                // The window may have closed meanwhile; then there is no one to tell.
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) SetStatus(message, problem); })); }
                catch (InvalidOperationException) { }
            });
        }

        // =====================================================================================
        // Grid
        // =====================================================================================

        private bool Matches(ImportCandidate c, string chip)
        {
            switch (chip)
            {
                case "Pipe": return c.Action == ImportKind.Pipe;
                case "Box": return c.Action == ImportKind.Box;
                case "Structure": return c.Action == ImportKind.Structure;
                case "Solid": return c.Action == ImportKind.Solid;
                case "Review": return NeedsReview(c);
                case "Already imported": return _session.Existing.Contains(c.Guid ?? "");
                case "Checked": return _session.Ticked.Contains(c);
                default: return true;
            }
        }

        private bool NeedsReview(ImportCandidate c) =>
            c.Confidence != ImportConfidence.High || (_session.Plan.TryGetValue(c, out RowOutcome p) && (p.Failed || p.PartDeviation > NavisInterpreter.HighTolerance));

        private List<ImportCandidate> VisibleRows()
        {
            string q = _txtSearch.Text.Trim();
            return _session.Rows.Where(c => Matches(c, _chip) && (q.Length == 0 || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || c.SizeText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (c.Network ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        private void FillGrid()
        {
            _loading = true;
            ImportCandidate current = CurrentRow();
            _grid.Rows.Clear();
            NavisImportOptions o = Options();
            foreach (ImportCandidate c in VisibleRows())
            {
                int i = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[i];
                row.Tag = c;
                FillRow(row, c, o);
            }
            foreach (StyleChip chip in _chips.Values) chip.Count = _session.Rows.Count(c => Matches(c, chip.Kind));
            if (current != null)
                foreach (DataGridViewRow r in _grid.Rows)
                    if (r.Tag == current) { _grid.CurrentCell = r.Cells[CName]; break; }
            _loading = false;
            UpdateSummary();
            ShowDetails(CurrentRow());
        }

        private void FillRow(DataGridViewRow row, ImportCandidate c, NavisImportOptions o)
        {
            row.Cells[CCheck].Value = _session.Ticked.Contains(c);
            row.Cells[CName].Value = c.Name;
            row.Cells[CAction].Value = ActionName(c.Action);
            row.Cells[CSize].Value = c.SizeText;
            bool smart = c.Action != ImportKind.Solid && c.Action != ImportKind.Skip;
            if (_session.Plan.TryGetValue(c, out RowOutcome p))
            {
                row.Cells[CPart].Value = c.Action == ImportKind.Solid ? "Exact 3D solid" : c.Action == ImportKind.Skip ? ""
                    : p.Status == "Catalog size to add" ? $"＋ {p.Family} · {p.Size}"
                    : p.AsSolid ? $"Exact 3D solid — no size within {_numMaxError.Value:0} mm" + (p.Size.Length > 0 ? $" (nearest {p.Size})" : "")
                    : p.Failed ? "⚠ " + (p.Detail.Length > 0 ? p.Detail : "No part")
                    : (p.Overridden ? "✎ " : "") + $"{p.Family} · {p.Size}";
                row.Cells[CDev].Value = smart && !p.Failed && p.PartDeviation < 1e6 ? $"{p.PartDeviation * 1000:0} mm" : "";
                row.Cells[CPart].Style.ForeColor = p.AsSolid ? StyleUi.Muted : p.Overridden ? StyleUi.Accent : StyleUi.Text;
            }
            else
            {
                row.Cells[CPart].Value = c.Action == ImportKind.Solid ? "Exact 3D solid" : c.Action == ImportKind.Skip ? "" : "Check parts to match";
                row.Cells[CDev].Value = "";
            }
            row.Cells[CNetwork].Value = smart ? NavisImporter.NetworkFor(c, o) : c.Action == ImportKind.Solid ? o.SolidLayer : "";
            row.Cells[CResult].Value = _session.Done.TryGetValue(c, out RowOutcome d) ? d.Status : _session.Existing.Contains(c.Guid ?? "") ? "Already imported" : "";
            row.DefaultCellStyle.ForeColor = c.Action == ImportKind.Skip ? StyleUi.Muted : StyleUi.Text;
        }

        private void RefreshRows()
        {
            _loading = true;
            NavisImportOptions o = Options();
            foreach (DataGridViewRow r in _grid.Rows) if (r.Tag is ImportCandidate c) FillRow(r, c, o);
            foreach (StyleChip chip in _chips.Values) chip.Count = _session.Rows.Count(c => Matches(c, chip.Kind));
            _loading = false;
            UpdateSummary();
            ShowDetails(CurrentRow());
        }

        private void RefreshNetworks()
        {
            if (_loading) return;
            NavisImportOptions o = Options();
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is ImportCandidate c && c.Action != ImportKind.Solid && c.Action != ImportKind.Skip) r.Cells[CNetwork].Value = NavisImporter.NetworkFor(c, o);
        }

        private void RefreshTicks()
        {
            _loading = true;
            foreach (DataGridViewRow r in _grid.Rows) if (r.Tag is ImportCandidate c) r.Cells[CCheck].Value = _session.Ticked.Contains(c);
            _loading = false;
            if (_chips.TryGetValue("Checked", out StyleChip chip)) chip.Count = _session.Ticked.Count;
            UpdateSummary();
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading || e.RowIndex < 0 || !(_grid.Rows[e.RowIndex].Tag is ImportCandidate c)) return;
            if (e.ColumnIndex == CCheck)
            {
                if (_grid.Rows[e.RowIndex].Cells[CCheck].Value is bool b && b) _session.Ticked.Add(c); else _session.Ticked.Remove(c);
                if (_chips.TryGetValue("Checked", out StyleChip chip)) chip.Count = _session.Ticked.Count;
                UpdateSummary();
            }
            else if (e.ColumnIndex == CAction)
            {
                ImportKind kind = ActionFromName(_grid.Rows[e.RowIndex].Cells[CAction].Value as string);
                BeginInvoke(new Action(() => SetAction(new List<ImportCandidate> { c }, kind)));
            }
        }

        private void SetAction(List<ImportCandidate> rows, ImportKind kind)
        {
            List<string> refused = new List<string>();
            foreach (ImportCandidate c in rows)
            {
                if (!CanBe(c, kind)) { refused.Add(c.Name); continue; }
                // A pipe part cannot be used for a structure (or the other way round), so a pick for the old kind is dropped.
                if (c.Action != kind) _session.Overrides.Remove(c);
                c.Action = kind;
                if (kind == ImportKind.Skip) _session.Ticked.Remove(c); else _session.Ticked.Add(c);
                _session.Plan.Remove(c);
            }
            RefreshRows();
            if (refused.Count > 0) SetStatus($"{refused.Count} row(s) cannot be imported that way (no measured shape for it): {string.Join(", ", refused.Take(4))}", true);
            if (rows.Any(r => !_session.Plan.ContainsKey(r) && r.Action != ImportKind.Solid && r.Action != ImportKind.Skip)) RunPlan(false);
        }

        /// <summary>Smart parts need a measured shape of that kind; a solid or skip is always possible.</summary>
        private static bool CanBe(ImportCandidate c, ImportKind kind)
        {
            switch (kind)
            {
                case ImportKind.Pipe:
                    if (c.Detected == ImportKind.Pipe) return true;
                    // Your choice wins over the shape guess (a pressure pipe's bells made it a structure, and the choice reverted:
                    // user, 2026-11-17). Any object with a measurable axis can be a pipe; its ends and size come from that axis.
                    if (c.Fit == null && c.Source.Triangles.Length >= 18) c.Fit = NavisGeometryFit.FitLinear(c.Source.Triangles);
                    if (c.Fit == null || c.Fit.Length < 0.01) return false;
                    // A box or structure becomes a round pipe sized to its section; the measured box is kept on the row (Fit),
                    // so it can be switched back (review finding).
                    c.Start = c.Fit.Start; c.End = c.Fit.End;
                    c.Circular = true;
                    c.OuterWidth = c.OuterHeight = c.Fit.Shape == FitShape.Circular ? c.Fit.OuterDiameter : Math.Max(c.Fit.Width, c.Fit.Height);
                    if (c.InnerWidth >= c.OuterWidth) c.InnerWidth = c.InnerHeight = 0;
                    return true;
                case ImportKind.Box:
                    if (c.Fit == null || c.Detected != ImportKind.Box) return c.Fit != null && !c.Circular && c.Detected == ImportKind.Pipe;
                    // Back to the box it was measured as.
                    c.Start = c.Fit.Start; c.End = c.Fit.End;
                    c.Circular = false;
                    c.OuterWidth = c.Fit.Width; c.OuterHeight = c.Fit.Height;
                    c.InnerWidth = c.Fit.InnerWidth; c.InnerHeight = c.Fit.InnerHeight;
                    return true;
                case ImportKind.Structure:
                    if (c.Footprint == null && c.Source.Triangles.Length >= 9)
                    {
                        FootprintFit f = NavisGeometryFit.FitFootprint(c.Source.Triangles);
                        if (f == null) return false;
                        c.Footprint = f;
                        if (c.Origin != "Civil 3D") { c.X = f.CenterX; c.Y = f.CenterY; c.Rotation = f.Rotation; c.Rim = f.Top; c.Sump = f.Bottom; c.Bottom = f.Bottom; }
                        c.StructCircular = f.Shape == FitShape.Circular; c.StructDiameter = f.Diameter; c.StructWidth = f.Width; c.StructLength = f.Length;
                    }
                    if (c.Footprint != null && c.Rim == 0) { c.X = c.Footprint.CenterX; c.Y = c.Footprint.CenterY; c.Rotation = c.Footprint.Rotation; c.Rim = c.Footprint.Top; c.Sump = c.Footprint.Bottom; c.Bottom = c.Footprint.Bottom; }
                    return c.Footprint != null;
                default: return true;
            }
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != CConf && e.ColumnIndex != CResult)) return;
            if (!(_grid.Rows[e.RowIndex].Tag is ImportCandidate c)) return;
            string pill = e.ColumnIndex == CConf
                ? (c.Action == ImportKind.Skip ? "" : NeedsReview(c) ? (c.Confidence == ImportConfidence.Low || (_session.Plan.TryGetValue(c, out RowOutcome p) && p.Failed) ? "Low" : "Medium") : "High")
                : (e.FormattedValue as string ?? "");
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            if (pill.Length > 0)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.SetClip(e.CellBounds);
                string shown = e.ColumnIndex == CResult ? pill : pill;
                string kind = shown.StartsWith("Imported") ? "Imported" : shown.StartsWith("Failed") || shown.StartsWith("Not imported") ? "Failed" : shown;
                (Color fore, Color back) = StyleUi.PillColors(kind);
                SizeF size = e.Graphics.MeasureString(shown, StyleUi.Small);
                RectangleF r = new RectangleF(e.CellBounds.X + 6, e.CellBounds.Y + (e.CellBounds.Height - size.Height) / 2 - 1, Math.Min(size.Width + 10, e.CellBounds.Width - 10), size.Height + 2);
                using (System.Drawing.Drawing2D.GraphicsPath path = StyleUi.Rounded(r, r.Height / 2))
                using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillPath(b, path);
                using (SolidBrush t = new SolidBrush(fore)) e.Graphics.DrawString(shown, StyleUi.Small, t, new RectangleF(r.X + 5, r.Y + 1, r.Width - 6, r.Height), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                e.Graphics.ResetClip();
            }
            e.Handled = true;
        }

        private ImportCandidate CurrentRow() => _grid.CurrentRow?.Tag as ImportCandidate;

        private List<ImportCandidate> SelectedRows()
        {
            List<ImportCandidate> list = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as ImportCandidate).Where(c => c != null).ToList();
            if (list.Count == 0 && CurrentRow() != null) list.Add(CurrentRow());
            return list;
        }

        private List<ImportCandidate> TickedRows() => _session.Rows.Where(r => _session.Ticked.Contains(r) && r.Action != ImportKind.Skip).ToList();

        private static string ActionName(ImportKind k) => k == ImportKind.Pipe ? "Pipe" : k == ImportKind.Box ? "Box culvert" : k == ImportKind.Structure ? "Structure" : k == ImportKind.Solid ? "3D solid" : "Skip";

        private static ImportKind ActionFromName(string s) =>
            s == "Pipe" ? ImportKind.Pipe : s == "Box culvert" ? ImportKind.Box : s == "Structure" ? ImportKind.Structure : s == "3D solid" ? ImportKind.Solid : ImportKind.Skip;

        private void UpdateSummary()
        {
            List<ImportCandidate> t = TickedRows();
            _summary.Text = $"{t.Count} ticked: {t.Count(r => r.Action == ImportKind.Pipe)} pipes ({t.Where(r => r.Action == ImportKind.Pipe).Sum(r => r.Barrels.Count)} incl. barrels), " +
                            $"{t.Count(r => r.Action == ImportKind.Box)} boxes, {t.Count(r => r.Action == ImportKind.Structure)} structures, {t.Count(r => r.Action == ImportKind.Solid)} solids";
            if (_btnImport != null) _btnImport.Enabled = t.Count > 0;
        }

        // =====================================================================================
        // Details
        // =====================================================================================

        private IEnumerable<string> Pills(ImportCandidate c)
        {
            yield return c.Action == ImportKind.Pipe ? "Pipe" : c.Action == ImportKind.Box ? "Box" : c.Action == ImportKind.Structure ? "Structure" : c.Action == ImportKind.Solid ? "Solid" : "Skip";
            if (c.Action != ImportKind.Skip) yield return NeedsReview(c) ? "Medium" : "High";
            if (_session.Existing.Contains(c.Guid ?? "")) yield return "Already imported";
        }

        private void ShowDetails(ImportCandidate c)
        {
            _detail = c;
            _lvCompare.BeginUpdate();
            _lvCompare.Items.Clear();
            if (c == null)
            {
                _detName.Text = _session.Rows.Count == 0 ? "Nothing loaded yet" : "Select a row";
                _detSub.Text = _session.Rows.Count == 0 ? "Select pipes, culverts, duct banks or structures in Navisworks, then click \"Get selection from Navisworks\"." : "";
                _detNotes.Text = "";
                _lvCompare.EndUpdate();
                _lvProps.Items.Clear();
                _detPills.Invalidate();
                return;
            }
            _detName.Text = c.Name;
            _detSub.Text = $"{c.Origin} · {c.Source.ClassDisplayName} ({c.Source.ClassName}) · layer {c.Layer}" + (string.IsNullOrEmpty(c.Source.SourceFile) ? "" : $" · {c.Source.SourceFile}");
            _session.Plan.TryGetValue(c, out RowOutcome plan);
            string mm(double m) => m > 0 ? ImportCandidate.Mm(m) + " mm" : "";
            void Row(string what, string navis, string measured, string part) => _lvCompare.Items.Add(new ListViewItem(new[] { what, navis, measured, part }));
            if (c.Action == ImportKind.Pipe || c.Action == ImportKind.Box)
            {
                LinearFit f = c.Fit;
                Row("Outer width", mm(c.OuterWidth), f == null ? "" : mm(c.Circular ? f.OuterDiameter : f.Width), plan == null ? "" : plan.Size);
                if (!c.Circular) Row("Outer height", mm(c.OuterHeight), f == null ? "" : mm(f.Height), "");
                Row("Inner size", c.InnerWidth > 0 ? (c.Circular ? mm(c.InnerWidth) : $"{ImportCandidate.Mm(c.InnerWidth)} × {ImportCandidate.Mm(c.InnerHeight)}") : "unknown", "", plan == null ? "" : plan.Family);
                Row("Wall", c.InnerWidth > 0 || c.Wall > 0 ? ImportCandidate.Mm(c.Wall) + " mm" : "unknown", "", "");
                Row("Length", $"{c.Length:0.000} m", f == null ? "" : $"{f.Length:0.000} m", "");
                Row("Start", $"{c.Start.X:0.000}, {c.Start.Y:0.000}", $"z {c.Start.Z:0.000}", "");
                Row("End", $"{c.End.X:0.000}, {c.End.Y:0.000}", $"z {c.End.Z:0.000}", "");
                if (c.Barrels.Count > 1) Row("Barrels", c.Barrels.Count.ToString(), "", "");
                Row("Material", c.Material, "", plan?.Family ?? "");
            }
            else if (c.Action == ImportKind.Structure)
            {
                Row("Footprint", c.StructCircular ? "Ø " + mm(c.StructDiameter) : $"{ImportCandidate.Mm(c.StructWidth)} × {ImportCandidate.Mm(c.StructLength)} mm", c.Footprint == null ? "" : c.Footprint.Shape.ToString(), plan == null ? "" : plan.Size);
                Row("Position", $"{c.X:0.000}, {c.Y:0.000}", "", "");
                Row("Rotation", $"{c.Rotation * 180 / Math.PI:0.0}°", c.Footprint == null ? "" : $"{c.Footprint.Rotation * 180 / Math.PI:0.0}°", "");
                Row("Rim / sump", $"{c.Rim:0.000} / {c.Sump:0.000}", c.Footprint == null ? "" : $"top {c.Footprint.Top:0.000}", "");
                Row("Family", "", "", plan?.Family ?? "");
            }
            else Row("Geometry", $"{c.Source.Triangles.Length / 9} triangles", "", c.Action == ImportKind.Solid ? "Exact 3D solid" : "");
            _lvCompare.EndUpdate();
            Spread(_lvCompare, 0.24, 0.28, 0.20, 0.28);

            List<string> notes = new List<string>(c.Notes);
            if (plan != null && plan.Detail.Length > 0) notes.Add(plan.Detail);
            if (_session.Done.TryGetValue(c, out RowOutcome d) && d.Detail.Length > 0) notes.Add(d.Detail);
            _detNotes.Text = notes.Count == 0 ? "No issues: the properties and the measured model agree." : "• " + string.Join(Environment.NewLine + "• ", notes.Distinct());
            _detNotes.ForeColor = notes.Count == 0 ? Color.FromArgb(21, 128, 61) : StyleUi.Text;
            FillProps();
            _detPills.Invalidate();
        }

        private void FillProps()
        {
            _lvProps.BeginUpdate();
            _lvProps.Items.Clear();
            if (_detail != null)
            {
                string q = _txtPropFilter.Text.Trim();
                foreach (NavisProp p in _detail.Source.Props)
                {
                    if (q.Length > 0 && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && (p.Value ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _lvProps.Items.Add(new ListViewItem(new[] { p.Category, p.Name, p.Value }));
                }
            }
            _lvProps.EndUpdate();
        }

        private void CopyProps()
        {
            if (_detail == null) return;
            StringBuilder sb = new StringBuilder("Tab\tProperty\tValue\r\n");
            foreach (NavisProp p in _detail.Source.Props) sb.Append($"{p.Category}\t{p.Name}\t{p.Value}\r\n");
            Clipboard.SetText(sb.ToString());
            SetStatus("Properties copied.");
        }

        // =====================================================================================
        // Matching, preview and import
        // =====================================================================================

        private void InvalidatePlan()
        {
            if (_session.Rows.Count == 0) return;
            _session.Plan.Clear();
            RefreshRows();
        }

        /// <summary>Matches parts in a transaction that is thrown away, so the grid can show the exact part for every row.</summary>
        private ImportReport RunPlan(bool announce)
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            Bind(doc);
            List<ImportCandidate> rows = _session.Rows.Where(r => r.Action != ImportKind.Skip).ToList();
            if (doc == null || rows.Count == 0) return null;
            Cursor = Cursors.WaitCursor;
            SetStatus("Matching Civil 3D parts…");
            ImportReport report;
            try { report = NavisImporter.Plan(doc, rows, Options()); }
            finally { Cursor = Cursors.Default; }
            if (report.Error.Length > 0) { SetStatus("Part matching failed: " + report.Error, true); return null; }
            foreach (KeyValuePair<ImportCandidate, RowOutcome> kv in report.Rows) _session.Plan[kv.Key] = kv.Value;
            RefreshRows();
            int review = rows.Count(NeedsReview);
            string catalog = report.CatalogAdditions.Count > 0 ? $" {report.CatalogAdditions.Count} size(s) will be added to the catalog on import." : "";
            string notes = report.CatalogNotes.Count > 0 ? " " + string.Join(" ", report.CatalogNotes) : "";
            SetStatus($"Matched against {report.CatalogPipeFamilies} pipe and {report.CatalogStructureFamilies} structure families in the catalog. " +
                      (review > 0 ? $"{review} row(s) need review." : "All rows match.") + catalog + notes + (announce ? "" : " Nothing has been changed yet."), review > 0 || notes.Length > 0);
            return report;
        }

        private string SizeModeText() =>
            "Missing sizes: " + (_session.Settings.SizeMode == (int)CatalogMode.AnySize ? "Method A" : _session.Settings.SizeMode == (int)CatalogMode.Extend ? "Method B" : "nearest size");

        /// <summary>Asks how sizes the catalog lacks are made, then sets that route up.</summary>
        private void ChooseSizeMode()
        {
            using (FormNavisSizeMode f = new FormNavisSizeMode((CatalogMode)_session.Settings.SizeMode, _session.Settings.AskSizeMode))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _session.Settings.SizeMode = (int)f.Mode;
                _session.Settings.AskSizeMode = f.AskAgain;
            }
            _session.Settings.Save();
            _btnSizeMode.Text = SizeModeText();
            if (_session.Settings.SizeMode == (int)CatalogMode.AnySize) SetUpAnySize(true);
            if (_session.Plan.Count > 0) RunPlan(false);
        }

        /// <summary>Method A: makes sure the any-size families are in the catalogs; the first time, Civil 3D must restart to list them.</summary>
        private void SetUpAnySize(bool announce)
        {
            List<string> log = new List<string>();
            bool restart;
            try { restart = NavisImporter.EnsureAnySizeFamilies(log); }
            catch (Exception ex) { SetStatus("Could not set up the any-size families: " + ex.Message, true); return; }
            List<string> added = log.Where(l => l.StartsWith("Added")).ToList();
            List<string> problems = log.Where(l => !l.StartsWith("Added")).ToList();
            if (restart && (announce || added.Count > 0))
            {
                MessageBox.Show(this,
                    (added.Count > 0 ? string.Join("\n", added) + "\n\n" : "The any-size families are in the catalog.\n\n") +
                    "Civil 3D lists new catalog families only after it starts again: close and reopen Civil 3D once, then every imported size is exact.\n\n" +
                    "Until then this import uses the nearest catalog sizes (or exact solids when they are too far off)." +
                    (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : ""),
                    "Navisworks Import - Method A", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (problems.Count > 0) SetStatus(string.Join("  ", problems), true);
            else if (announce) SetStatus("Method A: the any-size families are loaded; every imported size will be exact.");
        }

        /// <summary>Opens the part picker for the selected rows (all of one kind) and stores the pick as an override.</summary>
        private void ChoosePart()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            Bind(doc);
            List<ImportCandidate> rows = SelectedRows().Where(r => r.Action == ImportKind.Pipe || r.Action == ImportKind.Box || r.Action == ImportKind.Structure).ToList();
            if (doc == null || rows.Count == 0) { SetStatus("Select pipe, box culvert or structure rows to choose their part.", true); return; }
            ImportKind kind = rows[0].Action;
            if (rows.Any(r => r.Action != kind)) { SetStatus("Select rows of one kind (all pipes, all box culverts or all structures) to choose their part together.", true); return; }

            (ObjectId, ObjectId)? current = _session.Overrides.TryGetValue(rows[0], out (ObjectId, ObjectId) o) ? o : ((ObjectId, ObjectId)?)null;
            using (FormNavisPartPicker picker = new FormNavisPartPicker(doc, rows, _txtPartsList.Text.Trim(), current))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (picker.ClearOverride) { ClearOverrides(rows); return; }
                foreach (ImportCandidate r in rows) { _session.Overrides[r] = (picker.FamilyId, picker.SizeId); _session.Plan.Remove(r); _session.Ticked.Add(r); }
            }
            RunPlan(false);
        }

        private void ClearOverrides(List<ImportCandidate> rows)
        {
            bool any = false;
            foreach (ImportCandidate r in rows) any |= _session.Overrides.Remove(r);
            if (any) RunPlan(false);
        }

        private void PreviewInDrawing(List<ImportCandidate> rows)
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null || rows.Count == 0) return;
            double s = NavisImporter.DrawingScale(doc.Database);
            _markers.Clear();
            Extents3d? ext = null;
            void Grow(Point3d p) { if (ext.HasValue) { Extents3d x = ext.Value; x.AddPoint(p); ext = x; } else ext = new Extents3d(p, p); }
            Point3d P(V3 v) => new Point3d(v.X * s, v.Y * s, v.Z * s);
            foreach (ImportCandidate c in rows)
            {
                if (c.IsLinear)
                {
                    foreach ((double u, double w) in c.Barrels)
                    {
                        V3 off = c.BarrelAxis * u + c.BarrelUp * w;
                        Point3d a = P(c.Start + off), b = P(c.End + off);
                        _markers.AddPath(new[] { a, b }, (short)(c.Action == ImportKind.Pipe ? 3 : 2), Math.Max(0.05, c.OuterWidth * s * 0.25));
                        Grow(a); Grow(b);
                    }
                }
                else if (c.Action == ImportKind.Structure)
                {
                    Point3d center = new Point3d(c.X * s, c.Y * s, c.Rim * s);
                    if (c.StructCircular) _markers.AddRing(center, c.StructDiameter / 2 * s, 4, 0.05);
                    else
                    {
                        double ang = c.Footprint?.Rotation ?? c.Rotation, hl = (c.Footprint?.Length ?? c.StructLength) / 2 * s, hw = (c.Footprint?.Width ?? c.StructWidth) / 2 * s;
                        Vector3d along = new Vector3d(Math.Cos(ang), Math.Sin(ang), 0), across = new Vector3d(-Math.Sin(ang), Math.Cos(ang), 0);
                        Point3d[] box = { center + along * hl + across * hw, center - along * hl + across * hw, center - along * hl - across * hw, center + along * hl - across * hw, center + along * hl + across * hw };
                        _markers.AddPath(box, 4, 0.05);
                    }
                    Grow(center);
                }
                else if (c.Source.Box.Length == 6)
                {
                    double[] b = c.Source.Box;
                    Point3d[] outline = { new Point3d(b[0] * s, b[1] * s, b[5] * s), new Point3d(b[3] * s, b[1] * s, b[5] * s), new Point3d(b[3] * s, b[4] * s, b[5] * s), new Point3d(b[0] * s, b[4] * s, b[5] * s), new Point3d(b[0] * s, b[1] * s, b[5] * s) };
                    _markers.AddPath(outline, 6, 0.05);
                    Grow(outline[0]); Grow(outline[2]);
                }
            }
            if (ext.HasValue)
            {
                using (doc.LockDocument()) ViewHelper.ZoomTo(ext.Value, 1.3, 10.0);
                SetStatus($"Previewing {rows.Count} row(s) in the drawing: green pipes, yellow boxes, cyan structures, magenta solids. Nothing has been created.");
            }
        }

        private void ImportTicked()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            List<ImportCandidate> rows = TickedRows();
            if (doc == null || rows.Count == 0) return;
            NavisImportOptions o = Options();
            ImportReport plan = RunPlan(true);
            if (plan == null) return;

            // One confirmation listing exactly what will be created and added.
            List<PreviewRow> preview = new List<PreviewRow>();
            if (plan.CreatesPartsList) preview.Add(new PreviewRow { Cells = new[] { "Parts list", o.PartsListName, "New parts list", "" }, Enabled = false });
            foreach (string f in plan.AddedFamilies) preview.Add(new PreviewRow { Cells = new[] { "Family", f, "Added to " + o.PartsListName, "" }, Enabled = false });
            foreach (string sz in plan.AddedSizes) preview.Add(new PreviewRow { Cells = new[] { "Size", sz, "Added to " + o.PartsListName, "" }, Enabled = false });
            foreach (string n in plan.Networks) preview.Add(new PreviewRow { Cells = new[] { "Network", n, "Created or reused", "" }, Enabled = false });
            foreach (string a in plan.CatalogAdditions) preview.Add(new PreviewRow { Cells = new[] { "Catalog", a, "Added to the catalog family (backup kept)", "Method B" }, Enabled = false });
            foreach (ImportCandidate c in rows)
            {
                RowOutcome p = plan.Rows[c];
                bool exists = _session.Existing.Contains(c.Guid ?? "");
                string what = c.Action == ImportKind.Solid ? "3D solid" : p.AsSolid ? $"3D solid (no size within {o.MaxPartError * 1000:0} mm)" : (p.Overridden ? "✎ " : "") + $"{p.Family} · {p.Size}";
                string note = p.Failed ? "No part: " + p.Detail : (exists ? (o.Existing == ExistingMode.Skip ? "Already imported: skipped" : o.Existing == ExistingMode.Replace ? "Replaces the earlier import" : "Imported again") : "")
                              + (c.Barrels.Count > 1 ? $" {c.Barrels.Count} barrels" : "") + (p.PartDeviation > NavisInterpreter.HighTolerance && !p.Failed ? $" {p.PartDeviation * 1000:0} mm off" : "");
                preview.Add(new PreviewRow { Cells = new[] { ActionName(c.Action), c.Name, what, note.Trim() }, Enabled = !p.Failed, Checked = !p.Failed, Tag = c });
            }
            List<ImportCandidate> chosen;
            using (FormStylePreview f = new FormStylePreview("Import from Navisworks",
                       $"{rows.Count} row(s). The parts list, families, sizes and networks above are added as needed. Untick rows to leave them out; UNDO reverses the whole import.",
                       new[] { "What", "Name", "Civil 3D", "Note" }, preview, "Import ticked", windowTitle: "Navisworks Import - Confirm import"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                chosen = f.Chosen.OfType<ImportCandidate>().ToList();
            }
            if (chosen.Count == 0) return;

            Cursor = Cursors.WaitCursor;
            SetStatus($"Importing {chosen.Count} row(s)…");
            ImportReport report;
            try { report = NavisImporter.Import(doc, chosen, o); }
            finally { Cursor = Cursors.Default; }
            if (report.Error.Length > 0) { SetStatus("Import failed and was rolled back: " + report.Error, true); return; }

            _session.Record(report, doc);
            _markers.Clear();
            RefreshRows();
            if (report.CatalogNotes.Count > 0)
                MessageBox.Show(this, string.Join("\n", report.CatalogNotes), "Navisworks Import - catalog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            int ok = report.Rows.Values.Count(r => !r.Failed && r.Created.Count > 0), failed = report.Rows.Values.Count(r => r.Failed);
            int objects = report.Rows.Values.Sum(r => r.Created.Count);
            SetStatus($"Imported {ok} row(s) as {objects} object(s)" + (failed > 0 ? $"; {failed} failed (see the Result column)." : ".") + " Use UNDO to reverse.", failed > 0);
            ZoomToCreated(doc, report.Rows.Values.SelectMany(r => r.Created).ToList());
        }

        private void ZoomToCreated(Document doc, List<ObjectId> ids)
        {
            Extents3d? ext = null;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                        Extents3d e = ent.GeometricExtents;
                        if (ext.HasValue) { Extents3d x = ext.Value; x.AddExtents(e); ext = x; } else ext = e;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                tr.Commit();
                if (ext.HasValue) ViewHelper.ZoomTo(ext.Value, 1.2, 10.0);
            }
        }

        // =====================================================================================
        // Misc
        // =====================================================================================

        private void FillSurfaces()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            string wanted = (_cbSurface.SelectedItem as Choice)?.Text ?? _session.Settings.SurfaceName;
            _cbSurface.Items.Clear();
            _cbSurface.Items.Add(new Choice("(none)", ObjectId.Null));
            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in CivilDocument.GetCivilDocument(doc.Database).GetSurfaceIds())
                        if (tr.GetObject(id, OpenMode.ForRead) is Surface sf) _cbSurface.Items.Add(new Choice(sf.Name, id));
                    tr.Commit();
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            int index = 0;
            for (int i = 0; i < _cbSurface.Items.Count; i++) if (((Choice)_cbSurface.Items[i]).Text == wanted) index = i;
            _cbSurface.SelectedIndex = index;
        }

        private void SetStatus(string text, bool warn = false)
        {
            _status.Text = text;
            _status.ForeColor = warn ? StyleUi.Danger : StyleUi.Text;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { _txtSearch.Focus(); _txtSearch.SelectAll(); return true; }
            if (keyData == Keys.F5) { _ = GetSelection(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "How Navisworks Import works\n\n" +
                "1. In Navisworks, select pipes, culverts, duct banks or structures (any selection; layers and groups are opened up).\n" +
                "2. Here, click \"Get selection from Navisworks\" (F5). Each item is measured from its real geometry; items exported from " +
                "Civil 3D also bring their exact properties, which are checked against the geometry.\n" +
                "3. Every row is matched to the Civil 3D catalog. Only the families and sizes used are added, to the \"" + _txtPartsList.Text + "\" parts list. " +
                "The Error column shows how far the chosen size is from the Navisworks one.\n" +
                "4. Change \"Import as\" for any row (pipe, box culvert, structure, exact 3D solid, or skip), preview it in the drawing, " +
                "then Import ticked. A confirmation lists everything that will be added; UNDO reverses the import.\n\n" +
                "Twin and multi-barrel culverts become one pipe per barrel. Headwalls come in as exact solids. " +
                "Imported objects remember their Navisworks item: importing again replaces them, and \"Show in Navisworks\" finds the source.",
                "Navisworks Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class Choice
        {
            public readonly string Text;
            public readonly object Value;
            public Choice(string text, object value) { Text = text; Value = value; }
            public override string ToString() => Text;
        }
    }
}
