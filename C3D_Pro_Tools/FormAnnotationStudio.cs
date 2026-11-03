using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CLabel = Autodesk.Civil.DatabaseServices.Label;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Annotation Studio: network labels for the plotted sheets, in a modeless window that stays open while you work.
    /// Add labels (choose the plan and profile label styles, then the parts or networks), arrange labels so every sheet reads
    /// cleanly (inside its viewport, clear of parts and each other, dragged close to what they label), rename parts along
    /// their routes, and move between sheets. Each action is one UNDO.
    /// </summary>
    internal sealed class FormAnnotationStudio : Form
    {
        private static FormAnnotationStudio _instance;

        /// <summary>Opens the window (or brings it forward) for the active drawing.</summary>
        public static void ShowTool()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new FormAnnotationStudio();
                AcadUiApp.ShowModelessDialog(null, _instance, false);
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
            if (doc != null && doc != _instance._doc) _instance.BindTo(doc);
        }

        private const int PLabel = 0, PArrange = 1, PNames = 2, PSheets = 3;
        private const int TParts = 0, TNetworks = 1, TSheets = 2;

        /// <summary>What an action works on: parts picked in the drawing, ticked networks, or (for labelling) parts on ticked sheets.</summary>
        private sealed class Target
        {
            public readonly RadioButton Parts = new RadioButton { AutoSize = true }, Networks = new RadioButton { Text = "These networks:", AutoSize = true },
                Sheets = new RadioButton { Text = "Every part shown on these sheets:", AutoSize = true };
            public readonly CheckedListBox NetworkList = NewList(), SheetList = NewList();
            public readonly Label Info = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(8, 9, 0, 0), Text = "Nothing selected yet" };
            /// <summary>The parts picked (labels picked count as the part they label), and labels picked themselves.</summary>
            public readonly List<ObjectId> PartIds = new List<ObjectId>(), LabelIds = new List<ObjectId>();
            public int Mode
            {
                get => Parts.Checked ? TParts : Networks.Checked ? TNetworks : TSheets;
                set => (value == TNetworks ? Networks : value == TSheets && Sheets.Parent != null ? Sheets : Parts).Checked = true;
            }
        }

        private static CheckedListBox NewList() =>
            new CheckedListBox { Height = 120, Width = 480, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };

        private readonly AnnotationSettings _s = AnnotationSettings.Load();
        private Document _doc;
        private List<SheetInfo> _sheets = new List<SheetInfo>();
        private readonly Dictionary<string, ObjectId> _networks = new Dictionary<string, ObjectId>();
        private List<string> _pipeStyles = new List<string>(), _structureStyles = new List<string>();
        private readonly NavList _nav = new NavList();
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Surface };
        private readonly List<Control> _pages = new List<Control>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) };
        private readonly Label _lblDrawing = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Right, Margin = new Padding(3, 10, 3, 3) };
        private bool _busy;

        // Add labels
        private readonly Target _labelTarget = new Target();
        private readonly DataGridView _gridLabels = ToolUi.Grid();
        private readonly ComboBox _cbSet = ToolUi.Combo(260);
        private readonly ComboBox _cbExisting = ToolUi.Combo(300);
        private readonly CheckBox _chkArrangeAfter = ToolUi.Check("Arrange the new labels on their sheets afterwards");
        private static readonly (string text, double ratio)[] Positions = { ("Start", 0), ("Quarter", 0.25), ("Middle", 0.5), ("Three quarters", 0.75), ("End", 1) };
        private const string NoPosition = "—";

        // Arrange
        private readonly Target _arrangeTarget = new Target();
        private readonly CheckedListBox _clbSheets = NewList();
        private readonly TextBox _txtMaxDist = ToolUi.Number(60), _txtGap = ToolUi.Number(1.5), _txtMargin = ToolUi.Number(3);
        private readonly CheckBox _chkPlan = ToolUi.Check("Plan labels"), _chkProfile = ToolUi.Check("Profile labels");
        private readonly CheckBox _chkEveryLabel = ToolUi.Check("Arrange every label (off: only labels with a problem)");
        private readonly CheckBox _chkSlide = ToolUi.Check("Labels sitting on their pipe slide along it instead of being dragged out");
        private readonly CheckBox _chkAvoidText = ToolUi.Check("Keep clear of other text in the drawing");
        private readonly CheckBox _chkAvoidXrefs = ToolUi.Check("Keep clear of the linework and text of loaded xrefs");
        private readonly CheckBox _chkMask = ToolUi.Check("Mask the text of arranged labels (background mask on their label styles)");
        private readonly DataGridView _gridIssues = ToolUi.Grid();
        private readonly List<(ArrangeIssue issue, Box2? box)> _issues = new List<(ArrangeIssue, Box2?)>();

        // Names
        private readonly Target _nameTarget = new Target();
        private readonly TextBox _txtPipeTemplate = new TextBox { Width = 260 }, _txtStructTemplate = new TextBox { Width = 260 };
        private readonly Label _lblPipeExample = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 6) }, _lblStructExample = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
        private readonly CheckBox _chkNamePipes = ToolUi.Check("Rename pipes"), _chkNameStructs = ToolUi.Check("Rename structures");
        private readonly ComboBox _cbSheetSource = ToolUi.Combo(300);
        private readonly TextBox _txtSheetPattern = new TextBox { Width = 200 }, _txtFixedSheet = new TextBox { Width = 120 };
        private readonly ComboBox _cbPipeType = ToolUi.Combo(220), _cbStructType = ToolUi.Combo(220);
        private readonly DataGridView _gridCodes = ToolUi.Grid();
        private readonly RadioButton _rbRouteByRoute = new RadioButton { Text = "Route by route (each parallel route start to end, then the next)", AutoSize = true };
        private readonly RadioButton _rbSetBySet = new RadioButton { Text = "Set by set (across the parallel pipes, then the next set along)", AutoSize = true };
        private readonly TextBox _txtRouteGap = ToolUi.Number(1.0), _txtRouteCenter = ToolUi.Number(0.05);
        private readonly DataGridView _gridNames = ToolUi.Grid();
        private List<RenameRow> _renameRows = new List<RenameRow>();
        private List<ObjectId> _renameNetworks = new List<ObjectId>();

        // Sheets
        private readonly DataGridView _gridSheets = ToolUi.Grid();

        private static readonly string[] TypeSourceText = { "Pipe / structure style", "Part size", "Part family", "Part description" };

        private FormAnnotationStudio()
        {
            Text = "C3D Pro Tools - Annotation Studio";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(900, 620);
            ClientSize = new Size(_s.Width >= 900 ? _s.Width : 1080, _s.Height >= 620 ? _s.Height : 780);

            _nav.Add("Add labels", "Choose plan / profile styles, then parts");
            _nav.Add("Arrange labels", "Clear, readable sheets, network by network");
            _nav.Add("Rename parts", "Templates, per sheet and type, along routes");
            _nav.Add("Sheets", "Jump between layouts, check or arrange");
            _pages.Add(BuildLabelPage());
            _pages.Add(BuildArrangePage());
            _pages.Add(BuildNamesPage());
            _pages.Add(BuildSheetsPage());
            foreach (Control p in _pages) { p.Visible = false; _content.Controls.Add(p); }

            Controls.Add(_content);
            Controls.Add(_nav);
            Controls.Add(ToolUi.Header("Annotation Studio", "Network labels for the plotted sheets: add, arrange, rename, review.",
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp()),
                StyleUi.HeaderButton("Reload drawing", (s, e) => ReadDrawing())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _nav.SelectedChanged += (s, e) => ShowPage();
            _nav.Selected = Math.Max(0, Math.Min(_pages.Count - 1, _s.Section));
            ShowPage();

            AcadCoreApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcadCoreApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            FormClosing += (s, e) => { SaveSettings(); _s.Width = ClientSize.Width; _s.Height = ClientSize.Height; _s.Save(); };
            FormClosed += (s, e) =>
            {
                AcadCoreApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                AcadCoreApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                _instance = null;
            };
        }

        // =====================================================================================
        // Drawing
        // =====================================================================================

        private void BindTo(Document doc)
        {
            _doc = doc;
            foreach (Target t in new[] { _labelTarget, _arrangeTarget, _nameTarget }) { t.PartIds.Clear(); t.LabelIds.Clear(); t.Info.Text = "Nothing selected yet"; }
            // A rename preview names parts of the previous drawing.
            _renameRows = new List<RenameRow>();
            _renameNetworks = new List<ObjectId>();
            _gridNames.Rows.Clear();
            ReadDrawing();
        }

        private void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null && e.Document != _doc && !IsDisposed) BindTo(e.Document);
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != _doc || IsDisposed) return;
            _doc = null;
            _sheets.Clear();
            _networks.Clear();
            FillLists();
            _lblDrawing.Text = "No drawing";
        }

        /// <summary>Reads the sheets, gravity networks and label styles of the bound drawing and refills every list.</summary>
        private void ReadDrawing()
        {
            _sheets = new List<SheetInfo>();
            _networks.Clear();
            if (_doc == null) { FillLists(); return; }
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    _sheets = AnnotSheets.Read(tr, _doc.Database);
                    CivilDocument cdoc = CivilDocument.GetCivilDocument(_doc.Database);
                    foreach (ObjectId id in cdoc.GetPipeNetworkIds())
                        if (tr.GetObject(id, OpenMode.ForRead) is Network n) _networks[n.Name] = id;
                    _pipeStyles = AnnotLabels.StyleNames(tr, cdoc, LabelKind.PlanPipe);
                    _structureStyles = AnnotLabels.StyleNames(tr, cdoc, LabelKind.PlanStructure);
                }
            }
            catch (Exception ex) { SetStatus("Could not read the drawing: " + ex.Message); }
            _lblDrawing.Text = System.IO.Path.GetFileName(_doc.Name);
            FillLists();
            RefreshLabelRows();
            SetStatus($"{_sheets.Count} sheet(s) with {_sheets.Sum(s => s.Viewports.Count)} viewport(s), {_networks.Count} gravity network(s).");
        }

        private static void Refill(CheckedListBox box, IEnumerable<string> items)
        {
            HashSet<string> was = new HashSet<string>(box.CheckedItems.Cast<object>().Select(o => o.ToString()));
            box.BeginUpdate();
            box.Items.Clear();
            foreach (string i in items) box.Items.Add(i, was.Contains(i));
            box.EndUpdate();
        }

        private void FillLists()
        {
            List<string> nets = _networks.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> layouts = _sheets.Where(s => s.Viewports.Count > 0).Select(s => s.Layout).ToList();
            foreach (Target t in new[] { _labelTarget, _arrangeTarget, _nameTarget })
            {
                Refill(t.NetworkList, nets);
                Refill(t.SheetList, layouts);
            }
            Refill(_clbSheets, layouts);
            FillSheetGrid();
        }

        private string CurrentLayout()
        {
            try { return LayoutManager.Current.CurrentLayout; } catch (Exception) { return ""; }
        }

        private List<SheetInfo> Checked(CheckedListBox box) =>
            box.CheckedItems.Cast<object>().Select(o => _sheets.FirstOrDefault(s => s.Layout == o.ToString())).Where(s => s != null).ToList();

        private List<ObjectId> CheckedNetworks(CheckedListBox box) =>
            box.CheckedItems.Cast<object>().Select(o => _networks.TryGetValue(o.ToString(), out ObjectId id) ? id : ObjectId.Null).Where(id => !id.IsNull).ToList();

        private bool Ready()
        {
            if (_busy) return false;
            Document active = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (_doc != null && _doc == active) return true;
            if (active != null) { BindTo(active); return _doc != null; }
            MessageBox.Show(this, "Open a drawing first.", Text);
            return false;
        }

        /// <summary>
        /// Objects to work on: what is already selected in the drawing, else a selection prompt (run in the drawing's command
        /// context, so it works from this modeless window). Null when cancelled.
        /// </summary>
        private async Task<List<ObjectId>> PickAsync(string prompt)
        {
            if (!Ready()) return null;
            Editor ed = _doc.Editor;
            try
            {
                PromptSelectionResult implied = ed.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) return implied.Value.GetObjectIds().ToList();
            }
            catch (Exception) { }
            List<ObjectId> ids = null;
            _busy = true;
            try
            {
                try { Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView(); } catch (Exception) { }
                await AcadCoreApp.DocumentManager.ExecuteInCommandContextAsync(_ =>
                {
                    PromptSelectionResult r = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = prompt });
                    if (r.Status == PromptStatus.OK) ids = r.Value.GetObjectIds().ToList();
                    return Task.CompletedTask;
                }, null);
            }
            catch (Exception ex) { SetStatus("Could not select in the drawing: " + ex.Message); }
            finally { _busy = false; }
            try { Activate(); } catch (Exception) { }
            return ids;
        }

        /// <summary>Fills the target from a selection: pipes and structures as they are, labels as the part they label.</summary>
        private async void PickTarget(Target t, string prompt)
        {
            List<ObjectId> ids = await PickAsync(prompt);
            if (ids == null) return;
            t.PartIds.Clear();
            t.LabelIds.Clear();
            int pipes = 0, structures = 0;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                Dictionary<ObjectId, LabelInfo> labels = AnnotLabels.Scan(tr, _doc.Database).ToDictionary(l => l.Id);
                HashSet<ObjectId> parts = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    if (labels.TryGetValue(id, out LabelInfo l)) { t.LabelIds.Add(id); if (!l.PartId.IsNull) parts.Add(l.PartId); continue; }
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (o is Pipe || o is Structure) parts.Add(id);
                }
                foreach (ObjectId id in parts) { if (tr.GetObject(id, OpenMode.ForRead) is Pipe) pipes++; else structures++; }
                t.PartIds.AddRange(parts);
            }
            t.Info.Text = $"{pipes} pipe(s), {structures} structure(s)" + (t.LabelIds.Count > 0 ? $", {t.LabelIds.Count} label(s)" : "") + " selected";
            t.Mode = TParts;
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = StyleUi.Panel, Padding = new Padding(12, 4, 12, 6) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            footer.Controls.Add(_lblStatus, 0, 0);
            footer.Controls.Add(_lblDrawing, 1, 0);
            return footer;
        }

        private void ShowPage()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _nav.Selected;
            if (_nav.Selected == PSheets) FillSheetGrid();
        }

        private static FlowLayoutPanel Buttons(params Control[] buttons)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 2), Padding = new Padding(0) };
            foreach (Control b in buttons) row.Controls.Add(b);
            return row;
        }

        private static FlowLayoutPanel ListWithButtons(CheckedListBox box, params Control[] extra)
        {
            FlowLayoutPanel f = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(20, 0, 0, 4) };
            f.Controls.Add(box);
            FlowLayoutPanel buttons = ToolUi.Inline(
                StyleUi.Secondary("All", (s, e) => { for (int i = 0; i < box.Items.Count; i++) box.SetItemChecked(i, true); }),
                StyleUi.Secondary("None", (s, e) => { for (int i = 0; i < box.Items.Count; i++) box.SetItemChecked(i, false); }));
            foreach (Control c in extra) buttons.Controls.Add(c);
            f.Controls.Add(buttons);
            return f;
        }

        /// <summary>The "what to work on" rows of a card: selected parts (with a Select button), networks, and optionally sheets.</summary>
        private void AddTarget(TableLayoutPanel card, Target t, string partsText, string prompt, bool sheets)
        {
            t.Parts.Text = partsText;
            ToolUi.Wide(card, ToolUi.Inline(t.Parts, StyleUi.Secondary("Select in drawing…", (s, e) => PickTarget(t, prompt)), t.Info));
            ToolUi.Wide(card, t.Networks);
            ToolUi.Wide(card, ListWithButtons(t.NetworkList));
            t.NetworkList.ItemCheck += (s, e) => { if (t.Mode == TParts && !_busy) BeginInvoke((Action)(() => t.Mode = TNetworks)); };
            if (sheets)
            {
                ToolUi.Wide(card, t.Sheets);
                ToolUi.Wide(card, ListWithButtons(t.SheetList));
                t.SheetList.ItemCheck += (s, e) => BeginInvoke((Action)(() => t.Mode = TSheets));
                ToolUi.Exclusive(t.Parts, t.Networks, t.Sheets);
            }
            else ToolUi.Exclusive(t.Parts, t.Networks);
        }

        // ---------------- Add labels ----------------

        private Control BuildLabelPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();

            TableLayoutPanel target = ToolUi.Card("1. Parts to label");
            AddTarget(target, _labelTarget, "Parts selected in the drawing (pipes, structures or their labels)", "\nSelect the pipes and structures to label: ", true);
            ToolUi.Add(stack, target);

            TableLayoutPanel table = ToolUi.Card("2. Labels to add",
                "One row per label: plan labels go on the part, profile labels into every profile view that shows it. A pipe can have several " +
                "(one at each end, for example). Or fill the table from a part you labelled by hand. Save a table you use often as a label set.");
            _gridLabels.Dock = DockStyle.None;
            _gridLabels.Height = 190;
            _gridLabels.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _gridLabels.EditMode = DataGridViewEditMode.EditOnEnter;
            DataGridViewComboBoxColumn kind = new DataGridViewComboBoxColumn { Name = "Kind", HeaderText = "Label", FillWeight = 60, FlatStyle = FlatStyle.Flat };
            foreach (LabelKind k in new[] { LabelKind.PlanPipe, LabelKind.PlanStructure, LabelKind.ProfilePipe, LabelKind.ProfileStructure }) kind.Items.Add(LabelSetItem.KindText(k));
            _gridLabels.Columns.Add(kind);
            _gridLabels.Columns.Add(new DataGridViewComboBoxColumn { Name = "Style", HeaderText = "Style", FillWeight = 150, FlatStyle = FlatStyle.Flat });
            _gridLabels.Columns.Add(new DataGridViewComboBoxColumn { Name = "Position", HeaderText = "Along the pipe", FillWeight = 50, FlatStyle = FlatStyle.Flat });
            _gridLabels.CurrentCellDirtyStateChanged += (s, e) => { if (_gridLabels.IsCurrentCellDirty) _gridLabels.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _gridLabels.CellValueChanged += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) SetRowKind(_gridLabels.Rows[e.RowIndex]); };
            ToolUi.Wide(table, _gridLabels);
            ToolUi.Wide(table, Buttons(
                StyleUi.Secondary("Add a label", (s, e) => AddLabelRow(new LabelSetItem { Kind = LabelKind.PlanPipe, Ratio = 0.5 })),
                StyleUi.Secondary("Remove", (s, e) => { foreach (DataGridViewRow r in _gridLabels.SelectedRows.Cast<DataGridViewRow>().ToList()) _gridLabels.Rows.Remove(r); }),
                StyleUi.Secondary("Fill from a labelled part…", (s, e) => CaptureRows())));
            ToolUi.Field(table, "Label sets", ToolUi.Inline(_cbSet,
                StyleUi.Secondary("Load", (s, e) => LoadSet()),
                StyleUi.Secondary("Save as…", (s, e) => SaveSet()),
                StyleUi.Secondary("Delete", (s, e) => DeleteSet())));
            ToolUi.Add(stack, table);

            TableLayoutPanel run = ToolUi.Card("3. Add");
            ToolUi.Field(run, "Parts already labelled", _cbExisting, _tips,
                "Add missing: a part keeps its labels and only gets those of the table it lacks (same kind and style).\n" +
                "Skip: parts with any label are left alone.\nReplace: their labels of the kinds in the table are removed first.");
            _cbExisting.Items.AddRange(new object[] { "Add only the labels they are missing", "Skip parts that have labels", "Replace their labels" });
            ToolUi.Wide(run, _chkArrangeAfter);
            ToolUi.Wide(run, Buttons(StyleUi.Primary("Add labels", (s, e) => AddLabels())));
            ToolUi.Add(stack, run);
            return host;
        }

        // ---------------- Arrange ----------------

        private Control BuildArrangePage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();

            TableLayoutPanel target = ToolUi.Card("Labels to arrange",
                "The labels of these networks (or of the selected parts, or the selected labels) are arranged; all other labels stay where they are " +
                "and are kept clear of.");
            AddTarget(target, _arrangeTarget, "Selected in the drawing (labels, or the pipes and structures they label)", "\nSelect labels, pipes or structures: ", false);
            ToolUi.Add(stack, target);

            TableLayoutPanel sheets = ToolUi.Card("Sheets",
                "Labels are placed by how they appear on each sheet: inside the viewport (and their profile view), clear of pipes, structures, " +
                "text and each other, in rows beside the route where possible, otherwise at the nearest free spot. Moved labels are dragged, with a leader to their part.");
            ToolUi.Wide(sheets, ListWithButtons(_clbSheets, StyleUi.Secondary("Current sheet", (s, e) =>
            {
                string cur = CurrentLayout();
                for (int i = 0; i < _clbSheets.Items.Count; i++) _clbSheets.SetItemChecked(i, _clbSheets.Items[i].ToString() == cur);
            })));
            ToolUi.Add(stack, sheets);

            TableLayoutPanel place = ToolUi.Card("Placement (paper distances)");
            ToolUi.Field(place, "Furthest from its part", ToolUi.WithUnit(_txtMaxDist, "mm"), _tips, "A label is never moved further than this from the point it labels.");
            ToolUi.Field(place, "Space around labels", ToolUi.WithUnit(_txtGap, "mm"));
            ToolUi.Field(place, "Keep from viewport edge", ToolUi.WithUnit(_txtMargin, "mm"));
            ToolUi.Wide(place, ToolUi.Inline(_chkPlan, _chkProfile));
            ToolUi.Wide(place, _chkEveryLabel);
            ToolUi.Wide(place, _chkSlide);
            ToolUi.Wide(place, _chkAvoidText);
            ToolUi.Wide(place, _chkAvoidXrefs);
            ToolUi.Wide(place, _chkMask, _tips, "Civil 3D masks label text through the label style: every label of those styles is masked, here and elsewhere. " +
                                                "Data shortcut parts are networks in this drawing and are always avoided.");
            ToolUi.Wide(place, ToolUi.Note("Labels you placed by hand can be locked: they are never moved. Select them in the drawing, then Lock."));
            ToolUi.Wide(place, Buttons(
                StyleUi.Secondary("Lock selected labels…", (s, e) => LockSelected(true)),
                StyleUi.Secondary("Unlock selected labels…", (s, e) => LockSelected(false))));
            ToolUi.Wide(place, Buttons(
                StyleUi.Secondary("Mask selected labels' text…", (s, e) => MaskSelected(true)),
                StyleUi.Secondary("Unmask…", (s, e) => MaskSelected(false))));
            ToolUi.Wide(place, Buttons(
                StyleUi.Secondary("Check", (s, e) => RunArrange(Checked(_clbSheets), true)),
                StyleUi.Primary("Arrange labels", (s, e) => RunArrange(Checked(_clbSheets), false))));
            ToolUi.Add(stack, place);

            TableLayoutPanel results = ToolUi.Card("Results", "Every label that has a problem or could not be handled. Double-click a row to go to it on its sheet.");
            _gridIssues.Dock = DockStyle.None;
            _gridIssues.Height = 220;
            _gridIssues.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _gridIssues.ReadOnly = true;
            _gridIssues.Columns.Add("Sheet", "Sheet");
            _gridIssues.Columns.Add("Issue", "Issue");
            _gridIssues.Columns[0].FillWeight = 45;
            _gridIssues.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) GoToIssue(e.RowIndex); };
            ToolUi.Wide(results, _gridIssues);
            ToolUi.Add(stack, results);
            return host;
        }

        // ---------------- Names ----------------

        private Control BuildNamesPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();

            TableLayoutPanel target = ToolUi.Card("Parts to rename",
                "Each network is split into routes (parallel pipes kept apart) and numbered along them. Numbering restarts for every distinct name " +
                "around the number, so each sheet and type counts from 1. Data shortcut references cannot be renamed and are listed as such.");
            AddTarget(target, _nameTarget, "Parts selected in the drawing (numbered among themselves)", "\nSelect the pipes and structures to rename: ", false);
            ToolUi.Add(stack, target);

            TableLayoutPanel names = ToolUi.Card("Pipe names",
                "Tokens: {sheet}, {type}, {network}, {n}. Write {n:2} (or {n:00}) for two digits. Everything else is written as it is, so put any prefix " +
                "or suffix around the tokens. A token with no value takes the separator before it away.");
            ToolUi.Wide(names, _chkNamePipes);
            ToolUi.Field(names, "Template", _txtPipeTemplate);
            ToolUi.Wide(names, _lblPipeExample);
            ToolUi.Field(names, "Sheet number from", _cbSheetSource);
            _cbSheetSource.Items.AddRange(new object[] { "The network name (last number in it)", "Text on the sheet the part is shown on", "This text:" });
            ToolUi.Field(names, "Sheet text pattern", _txtSheetPattern, _tips, "A regular expression; the first group is the number. The default finds \"SHEET-124-C\" → 124.");
            ToolUi.Field(names, "Fixed sheet", _txtFixedSheet);
            ToolUi.Field(names, "{type} from", _cbPipeType);
            _cbPipeType.Items.AddRange(TypeSourceText);
            _gridCodes.Dock = DockStyle.None;
            _gridCodes.Height = 150;
            _gridCodes.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _gridCodes.Columns.Add("Value", "Value in the drawing");
            _gridCodes.Columns.Add("Code", "{type} written in names");
            _gridCodes.Columns[0].ReadOnly = true;
            _gridCodes.Columns[0].FillWeight = 160;
            _gridCodes.CellValueChanged += (s, e) => UpdateExamples();
            ToolUi.Wide(names, ToolUi.Note("Type codes: every value the parts have, and the code written for it. Edit a code to change it; codes are remembered."));
            ToolUi.Wide(names, Buttons(StyleUi.Secondary("Read types", (s, e) => ReadTypes())));
            ToolUi.Wide(names, _gridCodes);
            _cbSheetSource.SelectedIndexChanged += (s, e) =>
            {
                _txtSheetPattern.Enabled = _cbSheetSource.SelectedIndex == (int)SheetSource.SheetText;
                _txtFixedSheet.Enabled = _cbSheetSource.SelectedIndex == (int)SheetSource.Fixed;
                UpdateExamples();
            };
            _txtPipeTemplate.TextChanged += (s, e) => UpdateExamples();
            _txtFixedSheet.TextChanged += (s, e) => UpdateExamples();
            ToolUi.Add(stack, names);

            TableLayoutPanel structs = ToolUi.Card("Structure names");
            ToolUi.Wide(structs, _chkNameStructs);
            ToolUi.Field(structs, "Template", _txtStructTemplate);
            ToolUi.Wide(structs, _lblStructExample);
            ToolUi.Field(structs, "{type} from", _cbStructType);
            _cbStructType.Items.AddRange(TypeSourceText);
            _txtStructTemplate.TextChanged += (s, e) => UpdateExamples();
            _chkNamePipes.CheckedChanged += (s, e) => UpdateExamples();
            _chkNameStructs.CheckedChanged += (s, e) => UpdateExamples();
            ToolUi.Add(stack, structs);

            TableLayoutPanel order = ToolUi.Card("Parallel pipes");
            ToolUi.Wide(order, _rbRouteByRoute);
            ToolUi.Wide(order, _rbSetBySet);
            ToolUi.Field(order, "Join pipe ends closer than", ToolUi.WithUnit(_txtRouteGap, "m"));
            ToolUi.Field(order, "Same centre within", ToolUi.WithUnit(_txtRouteCenter, "m"), _tips, "Pipe ends this close are the same route node.");
            ToolUi.Add(stack, order);

            TableLayoutPanel preview = ToolUi.Card("Preview",
                "Untick a row to leave that part as it is, or type another new name. Names another part already uses are shown in red and not applied. " +
                "Double-click a row to zoom to the part.");
            _gridNames.Dock = DockStyle.None;
            _gridNames.Height = 280;
            _gridNames.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _gridNames.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "", FillWeight = 18 });
            foreach (string c in new[] { "Network", "Sheet", "Current name", "New name", "Note" }) _gridNames.Columns.Add(c, c);
            for (int i = 1; i < _gridNames.Columns.Count; i++) _gridNames.Columns[i].ReadOnly = i != 4;
            _gridNames.CellEndEdit += (s, e) => { if (e.ColumnIndex == 4 && e.RowIndex >= 0) NameEdited(e.RowIndex); };
            _gridNames.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0 && e.ColumnIndex != 4 && e.RowIndex < _renameRows.Count) ZoomToPart(_renameRows[e.RowIndex].Id); };
            ToolUi.Wide(preview, Buttons(StyleUi.Secondary("Preview names", (s, e) => PreviewNames()), StyleUi.Primary("Rename", (s, e) => ApplyNames())));
            ToolUi.Wide(preview, _gridNames);
            ToolUi.Add(stack, preview);
            return host;
        }

        // ---------------- Sheets ----------------

        private Control BuildSheetsPage()
        {
            TableLayoutPanel page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12, 10, 12, 8), BackColor = StyleUi.Surface };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(Buttons(
                StyleUi.Secondary("◀ Previous", (s, e) => Step(-1)),
                StyleUi.Secondary("Next ▶", (s, e) => Step(1)),
                StyleUi.Primary("Go to sheet", (s, e) => GoToSelectedSheet()),
                StyleUi.Secondary("Check this sheet", (s, e) => ArrangeSelectedSheet(true)),
                StyleUi.Secondary("Arrange this sheet", (s, e) => ArrangeSelectedSheet(false)),
                StyleUi.Secondary("Arrange all sheets", (s, e) => RunArrange(_sheets.Where(x => x.Viewports.Count > 0).ToList(), false)),
                StyleUi.Primary("Sheet Manager…", (s, e) => FormSheetManager.ShowTool())), 0, 0);
            page.Controls.Add(ToolUi.Note("Check and arrange work on the labels chosen on the Arrange labels page, with its placement options. " +
                                          "Rename, copy, create, revise and publish sheets in the Sheet Manager."), 0, 1);
            _gridSheets.ReadOnly = true;
            _gridSheets.MultiSelect = false;
            foreach (string c in new[] { "Layout", "Sheet no.", "Viewports" }) _gridSheets.Columns.Add(c, c);
            _gridSheets.Columns[0].FillWeight = 200;
            _gridSheets.Columns[1].FillWeight = 50;
            _gridSheets.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) GoToSelectedSheet(); };
            page.Controls.Add(_gridSheets, 0, 2);
            return page;
        }

        private void FillSheetGrid()
        {
            string cur = CurrentLayout();
            _gridSheets.Rows.Clear();
            foreach (SheetInfo s in _sheets)
            {
                int row = _gridSheets.Rows.Add(s.Layout, s.SheetNumber(_s.Naming.SheetPattern), string.Join(", ", s.Viewports.Select(v => v.ScaleText + (Math.Abs(v.Twist) > 1e-6 ? " rotated" : ""))));
                _gridSheets.Rows[row].Tag = s;
                if (s.Layout == cur) { _gridSheets.Rows[row].Selected = true; _gridSheets.CurrentCell = _gridSheets.Rows[row].Cells[0]; }
            }
        }

        // =====================================================================================
        // Settings
        // =====================================================================================

        private void ApplySettings()
        {
            FillSets(_s.LabelSet);
            foreach (LabelSetItem item in _s.LabelRows) AddLabelRow(item);
            _labelTarget.Mode = _s.LabelTarget;
            _cbExisting.SelectedIndex = Math.Max(0, Math.Min(2, _s.ExistingMode));
            _chkArrangeAfter.Checked = _s.ArrangeAfterLabelling;

            _arrangeTarget.Mode = _s.ArrangeTarget;
            _txtMaxDist.Text = ToolUi.Format(_s.MaxDistance);
            _txtGap.Text = ToolUi.Format(_s.Gap);
            _txtMargin.Text = ToolUi.Format(_s.Margin);
            _chkPlan.Checked = _s.Plan;
            _chkProfile.Checked = _s.Profile;
            _chkEveryLabel.Checked = _s.ArrangeEveryLabel;
            _chkSlide.Checked = _s.SlideOnPipe;
            _chkAvoidText.Checked = _s.AvoidModelText;
            _chkAvoidXrefs.Checked = _s.AvoidXrefs;
            _chkMask.Checked = _s.MaskArranged;

            NamingOptions n = _s.Naming;
            _nameTarget.Mode = _s.RenameTarget;
            _txtPipeTemplate.Text = n.PipeTemplate;
            _txtStructTemplate.Text = n.StructureTemplate;
            _chkNamePipes.Checked = n.Pipes;
            _chkNameStructs.Checked = n.Structures;
            _cbSheetSource.SelectedIndex = (int)n.Sheet;
            _txtSheetPattern.Text = n.SheetPattern;
            _txtFixedSheet.Text = n.FixedSheet;
            _cbPipeType.SelectedIndex = (int)n.PipeType;
            _cbStructType.SelectedIndex = (int)n.StructureType;
            (n.Parallel == ParallelOrder.SetBySet ? _rbSetBySet : _rbRouteByRoute).Checked = true;
            _txtRouteGap.Text = ToolUi.Format(_s.RouteGap);
            _txtRouteCenter.Text = ToolUi.Format(_s.RouteCenterTolerance);
            foreach (KeyValuePair<string, string> kv in n.TypeCodes) _gridCodes.Rows.Add(kv.Key, kv.Value);
            UpdateExamples();
        }

        private void SaveSettings()
        {
            _s.Section = _nav.Selected;
            _s.LabelRows = ReadLabelRows();
            _s.LabelSet = _cbSet.SelectedItem as string;
            _s.LabelTarget = _labelTarget.Mode;
            _s.ExistingMode = _cbExisting.SelectedIndex;
            _s.ArrangeAfterLabelling = _chkArrangeAfter.Checked;
            _s.ArrangeTarget = _arrangeTarget.Mode;
            _s.RenameTarget = _nameTarget.Mode;
            ReadArrangeOptions(out _, quiet: true);
            ReadNamingOptions(quiet: true);
        }

        private bool ReadArrangeOptions(out ArrangeOptions o, bool quiet = false)
        {
            o = null;
            if (!ToolUi.TryNumber(_txtMaxDist, out double max) || !ToolUi.TryNumber(_txtGap, out double gap) || !ToolUi.TryNumber(_txtMargin, out double margin) || max <= 0 || gap < 0 || margin < 0)
            {
                if (!quiet) MessageBox.Show(this, "Enter a positive distance and non-negative space and edge values (Placement).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            _s.MaxDistance = max; _s.Gap = gap; _s.Margin = margin;
            _s.Plan = _chkPlan.Checked; _s.Profile = _chkProfile.Checked;
            _s.ArrangeEveryLabel = _chkEveryLabel.Checked; _s.SlideOnPipe = _chkSlide.Checked; _s.AvoidModelText = _chkAvoidText.Checked;
            _s.AvoidXrefs = _chkAvoidXrefs.Checked; _s.MaskArranged = _chkMask.Checked;
            o = new ArrangeOptions
            {
                MaxDistance = max, Gap = gap, Margin = margin, Plan = _s.Plan, Profile = _s.Profile,
                RearrangeAll = _s.ArrangeEveryLabel, SlideOnPipe = _s.SlideOnPipe, AvoidModelText = _s.AvoidModelText, AvoidXrefs = _s.AvoidXrefs
            };
            return true;
        }

        private bool ReadNamingOptions(bool quiet = false)
        {
            NamingOptions n = _s.Naming;
            n.PipeTemplate = _txtPipeTemplate.Text.Trim();
            n.StructureTemplate = _txtStructTemplate.Text.Trim();
            n.Pipes = _chkNamePipes.Checked;
            n.Structures = _chkNameStructs.Checked;
            n.Sheet = (SheetSource)Math.Max(0, _cbSheetSource.SelectedIndex);
            n.SheetPattern = _txtSheetPattern.Text.Trim();
            n.FixedSheet = _txtFixedSheet.Text.Trim();
            n.PipeType = (TypeSource)Math.Max(0, _cbPipeType.SelectedIndex);
            n.StructureType = (TypeSource)Math.Max(0, _cbStructType.SelectedIndex);
            n.Parallel = _rbSetBySet.Checked ? ParallelOrder.SetBySet : ParallelOrder.RouteByRoute;
            foreach (DataGridViewRow r in _gridCodes.Rows)
            {
                string value = r.Cells[0].Value as string;
                if (!string.IsNullOrEmpty(value)) n.TypeCodes[value] = (r.Cells[1].Value as string ?? "").Trim();
            }
            if (ToolUi.TryNumber(_txtRouteGap, out double g) && g > 0) _s.RouteGap = g;
            else if (!quiet) { MessageBox.Show(this, "Enter a positive gap for joining pipe ends.", Text); return false; }
            if (ToolUi.TryNumber(_txtRouteCenter, out double c) && c > 0) _s.RouteCenterTolerance = c;
            else if (!quiet) { MessageBox.Show(this, "Enter a positive centre tolerance.", Text); return false; }
            if (quiet) return true;
            if (n.Pipes && NameTemplate.Parse(n.PipeTemplate).Errors.FirstOrDefault() is string pe) { MessageBox.Show(this, "Pipe template: " + pe, Text); return false; }
            if (n.Structures && NameTemplate.Parse(n.StructureTemplate).Errors.FirstOrDefault() is string se) { MessageBox.Show(this, "Structure template: " + se, Text); return false; }
            if (n.Sheet == SheetSource.SheetText)
            {
                try { System.Text.RegularExpressions.Regex.Match("", n.SheetPattern); }
                catch (ArgumentException) { MessageBox.Show(this, "The sheet text pattern is not a valid regular expression.", Text); return false; }
            }
            return true;
        }

        private void SetStatus(string text) => _lblStatus.Text = text;

        // =====================================================================================
        // Add labels
        // =====================================================================================

        private static LabelKind KindOf(object text)
        {
            foreach (LabelKind k in new[] { LabelKind.PlanPipe, LabelKind.PlanStructure, LabelKind.ProfilePipe, LabelKind.ProfileStructure })
                if (LabelSetItem.KindText(k) == text as string) return k;
            return LabelKind.PlanPipe;
        }

        private static bool IsPipeKind(LabelKind k) => k == LabelKind.PlanPipe || k == LabelKind.ProfilePipe;

        private static string PositionText(double ratio)
        {
            foreach ((string text, double r) in Positions) if (Math.Abs(r - ratio) < 0.01) return text;
            return $"At {ratio:0.##}";
        }

        private static double RatioOf(object text)
        {
            string s = text as string ?? "";
            foreach ((string t, double r) in Positions) if (t == s) return r;
            return s.StartsWith("At ") && double.TryParse(s.Substring(3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.5;
        }

        private void AddLabelRow(LabelSetItem item)
        {
            int i = _gridLabels.Rows.Add();
            DataGridViewRow row = _gridLabels.Rows[i];
            row.Cells[0].Value = LabelSetItem.KindText(item.Kind);
            SetRowKind(row, item.Style, item.Ratio);
        }

        /// <summary>Gives a row the style list and positions of its kind, keeping the chosen style when the list has it.</summary>
        private void SetRowKind(DataGridViewRow row, string style = null, double ratio = double.NaN)
        {
            LabelKind kind = KindOf(row.Cells[0].Value);
            DataGridViewComboBoxCell styleCell = (DataGridViewComboBoxCell)row.Cells[1];
            string keep = style ?? styleCell.Value as string;
            List<string> styles = IsPipeKind(kind) ? _pipeStyles : _structureStyles;
            styleCell.Items.Clear();
            foreach (string s in styles) styleCell.Items.Add(s);
            if (!string.IsNullOrEmpty(keep) && !styles.Contains(keep)) styleCell.Items.Add(keep);
            styleCell.Value = !string.IsNullOrEmpty(keep) ? keep : styles.FirstOrDefault();

            DataGridViewComboBoxCell posCell = (DataGridViewComboBoxCell)row.Cells[2];
            posCell.Items.Clear();
            if (IsPipeKind(kind))
            {
                double r = double.IsNaN(ratio) ? RatioOf(posCell.Value) : ratio;
                foreach ((string text, double _) in Positions) posCell.Items.Add(text);
                string current = PositionText(r);
                if (!posCell.Items.Contains(current)) posCell.Items.Add(current);
                posCell.Value = current;
                posCell.ReadOnly = false;
            }
            else
            {
                posCell.Items.Add(NoPosition);
                posCell.Value = NoPosition;
                posCell.ReadOnly = true;
            }
        }

        /// <summary>After the drawing (and so its style lists) changed: every row keeps its style when it still exists.</summary>
        private void RefreshLabelRows()
        {
            foreach (DataGridViewRow row in _gridLabels.Rows) SetRowKind(row);
        }

        private List<LabelSetItem> ReadLabelRows()
        {
            List<LabelSetItem> items = new List<LabelSetItem>();
            foreach (DataGridViewRow row in _gridLabels.Rows)
            {
                string style = row.Cells[1].Value as string;
                if (string.IsNullOrEmpty(style)) continue;
                LabelKind kind = KindOf(row.Cells[0].Value);
                items.Add(new LabelSetItem { Kind = kind, Style = style, Ratio = IsPipeKind(kind) ? RatioOf(row.Cells[2].Value) : 0.5 });
            }
            return items;
        }

        private void SetRows(IEnumerable<LabelSetItem> items)
        {
            _gridLabels.Rows.Clear();
            foreach (LabelSetItem i in items) AddLabelRow(i);
        }

        private void FillSets(string select)
        {
            _cbSet.BeginUpdate();
            _cbSet.Items.Clear();
            foreach (LabelSet set in _s.LabelSets) _cbSet.Items.Add(set.Name);
            _cbSet.EndUpdate();
            int i = _s.LabelSets.FindIndex(x => x.Name == select);
            _cbSet.SelectedIndex = i >= 0 ? i : (_cbSet.Items.Count > 0 ? 0 : -1);
        }

        private void LoadSet()
        {
            LabelSet set = _s.LabelSets.FirstOrDefault(x => x.Name == _cbSet.SelectedItem as string);
            if (set != null) { SetRows(set.Items); SetStatus($"Loaded \"{set.Name}\" ({set.Items.Count} label(s))."); }
        }

        private void SaveSet()
        {
            List<LabelSetItem> items = ReadLabelRows();
            if (items.Count == 0) { MessageBox.Show(this, "Add labels to the table first.", Text); return; }
            string name = FormPartsLists.AskText(this, "Label set", "Name of the label set:", _cbSet.SelectedItem as string ?? "Label set");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            _s.LabelSets.RemoveAll(x => x.Name == name);
            _s.LabelSets.Add(new LabelSet { Name = name, Items = items });
            _s.Save();
            FillSets(name);
            SetStatus($"Saved \"{name}\".");
        }

        private void DeleteSet()
        {
            string name = _cbSet.SelectedItem as string;
            if (name == null) return;
            if (MessageBox.Show(this, $"Delete the label set \"{name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _s.LabelSets.RemoveAll(x => x.Name == name);
            _s.Save();
            FillSets(null);
        }

        /// <summary>Fills the table with the labels of a part (or parts) labelled by hand.</summary>
        private async void CaptureRows()
        {
            List<ObjectId> ids = await PickAsync("\nSelect a labelled pipe and structure, or their labels: ");
            if (ids == null) return;
            LabelSet set;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                List<LabelInfo> labels = AnnotLabels.Scan(tr, _doc.Database);
                Dictionary<ObjectId, LabelInfo> byId = labels.ToDictionary(l => l.Id);
                HashSet<ObjectId> parts = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    if (byId.TryGetValue(id, out LabelInfo l)) { if (!l.PartId.IsNull) parts.Add(l.PartId); }
                    else if (tr.GetObject(id, OpenMode.ForRead) is Part) parts.Add(id);
                }
                set = AnnotLabels.Capture(labels, parts, tr);
            }
            if (set.Items.Count == 0) { MessageBox.Show(this, "The selected parts have no gravity network labels to copy.", Text); return; }
            SetRows(set.Items);
            SetStatus($"The table now has the {set.Items.Count} label(s) of the selected part(s).");
        }

        /// <summary>Pipes and structures of the networks, or those shown on the sheets.</summary>
        private static List<ObjectId> PartsOf(Transaction tr, IEnumerable<ObjectId> networkIds, List<SheetInfo> onSheets)
        {
            List<ObjectId> parts = new List<ObjectId>();
            foreach (ObjectId netId in networkIds)
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds())
                {
                    if (onSheets == null) { parts.Add(id); continue; }
                    Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                    Point3d mid = p.StartPoint + (p.EndPoint - p.StartPoint) / 2;
                    if (onSheets.Any(s => s.Viewports.Any(v => v.Sees(mid) || v.Sees(p.StartPoint) || v.Sees(p.EndPoint)))) parts.Add(id);
                }
                foreach (ObjectId id in net.GetStructureIds())
                {
                    if (onSheets == null) { parts.Add(id); continue; }
                    Structure st = (Structure)tr.GetObject(id, OpenMode.ForRead);
                    if (onSheets.Any(s => s.Viewports.Any(v => v.Sees(st.Location)))) parts.Add(id);
                }
            }
            return parts;
        }

        private void AddLabels()
        {
            if (!Ready()) return;
            LabelSet set = new LabelSet { Name = "Table", Items = ReadLabelRows() };
            if (set.Items.Count == 0) { MessageBox.Show(this, "Add at least one label to the table (step 2).", Text); return; }
            Target t = _labelTarget;
            if (t.Mode == TParts && t.PartIds.Count == 0) { MessageBox.Show(this, "Select the parts to label first (Select in drawing…).", Text); return; }
            List<SheetInfo> sheets = t.Mode == TSheets ? Checked(t.SheetList) : null;
            List<ObjectId> nets = t.Mode == TNetworks ? CheckedNetworks(t.NetworkList) : null;
            if (t.Mode == TSheets && sheets.Count == 0) { MessageBox.Show(this, "Tick the sheets whose parts to label.", Text); return; }
            if (t.Mode == TNetworks && nets.Count == 0) { MessageBox.Show(this, "Tick the networks to label.", Text); return; }

            AnnotLabels.ApplyResult r;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument cdoc = CivilDocument.GetCivilDocument(_doc.Database);
                    List<LabelInfo> labels = AnnotLabels.Scan(tr, _doc.Database);
                    List<ObjectId> parts = t.Mode == TParts ? t.PartIds.ToList() : PartsOf(tr, nets ?? cdoc.GetPipeNetworkIds().Cast<ObjectId>(), sheets);
                    r = AnnotLabels.Apply(tr, cdoc, set, parts, labels, (ExistingLabels)Math.Max(0, _cbExisting.SelectedIndex));
                    tr.Commit();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Adding labels failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            finally { Cursor = Cursors.Default; }

            string summary = $"Added {r.Created} label(s)" + (r.Removed > 0 ? $", removed {r.Removed}" : "") + (r.Skipped > 0 ? $", {r.Skipped} already there or skipped" : "") + (r.Failed > 0 ? $", {r.Failed} failed" : "") + ".";
            SetStatus(summary);
            if (r.Errors.Count > 0)
                MessageBox.Show(this, summary + "\n\n" + string.Join("\n", r.Errors.Distinct().Take(12)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (r.Created > 0 && _chkArrangeAfter.Checked)
            {
                HashSet<ObjectId> created = new HashSet<ObjectId>(r.NewIds);
                RunArrange(_sheets.Where(s => s.Viewports.Count > 0).ToList(), false, (tr, labels) => created);
            }
        }

        // =====================================================================================
        // Arrange
        // =====================================================================================

        /// <summary>The labels the Arrange page's target picks: of the selected parts / labels, or of the ticked networks.</summary>
        private HashSet<ObjectId> ArrangeChoice(Transaction tr, List<LabelInfo> labels)
        {
            Target t = _arrangeTarget;
            if (t.Mode == TParts)
            {
                // Labels picked themselves; parts picked themselves (not through one of their labels) bring all their labels.
                HashSet<ObjectId> chosen = new HashSet<ObjectId>(t.LabelIds);
                HashSet<ObjectId> parts = new HashSet<ObjectId>(t.PartIds);
                HashSet<ObjectId> viaLabel = new HashSet<ObjectId>(labels.Where(l => chosen.Contains(l.Id)).Select(l => l.PartId));
                foreach (LabelInfo l in labels) if (parts.Contains(l.PartId) && !viaLabel.Contains(l.PartId)) chosen.Add(l.Id);
                return chosen;
            }
            HashSet<ObjectId> nets = new HashSet<ObjectId>(CheckedNetworks(t.NetworkList));
            Dictionary<ObjectId, ObjectId> networkOf = new Dictionary<ObjectId, ObjectId>();
            HashSet<ObjectId> result = new HashSet<ObjectId>();
            foreach (LabelInfo l in labels)
            {
                if (l.PartId.IsNull) continue;
                if (!networkOf.TryGetValue(l.PartId, out ObjectId net))
                {
                    try { net = tr.GetObject(l.PartId, OpenMode.ForRead) is Part p ? p.NetworkId : ObjectId.Null; } catch (Exception) { net = ObjectId.Null; }
                    networkOf[l.PartId] = net;
                }
                if (nets.Contains(net)) result.Add(l.Id);
            }
            return result;
        }

        /// <summary>
        /// Checks or arranges the labels on the sheets. Only the labels <paramref name="choose"/> returns (by default the Arrange
        /// page's choice) move; every other label stays and counts as an obstacle.
        /// </summary>
        private void RunArrange(List<SheetInfo> sheets, bool checkOnly, Func<Transaction, List<LabelInfo>, HashSet<ObjectId>> choose = null)
        {
            if (!Ready()) return;
            if (sheets.Count == 0) { MessageBox.Show(this, "Tick the sheets to " + (checkOnly ? "check." : "arrange."), Text); return; }
            if (!ReadArrangeOptions(out ArrangeOptions o)) return;
            if (choose == null)
            {
                if (_arrangeTarget.Mode == TParts && _arrangeTarget.PartIds.Count == 0 && _arrangeTarget.LabelIds.Count == 0)
                { MessageBox.Show(this, "Select the labels or parts to arrange first, or tick networks (Labels to arrange).", Text); return; }
                if (_arrangeTarget.Mode == TNetworks && _arrangeTarget.NetworkList.CheckedItems.Count == 0)
                { MessageBox.Show(this, "Tick the networks whose labels to arrange (Labels to arrange).", Text); return; }
                choose = ArrangeChoice;
            }
            else o.RearrangeAll = true;
            ArrangeReport report = new ArrangeReport();
            int masked = -1;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    Database db = _doc.Database;
                    CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
                    List<LabelInfo> labels = AnnotLabels.Scan(tr, db);
                    o.Only = choose(tr, labels);
                    AnnotScene scene = AnnotScene.Read(tr, db, cdoc, o.AvoidModelText, o.Profile, o.AvoidXrefs);
                    double ppu = AnnotLabels.PaperPerUnit(db);
                    HashSet<ObjectId> handled = new HashSet<ObjectId>();
                    int done = 0;
                    foreach (SheetInfo s in sheets)
                    {
                        SetStatus($"{(checkOnly ? "Checking" : "Arranging")} {s.Layout} ({++done}/{sheets.Count})…");
                        _lblStatus.Refresh();
                        report.Add(AnnotArranger.Arrange(tr, s, labels, scene, o, handled, checkOnly, ppu));
                    }
                    if (!checkOnly && _s.MaskArranged)
                        masked = AnnotLabels.SetMask(tr, report.Boxes.Keys.Where(id => o.Only == null || o.Only.Contains(id)).ToList(), true).styles;
                    tr.Commit();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Arranging failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            finally { Cursor = Cursors.Default; }

            _issues.Clear();
            foreach (ArrangeIssue i in report.Issues)
                _issues.Add((i, report.Boxes.TryGetValue(i.LabelId, out Box2 b) ? b : (Box2?)null));
            _gridIssues.Rows.Clear();
            foreach ((ArrangeIssue i, Box2? _) in _issues) _gridIssues.Rows.Add(i.Sheet, i.Text);
            string unmeasured = report.Unmeasured > 0 ? $" {report.Unmeasured} could not be measured (listed)." : "";
            SetStatus(checkOnly
                ? $"Checked {report.Checked} label(s) on {sheets.Count} sheet(s): {report.Problems} with a problem.{unmeasured}"
                : $"Arranged {report.Checked} label(s) on {sheets.Count} sheet(s): moved {report.Moved}, {report.Unresolved} without a clear place (listed).{unmeasured}" +
                  (masked > 0 ? $" Text masking turned on in {masked} label style(s)." : ""));
            if (report.Checked == 0) SetStatus("No chosen labels are shown on those sheets.");
            if (_issues.Count > 0 && _nav.Selected != PArrange) _nav.Selected = PArrange;
        }

        private async void MaskSelected(bool mask)
        {
            List<ObjectId> ids = await PickAsync(mask ? "\nSelect the labels (or multileaders) whose text to mask: " : "\nSelect the labels (or multileaders) to unmask: ");
            if (ids == null) return;
            (int styles, int leaders) n;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
            {
                n = AnnotLabels.SetMask(tr, ids, mask);
                tr.Commit();
            }
            _doc.Editor.Regen();
            if (n.styles + n.leaders == 0) SetStatus(mask ? "Nothing changed: the selection's labels are masked already (or it has no labels)." : "Nothing changed.");
            else SetStatus($"{(mask ? "Masked" : "Unmasked")} the text of {n.styles} label style(s) and {n.leaders} multileader(s).");
        }

        private async void LockSelected(bool locked)
        {
            List<ObjectId> ids = await PickAsync(locked ? "\nSelect the labels to lock: " : "\nSelect the labels to unlock: ");
            if (ids == null) return;
            int count;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
            {
                List<ObjectId> labels = ids.Where(id => tr.GetObject(id, OpenMode.ForRead) is CLabel l && AnnotLabels.KindOf(l) != null).ToList();
                count = labels.Count;
                if (count > 0) AnnotLabels.SetLocked(tr, _doc.Database, labels, locked);
                tr.Commit();
            }
            if (count == 0) MessageBox.Show(this, "The selection has no pipe or structure labels.", Text);
            else SetStatus($"{(locked ? "Locked" : "Unlocked")} {count} label(s).");
        }

        private void GoToIssue(int row)
        {
            if (row >= _issues.Count) return;
            (ArrangeIssue issue, Box2? box) = _issues[row];
            GoToSheet(issue.Sheet, box);
        }

        // =====================================================================================
        // Sheets
        // =====================================================================================

        /// <summary>Makes the layout current, in paper space, zoomed to the paper box when there is one.</summary>
        private void GoToSheet(string layout, Box2? box = null)
        {
            if (!Ready()) return;
            try
            {
                using (_doc.LockDocument()) LayoutManager.Current.CurrentLayout = layout;
                try { _doc.Editor.SwitchToPaperSpace(); } catch (Exception) { }
                if (box is Box2 b)
                    ViewHelper.ZoomTo(new Extents3d(new Point3d(b.MinX, b.MinY, 0), new Point3d(b.MaxX, b.MaxY, 0)), 3.0, 60.0);
                SetStatus("Sheet " + layout);
                SelectSheetRow(layout);
            }
            catch (Exception ex) { SetStatus($"Could not open {layout}: {ex.Message}"); }
        }

        private void SelectSheetRow(string layout)
        {
            foreach (DataGridViewRow r in _gridSheets.Rows)
                if ((r.Tag as SheetInfo)?.Layout == layout) { _gridSheets.ClearSelection(); r.Selected = true; _gridSheets.CurrentCell = r.Cells[0]; }
        }

        private SheetInfo SelectedSheet() => _gridSheets.CurrentRow?.Tag as SheetInfo;

        private void GoToSelectedSheet()
        {
            SheetInfo s = SelectedSheet();
            if (s != null) GoToSheet(s.Layout);
        }

        private void Step(int by)
        {
            if (_sheets.Count == 0) return;
            string cur = CurrentLayout();
            int i = _sheets.FindIndex(s => s.Layout == cur);
            int next = i < 0 ? 0 : (i + by + _sheets.Count) % _sheets.Count;
            GoToSheet(_sheets[next].Layout);
        }

        private void ArrangeSelectedSheet(bool checkOnly)
        {
            SheetInfo s = SelectedSheet();
            if (s == null) return;
            RunArrange(new List<SheetInfo> { s }, checkOnly);
        }

        // =====================================================================================
        // Names
        // =====================================================================================

        /// <summary>The networks and (for a selection) the parts to rename; false with a message when nothing is chosen.</summary>
        private bool RenameScope(Transaction tr, out List<ObjectId> networks, out HashSet<ObjectId> only)
        {
            only = null;
            if (_nameTarget.Mode == TParts)
            {
                only = new HashSet<ObjectId>(_nameTarget.PartIds);
                networks = only.Select(id => tr.GetObject(id, OpenMode.ForRead) is Part p ? p.NetworkId : ObjectId.Null).Where(n => !n.IsNull).Distinct().ToList();
                if (only.Count == 0) { MessageBox.Show(this, "Select the parts to rename first (Select in drawing…).", Text); return false; }
            }
            else
            {
                networks = CheckedNetworks(_nameTarget.NetworkList);
                if (networks.Count == 0) { MessageBox.Show(this, "Tick the networks to rename.", Text); return false; }
            }
            return true;
        }

        /// <summary>Shows what each template makes, with sample values (or what is wrong with it).</summary>
        private void UpdateExamples()
        {
            string sheet = _cbSheetSource.SelectedIndex == (int)SheetSource.Fixed ? _txtFixedSheet.Text.Trim() : "124";
            string type = _gridCodes.Rows.Cast<DataGridViewRow>().Select(r => r.Cells[1].Value as string).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "IC3B";
            string network = _nameTarget.NetworkList.CheckedItems.Cast<object>().FirstOrDefault()?.ToString() ?? "ICT-TILE-124";
            void Show(Label label, string text, bool used)
            {
                NameTemplate t = NameTemplate.Parse(text);
                if (!used) { label.Text = "Not renamed"; label.ForeColor = StyleUi.Muted; return; }
                if (t.Errors.Count > 0) { label.Text = "⚠ " + t.Errors[0]; label.ForeColor = StyleUi.Danger; return; }
                string Value(string token) => token == "sheet" ? sheet : token == "type" ? type : token == "network" ? network : "";
                label.Text = $"Example: {t.Render(Value, 1)},  {t.Render(Value, 2)} …" + (t.HasNumber ? "" : "   (no {n}: names will repeat)");
                label.ForeColor = t.HasNumber ? StyleUi.Muted : StyleUi.Danger;
            }
            Show(_lblPipeExample, _txtPipeTemplate.Text, _chkNamePipes.Checked);
            Show(_lblStructExample, _txtStructTemplate.Text, _chkNameStructs.Checked);
        }

        /// <summary>Lists every type value the chosen parts have, with its code (kept, else suggested).</summary>
        private void ReadTypes(bool quiet = false)
        {
            if (!Ready() || !ReadNamingOptions(quiet: true)) return;
            SortedSet<string> values;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                if (_nameTarget.Mode == TNetworks && _nameTarget.NetworkList.CheckedItems.Count == 0 || _nameTarget.Mode == TParts && _nameTarget.PartIds.Count == 0)
                {
                    if (!quiet) MessageBox.Show(this, "Choose the parts or networks first.", Text);
                    return;
                }
                if (!RenameScope(tr, out List<ObjectId> nets, out _)) return;
                values = AnnotNaming.TypeValues(tr, nets, _s.Naming);
            }
            Dictionary<string, string> codes = _s.Naming.TypeCodes;
            HashSet<string> shown = new HashSet<string>(_gridCodes.Rows.Cast<DataGridViewRow>().Select(r => r.Cells[0].Value as string), StringComparer.OrdinalIgnoreCase);
            foreach (string v in values)
                if (!shown.Contains(v)) _gridCodes.Rows.Add(v, codes.TryGetValue(v, out string c) && !string.IsNullOrWhiteSpace(c) ? c : AnnotNaming.SuggestCode(v));
            UpdateExamples();
            if (!quiet) SetStatus($"{values.Count} type value(s).");
        }

        private void PreviewNames()
        {
            if (!Ready() || !ReadNamingOptions()) return;
            ReadTypes(quiet: true);
            ReadNamingOptions(quiet: true);
            Cursor = Cursors.WaitCursor;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    if (!RenameScope(tr, out List<ObjectId> nets, out HashSet<ObjectId> only)) return;
                    _renameNetworks = nets;
                    _renameRows = AnnotNaming.Plan(tr, nets, _s.Naming, _sheets, _s.RouteGap, _s.RouteCenterTolerance, only);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not plan the names: " + ex.Message, Text); return; }
            finally { Cursor = Cursors.Default; }
            _gridNames.Rows.Clear();
            foreach (RenameRow r in _renameRows)
                _gridNames.Rows.Add(r.New != r.Old && !r.Clash && !r.Note.StartsWith("Data reference") && !r.Note.Contains("template"), r.Network, r.Sheet, r.Old, r.New, r.Note);
            PaintNameRows();
            int changes = _renameRows.Count(r => r.New != r.Old);
            SetStatus($"{_renameRows.Count} part(s), {changes} name change(s)" + (_renameRows.Any(r => r.Clash) ? $", {_renameRows.Count(r => r.Clash)} taken (red)." : "."));
        }

        private void PaintNameRows()
        {
            for (int i = 0; i < _renameRows.Count && i < _gridNames.Rows.Count; i++)
            {
                RenameRow r = _renameRows[i];
                DataGridViewRow row = _gridNames.Rows[i];
                row.Cells[5].Value = r.Note;
                row.DefaultCellStyle.ForeColor = r.Clash ? StyleUi.Danger : r.New == r.Old ? StyleUi.Muted : StyleUi.Text;
                if (r.Clash) row.Cells[0].Value = false;
            }
        }

        /// <summary>A new name typed in the preview: checked again against every other name.</summary>
        private void NameEdited(int index)
        {
            if (index >= _renameRows.Count || _doc == null) return;
            RenameRow r = _renameRows[index];
            r.New = (_gridNames.Rows[index].Cells[4].Value as string ?? "").Trim();
            if (r.New.Length == 0) r.New = r.Old;
            _gridNames.Rows[index].Cells[4].Value = r.New;
            using (_doc.LockDocument())
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                AnnotNaming.CheckNames(_renameRows, AnnotNaming.KeptNames(tr, _renameNetworks, _renameRows));
            if (!r.Clash && r.New != r.Old) _gridNames.Rows[index].Cells[0].Value = true;
            PaintNameRows();
        }

        private void ApplyNames()
        {
            if (!Ready()) return;
            if (_renameRows.Count == 0) { PreviewNames(); if (_renameRows.Count == 0) return; }
            _gridNames.EndEdit();
            List<RenameRow> rows = new List<RenameRow>();
            for (int i = 0; i < _renameRows.Count && i < _gridNames.Rows.Count; i++)
                if (_gridNames.Rows[i].Cells[0].Value is bool b && b && _renameRows[i].New != _renameRows[i].Old && !_renameRows[i].Clash) rows.Add(_renameRows[i]);
            if (rows.Count == 0) { MessageBox.Show(this, "No ticked row changes a name.", Text); return; }
            if (MessageBox.Show(this, $"Rename {rows.Count} part(s)?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            (int renamed, int failed, List<string> errors, bool complete) result;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    result = AnnotNaming.Apply(tr, rows);
                    // All or nothing: one failed row undoes every rename, so no part keeps a temporary name.
                    if (result.complete) tr.Commit();
                    else tr.Abort();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No part was renamed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                PreviewNames();
                return;
            }
            if (result.complete) SetStatus($"Renamed {result.renamed} part(s).");
            else
            {
                SetStatus($"No part was renamed: {result.failed} rename(s) failed (see the message).");
                MessageBox.Show(this, "No part was renamed, because these failed:\n\n" + string.Join("\n", result.errors.Take(12)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            PreviewNames();
        }

        private void ZoomToPart(ObjectId id)
        {
            if (!Ready()) return;
            try
            {
                Extents3d ext;
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                    ext = ((Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(id, OpenMode.ForRead)).GeometricExtents;
                if (LayoutManager.Current.CurrentLayout != "Model")
                    using (_doc.LockDocument()) LayoutManager.Current.CurrentLayout = "Model";
                ViewHelper.ZoomTo(ext, 1.6, 20.0);
                _doc.Editor.SetImpliedSelection(new[] { id });
            }
            catch (Exception ex) { SetStatus("Could not zoom: " + ex.Message); }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Choosing what to work on\n" +
                "Each page works on parts selected in the drawing (Select in drawing… uses what is already selected, or asks you to select), " +
                "or on ticked networks.\n\n" +
                "Add labels\n" +
                "Fill the table with the labels to add: plan or profile, pipe or structure, the style, and where along the pipe. Or fill it from a " +
                "part you labelled by hand. Profile labels go into every profile view that shows the part.\n\n" +
                "Arrange labels\n" +
                "Works from what each sheet shows. Every chosen label is placed: in rows beside the route where possible, otherwise at the nearest " +
                "free spot within the distance limit, inside its viewport and profile view, dragged with a leader to its part. Other labels stay " +
                "where they are and are kept clear of. Lock labels you placed by hand.\n\n" +
                "Rename parts\n" +
                "Names are built from templates; the example under each shows the result. Numbering restarts for every sheet and type. " +
                "You can edit any new name in the preview before renaming.\n\n" +
                "Sheets\n" +
                "Move between layouts, and check or arrange one sheet or all of them.\n\n" +
                "Each action is a single UNDO.",
                "Annotation Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
