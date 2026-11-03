using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Network Editor, Duct bank conduits: a grid of conduit pipes inside each chosen duct bank (encasement) pipe, from a saved
    /// layout. Unlike parallel copies every grid position gets a conduit, the centre one included.
    /// </summary>
    internal sealed partial class FormNetworkEditor
    {
        private readonly ComboBox _cbConLayout = ToolUi.Combo(260);
        private readonly NumericUpDown _numConCols = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 5, Width = 56 };
        private readonly NumericUpDown _numConRows = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 3, Width = 56 };
        private readonly TextBox _txtConSpH = ToolUi.Number(0.3, 60), _txtConSpV = ToolUi.Number(0.5, 60);
        private readonly TextBox _txtConBottom = ToolUi.Number(0.25, 60), _txtConShift = ToolUi.Number(0, 60);
        private readonly RadioButton _rbConCentred = new RadioButton { Text = "Rows centred on the encasement's centreline", AutoSize = true };
        private readonly RadioButton _rbConBottom = new RadioButton { Text = "Lowest row's centre above the encasement's bottom:", AutoSize = true };
        private readonly PartChooser _conPart = new PartChooser(DomainType.Pipe);
        private readonly RadioButton _rbConSameNet = new RadioButton { Text = "Each encasement's own network", AutoSize = true }, _rbConNewNet = new RadioButton { Text = "A new network:", AutoSize = true };
        private readonly TextBox _txtConNet = new TextBox { Width = 220 };
        private readonly Panel _conSection = new DoubleBufferedPanel { Width = 420, Height = 240, BackColor = Color.White, Margin = new Padding(0, 4, 0, 4) };
        private readonly Label _lblConCheck = ToolUi.Note("");
        private bool _conLoading;

        /// <summary>The first chosen gravity pipe, drawn as the encasement in the section (outside width and height, m).</summary>
        private string _encName;
        private double _encWidth, _encHeight;

        private sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

        private Control BuildConduits()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel card = ToolUi.Card("Conduit layout",
                "Choose the duct bank (encasement) pipes at the top. Each one gets a conduit at every grid position, the centre included, " +
                "running its full length at its slope. Spacings are centre to centre; the grid is centred across the encasement.");
            Button save = StyleUi.Secondary("Save as…", (s, e) => SaveConduitLayout());
            Button delete = StyleUi.Secondary("Delete", (s, e) => DeleteConduitLayout());
            ToolUi.Field(card, "Layout", ToolUi.Inline(_cbConLayout, save, delete));
            ToolUi.Field(card, "Grid", ToolUi.Inline(_numConCols, Muted("columns ×"), _numConRows, Muted("rows")));
            ToolUi.Field(card, "Spacing", ToolUi.Inline(_txtConSpH, Muted("m across ×"), _txtConSpV, Muted("m up (centre to centre)")));
            ToolUi.Field(card, "Side shift", ToolUi.WithUnit(_txtConShift, "m (+ = right of the encasement's direction)"));
            ToolUi.Wide(card, _rbConCentred);
            ToolUi.Wide(card, ToolUi.Inline(_rbConBottom, _txtConBottom, Muted("m")));
            ToolUi.Add(stack, card);

            TableLayoutPanel section = ToolUi.Card("Section", "Looking along the first chosen encasement. Red conduits cross its outside edge.");
            ToolUi.Wide(section, _conSection);
            ToolUi.Wide(section, _lblConCheck);
            ToolUi.Add(stack, section);

            TableLayoutPanel part = ToolUi.Card("Conduit part", "\"From drawing…\" copies an existing conduit when the part is not in the catalog.");
            ToolUi.Wide(part, _conPart);
            ToolUi.Add(stack, part);

            TableLayoutPanel net = ToolUi.Card("Network", "A conduit part with no family (copied from the drawing) cannot change network in Civil 3D: " +
                "its conduits stay in the network of the conduit you picked.");
            FlowLayoutPanel radios = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            radios.Controls.Add(_rbConSameNet);
            radios.Controls.Add(ToolUi.Inline(_rbConNewNet, _txtConNet));
            ToolUi.Wide(net, radios);
            ToolUi.Add(stack, net);

            ToolUi.Exclusive(_rbConCentred, _rbConBottom);
            ToolUi.Exclusive(_rbConSameNet, _rbConNewNet);
            _conSection.Paint += PaintConduitSection;
            _cbConLayout.SelectedIndexChanged += (s, e) =>
            {
                ConduitLayout l = _s.ConduitLayouts.FirstOrDefault(x => x.Name == _cbConLayout.SelectedItem as string);
                if (l != null) ShowConduitLayout(l);
            };
            EventHandler changed = (s, e) => { if (!_conLoading) { _txtConBottom.Enabled = _rbConBottom.Checked; _conSection.Invalidate(); UpdateConduitCheck(); } };
            foreach (Control c in new Control[] { _numConCols, _numConRows, _txtConSpH, _txtConSpV, _txtConBottom, _txtConShift })
                c.TextChanged += changed;
            _numConCols.ValueChanged += changed;
            _numConRows.ValueChanged += changed;
            _rbConBottom.CheckedChanged += changed;
            _conPart.Changed += changed;
            _rbConNewNet.CheckedChanged += (s, e) => { _txtConNet.Enabled = _rbConNewNet.Checked; _conPart.SetContext(_rbConNewNet.Checked ? ObjectId.Null : _partNetwork); };
            return host;
        }

        private static Label Muted(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) };

        private void ApplyConduitSettings()
        {
            FillConduitLayouts(_s.ConduitLayoutName);
            if (_cbConLayout.SelectedIndex < 0 && _cbConLayout.Items.Count > 0) _cbConLayout.SelectedIndex = 0;
            _rbConSameNet.Checked = _s.ConduitSameNetwork;
            _rbConNewNet.Checked = !_s.ConduitSameNetwork;
            _txtConNet.Text = _s.ConduitNetworkName;
            _txtConNet.Enabled = !_s.ConduitSameNetwork;
            _conPart.SetContext(ObjectId.Null, _s.ConduitPartsList, _s.ConduitFamily, _s.ConduitSize);
        }

        private void SaveConduitSettings()
        {
            _s.ConduitLayoutName = _cbConLayout.SelectedItem as string;
            _s.ConduitSameNetwork = _rbConSameNet.Checked;
            _s.ConduitNetworkName = _txtConNet.Text;
            _s.ConduitPartsList = _conPart.ListName;
            _s.ConduitFamily = _conPart.FamilyName;
            _s.ConduitSize = _conPart.SizeName;
        }

        private void FillConduitLayouts(string select = null)
        {
            _cbConLayout.Items.Clear();
            foreach (ConduitLayout l in _s.ConduitLayouts) _cbConLayout.Items.Add(l.Name);
            ToolUi.Select(_cbConLayout, select);
        }

        private void ShowConduitLayout(ConduitLayout l)
        {
            _conLoading = true;
            _numConCols.Value = Math.Max(_numConCols.Minimum, Math.Min(_numConCols.Maximum, l.Columns));
            _numConRows.Value = Math.Max(_numConRows.Minimum, Math.Min(_numConRows.Maximum, l.Rows));
            _txtConSpH.Text = ToolUi.Format(l.HorizontalSpacing);
            _txtConSpV.Text = ToolUi.Format(l.VerticalSpacing);
            _txtConBottom.Text = ToolUi.Format(l.BottomToFirstRow);
            _txtConShift.Text = ToolUi.Format(l.SideShift);
            _rbConBottom.Checked = l.FromBottom;
            _rbConCentred.Checked = !l.FromBottom;
            _txtConBottom.Enabled = l.FromBottom;
            _conLoading = false;
            _conSection.Invalidate();
            UpdateConduitCheck();
        }

        /// <summary>The layout as set on the page; null (with <paramref name="error"/>) when a value is not valid.</summary>
        private ConduitLayout CurrentConduitLayout(out string error)
        {
            error = null;
            ConduitLayout l = new ConduitLayout { Columns = (int)_numConCols.Value, Rows = (int)_numConRows.Value, FromBottom = _rbConBottom.Checked };
            if (!ToolUi.TryNumber(_txtConSpH, out double h) || (l.Columns > 1 && h <= 0)) { error = "Enter a positive spacing across."; return null; }
            if (!ToolUi.TryNumber(_txtConSpV, out double v) || (l.Rows > 1 && v <= 0)) { error = "Enter a positive spacing up."; return null; }
            if (!ToolUi.TryNumber(_txtConShift, out double shift)) { error = "Enter the side shift as a number (0 for none)."; return null; }
            double bottom = 0;
            if (l.FromBottom && (!ToolUi.TryNumber(_txtConBottom, out bottom) || bottom <= 0)) { error = "Enter the height of the lowest row above the bottom."; return null; }
            l.HorizontalSpacing = h; l.VerticalSpacing = v; l.SideShift = shift; l.BottomToFirstRow = bottom;
            return l;
        }

        private void SaveConduitLayout()
        {
            ConduitLayout l = CurrentConduitLayout(out string error);
            if (l == null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string name = FormPartsLists.AskText(this, "Save conduit layout", "Name of the layout:", _cbConLayout.SelectedItem as string ?? $"Duct bank {l.Columns} × {l.Rows}");
            if (string.IsNullOrWhiteSpace(name)) return;
            l.Name = name.Trim();
            int i = _s.ConduitLayouts.FindIndex(x => x.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (MessageBox.Show(this, $"Replace the layout \"{_s.ConduitLayouts[i].Name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _s.ConduitLayouts[i] = l;
            }
            else _s.ConduitLayouts.Add(l);
            _s.Save();
            FillConduitLayouts(l.Name);
        }

        private void DeleteConduitLayout()
        {
            if (!(_cbConLayout.SelectedItem is string name)) return;
            if (MessageBox.Show(this, $"Delete the conduit layout \"{name}\"?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _s.ConduitLayouts.RemoveAll(x => x.Name == name);
            if (_s.ConduitLayouts.Count == 0) _s.ConduitLayouts = ConduitLayout.Defaults();
            _s.Save();
            FillConduitLayouts();
            if (_cbConLayout.Items.Count > 0) _cbConLayout.SelectedIndex = 0;
        }

        /// <summary>Reads the first chosen gravity pipe's outside size for the section (called whenever the choice may have changed).</summary>
        private void RefreshConduitSection()
        {
            _encName = null;
            _encWidth = _encHeight = 0;
            Database db = AcadApp.DocumentManager.MdiActiveDocument?.Database;
            if (db == null) return;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in _pipes)
                {
                    if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                    _encName = p.Name;
                    _encWidth = OuterWidthOf(p);
                    _encHeight = CoverRoute.OuterHeightOf(p);
                    break;
                }
            _conSection.Invalidate();
            UpdateConduitCheck();
        }

        /// <summary>The pipe's outermost width; duct banks drawn with no wall report 0, so it is rebuilt from the inside and the wall.</summary>
        private static double OuterWidthOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterDiameterOrWidth; } catch (Exception) { }
            try { inner = pipe.InnerDiameterOrWidth; } catch (Exception) { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch (Exception) { }
            return Math.Max(outer, inner + 2 * wall);
        }

        /// <summary>The chosen conduit part's outside diameter, or 0 when no part is chosen.</summary>
        private double ConduitDiameter()
        {
            PartPick pick = _conPart.Pick;
            if (pick == null) return 0;
            return pick.InnerHeight + 2 * pick.Get(PartContextType.WallThickness);
        }

        /// <summary>Conduit centres in the section, from the encasement's centreline (x right, y up).</summary>
        private static List<(double x, double y)> ConduitCentres(ConduitLayout l, double encHeight)
        {
            List<(double, double)> list = new List<(double, double)>();
            foreach (double v in l.VerticalOffsets())
                foreach (double h in l.HorizontalOffsets())
                    list.Add((h, l.FromBottom ? v - encHeight / 2.0 : v));
            return list;
        }

        /// <summary>Conduits whose outside crosses the encasement's outside edge.</summary>
        private int ConduitsOutside(ConduitLayout l, double diameter)
        {
            if (_encWidth <= 0 || _encHeight <= 0) return 0;
            double r = diameter / 2.0;
            return ConduitCentres(l, _encHeight).Count(c => Math.Abs(c.x) + r > _encWidth / 2.0 + 1e-6 || Math.Abs(c.y) + r > _encHeight / 2.0 + 1e-6);
        }

        private void UpdateConduitCheck()
        {
            ConduitLayout l = CurrentConduitLayout(out string error);
            double d = ConduitDiameter();
            if (l == null) _lblConCheck.Text = error;
            else if (_encName == null) _lblConCheck.Text = $"{l.Columns * l.Rows} conduits per encasement. Choose the encasement pipes at the top to see them in the section.";
            else
            {
                int outside = ConduitsOutside(l, d);
                _lblConCheck.Text = $"{l.Columns * l.Rows} conduits in <{_encName}> ({ToolUi.Format(_encWidth)} × {ToolUi.Format(_encHeight)} m outside)" +
                                    (d > 0 ? $", each {ToolUi.Format(Math.Round(d * 1000))} mm outside." : ". Choose the conduit part to check that they fit.") +
                                    (outside > 0 ? $" {outside} cross the encasement's edge." : "");
                _lblConCheck.ForeColor = outside > 0 ? Color.Firebrick : StyleUi.Muted;
            }
        }

        private void PaintConduitSection(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ConduitLayout l = CurrentConduitLayout(out _);
            if (l == null) return;
            double d = ConduitDiameter();
            double drawD = d > 0 ? d : 0.6 * Math.Max(0.05, Math.Min(l.Columns > 1 ? l.HorizontalSpacing : 1, l.Rows > 1 ? l.VerticalSpacing : 1));
            List<(double x, double y)> centres = ConduitCentres(l, _encHeight);

            // Extents in metres: the encasement (when known) and every conduit.
            double minX = -_encWidth / 2, maxX = _encWidth / 2, minY = -_encHeight / 2, maxY = _encHeight / 2;
            foreach ((double x, double y) in centres)
            {
                minX = Math.Min(minX, x - drawD / 2); maxX = Math.Max(maxX, x + drawD / 2);
                minY = Math.Min(minY, y - drawD / 2); maxY = Math.Max(maxY, y + drawD / 2);
            }
            if (maxX - minX < 1e-6 || maxY - minY < 1e-6) return;
            const int pad = 18;
            float scale = (float)Math.Min((_conSection.Width - 2 * pad) / (maxX - minX), (_conSection.Height - 2 * pad) / (maxY - minY));
            float cx = _conSection.Width / 2f - (float)((minX + maxX) / 2 * scale), cy = _conSection.Height / 2f + (float)((minY + maxY) / 2 * scale);
            PointF P(double x, double y) => new PointF(cx + (float)(x * scale), cy - (float)(y * scale));

            if (_encWidth > 0 && _encHeight > 0)
            {
                PointF tl = P(-_encWidth / 2, _encHeight / 2);
                using (Brush fill = new HatchBrush(System.Drawing.Drawing2D.HatchStyle.DottedGrid, Color.Gainsboro, Color.White))
                    g.FillRectangle(fill, tl.X, tl.Y, (float)(_encWidth * scale), (float)(_encHeight * scale));
                using (Pen pen = new Pen(Color.DimGray, 1.5f)) g.DrawRectangle(pen, tl.X, tl.Y, (float)(_encWidth * scale), (float)(_encHeight * scale));
                using (Pen axis = new Pen(Color.LightGray) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(axis, P(minX, 0), P(maxX, 0));
                    g.DrawLine(axis, P(0, minY), P(0, maxY));
                }
            }
            double r = drawD / 2;
            foreach ((double x, double y) in centres)
            {
                // With no part chosen yet only the centre is checked, as in the count below the section.
                double rr = d > 0 ? r : 0;
                bool outside = _encWidth > 0 && (Math.Abs(x) + rr > _encWidth / 2 + 1e-6 || Math.Abs(y) + rr > _encHeight / 2 + 1e-6);
                PointF c = P(x - r, y + r);
                float size = (float)(drawD * scale);
                using (Brush fill = new SolidBrush(outside ? Color.MistyRose : Color.White)) g.FillEllipse(fill, c.X, c.Y, size, size);
                using (Pen pen = new Pen(outside ? Color.Firebrick : StyleUi.Accent, 1.5f) { DashStyle = d > 0 ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawEllipse(pen, c.X, c.Y, size, size);
            }
        }

        private string ConduitStatus(int gravity)
        {
            ConduitLayout l = CurrentConduitLayout(out _);
            int each = l == null ? 0 : l.Columns * l.Rows;
            return gravity == 0 ? "Choose the duct bank (encasement) pipes at the top." : $"{gravity} encasement(s) × {each} conduits = {gravity * each} new pipes.";
        }

        private void RunConduits()
        {
            List<ObjectId> encasements = GravityPipes();
            if (!NeedGravity(encasements)) return;
            ConduitLayout l = CurrentConduitLayout(out string error);
            if (l == null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            PartPick pick = _conPart.Pick;
            if (pick == null) { MessageBox.Show(this, "Choose the conduit part (parts list, family and size, or From drawing…).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            int each = l.Columns * l.Rows;
            int outside = ConduitsOutside(l, ConduitDiameter());
            string network = _rbConSameNet.Checked ? "in each encasement's own network" : $"in the new network \"{(string.IsNullOrWhiteSpace(_txtConNet.Text) ? "Conduits" : _txtConNet.Text.Trim())}\"";
            string question = $"Create {each} conduits ({l.Columns} × {l.Rows}, {pick.Label}) inside each of the {encasements.Count} encasement pipe(s): {each * encasements.Count} new pipes {network}?" +
                              (outside > 0 ? $"\n\nWarning: {outside} conduit(s) per encasement cross the outside of <{_encName}>." : "");
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, outside > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;

            NetworkEditOps.ParallelOptions o = new NetworkEditOps.ParallelOptions
            {
                Horizontal = l.HorizontalOffsets(), Vertical = l.VerticalOffsets(), IncludeSource = true, FromBottom = l.FromBottom,
                SameNetwork = _rbConSameNet.Checked, NewNetworkName = string.IsNullOrWhiteSpace(_txtConNet.Text) ? "Conduits" : _txtConNet.Text.Trim(),
                Part = pick, PartsListId = pick.PartsListId,
                // Conduits stay unconnected as before; at joints without a structure they meet at a mitre.
                ConnectToStructures = false
            };
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            NetworkEditOps.Counts c = Change((tr, ed) => NetworkEditOps.ApplyParallel(tr, CivilApplication.ActiveDocument, acDoc.Database, encasements, o, ed));
            Report($"Created {c.Created} conduits in {c.Done} encasement pipe(s).", c,
                c.StayedIn.Count > 0 ? $"The conduit part has no family, so the conduits were copied from the picked one and stay in its network: {string.Join(", ", c.StayedIn)}." : "");
        }
    }
}
