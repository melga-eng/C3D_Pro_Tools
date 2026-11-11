using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DProTools.ProfileTool
{
    /// <summary>Modal dialog: pick label styles (by category or from labels picked in the drawing), batch-set any of their values.</summary>
    public sealed class StyleEditorForm : Form
    {
        private const string AllCats = "(all categories)";
        private const string FromSelection = "(from selected labels)";

        private readonly Document _doc;
        private readonly CivilDocument _civil;
        private readonly double _lengthScale;
        private List<StyleCategory> _cats = new();
        private List<StyleEntry> _selectionStyles = new();
        private readonly HashSet<ObjectId> _checked = new();
        private Dictionary<string, List<PropSlot>> _rowsByKey = new();
        private bool _filling;

        private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _styleSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "filter styles" };
        private readonly CheckedListBox _styles = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly Button _pick = new() { Text = "Pick labels in drawing...", AutoSize = true };
        private readonly Button _checkAll = new() { Text = "Check all", AutoSize = true };
        private readonly Button _uncheckAll = new() { Text = "Uncheck all", AutoSize = true };

        private readonly TextBox _propSearch = new() { Width = 220, PlaceholderText = "filter values, e.g. height|arrow" };
        private readonly Button _preset = new() { Text = "Text heights + arrowhead", AutoSize = true };
        private readonly CheckBox _split = new() { Text = "Components by name", AutoSize = true };
        private readonly CheckBox _changedOnly = new() { Text = "Only rows I changed", AutoSize = true };
        private readonly DataGridView _grid = new();
        private readonly Button _apply = new() { Text = "Apply to checked styles", AutoSize = true };
        private readonly Button _clear = new() { Text = "Clear new values", AutoSize = true };
        private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };

        public StyleEditorForm(Document doc, CivilDocument civil)
        {
            _doc = doc;
            _civil = civil;
            _lengthScale = doc.Database.Insunits switch
            {
                UnitsValue.Meters => 1000.0,
                UnitsValue.Centimeters => 10.0,
                _ => 1.0,
            };

            Text = "Label Style Editor";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1250, 800);
            MinimumSize = new Size(900, 560);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            BuildGrid();
            Controls.Add(BuildLayout());

            _category.SelectedIndexChanged += (_, _) => FillStyles();
            _styleSearch.TextChanged += (_, _) => FillStyles();
            _styles.ItemCheck += OnItemCheck;
            _checkAll.Click += (_, _) => SetAllShown(true);
            _uncheckAll.Click += (_, _) => SetAllShown(false);
            _pick.Click += (_, _) => PickLabels();
            _propSearch.TextChanged += (_, _) => FillGrid(keepEdits: true);
            _preset.Click += (_, _) => _propSearch.Text = "Text component.*› Text Height|Dragged State Components Text Height|Leader Arrowhead Size";
            _split.CheckedChanged += (_, _) => FillGrid(keepEdits: false);
            _changedOnly.CheckedChanged += (_, _) => FillGrid(keepEdits: true);
            _apply.Click += (_, _) => Apply();
            _clear.Click += (_, _) => { foreach (DataGridViewRow r in _grid.Rows) r.Cells["new"].Value = null; };

            LoadCatalog();
        }

        // ---------- layout ----------

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "group", HeaderText = "Group", FillWeight = 26, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "prop", HeaderText = "Value", FillWeight = 18, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "unit", HeaderText = "Unit", FillWeight = 5, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "current", HeaderText = "Current (checked styles)", FillWeight = 33, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "new", HeaderText = "New value", FillWeight = 18 });
            _grid.CellValueChanged += (_, e) =>
            {
                if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "new") return;
                var cell = _grid.Rows[e.RowIndex].Cells["new"];
                bool set = cell.Value is string s && s.Length > 0;
                cell.Style.BackColor = set ? Color.LightYellow : Color.Empty;
                UpdateStatus();
            };
            _grid.DataError += (_, e) => e.ThrowException = false;
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
        }

        private Control BuildLayout()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(_pick, 0, 0);
            left.Controls.Add(_category, 0, 1);
            left.Controls.Add(_styleSearch, 0, 2);
            left.Controls.Add(_styles, 0, 3);
            var lb = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            lb.Controls.Add(_checkAll);
            lb.Controls.Add(_uncheckAll);
            left.Controls.Add(lb, 0, 4);
            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            top.Controls.Add(_propSearch);
            top.Controls.Add(_preset);
            _split.Margin = new Padding(12, 6, 3, 0);
            _changedOnly.Margin = new Padding(12, 6, 3, 0);
            top.Controls.Add(_split);
            top.Controls.Add(_changedOnly);
            right.Controls.Add(top, 0, 0);
            right.Controls.Add(_grid, 0, 1);
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            bottom.Controls.Add(_apply);
            bottom.Controls.Add(_clear);
            right.Controls.Add(bottom, 0, 2);
            right.Controls.Add(_status, 0, 3);
            split.Panel2.Controls.Add(right);

            split.HandleCreated += (_, _) => { try { split.SplitterDistance = 380; } catch { } };
            return split;
        }

        // ---------- styles ----------

        private void LoadCatalog()
        {
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                _cats = StyleCatalog.Load(tr, _civil);
                tr.Commit();
            }
            string keep = (_category.SelectedItem as StyleCategory)?.Path ?? _category.SelectedItem as string;
            _category.Items.Clear();
            _category.Items.Add(AllCats);
            if (_selectionStyles.Count > 0) _category.Items.Add(FromSelection);
            foreach (var c in _cats) _category.Items.Add(c);

            int idx = 0;
            for (int i = 0; i < _category.Items.Count; i++)
            {
                var it = _category.Items[i];
                if ((it as StyleCategory)?.Path == keep || it as string == keep) idx = i;
            }
            if (keep == null)
                for (int i = 0; i < _category.Items.Count; i++)
                    if (_category.Items[i] is StyleCategory c && c.Path.Contains("Pipe") && c.Path.Contains("Plan Profile")) idx = i;
            _category.SelectedIndex = idx;
        }

        private IEnumerable<StyleEntry> CurrentCategoryStyles()
        {
            var sel = _category.SelectedItem;
            if (sel is StyleCategory c) return c.Styles;
            if (sel as string == FromSelection) return _selectionStyles;
            return _cats.SelectMany(x => x.Styles);
        }

        private void FillStyles()
        {
            _filling = true;
            _styles.BeginUpdate();
            _styles.Items.Clear();
            string q = _styleSearch.Text.Trim();
            bool all = _category.SelectedItem as string == AllCats || _category.SelectedItem as string == FromSelection;
            foreach (var s in CurrentCategoryStyles())
            {
                if (q.Length > 0 && s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && s.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = all ? new ListItem(s, true) : new ListItem(s, false);
                _styles.Items.Add(item, _checked.Contains(s.Id));
            }
            _styles.EndUpdate();
            _filling = false;
            UpdateStatus();
        }

        private sealed class ListItem
        {
            public readonly StyleEntry Entry;
            private readonly bool _showCategory;
            public ListItem(StyleEntry e, bool showCategory) { Entry = e; _showCategory = showCategory; }
            public override string ToString() => _showCategory ? $"{Entry}    — {Entry.Category}" : Entry.ToString();
        }

        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_filling) return;
            var item = (ListItem)_styles.Items[e.Index];
            if (e.NewValue == CheckState.Checked) _checked.Add(item.Entry.Id); else _checked.Remove(item.Entry.Id);
            BeginInvoke(new Action(() => FillGrid(keepEdits: true)));
        }

        private void SetAllShown(bool value)
        {
            _filling = true;
            for (int i = 0; i < _styles.Items.Count; i++)
            {
                _styles.SetItemChecked(i, value);
                var id = ((ListItem)_styles.Items[i]).Entry.Id;
                if (value) _checked.Add(id); else _checked.Remove(id);
            }
            _filling = false;
            FillGrid(keepEdits: true);
        }

        private void PickLabels()
        {
            var ed = _doc.Editor;
            var found = new Dictionary<ObjectId, int>();
            using (ed.StartUserInteraction(this))
            {
                var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect labels (pipe, structure, profile, alignment... any Civil 3D label): " });
                if (res.Status != PromptStatus.OK) return;
                using var tr = _doc.Database.TransactionManager.StartTransaction();
                foreach (ObjectId id in res.Value.GetObjectIds())
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    ObjectId sid = ObjectId.Null;
                    try { if (obj.GetType().GetProperty("StyleId")?.GetValue(obj) is ObjectId s) sid = s; } catch { }
                    if (sid.IsNull) continue;
                    if (tr.GetObject(sid, OpenMode.ForRead) is not LabelStyle) continue;
                    found[sid] = found.TryGetValue(sid, out int n) ? n + 1 : 1;
                }
                tr.Commit();
            }
            if (found.Count == 0) { MessageBox.Show(this, "No Civil 3D labels with a label style in the selection.", "Pick labels"); return; }

            var all = _cats.SelectMany(c => c.Styles).ToDictionary(s => s.Id);
            _selectionStyles = found.Keys.Where(all.ContainsKey).Select(id => all[id]).OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
            _checked.Clear();
            foreach (var s in _selectionStyles) _checked.Add(s.Id);
            LoadCatalog();
            _category.SelectedItem = FromSelection;
            FillGrid(keepEdits: true);
            _status.Text = $"{found.Values.Sum()} labels use {found.Count} style(s); they are checked.";
        }

        // ---------- values grid ----------

        private void FillGrid(bool keepEdits)
        {
            var edits = new Dictionary<string, object>();
            if (keepEdits)
                foreach (DataGridViewRow r in _grid.Rows)
                    if (r.Cells["new"].Value is string s && s.Length > 0) edits[(string)r.Tag] = s;

            _rowsByKey = new Dictionary<string, List<PropSlot>>();
            var order = new List<string>();
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in _checked)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not LabelStyle st) continue;
                    foreach (var slot in StyleProps.Read(tr, st, OpenMode.ForRead, _split.Checked))
                    {
                        if (!_rowsByKey.TryGetValue(slot.Key, out var list)) { _rowsByKey[slot.Key] = list = new List<PropSlot>(); order.Add(slot.Key); }
                        list.Add(slot);
                    }
                }
                tr.Commit();
            }

            Regex filter = null;
            string q = _propSearch.Text.Trim();
            if (q.Length > 0)
            {
                try { filter = new Regex(q, RegexOptions.IgnoreCase); }
                catch { filter = new Regex(Regex.Escape(q), RegexOptions.IgnoreCase); }
            }

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var key in order.OrderBy(k => GroupRank(_rowsByKey[k][0].Group)).ThenBy(k => _rowsByKey[k][0].Group).ThenBy(k => _rowsByKey[k][0].Name))
            {
                var slots = _rowsByKey[key];
                var first = slots[0];
                if (filter != null && !filter.IsMatch(first.Group + " " + first.Name) && !filter.IsMatch(first.Group + "|" + first.Name) && !filter.IsMatch(first.Name)) continue;
                if (_changedOnly.Checked && !edits.ContainsKey(key)) continue;

                int i = _grid.Rows.Add(first.Group, first.Name, StyleProps.UnitOf(first, _lengthScale), Summary(slots), null);
                var row = _grid.Rows[i];
                row.Tag = key;
                if (first.Kind == ValueKind.Bool || first.Kind == ValueKind.Enum)
                {
                    var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                    combo.Items.Add("");
                    if (first.Kind == ValueKind.Bool) { combo.Items.Add("True"); combo.Items.Add("False"); }
                    else foreach (var n in Enum.GetNames(first.ValueType)) combo.Items.Add(n);
                    row.Cells["new"] = combo;
                }
                if (edits.TryGetValue(key, out var e)) row.Cells["new"].Value = e;
            }
            _grid.ResumeLayout();
            UpdateStatus();
        }

        private static int GroupRank(string g) =>
            g.StartsWith("Label") ? 0 : g.StartsWith("Leader") ? 1 : g.StartsWith("Dragged") ? 2 : g.Contains("component") ? 4 : 3;

        private string Summary(List<PropSlot> slots)
        {
            var groups = slots.GroupBy(s => StyleProps.Format(s, _lengthScale)).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 1) return $"{groups[0].Key}" + (slots.Count > 1 ? $"  (all {slots.Count})" : "");
            return "MIXED: " + string.Join(", ", groups.Take(4).Select(g => $"{g.Key} ×{g.Count()}")) + (groups.Count > 4 ? ", ..." : "");
        }

        private void UpdateStatus()
        {
            int edits = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells["new"].Value is string s && s.Length > 0);
            _apply.Text = $"Apply {edits} value(s) to {_checked.Count} checked style(s)";
            _apply.Enabled = edits > 0 && _checked.Count > 0;
            if (!_status.Text.Contains("labels use"))
                _status.Text = $"{_checked.Count} style(s) checked. Lengths in {(_lengthScale == 1000 ? "mm" : "drawing units")} (plotted size), angles in degrees. Changing a style changes every label that uses it.";
        }

        // ---------- apply ----------

        private void Apply()
        {
            _grid.EndEdit();
            var changes = new Dictionary<string, object>();
            var describe = new List<string>();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.Cells["new"].Value is not string text || text.Length == 0) continue;
                string key = (string)r.Tag;
                var sample = _rowsByKey[key][0];
                if (!StyleProps.TryParse(sample, text, _lengthScale, out object value, out string error))
                {
                    MessageBox.Show(this, $"{sample.Group} › {sample.Name}: {error}", "Invalid value");
                    _grid.CurrentCell = r.Cells["new"];
                    return;
                }
                changes[key] = value;
                describe.Add($"  {sample.Group} › {sample.Name} = {text} {StyleProps.UnitOf(sample, _lengthScale)}  ({_rowsByKey[key].Count} place(s))");
            }
            if (changes.Count == 0) return;

            string msg = $"Set {changes.Count} value(s) on {_checked.Count} label style(s)?" + Environment.NewLine + Environment.NewLine
                       + string.Join(Environment.NewLine, describe.Take(20)) + (describe.Count > 20 ? $"{Environment.NewLine}  ..." : "")
                       + Environment.NewLine + Environment.NewLine + "Every label that uses these styles changes. UNDO after closing reverts the whole STYLEMGR session.";
            if (MessageBox.Show(this, msg, "Apply style values", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            int set = 0, failed = 0;
            var notes = new List<string>();
            using (_doc.LockDocument())
            using (var tr = _doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in _checked)
                {
                    LabelStyle st;
                    try { st = (LabelStyle)tr.GetObject(id, OpenMode.ForWrite); }
                    catch (Exception ex) { failed++; notes.Add($"{id.Handle}: {ex.Message}"); continue; }
                    foreach (var slot in StyleProps.Read(tr, st, OpenMode.ForWrite, _split.Checked))
                    {
                        if (!changes.TryGetValue(slot.Key, out object value)) continue;
                        try { slot.ValueProp.SetValue(slot.Holder, value); set++; }
                        catch (Exception ex)
                        {
                            failed++;
                            notes.Add($"{st.Name} › {slot.Group} › {slot.Name}: {(ex.InnerException ?? ex).Message}");
                        }
                    }
                }
                tr.Commit();
            }
            _doc.Editor.Regen();

            foreach (DataGridViewRow r in _grid.Rows) r.Cells["new"].Value = null;
            FillGrid(keepEdits: false);
            MessageBox.Show(this, $"Set {set} value(s)" + (failed > 0 ? $", {failed} failed:{Environment.NewLine}{string.Join(Environment.NewLine, notes.Take(20))}" : "."), "Apply style values");
        }
    }
}
