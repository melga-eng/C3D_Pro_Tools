using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C3DProTools.ProfileTool
{
    /// <summary>Modal dialog: plan cover and clearance DIMs for the profile views, review them, draw them or remove them.</summary>
    public sealed class ProfileDimsForm : Form
    {
        private readonly Document _doc;
        private readonly ProfileDimSettings _settings = ProfileDimSettings.Load();
        private List<ViewDims> _views = new List<ViewDims>();
        private List<OperationIssue> _sceneIssues = new List<OperationIssue>();

        private readonly ComboBox _style = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 230 };
        private readonly ComboBox _layer = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 230 };
        private readonly CheckBox _cover = new() { Text = "Cover DIMs (duct top to proposed surface)", AutoSize = true };
        private readonly CheckBox _faces = new() { Text = "At structure faces", AutoSize = true };
        private readonly CheckBox _clearance = new() { Text = "Clearance DIMs (duct to crossing pipes)", AutoSize = true };
        private readonly CheckBox _shownOnly = new() { Text = "Only crossings drawn in the view", AutoSize = true };
        private readonly TextBox _grade = Num(), _merge = Num(), _spacing = Num(), _minClear = Num(), _offset = Num(), _skipNear = Num();

        private readonly TextBox _search = new() { Width = 200, PlaceholderText = "view / alignment" };
        private readonly CheckBox _withDimsOnly = new() { Text = "Only views with DIMs to draw", AutoSize = true };
        private readonly DataGridView _grid = new();
        private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
        private readonly Label _summary = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };

        private readonly Button _plan = new() { Text = "Plan", AutoSize = true };
        private readonly Button _checkAll = new() { Text = "Check all", AutoSize = true };
        private readonly Button _uncheck = new() { Text = "Uncheck all", AutoSize = true };
        private readonly Button _zoom = new() { Text = "Zoom to view", AutoSize = true };
        private readonly Button _create = new() { Text = "Draw DIMs in checked views", AutoSize = true };
        private readonly Button _remove = new() { Text = "Remove tool DIMs", AutoSize = true };

        private static TextBox Num() => new() { Width = 55 };

        public ProfileDimsForm(Document doc)
        {
            _doc = doc;
            Text = "Profile DIMs";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 820);
            MinimumSize = new Size(900, 600);
            Font = new Font("Segoe UI", 9f);

            try
            {
                _style.Items.AddRange(ProfileDimsCivil3D.DimStyles(doc.Database).Cast<object>().ToArray());
                _layer.Items.AddRange(ProfileDimsCivil3D.Layers(doc.Database).Cast<object>().ToArray());
            }
            catch (Exception ex) { C3DProLogger.Warn("Profile DIMs: styles or layers could not be listed.", ex); }
            LoadSettings();
            BuildGrid();
            Controls.Add(BuildLayout());

            _plan.Click += (_, _) => Replan();
            _search.TextChanged += (_, _) => FillGrid();
            _withDimsOnly.CheckedChanged += (_, _) => FillGrid();
            _checkAll.Click += (_, _) => SetChecks(true);
            _uncheck.Click += (_, _) => SetChecks(false);
            _zoom.Click += (_, _) => Zoom();
            _grid.CellDoubleClick += (_, _) => Zoom();
            _grid.SelectionChanged += (_, _) => ShowDetails();
            _create.Click += (_, _) => Create();
            _remove.Click += (_, _) => Remove();
            Shown += (_, _) => Replan();
            FormClosing += (_, _) => { if (ReadSettings(out _)) _settings.Save(); };
        }

        // ---- layout ------------------------------------------------------------------------------------------------

        private Control BuildLayout()
        {
            TableLayoutPanel root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel opts = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            opts.Controls.Add(Group("DIM", Row("Style", _style), Row("Layer", _layer),
                Row("DIM line offset", _offset, "m"), Row("Skip near a hand DIM", _skipNear, "m")));
            opts.Controls.Add(Group("Cover", _cover, _faces, Row("Major grade change", _grade, "%"),
                Row("Merge breaks within", _merge, "m"), Row("Min spacing", _spacing, "m")));
            opts.Controls.Add(Group("Clearance", _clearance, _shownOnly, Row("Warn below", _minClear, "m")));
            root.Controls.Add(opts);

            FlowLayoutPanel bar = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            bar.Controls.AddRange(new Control[] { _plan, new Label { Text = "Filter", AutoSize = true, Margin = new Padding(12, 8, 0, 0) }, _search, _withDimsOnly, _checkAll, _uncheck, _zoom, _summary });
            root.Controls.Add(bar);

            SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_details);
            root.Controls.Add(split);

            FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            CancelButton = close;
            actions.Controls.AddRange(new Control[] { close, _create, _remove,
                new Label { Text = "UNDO after closing reverts each draw or remove.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 8, 12, 0) } });
            root.Controls.Add(actions);
            return root;
        }

        private static GroupBox Group(string title, params Control[] rows)
        {
            FlowLayoutPanel f = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            f.Controls.AddRange(rows);
            GroupBox g = new() { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6), Margin = new Padding(0, 0, 8, 4) };
            g.Controls.Add(f);
            return g;
        }

        private static Control Row(string label, Control field, string unit = null)
        {
            FlowLayoutPanel f = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            f.Controls.Add(new Label { Text = label, AutoSize = false, Width = 130, Height = 23, TextAlign = ContentAlignment.MiddleLeft });
            f.Controls.Add(field);
            if (unit != null) f.Controls.Add(new Label { Text = unit, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 0, 0) });
            return f;
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "", FillWeight = 12 });
            foreach ((string name, string header, float w) in new[] {
                ("View", "Profile view", 120f), ("Alignment", "Alignment", 90f), ("Top", "TOP profile", 100f), ("Surface", "Proposed surface", 100f),
                ("Cover", "Cover", 30f), ("Clearance", "Clearance", 34f), ("Hand", "Hand DIMs", 34f), ("Tool", "Tool DIMs", 34f), ("Issues", "Issues", 120f) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, ReadOnly = true });
            _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        }

        // ---- settings ----------------------------------------------------------------------------------------------

        private void LoadSettings()
        {
            _style.Text = _settings.DimStyle;
            _layer.Text = _settings.Layer;
            _cover.Checked = _settings.Cover;
            _faces.Checked = _settings.AtStructureFaces;
            _clearance.Checked = _settings.Clearance;
            _shownOnly.Checked = _settings.CrossingsShownOnly;
            _grade.Text = F(_settings.MajorGradeChangePct);
            _merge.Text = F(_settings.MergeWithinM);
            _spacing.Text = F(_settings.MinSpacingM);
            _minClear.Text = F(_settings.MinClearanceM);
            _offset.Text = F(_settings.OffsetM);
            _skipNear.Text = F(_settings.SkipNearHandDimM);
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private bool ReadSettings(out string problem)
        {
            problem = null;
            double[] v = new double[6];
            TextBox[] boxes = { _grade, _merge, _spacing, _minClear, _offset, _skipNear };
            string[] names = { "Major grade change", "Merge breaks within", "Min spacing", "Warn below", "DIM line offset", "Skip near a hand DIM" };
            for (int i = 0; i < boxes.Length; i++)
                if (!double.TryParse(boxes[i].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || !double.IsFinite(v[i]))
                {
                    problem = $"{names[i]} must be a number of 0 or more.";
                    return false;
                }
            if (v[0] <= 0) { problem = "Major grade change must be more than 0."; return false; }
            _settings.DimStyle = _style.Text.Trim();
            _settings.Layer = _layer.Text.Trim();
            _settings.Cover = _cover.Checked;
            _settings.AtStructureFaces = _faces.Checked;
            _settings.Clearance = _clearance.Checked;
            _settings.CrossingsShownOnly = _shownOnly.Checked;
            (_settings.MajorGradeChangePct, _settings.MergeWithinM, _settings.MinSpacingM, _settings.MinClearanceM, _settings.OffsetM, _settings.SkipNearHandDimM)
                = (v[0], v[1], v[2], v[3], v[4], v[5]);
            return true;
        }

        // ---- plan and list -----------------------------------------------------------------------------------------

        private void Replan()
        {
            if (!ReadSettings(out string problem)) { MessageBox.Show(this, problem, Text); return; }
            HashSet<string> checkedViews = new HashSet<string>(Checked().Select(v => v.Plan.View.Name));
            bool first = _views.Count == 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                _views = ProfileDimsCivil3D.Plan(_doc, _settings, out _sceneIssues);
                foreach (ViewDims v in _views) _checks[v] = !first && checkedViews.Contains(v.Plan.View.Name);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Profile DIMs: planning failed.", ex);
                MessageBox.Show(this, "The drawing could not be read: " + ex.Message, Text);
            }
            finally { Cursor = Cursors.Default; }
            FillGrid();
        }

        private readonly Dictionary<ViewDims, bool> _checks = new Dictionary<ViewDims, bool>();

        private void FillGrid()
        {
            string q = _search.Text.Trim();
            _grid.Rows.Clear();
            foreach (ViewDims v in _views)
            {
                ProfileViewSnapshot view = v.Plan.View;
                if (q.Length > 0 && view.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && v.AlignmentName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_withDimsOnly.Checked && v.Plan.Dims.Count == 0) continue;
                int cover = v.Plan.Dims.Count(d => d.Dim.Kind == DimKind.Cover), clear = v.Plan.Dims.Count(d => d.Dim.Kind == DimKind.Clearance);
                int errors = v.Plan.Issues.Count(i => i.Severity == IssueSeverity.Error), warnings = v.Plan.Issues.Count(i => i.Severity == IssueSeverity.Warning);
                string issues = errors + warnings == 0 ? "" : string.Join(", ", new[] { errors > 0 ? $"{errors} error(s)" : null, warnings > 0 ? $"{warnings} warning(s)" : null }.Where(s => s != null))
                    + ": " + v.Plan.Issues.First(i => i.Severity != IssueSeverity.Info).Message;
                int row = _grid.Rows.Add(_checks.TryGetValue(v, out bool on) && on, view.Name, v.AlignmentName, v.Plan.TopProfile, v.Plan.SurfaceProfile,
                    cover, clear, v.Hand, v.Tool, issues);
                _grid.Rows[row].Tag = v;
                if (errors > 0) _grid.Rows[row].Cells["Issues"].Style.ForeColor = Color.Firebrick;
                else if (warnings > 0) _grid.Rows[row].Cells["Issues"].Style.ForeColor = Color.DarkOrange;
            }
            int planned = _views.Sum(v => v.Plan.Dims.Count);
            _summary.Text = $"{_views.Count} views, {planned} DIMs planned, {_views.Sum(v => v.Tool)} tool DIMs and {_views.Sum(v => v.Hand)} hand DIMs in the drawing";
            ShowDetails();
        }

        private void SetChecks(bool on)
        {
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is ViewDims v) { r.Cells["Use"].Value = on; _checks[v] = on; }
        }

        private List<ViewDims> Checked()
        {
            List<ViewDims> list = new List<ViewDims>();
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is ViewDims v)
                {
                    bool on = r.Cells["Use"].Value is bool b && b;
                    _checks[v] = on;
                    if (on) list.Add(v);
                }
            return list;
        }

        private void ShowDetails()
        {
            StringBuilder sb = new StringBuilder();
            if (_grid.CurrentRow?.Tag is ViewDims v)
            {
                sb.AppendLine($"{v.Plan.View.Name}   stations {v.Plan.View.StartStationM:F2} to {v.Plan.View.EndStationM:F2}   exaggeration {v.Plan.View.VerticalExaggeration:0.##}");
                sb.AppendLine($"TOP profile: {Or(v.Plan.TopProfile, "(none: duct pipe tops)")}   proposed surface: {Or(v.Plan.SurfaceProfile, "(none)")}");
                sb.AppendLine($"Already in the view: {v.Tool} tool DIM(s) (replaced on draw), {v.Hand} hand-drawn DIM(s) (kept"
                    + (_settings.SkipNearHandDimM > 0 ? $"; no tool DIM within {F(_settings.SkipNearHandDimM)} m of one)" : ")"));
                sb.AppendLine();
                sb.AppendLine($"{"Kind",-10}{"Station",10}{"Value",9}{"Lower",10}{"Upper",10}  {"Side",-6}{"Why",-15}Source");
                foreach (PlacedDim p in v.Plan.Dims)
                {
                    PlannedDim d = p.Dim;
                    bool nearHand = _settings.SkipNearHandDimM > 0 && v.Existing.Any(e => !e.Tool && Math.Abs(e.StationM - d.StationM) <= _settings.SkipNearHandDimM);
                    sb.AppendLine($"{d.Kind,-10}{d.StationM,10:F2}{d.ValueM,9:F3}{d.LowerElevM,10:F3}{d.UpperElevM,10:F3}  {(p.Side > 0 ? "ahead" : "back"),-6}{d.Reason,-15}{ProfileDimsCivil3D.SourceName(v, d)}{(nearHand ? "   [skipped: hand DIM here]" : "")}");
                }
                if (v.Plan.Issues.Count > 0)
                {
                    sb.AppendLine();
                    foreach (OperationIssue i in v.Plan.Issues.OrderByDescending(i => i.Severity)) sb.AppendLine($"{i.Severity,-8} {i.Message}");
                }
            }
            if (_sceneIssues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Drawing read:");
                foreach (OperationIssue i in _sceneIssues.Take(30)) sb.AppendLine($"{i.Severity,-8} {i.Message}");
            }
            _details.Text = sb.ToString();
        }

        private static string Or(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;

        // ---- actions -----------------------------------------------------------------------------------------------

        private void Zoom()
        {
            if (_grid.CurrentRow?.Tag is not ViewDims v) return;
            try { TopProfileCivil.ZoomTo(_doc, v.ViewId); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Zoom"); }
        }

        private void Create()
        {
            if (!ReadSettings(out string problem)) { MessageBox.Show(this, problem, Text); return; }
            List<ViewDims> views = Checked();
            if (views.Count == 0) { MessageBox.Show(this, "Check the views to dimension first.", Text); return; }
            int planned = views.Sum(v => v.Plan.Dims.Count), replaced = views.Sum(v => v.Tool);
            string msg = $"Draw {planned} DIM(s) in {views.Count} view(s), style \"{_settings.DimStyle}\", layer \"{_settings.Layer}\"?"
                + (replaced > 0 ? $"{Environment.NewLine}{Environment.NewLine}{replaced} tool DIM(s) from an earlier run are replaced." : "")
                + $"{Environment.NewLine}Hand-drawn DIMs are not touched.";
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                DimWriteResult r = ProfileDimsCivil3D.Write(_doc, views, _settings);
                string text = $"Drew {r.Created} DIM(s), replaced {r.Removed} earlier tool DIM(s)"
                    + (r.SkippedNearHand > 0 ? $", skipped {r.SkippedNearHand} next to hand-drawn DIMs" : "") + ".";
                if (r.Problems.Count > 0) text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, r.Problems.Take(20));
                MessageBox.Show(this, text, Text);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Profile DIMs: drawing failed.", ex);
                MessageBox.Show(this, "Nothing was drawn: " + ex.Message, Text);
            }
            Replan();
        }

        private void Remove()
        {
            List<ViewDims> views = Checked();
            int count = views.Sum(v => v.Tool);
            if (count == 0) { MessageBox.Show(this, "The checked views have no tool DIMs.", Text); return; }
            if (MessageBox.Show(this, $"Erase {count} tool DIM(s) from {views.Count(v => v.Tool > 0)} view(s)? Hand-drawn DIMs stay.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { MessageBox.Show(this, $"Erased {ProfileDimsCivil3D.Remove(_doc, views)} DIM(s).", Text); }
            catch (Exception ex)
            {
                C3DProLogger.Error("Profile DIMs: remove failed.", ex);
                MessageBox.Show(this, "Nothing was erased: " + ex.Message, Text);
            }
            Replan();
        }
    }
}
