using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DProTools.ProfileTool
{
    /// <summary>Modal dialog: profile list by surface, delete checked, create surface profiles on all alignments.</summary>
    public sealed class ProfileManagerForm : Form
    {
        private const string AllSurfaces = "(all)";
        private const string NoSurface = "(no surface / not EG)";

        private readonly Document _doc;
        private readonly CivilDocument _civil;
        private ScanResult _scan;

        private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        private readonly CheckBox _egOnly = new() { Text = "Surface (EG) profiles only", AutoSize = true, Checked = true };
        private readonly DataGridView _grid = new();
        private readonly Button _checkShown = new() { Text = "Check shown", AutoSize = true };
        private readonly Button _uncheckAll = new() { Text = "Uncheck all", AutoSize = true };
        private readonly Button _checkCleanup = new() { Text = "Check empty + duplicates", AutoSize = true };
        private readonly Button _delete = new() { Text = "Delete checked", AutoSize = true };
        private readonly Button _rescan = new() { Text = "Rescan", AutoSize = true };

        private readonly ComboBox _cSurface = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _cStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _cLabels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _cLayer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly TextBox _cName = new() { Text = "{alignment}_{surface}", Width = 260 };
        private readonly CheckBox _cSkip = new() { Text = "Skip alignments that already have a profile from this surface", AutoSize = true, Checked = true };
        private readonly Button _create = new() { Text = "Create on all alignments", AutoSize = true };

        private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };

        private sealed class Item
        {
            public ObjectId Id;
            public string Name;
            public override string ToString() => Name;
        }

        public ProfileManagerForm(Document doc, CivilDocument civil)
        {
            _doc = doc;
            _civil = civil;

            Text = "Profile Manager";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, 760);
            MinimumSize = new Size(800, 560);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            BuildGrid();
            Controls.Add(BuildLayout());

            _filter.SelectedIndexChanged += (_, _) => FillGrid();
            _egOnly.CheckedChanged += (_, _) => FillGrid();
            _checkShown.Click += (_, _) => SetChecks(true);
            _uncheckAll.Click += (_, _) => SetChecks(false);
            _checkCleanup.Click += (_, _) => CheckCleanup();
            _delete.Click += (_, _) => DeleteChecked();
            _rescan.Click += (_, _) => Rescan();
            _create.Click += (_, _) => CreateProfiles();
            _grid.CellValueChanged += (_, _) => UpdateDeleteText();
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            FillCreateLists();
            Rescan();
        }

        // ---------- layout ----------

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "chk", HeaderText = "", FillWeight = 4, SortMode = DataGridViewColumnSortMode.Automatic });
            AddText("surface", "Surface", 24);
            AddText("alignment", "Alignment", 22);
            AddText("profile", "Profile", 34);
            AddText("type", "Type", 7);
            AddText("match", "Matched by", 10);
            AddText("note", "Note", 8);
        }

        private void AddText(string name, string header, float weight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Automatic,
            });
        }

        private Control BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            top.Controls.Add(new Label { Text = "Surface:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            top.Controls.Add(_filter);
            top.Controls.Add(Spacer());
            top.Controls.Add(_egOnly);
            top.Controls.Add(Spacer());
            top.Controls.Add(_checkShown);
            top.Controls.Add(_uncheckAll);
            top.Controls.Add(_checkCleanup);
            top.Controls.Add(_delete);
            top.Controls.Add(Spacer());
            top.Controls.Add(_rescan);
            _egOnly.Margin = new Padding(3, 7, 3, 0);
            root.Controls.Add(top, 0, 0);

            root.Controls.Add(_grid, 0, 1);

            var create = new GroupBox { Text = "Create surface profiles on all alignments", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
            AddField(t, 0, 0, "Surface", _cSurface);
            AddField(t, 0, 2, "Profile style", _cStyle);
            AddField(t, 1, 0, "Label set", _cLabels);
            AddField(t, 1, 2, "Layer", _cLayer);
            AddField(t, 2, 0, "Name", _cName);
            t.Controls.Add(new Label { Text = "Tokens: {alignment} {surface}", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 7, 3, 0) }, 2, 2);
            t.SetColumnSpan(t.GetControlFromPosition(2, 2), 2);
            t.Controls.Add(_cSkip, 0, 3);
            t.SetColumnSpan(_cSkip, 3);
            t.Controls.Add(_create, 3, 3);
            create.Controls.Add(t);
            root.Controls.Add(create, 0, 2);

            root.Controls.Add(_status, 0, 3);
            return root;
        }

        private static void AddField(TableLayoutPanel t, int row, int col, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, col, row);
            t.Controls.Add(c, col + 1, row);
        }

        private static Control Spacer() => new Panel { Width = 16, Height = 1 };

        // ---------- data ----------

        private void Rescan()
        {
            Cursor = Cursors.WaitCursor;
            try { _scan = ProfileScanner.Scan(_doc.Database, _civil); }
            finally { Cursor = Cursors.Default; }

            string keep = _filter.SelectedItem as string;
            _filter.Items.Clear();
            _filter.Items.Add($"{AllSurfaces}  [{_scan.Rows.Count}]");
            foreach (var s in _scan.Surfaces)
                _filter.Items.Add($"{s.Name}  [{_scan.Rows.Count(r => r.IsFrom(s.Name, false))}]");
            _filter.Items.Add($"{NoSurface}  [{_scan.Rows.Count(r => r.Surface == "")}]");
            int ambiguous = _scan.Rows.Count(r => r.IsAmbiguous);

            int idx = 0;
            if (keep != null)
                for (int i = 0; i < _filter.Items.Count; i++)
                    if (Strip((string)_filter.Items[i]) == Strip(keep)) idx = i;
            _filter.SelectedIndex = idx;   // fills the grid

            string sel = (_cSurface.SelectedItem as Item)?.Name;
            _cSurface.Items.Clear();
            foreach (var s in _scan.Surfaces) _cSurface.Items.Add(new Item { Id = s.Id, Name = s.Name });
            SelectByName(_cSurface, sel);

            _status.Text = $"{_scan.AlignmentIds.Count} alignments, {_scan.Rows.Count} profiles, "
                         + $"{_scan.Rows.Count(r => r.IsSurfaceProfile)} surface profiles"
                         + $", {_scan.Rows.Count(r => r.IsEmpty)} empty, {_scan.Rows.Count(r => r.IsDuplicate)} duplicate"
                         + (ambiguous > 0 ? $", {ambiguous} ambiguous (yellow: elevations match more than one surface, never auto-checked)" : "")
                         + ".";
        }

        private static string Strip(string filterItem)
        {
            int i = filterItem.LastIndexOf("  [", StringComparison.Ordinal);
            return i < 0 ? filterItem : filterItem.Substring(0, i);
        }

        private void FillGrid()
        {
            if (_scan == null) return;
            string f = Strip(_filter.SelectedItem as string ?? AllSurfaces);

            IEnumerable<ProfileRow> rows = _scan.Rows;
            if (_egOnly.Checked) rows = rows.Where(r => r.IsSurfaceProfile);
            if (f == NoSurface) rows = rows.Where(r => r.Surface == "");
            else if (f != AllSurfaces) rows = rows.Where(r => r.IsFrom(f, true));

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var r in rows.OrderBy(r => r.Surface).ThenBy(r => r.Alignment).ThenBy(r => r.Profile))
            {
                int i = _grid.Rows.Add(false, r.Surface, r.Alignment, r.Profile, r.Type.ToString(), r.Match, r.Note);
                var gr = _grid.Rows[i];
                gr.Tag = r;
                if (r.IsAmbiguous) gr.DefaultCellStyle.BackColor = Color.LightYellow;
                else if (r.IsEmpty || r.IsDuplicate) gr.DefaultCellStyle.BackColor = Color.MistyRose;
                else if (r.Match == "name only" || r.Match == "none") gr.DefaultCellStyle.ForeColor = Color.Gray;
            }
            _grid.ResumeLayout();
            UpdateDeleteText();
        }

        private void CheckCleanup()
        {
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                var r = (ProfileRow)gr.Tag;
                gr.Cells["chk"].Value = r.IsEmpty || r.IsDuplicate;
            }
            UpdateDeleteText();
        }

        private void SetChecks(bool value)
        {
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                var r = (ProfileRow)gr.Tag;
                // "Check shown" never ticks ambiguous rows; tick those by hand if they really are from this surface.
                gr.Cells["chk"].Value = value && !r.IsAmbiguous;
            }
            UpdateDeleteText();
        }

        private List<ProfileRow> CheckedRows() =>
            _grid.Rows.Cast<DataGridViewRow>()
                 .Where(gr => gr.Cells["chk"].Value is true)
                 .Select(gr => (ProfileRow)gr.Tag)
                 .ToList();

        private void UpdateDeleteText()
        {
            int n = _grid.Rows.Cast<DataGridViewRow>().Count(gr => gr.Cells["chk"].Value is true);
            _delete.Text = $"Delete checked ({n})";
            _delete.Enabled = n > 0;
        }

        // ---------- actions ----------

        private void DeleteChecked()
        {
            var rows = CheckedRows();
            if (rows.Count == 0) return;

            var bySurface = rows.GroupBy(r => r.Surface == "" ? "(no surface)" : r.Surface)
                                .Select(g => $"  {g.Key}: {g.Count()}");
            var nonEg = rows.Count(r => !r.IsSurfaceProfile);
            string msg = $"Delete {rows.Count} profile(s)?{Environment.NewLine}{Environment.NewLine}"
                       + string.Join(Environment.NewLine, bySurface)
                       + (nonEg > 0 ? $"{Environment.NewLine}{Environment.NewLine}WARNING: {nonEg} of them are NOT surface profiles (design/layout etc.)." : "")
                       + $"{Environment.NewLine}{Environment.NewLine}Profile views that show them lose those lines. UNDO after closing reverts the whole PROFILEMGR session.";
            if (MessageBox.Show(this, msg, "Delete profiles", MessageBoxButtons.YesNo,
                    nonEg > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;

            var report = ProfileOperations.Delete(_doc, rows);
            Rescan();
            MessageBox.Show(this, report.Summary("Deleted"), "Delete profiles");
        }

        private void CreateProfiles()
        {
            if (_cSurface.SelectedItem is not Item surface) { MessageBox.Show(this, "Pick a surface."); return; }
            if (_cStyle.SelectedItem is not Item style) { MessageBox.Show(this, "Pick a profile style."); return; }
            if (_cLabels.SelectedItem is not Item labels) { MessageBox.Show(this, "Pick a label set."); return; }
            var layer = _cLayer.SelectedItem as Item;

            string msg = $"Create a profile from '{surface.Name}' on up to {_scan.AlignmentIds.Count} alignment(s)?"
                       + (_cSkip.Checked ? $"{Environment.NewLine}Alignments that already have one from this surface are skipped." : "");
            if (MessageBox.Show(this, msg, "Create surface profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var o = new CreateOptions
            {
                SurfaceId = surface.Id,
                SurfaceName = surface.Name,
                StyleId = style.Id,
                LabelSetId = labels.Id,
                LayerId = layer?.Id ?? ObjectId.Null,
                NameTemplate = _cName.Text,
                SkipExisting = _cSkip.Checked,
            };
            Cursor = Cursors.WaitCursor;
            OperationReport report;
            try { report = ProfileOperations.CreateOnAll(_doc, _scan, o); }
            finally { Cursor = Cursors.Default; }
            Rescan();
            MessageBox.Show(this, report.Summary("Created"), "Create surface profiles");
        }

        private void FillCreateLists()
        {
            using var tr = _doc.Database.TransactionManager.StartTransaction();

            foreach (ObjectId id in _civil.Styles.ProfileStyles)
                _cStyle.Items.Add(new Item { Id = id, Name = ((StyleBase)tr.GetObject(id, OpenMode.ForRead)).Name });
            foreach (ObjectId id in _civil.Styles.LabelSetStyles.ProfileLabelSetStyles)
                _cLabels.Items.Add(new Item { Id = id, Name = ((StyleBase)tr.GetObject(id, OpenMode.ForRead)).Name });

            var lt = (LayerTable)tr.GetObject(_doc.Database.LayerTableId, OpenMode.ForRead);
            var layers = new List<Item>();
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!l.IsDependent) layers.Add(new Item { Id = id, Name = l.Name });
            }
            _cLayer.Items.Add(new Item { Id = ObjectId.Null, Name = "(current layer)" });
            foreach (var l in layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)) _cLayer.Items.Add(l);
            _cLayer.SelectedIndex = 0;

            // Default style: the one existing surface profiles use most, else anything with "Existing" in it.
            string usual = MostUsedEgStyle(tr);
            SelectByName(_cStyle, usual);
            if (_cStyle.SelectedIndex < 0) SelectContaining(_cStyle, "Existing");
            if (_cStyle.SelectedIndex < 0 && _cStyle.Items.Count > 0) _cStyle.SelectedIndex = 0;
            SelectContaining(_cLabels, "No Label");
            if (_cLabels.SelectedIndex < 0 && _cLabels.Items.Count > 0) _cLabels.SelectedIndex = 0;

            tr.Commit();
        }

        private string MostUsedEgStyle(Transaction tr)
        {
            var counts = new Dictionary<string, int>();
            foreach (ObjectId aid in _civil.GetAlignmentIds())
            {
                if (tr.GetObject(aid, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.Alignment al) continue;
                foreach (ObjectId pid in al.GetProfileIds())
                    if (tr.GetObject(pid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Profile p
                        && p.ProfileType == Autodesk.Civil.DatabaseServices.ProfileType.EG)
                        counts[p.StyleName] = counts.TryGetValue(p.StyleName, out int c) ? c + 1 : 1;
            }
            return counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        }

        private static void SelectByName(ComboBox cb, string name)
        {
            if (name == null) return;
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is Item it && it.Name == name) { cb.SelectedIndex = i; return; }
        }

        private static void SelectContaining(ComboBox cb, string part)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is Item it && it.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { cb.SelectedIndex = i; return; }
        }
    }
}
