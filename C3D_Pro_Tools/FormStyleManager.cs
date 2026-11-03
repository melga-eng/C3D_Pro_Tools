using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Label = System.Windows.Forms.Label;
using Timer = System.Windows.Forms.Timer;

namespace C3DProTools
{
    /// <summary>
    /// Style Manager: every style in the drawing with its real usage, in a modeless, resizable window.
    /// Left: categories. Centre: styles. Right: what uses the selected style. Bottom: clean-up, rename, standards,
    /// transfer, batch and report actions. Every change is previewed first and the drawing is rescanned afterwards.
    /// </summary>
    internal sealed class FormStyleManager : Form
    {
        private static FormStyleManager _instance;

        /// <summary>Opens the window (or brings it forward) for the active drawing.</summary>
        public static void ShowTool()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new FormStyleManager();
                AcadUiApp.ShowModelessDialog(null, _instance, false);
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
            if (doc != null && doc != _instance._doc) _instance.BindTo(doc);
        }

        private const int ColCheck = 0, ColName = 1, ColNew = 2, ColStatus = 3, ColUses = 4, ColCategory = 5, ColDescription = 6, ColModified = 7;

        private readonly StyleManagerSettings _settings = StyleManagerSettings.Load();
        private Document _doc;
        private StyleSession _session;
        private StyleTemplate _template;
        private readonly HashSet<ObjectId> _checked = new HashSet<ObjectId>();
        private List<StyleItem> _view = new List<StyleItem>();
        private string _category = "";
        private string _chip = "All";
        private int _sortColumn = ColName;
        private bool _sortAscending = true;
        private RenameRule _rule;
        private Dictionary<ObjectId, PlannedRename> _renamePreview = new Dictionary<ObjectId, PlannedRename>();
        private bool _busy;

        // Header and filters
        private readonly Label _lblDrawing = new Label { AutoSize = true, ForeColor = StyleUi.HeaderMuted, Margin = new Padding(2, 0, 3, 0) };
        private readonly Dictionary<string, StyleChip> _chips = new Dictionary<string, StyleChip>();
        private readonly TextBox _txtSearch = new TextBox { PlaceholderText = "Search names, descriptions, categories…  (Ctrl+F)", Width = 340, Anchor = AnchorStyles.Right, Margin = new Padding(3, 9, 3, 3) };
        private readonly Timer _searchTimer = new Timer { Interval = 220 };
        private readonly Timer _detailsTimer = new Timer { Interval = 120 };

