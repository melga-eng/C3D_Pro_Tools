using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DProTools.ProfileTool
{
    /// <summary>Modal dialog: every profile view, filter/sort, fit TOP profiles to duct tops, create missing ones.</summary>
    public sealed class ProfileViewManagerForm : Form
    {
        private const string Any = "(all)";

        private readonly Document _doc;
        private readonly CivilDocument _civil;
        private TopScan _scan;

        // scan options
        private readonly TextBox _pattern = new() { Text = "TOP", Width = 70 };
        private readonly TextBox _tol = new() { Text = "0.30", Width = 45 };
        private readonly ComboBox _end = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly CheckBox _shownOnly = new() { Text = "Only ducts shown in the views", AutoSize = true, Checked = true };
        private readonly Button _rescan = new() { Text = "Rescan", AutoSize = true };

        // filters
        private readonly TextBox _search = new() { Width = 160, PlaceholderText = "view / alignment / profile" };
        private readonly ComboBox _hasTop = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox _status = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ComboBox _network = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };

        private readonly DataGridView _grid = new();
        private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 9f) };

        private readonly Button _checkShown = new() { Text = "Check shown", AutoSize = true };
        private readonly Button _uncheck = new() { Text = "Uncheck all", AutoSize = true };
        private readonly Button _zoom = new() { Text = "Zoom to view", AutoSize = true };
        private readonly Button _fit = new() { Text = "Fit TOP to ducts", AutoSize = true };

        private readonly TextBox _cName = new() { Text = "{alignment}-Duct-TOP", Width = 170 };
        private readonly ComboBox _cStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly ComboBox _cLabels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox _cLayer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly Button _create = new() { Text = "Create missing TOP", AutoSize = true };

        private readonly Label _summary = new() { AutoSize = true, ForeColor = Color.DimGray };

        private sealed class Item
        {
            public ObjectId Id;
            public string Name;
            public override string ToString() => Name;
        }

        public ProfileViewManagerForm(Document doc, CivilDocument civil)
        {
            _doc = doc;
            _civil = civil;
            Text = "Profile Views and TOP Profiles";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 820);
            MinimumSize = new Size(900, 600);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            _end.Items.AddRange(new object[] { "Structure outer face", "Structure inside edge" });
            _end.SelectedIndex = 0;
            _hasTop.Items.AddRange(new object[] { Any, "Has TOP profile", "Missing TOP profile" });
            _hasTop.SelectedIndex = 0;
            _status.Items.AddRange(new object[] { Any, AlignmentFit.StatusFits, AlignmentFit.StatusDiffers, AlignmentFit.StatusMissing, AlignmentFit.StatusNoDucts });
            _status.SelectedIndex = 0;

            BuildGrid();
            Controls.Add(BuildLayout());

            _rescan.Click += (_, _) => Rescan();
            _search.TextChanged += (_, _) => FillGrid();
            _hasTop.SelectedIndexChanged += (_, _) => FillGrid();
            _status.SelectedIndexChanged += (_, _) => FillGrid();
            _network.SelectedIndexChanged += (_, _) => FillGrid();
            _checkShown.Click += (_, _) => SetChecks(true);
            _uncheck.Click += (_, _) => SetChecks(false);
            _zoom.Click += (_, _) => ZoomSelected();
            _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ZoomSelected(); };
            _grid.SelectionChanged += (_, _) => ShowDetails();
            _fit.Click += (_, _) => FitChecked();
            _create.Click += (_, _) => CreateChecked();
            _grid.CellValueChanged += (_, _) => UpdateButtons();
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            Rescan();
            FillCreateLists();
        }

        // ---------- layout ----------

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "chk", HeaderText = "", FillWeight = 3, SortMode = DataGridViewColumnSortMode.Automatic });
            Col("view", "Profile view", 16, typeof(string));
            Col("alignment", "Alignment", 15, typeof(string));
            Col("s0", "Sta start", 7, typeof(double), "F2");
            Col("s1", "Sta end", 7, typeof(double), "F2");
            Col("len", "Length", 6, typeof(double), "F1");
            Col("top", "TOP profile", 17, typeof(string));
            Col("status", "Status", 8, typeof(string));
            Col("diff", "Max diff", 6, typeof(double), "F3");
            Col("ducts", "Ducts", 5, typeof(int));
            Col("network", "Networks", 12, typeof(string));
            Col("notes", "Notes", 5, typeof(int));
        }

        private void Col(string name, string header, float weight, Type type, string format = null)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true, ValueType = type,
                SortMode = DataGridViewColumnSortMode.Automatic,
            };
            if (format != null) { c.DefaultCellStyle.Format = format; c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
            _grid.Columns.Add(c);
        }

        private Control BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var opts = Flow();
            opts.Controls.Add(L("TOP profile name contains"));
            opts.Controls.Add(_pattern);
            opts.Controls.Add(L("Duct offset tol."));
            opts.Controls.Add(_tol);
            opts.Controls.Add(L("Duct end at"));
            opts.Controls.Add(_end);
            _shownOnly.Margin = new Padding(8, 6, 3, 0);
            opts.Controls.Add(_shownOnly);
            opts.Controls.Add(_rescan);
            root.Controls.Add(opts, 0, 0);

            var filters = Flow();
            filters.Controls.Add(L("Search"));
            filters.Controls.Add(_search);
            filters.Controls.Add(L("TOP"));
            filters.Controls.Add(_hasTop);
            filters.Controls.Add(L("Status"));
            filters.Controls.Add(_status);
            filters.Controls.Add(L("Network"));
            filters.Controls.Add(_network);
            root.Controls.Add(filters, 0, 1);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_details);
            root.Controls.Add(split, 0, 2);
            split.HandleCreated += (_, _) => { try { split.SplitterDistance = (int)(split.Height * 0.68); } catch { } };

            var actions = Flow();
            actions.Controls.Add(_checkShown);
            actions.Controls.Add(_uncheck);
            actions.Controls.Add(_zoom);
            actions.Controls.Add(new Panel { Width = 20, Height = 1 });
            actions.Controls.Add(_fit);
            actions.Controls.Add(new Panel { Width = 20, Height = 1 });
            actions.Controls.Add(L("Name"));
            actions.Controls.Add(_cName);
            actions.Controls.Add(L("Style"));
            actions.Controls.Add(_cStyle);
            actions.Controls.Add(L("Labels"));
            actions.Controls.Add(_cLabels);
            actions.Controls.Add(L("Layer"));
            actions.Controls.Add(_cLayer);
            actions.Controls.Add(_create);
            root.Controls.Add(actions, 0, 3);

            root.Controls.Add(_summary, 0, 4);
            return root;
        }

        private static FlowLayoutPanel Flow() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        private static Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 3, 0) };

        // ---------- data ----------

        private TopOptions Options()
        {
            double tol = 0.30;
            double.TryParse(_tol.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tol);
            if (tol <= 0) tol = 0.30;
            return new TopOptions
            {
                Pattern = _pattern.Text.Trim(),
                OffsetTolerance = tol,
                OuterFace = _end.SelectedIndex == 0,
                ShownOnly = _shownOnly.Checked,
            };
        }

        private void Rescan()
        {
            Cursor = Cursors.WaitCursor;
            try { _scan = TopProfileCivil.Scan(_doc.Database, _civil, Options()); }
            finally { Cursor = Cursors.Default; }

            string keepNet = _network.SelectedItem as string;
            _network.Items.Clear();
            _network.Items.Add(Any);
            foreach (var n in _scan.Alignments.SelectMany(a => a.Ducts).Select(d => d.Network).Distinct().OrderBy(n => n))
                _network.Items.Add(n);
            _network.SelectedItem = keepNet != null && _network.Items.Contains(keepNet) ? keepNet : Any;

            FillGrid();
            var a = _scan.Alignments;
            _summary.Text = $"{_scan.Views.Count} profile views on {a.Count} alignments. TOP: "
                          + $"{a.Count(x => x.Status == AlignmentFit.StatusFits)} fit, "
                          + $"{a.Count(x => x.Status == AlignmentFit.StatusDiffers)} differ, "
                          + $"{a.Count(x => x.Status == AlignmentFit.StatusMissing)} missing, "
                          + $"{a.Count(x => x.Status == AlignmentFit.StatusNoDucts)} with no ducts. "
                          + "Fit and Create act per alignment (all its views).";
        }

        private void FillGrid()
        {
            if (_scan == null) return;
            string q = _search.Text.Trim();
            string net = _network.SelectedItem as string ?? Any;
            string status = _status.SelectedItem as string ?? Any;

            IEnumerable<ViewRow> rows = _scan.Views;
            if (q.Length > 0)
                rows = rows.Where(r => Has(r.View, q) || Has(r.Fit.Alignment, q) || r.Fit.Tops.Any(t => Has(t.Name, q)));
            if (_hasTop.SelectedIndex == 1) rows = rows.Where(r => r.Fit.Primary != null);
            if (_hasTop.SelectedIndex == 2) rows = rows.Where(r => r.Fit.Primary == null);
            if (status != Any) rows = rows.Where(r => r.Fit.Status == status);
            if (net != Any) rows = rows.Where(r => r.Fit.Ducts.Any(d => d.Network == net));

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var r in rows.OrderBy(r => r.Fit.Alignment).ThenBy(r => r.StaStart))
            {
                var f = r.Fit;
                object diff = f.MaxDiff.HasValue ? (object)f.MaxDiff.Value : null;
                int i = _grid.Rows.Add(false, r.View, f.Alignment, r.StaStart, r.StaEnd, r.StaEnd - r.StaStart,
                    f.Primary?.Name ?? (f.Tops.Count > 0 ? "(only outside alignment)" : ""),
                    f.Status, diff, r.DuctsInView, r.Networks, f.Notes.Count + f.Result.Warnings.Count);
                var gr = _grid.Rows[i];
                gr.Tag = r;
                if (f.Status == AlignmentFit.StatusDiffers) gr.DefaultCellStyle.BackColor = Color.LightYellow;
                else if (f.Status == AlignmentFit.StatusMissing) gr.DefaultCellStyle.BackColor = Color.MistyRose;
                else if (f.Status == AlignmentFit.StatusNoDucts) gr.DefaultCellStyle.ForeColor = Color.Gray;
            }
            _grid.ResumeLayout();
            UpdateButtons();
            ShowDetails();
        }

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private void SetChecks(bool value)
        {
            foreach (DataGridViewRow gr in _grid.Rows) gr.Cells["chk"].Value = value;
            UpdateButtons();
        }

        private List<AlignmentFit> CheckedAlignments() =>
            _grid.Rows.Cast<DataGridViewRow>()
                 .Where(gr => gr.Cells["chk"].Value is true)
                 .Select(gr => ((ViewRow)gr.Tag).Fit)
                 .Distinct()
                 .ToList();

        private void UpdateButtons()
        {
            var fits = CheckedAlignments();
            int canFit = fits.Count(f => f.Primary != null && f.Result.Points.Count >= 2);
            int canCreate = fits.Count(f => f.Primary == null && f.Result.Points.Count >= 2);
            _fit.Text = $"Fit TOP to ducts ({canFit})";
            _fit.Enabled = canFit > 0;
            _create.Text = $"Create missing TOP ({canCreate})";
            _create.Enabled = canCreate > 0;
        }

        private void ShowDetails()
        {
            if (_grid.CurrentRow?.Tag is not ViewRow r) { _details.Text = ""; return; }
            var f = r.Fit;
            var sb = new StringBuilder();
            sb.AppendLine($"Alignment {f.Alignment}   sta {f.Start:F3} to {f.End:F3}   status: {f.Status}" + (f.MaxDiff.HasValue ? $"   max diff {f.MaxDiff:F3}" : ""));
            sb.AppendLine($"Profile view {r.View}   sta {r.StaStart:F2} to {r.StaEnd:F2}");
            sb.AppendLine("TOP profiles: " + (f.Tops.Count == 0 ? "none" : string.Join(", ", f.Tops.Select(t => $"{t.Name} ({t.Start:F2}-{t.End:F2}{(t == f.Primary ? ", will be fitted" : "")})"))));
            foreach (var n in f.Notes) sb.AppendLine("NOTE  " + n);
            foreach (var w in f.Result.Warnings) sb.AppendLine("WARN  " + w);
            sb.AppendLine();
            sb.AppendLine($"Ducts used ({f.Ducts.Count}):");
            foreach (var d in f.Ducts)
                sb.AppendLine($"  {d.Name,-34} {d.Network,-22} sta {d.Segment.S0,9:F3} to {d.Segment.S1,9:F3}   top {d.Segment.Z0:F3} to {d.Segment.Z1:F3}");
            sb.AppendLine();
            sb.AppendLine($"Fitted TOP PVIs ({f.Result.Points.Count}):        station    elevation");
            foreach (var p in f.Result.Points) sb.AppendLine($"  {p.Station,30:F3}  {p.Elevation,10:F3}");
            _details.Text = sb.ToString();
        }

        // ---------- actions ----------

        private void ZoomSelected()
        {
            if (_grid.CurrentRow?.Tag is not ViewRow r) return;
            try { TopProfileCivil.ZoomTo(_doc, r.ViewId); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Zoom"); }
        }

        private void FitChecked()
        {
            var fits = CheckedAlignments().Where(f => f.Primary != null && f.Result.Points.Count >= 2).ToList();
            if (fits.Count == 0) return;
            int warned = fits.Count(f => f.Result.Warnings.Count > 0 || f.Notes.Count > 0);
            string msg = $"Replace the geometry of {fits.Count} TOP profile(s) with the duct fit?"
                       + Environment.NewLine + Environment.NewLine
                       + string.Join(Environment.NewLine, fits.Take(15).Select(f => $"  {f.Primary.Name}  ({f.Status}{(f.MaxDiff.HasValue ? $", {f.MaxDiff:F3}" : "")})"))
                       + (fits.Count > 15 ? $"{Environment.NewLine}  ... and {fits.Count - 15} more" : "")
                       + (warned > 0 ? $"{Environment.NewLine}{Environment.NewLine}{warned} of them have notes/warnings: check the details panel first." : "")
                       + $"{Environment.NewLine}{Environment.NewLine}Name, style, labels and profile views stay. UNDO after closing reverts the whole PVMGR session.";
            if (MessageBox.Show(this, msg, "Fit TOP profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var report = TopProfileCivil.Fit(_doc, fits);
            Rescan();
            MessageBox.Show(this, report.Summary("Fitted"), "Fit TOP profiles");
        }

        private void CreateChecked()
        {
            var fits = CheckedAlignments().Where(f => f.Primary == null && f.Result.Points.Count >= 2).ToList();
            if (fits.Count == 0) return;
            if (_cStyle.SelectedItem is not Item style || _cLabels.SelectedItem is not Item labels) { MessageBox.Show(this, "Pick a style and a label set."); return; }
            var layer = _cLayer.SelectedItem as Item;
            string msg = $"Create a TOP profile on {fits.Count} alignment(s)?"
                       + Environment.NewLine + string.Join(Environment.NewLine, fits.Take(15).Select(f => "  " + f.Alignment))
                       + (fits.Count > 15 ? $"{Environment.NewLine}  ... and {fits.Count - 15} more" : "");
            if (MessageBox.Show(this, msg, "Create TOP profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var report = TopProfileCivil.Create(_doc, fits, new TopProfileCivil.CreateTopOptions
            {
                NameTemplate = _cName.Text,
                StyleId = style.Id,
                LabelSetId = labels.Id,
                LayerId = layer?.Id ?? ObjectId.Null,
            });
            Rescan();
            MessageBox.Show(this, report.Summary("Created"), "Create TOP profiles");
        }

        private void FillCreateLists()
        {
            using var tr = _doc.Database.TransactionManager.StartTransaction();
            foreach (ObjectId id in _civil.Styles.ProfileStyles)
                _cStyle.Items.Add(new Item { Id = id, Name = ((StyleBase)tr.GetObject(id, OpenMode.ForRead)).Name });
            foreach (ObjectId id in _civil.Styles.LabelSetStyles.ProfileLabelSetStyles)
                _cLabels.Items.Add(new Item { Id = id, Name = ((StyleBase)tr.GetObject(id, OpenMode.ForRead)).Name });

            _cLayer.Items.Add(new Item { Id = ObjectId.Null, Name = "(current layer)" });
            var lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
            var layers = new List<Item>();
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!l.IsDependent) layers.Add(new Item { Id = id, Name = l.Name });
            }
            foreach (var l in layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)) _cLayer.Items.Add(l);
            tr.Commit();

            // Defaults from the TOP profiles already in the drawing.
            Select(_cStyle, _scan.TopStyles.FirstOrDefault());
            if (_cStyle.SelectedIndex < 0 && _cStyle.Items.Count > 0) _cStyle.SelectedIndex = 0;
            if (_cLabels.Items.Count > 0) _cLabels.SelectedIndex = 0;
            Select(_cLayer, _scan.TopLayers.FirstOrDefault());
            if (_cLayer.SelectedIndex < 0) _cLayer.SelectedIndex = 0;
        }

        private static void Select(ComboBox cb, string name)
        {
            if (name == null) return;
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is Item it && it.Name == name) { cb.SelectedIndex = i; return; }
        }
    }
}
