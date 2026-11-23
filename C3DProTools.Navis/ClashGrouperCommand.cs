using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Plugins;
using NavisApp = Autodesk.Navisworks.Api.Application;
using Color = System.Drawing.Color;
using View = System.Windows.Forms.View;

namespace C3DProTools.Navis
{
    /// <summary>
    /// Clash Grouper (Add-ins tab): the window. With arguments it runs without one (for background tests):
    /// "group", tests ("*" or names joined by |), statuses ("New,Active,Reviewed"), connect gap m, largest span m, main side
    /// ("1" or "2"), report file.
    /// </summary>
    [Plugin("C3DProTools.ClashGrouper", "C3DPro", DisplayName = "Clash Grouper", ToolTip = "Group clashes by connected elements, area and status")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class ClashGrouperCommand : AddInPlugin
    {
        private static FormClashGrouper _form;

        public override int Execute(params string[] args)
        {
            Document doc = NavisApp.ActiveDocument;
            if (args != null && args.Length >= 7 && args[0] == "group")
            {
                var o = new GroupingOptions
                {
                    Statuses = new HashSet<string>(args[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase),
                    ConnectGap = double.Parse(args[3], CultureInfo.InvariantCulture),
                    MaxSpan = double.Parse(args[4], CultureInfo.InvariantCulture),
                };
                var names = new HashSet<string>(args[1].Split('|'), StringComparer.OrdinalIgnoreCase);
                var runs = new List<TestGrouping>();
                foreach (ClashTest t in ClashGrouperEngine.Tests(doc).ToList())
                {
                    if (!names.Contains("*") && !names.Contains(t.DisplayName)) continue;
                    try { runs.Add(ClashGrouperEngine.Group(doc, t, args[5] != "2", o)); }
                    catch (Exception ex) { runs.Add(new TestGrouping { Test = t.DisplayName, Error = ex.Message }); }
                }
                File.WriteAllText(args[6], ClashGrouperEngine.Describe(runs), Encoding.UTF8);
                return 0;
            }
            // "run", test: runs the clash test again (background tests on a model whose files changed).
            if (args != null && args.Length >= 2 && args[0] == "run")
            {
                ClashTest t = ClashGrouperEngine.Tests(doc).FirstOrDefault(x => x.DisplayName == args[1]);
                if (t != null) doc.GetClash().TestsData.TestsRunTest(t);
                return 0;
            }
            // "send", test, group names joined by | ("*" = every group), main side "1"/"2", file: Send to Civil 3D (the clash
            // package), copied to the file for background tests.
            if (args != null && args.Length >= 5 && args[0] == "send")
            {
                ClashTest t = ClashGrouperEngine.Tests(doc).FirstOrDefault(x => x.DisplayName == args[1]);
                if (t == null) { File.WriteAllText(args[4], "no test " + args[1]); return 1; }
                List<string> names = args[2] == "*" ? t.Children.OfType<GroupItem>().Select(g => g.DisplayName).ToList() : args[2].Split('|').ToList();
                ClashGrouperEngine.Send(doc, t, names, args[3] != "2");
                File.Copy(NavisLink.NavisClashPackage.Path, args[4], true);
                return 0;
            }
            if (doc == null || doc.IsClear) { MessageBox.Show("Open a model with clash tests first.", "Clash Grouper"); return 0; }
            if (_form == null || _form.IsDisposed) _form = new FormClashGrouper();
            _form.Show();
            _form.Activate();
            _form.Fill();
            return 0;
        }
    }

    /// <summary>
    /// The Clash Grouper window, two steps side by side: 1 group the ticked clash tests, 2 review a test's groups and clashes,
    /// select their items or send them to Civil 3D. Every row is sized by the layout, so nothing is cut off at any window size.
    /// </summary>
    internal sealed class FormClashGrouper : Form
    {
        private static readonly Color Ink = Color.FromArgb(15, 23, 42), Muted = Color.FromArgb(100, 116, 139), Line = Color.FromArgb(226, 232, 240);
        private static readonly Color Band = Color.FromArgb(30, 41, 59), Accent = Color.FromArgb(37, 99, 235), Page = Color.FromArgb(248, 250, 252);

        private readonly CheckedListBox _tests = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly List<CheckBox> _statuses = new List<CheckBox>();
        private readonly RadioButton _item1 = new RadioButton { Text = "Item 1", AutoSize = true, Checked = true }, _item2 = new RadioButton { Text = "Item 2", AutoSize = true };
        private readonly NumericUpDown _gap = Number(1), _span = Number(25);
        private readonly ComboBox _groupTest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ListView _groups = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Ink, Padding = new Padding(10, 6, 10, 6), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };

        public FormClashGrouper()
        {
            Text = "Clash Grouper";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Page;
            ClientSize = new Size(1040, 680);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0), Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            Control header = Header();
            root.Controls.Add(header, 0, 0);
            root.SetColumnSpan(header, 2);
            root.Controls.Add(GroupStep(), 0, 1);
            root.Controls.Add(ReviewStep(), 1, 1);
            var foot = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 10) };
            foot.Controls.Add(_status);
            root.Controls.Add(foot, 0, 2);
            root.SetColumnSpan(foot, 2);
            Controls.Add(root);
            Say("Tick the clash tests on the left and press Group. Then pick a test on the right to review its groups and send them to Civil 3D.");
        }

        // ---------- layout ----------

        private static NumericUpDown Number(decimal value) =>
            new NumericUpDown { Value = value, Minimum = 0, Maximum = 10000, DecimalPlaces = 1, Increment = 0.5m, Width = 80, TextAlign = HorizontalAlignment.Right };

        private Control Header()
        {
            var band = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, BackColor = Band, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            band.Controls.Add(new Label { Text = "Clash Grouper", AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0) });
            band.Controls.Add(new Label
            {
                Text = "Groups clashes whose main-side elements touch, per status and area. Send the other side's items to Civil 3D with a revision cloud per group.",
                AutoSize = true, MaximumSize = new Size(980, 0), ForeColor = Color.FromArgb(203, 213, 225), Margin = new Padding(0, 4, 0, 0)
            });
            return band;
        }

        /// <summary>A step card: a numbered title, a one-line hint and the content filling the rest.</summary>
        private static TableLayoutPanel Card(string number, string title, string hint, out Panel body)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Margin = new Padding(12, 12, 6, 6), Padding = new Padding(14, 12, 14, 12) };
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
            card.Controls.Add(new Label { Text = $"{number}   {title}", AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 11.5f), Margin = new Padding(0) }, 0, 0);
            card.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Muted, MaximumSize = new Size(600, 0), Margin = new Padding(0, 2, 0, 8) }, 0, 1);
            body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            card.Controls.Add(body, 0, 2);
            return card;
        }

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9f), Margin = new Padding(0, 10, 0, 4) };

        private static Button Primary(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 6, 8, 0), Font = new Font("Segoe UI Semibold", 9.5f) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += click;
            return b;
        }

        private static Button Secondary(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, BackColor = Color.White, ForeColor = Ink, FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 6, 6, 0) };
            b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            b.Click += click;
            return b;
        }

        private static LinkLabel Link(string text, Action click)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, LinkColor = Accent, Margin = new Padding(0, 0, 12, 0) };
            l.LinkClicked += (s, e) => click();
            return l;
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        private Control GroupStep()
        {
            TableLayoutPanel card = Card("1", "Group clashes", "Old groups in the ticked tests are discarded and the clashes regrouped.", out Panel body);
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var testsHead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            testsHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            testsHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label testsCaption = Caption("Clash tests");
            testsCaption.Margin = new Padding(0, 0, 0, 4);
            testsHead.Controls.Add(testsCaption, 0, 0);
            testsHead.Controls.Add(Row(Link("All", () => TickTests(true)), Link("None", () => TickTests(false))), 1, 0);
            t.Controls.Add(testsHead);
            t.Controls.Add(_tests);

            t.Controls.Add(Caption("Statuses to group (each status on its own)"));
            var statuses = Row();
            foreach (string s in new[] { "New", "Active", "Reviewed", "Approved", "Resolved" })
            {
                var c = new CheckBox { Text = s, AutoSize = true, Checked = s == "New" || s == "Active" || s == "Reviewed", Margin = new Padding(0, 0, 12, 2) };
                _statuses.Add(c);
                statuses.Controls.Add(c);
            }
            t.Controls.Add(statuses);

            var rules = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
            rules.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rules.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rules.Controls.Add(new Label { Text = "Main side (the network you change)", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ink }, 0, 0);
            _item1.Margin = new Padding(0, 3, 10, 3); _item2.Margin = new Padding(0, 3, 0, 3);
            rules.Controls.Add(Row(_item1, _item2), 1, 0);
            rules.Controls.Add(new Label { Text = "Elements touching within (m)", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ink }, 0, 1);
            rules.Controls.Add(_gap, 1, 1);
            rules.Controls.Add(new Label { Text = "Largest span of a group (m)", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ink }, 0, 2);
            rules.Controls.Add(_span, 1, 2);
            t.Controls.Add(rules);

            t.Controls.Add(Row(Primary("Group ticked tests", (s, e) => RunGroup()), Secondary("Undo last grouping", (s, e) => RunUndo())));
            body.Controls.Add(t);
            return card;
        }

        private Control ReviewStep()
        {
            TableLayoutPanel card = Card("2", "Review and send", "Tick groups or single clashes, then select their items in the model or send them to Civil 3D.", out Panel body);
            card.Margin = new Padding(6, 12, 12, 6);
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var pick = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pick.Controls.Add(new Label { Text = "Clash test", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9f), Margin = new Padding(0, 0, 10, 0) }, 0, 0);
            pick.Controls.Add(_groupTest, 1, 0);
            t.Controls.Add(pick);
            t.Controls.Add(Row(new Label { Text = "Tick:", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 8, 4) },
                Link("Groups", () => TickRows(r => r.Group)), Link("Everything", () => TickRows(r => true)), Link("None", () => TickRows(r => false))));

            _groups.Columns.Add("Group or clash", 360);
            _groups.Columns.Add("Status", 90);
            _groups.Columns.Add("Clashes", 70, HorizontalAlignment.Right);
            _groups.Resize += (s, e) => { int w = _groups.ClientSize.Width - 160 - 4; if (w > 120) _groups.Columns[0].Width = w; };
            t.Controls.Add(_groups);

            t.Controls.Add(Row(new Label { Text = "Select in the model:", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 12, 8, 0) },
                Secondary("All items", (s, e) => SelectItems(true, true)), Secondary("Item 1", (s, e) => SelectItems(true, false)), Secondary("Item 2", (s, e) => SelectItems(false, true))));
            t.Controls.Add(Row(Primary("Send ticked to Civil 3D", (s, e) => SendToCivil()),
                new Label { Text = "Then in Civil 3D: Navisworks Import > Import clashes.", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 12, 0, 0) }));
            _groupTest.SelectedIndexChanged += (s, e) => FillGroups();
            body.Controls.Add(t);
            return card;
        }

        private void Say(string text, bool warn = false)
        {
            _status.Text = text;
            _status.ForeColor = warn ? Color.FromArgb(185, 28, 28) : Ink;
        }

        // ---------- data ----------

        private static Document Doc => NavisApp.ActiveDocument;

        public void Fill()
        {
            string current = (_groupTest.SelectedItem as TestItem)?.Name;
            var ticked = new HashSet<string>(_tests.CheckedItems.Cast<TestItem>().Select(x => x.Name));
            _tests.Items.Clear();
            _groupTest.Items.Clear();
            foreach (ClashTest t in ClashGrouperEngine.Tests(Doc))
            {
                int n = ClashGrouperEngine.Results(t).Count;
                _tests.Items.Add(new TestItem(t, n), ticked.Contains(t.DisplayName));
                _groupTest.Items.Add(new TestItem(t, n));
            }
            int at = _groupTest.Items.Cast<TestItem>().ToList().FindIndex(x => x.Name == current);
            if (_groupTest.Items.Count > 0) _groupTest.SelectedIndex = at >= 0 ? at : 0;
            else FillGroups();
        }

        private sealed class TestItem
        {
            public readonly string Name; public readonly int Count;
            public TestItem(ClashTest t, int n) { Name = t.DisplayName; Count = n; }
            public override string ToString() => $"{Name}   ({Count} clashes)";
        }

        private sealed class RowTag { public string Name; public bool Group; }

        private ClashTest Find(string name) => ClashGrouperEngine.Tests(Doc).FirstOrDefault(t => t.DisplayName == name);

        private void TickTests(bool on) { for (int i = 0; i < _tests.Items.Count; i++) _tests.SetItemChecked(i, on); }

        private void TickRows(Func<RowTag, bool> on) { foreach (ListViewItem i in _groups.Items) i.Checked = on((RowTag)i.Tag); }

        private bool Options(out GroupingOptions o)
        {
            o = new GroupingOptions
            {
                Statuses = new HashSet<string>(_statuses.Where(c => c.Checked).Select(c => c.Text), StringComparer.OrdinalIgnoreCase),
                ConnectGap = (double)_gap.Value,
                MaxSpan = (double)_span.Value,
            };
            if (o.MaxSpan <= 0) { Say("The largest span must be more than 0 m.", true); return false; }
            if (o.Statuses.Count == 0) { Say("Tick at least one status to group.", true); return false; }
            return true;
        }

        private void RunGroup()
        {
            List<TestItem> chosen = _tests.CheckedItems.Cast<TestItem>().ToList();
            if (chosen.Count == 0) { Say("Tick the clash tests to group (left, step 1).", true); return; }
            if (!Options(out GroupingOptions o)) return;
            Cursor = Cursors.WaitCursor;
            Say($"Grouping {chosen.Count} test(s)…");
            Refresh();
            var runs = new List<TestGrouping>();
            try
            {
                foreach (TestItem ti in chosen)
                {
                    ClashTest t = Find(ti.Name);
                    if (t == null) continue;
                    try { runs.Add(ClashGrouperEngine.Group(Doc, t, _item1.Checked, o)); }
                    catch (Exception ex) { C3DProLogger.Error("Clash Grouper failed on " + ti.Name, ex); runs.Add(new TestGrouping { Test = ti.Name, Error = ex.Message }); }
                }
            }
            finally { Cursor = Cursors.Default; }
            Fill();
            string first = runs.FirstOrDefault(r => string.IsNullOrEmpty(r.Error))?.Test;
            for (int i = 0; i < _groupTest.Items.Count; i++) if (((TestItem)_groupTest.Items[i]).Name == first) _groupTest.SelectedIndex = i;
            string text = ClashGrouperEngine.Describe(runs).Replace("\r", "");
            Say(string.Join("   ", text.Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith(" ")).Take(4)), runs.Any(r => !string.IsNullOrEmpty(r.Error)));
        }

        private void RunUndo()
        {
            int n = 0;
            foreach (TestItem ti in _tests.CheckedItems.Cast<TestItem>().ToList())
            {
                ClashTest t = Find(ti.Name);
                if (t != null && ClashGrouperEngine.Undo(Doc, t)) n++;
            }
            Fill();
            Say(n == 0 ? "Nothing to undo for the ticked tests (only the last grouping in this session can be undone)." : $"{n} test(s) put back as they were before the last grouping.", n == 0);
        }

        private void FillGroups()
        {
            _groups.BeginUpdate();
            _groups.Items.Clear();
            if (_groupTest.SelectedItem is TestItem ti && Find(ti.Name) is ClashTest t)
            {
                var bold = new Font(_groups.Font, FontStyle.Bold);
                foreach (SavedItem s in t.Children)
                {
                    List<ClashResult> rs = s is GroupItem g ? g.Children.OfType<ClashResult>().ToList() : s is ClashResult r ? new List<ClashResult> { r } : new List<ClashResult>();
                    string status = rs.Select(x => x.Status.ToString()).Distinct().Count() == 1 ? rs[0].Status.ToString() : rs.Count == 0 ? "" : "mixed";
                    var item = new ListViewItem(new[] { s is GroupItem ? s.DisplayName : "    " + s.DisplayName, status, rs.Count.ToString() })
                    {
                        Tag = new RowTag { Name = s.DisplayName, Group = s is GroupItem },
                        Font = s is GroupItem ? bold : _groups.Font,
                    };
                    _groups.Items.Add(item);
                }
            }
            _groups.EndUpdate();
        }

        private List<string> TickedNames() => _groups.CheckedItems.Cast<ListViewItem>().Select(i => ((RowTag)i.Tag).Name).ToList();

        /// <summary>The ticked groups and clashes sent as the clash package: Navisworks Import's "Import clashes" brings them in.</summary>
        private void SendToCivil()
        {
            if (!(_groupTest.SelectedItem is TestItem ti) || Find(ti.Name) is not ClashTest t) return;
            List<string> names = TickedNames();
            if (names.Count == 0) { Say("Tick the groups or clashes to send (right, step 2).", true); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                NavisLink.NavisResponse p = ClashGrouperEngine.Send(Doc, t, names, _item1.Checked);
                string side = _item1.Checked ? "Item 2" : "Item 1";
                Say($"Sent {p.Elements.Count} {side} item(s) in {p.ClashAreas.Count} clash area(s) from {t.DisplayName}." +
                    "  In Civil 3D: Navisworks Import > Import clashes (one revision cloud per area)." +
                    (p.Warnings.Count > 0 ? "  " + string.Join("  ", p.Warnings.Take(2)) : ""), p.Elements.Count == 0);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Clash Grouper: Send to Civil 3D failed", ex);
                Say("Send to Civil 3D failed: " + ex.Message, true);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void SelectItems(bool item1, bool item2)
        {
            if (!(_groupTest.SelectedItem is TestItem ti) || Find(ti.Name) is not ClashTest t) return;
            var names = TickedNames();
            if (names.Count == 0) { Say("Tick the groups or clashes whose items to select.", true); return; }
            ModelItemCollection items = ClashGrouperEngine.Items(t, names, item1, item2);
            Doc.CurrentSelection.CopyFrom(items);
            Say($"Selected {items.Count} item(s) of {names.Count} group(s) or clash(es).");
        }
    }
}