        // Body
        private readonly SplitContainer _splitDeck = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6, BackColor = StyleUi.Border };
        private readonly SplitContainer _splitMain = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6, BackColor = StyleUi.Border };
        private readonly SplitContainer _splitInner = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6, BackColor = StyleUi.Border };
        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, FullRowSelect = true, ShowLines = false, ItemHeight = 24, Indent = 16 };
        private readonly DataGridView _grid = new DataGridView();
        private readonly ContextMenuStrip _gridMenu = new ContextMenuStrip();

        // Details
        private readonly Label _detName = new Label { Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading, Padding = new Padding(0, 0, 0, 2) };
        private readonly Label _detCategory = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted };
        private readonly Panel _detPills = new Panel { Dock = DockStyle.Top, Height = 30 };
        private readonly Label _detExplain = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 2, 0, 8) };
        private readonly Label _detMeta = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 0, 0, 8) };
        private readonly ListView _lvUsers = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly Label _lblUsers = new Label { Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Bold, Padding = new Padding(0, 4, 0, 4), Text = "Used by" };
        private StyleItem _detailItem;

        // Deck
        private readonly TabControl _deck = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        private StyleCard _cardPurge, _cardDuplicates, _cardChecked, _cardRename, _cardTemplate;
        private readonly ComboBox _cbPurgeScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly CheckBox _chkRepeat = new CheckBox { Text = "Repeat until nothing more frees up", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        private readonly ComboBox _cbMergeTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        private readonly TextBox _txtFind = StyleUi.Box(), _txtReplace = StyleUi.Box(), _txtAddPrefix = StyleUi.Box("e.g. ABC-"), _txtRemovePrefix = StyleUi.Box();
        private readonly TextBox _txtAddSuffix = StyleUi.Box(), _txtRemoveSuffix = StyleUi.Box();
        private readonly CheckBox _chkCase = new CheckBox { Text = "Match case", AutoSize = true }, _chkRegex = new CheckBox { Text = "Regular expression", AutoSize = true };
        private readonly ComboBox _cbCase = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly RadioButton _rbName = new RadioButton { Text = "Names", AutoSize = true, Checked = true }, _rbDescription = new RadioButton { Text = "Descriptions", AutoSize = true };
        private readonly Label _lblRenameSummary = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 6, 3, 6) };
        private readonly TextBox _txtTemplate = StyleUi.Box("Company template (.dwt or .dwg)");
        private readonly Label _lblTemplate = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 6, 3, 3) };
        private readonly Button _btnImportMissing = StyleUi.Secondary("Import missing styles…"), _btnUpdateMatching = StyleUi.Secondary("Update matching styles from template…"), _btnMap = StyleUi.Primary("Map non-standard styles…");
        private readonly ComboBox _cbPushTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        private readonly ComboBox _cbPullOpen = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        private readonly TextBox _txtPull = StyleUi.Box("Drawing or template to pull styles from");
        private readonly ComboBox _cbConflict = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };

        // Status
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripProgressBar _progress = new ToolStripProgressBar { Visible = false, Width = 180 };
        private readonly ToolStripStatusLabel _scanInfo = new ToolStripStatusLabel { ForeColor = StyleUi.Muted };
        private readonly ToolStripStatusLabel _notes = new ToolStripStatusLabel { IsLink = true, Visible = false };

        private FormStyleManager()
        {
            Text = "C3D Pro Tools - Style Manager";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1000, 640);
            KeyPreview = true;
            ShowInTaskbar = false;
            _rule = _settings.Rule ?? new RenameRule();

            BuildGrid();
            BuildDetails();
            BuildDeck();

            _splitInner.Panel1.Controls.Add(_grid);
            _splitInner.Panel2.Controls.Add(BuildDetailsHost());
            Panel treeHost = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Panel, Padding = new Padding(6, 8, 0, 6) };
            treeHost.Controls.Add(_tree);
            _tree.AfterSelect += (s, e) => { _category = e.Node?.Tag as string ?? ""; ApplyView(); };
            _splitMain.Panel1.Controls.Add(treeHost);
            _splitMain.Panel2.Controls.Add(_splitInner);
            _splitDeck.Panel1.Controls.Add(_splitMain);
            Panel deckHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 6), BackColor = StyleUi.Surface };
            deckHost.Controls.Add(_deck);
            _splitDeck.Panel2.Controls.Add(deckHost);

            StatusStrip strip = new StatusStrip { BackColor = StyleUi.Panel, SizingGrip = true };
            strip.Items.AddRange(new ToolStripItem[] { _status, _notes, _progress, _scanInfo });
            _notes.Click += (s, e) => ShowNotes();

            Controls.Add(_splitDeck);
            Controls.Add(BuildChipBar());
            Controls.Add(BuildHeader());
            Controls.Add(strip);

            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplyView(); };
            _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _detailsTimer.Tick += (s, e) => { _detailsTimer.Stop(); ShowDetails(CurrentItem()); };

            RestoreLayout();
            AcadCoreApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcadCoreApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            FormClosing += (s, e) => SaveLayout();
            FormClosed += (s, e) =>
            {
                AcadCoreApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                AcadCoreApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                _template?.Dispose();
                _instance = null;
            };
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Header, Padding = new Padding(16, 8, 12, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel titles = new FlowLayoutPanel { FlowDirection = System.Windows.Forms.FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            titles.Controls.Add(new Label { Text = "Style Manager", Font = StyleUi.Title, ForeColor = StyleUi.HeaderText, AutoSize = true, Margin = new Padding(0) });
            _lblDrawing.Text = "No drawing";
            titles.Controls.Add(_lblDrawing);

            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            actions.Controls.Add(StyleUi.HeaderButton("⟳  Rescan  (F5)", (s, e) => Reload()));
            actions.Controls.Add(StyleUi.HeaderButton("Batch…", (s, e) => OpenBatch()));
            actions.Controls.Add(StyleUi.HeaderButton("How it works", (s, e) => ShowHelp()));

            header.Controls.Add(titles, 0, 0);
            header.Controls.Add(actions, 1, 0);
            return header;
        }

        private Control BuildChipBar()
        {
            TableLayoutPanel bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Surface, Padding = new Padding(10, 2, 12, 2) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel chips = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            foreach ((string text, string kind) in new[]
            {
                ("All", "All"), ("Used", "Used"), ("Unused", "Unused"), ("Hidden refs", "Hidden ref"), ("Protected", "Protected"),
                ("Duplicates", "Duplicate"), ("Non-standard", "Non-standard"), ("Missing", "Missing"), ("Checked", "Checked")
            })
            {
                StyleChip chip = new StyleChip(text, kind);
                chip.Click += (s, e) => SelectChip(kind);
                _chips[kind] = chip;
                chips.Controls.Add(chip);
            }
            _chips["All"].Selected = true;
            _chips["Non-standard"].Visible = _chips["Missing"].Visible = false;
            bar.Controls.Add(chips, 0, 0);
            bar.Controls.Add(_txtSearch, 1, 0);
            return bar;
        }

        private void BuildGrid()
        {
            DataGridView g = _grid;
            g.Dock = DockStyle.Fill;
            g.VirtualMode = true;
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
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = StyleUi.Surface, ForeColor = StyleUi.Muted, Font = StyleUi.Bold, SelectionBackColor = StyleUi.Surface,
                SelectionForeColor = StyleUi.Muted, Padding = new Padding(6, 0, 0, 0), WrapMode = DataGridViewTriState.False
            };
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = StyleUi.Panel, ForeColor = StyleUi.Text, SelectionBackColor = StyleUi.AccentSoft, SelectionForeColor = StyleUi.Text,
                Padding = new Padding(6, 0, 4, 0), WrapMode = DataGridViewTriState.False, Font = StyleUi.Base
            };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(250, 251, 253) };
            g.RowTemplate.Height = 28;
            g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 34, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Resizable = DataGridViewTriState.False });
            AddColumn("Style name", 30, false);
            AddColumn("New name (preview)", 24, true).DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Accent, Font = StyleUi.Bold, SelectionForeColor = StyleUi.Accent };
            AddFixed("Status", 210);
            AddFixed("Uses", 64).DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
            AddColumn("Category", 30, true).DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Muted };
            AddColumn("Description", 22, true).DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Muted };
            AddFixed("Modified", 118).DefaultCellStyle = new DataGridViewCellStyle { ForeColor = StyleUi.Muted };
            g.Columns[ColNew].Visible = false;
            foreach (DataGridViewColumn c in g.Columns) c.SortMode = c.Index == ColCheck ? DataGridViewColumnSortMode.NotSortable : DataGridViewColumnSortMode.Programmatic;

            g.CellValueNeeded += Grid_CellValueNeeded;
            g.CellValuePushed += Grid_CellValuePushed;
            g.CellPainting += Grid_CellPainting;
            g.CellFormatting += Grid_CellFormatting;
            g.CellBeginEdit += (s, e) => { StyleItem i = ItemAt(e.RowIndex); if (i == null || IsMissing(i) || (e.ColumnIndex == ColName && _busy)) e.Cancel = true; };
            g.CurrentCellDirtyStateChanged += (s, e) => { if (g.CurrentCell?.ColumnIndex == ColCheck) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            g.ColumnHeaderMouseClick += Grid_HeaderClick;
            g.SelectionChanged += (s, e) => { _detailsTimer.Stop(); _detailsTimer.Start(); };
            g.KeyDown += Grid_KeyDown;
            g.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                if (!g.Rows[e.RowIndex].Selected) { g.ClearSelection(); g.Rows[e.RowIndex].Selected = true; g.CurrentCell = g.Rows[e.RowIndex].Cells[ColName]; }
            };
            g.DataError += (s, e) => e.ThrowException = false;

            _gridMenu.Items.Add("Tick selected rows", null, (s, e) => SetChecked(SelectedItems(), true));
            _gridMenu.Items.Add("Untick selected rows", null, (s, e) => SetChecked(SelectedItems(), false));
            _gridMenu.Items.Add("Tick all visible rows", null, (s, e) => SetChecked(_view, true));
            _gridMenu.Items.Add("Untick everything", null, (s, e) => { _checked.Clear(); AfterCheckChange(); });
            _gridMenu.Items.Add(new ToolStripSeparator());
            _gridMenu.Items.Add("Rename  (F2)", null, (s, e) => { if (g.CurrentRow != null) { g.CurrentCell = g.CurrentRow.Cells[ColName]; g.BeginEdit(true); } });
            _gridMenu.Items.Add("Copy name", null, (s, e) => CopyNames());
            _gridMenu.Items.Add("Zoom to objects using it", null, (s, e) => ZoomToUsers(CurrentItem()));
            _gridMenu.Items.Add("Select objects using it", null, (s, e) => SelectUsers(SelectedItems()));
            _gridMenu.Items.Add(new ToolStripSeparator());
            _gridMenu.Items.Add("Merge ticked styles into this one…", null, (s, e) => MergeCheckedInto(CurrentItem()));
            _gridMenu.Items.Add("Show this category", null, (s, e) => { StyleItem i = CurrentItem(); if (i != null) SelectCategory(i.TreePath); });
            g.ContextMenuStrip = _gridMenu;
        }

        private DataGridViewColumn AddColumn(string header, float weight, bool readOnly)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, ReadOnly = readOnly, MinimumWidth = 80 };
            _grid.Columns.Add(c);
            return c;
        }

        private DataGridViewColumn AddFixed(string header, int width)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
            _grid.Columns.Add(c);
            return c;
        }

        private void BuildDetails()
        {
            _lvUsers.Columns.Add("Kind", 110);
            _lvUsers.Columns.Add("Name / layer", 130);
            _lvUsers.Columns.Add("Where", 110);
            _lvUsers.DoubleClick += (s, e) =>
            {
                if (_lvUsers.SelectedItems.Count > 0 && _lvUsers.SelectedItems[0].Tag is ObjectId id) ZoomToIds(new[] { id });
            };
            _detPills.Paint += (s, e) =>
            {
                if (_detailItem == null) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float x = 0;
                foreach (string pill in Pills(_detailItem)) x += StyleUi.DrawPill(e.Graphics, pill, x, 14, StyleUi.Bold) + 6;
            };
        }

        private Control BuildDetailsHost()
        {
            Panel host = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Panel, Padding = new Padding(16, 12, 14, 10) };
            FlowLayoutPanel buttons = StyleUi.Row(
                StyleUi.Secondary("Zoom to", (s, e) => ZoomToUsers(_detailItem)),
                StyleUi.Secondary("Select", (s, e) => SelectUsers(_detailItem == null ? new List<StyleItem>() : new List<StyleItem> { _detailItem })),
                StyleUi.Secondary("Copy name", (s, e) => { if (_detailItem != null) Clipboard.SetText(_detailItem.Name); }));
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(0, 8, 0, 0);

            host.Controls.Add(_lvUsers);
            host.Controls.Add(_lblUsers);
            host.Controls.Add(_detMeta);
            host.Controls.Add(_detExplain);
            host.Controls.Add(_detPills);
            host.Controls.Add(_detCategory);
            host.Controls.Add(_detName);
            host.Controls.Add(buttons);
            host.Resize += (s, e) =>
            {
                Size max = new Size(Math.Max(120, host.ClientSize.Width - host.Padding.Horizontal), 0);
                _detName.MaximumSize = _detCategory.MaximumSize = _detExplain.MaximumSize = _detMeta.MaximumSize = max;
            };
            ShowDetails(null);
            return host;
        }

        private void BuildDeck()
        {
            _deck.TabPages.Add(DeckPage("Clean up", BuildCleanupTab()));
            _deck.TabPages.Add(DeckPage("Rename", BuildRenameTab()));
            _deck.TabPages.Add(DeckPage("Standards", BuildStandardsTab()));
            _deck.TabPages.Add(DeckPage("Transfer", BuildTransferTab()));
            _deck.TabPages.Add(DeckPage("Batch", BuildBatchTab()));
            _deck.TabPages.Add(DeckPage("Report", BuildReportTab()));
            _deck.SelectedIndexChanged += (s, e) => UpdateRenamePreview();
        }

        private static TabPage DeckPage(string title, Control content)
        {
            TabPage page = new TabPage(title) { BackColor = StyleUi.Surface, Padding = new Padding(2) };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        private static TableLayoutPanel Columns(params (Control control, float percent)[] cards)
        {
            TableLayoutPanel t = new TableLayoutPanel { ColumnCount = cards.Length, RowCount = 1, Dock = DockStyle.Fill };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < cards.Length; i++)
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, cards[i].percent));
                t.Controls.Add(cards[i].control, i, 0);
            }
            return t;
        }

        private Control BuildCleanupTab()
        {
            _cbPurgeScope.Items.AddRange(new object[] { "All unused styles in the drawing", "Unused styles in the selected category", "Ticked styles only" });
            _cbPurgeScope.SelectedIndex = Math.Max(0, Math.Min(2, _settings.PurgeScope));
            _chkRepeat.Checked = _settings.RepeatPurge;
            _cardPurge = new StyleCard("Purge unused styles", "Rescan to see how many styles nothing uses.");
            _cardPurge.Body.Controls.Add(_cbPurgeScope);
            _cardPurge.Body.Controls.Add(_chkRepeat);
            _cardPurge.Body.Controls.Add(StyleUi.Primary("Review and purge…", (s, e) => PurgeUnused()));

            _cardDuplicates = new StyleCard("Fix duplicates", "Copies such as \"Name (1)\", \"Copy of Name\" or \"Name_2\" are merged into the original.");
            _cardDuplicates.Body.Controls.Add(StyleUi.Primary("Review and merge duplicates…", (s, e) => MergeDuplicates()));
            _cardDuplicates.Body.Controls.Add(StyleUi.Secondary("Show duplicates", (s, e) => SelectChip("Duplicate")));

            _cardChecked = new StyleCard("Ticked styles", "Tick styles in the list to merge or delete them.");
            _cardChecked.Body.Controls.Add(StyleUi.Row(StyleUi.Caption("Merge into:"), _cbMergeTarget));
            _cardChecked.Body.Controls.Add(StyleUi.Row(
                StyleUi.Primary("Merge ticked…", (s, e) => MergeChecked()),
                StyleUi.DangerButton("Delete ticked…", (s, e) => DeleteChecked()),
                StyleUi.Secondary("Untick all", (s, e) => { _checked.Clear(); AfterCheckChange(); })));

            return Columns((_cardPurge, 34), (_cardDuplicates, 30), (_cardChecked, 36));
        }

        private Control BuildRenameTab()
        {
            _cbCase.Items.AddRange(new object[] { "Keep as is", "UPPER CASE", "lower case", "Title Case" });
            TableLayoutPanel rule = new TableLayoutPanel { ColumnCount = 6, AutoSize = true };
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rule.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            FlowLayoutPanel options = StyleUi.Row(_chkCase, _chkRegex);
            FlowLayoutPanel target = StyleUi.Row(_rbName, _rbDescription);
            AddRuleRow(rule, 0, "Find", _txtFind, "Replace with", _txtReplace, "Options", options);
            AddRuleRow(rule, 1, "Add prefix", _txtAddPrefix, "Remove prefix", _txtRemovePrefix, "Case", _cbCase);
            AddRuleRow(rule, 2, "Add suffix", _txtAddSuffix, "Remove suffix", _txtRemoveSuffix, "Change", target);

            _txtFind.Text = _rule.Find; _txtReplace.Text = _rule.Replace;
            _txtAddPrefix.Text = _rule.AddPrefix; _txtRemovePrefix.Text = _rule.RemovePrefix;
            _txtAddSuffix.Text = _rule.AddSuffix; _txtRemoveSuffix.Text = _rule.RemoveSuffix;
            _chkCase.Checked = _rule.MatchCase; _chkRegex.Checked = _rule.UseRegex;
            _cbCase.SelectedIndex = Math.Max(0, Math.Min(3, _rule.CaseMode));
            _rbDescription.Checked = _rule.TargetDescription;
            foreach (TextBox t in new[] { _txtFind, _txtReplace, _txtAddPrefix, _txtRemovePrefix, _txtAddSuffix, _txtRemoveSuffix }) t.TextChanged += (s, e) => RuleChanged();
            _chkCase.CheckedChanged += (s, e) => RuleChanged();
            _chkRegex.CheckedChanged += (s, e) => RuleChanged();
            _cbCase.SelectedIndexChanged += (s, e) => RuleChanged();
            _rbName.CheckedChanged += (s, e) => RuleChanged();

            _cardRename = new StyleCard("Rename rule", "Applies to ticked styles. Steps run in order: find/replace, remove prefix/suffix, add prefix/suffix (skipped when already there), change case.", rule);
            StyleCard apply = new StyleCard("Preview and apply", "The new names appear in the \"New name\" column for ticked styles while this tab is open.");
            apply.Body.Controls.Add(_lblRenameSummary);
            apply.Body.Controls.Add(StyleUi.Row(
                StyleUi.Primary("Apply to ticked…", (s, e) => ApplyRename()),
                StyleUi.Secondary("Clear rule", (s, e) => ClearRule())));
            return Columns((_cardRename, 68), (apply, 32));
        }

        private static void AddRuleRow(TableLayoutPanel t, int row, string l1, Control c1, string l2, Control c2, string l3, Control c3)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(StyleUi.Caption(l1), 0, row);
            t.Controls.Add(c1, 1, row);
            t.Controls.Add(StyleUi.Caption(l2), 2, row);
            t.Controls.Add(c2, 3, row);
            t.Controls.Add(StyleUi.Caption(l3), 4, row);
            c3.Dock = DockStyle.Fill;
            t.Controls.Add(c3, 5, row);
        }

        private Control BuildStandardsTab()
        {
            _txtTemplate.Text = _settings.TemplatePath ?? "";
            TableLayoutPanel pick = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.Controls.Add(_txtTemplate, 0, 0);
            pick.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFile(_txtTemplate, "Choose the company template")), 1, 0);
            pick.Controls.Add(StyleUi.Primary("Compare", (s, e) => LoadTemplate()), 2, 0);
            pick.Controls.Add(StyleUi.Secondary("Clear", (s, e) => ClearTemplate()), 3, 0);
            Panel body = new Panel { AutoScroll = true };
            body.Controls.Add(_lblTemplate);
            _lblTemplate.Dock = DockStyle.Top;
            body.Controls.Add(pick);
            _lblTemplate.Text = "Compare the drawing with the template to find styles that are missing or not part of the standard.";
            _cardTemplate = new StyleCard("Company standard", "Styles are matched by collection and name.", body);

            StyleCard actions = new StyleCard("Bring the drawing to standard", "Import what is missing, update definitions, then map every non-standard style onto a standard one (merging their objects).");
            _btnImportMissing.Click += (s, e) => ImportMissing();
            _btnUpdateMatching.Click += (s, e) => UpdateMatching();
            _btnMap.Click += (s, e) => MapNonStandard();
            actions.Body.Controls.Add(_btnImportMissing);
            actions.Body.Controls.Add(_btnUpdateMatching);
            actions.Body.Controls.Add(_btnMap);
            UpdateStandardsButtons();
            return Columns((_cardTemplate, 55), (actions, 45));
        }

        private Control BuildTransferTab()
        {
            _cbConflict.Items.AddRange(new object[] { "Skip it (keep the existing style)", "Overwrite it", "Keep both (import under a new name)" });
            _cbConflict.SelectedIndex = Math.Max(0, Math.Min(2, _settings.ConflictMode));

            StyleCard push = new StyleCard("Push ticked styles", "Copies the ticked styles into another open drawing.");
            push.Body.Controls.Add(StyleUi.Row(_cbPushTarget, StyleUi.Secondary("↻", (s, e) => FillOpenDrawings())));
            push.Body.Controls.Add(StyleUi.Primary("Push ticked…", (s, e) => PushChecked()));

            _txtPull.Text = _settings.PullPath ?? "";
            TableLayoutPanel pullBody = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
            pullBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pullBody.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pullBody.Controls.Add(_txtPull, 0, 0);
            pullBody.Controls.Add(StyleUi.Secondary("Browse…", (s, e) => BrowseFile(_txtPull, "Choose a drawing to pull styles from")), 1, 0);
            FlowLayoutPanel open = StyleUi.Row(StyleUi.Caption("or open drawing:"), _cbPullOpen);
            pullBody.Controls.Add(open, 0, 1);
            pullBody.SetColumnSpan(open, 2);
            FlowLayoutPanel pullButtons = StyleUi.Row(StyleUi.Primary("Choose styles to pull…", (s, e) => PullStyles()));
            pullBody.Controls.Add(pullButtons, 0, 2);
            StyleCard pull = new StyleCard("Pull styles", "Pick styles from a file or another open drawing.", pullBody);

            StyleCard conflict = new StyleCard("When a style already exists", "Used by push, pull and import.");
            conflict.Body.Controls.Add(_cbConflict);
            FillOpenDrawings();
            return Columns((push, 33), (pull, 40), (conflict, 27));
        }

        private Control BuildBatchTab()
        {
            StyleCard card = new StyleCard("Batch processing",
                "Clean a whole folder of drawings without opening them: merge duplicates, apply your mapping file, bring them to the template, " +
                "apply the rename rule and purge unused styles. Each drawing is saved with a .bak copy, or into another folder.");
            card.Body.Controls.Add(StyleUi.Primary("Open batch processor…", (s, e) => OpenBatch()));
            return Columns((card, 100));
        }

        private Control BuildReportTab()
        {
            StyleCard card = new StyleCard("Report", "Exports name, status, objects, other references, duplicate, standard state, description, author and dates.");
            card.Body.Controls.Add(StyleUi.Row(
                StyleUi.Primary("Export all styles to CSV…", (s, e) => ExportCsv(_session?.Items)),
                StyleUi.Secondary("Export visible rows to CSV…", (s, e) => ExportCsv(_view)),
                StyleUi.Secondary("Copy visible rows", (s, e) => CopyVisible())));
            return Columns((card, 100));
        }

        private void RestoreLayout()
        {
            Size = new Size(Math.Max(MinimumSize.Width, _settings.WindowWidth), Math.Max(MinimumSize.Height, _settings.WindowHeight));
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (_settings.WindowX >= 0 && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(_settings.WindowX + 50, _settings.WindowY + 20)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(_settings.WindowX, _settings.WindowY);
            }
            else
            {
                StartPosition = FormStartPosition.Manual;
                Width = Math.Min(Width, area.Width - 40);
                Height = Math.Min(Height, area.Height - 40);
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
            if (_settings.Maximized) WindowState = FormWindowState.Maximized;
            _deck.SelectedIndex = Math.Max(0, Math.Min(_deck.TabCount - 1, _settings.LastTab));

            Shown += (s, e) =>
            {
                TrySplit(_splitMain, _settings.TreeWidth);
                TrySplit(_splitInner, _splitInner.Width - _settings.DetailsWidth);
                TrySplit(_splitDeck, _splitDeck.Height - _settings.DeckHeight);
            };
        }

        private static void TrySplit(SplitContainer split, int distance)
        {
            try { split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(distance, (split.Orientation == Orientation.Vertical ? split.Width : split.Height) - split.Panel2MinSize - 10)); }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        private void SaveLayout()
        {
            _settings.Maximized = WindowState == FormWindowState.Maximized;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowX = b.X; _settings.WindowY = b.Y; _settings.WindowWidth = b.Width; _settings.WindowHeight = b.Height;
            _settings.TreeWidth = _splitMain.SplitterDistance;
            _settings.DetailsWidth = _splitInner.Width - _splitInner.SplitterDistance;
            _settings.DeckHeight = _splitDeck.Height - _splitDeck.SplitterDistance;
            _settings.LastTab = _deck.SelectedIndex;
            _settings.Rule = _rule;
            _settings.RepeatPurge = _chkRepeat.Checked;
            _settings.PurgeScope = _cbPurgeScope.SelectedIndex;
            _settings.ConflictMode = _cbConflict.SelectedIndex;
            _settings.TemplatePath = _txtTemplate.Text.Trim();
            _settings.PullPath = _txtPull.Text.Trim();
            _settings.Save();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { _txtSearch.Focus(); _txtSearch.SelectAll(); return true; }
            if (keyData == Keys.F5) { Reload(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // =====================================================================================
        // Drawing binding and scanning
        // =====================================================================================

        private void BindTo(Document doc)
        {
            _doc = doc;
            _checked.Clear();
            _session = doc == null ? null : new StyleSession(doc.Database) { Template = _template };
            _lblDrawing.Text = doc == null ? "No drawing" : Path.GetFileName(doc.Name);
            FillOpenDrawings();
            Reload();
        }

        private void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document == null || e.Document == _doc || IsDisposed || _busy) return;
            BeginInvoke(new Action(() => { if (!IsDisposed) BindTo(e.Document); }));
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != _doc) return;
            _doc = null;
            _session = null;
            _checked.Clear();
            _lblDrawing.Text = "No drawing";
            BeginInvoke(new Action(() => { if (!IsDisposed) { FillOpenDrawings(); RefreshAll(); } }));
        }

        private void Reload()
        {
            if (_session == null || _doc == null) { RefreshAll(); return; }
            _busy = true;
            Cursor = Cursors.WaitCursor;
            _progress.Visible = true;
            _progress.Value = 0;
            SetStatus("Scanning every object for style references…");
            try
            {
                using (_doc.LockDocument())
                {
                    _session.Reload(p =>
                    {
                        _progress.Value = Math.Max(0, Math.Min(100, (int)(p * 100)));
                        _progress.GetCurrentParent()?.Refresh();
                    });
                }
                HashSet<ObjectId> alive = new HashSet<ObjectId>(_session.Items.Select(i => i.Id).Concat(_session.Missing.Select(m => m.Id)));
                _checked.RemoveWhere(id => !alive.Contains(id));
                ScanResult scan = _session.Scan;
                _scanInfo.Text = $"{_session.Items.Count:N0} styles · {scan.ObjectsScanned:N0} objects scanned in {scan.Seconds:0.0} s";
                _notes.Visible = scan.Notes.Count > 0 || scan.Unreadable > 0;
                _notes.Text = $"{scan.Notes.Count + (scan.Unreadable > 0 ? 1 : 0)} scan note(s)";
                SetStatus("Ready.");
            }
            catch (Exception ex)
            {
                SetStatus("Scan failed: " + ex.Message, true);
            }
            finally
            {
                _progress.Visible = false;
                Cursor = Cursors.Default;
                _busy = false;
            }
            RefreshAll();
        }

        private void RefreshAll()
        {
            BuildTree();
            ApplyView();
            UpdateDeck();
            UpdateStandardsButtons();
        }

        private void ShowNotes()
        {
            if (_session == null) return;
            List<string> lines = new List<string>(_session.Scan.Notes);
            if (_session.Scan.Unreadable > 0) lines.Add($"{_session.Scan.Unreadable} object(s) could not be read; their references are not counted.");
            MessageBox.Show(this, string.Join("\n\n", lines), "Scan notes", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================================
        // Tree, chips and the filtered view
        // =====================================================================================

        private void BuildTree()
        {
            HashSet<string> expanded = new HashSet<string>();
            CollectExpanded(_tree.Nodes, expanded);
            bool first = _tree.Nodes.Count == 0;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            List<StyleItem> items = _session?.Items ?? new List<StyleItem>();

            Dictionary<string, (int total, int unused)> counts = new Dictionary<string, (int, int)>();
            foreach (StyleItem i in items)
            {
                string path = "";
                foreach (string part in i.TreePath.Split(new[] { " › " }, StringSplitOptions.None))
                {
                    path = path.Length == 0 ? part : path + " › " + part;
                    counts.TryGetValue(path, out (int total, int unused) c);
                    counts[path] = (c.total + 1, c.unused + (i.Usage == StyleUsage.Unused && !i.Protected ? 1 : 0));
                }
            }

            TreeNode root = new TreeNode($"All styles  ({items.Count:N0})") { Tag = "" };
            _tree.Nodes.Add(root);
            Dictionary<string, TreeNode> nodes = new Dictionary<string, TreeNode> { [""] = root };
            foreach (string path in counts.Keys.OrderBy(k => k.StartsWith(StyleCatalog.AcadGroup) ? 1 : 0).ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                int cut = path.LastIndexOf(" › ", StringComparison.Ordinal);
                string parent = cut < 0 ? "" : path.Substring(0, cut);
                string name = cut < 0 ? path : path.Substring(cut + 3);
                (int total, int unused) c = counts[path];
                TreeNode n = new TreeNode(c.unused > 0 ? $"{name}  ({c.total} · {c.unused} unused)" : $"{name}  ({c.total})") { Tag = path };
                if (cut < 0) n.NodeFont = StyleUi.Bold;
                if (!nodes.TryGetValue(parent, out TreeNode p)) p = root;
                p.Nodes.Add(n);
                nodes[path] = n;
            }
            root.Expand();
            foreach (TreeNode n in nodes.Values) if (expanded.Contains((string)n.Tag) || (first && n.Parent == root)) n.Expand();
            _tree.SelectedNode = nodes.TryGetValue(_category, out TreeNode sel) ? sel : root;
            if (_tree.SelectedNode == root) _category = "";
            _tree.EndUpdate();
        }

        private static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> into)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.IsExpanded && n.Tag is string s) into.Add(s);
                CollectExpanded(n.Nodes, into);
            }
        }

        private void SelectCategory(string path)
        {
            TreeNode found = FindNode(_tree.Nodes, path);
            if (found != null) { _tree.SelectedNode = found; found.EnsureVisible(); }
        }

        private static TreeNode FindNode(TreeNodeCollection nodes, string path)
        {
            foreach (TreeNode n in nodes)
            {
                if ((n.Tag as string) == path) return n;
                TreeNode f = FindNode(n.Nodes, path);
                if (f != null) return f;
            }
            return null;
        }

        private void SelectChip(string kind)
        {
            _chip = kind;
            foreach (StyleChip c in _chips.Values) c.Selected = c.Kind == kind;
            ApplyView();
        }

        private bool InCategory(StyleItem i) => _category.Length == 0 || i.TreePath == _category || i.TreePath.StartsWith(_category + " › ", StringComparison.Ordinal);

        private bool MatchesSearch(StyleItem i)
        {
            string q = _txtSearch.Text.Trim();
            if (q.Length == 0) return true;
            return i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                   || (i.Description ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                   || i.FullCategory.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool MatchesChip(StyleItem i, string kind)
        {
            switch (kind)
            {
                case "Used": return i.Usage == StyleUsage.Used;
                case "Unused": return i.Usage == StyleUsage.Unused && !i.Protected;
                case "Hidden ref": return i.Usage == StyleUsage.Hidden && !i.Protected;
                case "Protected": return i.Protected;
                case "Duplicate": return i.DuplicateOf != null;
                case "Non-standard": return i.Standard == StandardState.NonStandard;
                case "Checked": return _checked.Contains(i.Id);
                default: return true;
            }
        }

        private static readonly System.Drawing.Font Italic = new System.Drawing.Font(StyleUi.Base, FontStyle.Italic);
        private HashSet<StyleItem> _missingSet = new HashSet<StyleItem>();

        private bool IsMissing(StyleItem i) => _missingSet.Contains(i);

        private void ApplyView()
        {
            List<StyleItem> items = _session?.Items ?? new List<StyleItem>();
            List<StyleItem> missing = _session?.Missing ?? new List<StyleItem>();
            _missingSet = new HashSet<StyleItem>(missing);
            List<StyleItem> scoped = items.Where(i => InCategory(i) && MatchesSearch(i)).ToList();
            List<StyleItem> scopedMissing = missing.Where(i => InCategory(i) && MatchesSearch(i)).ToList();

            foreach (StyleChip chip in _chips.Values)
                chip.Count = chip.Kind == "Missing" ? scopedMissing.Count
                    : chip.Kind == "Checked" ? scoped.Count(i => _checked.Contains(i.Id)) + scopedMissing.Count(i => _checked.Contains(i.Id))
                    : scoped.Count(i => MatchesChip(i, chip.Kind));

            _view = _chip == "Missing" ? scopedMissing : scoped.Where(i => MatchesChip(i, _chip)).ToList();
            SortView();

            ObjectId current = CurrentItem()?.Id ?? ObjectId.Null;
            _grid.RowCount = 0;
            _grid.RowCount = _view.Count;
            int index = _view.FindIndex(i => i.Id == current);
            if (index >= 0)
            {
                _grid.ClearSelection();
                _grid.CurrentCell = _grid.Rows[index].Cells[ColName];
                _grid.Rows[index].Selected = true;
            }
            UpdateRenamePreview();
            ShowDetails(CurrentItem());
            if (_session != null && _view.Count == 0 && items.Count > 0) SetStatus("No styles match the current filter.");
        }

        private void SortView()
        {
            Comparison<StyleItem> cmp;
            switch (_sortColumn)
            {
                case ColStatus: cmp = (a, b) => StatusRank(a).CompareTo(StatusRank(b)); break;
                case ColUses: cmp = (a, b) => a.Uses.CompareTo(b.Uses); break;
                case ColCategory: cmp = (a, b) => string.Compare(a.FullCategory, b.FullCategory, StringComparison.OrdinalIgnoreCase); break;
                case ColDescription: cmp = (a, b) => string.Compare(a.Description, b.Description, StringComparison.OrdinalIgnoreCase); break;
                case ColModified: cmp = (a, b) => Nullable.Compare(a.Modified, b.Modified); break;
                case ColNew: cmp = (a, b) => string.Compare(PreviewName(a), PreviewName(b), StringComparison.OrdinalIgnoreCase); break;
                default: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
            }
            Comparison<StyleItem> final = _sortAscending ? cmp : (a, b) => cmp(b, a);
            // Ties fall back to category, then name, so the order never jumps around.
            _view.Sort((a, b) =>
            {
                int r = final(a, b);
                if (r != 0) return r;
                r = string.Compare(a.FullCategory, b.FullCategory, StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (DataGridViewColumn c in _grid.Columns)
                c.HeaderCell.SortGlyphDirection = c.Index == _sortColumn ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending) : SortOrder.None;
        }

        private int StatusRank(StyleItem i) => IsMissing(i) ? 5 : i.Protected ? 4 : i.Usage == StyleUsage.Unused ? 0 : i.Usage == StyleUsage.Hidden ? 2 : 3;

        // =====================================================================================
        // Grid
        // =====================================================================================

        private StyleItem ItemAt(int row) => row >= 0 && row < _view.Count ? _view[row] : null;
        private StyleItem CurrentItem() => ItemAt(_grid.CurrentCell?.RowIndex ?? -1);

        private List<StyleItem> SelectedItems()
        {
            List<StyleItem> list = new List<StyleItem>();
            foreach (DataGridViewRow r in _grid.SelectedRows) { StyleItem i = ItemAt(r.Index); if (i != null) list.Add(i); }
            if (list.Count == 0 && CurrentItem() != null) list.Add(CurrentItem());
            return list;
        }

        private List<StyleItem> CheckedItems() => _session == null ? new List<StyleItem>() : _session.Items.Where(i => _checked.Contains(i.Id)).ToList();

        private string PreviewName(StyleItem i) => _renamePreview.TryGetValue(i.Id, out PlannedRename p) ? p.NewValue : "";

        private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            StyleItem i = ItemAt(e.RowIndex);
            if (i == null) return;
            switch (e.ColumnIndex)
            {
                case ColCheck: e.Value = _checked.Contains(i.Id); break;
                case ColName: e.Value = i.Name; break;
                case ColNew:
                    e.Value = _renamePreview.TryGetValue(i.Id, out PlannedRename p) ? (p.Problem.Length > 0 ? "⚠ " + p.Problem : p.NewValue) : "";
                    break;
                case ColUses: e.Value = IsMissing(i) ? "" : i.Uses.ToString("N0"); break;
                case ColCategory: e.Value = i.FullCategory; break;
                case ColDescription: e.Value = i.Description; break;
                case ColModified: e.Value = i.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""; break;
                default: e.Value = ""; break;
            }
        }

        private void Grid_CellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            StyleItem i = ItemAt(e.RowIndex);
            if (i == null) return;
            if (e.ColumnIndex == ColCheck)
            {
                if (e.Value is bool b && b) _checked.Add(i.Id); else _checked.Remove(i.Id);
                AfterCheckChange(false);
            }
            else if (e.ColumnIndex == ColName)
            {
                string newName = (e.Value as string ?? "").Trim();
                if (newName.Length == 0 || newName == i.Name) return;
                // Renaming rescans the drawing, which rebuilds the grid, so run it after the edit has finished.
                BeginInvoke(new Action(() => RenameSingle(i, newName)));
            }
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            StyleItem i = ItemAt(e.RowIndex);
            if (i == null) return;
            if (IsMissing(i) && e.ColumnIndex != ColStatus) { e.CellStyle.ForeColor = StyleUi.Muted; e.CellStyle.Font = Italic; }
            if (e.ColumnIndex == ColNew && _renamePreview.TryGetValue(i.Id, out PlannedRename p) && p.Problem.Length > 0) e.CellStyle.ForeColor = StyleUi.Danger;
            if (e.ColumnIndex == ColName && i.IsChild) e.CellStyle.Padding = new Padding(22, 0, 4, 0);
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColStatus) return;
            StyleItem i = ItemAt(e.RowIndex);
            if (i == null) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float x = e.CellBounds.X + 6;
            float cy = e.CellBounds.Y + e.CellBounds.Height / 2f;
            e.Graphics.SetClip(e.CellBounds);
            foreach (string pill in Pills(i)) x += StyleUi.DrawPill(e.Graphics, pill, x, cy, StyleUi.Small) + 4;
            e.Graphics.ResetClip();
            e.Handled = true;
        }

        private IEnumerable<string> Pills(StyleItem i)
        {
            if (IsMissing(i)) { yield return "Missing"; yield break; }
            yield return i.Protected ? "Protected" : i.Usage == StyleUsage.Used ? "Used" : i.Usage == StyleUsage.Unused ? "Unused" : "Hidden ref";
            if (i.DuplicateOf != null) yield return "Duplicate";
            if (i.Standard == StandardState.NonStandard) yield return "Non-standard";
        }

        private void Grid_HeaderClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex == ColCheck)
            {
                // Header of the tick column: tick all visible, or untick them when all are ticked.
                bool all = _view.Count > 0 && _view.All(i => _checked.Contains(i.Id));
                SetChecked(_view, !all);
                return;
            }
            if (_sortColumn == e.ColumnIndex) _sortAscending = !_sortAscending;
            else { _sortColumn = e.ColumnIndex; _sortAscending = e.ColumnIndex != ColUses; }
            SortView();
            _grid.Invalidate();
        }

        private void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && _grid.CurrentCell?.ColumnIndex != ColCheck && !_grid.IsCurrentCellInEditMode)
            {
                List<StyleItem> sel = SelectedItems();
                SetChecked(sel, !sel.All(i => _checked.Contains(i.Id)));
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.C && !_grid.IsCurrentCellInEditMode)
            {
                CopyNames();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void SetChecked(IEnumerable<StyleItem> items, bool value)
        {
            foreach (StyleItem i in items) { if (value) _checked.Add(i.Id); else _checked.Remove(i.Id); }
            AfterCheckChange();
        }

        private void AfterCheckChange(bool invalidateGrid = true)
        {
            if (invalidateGrid) _grid.Invalidate();
            if (_chips.TryGetValue("Checked", out StyleChip chip)) chip.Count = _view.Count(i => _checked.Contains(i.Id));
            // Rebuilding the rows is not allowed inside the grid's own value events, so defer it.
            if (_chip == "Checked") BeginInvoke(new Action(ApplyView));
            UpdateDeck();
            UpdateRenamePreview();
        }

        private void CopyNames()
        {
            List<StyleItem> sel = SelectedItems();
            if (sel.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(i => i.Name)));
        }

        // =====================================================================================
        // Details panel
        // =====================================================================================

        private void ShowDetails(StyleItem item)
        {
            _detailItem = item;
            _lvUsers.BeginUpdate();
            _lvUsers.Items.Clear();
            if (item == null)
            {
                _detName.Text = _session == null ? "Open a drawing" : "Select a style";
                _detCategory.Text = "";
                _detExplain.Text = "Pick a style in the list to see what uses it.";
                _detMeta.Text = "";
                _lblUsers.Text = "Used by";
                _lvUsers.EndUpdate();
                _detPills.Invalidate();
                return;
            }

            _detName.Text = item.Name;
            _detCategory.Text = item.FullCategory;
            _detExplain.Text = Explain(item);
            List<string> meta = new List<string> { "Type: " + item.TypeName };
            if (!string.IsNullOrEmpty(item.Description)) meta.Add("Description: " + item.Description);
            if (!string.IsNullOrEmpty(item.CreatedBy)) meta.Add("Created by: " + item.CreatedBy);
            if (!string.IsNullOrEmpty(item.ModifiedBy)) meta.Add("Modified by: " + item.ModifiedBy + (item.Modified.HasValue ? $" ({item.Modified:yyyy-MM-dd HH:mm})" : ""));
            _detMeta.Text = string.Join(Environment.NewLine, meta);
            _detPills.Invalidate();

            if (!IsMissing(item) && _session != null)
            {
                _lblUsers.Text = $"Used by  ({item.Uses:N0})";
                const int limit = 400;
                using (Transaction tr = _session.Db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in item.EntityUsers.Concat(item.OtherReferrers).Take(limit))
                    {
                        string kind = id.ObjectClass?.Name ?? "", name = "", where = "";
                        try
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            kind = obj.GetType().Name.Replace("TableRecord", "");
                            name = StyleCatalog.ReadString(obj, "Name") ?? "";
                            if (obj is Entity ent)
                            {
                                if (name.Length == 0) name = "Layer " + ent.Layer;
                                where = SpaceName(tr, ent.OwnerId);
                            }
                            else where = "Style / setting";
                        }
                        catch (Exception) { name = "<" + id.Handle + ">"; }
                        _lvUsers.Items.Add(new ListViewItem(new[] { kind, name, where }) { Tag = id });
                    }
                }
                if (item.Uses > limit) _lvUsers.Items.Add(new ListViewItem(new[] { $"… {item.Uses - limit:N0} more", "", "" }));
            }
            else _lblUsers.Text = "Used by";
            _lvUsers.EndUpdate();
        }

        private string Explain(StyleItem i)
        {
            if (IsMissing(i)) return "In the template but not in this drawing. Tick it and use Standards › Import missing.";
            StringBuilder sb = new StringBuilder();
            if (i.Protected) sb.Append(i.ProtectedReason).Append(". It is never deleted. ");
            else if (i.Usage == StyleUsage.Used) sb.Append($"Used by {StyleOperations.Describe(i)}. ");
            else if (i.Usage == StyleUsage.Unused) sb.Append("Nothing uses this style, so it can be purged. ");
            else sb.Append("No reference was found, but AutoCAD still refuses to purge it (usually a default in Feature or Command settings). It is never deleted. ");
            if (i.DuplicateOf != null) sb.Append($"Looks like a copy of \"{i.DuplicateOf.Name}\"; merging moves its objects there. ");
            if (i.Standard == StandardState.NonStandard) sb.Append("Not part of the template. ");
            if (i.Standard == StandardState.Standard) sb.Append("Matches a template style. ");
            return sb.ToString().Trim();
        }

        private static string SpaceName(Transaction tr, ObjectId ownerId)
        {
            try
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
                if (!btr.IsLayout) return "Block " + btr.Name;
                Layout layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                return layout.ModelType ? "Model" : "Layout " + layout.LayoutName;
            }
            catch (Exception) { return ""; }
        }

        // =====================================================================================
        // Zoom and select
        // =====================================================================================

        private bool CanUseView()
        {
            if (_doc != null && _doc == AcadCoreApp.DocumentManager.MdiActiveDocument) return true;
            SetStatus("Switch to the drawing first.", true);
            return false;
        }

        private void ZoomToUsers(StyleItem item)
        {
            if (item == null) return;
            if (item.EntityUsers.Count == 0) { SetStatus("No objects use this style directly.", true); return; }
            ZoomToIds(item.EntityUsers);
        }

        private void ZoomToIds(IEnumerable<ObjectId> ids)
        {
            if (!CanUseView()) return;
            int other = 0, shown = 0;
            Extents3d? ext = null;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                ObjectId space = _doc.Database.CurrentSpaceId;
                foreach (ObjectId id in ids)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    if (ent.OwnerId != space) { other++; continue; }
                    try
                    {
                        Extents3d e = ent.GeometricExtents;
                        if (ext.HasValue) { Extents3d x = ext.Value; x.AddExtents(e); ext = x; } else ext = e;
                        shown++;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                tr.Commit();
                if (ext.HasValue) ViewHelper.ZoomTo(ext.Value, 1.25, 5.0);
            }
            SetStatus(ext.HasValue ? $"Zoomed to {shown} object(s)" + (other > 0 ? $"; {other} more are in other layouts or blocks." : ".")
                                   : $"None of these objects are in the current space ({other} elsewhere).", !ext.HasValue);
        }

        private void SelectUsers(List<StyleItem> items)
        {
            if (items.Count == 0 || !CanUseView()) return;
            ObjectId space = _doc.Database.CurrentSpaceId;
            List<ObjectId> here = new List<ObjectId>();
            int other = 0;
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in items.SelectMany(i => i.EntityUsers).Distinct())
                {
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead).OwnerId == space) here.Add(id); else other++;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
                tr.Commit();
            }
            try
            {
                using (_doc.LockDocument()) _doc.Editor.SetImpliedSelection(here.ToArray());
                SetStatus($"Selected {here.Count} object(s) in the current space" + (other > 0 ? $"; {other} more are in other layouts or blocks." : "."));
            }
            catch (Exception ex) { SetStatus("Selection failed: " + ex.Message, true); }
        }

        // =====================================================================================
        // Operations
        // =====================================================================================

        /// <summary>Runs a change on the drawing (locked), rescans, and reports the outcome.</summary>
        private void RunOp(string verb, Func<OpResult> op)
        {
            if (_session == null || _doc == null) return;
            OpResult result;
            _busy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (_doc.LockDocument()) result = op();
            }
            catch (Exception ex)
            {
                _busy = false;
                Cursor = Cursors.Default;
                MessageBox.Show(this, ex.Message, "Style Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Reload();
                return;
            }
            _busy = false;
            Cursor = Cursors.Default;
            Reload();
            if (result == null) return;
            string summary = result.Summary(verb);
            SetStatus(summary, result.Failed > 0);
            if (result.Failed > 0 || result.Log.Count > 0)
            {
                string log = string.Join("\n", result.Log.Take(30)) + (result.Log.Count > 30 ? $"\n… {result.Log.Count - 30} more" : "");
                MessageBox.Show(this, summary + (log.Length > 0 ? "\n\n" + log : ""), "Style Manager", MessageBoxButtons.OK,
                    result.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        private List<object> Preview(string title, string intro, string[] columns, List<PreviewRow> rows, string apply, bool destructive)
        {
            if (rows.Count == 0) { SetStatus("Nothing to do."); return null; }
            using (FormStylePreview f = new FormStylePreview(title, intro, columns, rows, apply, destructive))
                return f.ShowDialog(this) == DialogResult.OK && f.Chosen.Count > 0 ? f.Chosen : null;
        }

        private void PurgeUnused()
        {
            if (_session == null) return;
            int scope = _cbPurgeScope.SelectedIndex;
            string category = _category;
            HashSet<ObjectId> ticked = new HashSet<ObjectId>(_checked);
            Func<StyleItem, bool> inScope = i => scope == 0 || (scope == 1 ? (category.Length == 0 || i.TreePath == category || i.TreePath.StartsWith(category + " › ", StringComparison.Ordinal)) : ticked.Contains(i.Id));
            List<StyleItem> candidates = _session.Items.Where(i => i.Usage == StyleUsage.Unused && !i.Protected && inScope(i)).ToList();
            bool repeat = _chkRepeat.Checked;

            List<object> chosen = Preview("Purge unused styles",
                $"{candidates.Count} style(s) are not used by anything. Untick any you want to keep." +
                (repeat ? " After deleting, the drawing is rescanned and styles that only these ones used are deleted too (within the same scope; unticked styles are always kept)." : ""),
                new[] { "Style", "Category", "Type" },
                candidates.Select(i => new PreviewRow { Cells = new[] { i.Name, i.FullCategory, i.TypeName }, Tag = i }).ToList(),
                "Delete ticked", true);
            if (chosen == null) return;
            HashSet<ObjectId> keep = new HashSet<ObjectId>(candidates.Select(i => i.Id).Except(chosen.Cast<StyleItem>().Select(i => i.Id)));
            RunOp("deleted", () => StyleOperations.DeepPurge(_session, inScope, keep, repeat, s => SetStatus(s)));
        }

        private void MergeDuplicates()
        {
            if (_session == null) return;
            List<StyleItem> dups = _session.Items.Where(i => i.DuplicateOf != null && !i.Protected && InCategory(i)).ToList();
            List<object> chosen = Preview("Merge duplicates",
                "Each copy's objects move to the original style, then the copy is deleted. A copy something else still points at (for example a default in settings) is kept and reported.",
                new[] { "Copy", "Merge into", "Category", "Uses" },
                dups.Select(i => new PreviewRow { Cells = new[] { i.Name, i.DuplicateOf.Name, i.FullCategory, i.Uses.ToString() }, Tag = i }).ToList(),
                "Merge ticked", true);
            if (chosen == null) return;
            List<KeyValuePair<StyleItem, StyleItem>> pairs = chosen.Cast<StyleItem>().Select(i => new KeyValuePair<StyleItem, StyleItem>(i, i.DuplicateOf)).ToList();
            RunOp("merged", () => StyleOperations.Merge(_session, pairs));
        }

        private void MergeChecked()
        {
            if (!(_cbMergeTarget.SelectedItem is Choice c) || !(c.Value is StyleItem target)) { SetStatus("Pick the style to merge into.", true); return; }
            MergeCheckedInto(target);
        }

        private void MergeCheckedInto(StyleItem target)
        {
            if (_session == null || target == null) return;
            List<StyleItem> sources = CheckedItems().Where(i => i.Id != target.Id).ToList();
            if (sources.Count == 0) { SetStatus("Tick the styles to merge first.", true); return; }
            List<object> chosen = Preview($"Merge into \"{target.Name}\"",
                "Objects and style references move to the target, then each source style is deleted if nothing else still points at it. Styles of another kind cannot be merged.",
                new[] { "Source style", "Category", "Uses", "Note" },
                sources.Select(i => new PreviewRow
                {
                    Cells = new[] { i.Name, i.FullCategory, i.Uses.ToString(), StyleOperations.CanMerge(i, target) ? "" : "Different kind of style" },
                    Enabled = StyleOperations.CanMerge(i, target) && !i.Protected,
                    Checked = StyleOperations.CanMerge(i, target),
                    Tag = i
                }).ToList(),
                "Merge ticked", true);
            if (chosen == null) return;
            List<KeyValuePair<StyleItem, StyleItem>> pairs = chosen.Cast<StyleItem>().Select(i => new KeyValuePair<StyleItem, StyleItem>(i, target)).ToList();
            RunOp("merged", () => StyleOperations.Merge(_session, pairs));
        }

        private void DeleteChecked()
        {
            List<StyleItem> items = CheckedItems();
            if (items.Count == 0) { SetStatus("Tick styles to delete first.", true); return; }
            List<object> chosen = Preview("Delete ticked styles",
                "Only styles nothing uses can be deleted; the others are listed greyed with the reason. Use Merge to move objects off a used style first.",
                new[] { "Style", "Category", "Status" },
                items.Select(i =>
                {
                    bool ok = i.Usage == StyleUsage.Unused && !i.Protected;
                    return new PreviewRow
                    {
                        Cells = new[] { i.Name, i.FullCategory, i.Protected ? i.ProtectedReason : ok ? "Unused" : "In use: " + StyleOperations.Describe(i) },
                        Enabled = ok, Tag = i
                    };
                }).ToList(),
                "Delete ticked", true);
            if (chosen == null) return;
            RunOp("deleted", () => StyleOperations.Delete(_session, chosen.Cast<StyleItem>()));
        }

        private void RenameSingle(StyleItem item, string newName)
        {
            if (_session == null) return;
            PlannedRename plan = StyleOperations.PlanSingle(_session.Items, item, newName);
            if (plan.Problem.Length > 0) { SetStatus($"Cannot rename to \"{newName}\": {plan.Problem}.", true); _grid.Invalidate(); return; }
            RunOp("renamed", () => StyleOperations.ApplyRenames(_session.Db, new List<PlannedRename> { plan }, false));
        }

        // ---------------- Rename rule ----------------

        private void RuleChanged()
        {
            _rule = new RenameRule
            {
                TargetDescription = _rbDescription.Checked,
                Find = _txtFind.Text, Replace = _txtReplace.Text, MatchCase = _chkCase.Checked, UseRegex = _chkRegex.Checked,
                AddPrefix = _txtAddPrefix.Text, RemovePrefix = _txtRemovePrefix.Text, AddSuffix = _txtAddSuffix.Text, RemoveSuffix = _txtRemoveSuffix.Text,
                CaseMode = Math.Max(0, _cbCase.SelectedIndex)
            };
            UpdateRenamePreview();
        }

        private void UpdateRenamePreview()
        {
            bool active = _deck.SelectedTab?.Text == "Rename" && !_rule.IsEmpty && _session != null;
            _renamePreview = active
                ? StyleOperations.PlanRenames(_session.Items, CheckedItems(), _rule).ToDictionary(p => p.Item.Id)
                : new Dictionary<ObjectId, PlannedRename>();
            _grid.Columns[ColNew].Visible = active;
            _grid.Columns[ColNew].HeaderText = _rule.TargetDescription ? "New description (preview)" : "New name (preview)";
            int problems = _renamePreview.Values.Count(p => p.Problem.Length > 0);
            int ticked = _checked.Count;
            _lblRenameSummary.Text = _rule.IsEmpty ? "Fill in the rule to see a preview."
                : ticked == 0 ? "Tick the styles to rename."
                : $"{_renamePreview.Count - problems} of {ticked} ticked style(s) will change" + (problems > 0 ? $" · {problems} cannot (shown in red)." : ".");
            _grid.Invalidate();
        }

        private void ApplyRename()
        {
            if (_session == null || _rule.IsEmpty) { SetStatus("Fill in the rename rule first.", true); return; }
            List<PlannedRename> plans = StyleOperations.PlanRenames(_session.Items, CheckedItems(), _rule);
            RenameRule rule = _rule.Clone();
            List<object> chosen = Preview(rule.TargetDescription ? "Change descriptions" : "Rename styles",
                "Rows that would clash with an existing name, or are not valid, are greyed out.",
                new[] { "Current", "New", "Category", "Problem" },
                plans.Select(p => new PreviewRow { Cells = new[] { p.OldValue, p.NewValue, p.Item.FullCategory, p.Problem }, Enabled = p.Problem.Length == 0, Tag = p }).ToList(),
                "Apply ticked", false);
            if (chosen == null) return;
            RunOp(rule.TargetDescription ? "changed" : "renamed", () => StyleOperations.ApplyRenames(_session.Db, chosen.Cast<PlannedRename>().ToList(), rule.TargetDescription));
        }

        private void ClearRule()
        {
            foreach (TextBox t in new[] { _txtFind, _txtReplace, _txtAddPrefix, _txtRemovePrefix, _txtAddSuffix, _txtRemoveSuffix }) t.Clear();
            _cbCase.SelectedIndex = 0;
            _chkCase.Checked = _chkRegex.Checked = false;
            RuleChanged();
        }

        // ---------------- Standards ----------------

        private void LoadTemplate()
        {
            string path = _txtTemplate.Text.Trim();
            if (!File.Exists(path)) { SetStatus("Template file not found.", true); return; }
            try
            {
                Cursor = Cursors.WaitCursor;
                StyleTemplate t = new StyleTemplate(path);
                _template?.Dispose();
                _template = t;
                if (_session != null) _session.Template = t;
                _settings.TemplatePath = path;
            }
            catch (Exception ex) { SetStatus("Could not read the template: " + ex.Message, true); return; }
            finally { Cursor = Cursors.Default; }
            if (_session != null) _session.Missing = StyleOperations.CompareStandards(_session.Items, _template.Items);
            _chips["Non-standard"].Visible = _chips["Missing"].Visible = true;
            RefreshAll();
            SetStatus($"Compared with {Path.GetFileName(path)}.");
        }

        private void ClearTemplate()
        {
            _template?.Dispose();
            _template = null;
            if (_session != null)
            {
                _session.Template = null;
                _session.Missing = new List<StyleItem>();
                foreach (StyleItem i in _session.Items) i.Standard = StandardState.Unknown;
            }
            _chips["Non-standard"].Visible = _chips["Missing"].Visible = false;
            if (_chip == "Missing" || _chip == "Non-standard") SelectChip("All");
            RefreshAll();
        }

        private void UpdateStandardsButtons()
        {
            bool has = _template != null && _session != null;
            int missing = has ? _session.Missing.Count : 0;
            int nonStd = has ? _session.Items.Count(i => i.Standard == StandardState.NonStandard && !i.IsChild && !i.Protected) : 0;
            int std = has ? _session.Items.Count(i => i.Standard == StandardState.Standard) : 0;
            _btnImportMissing.Text = has ? $"Import missing styles ({missing})…" : "Import missing styles…";
            _btnMap.Text = has ? $"Map non-standard styles ({nonStd})…" : "Map non-standard styles…";
            _btnImportMissing.Enabled = _btnUpdateMatching.Enabled = _btnMap.Enabled = has;
            _lblTemplate.Text = has
                ? $"{Path.GetFileName(_template.Path)}: {_template.Items.Count:N0} styles.  This drawing: {std:N0} standard, {nonStd:N0} non-standard, {missing:N0} missing."
                : "Compare the drawing with the template to find styles that are missing or not part of the standard.";
        }

        private ConflictMode Conflict => (ConflictMode)Math.Max(0, _cbConflict.SelectedIndex);

        private void ImportMissing()
        {
            if (_template == null || _session == null) return;
            List<object> chosen = Preview("Import missing styles", "These template styles are not in the drawing.",
                new[] { "Style", "Category" },
                _session.Missing.Select(i => new PreviewRow { Cells = new[] { i.Name, i.FullCategory }, Checked = _chip != "Missing" || _checked.Contains(i.Id) || _checked.Count == 0, Tag = i }).ToList(),
                "Import ticked", false);
            if (chosen == null) return;
            RunOp("imported", () => StyleOperations.Import(_template.Db, _session.Db, chosen.Cast<StyleItem>(), ConflictMode.Skip));
        }

        private void UpdateMatching()
        {
            if (_template == null || _session == null) return;
            HashSet<string> keys = new HashSet<string>(_session.Items.Select(i => i.Key));
            List<StyleItem> matching = _template.Items.Where(t => !t.IsChild && keys.Contains(t.Key)).ToList();
            List<object> chosen = Preview("Update styles from the template",
                "The drawing's definition of each ticked style is replaced by the template's (colours, layers, components). Names and the objects using them stay.",
                new[] { "Style", "Category" },
                matching.Select(i => new PreviewRow { Cells = new[] { i.Name, i.FullCategory }, Tag = i }).ToList(),
                "Overwrite ticked", true);
            if (chosen == null) return;
            RunOp("updated", () => StyleOperations.Import(_template.Db, _session.Db, chosen.Cast<StyleItem>(), ConflictMode.Overwrite));
        }

        private void MapNonStandard()
        {
            if (_template == null || _session == null) return;
            List<StyleItem> sources = _session.Items.Where(i => i.Standard == StandardState.NonStandard && !i.IsChild && !i.Protected).ToList();
            if (sources.Count == 0) { SetStatus("Every style is part of the standard."); return; }
            List<StyleMapEntry> entries;
            using (FormStyleMapping f = new FormStyleMapping(sources, _template.Items, _settings.MappingPath))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                entries = f.Result;
                _settings.MappingPath = f.MappingPath;
            }
            if (entries.Count == 0) return;

            RunOp("merged", () =>
            {
                // Targets must exist in the drawing before objects can move to them.
                HashSet<string> present = new HashSet<string>(_session.Items.Select(i => i.Key));
                List<StyleItem> needed = _template.Items.Where(t => entries.Any(e => e.Collection == t.CollectionKey && string.Equals(e.To, t.Name, StringComparison.OrdinalIgnoreCase)) && !present.Contains(t.Key)).ToList();
                OpResult total = new OpResult("Map to standard");
                if (needed.Count > 0)
                {
                    OpResult imp = StyleOperations.Import(_template.Db, _session.Db, needed, ConflictMode.Skip);
                    total.Log.Add($"{imp.Changed} target style(s) imported from the template.");
                    total.Log.AddRange(imp.Log);
                    _session.Reload();
                }
                OpResult merge = StyleOperations.Merge(_session, StyleOperations.ResolveMapping(_session.Items, entries));
                total.Add(merge);
                return total;
            });
        }

        // ---------------- Transfer ----------------

        private void FillOpenDrawings()
        {
            _cbPushTarget.Items.Clear();
            _cbPullOpen.Items.Clear();
            _cbPullOpen.Items.Add(new Choice("(use the file above)", null));
            foreach (Document d in AcadCoreApp.DocumentManager)
            {
                if (d == _doc) continue;
                Choice c = new Choice(Path.GetFileName(d.Name), d);
                _cbPushTarget.Items.Add(c);
                _cbPullOpen.Items.Add(new Choice(Path.GetFileName(d.Name), d));
            }
            if (_cbPushTarget.Items.Count > 0) _cbPushTarget.SelectedIndex = 0;
            _cbPullOpen.SelectedIndex = 0;
        }

        private void PushChecked()
        {
            if (_session == null) return;
            if (!(_cbPushTarget.SelectedItem is Choice c) || !(c.Value is Document dest)) { SetStatus("Open the destination drawing first, then press ↻.", true); return; }
            List<StyleItem> items = CheckedItems();
            if (items.Count == 0) { SetStatus("Tick the styles to push first.", true); return; }
            List<object> chosen = Preview($"Push to {c.Text}", "Child label styles travel with their parent.",
                new[] { "Style", "Category" },
                items.Select(i => new PreviewRow { Cells = new[] { i.Name, i.FullCategory }, Enabled = !i.IsChild, Tag = i }).ToList(),
                "Push ticked", false);
            if (chosen == null) return;
            ConflictMode mode = Conflict;
            RunOp("pushed", () =>
            {
                using (dest.LockDocument()) return StyleOperations.Import(_session.Db, dest.Database, chosen.Cast<StyleItem>(), mode);
            });
        }

        private void PullStyles()
        {
            if (_session == null) return;
            Document openSource = (_cbPullOpen.SelectedItem as Choice)?.Value as Document;
            StyleTemplate file = null;
            Database source;
            List<StyleItem> items;
            try
            {
                if (openSource != null)
                {
                    source = openSource.Database;
                    using (Transaction tr = source.TransactionManager.StartTransaction()) { items = StyleCatalog.Build(source, tr); tr.Commit(); }
                }
                else
                {
                    string path = _txtPull.Text.Trim();
                    if (!File.Exists(path)) { SetStatus("Choose a drawing or template to pull from.", true); return; }
                    file = new StyleTemplate(path);
                    source = file.Db;
                    items = file.Items;
                }

                HashSet<string> present = new HashSet<string>(_session.Items.Select(i => i.Key));
                List<object> chosen = Preview("Pull styles", "Styles the drawing does not have yet are ticked. Existing ones follow the conflict choice on the Transfer tab.",
                    new[] { "Style", "Category", "In this drawing" },
                    items.Where(i => !i.IsChild).Select(i => new PreviewRow
                    {
                        Cells = new[] { i.Name, i.FullCategory, present.Contains(i.Key) ? "Exists" : "New" },
                        Checked = !present.Contains(i.Key), Tag = i
                    }).ToList(),
                    "Pull ticked", false);
                if (chosen == null) return;
                ConflictMode mode = Conflict;
                RunOp("pulled", () =>
                {
                    if (openSource == null) return StyleOperations.Import(source, _session.Db, chosen.Cast<StyleItem>(), mode);
                    using (openSource.LockDocument()) return StyleOperations.Import(source, _session.Db, chosen.Cast<StyleItem>(), mode);
                });
            }
            catch (Exception ex) { SetStatus("Could not read the source: " + ex.Message, true); }
            finally { file?.Dispose(); }
        }

        // ---------------- Batch and report ----------------

        private void OpenBatch()
        {
            SaveLayout();
            using (FormStyleBatch f = new FormStyleBatch(_settings, _rule.Clone(), _txtTemplate.Text.Trim()))
                f.ShowDialog(this);
            _settings.Save();
        }

        private void ExportCsv(IEnumerable<StyleItem> items)
        {
            if (items == null) return;
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = Path.GetFileNameWithoutExtension(_doc?.Name ?? "Styles") + "_Styles.csv" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try { StyleOperations.ExportCsv(sfd.FileName, items); SetStatus("Report saved to " + sfd.FileName); }
                catch (IOException ex) { SetStatus("Could not write the report: " + ex.Message, true); }
            }
        }

        private void CopyVisible()
        {
            StringBuilder sb = new StringBuilder("Style\tStatus\tUses\tCategory\tDescription\r\n");
            foreach (StyleItem i in _view) sb.Append($"{i.Name}\t{string.Join(", ", Pills(i))}\t{i.Uses}\t{i.FullCategory}\t{i.Description}\r\n");
            Clipboard.SetText(sb.ToString());
            SetStatus($"{_view.Count} row(s) copied.");
        }

        // =====================================================================================
        // Deck state
        // =====================================================================================

        private void UpdateDeck()
        {
            List<StyleItem> items = _session?.Items ?? new List<StyleItem>();
            int unused = items.Count(i => i.Usage == StyleUsage.Unused && !i.Protected);
            int hidden = items.Count(i => i.Usage == StyleUsage.Hidden && !i.Protected);
            _cardPurge.Info.Text = $"{unused:N0} style(s) are not used by anything." +
                                   (hidden > 0 ? $" {hidden:N0} more have no visible user but AutoCAD still holds them (hidden references); those are kept." : "");
            int dups = items.Count(i => i.DuplicateOf != null && !i.Protected);
            _cardDuplicates.Info.Text = $"{dups:N0} style(s) look like copies, such as \"Name (1)\", \"Copy of Name\" or \"Name_2\". Merging moves their objects to the original and deletes the copy.";

            List<StyleItem> ticked = CheckedItems();
            _cbMergeTarget.Items.Clear();
            if (ticked.Count == 0)
                _cardChecked.Info.Text = "Tick styles in the list (Space toggles the selected rows) to merge or delete them.";
            else
            {
                StyleItem first = ticked[0];
                bool sameKind = ticked.All(t => t.CollectionKey == first.CollectionKey && t.TypeName == first.TypeName);
                _cardChecked.Info.Text = sameKind
                    ? $"{ticked.Count} ticked in {first.FullCategory}. Merge moves their objects to the target and deletes them."
                    : $"{ticked.Count} ticked, of different kinds. Merge needs styles from one collection; delete works on any.";
                if (sameKind)
                {
                    HashSet<ObjectId> tickedIds = new HashSet<ObjectId>(ticked.Select(t => t.Id));
                    foreach (StyleItem candidate in items.Where(i => StyleOperations.CanMerge(first, i)).Where(i => !tickedIds.Contains(i.Id)).OrderBy(i => i.Name))
                        _cbMergeTarget.Items.Add(new Choice($"{candidate.Name}   ({candidate.Uses} uses)", candidate));
                    if (_cbMergeTarget.Items.Count > 0) _cbMergeTarget.SelectedIndex = 0;
                }
            }
        }

        private void SetStatus(string text, bool warn = false)
        {
            _status.Text = text;
            _status.ForeColor = warn ? StyleUi.Danger : StyleUi.Text;
            _status.GetCurrentParent()?.Refresh();
        }

        private void BrowseFile(TextBox target, string title)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Title = title, Filter = "Drawings and templates (*.dwt;*.dwg)|*.dwt;*.dwg|All files|*.*" })
            {
                if (File.Exists(target.Text)) ofd.InitialDirectory = Path.GetDirectoryName(target.Text);
                if (ofd.ShowDialog(this) == DialogResult.OK) target.Text = ofd.FileName;
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "How the Style Manager works\n\n" +
                "1. Every object in the drawing is read to find what references each style: objects, other styles (a label style's text style, " +
                "a profile view style's band set), networks (parts lists) and settings. AutoCAD's own Purge check is combined with it.\n\n" +
                "2. Status:\n   • Used: something references it.\n   • Unused: nothing does; it can be purged.\n" +
                "   • Hidden ref: AutoCAD holds it (usually a default in settings); never deleted.\n   • Protected: current or AutoCAD default; never deleted.\n" +
                "   • Duplicate: looks like a copy of another style, e.g. \"Name (1)\".\n   • Non-standard / Missing: compared with your template.\n\n" +
                "3. Every change shows a preview first, and the drawing is rescanned afterwards. Each change can be undone with UNDO.\n\n" +
                "Tips: click a chip to filter, tick with Space, rename in place with F2, right-click for more, F5 to rescan. " +
                "The window can stay open while you work; it follows the active drawing.",
                "Style Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
