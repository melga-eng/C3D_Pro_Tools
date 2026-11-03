using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// The drawing's parts lists in one place: lists, their pipe and structure families and sizes, with how many parts use each.
    /// Create, copy, rename or delete lists; add families and catalog sizes; make new sizes (Method A / B); remove what nothing uses.
    /// Shared by the Network Editor and the Profile &amp; Cover Designer (through every part chooser).
    /// </summary>
    internal sealed class FormPartsLists : Form
    {
        private sealed class Node
        {
            public ObjectId ListId, FamilyId, SizeId;
            public DomainType Domain;
            public int Level; // 0 list, 1 domain, 2 family, 3 size
        }

        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None, ShowLines = true, FullRowSelect = true, ItemHeight = 22 };
        private readonly Label _title = new Label { AutoSize = true, Font = StyleUi.Heading, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false };
        private readonly Label _detail = ToolUi.Note("");
        private readonly FlowLayoutPanel _actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 8, 3, 3) };
        private readonly ToolTip _tips = new ToolTip();
        private Dictionary<ObjectId, int> _usage = new Dictionary<ObjectId, int>();

        public FormPartsLists(string selectList = null)
        {
            Text = "C3D Pro Tools - Parts Lists";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimumSize = new Size(820, 560);
            ClientSize = new Size(980, 680);

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, BackColor = StyleUi.Border, FixedPanel = FixedPanel.Panel2 };
            Panel treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = StyleUi.Panel };
            treeHost.Controls.Add(_tree);
            split.Panel1.Controls.Add(treeHost);
            TableLayoutPanel side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = StyleUi.Surface, Padding = new Padding(14, 12, 14, 12), AutoScroll = true };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (Control c in new Control[] { _title, _detail, _actions }) { side.RowStyles.Add(new RowStyle(SizeType.AutoSize)); side.Controls.Add(c, 0, side.RowCount++); }
            side.Resize += (s, e) => _detail.MaximumSize = new Size(Math.Max(150, side.ClientSize.Width - 30), 0);
            split.Panel2.Controls.Add(side);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = StyleUi.Panel, Padding = new Padding(12, 6, 12, 6) };
            Button close = StyleUi.Primary("Close");
            close.DialogResult = DialogResult.OK;
            close.Dock = DockStyle.Right;
            footer.Controls.Add(_status);
            footer.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            Controls.Add(split);
            Controls.Add(ToolUi.Header("Parts Lists", "Families and sizes of the drawing's parts lists. A part in a network can only use its network's list.",
                StyleUi.HeaderButton("Set up Method A", (s, e) => SetUpMethodA()),
                StyleUi.HeaderButton("New parts list…", (s, e) => NewList(null))));
            Controls.Add(footer);

            _tree.AfterSelect += (s, e) => ShowNode();
            _tree.BeforeExpand += (s, e) => { if (e.Node.Tag is Node n && n.Level == 2) FillSizes(e.Node); };
            Shown += (s, e) =>
            {
                split.Panel2MinSize = 320;
                split.SplitterDistance = Math.Max(300, split.Width - (int)Math.Round(380 * DeviceDpi / 96.0));
                Reload(selectList);
            };
        }

        // =====================================================================================
        // Tree
        // =====================================================================================

        private void Reload(string selectList = null, ObjectId selectId = default, ObjectId selectSize = default)
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            CivilDocument doc = CivilApplication.ActiveDocument;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                _usage = PartsListTools.Usage(tr, doc);
                foreach (ObjectId listId in doc.Styles.PartsListSet)
                {
                    if (!(tr.GetObject(listId, OpenMode.ForRead) is PartsList pl)) continue;
                    List<string> nets = PartsListTools.NetworksUsing(tr, doc, listId);
                    TreeNode listNode = new TreeNode(pl.Name + (nets.Count > 0 ? $"   ({nets.Count} network{(nets.Count == 1 ? "" : "s")})" : "")) { Tag = new Node { ListId = listId, Level = 0 }, NodeFont = StyleUi.Bold };
                    foreach (DomainType domain in new[] { DomainType.Pipe, DomainType.Structure })
                    {
                        List<PartFamily> families = PartsListTools.Families(tr, listId, domain);
                        TreeNode domainNode = new TreeNode($"{(domain == DomainType.Pipe ? "Pipes" : "Structures")} ({families.Count})") { Tag = new Node { ListId = listId, Domain = domain, Level = 1 } };
                        foreach (PartFamily f in families)
                        {
                            TreeNode famNode = new TreeNode($"{f.Name}   ({f.PartSizeCount} size{(f.PartSizeCount == 1 ? "" : "s")}{Used(f.ObjectId)})")
                            { Tag = new Node { ListId = listId, Domain = domain, FamilyId = f.ObjectId, Level = 2 } };
                            famNode.Nodes.Add(new TreeNode("…"));
                            domainNode.Nodes.Add(famNode);
                        }
                        listNode.Nodes.Add(domainNode);
                    }
                    _tree.Nodes.Add(listNode);
                    if (pl.Name == selectList) { listNode.Expand(); _tree.SelectedNode = listNode; }
                }
            }
            _tree.EndUpdate();
            if (!selectId.IsNull) Select(_tree.Nodes, selectId, selectSize);
            if (_tree.SelectedNode == null && _tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
            ShowNode();
        }

        /// <summary>Selects the list or family with this id (opening a family to select <paramref name="size"/> in it).</summary>
        private bool Select(TreeNodeCollection nodes, ObjectId id, ObjectId size)
        {
            foreach (TreeNode t in nodes)
            {
                if (!(t.Tag is Node n)) continue;
                if (n.Level == 0 && n.ListId == id || n.Level == 2 && n.FamilyId == id)
                {
                    _tree.SelectedNode = t;
                    if (n.Level == 2 && !size.IsNull)
                    {
                        FillSizes(t);
                        t.Expand();
                        foreach (TreeNode s in t.Nodes) if (s.Tag is Node sn && sn.SizeId == size) _tree.SelectedNode = s;
                    }
                    _tree.SelectedNode.EnsureVisible();
                    return true;
                }
                if (n.Level < 2 && Select(t.Nodes, id, size)) return true;
            }
            return false;
        }

        private string Used(ObjectId id) => _usage.TryGetValue(id, out int n) && n > 0 ? $", used by {n}" : "";

        private void FillSizes(TreeNode famNode)
        {
            Node n = (Node)famNode.Tag;
            famNode.Nodes.Clear();
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                PartFamily f = (PartFamily)tr.GetObject(n.FamilyId, OpenMode.ForRead);
                foreach ((ObjectId id, string name, Dictionary<PartContextType, double> v) in PartsListTools.ListSizes(tr, f))
                    famNode.Nodes.Add(new TreeNode(name + (_usage.TryGetValue(id, out int u) && u > 0 ? $"   (used by {u})" : ""))
                    { Tag = new Node { ListId = n.ListId, Domain = n.Domain, FamilyId = n.FamilyId, SizeId = id, Level = 3 }, ToolTipText = PartsListTools.Describe(v) });
            }
        }

        // =====================================================================================
        // Details and actions of the selected node
        // =====================================================================================

        private void ShowNode()
        {
            _actions.Controls.Clear();
            if (!(_tree.SelectedNode?.Tag is Node n)) { _title.Text = ""; _detail.Text = ""; return; }
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            CivilDocument doc = CivilApplication.ActiveDocument;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                PartsList pl = (PartsList)tr.GetObject(n.ListId, OpenMode.ForRead);
                List<string> nets = PartsListTools.NetworksUsing(tr, doc, n.ListId);
                switch (n.Level)
                {
                    case 0:
                        _title.Text = pl.Name;
                        _detail.Text = nets.Count == 0 ? "No network uses this list." : "Used by: " + string.Join(", ", nets) + ".";
                        Action("New list (copy of this)…", () => NewList(pl.Name, true));
                        Action("Rename…", () => RenameList(n.ListId, pl.Name));
                        Action("Delete list", () => DeleteList(n.ListId, pl.Name), nets.Count == 0, "A list used by a network cannot be deleted.");
                        break;
                    case 1:
                        _title.Text = $"{pl.Name} · {(n.Domain == DomainType.Pipe ? "Pipes" : "Structures")}";
                        _detail.Text = "Add families from the catalog; each comes with its default size, and you then add the sizes you need.";
                        Action("Add families…", () => AddFamilies(n.ListId, n.Domain));
                        break;
                    case 2:
                    {
                        PartFamily f = (PartFamily)tr.GetObject(n.FamilyId, OpenMode.ForRead);
                        int used = _usage.TryGetValue(n.FamilyId, out int u) ? u : 0;
                        _title.Text = f.Name;
                        _detail.Text = $"{f.PartSizeCount} size(s) in \"{pl.Name}\"; " + (used > 0 ? $"{used} part(s) in the drawing use this family." : "no part uses it.");
                        Action("Add catalog sizes…", () => AddCatalogSizes(n.FamilyId));
                        Action("New size…", () => NewSize(n.ListId, n.FamilyId));
                        Action("Remove family", () => RemoveFamily(n.ListId, n.FamilyId, f.Name), used == 0, "Parts in the drawing use this family.");
                        break;
                    }
                    case 3:
                    {
                        PartSize s = (PartSize)tr.GetObject(n.SizeId, OpenMode.ForRead);
                        PartFamily f = (PartFamily)tr.GetObject(n.FamilyId, OpenMode.ForRead);
                        int used = _usage.TryGetValue(n.SizeId, out int u) ? u : 0;
                        _title.Text = s.Name;
                        string dims = "";
                        try { dims = PartsListTools.Describe(PartsListTools.ReadValues(f, s.SizeDataRecord)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        _detail.Text = $"{f.Name} in \"{pl.Name}\". {dims}. " + (used > 0 ? $"{used} part(s) use this size." : "No part uses it.");
                        Action("New size…", () => NewSize(n.ListId, n.FamilyId));
                        Action("Remove size", () => RemoveSize(n.FamilyId, n.SizeId, s.Name), used == 0 && f.PartSizeCount > 1,
                            used > 0 ? "Parts in the drawing use this size." : "A family keeps at least one size.");
                        break;
                    }
                }
            }
        }

        private void Action(string text, System.Action run, bool enabled = true, string whyNot = null)
        {
            Button b = StyleUi.Secondary(text, (s, e) =>
            {
                try { run(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            });
            b.MinimumSize = new Size(220, 32);
            b.Enabled = enabled;
            _actions.Controls.Add(b);
            if (!enabled && whyNot != null) { Label why = ToolUi.Note(whyNot); why.Margin = new Padding(4, 0, 0, 6); _actions.Controls.Add(why); }
        }

        /// <summary>Runs a change in one locked transaction; false (nothing kept) when it throws.</summary>
        private bool Change(Action<Transaction> work)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                work(tr);
                tr.Commit();
            }
            return true;
        }

        private void NewList(string copyOf, bool copy = false)
        {
            string name = AskNewList(this, copyOf, out bool doCopy, copy);
            if (name == null) return;
            CivilDocument doc = CivilApplication.ActiveDocument;
            List<string> log = new List<string>();
            ObjectId created = ObjectId.Null;
            Change(tr =>
            {
                ObjectId from = ObjectId.Null;
                if (doCopy && copyOf != null)
                    foreach (ObjectId id in doc.Styles.PartsListSet) if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl && pl.Name == copyOf) from = id;
                name = PartsListTools.UniqueListName(tr, doc, name);
                created = PartsListTools.CreateList(tr, doc, name, from, log);
            });
            _status.Text = $"Created the parts list \"{name}\"." + (log.Count > 0 ? $" {log.Count} size(s) could not be copied." : "");
            if (log.Count > 0) MessageBox.Show(this, string.Join("\n", log.Take(20)), "Not copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Reload(name);
        }

        private void RenameList(ObjectId id, string old)
        {
            string name = AskText(this, "Rename parts list", "New name:", old);
            if (string.IsNullOrWhiteSpace(name) || name == old) return;
            Change(tr => ((PartsList)tr.GetObject(id, OpenMode.ForWrite)).Name = name.Trim());
            Reload(name.Trim());
        }

        private void DeleteList(ObjectId id, string name)
        {
            if (MessageBox.Show(this, $"Delete the parts list \"{name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Change(tr => tr.GetObject(id, OpenMode.ForWrite).Erase());
            _status.Text = $"Deleted \"{name}\".";
            Reload();
        }

        private void AddFamilies(ObjectId listId, DomainType domain)
        {
            HashSet<string> have;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                have = new HashSet<string>(PartsListTools.Families(tr, listId, domain).Select(f => f.GUID), StringComparer.OrdinalIgnoreCase);
            List<PreviewRow> rows = PartsListTools.CatalogFamilies(domain).Where(f => !have.Contains(f.guid))
                .Select(f => new PreviewRow { Tag = f.guid, Checked = false, Cells = new[] { f.description, f.guid } }).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "Every catalog family is already in this list.", Text); return; }
            using (FormStylePreview pick = new FormStylePreview("Add families", "Tick the catalog families to add. Each comes with its default size.",
                       new[] { "Family", "Catalog ID" }, rows, "Add", false, "Parts Lists - Add families"))
            {
                if (pick.ShowDialog(this) != DialogResult.OK || pick.Chosen.Count == 0) return;
                ObjectId last = ObjectId.Null;
                Change(tr => { foreach (string guid in pick.Chosen.Cast<string>()) last = PartsListTools.EnsureFamily(tr, listId, domain, guid, out _); });
                _status.Text = $"Added {pick.Chosen.Count} famil{(pick.Chosen.Count == 1 ? "y" : "ies")}.";
                Reload(null, last);
            }
        }

        private void AddCatalogSizes(ObjectId familyId)
        {
            List<Dictionary<PartContextType, double>> missing;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                PartFamily f = (PartFamily)tr.GetObject(familyId, OpenMode.ForRead);
                List<Dictionary<PartContextType, double>> listed = PartsListTools.ListSizes(tr, f).Select(s => s.values).ToList();
                missing = PartsListTools.CatalogSizes(f).Where(v => !listed.Any(x => PartsListTools.Same(v, x))).ToList();
            }
            if (missing.Count == 0) { MessageBox.Show(this, "The list already has every size the catalog lists for this family. Use New size… for other dimensions.", Text); return; }
            List<PreviewRow> rows = missing.Select(v => new PreviewRow { Tag = v, Checked = false, Cells = new[] { PartsListTools.Describe(v) } }).ToList();
            using (FormStylePreview pick = new FormStylePreview("Add catalog sizes", "Tick the sizes to add to this family.", new[] { "Size" }, rows, "Add", false, "Parts Lists - Add sizes"))
            {
                if (pick.ShowDialog(this) != DialogResult.OK || pick.Chosen.Count == 0) return;
                List<string> failed = new List<string>();
                Change(tr =>
                {
                    PartFamily f = (PartFamily)tr.GetObject(familyId, OpenMode.ForWrite);
                    foreach (Dictionary<PartContextType, double> v in pick.Chosen.Cast<Dictionary<PartContextType, double>>())
                        if (PartsListTools.AddSize(tr, f, v, out string error).IsNull) failed.Add($"{PartsListTools.Describe(v)}: {error}");
                });
                _status.Text = $"Added {pick.Chosen.Count - failed.Count} size(s).";
                if (failed.Count > 0) MessageBox.Show(this, string.Join("\n", failed), "Not added", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Reload(null, familyId, ObjectId.Null);
            }
        }

        private void NewSize(ObjectId listId, ObjectId familyId)
        {
            List<string> log = new List<string>();
            string error = null;
            ObjectId created = ObjectId.Null, createdFamily = ObjectId.Null;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                PartFamily f = (PartFamily)tr.GetObject(familyId, OpenMode.ForWrite);
                Dictionary<PartContextType, double> start = PartsListTools.ListSizes(tr, f).Select(s => s.values).FirstOrDefault() ?? new Dictionary<PartContextType, double>();
                using (FormNewPartSize dlg = new FormNewPartSize(f, start, NewSizeMethod.AnySizeFamily))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) { tr.Abort(); return; }
                    (createdFamily, created) = PartSizeCreator.Create(tr, listId, f, dlg.Values, dlg.Method, log, out error);
                }
                if (created.IsNull) tr.Abort(); else tr.Commit();
            }
            if (created.IsNull) { MessageBox.Show(this, "The size was not created: " + error + ".", "New size", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _status.Text = string.Join(" ", log);
            Reload(null, createdFamily, created);
        }

        private void RemoveFamily(ObjectId listId, ObjectId familyId, string name)
        {
            if (MessageBox.Show(this, $"Remove \"{name}\" from the list?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Change(tr => ((PartsList)tr.GetObject(listId, OpenMode.ForWrite)).RemovePartFamily(familyId));
            _status.Text = $"Removed \"{name}\".";
            Reload(null, listId);
        }

        private void RemoveSize(ObjectId familyId, ObjectId sizeId, string name)
        {
            Change(tr => ((PartFamily)tr.GetObject(familyId, OpenMode.ForWrite)).RemovePartSize(sizeId));
            _status.Text = $"Removed the size {name}.";
            Reload(null, familyId, ObjectId.Null);
        }

        private void SetUpMethodA()
        {
            if (MessageBox.Show(this, "Method A adds four C3D Pro families (box culvert, circular pipe, rectangular and cylindrical structure) whose sizes are free, " +
                                      "to the registered catalogs. Autodesk's own families are not changed. Civil 3D must be restarted once to list them.\n\nSet them up now?",
                                      Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            List<string> log = new List<string>();
            bool restart = NavisImporter.EnsureAnySizeFamilies(log);
            MessageBox.Show(this, string.Join("\n", log) + (restart ? "\n\nRestart Civil 3D once to use them." : "\n\nThey are ready to use."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================================
        // Small dialogs
        // =====================================================================================

        /// <summary>Asks for a new list's name and whether to copy <paramref name="copyOf"/>; null when cancelled.</summary>
        public static string AskNewList(IWin32Window owner, string copyOf, out bool copy, bool copyDefault = false)
        {
            copy = false;
            using (Form f = SmallDialog("New parts list", out TableLayoutPanel body, out Button ok))
            {
                TextBox name = new TextBox { Width = 320, Text = copyOf != null ? copyOf + " copy" : "New parts list" };
                CheckBox chk = ToolUi.Check(copyOf != null ? $"Copy the families and sizes of \"{copyOf}\"" : "Copy another list", copyDefault && copyOf != null);
                chk.Enabled = copyOf != null;
                ToolUi.Field(body, "Name", name);
                name.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                ToolUi.Wide(body, chk);
                if (f.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(name.Text)) return null;
                copy = chk.Checked;
                return name.Text.Trim();
            }
        }

        public static string AskText(IWin32Window owner, string title, string label, string value)
        {
            using (Form f = SmallDialog(title, out TableLayoutPanel body, out Button ok))
            {
                TextBox box = new TextBox { Width = 320, Text = value };
                ToolUi.Field(body, label, box);
                box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }

        internal static Form SmallDialog(string title, out TableLayoutPanel body, out Button ok)
        {
            Form f = new Form
            {
                Text = title, Font = StyleUi.Base, BackColor = StyleUi.Surface, AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Padding = new Padding(12)
            };
            body = ToolUi.Card(title);
            body.Dock = DockStyle.Top;
            ok = StyleUi.Primary("OK");
            ok.DialogResult = DialogResult.OK;
            Button cancel = StyleUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            f.Controls.Add(buttons);
            f.Controls.Add(body);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            // Sized from its content once shown (an auto-sizing form measures docked content too small and loses the buttons);
            // it can be made wider, never smaller than that.
            TableLayoutPanel card = body;
            f.Load += (s, e) =>
            {
                int width = Math.Max(f.LogicalToDeviceUnits(480), card.GetPreferredSize(Size.Empty).Width + f.Padding.Horizontal);
                int inner = width - f.Padding.Horizontal;
                int height = card.GetPreferredSize(new Size(inner, 0)).Height + buttons.GetPreferredSize(new Size(inner, 0)).Height + f.Padding.Vertical;
                f.ClientSize = new Size(width, height);
                f.MinimumSize = f.Size;
                f.MaximumSize = new Size(int.MaxValue, f.Height);
            };
            return f;
        }
    }
}
