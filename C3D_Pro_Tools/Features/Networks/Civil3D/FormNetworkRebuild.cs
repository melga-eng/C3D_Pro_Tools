using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.DatabaseServices;
using Exception = System.Exception;
using Label = System.Windows.Forms.Label;
using Font = System.Drawing.Font;
using FlowDirection = System.Windows.Forms.FlowDirection;

namespace C3DProTools.Features.Networks.Civil3D
{
    /// <summary>
    /// The Rebuild Network window: the drawing's networks (data-shortcut references first and ticked), Check to see what each
    /// would become, Rebuild to make them; the report stays in the window.
    /// </summary>
    internal sealed class FormNetworkRebuild : Form
    {
        private readonly Document _doc;
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        private readonly TextBox _report = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font("Consolas", 9f), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        private readonly Label _count = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private readonly RadioButton _all = new RadioButton { Text = "All", AutoSize = true, Checked = true, Margin = new Padding(3, 8, 6, 3) };
        private readonly RadioButton _structures = new RadioButton { Text = "Structures only", AutoSize = true, Margin = new Padding(3, 8, 6, 3) };
        private readonly RadioButton _pipes = new RadioButton { Text = "Pipes only", AutoSize = true, Margin = new Padding(3, 8, 6, 3) };
        private readonly Label _pickedText = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private HashSet<ObjectId> _picked;
        private List<NetworkRebuild.NetworkRow> _rows = new List<NetworkRebuild.NetworkRow>();

        public FormNetworkRebuild(Document doc)
        {
            _doc = doc;
            Text = "Rebuild Network";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 640);
            MinimumSize = new Size(560, 480);
            ShowInTaskbar = false;

