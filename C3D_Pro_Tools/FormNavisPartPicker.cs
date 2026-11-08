using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Lets the user pick the Civil 3D part for import rows themselves: any parts list in the drawing, any family of the
    /// right domain, any size. Sizes are listed with their dimensions and how far they are from the Navisworks size.
    /// </summary>
    internal sealed class FormNavisPartPicker : Form
    {
        private readonly Document _doc;
        private readonly List<ImportCandidate> _rows;
        private readonly DomainType _domain;
        private readonly ComboBox _cbList = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly ComboBox _cbFamily = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        private readonly CheckBox _chkAllShapes = new CheckBox { Text = "Show families of every shape", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        private readonly ListView _sizes = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
        private readonly Label _hint = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(3, 9, 3, 3) };
        private readonly Button _btnUse;

        /// <summary>The family and size picked, or nulls when the user went back to the automatic match.</summary>
        public ObjectId FamilyId { get; private set; }
        public ObjectId SizeId { get; private set; }
        public bool ClearOverride { get; private set; }

        public FormNavisPartPicker(Document doc, List<ImportCandidate> rows, string preferredList, (ObjectId family, ObjectId size)? current)
        {
            _doc = doc;
            _rows = rows;
            _domain = rows[0].Action == ImportKind.Structure ? DomainType.Structure : DomainType.Pipe;
            Text = "Navisworks Import - Choose the Civil 3D part";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            ImportCandidate first = rows[0];
            Panel head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 12, 16, 10), BackColor = StyleUi.Panel };
            Label sub = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, ForeColor = StyleUi.Muted, Padding = new Padding(0, 4, 0, 0),
                Text = (rows.Count == 1 ? first.Name : $"{rows.Count} rows: {string.Join(", ", rows.Take(3).Select(r => r.Name))}{(rows.Count > 3 ? "…" : "")}")
                       + $"   ·   Navisworks: {first.SizeText}" + (first.Action == ImportKind.Box ? "   (width × height: width is across, height is vertical)" : "")
            };
            head.Controls.Add(sub);
            head.Controls.Add(new Label { Dock = DockStyle.Top, AutoSize = true, Font = StyleUi.Heading, Text = _domain == DomainType.Pipe ? "Choose the pipe part" : "Choose the structure part" });
            head.Resize += (s, e) => sub.MaximumSize = new Size(Math.Max(200, head.ClientSize.Width - 32), 0);

            TableLayoutPanel pick = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 8, 12, 4) };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pick.Controls.Add(StyleUi.Caption("Parts list"), 0, 0);
            pick.Controls.Add(_cbList, 1, 0);
            pick.Controls.Add(StyleUi.Caption("Family"), 0, 1);
            pick.Controls.Add(_cbFamily, 1, 1);
            pick.Controls.Add(_chkAllShapes, 2, 1);

            _sizes.Columns.Add("Size", 300);
            _sizes.Columns.Add(_domain == DomainType.Pipe ? "Inner (W × H)" : "Inner", 130);
            _sizes.Columns.Add("Wall", 70, HorizontalAlignment.Right);
            _sizes.Columns.Add(_domain == DomainType.Pipe ? "Outer (W × H)" : "Outer", 130);
            _sizes.Columns.Add("Error", 80, HorizontalAlignment.Right);
            _sizes.DoubleClick += (s, e) => Use();
            // The size name takes the spare width; the numbers keep readable widths.
            // Sized against the width with a vertical scrollbar, so it never adds a horizontal one (which would resize and repeat).
            _sizes.Resize += (s, e) =>
            {
                int w = _sizes.Width - SystemInformation.VerticalScrollBarWidth - 6;
                if (w <= 0) return;
                double[] share = { 0.40, 0.17, 0.09, 0.17, 0.17 };
                for (int i = 0; i < share.Length; i++)
                {
                    int cw = Math.Max(50, (int)(w * share[i]));
                    if (_sizes.Columns[i].Width != cw) _sizes.Columns[i].Width = cw;
                }
            };
            _sizes.SelectedIndexChanged += (s, e) => _btnUse.Enabled = _sizes.SelectedItems.Count > 0;
            Panel listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0) };
            listHost.Controls.Add(_sizes);

            _btnUse = StyleUi.Primary("Use this size", (s, e) => Use());
            _btnUse.Enabled = false;
            Button auto = StyleUi.Secondary("Back to automatic match", (s, e) => { ClearOverride = true; DialogResult = DialogResult.OK; });
            auto.Visible = current.HasValue;
            Button cancel = StyleUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 10) };
            buttons.Controls.AddRange(new Control[] { cancel, _btnUse, auto, _hint });
            CancelButton = cancel;

            Controls.Add(listHost);
            Controls.Add(pick);
            Controls.Add(head);
            Controls.Add(buttons);

            _cbList.SelectedIndexChanged += (s, e) => FillFamilies();
            _cbFamily.SelectedIndexChanged += (s, e) => FillSizes();
            _chkAllShapes.CheckedChanged += (s, e) => FillFamilies();
            FillLists(preferredList, current);
        }

        private sealed class Item
        {
            public readonly string Text;
            public readonly ObjectId Id;
            public Item(string text, ObjectId id) { Text = text; Id = id; }
            public override string ToString() => Text;
        }

        private ObjectId _preferredFamily, _preferredSize;

        private void FillLists(string preferredList, (ObjectId family, ObjectId size)? current)
        {
            _preferredFamily = current?.family ?? ObjectId.Null;
            _preferredSize = current?.size ?? ObjectId.Null;
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in CivilDocument.GetCivilDocument(_doc.Database).Styles.PartsListSet)
                {
                    if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is PartsList pl)) continue;
                    _cbList.Items.Add(new Item(pl.Name, id));
                    // Open on the list holding the current pick, else the preferred (Navis Import) list.
                    if (!_preferredFamily.IsNull && pl.GetPartFamilyIdsByDomain(_domain).Cast<ObjectId>().Contains(_preferredFamily)) _cbList.SelectedIndex = _cbList.Items.Count - 1;
                }
                tr.Commit();
            }
            if (_cbList.Items.Count == 0)
            {
                _hint.Text = "This drawing has no parts lists.";
                return;
            }
            if (_cbList.SelectedIndex < 0)
            {
                int i = _cbList.Items.Cast<Item>().ToList().FindIndex(x => x.Text.Equals(preferredList, StringComparison.OrdinalIgnoreCase));
                _cbList.SelectedIndex = i >= 0 ? i : 0;
            }
        }

        private bool ShapeFits(PartFamily f)
        {
            ImportCandidate c = _rows[0];
            if (_domain == DomainType.Pipe) return f.SweptShape == (c.Action == ImportKind.Box ? SweptShapeType.Rectangular : SweptShapeType.Circular);
            return f.BoundingShape == (c.StructCircular ? BoundingShapeType.Cylinder : BoundingShapeType.Box);
        }

        private void FillFamilies()
        {
            _cbFamily.Items.Clear();
            if (!(_cbList.SelectedItem is Item list)) return;
            int select = -1;
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                PartsList pl = (PartsList)tr.GetObject(list.Id, OpenMode.ForRead);
                List<PartFamily> families = NavisPartMatcher.FamilyIds(pl, _domain).Select(id => tr.GetObject(id, OpenMode.ForRead) as PartFamily).Where(f => f != null).ToList();
                foreach (PartFamily f in families.OrderBy(f => ShapeFits(f) ? 0 : 1).ThenBy(f => f.Name))
                {
                    bool fits = ShapeFits(f);
                    if (!fits && !_chkAllShapes.Checked) continue;
                    _cbFamily.Items.Add(new Item(f.Name + (fits ? "" : "   (other shape)") + $"   ·   {f.PartSizeCount} size(s)", f.ObjectId));
                    if (f.ObjectId == _preferredFamily) select = _cbFamily.Items.Count - 1;
                }
                tr.Commit();
            }
            if (_cbFamily.Items.Count == 0)
            {
                _sizes.Items.Clear();
                _hint.Text = _chkAllShapes.Checked ? "This parts list has no families of this kind." : "No family of this shape here; tick \"Show families of every shape\" or pick another list.";
                return;
            }
            _cbFamily.SelectedIndex = select >= 0 ? select : 0;
        }

        private void FillSizes()
        {
            _sizes.BeginUpdate();
            _sizes.Items.Clear();
            if (_cbFamily.SelectedItem is Item famItem)
            {
                using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    PartFamily fam = (PartFamily)tr.GetObject(famItem.Id, OpenMode.ForRead);
                    List<(ListViewItem item, double error)> rows = new List<(ListViewItem, double)>();
                    foreach (ObjectId sizeId in NavisPartMatcher.SizeIds(fam))
                    {
                        if (!(tr.GetObject(sizeId, OpenMode.ForRead) is PartSize size)) continue;
                        double error;
                        (double a, double b, double wall) dims;
                        try
                        {
                            dims = NavisPartMatcher.ReadSize(fam, size);
                            error = _rows.Max(r => NavisPartMatcher.Evaluate(fam, size, r).Deviation);
                        }
                        catch (Exception) { continue; }
                        bool round = Math.Abs(dims.a - dims.b) < 1e-6 && (_domain == DomainType.Structure ? fam.BoundingShape == BoundingShapeType.Cylinder : fam.SweptShape == SweptShapeType.Circular);
                        string inner = round ? "Ø " + ImportCandidate.Mm(dims.a) : $"{ImportCandidate.Mm(dims.a)} × {ImportCandidate.Mm(dims.b)}";
                        string outer = round ? "Ø " + ImportCandidate.Mm(dims.a + 2 * dims.wall) : $"{ImportCandidate.Mm(dims.a + 2 * dims.wall)} × {ImportCandidate.Mm(dims.b + 2 * dims.wall)}";
                        ListViewItem item = new ListViewItem(new[] { size.Name, inner, ImportCandidate.Mm(dims.wall), outer, error >= 1e6 ? "?" : $"{error * 1000:0} mm" }) { Tag = size.ObjectId };
                        if (error <= NavisInterpreter.HighTolerance) item.ForeColor = Color.FromArgb(21, 128, 61);
                        else if (error > NavisInterpreter.MediumTolerance) item.ForeColor = StyleUi.Muted;
                        rows.Add((item, error));
                    }
                    foreach ((ListViewItem item, double _) in rows.OrderBy(r => r.error)) _sizes.Items.Add(item);
                    tr.Commit();
                }
            }
            _sizes.EndUpdate();
            foreach (ListViewItem item in _sizes.Items)
                if ((ObjectId)item.Tag == _preferredSize) { item.Selected = true; item.EnsureVisible(); break; }
            if (_sizes.SelectedItems.Count == 0 && _sizes.Items.Count > 0) _sizes.Items[0].Selected = true;
            _hint.Text = _sizes.Items.Count == 0
                ? "This family has no sizes in the list. Add sizes in Civil 3D's Parts List editor, then open this again."
                : "Sorted by error. Missing a size? Add it in Civil 3D's Parts List editor, then open this again.";
        }

        private void Use()
        {
            if (_sizes.SelectedItems.Count == 0 || !(_cbFamily.SelectedItem is Item fam)) return;
            FamilyId = fam.Id;
            SizeId = (ObjectId)_sizes.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
        }
    }
}
