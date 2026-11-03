using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Picks a part the way Civil 3D does (parts list, family, size), used wherever the tools need a pipe or structure part.
    /// <list type="bullet">
    /// <item>With a network, it opens on the network's own parts list. Choosing another list adds the part to the network's
    /// own list when applied (default), or switches the network to that list.</item>
    /// <item>Families and sizes the catalog offers but the list lacks are listed too (marked "+") and added when applied.</item>
    /// <item>"New size…" makes a size with any dimensions; "Parts lists…" opens the shared Parts Lists window.</item>
    /// </list>
    /// </summary>
    internal sealed class PartChooser : TableLayoutPanel
    {
        private const string NewListItem = "+ New parts list…";
        private const string CatalogSeparator = "── more in the catalog ──";

        private sealed class ListItem { public ObjectId Id; public string Name; public bool Own; public override string ToString() => Own ? Name + "  (network's list)" : Name; }
        private sealed class FamilyItem { public ObjectId Id; public string Guid, Name; public bool InList; public override string ToString() => InList ? Name : "+ " + Name; }
        private sealed class SizeItem { public ObjectId Id; public string Name; public Dictionary<PartContextType, double> Values; public override string ToString() => Id.IsNull ? "+ " + Name : Name; }

        private readonly DomainType _domain;
        private readonly ComboBox _cbList = ToolUi.Combo(), _cbFamily = ToolUi.Combo(), _cbSize = ToolUi.Combo();
        private readonly RadioButton _rbAdd = new RadioButton { Text = "Add the part to the network's own list", AutoSize = true, Checked = true };
        private readonly RadioButton _rbSwitch = new RadioButton { Text = "Switch the network to this list", AutoSize = true };
        private readonly FlowLayoutPanel _switchRow;
        private readonly Label _note = ToolUi.Note("");
        private ObjectId _networkId, _ownListId;
        private bool _filling;
        private string _wantFamily, _wantSize;
        /// <summary>A part picked from the drawing whose family is in no parts list: it is used by copying it.</summary>
        private PartPick _copyOf;
        /// <summary>The last part picked from the drawing, copied as a fallback while its family and size stay chosen.</summary>
        private ObjectId _pickedPart;
        private string _pickedFamily, _pickedSize;

        public event EventHandler Changed;

        public PartChooser(DomainType domain)
        {
            _domain = domain;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 2;
            Margin = new Padding(0);
            // Fills the card's width (the lists then grow up to a readable width) instead of shrinking to its narrowest.
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            MinimumSize = new Size(320, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _switchRow = ToolUi.Inline(_rbAdd, _rbSwitch);
            _switchRow.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;

            Row("Parts list", _cbList);
            Wide(_switchRow);
            Row("Family", _cbFamily);
            Row("Size", _cbSize);
            Button newSize = StyleUi.Secondary("New size…", (s, e) => NewSize());
            Button lists = StyleUi.Secondary("Parts lists…", (s, e) => OpenPartsLists());
            Button fromDrawing = StyleUi.Secondary("From drawing…", (s, e) => PickFromDrawing());
            Wide(ToolUi.Inline(fromDrawing, newSize, lists));
            Wide(_note);
            _switchRow.Visible = false;
            _note.Visible = false;
            ToolUi.Exclusive(_rbAdd, _rbSwitch);

            // Choosing in the lists ends a copy of a part picked from the drawing.
            _cbList.SelectedIndexChanged += (s, e) => { if (!_filling) { _copyOf = null; ListChosen(); } };
            _cbFamily.SelectedIndexChanged += (s, e) => { if (!_filling) { _copyOf = null; FamilyChosen(); } };
            _cbSize.SelectedIndexChanged += (s, e) => { if (!_filling) { _copyOf = null; SizeChosen(); } };
            _rbSwitch.CheckedChanged += (s, e) => { UpdateNote(); Changed?.Invoke(this, EventArgs.Empty); };
            // The note is wrapped by the page it sits on (ToolUi.FitNotes): sizing it from this control's own width would grow both forever.
            _note.MaximumSize = new Size(380, 0);
        }

        private void Row(string label, Control field)
        {
            int row = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 7, 10, 3) }, 0, row);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            field.MaximumSize = new Size(460, 0);
            Controls.Add(field, 1, row);
        }

        private void Wide(Control c)
        {
            int row = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(c, 0, row);
            SetColumnSpan(c, 2);
        }

        // =====================================================================================
        // Context and selection
        // =====================================================================================

        /// <summary>
        /// The network the part is for (null: no network yet, e.g. a new one; then the chosen list is used as it is).
        /// Keeps the current choice where the new list has it, else the family / size named.
        /// </summary>
        public void SetContext(ObjectId networkId, string listName = null, string familyName = null, string sizeName = null)
        {
            _networkId = networkId;
            _ownListId = ObjectId.Null;
            string keepList = listName ?? (_cbList.SelectedItem as ListItem)?.Name;
            _wantFamily = familyName ?? (_cbFamily.SelectedItem as FamilyItem)?.Name ?? _wantFamily;
            _wantSize = sizeName ?? (_cbSize.SelectedItem as SizeItem)?.Name ?? _wantSize;

            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            List<ListItem> items = new List<ListItem>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                if (!networkId.IsNull && tr.GetObject(networkId, OpenMode.ForRead) is Network net) _ownListId = net.PartsListId;
                foreach (ObjectId id in CivilApplication.ActiveDocument.Styles.PartsListSet)
                    if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl) items.Add(new ListItem { Id = id, Name = pl.Name, Own = id == _ownListId });
            }
            items = items.OrderByDescending(i => i.Own).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

            _filling = true;
            _cbList.Items.Clear();
            foreach (ListItem i in items) _cbList.Items.Add(i);
            _cbList.Items.Add(NewListItem);
            // A list named on purpose (just created, or remembered without a network) wins; otherwise the network's own list.
            ListItem start = (listName != null ? items.FirstOrDefault(i => i.Name == listName) : null)
                             ?? items.FirstOrDefault(i => i.Own) ?? items.FirstOrDefault(i => i.Name == keepList) ?? items.FirstOrDefault();
            _cbList.SelectedItem = start;
            _filling = false;
            ListChosen();
        }

        /// <summary>The chosen part, or null while nothing usable is chosen.</summary>
        public PartPick Pick
        {
            get
            {
                if (_copyOf != null && !_copyOf.TemplateId.IsErased)
                    return new PartPick
                    {
                        Domain = _domain, FamilyName = _copyOf.FamilyName, SizeName = _copyOf.SizeName, TemplateId = _copyOf.TemplateId,
                        Values = new Dictionary<PartContextType, double>(_copyOf.Values)
                    };
                if (!(_cbList.SelectedItem is ListItem list) || !(_cbFamily.SelectedItem is FamilyItem fam) || !(_cbSize.SelectedItem is SizeItem size) || size.Values == null) return null;
                return new PartPick
                {
                    Domain = _domain, PartsListId = list.Id, PartsListName = list.Name, FamilyGuid = fam.Guid, FamilyName = fam.Name, FamilyId = fam.Id,
                    SizeId = size.Id, SizeName = size.Name, Values = new Dictionary<PartContextType, double>(size.Values),
                    SwitchNetwork = _switchRow.Visible && _rbSwitch.Checked,
                    // The part picked from the drawing is still chosen: it can be copied if Civil 3D cannot build it.
                    TemplateId = !_pickedPart.IsNull && !_pickedPart.IsErased && fam.Name == _pickedFamily && size.Name == _pickedSize ? _pickedPart : ObjectId.Null
                };
            }
        }

        public string ListName => (_cbList.SelectedItem as ListItem)?.Name;
        public string FamilyName => (_cbFamily.SelectedItem as FamilyItem)?.Name;
        public string SizeName => (_cbSize.SelectedItem as SizeItem)?.Name;

        private void ListChosen()
        {
            if (Equals(_cbList.SelectedItem, NewListItem)) { NewList(); return; }
            ListItem list = _cbList.SelectedItem as ListItem;
            _switchRow.Visible = list != null && !_ownListId.IsNull && list.Id != _ownListId;
            FillFamilies(list);
        }

        private void FillFamilies(ListItem list)
        {
            List<FamilyItem> families = new List<FamilyItem>();
            if (list != null)
            {
                Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                    foreach (PartFamily f in PartsListTools.Families(tr, list.Id, _domain)) families.Add(new FamilyItem { Id = f.ObjectId, Guid = f.GUID, Name = f.Name, InList = true });
                HashSet<string> have = new HashSet<string>(families.Select(f => f.Guid), StringComparer.OrdinalIgnoreCase);
                List<FamilyItem> more = PartsListTools.CatalogFamilies(_domain).Where(c => !have.Contains(c.guid)).Select(c => new FamilyItem { Guid = c.guid, Name = c.description }).ToList();
                _filling = true;
                _cbFamily.Items.Clear();
                foreach (FamilyItem f in families) _cbFamily.Items.Add(f);
                if (more.Count > 0) { _cbFamily.Items.Add(CatalogSeparator); foreach (FamilyItem f in more) _cbFamily.Items.Add(f); }
                // Without a remembered family, a real part comes first (every list also has the "Null Structure" placeholder).
                FamilyItem pick = families.FirstOrDefault(f => f.Name == _wantFamily) ?? more.FirstOrDefault(f => f.Name == _wantFamily)
                                  ?? families.FirstOrDefault(f => f.Name.IndexOf("Null", StringComparison.OrdinalIgnoreCase) < 0) ?? families.FirstOrDefault();
                _cbFamily.SelectedItem = pick;
                _filling = false;
            }
            else { _filling = true; _cbFamily.Items.Clear(); _filling = false; }
            FamilyChosen();
        }

        private void FamilyChosen()
        {
            if (Equals(_cbFamily.SelectedItem, CatalogSeparator)) { _cbFamily.SelectedIndex = Math.Min(_cbFamily.Items.Count - 1, _cbFamily.SelectedIndex + 1); return; }
            FamilyItem fam = _cbFamily.SelectedItem as FamilyItem;
            if (fam != null) _wantFamily = fam.Name;
            List<SizeItem> inList = new List<SizeItem>(), more = new List<SizeItem>();
            if (fam != null && _cbList.SelectedItem is ListItem list) ReadSizes(list.Id, fam, inList, more);
            _filling = true;
            _cbSize.Items.Clear();
            foreach (SizeItem s in inList) _cbSize.Items.Add(s);
            if (more.Count > 0) { _cbSize.Items.Add(CatalogSeparator); foreach (SizeItem s in more) _cbSize.Items.Add(s); }
            _cbSize.SelectedItem = inList.FirstOrDefault(s => s.Name == _wantSize) ?? more.FirstOrDefault(s => s.Name == _wantSize) ?? inList.FirstOrDefault() ?? more.FirstOrDefault();
            _filling = false;
            SizeChosen();
        }

        /// <summary>The list's sizes of the family and the catalog's other sizes (a family not in the list is read through a discarded transaction).</summary>
        private void ReadSizes(ObjectId listId, FamilyItem fam, List<SizeItem> inList, List<SizeItem> more)
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            try
            {
                using (AcadApp.DocumentManager.MdiActiveDocument.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId famId = PartsListTools.EnsureFamily(tr, listId, _domain, fam.Guid, out bool added);
                    if (famId.IsNull) return;
                    PartFamily f = (PartFamily)tr.GetObject(famId, OpenMode.ForRead);
                    List<Dictionary<PartContextType, double>> listed = new List<Dictionary<PartContextType, double>>();
                    foreach ((ObjectId id, string name, Dictionary<PartContextType, double> v) in PartsListTools.ListSizes(tr, f))
                    {
                        // A family just read from the catalog brings its default size; it is offered like any catalog size.
                        if (added) more.Add(new SizeItem { Id = ObjectId.Null, Name = PartsListTools.Describe(v), Values = v });
                        else inList.Add(new SizeItem { Id = id, Name = name, Values = v });
                        listed.Add(v);
                    }
                    foreach (Dictionary<PartContextType, double> v in PartsListTools.CatalogSizes(f))
                        if (!listed.Any(x => PartsListTools.Same(v, x))) more.Add(new SizeItem { Id = ObjectId.Null, Name = PartsListTools.Describe(v), Values = v });
                    // Nothing is kept: a family or size is only added to the list when the part is applied.
                    tr.Abort();
                }
            }
            catch (Exception ex) { _note.Text = "Sizes could not be read: " + ex.Message; _note.Visible = true; }
        }

        private void SizeChosen()
        {
            if (Equals(_cbSize.SelectedItem, CatalogSeparator)) { _cbSize.SelectedIndex = Math.Min(_cbSize.Items.Count - 1, _cbSize.SelectedIndex + 1); return; }
            if (_cbSize.SelectedItem is SizeItem s) _wantSize = s.Name;
            UpdateNote();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateNote()
        {
            if (_copyOf != null) return;
            List<string> notes = new List<string>();
            if (_cbFamily.SelectedItem is FamilyItem f && !f.InList) notes.Add($"\"{f.Name}\" is added to the parts list when applied.");
            if (_cbSize.SelectedItem is SizeItem s && s.Id.IsNull) notes.Add($"The size {s.Name} is added to the family when applied.");
            if (_switchRow.Visible)
                notes.Add(_rbSwitch.Checked ? "The network is switched to this parts list when applied." : "The part is copied into the network's own parts list when applied.");
            _note.Text = string.Join(" ", notes);
            _note.Visible = notes.Count > 0;
        }

        // =====================================================================================
        // Creating lists and sizes
        // =====================================================================================

        private void NewList()
        {
            ListItem current = _cbList.Items.OfType<ListItem>().FirstOrDefault(i => i.Own) ?? _cbList.Items.OfType<ListItem>().FirstOrDefault();
            string name = FormPartsLists.AskNewList(FindForm(), current?.Name, out bool copy);
            if (name == null) { SetContext(_networkId); return; }
            Autodesk.AutoCAD.ApplicationServices.Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            List<string> log = new List<string>();
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                PartsListTools.CreateList(tr, CivilApplication.ActiveDocument, PartsListTools.UniqueListName(tr, CivilApplication.ActiveDocument, name), copy && current != null ? current.Id : ObjectId.Null, log);
                tr.Commit();
            }
            if (log.Count > 0) MessageBox.Show(FindForm(), "The list was created; some sizes could not be copied:\n\n" + string.Join("\n", log.Take(15)), "Parts list", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetContext(_networkId, name);
        }

        private void NewSize()
        {
            if (!(_cbList.SelectedItem is ListItem list) || !(_cbFamily.SelectedItem is FamilyItem fam)) return;
            Autodesk.AutoCAD.ApplicationServices.Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            List<string> log = new List<string>();
            string error = null;
            string created = null, createdFamily = null;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId famId = PartsListTools.EnsureFamily(tr, list.Id, _domain, fam.Guid, out _);
                PartFamily f = (PartFamily)tr.GetObject(famId, OpenMode.ForWrite);
                Dictionary<PartContextType, double> start = (_cbSize.SelectedItem as SizeItem)?.Values ?? new Dictionary<PartContextType, double>();
                using (FormNewPartSize dlg = new FormNewPartSize(f, start, NewSizeMethod.AnySizeFamily))
                {
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) { tr.Abort(); return; }
                    (ObjectId newFam, ObjectId newSize) = PartSizeCreator.Create(tr, list.Id, f, dlg.Values, dlg.Method, log, out error);
                    if (newSize.IsNull) { tr.Abort(); }
                    else
                    {
                        created = ((PartSize)tr.GetObject(newSize, OpenMode.ForRead)).Name;
                        createdFamily = ((PartFamily)tr.GetObject(newFam, OpenMode.ForRead)).Name;
                        tr.Commit();
                    }
                }
            }
            if (created == null) { MessageBox.Show(FindForm(), "The size was not created: " + error + ".", "New size", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (log.Count > 0) MessageBox.Show(FindForm(), string.Join("\n", log), "New size", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetContext(_networkId, list.Name, createdFamily, created);
        }

        /// <summary>
        /// Uses the part of a pipe or structure picked in the drawing, as it is: its parts list, family and size are chosen.
        /// Needs no catalog, so parts whose catalog is missing (or another office's) can still be used.
        /// </summary>
        private void PickFromDrawing()
        {
            Form form = FindForm();
            Autodesk.AutoCAD.ApplicationServices.Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            string kind = _domain == DomainType.Pipe ? "pipe" : "structure";
            PromptEntityResult picked;
            form?.Hide();
            try
            {
                PromptEntityOptions o = new PromptEntityOptions($"\nPick a {kind} whose part to use: ");
                o.SetRejectMessage($"\nPick a gravity {kind}.");
                o.AddAllowedClass(_domain == DomainType.Pipe ? typeof(Pipe) : typeof(Structure), false);
                picked = doc.Editor.GetEntity(o);
            }
            finally { form?.Show(); }
            if (picked.Status != PromptStatus.OK) return;

            string listName = null, familyName = null, sizeName = null, partName = null, drawnSize = "";
            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                Part part = (Part)tr.GetObject(picked.ObjectId, OpenMode.ForRead);
                partName = part.Name;
                try { drawnSize = part.PartSizeName; } catch (Exception) { }
                ObjectId famId = ObjectId.Null, sizeId = ObjectId.Null;
                try { famId = part.PartFamilyId; sizeId = part.GetPartSizeId(); } catch (Exception) { }
                if (PartCopy.IsFamily(tr, famId))
                {
                    familyName = ((PartFamily)tr.GetObject(famId, OpenMode.ForRead)).Name;
                    if (!sizeId.IsNull && tr.GetObject(sizeId, OpenMode.ForRead) is PartSize size) sizeName = size.Name;
                    foreach (ObjectId id in CivilApplication.ActiveDocument.Styles.PartsListSet)
                        if (tr.GetObject(id, OpenMode.ForRead) is PartsList pl && pl.GetPartFamilyIdsByDomain(_domain).Cast<ObjectId>().Contains(famId)) { listName = pl.Name; break; }
                }
                // Its inside size and wall, read from the part itself (used for cover and levels).
                try
                {
                    if (part is Pipe p)
                    {
                        values[PartContextType.PipeInnerWidth] = p.InnerDiameterOrWidth;
                        values[PartContextType.PipeInnerHeight] = p.InnerHeight;
                        try { values[PartContextType.WallThickness] = Math.Max(0, p.WallThickness); } catch (Exception) { }
                    }
                    else if (part is Structure st)
                    {
                        values[PartContextType.StructInnerWidth] = st.InnerDiameterOrWidth;
                        values[PartContextType.StructInnerLength] = st.InnerLength;
                    }
                }
                catch (Exception) { }
            }
            _pickedPart = picked.ObjectId;
            if (listName != null && sizeName != null)
            {
                _pickedFamily = familyName;
                _pickedSize = sizeName;
                _copyOf = null;
                SetContext(_networkId, listName, familyName, sizeName);
                return;
            }
            // Its family is not in the drawing (or no list holds it): the part is used by copying it.
            _copyOf = new PartPick { Domain = _domain, FamilyName = familyName ?? "(family not in the drawing)", SizeName = string.IsNullOrEmpty(drawnSize) ? partName : drawnSize, TemplateId = picked.ObjectId, Values = values };
            _note.Text = $"Copies {kind} {partName} ({_copyOf.SizeName}) as it is in the drawing: its family is in no parts list, so new {kind}s are made " +
                         "as copies of it (no catalog needed). Choose in the lists above to use a parts list part instead.";
            _note.Visible = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OpenPartsLists()
        {
            string list = ListName, family = FamilyName, size = SizeName;
            using (FormPartsLists form = new FormPartsLists(list)) form.ShowDialog(FindForm());
            SetContext(_networkId, list, family, size);
        }
    }
}
