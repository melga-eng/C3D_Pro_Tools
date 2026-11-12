using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Network Organizer: every gravity network with its parts, sizes and the copies it shares with others (or itself), and a
    /// merge of the ticked networks into one: copies deleted (the most used one stays), parts of another size in the same place
    /// left for you to choose, everything else moved. Every merge is previewed part by part first, and is one UNDO.
    /// </summary>
    internal sealed class FormNetworkOrganizer : Form
    {
        private readonly DataGridView _grid = ToolUi.Grid();
        private readonly RadioButton _rbInto = new RadioButton { Text = "Into the network:", AutoSize = true, Checked = true }, _rbNew = new RadioButton { Text = "Into a new network:", AutoSize = true };
        private readonly ComboBox _cbTarget = ToolUi.Combo(320);
        private readonly TextBox _txtNew = new TextBox { Width = 320, Text = "Merged network" };
        private readonly CheckBox _chkConnect = ToolUi.Check("Connect pipe ends lying inside one of the network's structures", true);
        private readonly CheckBox _chkDeleteEmpty = ToolUi.Check("Delete the networks left empty", true);
        private readonly Label _lblStatus = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 10, 3, 3) };
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private List<NetworkSummary> _networks = new List<NetworkSummary>();

        public FormNetworkOrganizer()
        {
            Text = "C3D Pro Tools - Network Organizer";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(820, 560);
            ClientSize = new Size(1000, 720);

            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel list = ToolUi.Card("Gravity networks",
                "Copies: parts with another part in the same place (pipes with both ends together, either way round; structures at the same point), " +
                "in another network or the same one. Data shortcut networks are read-only here and cannot be merged.");
            _grid.Dock = DockStyle.None;
            _grid.Height = 300;
            _grid.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _grid.ReadOnly = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "", FillWeight = 12 });
            foreach ((string name, string header, int w) in new[] { ("Network", "Network", 120), ("Pipes", "Pipes", 25), ("Structures", "Structures", 30), ("Copies", "Copies", 25), ("Sizes", "Main sizes", 110), ("Note", "", 50) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = w, ReadOnly = true });
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            ToolUi.Wide(list, _grid);
            ToolUi.Wide(list, ToolUi.Inline(
                StyleUi.Secondary("Refresh", (s, e) => ReadDrawing()),
                StyleUi.Secondary("Tick those with copies", (s, e) => Tick(n => n.Copies > 0)),
                StyleUi.Secondary("Tick the empty ones", (s, e) => Tick(n => n.Empty)),
                StyleUi.Secondary("Untick all", (s, e) => Tick(n => false)),
                StyleUi.Secondary("Zoom to the ticked", (s, e) => ZoomToTicked())));
            ToolUi.Add(stack, list);

            TableLayoutPanel merge = ToolUi.Card("Merge the ticked networks",
                "Every part of the ticked networks ends up in one network. Copies of the same part in the same place are deleted; the copy with the " +
                "most labels and profile views stays (then the one already in the target). Parts of another size in the same place are shown for you to " +
                "choose and kept unless ticked. Pipes on a deleted structure are connected to the one that stays. You see every part's change first.");
            ToolUi.Wide(merge, ToolUi.Inline(_rbInto, _cbTarget));
            ToolUi.Wide(merge, ToolUi.Inline(_rbNew, _txtNew));
            ToolUi.Wide(merge, _chkConnect);
            ToolUi.Wide(merge, _chkDeleteEmpty);
            ToolUi.Wide(merge, ToolUi.Note("Parts without a part family (duct banks copied from a pipe, for one) may be refused by Civil 3D when moving " +
                                            "to another network; those stay where they are and are listed. Merging into the network that holds them avoids it."));
            ToolUi.Wide(merge, ToolUi.Inline(
                StyleUi.Primary("Merge…", (s, e) => Merge()),
                StyleUi.Secondary("Delete the ticked empty networks", (s, e) => DeleteEmpty())));
            ToolUi.Add(stack, merge);
            ToolUi.Exclusive(_rbInto, _rbNew);
            _rbInto.CheckedChanged += (s, e) => { _cbTarget.Enabled = _rbInto.Checked; _txtNew.Enabled = _rbNew.Checked; };
            _txtNew.Enabled = false;

            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 4, 10, 8) };
            footer.Controls.Add(StyleUi.Secondary("Close", (s, e) => Close()));
            footer.Controls.Add(_lblStatus);
            Controls.Add(host);
            Controls.Add(footer);
            Controls.Add(ToolUi.Header("Network Organizer", "Merge networks split by imports, and remove the copies shared between them."));
            Shown += (s, e) => { ToolUi.FitNotes(host, host.ClientSize.Width); ReadDrawing(); };
            Resize += (s, e) => ToolUi.FitNotes(host, host.ClientSize.Width);
        }

        private Document Doc => AcadApp.DocumentManager.MdiActiveDocument;

        private void ReadDrawing()
        {
            HashSet<string> ticked = new HashSet<string>(TickedSummaries().Select(n => n.Name));
            string target = _cbTarget.SelectedItem as string;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (Transaction tr = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                    _networks = NetworkOrganizer.Read(tr, Doc.Database, CivilDocument.GetCivilDocument(Doc.Database));
            }
            catch (Exception ex) { _lblStatus.Text = "Could not read the networks: " + ex.Message; return; }
            finally { Cursor = Cursors.Default; }
            _grid.Rows.Clear();
            foreach (NetworkSummary n in _networks)
            {
                int i = _grid.Rows.Add(ticked.Contains(n.Name), n.Name, n.Pipes, n.Structures, n.Copies, n.Sizes, n.Reference ? "Data shortcut" : n.Empty ? "Empty" : "");
                _grid.Rows[i].Tag = n;
                if (n.Reference) { _grid.Rows[i].ReadOnly = true; _grid.Rows[i].DefaultCellStyle.ForeColor = StyleUi.Muted; }
                else if (n.Copies > 0) _grid.Rows[i].Cells["Copies"].Style.ForeColor = StyleUi.Danger;
            }
            _cbTarget.Items.Clear();
            foreach (NetworkSummary n in _networks.Where(x => !x.Reference)) _cbTarget.Items.Add(n.Name);
            ToolUi.Select(_cbTarget, target);
            if (_cbTarget.SelectedIndex < 0 && _cbTarget.Items.Count > 0) _cbTarget.SelectedIndex = 0;
            _lblStatus.Text = $"{_networks.Count} gravity network(s); {_networks.Count(n => n.Copies > 0)} with copies, {_networks.Count(n => n.Empty)} empty.";
        }

        private List<NetworkSummary> TickedSummaries()
        {
            _grid.EndEdit();
            return _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["Use"].Value is true && r.Tag is NetworkSummary n && !n.Reference).Select(r => (NetworkSummary)r.Tag).ToList();
        }

        private void Tick(Func<NetworkSummary, bool> which)
        {
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag is NetworkSummary n && !n.Reference) r.Cells["Use"].Value = which(n);
        }

        private void ZoomToTicked()
        {
            List<NetworkSummary> ticked = TickedSummaries();
            if (ticked.Count == 0) { _lblStatus.Text = "Tick the networks to zoom to."; return; }
            Extents3d? ext;
            using (Transaction tr = Doc.Database.TransactionManager.StartOpenCloseTransaction()) ext = NetworkOrganizer.Extents(tr, ticked.Select(n => n.Id));
            if (ext.HasValue) ViewHelper.ZoomTo(ext.Value, 1.15, 10.0);
            else _lblStatus.Text = "The ticked networks have no parts.";
        }

        private static string ActionText(MergeRow r) =>
            r.Action == MergeAction.Move ? "Move into the target"
            : r.Action == MergeAction.Stay ? "Stays (already in the target)"
            : r.Action == MergeAction.DeleteCopy ? $"Delete: a copy of {r.Keeper.Name} ({r.Keeper.Network})"
            : $"Delete? Same place as {r.Keeper.Name} ({r.Keeper.Network}) but {r.Part.Size} vs {r.Keeper.Size}: tick to delete it";

        private void Merge()
        {
            List<NetworkSummary> ticked = TickedSummaries();
            ObjectId target = ObjectId.Null;
            if (_rbInto.Checked)
            {
                NetworkSummary t = _networks.FirstOrDefault(n => n.Name == _cbTarget.SelectedItem as string);
                if (t == null) { MessageBox.Show(this, "Choose the network to merge into.", Text); return; }
                target = t.Id;
                if (!ticked.Contains(t)) ticked.Insert(0, t);
            }
            else if (string.IsNullOrWhiteSpace(_txtNew.Text)) { MessageBox.Show(this, "Enter the new network's name.", Text); return; }
            else if (_networks.Any(n => string.Equals(n.Name, _txtNew.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show(this, "A network of that name exists already: choose it under \"Into the network\", or another name.", Text); return; }
            if (ticked.Count == 0 || (ticked.Count == 1 && ticked[0].Id == target && ticked[0].Copies == 0))
            { MessageBox.Show(this, "Tick the networks to merge (or one network with copies, to remove them).", Text); return; }

            List<(MergeRow row, ObjectId id, ObjectId network)> plan;
            using (Transaction tr = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                plan = NetworkOrganizer.Plan(tr, Doc.Database, ticked.Select(n => n.Id), target);
            List<PreviewRow> rows = plan.Where(p => p.row.Action != MergeAction.Stay).Select(p => new PreviewRow
            {
                Tag = p, Checked = p.row.Action != MergeAction.SamePlaceOtherSize,
                Cells = new[] { p.row.Part.Name, p.row.Part.IsPipe ? "Pipe" : "Structure", p.row.Part.Network, p.row.Part.Size, ActionText(p.row) }
            }).ToList();
            int copies = plan.Count(p => p.row.Action == MergeAction.DeleteCopy), other = plan.Count(p => p.row.Action == MergeAction.SamePlaceOtherSize);
            int unique = plan.Count(p => p.row.Action == MergeAction.Move || p.row.Action == MergeAction.Stay);
            string into = target.IsNull ? $"a new network \"{_txtNew.Text.Trim()}\"" : $"\"{_cbTarget.SelectedItem}\"";
            string intro = $"{ticked.Count} network(s) into {into}: {unique} part(s) in all, {copies} copies to delete" +
                           (other > 0 ? $", {other} part(s) in the same place as another of a different size (unticked: kept; tick to delete)" : "") +
                           ". Untick a move to leave that part where it is, or a copy to keep it.";
            if (rows.Count == 0) { MessageBox.Show(this, "Nothing to do: the parts are all in the target already and there are no copies.", Text); return; }
            using (FormStylePreview preview = new FormStylePreview("Merge networks", intro, new[] { "Part", "Type", "Network", "Size", "Change" }, rows, "Merge", true, "Network Organizer - Merge"))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                HashSet<object> chosen = new HashSet<object>(preview.Chosen);
                rows.RemoveAll(r => !chosen.Contains(r.Tag));
            }
            HashSet<ObjectId> move = new HashSet<ObjectId>(rows.Select(r => ((ValueTuple<MergeRow, ObjectId, ObjectId>)r.Tag)).Where(p => p.Item1.Action == MergeAction.Move).Select(p => p.Item2));
            Dictionary<string, ObjectId> idOf = plan.ToDictionary(p => p.row.Part.Id, p => p.id);
            Dictionary<ObjectId, ObjectId> delete = rows.Select(r => ((ValueTuple<MergeRow, ObjectId, ObjectId>)r.Tag))
                .Where(p => p.Item1.Action == MergeAction.DeleteCopy || p.Item1.Action == MergeAction.SamePlaceOtherSize)
                .ToDictionary(p => p.Item2, p => idOf[p.Item1.Keeper.Id]);

            MergeResult result;
            try
            {
                using (Doc.LockDocument())
                using (Transaction tr = Doc.Database.TransactionManager.StartTransaction())
                {
                    result = NetworkOrganizer.Merge(tr, CivilDocument.GetCivilDocument(Doc.Database), ticked.Select(n => n.Id).ToList(), target, _txtNew.Text,
                        move, delete, _chkConnect.Checked, _chkDeleteEmpty.Checked, Doc.Editor);
                    tr.Commit();
                }
                ViewHelper.Regen(Doc);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Network Organizer: the merge failed", ex);
                MessageBox.Show(this, "The merge failed and nothing was changed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ReadDrawing();
            string text = $"Merged into {result.TargetName}: {result.Moved} part(s) moved, {result.Deleted} copie(s) deleted" +
                          (result.Reconnected > 0 ? $", {result.Reconnected} pipe end(s) moved to the structure that stays" : "") +
                          (result.Connected > 0 ? $", {result.Connected} pipe end(s) connected to the structure they lie in" : "") +
                          (result.NetworksDeleted > 0 ? $", {result.NetworksDeleted} empty network(s) deleted" : "") + ". UNDO reverses it." +
                          (result.Log.Count > 0 ? "\n\n" + string.Join("\n", result.Log.Take(20)) + (result.Log.Count > 20 ? $"\n… and {result.Log.Count - 20} more" : "") : "");
            _lblStatus.Text = $"Merged into {result.TargetName}.";
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, result.Log.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void DeleteEmpty()
        {
            List<NetworkSummary> empty = TickedSummaries().Where(n => n.Empty).ToList();
            if (empty.Count == 0) { MessageBox.Show(this, "Tick the empty networks to delete (\"Tick the empty ones\").", Text); return; }
            if (MessageBox.Show(this, $"Delete {empty.Count} empty network(s)?\n\n" + string.Join("\n", empty.Take(15).Select(n => "• " + n.Name)), Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            List<string> log = new List<string>();
            int n;
            using (Doc.LockDocument())
            using (Transaction tr = Doc.Database.TransactionManager.StartTransaction())
            {
                n = NetworkOrganizer.DeleteEmpty(tr, empty.Select(x => x.Id), log);
                tr.Commit();
            }
            ReadDrawing();
            _lblStatus.Text = $"Deleted {n} empty network(s)." + (log.Count > 0 ? " " + string.Join(" ", log) : "");
        }
    }
}
