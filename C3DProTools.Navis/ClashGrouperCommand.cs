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
            // "send", test, group names joined by | ("*" = every group), main side "1"/"2", file: Send to Civil 3D, the
            // selection and its clash areas saved as Navisworks Import reads them (background tests).
            if (args != null && args.Length >= 5 && args[0] == "send")
            {
                ClashTest t = ClashGrouperEngine.Tests(doc).FirstOrDefault(x => x.DisplayName == args[1]);
                if (t == null) { File.WriteAllText(args[4], "no test " + args[1]); return 1; }
                List<string> names = args[2] == "*" ? t.Children.OfType<GroupItem>().Select(g => g.DisplayName).ToList() : args[2].Split('|').ToList();
                ClashGrouperEngine.Send(doc, t, names, args[3] != "2");
                NavisLink.NavisResponse r = NavisExtractor.ReadSelection(doc);
                r.ClashAreas = ClashGrouperEngine.AreasForSelection(doc);
                using (FileStream f = File.Create(args[4]))
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(NavisLink.NavisResponse)).WriteObject(f, r);
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

    /// <summary>The Clash Grouper window: tests, statuses, main side, distances; Group, Undo; select the items of groups.</summary>
    internal sealed class FormClashGrouper : Form
    {
        private readonly CheckedListBox _tests = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly CheckedListBox _statuses = new CheckedListBox { Height = 95, Dock = DockStyle.Top, CheckOnClick = true, IntegralHeight = false };
        private readonly RadioButton _item1 = new RadioButton { Text = "Item 1 (default)", AutoSize = true, Checked = true }, _item2 = new RadioButton { Text = "Item 2", AutoSize = true };
        private readonly TextBox _gap = new TextBox { Text = "1", Width = 60 }, _span = new TextBox { Text = "25", Width = 60 };
        private readonly CheckedListBox _groups = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly ComboBox _groupTest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        private readonly TextBox _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5f) };

        public FormClashGrouper()
        {
            Text = "Clash Grouper";
            ClientSize = new Size(900, 640);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            foreach (string s in new[] { "New", "Active", "Reviewed", "Approved", "Resolved" }) _statuses.Items.Add(s, s == "New" || s == "Active" || s == "Reviewed");

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(new Label { Text = "Clash tests to group (old groups are discarded):", AutoSize = true }, 0, 0);
            left.Controls.Add(_tests, 0, 1);
            var opts = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            opts.Controls.Add(new Label { Text = "Statuses to group (each status groups on its own):", AutoSize = true }, 0, 0);
            opts.SetColumnSpan(opts.GetControlFromPosition(0, 0), 2);
            opts.Controls.Add(_statuses, 0, 1);
            opts.SetColumnSpan(_statuses, 2);
            opts.Controls.Add(new Label { Text = "Main side (the network you change):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
            opts.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { _item1, _item2 } }, 1, 2);
            opts.Controls.Add(new Label { Text = "Elements connected within (m):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 3);
            opts.Controls.Add(_gap, 1, 3);
            opts.Controls.Add(new Label { Text = "Largest span of a group (m):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 4);
            opts.Controls.Add(_span, 1, 4);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(Button("Group", (s, e) => RunGroup()));
            buttons.Controls.Add(Button("Undo last grouping", (s, e) => RunUndo()));
            buttons.Controls.Add(Button("Tick all", (s, e) => { for (int i = 0; i < _tests.Items.Count; i++) _tests.SetItemChecked(i, true); }));
            opts.Controls.Add(buttons, 0, 5);
            opts.SetColumnSpan(buttons, 2);
            left.Controls.Add(opts, 0, 2);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            right.Controls.Add(new Label { Text = "Groups and single clashes of:", AutoSize = true }, 0, 0);
            right.Controls.Add(_groupTest, 0, 1);
            right.Controls.Add(_groups, 0, 2);
            var sel = new FlowLayoutPanel { AutoSize = true };
            sel.Controls.Add(new Label { Text = "Select the ticked ones' items:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            sel.Controls.Add(Button("All", (s, e) => SelectItems(true, true)));
            sel.Controls.Add(Button("Item 1", (s, e) => SelectItems(true, false)));
            sel.Controls.Add(Button("Item 2", (s, e) => SelectItems(false, true)));
            sel.Controls.Add(Button("Send to Civil 3D", (s, e) => SendToCivil()));
            sel.Controls.Add(Button("Tick groups", (s, e) => { for (int i = 0; i < _groups.Items.Count; i++) _groups.SetItemChecked(i, !_groups.Items[i].ToString().StartsWith("   ")); }));
            right.Controls.Add(sel, 0, 3);
            right.Controls.Add(_log, 0, 4);
            _groupTest.SelectedIndexChanged += (s, e) => FillGroups();

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 400 };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            Controls.Add(split);
        }

        private static Button Button(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true }; b.Click += click; return b; }

        private static Document Doc => NavisApp.ActiveDocument;

        public void Fill()
        {
            _tests.Items.Clear();
            _groupTest.Items.Clear();
            foreach (ClashTest t in ClashGrouperEngine.Tests(Doc))
            {
                int n = ClashGrouperEngine.Results(t).Count;
                _tests.Items.Add(new TestItem(t, n), false);
                _groupTest.Items.Add(new TestItem(t, n));
            }
            if (_groupTest.Items.Count > 0) _groupTest.SelectedIndex = 0;
        }

        private sealed class TestItem
        {
            public readonly string Name; public readonly int Count;
            public TestItem(ClashTest t, int n) { Name = t.DisplayName; Count = n; }
            public override string ToString() => $"{Name}  ({Count} clashes)";
        }

        private ClashTest Find(string name) => ClashGrouperEngine.Tests(Doc).FirstOrDefault(t => t.DisplayName == name);

        private bool Options(out GroupingOptions o)
        {
            o = new GroupingOptions { Statuses = new HashSet<string>(_statuses.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase) };
            if (!double.TryParse(_gap.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out o.ConnectGap) || o.ConnectGap < 0 ||
                !double.TryParse(_span.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out o.MaxSpan) || o.MaxSpan <= 0)
            { MessageBox.Show(this, "Enter the distances in metres (e.g. 1 and 25).", Text); return false; }
            if (o.Statuses.Count == 0) { MessageBox.Show(this, "Tick at least one status to group.", Text); return false; }
            return true;
        }

        private void RunGroup()
        {
            List<TestItem> chosen = _tests.CheckedItems.Cast<TestItem>().ToList();
            if (chosen.Count == 0) { MessageBox.Show(this, "Tick the clash tests to group.", Text); return; }
            if (!Options(out GroupingOptions o)) return;
            Cursor = Cursors.WaitCursor;
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
            _log.Text = ClashGrouperEngine.Describe(runs).Replace("\n", Environment.NewLine);
            string current = (_groupTest.SelectedItem as TestItem)?.Name;
            Fill();
            for (int i = 0; i < _tests.Items.Count; i++) _tests.SetItemChecked(i, chosen.Any(c => c.Name == ((TestItem)_tests.Items[i]).Name));
            for (int i = 0; i < _groupTest.Items.Count; i++) if (((TestItem)_groupTest.Items[i]).Name == (runs.FirstOrDefault()?.Test ?? current)) _groupTest.SelectedIndex = i;
        }

        private void RunUndo()
        {
            int n = 0;
            foreach (TestItem ti in _tests.CheckedItems.Cast<TestItem>().ToList())
            {
                ClashTest t = Find(ti.Name);
                if (t != null && ClashGrouperEngine.Undo(Doc, t)) n++;
            }
            _log.Text = n == 0 ? "Nothing to undo for the ticked tests (only the last grouping in this session can be undone)." : $"{n} test(s) put back as they were before the last grouping.";
            Fill();
        }

        private void FillGroups()
        {
            _groups.Items.Clear();
            if (!(_groupTest.SelectedItem is TestItem ti) || Find(ti.Name) is not ClashTest t) return;
            foreach (SavedItem s in t.Children)
                _groups.Items.Add(s is GroupItem g ? $"{s.DisplayName}" : $"   {s.DisplayName} ({(s as ClashResult)?.Status})", false);
        }

        private List<string> TickedNames() =>
            _groups.CheckedItems.Cast<string>().Select(n => n.StartsWith("   ") ? n.Trim().Substring(0, n.Trim().LastIndexOf(" (", StringComparison.Ordinal)) : n).ToList();

        /// <summary>
        /// The other side's items of the ticked groups and clashes selected, with their areas kept for Civil 3D: Navisworks
        /// Import's "Get selection" then imports them and draws a revision cloud around each area.
        /// </summary>
        private void SendToCivil()
        {
            if (!(_groupTest.SelectedItem is TestItem ti) || Find(ti.Name) is not ClashTest t) return;
            List<string> names = TickedNames();
            if (names.Count == 0) { MessageBox.Show(this, "Tick the groups or clashes to send.", Text); return; }
            (int items, int areas) = ClashGrouperEngine.Send(Doc, t, names, _item1.Checked);
            _log.Text = $"Selected {items} item(s) of the {(_item1.Checked ? "item 2" : "item 1")} side in {areas} clash area(s)." + Environment.NewLine +
                        "In Civil 3D: Navisworks Import > Get selection, then Import. A revision cloud is drawn around each area." + Environment.NewLine +
                        "(The areas go with this selection only: selecting something else in Navisworks drops them.)";
        }

        private void SelectItems(bool item1, bool item2)
        {
            if (!(_groupTest.SelectedItem is TestItem ti) || Find(ti.Name) is not ClashTest t) return;
            var names = TickedNames();
            if (names.Count == 0) { MessageBox.Show(this, "Tick the groups or clashes whose items to select.", Text); return; }
            ModelItemCollection items = ClashGrouperEngine.Items(t, names, item1, item2);
            Doc.CurrentSelection.CopyFrom(items);
            _log.Text = $"Selected {items.Count} item(s) of {names.Count} group(s) / clash(es).";
        }
    }
}
