using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Sheet Manager: every sheet of the drawing with what it carries (drawing number, tile and part, title block, key plan,
    /// match lines, scale), kept in step when sheets are renamed, renumbered, copied, created or moved to another tile part.
    /// Title block table with revisions and an Excel register, viewport scale and layers, checks with fixes, page setups and
    /// PDF output. A modeless window; each change is one UNDO.
    /// </summary>
    internal sealed class FormSheetManager : Form
    {
        private static FormSheetManager _instance;

        public static void ShowTool()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new FormSheetManager();
                AcadUiApp.ShowModelessDialog(null, _instance, false);
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
            if (doc != null && doc != _instance._doc) _instance.BindTo(doc);
        }

        private const int PSheets = 0, PTitle = 1, PViewports = 2, PChecks = 3, PPublish = 4, PSettings = 5;

        private readonly SheetManagerSettings _s = SheetManagerSettings.Load();
        private SheetProfile P => _s.Profile;
        private Document _doc;
        private List<SheetRecord> _sheets = new List<SheetRecord>();
        private List<SheetIssue> _issues = new List<SheetIssue>();
        /// <summary>The chosen sheets (layout names), shared by every page.</summary>
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _filling, _busy;

        private readonly NavList _nav = new NavList();
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = StyleUi.Surface };
        private readonly List<Control> _pages = new List<Control>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
        private readonly Label _lblDrawing = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Anchor = AnchorStyles.Right, Margin = new Padding(3, 8, 3, 3) };

        private readonly DataGridView _gridSheets = ToolUi.Grid(), _gridTitle = ToolUi.Grid(), _gridViews = ToolUi.Grid(), _gridChecks = ToolUi.Grid();
        /// <summary>Title block cells edited and not yet applied: (layout, tag) → value.</summary>
        private readonly Dictionary<(string layout, string tag), string> _edits = new Dictionary<(string, string), string>();
        private readonly ComboBox _cbScale = ToolUi.Combo(110), _cbLayerSource = ToolUi.Combo(320), _cbPageSetup = ToolUi.Combo(220);
        private readonly TextBox _txtPdfFolder = new TextBox { Width = 380 }, _txtPdfName = new TextBox { Width = 260 };
        private readonly RadioButton _rbPdfEach = new RadioButton { Text = "One PDF per sheet", AutoSize = true }, _rbPdfOne = new RadioButton { Text = "One PDF with all the sheets", AutoSize = true };
        private readonly CheckBox _chkPdfForce = ToolUi.Check("Plot with \"DWG To PDF.pc3\" whatever the sheet's own device"), _chkPdfOpen = ToolUi.Check("Open the folder afterwards");
        private readonly Dictionary<string, TextBox> _profileBoxes = new Dictionary<string, TextBox>();

        private FormSheetManager()
        {
            Text = "C3D Pro Tools - Sheet Manager";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(980, 640);
            ClientSize = new Size(_s.Width >= 980 ? _s.Width : 1240, _s.Height >= 640 ? _s.Height : 800);

            _nav.Add("Sheets", "Names, tiles and parts: copy, create, sort");
            _nav.Add("Title block", "Every sheet's fields at once, revisions");
            _nav.Add("Viewports", "Scale, locking and layers");
            _nav.Add("Checks", "Find what is out of step, fix it");
            _nav.Add("Publish", "Page setups and PDF");
            _nav.Add("Settings", "Where each sheet keeps its data");
            _pages.Add(BuildSheetsPage());
            _pages.Add(BuildTitlePage());
            _pages.Add(BuildViewportsPage());
            _pages.Add(BuildChecksPage());
            _pages.Add(BuildPublishPage());
            _pages.Add(BuildSettingsPage());
            foreach (Control p in _pages) { p.Visible = false; _content.Controls.Add(p); }

            Controls.Add(_content);
            Controls.Add(_nav);
            Controls.Add(ToolUi.Header("Sheet Manager", "Every sheet with its number, tile, title block and key plan, kept in step.",
                StyleUi.HeaderButton("How it works", (s, e) => ShowHelp()),
                StyleUi.HeaderButton("Reload drawing", (s, e) => ReadDrawing())));
            Controls.Add(BuildFooter());

            ApplySettings();
            _nav.SelectedChanged += (s, e) => ShowPage();
            _nav.Selected = Math.Max(0, Math.Min(_pages.Count - 1, _s.Page));
            ShowPage();

            AcadCoreApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcadCoreApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            FormClosing += (s, e) =>
            {
                if (_edits.Count > 0 && MessageBox.Show(this, $"{_edits.Count} title block change(s) are not applied. Close anyway?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { e.Cancel = true; return; }
                SaveSettings();
                _s.Width = ClientSize.Width; _s.Height = ClientSize.Height; _s.Page = _nav.Selected;
                _s.Save();
            };
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
            _selected.Clear();
            _edits.Clear();
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
            FillAll();
            _lblDrawing.Text = "No drawing";
        }

        private void ReadDrawing()
        {
            _sheets = new List<SheetRecord>();
            if (_doc == null) { FillAll(); return; }
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    _sheets = SheetManager.Read(tr, _doc.Database, P);
                    _issues = SheetManager.Check(tr, _sheets, P);
                    FillPageSetups(SheetManager.PageSetups(tr, _doc.Database));
                    tr.Commit();
                }
            }
            catch (Exception ex) { SetStatus("Could not read the drawing: " + ex.Message); }
            _selected.RemoveWhere(n => !_sheets.Any(s => s.Layout.Equals(n, StringComparison.OrdinalIgnoreCase)));
            _lblDrawing.Text = Path.GetFileName(_doc.Name);
            FillAll();
            int parts = _sheets.Count(s => s.IsPart);
            SetStatus($"{_sheets.Count} sheet(s): {parts} tile part(s) in {_sheets.Where(s => s.IsPart).Select(s => s.Tile).Distinct().Count()} tile(s); {_issues.Count} issue(s) found.");
        }

        private bool Ready()
        {
            if (_busy) return false;
            Document active = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (_doc != null && _doc == active) return true;
            if (active != null) { BindTo(active); return _doc != null; }
            MessageBox.Show(this, "Open a drawing first.", Text);
            return false;
        }

        /// <summary>Runs a change in one transaction (one UNDO), then reads the drawing again; false when it failed (nothing changed).</summary>
        private bool Change(string what, Func<Transaction, string> work)
        {
            if (!Ready()) return false;
            _busy = true;
            string done = null;
            try
            {
                Cursor = Cursors.WaitCursor;
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    done = work(tr);
                    tr.Commit();
                }
                ViewHelper.Regen(_doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{what} stopped and nothing was changed.\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { _busy = false; Cursor = Cursors.Default; }
            ReadDrawing();
            if (!string.IsNullOrEmpty(done)) SetStatus(done);
            return true;
        }

        private void SetStatus(string text) => _lblStatus.Text = text;

        private List<SheetRecord> Chosen(bool askIfNone = true)
        {
            List<SheetRecord> list = _sheets.Where(s => _selected.Contains(s.Layout)).OrderBy(s => s.Tab).ToList();
            if (list.Count == 0 && askIfNone) MessageBox.Show(this, "Select one or more sheets in the list first (Ctrl or Shift for several).", Text);
            return list;
        }

        private SheetRecord Sheet(string layout) => _sheets.FirstOrDefault(s => s.Layout.Equals(layout, StringComparison.OrdinalIgnoreCase));

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

        private static FlowLayoutPanel Bar(params Control[] items)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2), Padding = new Padding(0) };
            row.Controls.AddRange(items);
            return row;
        }

        private static Label Hint(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(6, 9, 3, 3) };

        /// <summary>A page: bars of buttons over a grid that fills the rest.</summary>
        private static TableLayoutPanel GridPage(DataGridView grid, params Control[] top)
        {
            TableLayoutPanel page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 8, 12, 8), BackColor = StyleUi.Surface };
            foreach (Control c in top) { page.RowStyles.Add(new RowStyle(SizeType.AutoSize)); page.Controls.Add(c, 0, page.RowCount++); }
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(grid, 0, page.RowCount++);
            return page;
        }

        private void ShowPage()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == _nav.Selected;
            ApplySelection(GridOf(_nav.Selected));
        }

        private DataGridView GridOf(int page) => page == PSheets ? _gridSheets : page == PTitle ? _gridTitle : page == PViewports ? _gridViews : null;

        /// <summary>The grid's selected rows become the shared choice of sheets.</summary>
        private void TrackSelection(DataGridView grid)
        {
            grid.SelectionChanged += (s, e) =>
            {
                if (_filling || !grid.Visible) return;
                _selected.Clear();
                foreach (DataGridViewRow r in grid.Rows) if (r.Selected || r.Cells.Cast<DataGridViewCell>().Any(c => c.Selected)) if (r.Tag is SheetRecord sh) _selected.Add(sh.Layout);
            };
        }

        private void ApplySelection(DataGridView grid)
        {
            if (grid == null) return;
            _filling = true;
            grid.ClearSelection();
            foreach (DataGridViewRow r in grid.Rows)
                if (r.Tag is SheetRecord s && _selected.Contains(s.Layout))
                {
                    if (grid.SelectionMode == DataGridViewSelectionMode.CellSelect) r.Cells[0].Selected = true;
                    else r.Selected = true;
                }
            _filling = false;
        }

        private void FillAll()
        {
            FillSheets();
            FillTitle();
            FillViews();
            FillChecks();
            FillLayerSources();
        }

        // =====================================================================================
        // Sheets page
        // =====================================================================================

        private Control BuildSheetsPage()
        {
            Button sort = StyleUi.Secondary("Sort selected ▾");
            ContextMenuStrip sortMenu = new ContextMenuStrip();
            sortMenu.Items.Add("By running number", null, (s, e) => SortSelected("number"));
            sortMenu.Items.Add("By tile, then part", null, (s, e) => SortSelected("tile"));
            sortMenu.Items.Add("By name", null, (s, e) => SortSelected("name"));
            sort.Click += (s, e) => sortMenu.Show(sort, new Point(0, sort.Height));
            _tips.SetToolTip(sort, "Puts the selected sheets in order within the tab positions they hold; other sheets stay where they are.");

            Control nav = Bar(StyleUi.Primary("Go to sheet", (s, e) => GoTo(Chosen().FirstOrDefault())),
                StyleUi.Secondary("◀", (s, e) => Step(-1)), StyleUi.Secondary("▶", (s, e) => Step(1)),
                StyleUi.Secondary("Move up", (s, e) => MoveSheets(-1)), StyleUi.Secondary("Move down", (s, e) => MoveSheets(1)), sort,
                Hint("Edit Layout, Tile or Part in the list: the sheet's texts, title block, key plan and match lines follow."));
            Control make = Bar(StyleUi.Secondary("Duplicate…", (s, e) => Duplicate()), StyleUi.Secondary("Complete the tile…", (s, e) => CompleteTile()),
                StyleUi.Secondary("New from template…", (s, e) => NewFromTemplate()), StyleUi.Secondary("Rename / renumber…", (s, e) => RenameMany()),
                StyleUi.Secondary("Delete…", (s, e) => DeleteSheets()),
                StyleUi.Secondary("Update key plans", (s, e) => UpdateKeyPlans()), StyleUi.Secondary("Fill match lines", (s, e) => FillMatchLines()));

            _gridSheets.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            foreach ((string name, string head, int w, bool edit) in new[] { ("tab", "#", 5, false), ("layout", "Layout (drawing number)", 46, true), ("tile", "Tile", 8, true), ("part", "Part", 6, true),
                         ("title", "Sheet title", 22, false), ("rev", "Rev", 6, false), ("stage", "Stage", 16, false), ("scale", "Scale", 9, false), ("setup", "Page setup", 12, false), ("issues", "Issues", 22, false) })
                _gridSheets.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = head, FillWeight = w, ReadOnly = !edit, SortMode = DataGridViewColumnSortMode.NotSortable });
            _gridSheets.CellEndEdit += (s, e) => SheetCellEdited(e.RowIndex, e.ColumnIndex);
            // Double-click a read-only cell to open the sheet (the editable ones start editing).
            _gridSheets.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && _gridSheets.Columns[e.ColumnIndex].ReadOnly) GoTo(_gridSheets.Rows[e.RowIndex].Tag as SheetRecord); };
            TrackSelection(_gridSheets);
            return GridPage(_gridSheets, nav, make);
        }

        private void FillSheets()
        {
            _filling = true;
            _gridSheets.Rows.Clear();
            Dictionary<string, int> issues = _issues.GroupBy(i => i.Layout).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (SheetRecord s in _sheets)
            {
                string scale = s.ViewportScale + (s.Attr(P.ScaleTag).Length > 0 && s.Attr(P.ScaleTag).Replace(" ", "") != s.ViewportScale ? $" ≠ {s.Attr(P.ScaleTag)}" : "");
                int i = _gridSheets.Rows.Add(s.Tab, s.Layout, s.Tile, s.Part, s.Attr(P.SheetTitleTag), s.Attr(P.RevisionTag), s.Attr(P.StageTag), scale, s.PageSetup,
                    issues.TryGetValue(s.Layout, out int n) ? $"{n} issue(s)" : "");
                DataGridViewRow row = _gridSheets.Rows[i];
                row.Tag = s;
                if (!s.IsPart) { row.Cells["part"].Style.ForeColor = StyleUi.Muted; row.Cells["tile"].ToolTipText = "Not a tile part (no sheet ID text): " + (s.TileSource.Length > 0 ? "tile read from the " + s.TileSource : "no tile found"); }
                if (n > 0) { row.Cells["issues"].Style.ForeColor = Color.Firebrick; row.Cells["issues"].ToolTipText = string.Join("\n", _issues.Where(x => x.Layout == s.Layout).Select(x => x.Text)); }
                row.Selected = _selected.Contains(s.Layout);
            }
            _filling = false;
        }

        private void SheetCellEdited(int rowIndex, int col)
        {
            if (_filling || rowIndex < 0 || !(_gridSheets.Rows[rowIndex].Tag is SheetRecord s)) return;
            string column = _gridSheets.Columns[col].Name;
            string value = (_gridSheets.Rows[rowIndex].Cells[col].Value?.ToString() ?? "").Trim();
            if (column == "layout" && value != s.Layout)
            {
                string problem = SheetManager.NameProblem(value, _sheets, s);
                if (problem != null) { MessageBox.Show(this, problem, Text); BeginInvoke((Action)FillSheets); return; }
                string old = s.Layout;
                if (Change("Renaming", tr => string.Join("; ", SheetManager.Rename(tr, _doc.Database, _sheets, new Dictionary<SheetRecord, string> { [s] = value }, P)))) { _selected.Remove(old); _selected.Add(value); ApplySelection(_gridSheets); }
            }
            else if ((column == "tile" || column == "part") && value != (column == "tile" ? s.Tile : s.Part))
            {
                if (!s.IsPart && s.KeyTexts.Count == 0) { MessageBox.Show(this, "This sheet has no sheet ID text or key plan to show a tile on (see Settings).", Text); BeginInvoke((Action)FillSheets); return; }
                string tile = column == "tile" ? value : s.Tile, part = column == "part" ? value.ToUpperInvariant() : s.Part;
                if (part.Length > 0 && SheetManager.PartCell(part, P).row < 0) { MessageBox.Show(this, $"\"{part}\" is not a part of the tile grid ({P.PartGrid}).", Text); BeginInvoke((Action)FillSheets); return; }
                string oldTile = s.Tile;
                Change("Changing the tile", tr =>
                {
                    List<string> log = new List<string>();
                    SheetManager.SetTilePart(tr, s, tile, part, P, log);
                    SheetManager.FillMatchLines(tr, _sheets, _sheets.Where(x => x.Tile == tile || x.Tile == oldTile), P);
                    return $"{s.Layout} is now {s.TilePart}." + (log.Count > 0 ? " " + string.Join(" ", log) : "");
                });
            }
        }

        private void GoTo(SheetRecord s)
        {
            if (s == null || !Ready()) return;
            try
            {
                using (_doc.LockDocument()) LayoutManager.Current.CurrentLayout = s.Layout;
                try { _doc.Editor.SwitchToPaperSpace(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                SetStatus("Sheet " + s.Layout);
                _selected.Clear(); _selected.Add(s.Layout);
                ApplySelection(GridOf(_nav.Selected));
            }
            catch (Exception ex) { SetStatus($"Could not open {s.Layout}: {ex.Message}"); }
        }

        private void Step(int by)
        {
            if (_sheets.Count == 0 || !Ready()) return;
            string cur = "";
            try { cur = LayoutManager.Current.CurrentLayout; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            int i = _sheets.FindIndex(s => s.Layout == cur);
            GoTo(_sheets[i < 0 ? 0 : (i + by + _sheets.Count) % _sheets.Count]);
        }

        private void MoveSheets(int by)
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0) return;
            List<SheetRecord> order = _sheets.ToList();
            foreach (SheetRecord s in by < 0 ? chosen : Enumerable.Reverse(chosen))
            {
                int i = order.IndexOf(s), j = i + by;
                if (j < 0 || j >= order.Count || chosen.Contains(order[j])) continue;
                order.RemoveAt(i);
                order.Insert(j, s);
            }
            Change("Moving", tr => { SheetManager.Reorder(tr, order); return "Moved."; });
        }

        private void SortSelected(string by)
        {
            List<SheetRecord> chosen = Chosen(false);
            if (chosen.Count < 2) chosen = _sheets.ToList();
            IComparer<string> natural = Comparer<string>.Create(SheetManager.Natural);
            List<SheetRecord> sorted = by == "tile"
                ? chosen.OrderBy(s => s.Tile.Length == 0 ? 1 : 0).ThenBy(s => s.Tile, natural).ThenBy(s => s.IsPart ? 0 : 1).ThenBy(s => s.Part).ThenBy(s => s.Layout, natural).ToList()
                : by == "number" ? chosen.OrderBy(s => SheetManager.NumberOf(s.Layout, P) < 0 ? 1 : 0).ThenBy(s => SheetManager.NumberOf(s.Layout, P)).ThenBy(s => s.Layout, natural).ToList()
                : chosen.OrderBy(s => s.Layout, natural).ToList();
            // Into the tab positions the chosen sheets hold; the others stay put.
            List<SheetRecord> order = _sheets.ToList();
            List<int> slots = chosen.Select(s => order.IndexOf(s)).OrderBy(i => i).ToList();
            for (int k = 0; k < slots.Count; k++) order[slots[k]] = sorted[k];
            Change("Sorting", tr => { SheetManager.Reorder(tr, order); return $"Sorted {sorted.Count} sheet(s)."; });
        }

        private void Duplicate()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count != 1) { if (chosen.Count > 1) MessageBox.Show(this, "Select the one sheet to copy.", Text); return; }
            SheetRecord src = chosen[0];
            using (Form f = FormPartsLists.SmallDialog("Duplicate " + src.Layout, out TableLayoutPanel body, out Button ok))
            {
                NumericUpDown count = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 1, Width = 60 };
                CheckBox nextParts = ToolUi.Check("Give the copies the next free parts of tile " + src.Tile, src.IsPart);
                nextParts.Enabled = src.IsPart;
                ToolUi.Field(body, "Copies", count);
                ToolUi.Wide(body, nextParts);
                ToolUi.Wide(body, ToolUi.Note("Each copy gets the next free drawing number after this sheet. Their match lines are filled within the tile."));
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int n = (int)count.Value;
                bool parts = nextParts.Checked;
                Change("Duplicating", tr =>
                {
                    List<string> made = new List<string>(), log = new List<string>();
                    List<string> free = SheetManager.AllParts(P).Where(x => !_sheets.Any(s => s.IsPart && s.Tile == src.Tile && s.Part == x)).ToList();
                    long from = SheetManager.NumberOf(src.Layout, P) + 1;
                    for (int i = 0; i < n; i++)
                    {
                        string name = SheetManager.NextFreeName(src.Layout, from, _sheets, made, P);
                        SheetRecord copy = SheetManager.Duplicate(tr, _doc.Database, Sheet(src.Layout) ?? src, name, P);
                        if (parts && i < free.Count) SheetManager.SetTilePart(tr, copy, src.Tile, free[i], P, log);
                        made.Add(name);
                        from = SheetManager.NumberOf(name, P) + 1;
                    }
                    List<SheetRecord> all = SheetManager.Read(tr, _doc.Database, P);
                    SheetManager.FillMatchLines(tr, all, all.Where(s => s.Tile == src.Tile), P);
                    _selected.Clear(); foreach (string m in made) _selected.Add(m);
                    return $"Made {made.Count} cop{(made.Count == 1 ? "y" : "ies")}: {string.Join(", ", made.Select(Short))}." + (log.Count > 0 ? " " + string.Join(" ", log) : "");
                });
            }
        }

        /// <summary>The missing parts of the selected sheet's tile, copied from it with numbers following the tile's order.</summary>
        private void CompleteTile()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count != 1 || !chosen[0].IsPart) { if (chosen.Count > 0) MessageBox.Show(this, "Select one plan sheet of the tile (a sheet with its sheet ID text).", Text); return; }
            SheetRecord src = chosen[0];
            List<string> all = SheetManager.AllParts(P);
            List<string> missing = all.Where(x => !_sheets.Any(s => s.IsPart && s.Tile == src.Tile && s.Part == x)).ToList();
            if (missing.Count == 0) { MessageBox.Show(this, $"Tile {src.Tile} already has every part ({string.Join(", ", all)}).", Text); return; }
            long baseNo = SheetManager.NumberOf(src.Layout, P) - all.IndexOf(src.Part);
            List<(string part, string name)> plan = new List<(string, string)>();
            List<string> taken = new List<string>();
            foreach (string part in missing)
            {
                string want = SheetManager.WithNumber(src.Layout, baseNo + all.IndexOf(part), P);
                string name = SheetManager.NameProblem(want, _sheets) == null && !taken.Contains(want) ? want : SheetManager.NextFreeName(src.Layout, SheetManager.NumberOf(src.Layout, P) + 1, _sheets, taken, P);
                taken.Add(name);
                plan.Add((part, name));
            }
            if (MessageBox.Show(this, $"Copy {src.Tile}-{src.Part} as:\n\n" + string.Join("\n", plan.Select(x => $"• {src.Tile}-{x.part}  →  {x.name}")) +
                                      "\n\nTexts, title block, key plan and match lines are set for each part. Pan each new viewport onto its part afterwards.", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Change("Completing the tile", tr =>
            {
                List<string> log = new List<string>();
                foreach ((string part, string name) in plan)
                {
                    SheetRecord copy = SheetManager.Duplicate(tr, _doc.Database, src, name, P);
                    SheetManager.SetTilePart(tr, copy, src.Tile, part, P, log);
                }
                List<SheetRecord> sheets = SheetManager.Read(tr, _doc.Database, P);
                SheetManager.FillMatchLines(tr, sheets, sheets.Where(s => s.Tile == src.Tile), P);
                // In tile order: the new parts next to the source.
                List<SheetRecord> order = sheets.ToList();
                List<SheetRecord> tile = order.Where(s => s.IsPart && s.Tile == src.Tile).OrderBy(s => all.IndexOf(s.Part)).ToList();
                int at = tile.Min(s => order.IndexOf(s));
                foreach (SheetRecord s in tile) order.Remove(s);
                order.InsertRange(Math.Min(at, order.Count), tile);
                SheetManager.Reorder(tr, order);
                _selected.Clear(); foreach ((string _, string name) in plan) _selected.Add(name);
                return $"Tile {src.Tile} completed: {string.Join(", ", plan.Select(x => x.part + " " + Short(x.name)))}." + (log.Count > 0 ? " " + string.Join(" ", log) : "");
            });
        }

        private static string Short(string name) => name.Length > 14 ? "…" + name.Substring(name.Length - 12) : name;

        private void NewFromTemplate()
        {
            if (!Ready()) return;
            using (Form f = FormPartsLists.SmallDialog("New sheet from a template", out TableLayoutPanel body, out Button ok))
            {
                RadioButton here = new RadioButton { Text = "A sheet of this drawing", AutoSize = true, Checked = true }, file = new RadioButton { Text = "A layout of another drawing or template (DWG, DWT)", AutoSize = true };
                ComboBox hereList = ToolUi.Combo(420), fileList = ToolUi.Combo(420);
                TextBox path = new TextBox { Width = 330, Text = _s.TemplateFile };
                Button browse = StyleUi.Secondary("Browse…");
                TextBox name = new TextBox { Width = 420 }, tile = new TextBox { Width = 70 }, part = new TextBox { Width = 40 };
                foreach (SheetRecord s in _sheets) hereList.Items.Add(s.Layout);
                SheetRecord first = Chosen(false).FirstOrDefault() ?? _sheets.LastOrDefault(s => s.IsPart);
                if (first != null) { hereList.SelectedItem = first.Layout; name.Text = SheetManager.NextFreeName(first.Layout, _sheets.Max(s => SheetManager.NumberOf(s.Layout, P)) + 1, _sheets, null, P); }
                void LoadFile()
                {
                    fileList.Items.Clear();
                    if (!File.Exists(path.Text)) return;
                    try { foreach (string l in SheetManager.LayoutsIn(path.Text)) fileList.Items.Add(l); if (fileList.Items.Count > 0) fileList.SelectedIndex = 0; }
                    catch (Exception ex) { MessageBox.Show(f, "The drawing could not be read: " + ex.Message, Text); }
                }
                browse.Click += (s, e) =>
                {
                    using (OpenFileDialog d = new OpenFileDialog { Filter = "Drawings and templates (*.dwg;*.dwt)|*.dwg;*.dwt", FileName = path.Text })
                        if (d.ShowDialog(f) == DialogResult.OK) { path.Text = d.FileName; file.Checked = true; LoadFile(); }
                };
                LoadFile();
                ToolUi.Wide(body, here);
                ToolUi.Field(body, "Sheet", hereList);
                ToolUi.Wide(body, file);
                ToolUi.Field(body, "File", ToolUi.Inline(path, browse));
                ToolUi.Field(body, "Layout", fileList);
                ToolUi.Field(body, "New name", name);
                ToolUi.Field(body, "Tile / part", ToolUi.Inline(tile, part, new Label { Text = "optional, e.g. 106 / A", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(6, 7, 0, 0) }));
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string newName = name.Text.Trim();
                string problem = SheetManager.NameProblem(newName, _sheets);
                if (problem != null) { MessageBox.Show(this, problem, Text); return; }
                bool fromFile = file.Checked;
                string src = fromFile ? fileList.SelectedItem as string : hereList.SelectedItem as string;
                if (src == null) { MessageBox.Show(this, "Choose the sheet to start from.", Text); return; }
                _s.TemplateFile = path.Text;
                string t = tile.Text.Trim(), pt = part.Text.Trim().ToUpperInvariant();
                Change("Creating the sheet", tr =>
                {
                    SheetRecord made = fromFile ? SheetManager.FromTemplate(tr, _doc.Database, path.Text, src, newName, P) : SheetManager.Duplicate(tr, _doc.Database, Sheet(src), newName, P);
                    SheetManager.SetAttributes(tr, made, new Dictionary<string, string> { [P.NumberTag] = newName });
                    List<string> log = new List<string>();
                    if (t.Length > 0 || pt.Length > 0) SheetManager.SetTilePart(tr, made, t.Length > 0 ? t : made.Tile, pt.Length > 0 ? pt : made.Part, P, log);
                    List<SheetRecord> all = SheetManager.Read(tr, _doc.Database, P);
                    if (made.Tile.Length > 0) SheetManager.FillMatchLines(tr, all, all.Where(s => s.Tile == made.Tile), P);
                    _selected.Clear(); _selected.Add(newName);
                    return $"Created {newName}." + (log.Count > 0 ? " " + string.Join(" ", log) : "");
                });
            }
        }

        private void RenameMany()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0) return;
            using (Form f = FormPartsLists.SmallDialog($"Rename {chosen.Count} sheet(s)", out TableLayoutPanel body, out Button ok))
            {
                RadioButton rbReplace = new RadioButton { Text = "Find and replace", AutoSize = true, Checked = true }, rbNumber = new RadioButton { Text = "Renumber in tab order", AutoSize = true },
                            rbTemplate = new RadioButton { Text = "Template", AutoSize = true };
                TextBox find = new TextBox { Width = 200 }, replace = new TextBox { Width = 200 }, template = new TextBox { Width = 360, Text = "{old}" };
                NumericUpDown start = new NumericUpDown { Minimum = 0, Maximum = 999999999, Width = 110, Value = Math.Max(0, SheetManager.NumberOf(chosen[0].Layout, P)) }, step = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = 1, Width = 60 };
                DataGridView preview = ToolUi.Grid();
                preview.Dock = DockStyle.None; preview.Height = 220; preview.Width = 640; preview.ReadOnly = true;
                preview.Columns.Add("old", "Now"); preview.Columns.Add("new", "New name"); preview.Columns.Add("note", "Note");
                Dictionary<SheetRecord, string> plan = new Dictionary<SheetRecord, string>();
                void Update()
                {
                    plan.Clear();
                    preview.Rows.Clear();
                    for (int i = 0; i < chosen.Count; i++)
                    {
                        SheetRecord s = chosen[i];
                        string n = rbReplace.Checked ? (find.Text.Length > 0 ? s.Layout.Replace(find.Text, replace.Text) : s.Layout)
                                 : rbNumber.Checked ? SheetManager.WithNumber(s.Layout, (long)start.Value + i * (long)step.Value, P)
                                 : Template(template.Text, s, i);
                        plan[s] = n;
                    }
                    foreach (KeyValuePair<SheetRecord, string> kv in plan)
                    {
                        string note = kv.Value == kv.Key.Layout ? "unchanged" : SheetManager.NameProblem(kv.Value, _sheets.Where(x => !plan.ContainsKey(x)));
                        if (note == null && plan.Values.Count(v => v.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)) > 1) note = "given twice";
                        int r = preview.Rows.Add(kv.Key.Layout, kv.Value, note ?? "");
                        if (note != null && note != "unchanged") preview.Rows[r].Cells[2].Style.ForeColor = Color.Firebrick;
                    }
                }
                foreach (Control c in new Control[] { find, replace, template }) c.TextChanged += (s, e) => Update();
                foreach (RadioButton r in new[] { rbReplace, rbNumber, rbTemplate }) r.CheckedChanged += (s, e) => Update();
                start.ValueChanged += (s, e) => Update(); step.ValueChanged += (s, e) => Update();
                ToolUi.Wide(body, rbReplace);
                ToolUi.Field(body, "Find / replace", ToolUi.Inline(find, replace));
                ToolUi.Wide(body, rbNumber);
                ToolUi.Field(body, "Start / step", ToolUi.Inline(start, step));
                ToolUi.Wide(body, rbTemplate);
                ToolUi.Field(body, "Template", template);
                ToolUi.Wide(body, ToolUi.Note("Template tokens: {old} the name now, {tile}, {part}, {n} / {n:000} the order (from 1), {title} the sheet title. " +
                                              "Drawing numbers in the title blocks and match lines on every sheet follow."));
                ToolUi.Wide(body, preview);
                Update();
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Dictionary<SheetRecord, string> todo = plan.Where(kv => kv.Value != kv.Key.Layout).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (todo.Count == 0) return;
                Change("Renaming", tr =>
                {
                    List<string> log = SheetManager.Rename(tr, _doc.Database, _sheets, todo, P);
                    _selected.Clear(); foreach (string n in todo.Values) _selected.Add(n);
                    return $"Renamed {log.Count} sheet(s).";
                });
            }
        }

        private string Template(string template, SheetRecord s, int index)
        {
            string text = template ?? "";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\{n(?::(0+|\d))?\}", m =>
            {
                string f = m.Groups[1].Value;
                int pad = f.Length == 0 ? 1 : f.All(c => c == '0') ? f.Length : int.Parse(f, CultureInfo.InvariantCulture);
                return (index + 1).ToString(new string('0', pad), CultureInfo.InvariantCulture);
            });
            return text.Replace("{old}", s.Layout).Replace("{tile}", s.Tile).Replace("{part}", s.Part).Replace("{title}", s.Attr(P.SheetTitleTag)).Trim();
        }

        private void DeleteSheets()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0) return;
            if (MessageBox.Show(this, $"Delete {chosen.Count} sheet(s)?\n\n" + string.Join("\n", chosen.Take(15).Select(s => "• " + s.Layout)) + (chosen.Count > 15 ? "\n…" : "") +
                                      "\n\nUNDO brings them back.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Change("Deleting", tr => { SheetManager.Delete(chosen, _sheets); _selected.Clear(); return $"Deleted {chosen.Count} sheet(s)."; });
        }

        private void UpdateKeyPlans()
        {
            List<SheetRecord> chosen = Chosen(false);
            if (chosen.Count == 0) chosen = _sheets;
            Change("Updating key plans", tr =>
            {
                List<string> log = new List<string>();
                int n = chosen.Count(s => s.Tile.Length > 0 && s.KeyTexts.Count > 0 && SheetManager.UpdateKeyPlan(tr, s, P, log));
                return $"Key plans updated on {n} sheet(s)." + (log.Count > 0 ? $" {log.Count} note(s): " + string.Join(" ", log.Take(3)) : "");
            });
        }

        private void FillMatchLines()
        {
            List<SheetRecord> chosen = Chosen(false);
            if (chosen.Count == 0) chosen = _sheets;
            Change("Filling match lines", tr => $"Match lines filled on {SheetManager.FillMatchLines(tr, _sheets, chosen, P)} sheet(s) (neighbours within each tile; other tiles' references are kept).");
        }

        // =====================================================================================
        // Title block page
        // =====================================================================================

        private Control BuildTitlePage()
        {
            Control top = Bar(StyleUi.Secondary("Fields…", (s, e) => ChooseFields()), StyleUi.Secondary("Fill down", (s, e) => FillDown()),
                StyleUi.Secondary("Set value…", (s, e) => SetValue()), StyleUi.Primary("Apply changes", (s, e) => ApplyEdits()), StyleUi.Secondary("Discard", (s, e) => { _edits.Clear(); FillTitle(); }));
            Control more = Bar(StyleUi.Secondary("Add revision…", (s, e) => AddRevision()), StyleUi.Secondary("Export register…", (s, e) => ExportRegister()),
                StyleUi.Secondary("Import register…", (s, e) => ImportRegister()),
                Hint("Edit cells like a spreadsheet; changes show yellow until applied. The register is a CSV that Excel opens and saves."));
            _gridTitle.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _gridTitle.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _gridTitle.CellEndEdit += (s, e) => TitleCellEdited(e.RowIndex, e.ColumnIndex);
            _gridTitle.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.V) { PasteCells(); e.Handled = true; } };
            TrackSelection(_gridTitle);
            return GridPage(_gridTitle, top, more);
        }

        private void FillTitle()
        {
            _filling = true;
            _gridTitle.Columns.Clear();
            _gridTitle.Rows.Clear();
            _gridTitle.Columns.Add(new DataGridViewTextBoxColumn { Name = "layout", HeaderText = "Layout", ReadOnly = true, Frozen = true, Width = 300, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (string tag in P.TableTags)
                _gridTitle.Columns.Add(new DataGridViewTextBoxColumn { Name = tag, HeaderText = tag, Width = tag.Contains("TITLE") || tag.Contains("NUMBER") || tag.Contains("DESC") ? 260 : 120, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (SheetRecord s in _sheets)
            {
                DataGridViewRow row = new DataGridViewRow();
                row.CreateCells(_gridTitle);
                row.Cells[0].Value = s.Layout;
                for (int c = 0; c < P.TableTags.Count; c++)
                {
                    string tag = P.TableTags[c];
                    DataGridViewCell cell = row.Cells[c + 1];
                    if (!s.HasAttr(tag)) { cell.Value = ""; cell.ReadOnly = true; cell.Style.BackColor = StyleUi.Surface; continue; }
                    bool edited = _edits.TryGetValue((s.Layout, tag), out string v);
                    cell.Value = edited ? v : s.Attr(tag);
                    if (edited) cell.Style.BackColor = Color.LightYellow;
                }
                row.Tag = s;
                _gridTitle.Rows.Add(row);
            }
            _filling = false;
        }

        private void TitleCellEdited(int r, int c)
        {
            if (_filling || r < 0 || c < 1 || !(_gridTitle.Rows[r].Tag is SheetRecord s)) return;
            string tag = _gridTitle.Columns[c].Name;
            string value = _gridTitle.Rows[r].Cells[c].Value?.ToString() ?? "";
            Edit(s, tag, value, _gridTitle.Rows[r].Cells[c]);
        }

        private void Edit(SheetRecord s, string tag, string value, DataGridViewCell cell)
        {
            if (!s.HasAttr(tag)) return;
            if (value == s.Attr(tag)) { _edits.Remove((s.Layout, tag)); cell.Style.BackColor = Color.Empty; }
            else { _edits[(s.Layout, tag)] = value; cell.Style.BackColor = Color.LightYellow; }
            cell.Value = value;
            SetStatus($"{_edits.Count} change(s) to apply.");
        }

        /// <summary>The top selected cell's value in each column goes to the other selected cells below it.</summary>
        private void FillDown()
        {
            foreach (IGrouping<int, DataGridViewCell> col in _gridTitle.SelectedCells.Cast<DataGridViewCell>().Where(c => c.ColumnIndex > 0).GroupBy(c => c.ColumnIndex))
            {
                List<DataGridViewCell> cells = col.OrderBy(c => c.RowIndex).ToList();
                string value = cells[0].Value?.ToString() ?? "";
                foreach (DataGridViewCell c in cells.Skip(1)) if (!c.ReadOnly && _gridTitle.Rows[c.RowIndex].Tag is SheetRecord s) Edit(s, _gridTitle.Columns[c.ColumnIndex].Name, value, c);
            }
        }

        private void SetValue()
        {
            List<DataGridViewCell> cells = _gridTitle.SelectedCells.Cast<DataGridViewCell>().Where(c => c.ColumnIndex > 0 && !c.ReadOnly).ToList();
            if (cells.Count == 0) { MessageBox.Show(this, "Select the cells to set (one column or several).", Text); return; }
            string value = FormPartsLists.AskText(this, "Set value", $"Value for {cells.Count} cell(s)", cells[0].Value?.ToString() ?? "");
            if (value == null) return;
            foreach (DataGridViewCell c in cells) if (_gridTitle.Rows[c.RowIndex].Tag is SheetRecord s) Edit(s, _gridTitle.Columns[c.ColumnIndex].Name, value, c);
        }

        /// <summary>Pastes a block copied from Excel, starting at the current cell.</summary>
        private void PasteCells()
        {
            if (_gridTitle.CurrentCell == null || !Clipboard.ContainsText()) return;
            string[] lines = Clipboard.GetText().Replace("\r", "").TrimEnd('\n').Split('\n');
            int r0 = _gridTitle.CurrentCell.RowIndex, c0 = Math.Max(1, _gridTitle.CurrentCell.ColumnIndex);
            for (int i = 0; i < lines.Length && r0 + i < _gridTitle.Rows.Count; i++)
            {
                string[] values = lines[i].Split('\t');
                for (int j = 0; j < values.Length && c0 + j < _gridTitle.Columns.Count; j++)
                {
                    DataGridViewCell cell = _gridTitle.Rows[r0 + i].Cells[c0 + j];
                    if (!cell.ReadOnly && _gridTitle.Rows[r0 + i].Tag is SheetRecord s) Edit(s, _gridTitle.Columns[c0 + j].Name, values[j], cell);
                }
            }
        }

        private void ApplyEdits()
        {
            if (_edits.Count == 0) { SetStatus("No changes to apply."); return; }
            Dictionary<(string, string), string> edits = new Dictionary<(string, string), string>(_edits);
            // The drawing number is the layout name: changing it renames the sheet.
            Dictionary<SheetRecord, string> renames = edits.Where(e => e.Key.Item2.Equals(P.NumberTag, StringComparison.OrdinalIgnoreCase) && Sheet(e.Key.Item1) != null)
                                                           .ToDictionary(e => Sheet(e.Key.Item1), e => e.Value.Trim());
            if (Change("Applying title block changes", tr =>
            {
                int n = 0;
                foreach (IGrouping<string, KeyValuePair<(string, string), string>> g in edits.Where(e => !e.Key.Item2.Equals(P.NumberTag, StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.Key.Item1))
                    if (Sheet(g.Key) is SheetRecord s) n += SheetManager.SetAttributes(tr, s, g.ToDictionary(e => e.Key.Item2, e => e.Value));
                if (renames.Count > 0) SheetManager.Rename(tr, _doc.Database, _sheets, renames, P);
                return $"{n} field(s) changed" + (renames.Count > 0 ? $", {renames.Count} sheet(s) renamed to their new drawing number." : ".");
            })) _edits.Clear();
            FillTitle();
            ApplySelection(_gridTitle);
        }

        private void ChooseFields()
        {
            List<string> tags = SheetManager.AllTags(_sheets);
            if (tags.Count == 0) { MessageBox.Show(this, "No title block found (see Settings).", Text); return; }
            using (Form f = FormPartsLists.SmallDialog("Title block fields", out TableLayoutPanel body, out Button ok))
            {
                CheckedListBox list = new CheckedListBox { CheckOnClick = true, Height = 380, Width = 360, IntegralHeight = false };
                foreach (string t in P.TableTags.Where(tags.Contains).Concat(tags.Where(t => !P.TableTags.Contains(t)))) list.Items.Add(t, P.TableTags.Contains(t));
                ToolUi.Wide(body, ToolUi.Note("The columns shown, in this order (ticked ones first)."));
                ToolUi.Wide(body, list);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                P.TableTags = list.CheckedItems.Cast<string>().ToList();
                _s.Save();
                FillTitle();
            }
        }

        private void AddRevision()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0) return;
            using (Form f = FormPartsLists.SmallDialog($"Add a revision to {chosen.Count} sheet(s)", out TableLayoutPanel body, out Button ok))
            {
                TextBox code = new TextBox { Width = 90 }, date = new TextBox { Width = 110, Text = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) },
                        desc = new TextBox { Width = 320, Text = _s.RevisionDescription }, by = new TextBox { Width = 90, Text = _s.RevisionBy },
                        chk = new TextBox { Width = 90, Text = _s.RevisionChecked }, app = new TextBox { Width = 90, Text = _s.RevisionApproved };
                RadioButton fill = new RadioButton { Text = "Fill the next empty revision row", AutoSize = true, Checked = true }, shift = new RadioButton { Text = "Shift the rows down (newest in row 1, oldest dropped)", AutoSize = true };
                CheckBox setDate = ToolUi.Check($"Also set {P.DateTag} to this date", true);
                FlowLayoutPanel mode = ToolUi.Inline(fill, shift);
                mode.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;
                ToolUi.Field(body, "Revision", ToolUi.Inline(code, new Label { Text = "empty = each sheet's next (C01 → C02)", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(6, 7, 0, 0) }));
                ToolUi.Field(body, "Date", date);
                ToolUi.Field(body, "Description", desc);
                ToolUi.Field(body, "Drawn / checked / approved", ToolUi.Inline(by, chk, app));
                ToolUi.Wide(body, mode);
                ToolUi.Wide(body, setDate);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _s.RevisionDescription = desc.Text; _s.RevisionBy = by.Text; _s.RevisionChecked = chk.Text; _s.RevisionApproved = app.Text;
                SheetManager.Revision rev = new SheetManager.Revision { Code = code.Text, Date = date.Text, Description = desc.Text, By = by.Text, Checked = chk.Text, Approved = app.Text, Shift = shift.Checked, SetDate = setDate.Checked };
                Change("Adding the revision", tr =>
                {
                    List<string> codes = new List<string>(), problems = new List<string>();
                    foreach (SheetRecord s in chosen)
                    {
                        string c = SheetManager.AddRevision(tr, s, rev, P, out string problem);
                        if (c != null) codes.Add(c); else problems.Add($"{Short(s.Layout)}: {problem}");
                    }
                    if (problems.Count > 0) MessageBox.Show(this, "Not revised:\n" + string.Join("\n", problems.Take(20)), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return $"Revision added to {codes.Count} sheet(s): {string.Join(", ", codes.Distinct())}.";
                });
            }
        }

        private void ExportRegister()
        {
            if (_sheets.Count == 0) return;
            using (SaveFileDialog d = new SaveFileDialog { Filter = "Register for Excel (*.csv)|*.csv", FileName = Path.GetFileNameWithoutExtension(_doc?.Name ?? "Sheets") + " register.csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                List<string> tags = SheetManager.AllTags(_sheets);
                List<string> order = P.TableTags.Where(tags.Contains).Concat(tags.Where(t => !P.TableTags.Contains(t))).ToList();
                try { SheetManager.ExportRegister(d.FileName, _sheets, order); SetStatus($"Register written: {d.FileName}"); }
                catch (Exception ex) { MessageBox.Show(this, "The register could not be written: " + ex.Message, Text); }
            }
        }

        /// <summary>Reads a register back: its values become edits (yellow) to review and apply.</summary>
        private void ImportRegister()
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "Register (*.csv)|*.csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Dictionary<string, Dictionary<string, string>> rows;
                List<string> tags;
                try { rows = SheetManager.ReadRegister(d.FileName, out tags); }
                catch (Exception ex) { MessageBox.Show(this, "The register could not be read: " + ex.Message, Text); return; }
                int n = 0, missing = 0;
                foreach (KeyValuePair<string, Dictionary<string, string>> row in rows)
                {
                    SheetRecord s = Sheet(row.Key);
                    if (s == null) { missing++; continue; }
                    foreach (string tag in tags)
                        if (s.HasAttr(tag) && row.Value.TryGetValue(tag, out string v) && v != s.Attr(tag)) { _edits[(s.Layout, tag)] = v; n++; }
                }
                foreach (string tag in tags.Where(t => !P.TableTags.Contains(t) && _edits.Keys.Any(k => k.tag == t))) P.TableTags.Add(tag);
                FillTitle();
                SetStatus($"{n} change(s) read from the register" + (missing > 0 ? $"; {missing} row(s) name no sheet of this drawing" : "") + ". Review them (yellow) and Apply.");
            }
        }

        // =====================================================================================
        // Viewports page
        // =====================================================================================

        private Control BuildViewportsPage()
        {
            foreach (string sc in new[] { "1:100", "1:200", "1:250", "1:500", "1:750", "1:1000", "1:1250", "1:2000", "1:2500", "1:5000" }) _cbScale.Items.Add(sc);
            _cbScale.DropDownStyle = ComboBoxStyle.DropDown;
            _cbScale.Text = "1:500";
            Control scale = Bar(StyleUi.Secondary("Write viewport scale on the sheet", (s, e) => ForChosen("Writing the scale", (tr, x) => SheetManager.SyncScale(tr, x, P))),
                Hint("Set to"), _cbScale, StyleUi.Secondary("Set scale", (s, e) => SetScale()),
                StyleUi.Secondary("Lock", (s, e) => ForChosen("Locking", (tr, x) => { SheetManager.SetLocked(tr, x, true); return true; })),
                StyleUi.Secondary("Unlock", (s, e) => ForChosen("Unlocking", (tr, x) => { SheetManager.SetLocked(tr, x, false); return true; })));
            Control layers = Bar(Hint("Freeze the same layers as"), _cbLayerSource, StyleUi.Secondary("Copy layer setup to the selected", (s, e) => CopyLayers()));
            _gridViews.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _gridViews.ReadOnly = true;
            foreach ((string name, string head, int w) in new[] { ("layout", "Layout", 40), ("vp", "Viewport scale", 10), ("title", "Title block scale", 10), ("note", "Scale note", 10), ("twist", "Twist (°)", 8), ("locked", "Locked", 7), ("frozen", "Frozen layers", 8) })
                _gridViews.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = head, FillWeight = w, SortMode = DataGridViewColumnSortMode.NotSortable });
            _gridViews.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) GoTo(_gridViews.Rows[e.RowIndex].Tag as SheetRecord); };
            TrackSelection(_gridViews);
            return GridPage(_gridViews, scale, layers);
        }

        private void FillViews()
        {
            _filling = true;
            _gridViews.Rows.Clear();
            foreach (SheetRecord s in _sheets)
            {
                string note = System.Text.RegularExpressions.Regex.Match(s.ScaleText, @"1\s*:\s*[\d.,]+").Value;
                int i = _gridViews.Rows.Add(s.Layout, s.ViewportId.IsNull ? "none" : s.ViewportScale, s.Attr(P.ScaleTag), note, s.ViewportId.IsNull ? "" : (s.Twist * 180 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture),
                    s.ViewportId.IsNull ? "" : s.Locked ? "yes" : "NO", s.ViewportId.IsNull ? "" : s.FrozenLayers.ToString(CultureInfo.InvariantCulture));
                DataGridViewRow row = _gridViews.Rows[i];
                row.Tag = s;
                string shown = s.Attr(P.ScaleTag).Replace(" ", "");
                if (shown.Length > 0 && s.ViewportScale.Length > 0 && shown != s.ViewportScale) row.Cells["vp"].Style.ForeColor = Color.Firebrick;
                if (!s.ViewportId.IsNull && !s.Locked) row.Cells["locked"].Style.ForeColor = Color.Firebrick;
                row.Selected = _selected.Contains(s.Layout);
            }
            _filling = false;
        }

        private void FillLayerSources()
        {
            string keep = _cbLayerSource.SelectedItem as string;
            _cbLayerSource.Items.Clear();
            foreach (SheetRecord s in _sheets.Where(s => !s.ViewportId.IsNull)) _cbLayerSource.Items.Add(s.Layout);
            if (keep != null && _cbLayerSource.Items.Contains(keep)) _cbLayerSource.SelectedItem = keep;
            else if (_cbLayerSource.Items.Count > 0) _cbLayerSource.SelectedIndex = 0;
        }

        private void ForChosen(string what, Func<Transaction, SheetRecord, bool> work)
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0) return;
            Change(what, tr => $"{what}: {chosen.Count(s => work(tr, s))} of {chosen.Count} sheet(s) changed.");
        }

        private void SetScale()
        {
            string text = _cbScale.Text.Replace(" ", "");
            if (!(text.StartsWith("1:") && double.TryParse(text.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0))
            { MessageBox.Show(this, "Write the scale as 1:500.", Text); return; }
            ForChosen("Setting the scale", (tr, s) => { if (s.ViewportId.IsNull) return false; SheetManager.SetScale(tr, s, n, P); return true; });
        }

        private void CopyLayers()
        {
            SheetRecord from = Sheet(_cbLayerSource.SelectedItem as string);
            List<SheetRecord> chosen = Chosen();
            if (from == null || chosen.Count == 0) return;
            if (MessageBox.Show(this, $"The main viewport of {chosen.Count} sheet(s) will freeze exactly the {from.FrozenLayers} layer(s) frozen in {from.Layout}. Go on?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Change("Copying the layer setup", tr => { foreach (SheetRecord s in chosen) SheetManager.CopyFrozenLayers(tr, from, s); return $"Layer setup of {Short(from.Layout)} copied to {chosen.Count} sheet(s)."; });
        }

        // =====================================================================================
        // Checks page
        // =====================================================================================

        private Control BuildChecksPage()
        {
            Control top = Bar(StyleUi.Primary("Check again", (s, e) => ReadDrawing()), StyleUi.Secondary("Fix selected", (s, e) => Fix(false)), StyleUi.Secondary("Fix all that can be fixed", (s, e) => Fix(true)),
                Hint("Double-click a row to go to its sheet."));
            _gridChecks.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _gridChecks.ReadOnly = true;
            foreach ((string name, string head, int w) in new[] { ("sheet", "Sheet", 30), ("problem", "Problem", 50), ("fix", "Fix", 22) })
                _gridChecks.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = head, FillWeight = w, SortMode = DataGridViewColumnSortMode.NotSortable });
            _gridChecks.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _gridChecks.Rows[e.RowIndex].Tag is SheetIssue i) GoTo(Sheet(i.Layout)); };
            return GridPage(_gridChecks, top);
        }

        private void FillChecks()
        {
            _gridChecks.Rows.Clear();
            foreach (SheetIssue i in _issues)
            {
                int r = _gridChecks.Rows.Add(i.Layout.Length > 0 ? i.Layout : "(drawing)", i.Text, i.FixText ?? "");
                _gridChecks.Rows[r].Tag = i;
            }
        }

        private void Fix(bool all)
        {
            List<SheetIssue> todo = all ? _issues.Where(i => i.Fix != null).ToList()
                : _gridChecks.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as SheetIssue).Where(i => i?.Fix != null).ToList();
            if (todo.Count == 0) { SetStatus("Nothing selected that can be fixed automatically."); return; }
            if (MessageBox.Show(this, $"Fix {todo.Count} issue(s)?\n\n" + string.Join("\n", todo.GroupBy(i => i.FixText).Select(g => $"• {g.Key}: {g.Count()}")), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Change("Fixing", tr => { foreach (SheetIssue i in todo) i.Fix(tr); return $"Fixed {todo.Count} issue(s)."; });
            _nav.Selected = PChecks;
        }

        // =====================================================================================
        // Publish page
        // =====================================================================================

        private Control BuildPublishPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel setup = ToolUi.Card("Page setup", "Gives the selected sheets a named page setup of this drawing (device, paper, plot style, area).");
            ToolUi.Field(setup, "Page setup", _cbPageSetup);
            ToolUi.Wide(setup, StyleUi.Secondary("Apply to the selected sheets", (s, e) => ApplyPageSetup()));
            ToolUi.Add(stack, setup);

            TableLayoutPanel pdf = ToolUi.Card("PDF", "Plots the selected sheets in tab order with their own page setups. Select sheets on the Sheets page.");
            Button browse = StyleUi.Secondary("Browse…", (s, e) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { SelectedPath = _txtPdfFolder.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) _txtPdfFolder.Text = d.SelectedPath;
            });
            ToolUi.Field(pdf, "Folder", ToolUi.Inline(_txtPdfFolder, browse));
            ToolUi.Field(pdf, "File name", _txtPdfName, _tips, "Tokens: {layout}, {tile}, {part}, {rev}, {title}.");
            ToolUi.Wide(pdf, ToolUi.Note("Name tokens: {layout} the drawing number, {tile}, {part}, {rev} the current revision, {title} the sheet title. For one combined PDF the first sheet names it."));
            FlowLayoutPanel how = ToolUi.Inline(_rbPdfEach, _rbPdfOne);
            ToolUi.Field(pdf, "Files", how);
            ToolUi.Wide(pdf, _chkPdfForce);
            ToolUi.Wide(pdf, _chkPdfOpen);
            ToolUi.Wide(pdf, StyleUi.Primary("Publish the selected sheets", (s, e) => Publish()));
            ToolUi.Add(stack, pdf);
            return host;
        }

        private void FillPageSetups(List<string> names)
        {
            string keep = _cbPageSetup.SelectedItem as string ?? _s.PageSetup;
            _cbPageSetup.Items.Clear();
            foreach (string n in names) _cbPageSetup.Items.Add(n);
            if (keep != null && _cbPageSetup.Items.Contains(keep)) _cbPageSetup.SelectedItem = keep;
            else if (_cbPageSetup.Items.Count > 0) _cbPageSetup.SelectedIndex = 0;
        }

        private void ApplyPageSetup()
        {
            string name = _cbPageSetup.SelectedItem as string;
            List<SheetRecord> chosen = Chosen();
            if (name == null || chosen.Count == 0) { if (name == null) MessageBox.Show(this, "The drawing has no named page setups (Page Setup Manager).", Text); return; }
            Change("Applying the page setup", tr => { foreach (SheetRecord s in chosen) SheetManager.ApplyPageSetup(tr, _doc.Database, s, name); return $"\"{name}\" applied to {chosen.Count} sheet(s)."; });
        }

        private void Publish()
        {
            List<SheetRecord> chosen = Chosen();
            if (chosen.Count == 0 || !Ready()) return;
            string folder = _txtPdfFolder.Text.Trim();
            if (folder.Length == 0 && !string.IsNullOrEmpty(_doc.Database.Filename)) folder = Path.Combine(Path.GetDirectoryName(_doc.Database.Filename), "PDF");
            if (folder.Length == 0) { MessageBox.Show(this, "Choose the folder for the PDF files.", Text); return; }
            _txtPdfFolder.Text = folder;
            SaveSettings();
            _busy = true;
            try
            {
                Cursor = Cursors.WaitCursor;
                List<string> files;
                using (_doc.LockDocument())
                    files = SheetManager.Publish(_doc.Database, chosen, folder, _txtPdfName.Text, _rbPdfOne.Checked, _chkPdfForce.Checked, P, t => { SetStatus(t); _lblStatus.Refresh(); });
                SetStatus($"{files.Count} PDF file(s) written to {folder}.");
                if (_chkPdfOpen.Checked) try { System.Diagnostics.Process.Start("explorer.exe", folder); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            catch (Exception ex) { MessageBox.Show(this, "Publishing stopped: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _busy = false; Cursor = Cursors.Default; }
        }

        // =====================================================================================
        // Settings page
        // =====================================================================================

        private static readonly (string card, string key, string label, string tip)[] ProfileFields =
        {
            ("Title block", nameof(SheetProfile.TitleBlock), "Block name", "Empty: the block with the most attributes on each sheet."),
            ("Title block", nameof(SheetProfile.NumberTag), "Drawing number tag", "Kept equal to the layout name."),
            ("Title block", nameof(SheetProfile.SheetTitleTag), "Sheet title tag", null),
            ("Title block", nameof(SheetProfile.SheetTitleTemplate), "Sheet title template", "{tile} and {part}. A title of another form (profile sheets) is never overwritten."),
            ("Title block", nameof(SheetProfile.RevisionTag), "Current revision tag", null),
            ("Title block", nameof(SheetProfile.StageTag), "Stage tag", null),
            ("Title block", nameof(SheetProfile.DateTag), "Date tag", null),
            ("Title block", nameof(SheetProfile.ScaleTag), "Scale tag", null),
            ("Title block", nameof(SheetProfile.DrawnTag), "Drawn by tag", null),
            ("Title block", nameof(SheetProfile.CheckedTag), "Checked by tag", null),
            ("Title block", nameof(SheetProfile.ApprovedTag), "Approved by tag", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevDateTag), "Date", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevCodeTag), "Code", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevDescTag), "Description", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevNoTag), "Number", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevChkTag), "Checked", null),
            ("Revision rows ({i} = row number)", nameof(SheetProfile.RevAppTag), "Approved", null),
            ("Match lines", nameof(SheetProfile.MatchRightTag), "Right tag", null),
            ("Match lines", nameof(SheetProfile.MatchLeftTag), "Left tag", null),
            ("Match lines", nameof(SheetProfile.MatchUpTag), "Up tag", null),
            ("Match lines", nameof(SheetProfile.MatchDownTag), "Down tag", null),
            ("Match lines", nameof(SheetProfile.MatchTemplate), "Text", "{number} is the neighbour's drawing number."),
            ("Sheet texts and tiles", nameof(SheetProfile.SheetIdPattern), "Sheet ID pattern", "A regular expression with groups \"tile\" and \"part\"."),
            ("Sheet texts and tiles", nameof(SheetProfile.SheetIdTemplate), "Sheet ID text", "{tile} and {part}."),
            ("Sheet texts and tiles", nameof(SheetProfile.PartTextPattern), "Part letter pattern", "The letter shown on its own (title circle)."),
            ("Sheet texts and tiles", nameof(SheetProfile.KeyPlanPattern), "Key plan labels", "Groups \"tile\" and \"part\"; the hatch over a label marks the sheet."),
            ("Sheet texts and tiles", nameof(SheetProfile.PartGrid), "Parts of a tile", "Rows from the top, separated by \"/\": AB/CD = A, B on top, C, D below."),
            ("Sheet texts and tiles", nameof(SheetProfile.ScaleTextPattern), "Scale note pattern", "Its 1:N is rewritten from the viewport."),
            ("Sheet texts and tiles", nameof(SheetProfile.NumberPattern), "Running number", "The number renumbering changes (the last digits by default)."),
        };

        private Control BuildSettingsPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel actions = ToolUi.Card("Profile", "Where each sheet keeps its data. Detect reads it from the current sheet; export a profile to use it in other drawings and projects.");
            ToolUi.Wide(actions, ToolUi.Inline(StyleUi.Primary("Detect from the current sheet", (s, e) => DetectProfile()), StyleUi.Secondary("Save", (s, e) => { ReadProfile(); _s.Save(); ReadDrawing(); }),
                StyleUi.Secondary("Export…", (s, e) => ExportProfile()), StyleUi.Secondary("Import…", (s, e) => ImportProfile()), StyleUi.Secondary("Defaults", (s, e) => { _s.Profile = new SheetProfile(); ShowProfile(); ReadDrawing(); })));
            ToolUi.Add(stack, actions);
            foreach (IGrouping<string, (string card, string key, string label, string tip)> g in ProfileFields.GroupBy(f => f.card))
            {
                TableLayoutPanel card = ToolUi.Card(g.Key);
                foreach ((string _, string key, string label, string tip) in g)
                {
                    TextBox box = new TextBox { Width = 360 };
                    _profileBoxes[key] = box;
                    ToolUi.Field(card, label, box, _tips, tip);
                }
                if (g.Key == "Revision rows ({i} = row number)")
                {
                    TextBox rows = new TextBox { Width = 60 };
                    _profileBoxes[nameof(SheetProfile.RevisionRows)] = rows;
                    ToolUi.Field(card, "Rows", rows);
                }
                if (g.Key == "Title block")
                {
                    TextBox req = new TextBox { Width = 360 };
                    _profileBoxes[nameof(SheetProfile.RequiredTags)] = req;
                    ToolUi.Field(card, "Required tags", req, _tips, "Comma separated; Checks reports them when empty.");
                }
                ToolUi.Add(stack, card);
            }
            return host;
        }

        private void ShowProfile()
        {
            foreach (KeyValuePair<string, TextBox> kv in _profileBoxes)
            {
                object v = typeof(SheetProfile).GetProperty(kv.Key).GetValue(P);
                kv.Value.Text = v is List<string> l ? string.Join(", ", l) : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private void ReadProfile()
        {
            foreach (KeyValuePair<string, TextBox> kv in _profileBoxes)
            {
                var prop = typeof(SheetProfile).GetProperty(kv.Key);
                if (prop.PropertyType == typeof(int)) { if (int.TryParse(kv.Value.Text, out int n) && n > 0) prop.SetValue(P, n); }
                else if (prop.PropertyType == typeof(List<string>)) prop.SetValue(P, kv.Value.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList());
                else prop.SetValue(P, kv.Value.Text.Trim());
            }
            foreach (string key in new[] { nameof(SheetProfile.SheetIdPattern), nameof(SheetProfile.PartTextPattern), nameof(SheetProfile.KeyPlanPattern), nameof(SheetProfile.ScaleTextPattern), nameof(SheetProfile.NumberPattern) })
                if (_profileBoxes[key].Text.Trim().Length > 0 && SheetManager.Rx(_profileBoxes[key].Text) == null)
                    MessageBox.Show(this, $"\"{_profileBoxes[key].Text}\" is not a valid pattern ({key}).", Text);
        }

        private void DetectProfile()
        {
            if (!Ready()) return;
            try
            {
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    ObjectId id = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                    Layout lay = (Layout)tr.GetObject(id, OpenMode.ForRead);
                    if (lay.ModelType) { MessageBox.Show(this, "Make a sheet current first (not model space).", Text); return; }
                    _s.Profile = SheetManager.Detect(tr, lay, P);
                    tr.Commit();
                }
                ShowProfile();
                _s.Save();
                ReadDrawing();
                SetStatus($"Profile read from the current sheet: title block \"{P.TitleBlock}\".");
            }
            catch (Exception ex) { MessageBox.Show(this, "Detection failed: " + ex.Message, Text); }
        }

        private void ExportProfile()
        {
            ReadProfile();
            using (SaveFileDialog d = new SaveFileDialog { Filter = "Sheet profile (*.json)|*.json", FileName = "Sheet profile.json" })
                if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, JsonSerializer.Serialize(P, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void ImportProfile()
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "Sheet profile (*.json)|*.json" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { _s.Profile = JsonSerializer.Deserialize<SheetProfile>(File.ReadAllText(d.FileName)) ?? new SheetProfile(); }
                catch (Exception ex) { MessageBox.Show(this, "The profile could not be read: " + ex.Message, Text); return; }
                ShowProfile();
                _s.Save();
                ReadDrawing();
            }
        }

        // =====================================================================================
        // Settings
        // =====================================================================================

        private void ApplySettings()
        {
            ShowProfile();
            _txtPdfFolder.Text = _s.PdfFolder;
            _txtPdfName.Text = _s.PdfName;
            _rbPdfOne.Checked = _s.PdfOneFile;
            _rbPdfEach.Checked = !_s.PdfOneFile;
            _chkPdfForce.Checked = _s.PdfForceDevice;
            _chkPdfOpen.Checked = _s.PdfOpenFolder;
        }

        private void SaveSettings()
        {
            _s.PdfFolder = _txtPdfFolder.Text.Trim();
            _s.PdfName = _txtPdfName.Text.Trim();
            _s.PdfOneFile = _rbPdfOne.Checked;
            _s.PdfForceDevice = _chkPdfForce.Checked;
            _s.PdfOpenFolder = _chkPdfOpen.Checked;
            _s.PageSetup = _cbPageSetup.SelectedItem as string ?? _s.PageSetup;
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Sheets: every layout with its drawing number (the layout name), tile and part, title, revision, stage, scale and page setup. " +
                "Edit the name, tile or part in the list: the title block's drawing number and sheet title, the sheet ID text (SHEET-105-A), the part letter, " +
                "the key plan (labels, tile number, hatch) and the match lines on every sheet follow. Duplicate, complete a tile (its missing parts with the right numbers), " +
                "create from a template (this drawing or another DWG / DWT), rename by find / replace, renumber or template, delete, move and sort.\n\n" +
                "Title block: the fields of every sheet in one table; edit, fill down, set a value, paste from Excel, then apply. Add a revision to many sheets at once. " +
                "Export the register to Excel (CSV) and read it back.\n\n" +
                "Viewports: the main viewport's scale against the title block and scale note; set a scale, write it on the sheet, lock, copy frozen layers between sheets.\n\n" +
                "Checks: drawing numbers, sheet titles, part letters, key plans, scales, empty fields, unlocked viewports, match lines to missing drawings; most can be fixed in one go.\n\n" +
                "Publish: apply a page setup to many sheets; plot the selected sheets to PDF, one file each or one combined file, named from their fields.\n\n" +
                "Settings: where the title block, tags, sheet texts and key plan are; detect them from the current sheet; export the profile for other projects.\n\n" +
                "Profile and detail sheets (no sheet ID text) keep their own titles; only the tile parts are written. Each change is one UNDO.",
                "Sheet Manager - How it works", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