            _list.Columns.Add("Network", 330);
            _list.Columns.Add("Kind", 150);
            _list.Columns.Add("Structures", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Pipes", 60, HorizontalAlignment.Right);
            _list.Columns.Add("Note", 110);
            _list.ItemChecked += (s, e) => UpdateCount();

            Button rebuild = StyleUi.Primary("Rebuild", (s, e) => Run(true));
            Button check = StyleUi.Secondary("Check", (s, e) => Run(false));
            Button close = StyleUi.Secondary("Close", (s, e) => Close());
            CancelButton = close;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 10, 12, 6) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) };
            tools.Controls.Add(StyleUi.Secondary("Tick references", (s, e) => Tick(r => r.Reference)));
            tools.Controls.Add(StyleUi.Secondary("Tick all", (s, e) => Tick(r => true)));
            tools.Controls.Add(StyleUi.Secondary("Clear", (s, e) => Tick(r => false)));
            tools.Controls.Add(_count);
            body.Controls.Add(tools, 0, 0);
            body.Controls.Add(_list, 0, 1);
            var partsRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
            partsRow.Controls.Add(StyleUi.Caption("Parts"));
            partsRow.Controls.Add(_all);
            partsRow.Controls.Add(_structures);
            partsRow.Controls.Add(_pipes);
            partsRow.Controls.Add(StyleUi.Secondary("Pick parts <", (s, e) => Pick()));
            partsRow.Controls.Add(StyleUi.Secondary("Clear pick", (s, e) => SetPicked(null)));
            partsRow.Controls.Add(_pickedText);
            body.Controls.Add(partsRow, 0, 2);
            body.Controls.Add(ToolUi.Note("Check lists what each ticked network holds and which part each size becomes; nothing changes. Rebuild draws the parts " +
                                          "into \"<name>" + NetworkRebuild.Suffix + "\" (made the first time, added to after): structures, pipes, levels, connections, " +
                                          "names, styles and layers; parts already in it are skipped, so structures and pipes can be done one after the other. " +
                                          "The original is only read. All or nothing, one UNDO."), 0, 3);
            body.Controls.Add(StyleUi.Caption("Report"), 0, 4);
            body.Controls.Add(_report, 0, 5);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 8), BackColor = StyleUi.Surface };
            footer.Controls.Add(close);
            footer.Controls.Add(rebuild);
            footer.Controls.Add(check);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("Rebuild Network", "A network you cannot edit, drawn again as an ordinary one"));

            Fill(null);
            SetPicked(null);
            Resize += (s, e) => ToolUi.FitNotes(body, body.ClientSize.Width - body.Padding.Horizontal - 6);
            Shown += (s, e) => ToolUi.FitNotes(body, body.ClientSize.Width - body.Padding.Horizontal - 6);
        }

        /// <summary>Lists the networks again; keeps the given ticks, or ticks the references not rebuilt yet.</summary>
        private void Fill(HashSet<string> keep)
        {
            _rows = NetworkRebuild.Inventory(_doc);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (NetworkRebuild.NetworkRow r in _rows)
            {
                var item = new ListViewItem(r.Name) { Tag = r };
                item.SubItems.Add(r.Reference ? "Data-shortcut reference" : "Ordinary");
                item.SubItems.Add(r.Structures.ToString());
                item.SubItems.Add(r.Pipes.ToString());
                item.SubItems.Add(r.HasRebuilt ? "rebuilt before" : "");
                item.Checked = keep != null ? keep.Contains(r.Name) : r.Reference && !r.HasRebuilt;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            if (_rows.Count == 0) _report.Text = "This drawing has no pipe network.";
            UpdateCount();
        }

        private void Tick(Func<NetworkRebuild.NetworkRow, bool> on)
        {
            foreach (ListViewItem i in _list.Items) i.Checked = on((NetworkRebuild.NetworkRow)i.Tag);
        }

        private List<NetworkRebuild.NetworkRow> Ticked() =>
            _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (NetworkRebuild.NetworkRow)i.Tag).ToList();

        private void UpdateCount() => _count.Text = $"{_list.CheckedItems.Count} of {_list.Items.Count} ticked";

        private void SetPicked(HashSet<ObjectId> picked)
        {
            _picked = picked;
            _pickedText.Text = picked == null ? "every part of the ticked networks" : $"{picked.Count} picked part(s)";
        }

        /// <summary>Parts picked on screen: only those are rebuilt, and their networks are the ones ticked.</summary>
        private void Pick()
        {
            Editor ed = _doc.Editor;
            var ids = new HashSet<ObjectId>();
            var nets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (ed.StartUserInteraction(this))
            {
                var opts = new PromptSelectionOptions { MessageForAdding = "\nSelect pipes and structures to rebuild: " };
                PromptSelectionResult res = ed.GetSelection(opts, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "AECC_PIPE,AECC_STRUCTURE") }));
                if (res.Status != PromptStatus.OK) return;
                using (_doc.LockDocument())
                using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in res.Value.GetObjectIds())
                        if (tr.GetObject(id, OpenMode.ForRead) is Part p) { ids.Add(id); try { nets.Add(p.NetworkName); } catch (Exception) { } }
                    tr.Commit();
                }
            }
            if (ids.Count == 0) return;
            SetPicked(ids);
            foreach (ListViewItem i in _list.Items) i.Checked = nets.Contains(((NetworkRebuild.NetworkRow)i.Tag).Name);
        }

        private void Run(bool write)
        {
            List<NetworkRebuild.NetworkRow> ticked = Ticked();
            if (ticked.Count == 0) { MessageBox.Show(this, "Tick at least one network.", Text); return; }
            var scope = new NetworkRebuild.RebuildScope { Structures = !_pipes.Checked, Pipes = !_structures.Checked, Picked = _picked };

            Cursor = Cursors.WaitCursor;
            _report.Text = write ? "Rebuilding..." : "Checking...";
            _report.Refresh();
            // Execute never throws (it reports its own failure); anything after it only shows the result.
            string log = NetworkRebuild.Execute(_doc, ticked.Select(r => r.Name).ToList(), write, scope);
            try
            {
                string summary = RunReport.Write(log, "C3DPROREBUILD", write ? "Rebuild" : "Report");
                string file = summary.Split('\n').LastOrDefault(l => l.StartsWith("Full report: ", StringComparison.Ordinal));
                _report.Text = log.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + (file != null ? Environment.NewLine + file : "");
                if (write)
                {
                    ViewHelper.Regen(_doc);
                    Fill(new HashSet<string>(ticked.Select(r => r.Name), StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Rebuild Network window failed", ex);
                _report.Text = log.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine + $"(The window could not refresh: {ex.Message}. Close and reopen it.)";
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
