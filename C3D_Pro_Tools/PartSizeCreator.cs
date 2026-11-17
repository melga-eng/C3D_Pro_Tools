using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ColumnStyle = System.Windows.Forms.ColumnStyle;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>How a size the catalog does not offer is made.</summary>
    internal enum NewSizeMethod
    {
        /// <summary>Method A: the C3D Pro any-size family of the same shape takes any dimensions.</summary>
        AnySizeFamily,
        /// <summary>Method B: the size is written into the family's own catalog table or value list (a backup is kept).</summary>
        ExtendCatalog
    }

    /// <summary>
    /// Makes a size with exact dimensions in a parts list. When the family's catalog already allows them (a listed value,
    /// a table row, a free range) the size is simply added; otherwise Method A or Method B is used.
    /// </summary>
    internal static class PartSizeCreator
    {
        /// <summary>The any-size family of the same shape as <paramref name="fam"/>.</summary>
        public static AnySizeFamily AnySizeFor(PartFamily fam)
        {
            string key = fam.Domain == DomainType.Pipe ? (fam.SweptShape == SweptShapeType.Circular ? "circle" : "box")
                                                       : (fam.BoundingShape == BoundingShapeType.Cylinder ? "cyl" : "rect");
            return NavisCatalogEditor.AnySize.First(f => f.Key == key);
        }

        public static bool AnySizeLoaded(AnySizeFamily def)
        {
            try { return PartsList.GetAvailablePartFamilies(def.Pipe ? DomainType.Pipe : DomainType.Structure).Any(f => f.GUID.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Creates the size in the list and returns its family (the any-size one for Method A) and size, or nulls with the reason.
        /// </summary>
        public static (ObjectId family, ObjectId size) Create(Transaction tr, ObjectId partsListId, PartFamily fam, Dictionary<PartContextType, double> values,
            NewSizeMethod method, List<string> log, out string error)
        {
            error = null;
            if (!fam.IsWriteEnabled) fam.UpgradeOpen();
            // Already possible in the family itself: nothing in the catalog changes.
            if (PartsListTools.CatalogAllows(fam, values))
            {
                ObjectId direct = PartsListTools.AddSize(tr, fam, values, out error);
                if (!direct.IsNull) { log.Add($"Added {PartsListTools.Describe(values)} to \"{fam.Name}\" (the catalog already allows it)."); return (fam.ObjectId, direct); }
            }

            if (method == NewSizeMethod.AnySizeFamily)
            {
                AnySizeFamily def = AnySizeFor(fam);
                if (!AnySizeLoaded(def))
                {
                    error = $"the C3D Pro any-size family for this shape is not loaded. Set it up once (Parts Lists window → \"Set up Method A\") and restart Civil 3D";
                    return (ObjectId.Null, ObjectId.Null);
                }
                ObjectId famId = PartsListTools.EnsureFamily(tr, partsListId, fam.Domain, def.Guid, out bool added);
                // Civil 3D can accept the family and still not list it in the parts list (your structure size, 2026-11-17: the
                // empty id then crashed with eNullObjectId).
                if (famId.IsNull)
                {
                    error = $"Civil 3D did not put \"{def.Description}\" in this parts list; this drawing's pipe catalog may not hold the C3D Pro any-size families " +
                            "(Parts Lists window → \"Set up Method A\" for the catalog the drawing uses, then restart Civil 3D)";
                    return (ObjectId.Null, ObjectId.Null);
                }
                if (added) log.Add($"Added the family \"{def.Description}\" to the parts list.");
                PartFamily any = (PartFamily)tr.GetObject(famId, OpenMode.ForWrite);
                ObjectId size = PartsListTools.AddSize(tr, any, values, out error);
                if (size.IsNull) return (famId, ObjectId.Null);
                try { ((PartSize)tr.GetObject(size, OpenMode.ForWrite)).Name = PartsListTools.Describe(values) + " (any size)"; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                log.Add($"Added {PartsListTools.Describe(values)} to \"{def.Description}\" (Method A).");
                return (famId, size);
            }

            // Method B: write the size into the family's own catalog table, then add it to the list.
            NavisCatalogContext ctx = NavisCatalogContext.Resolve();
            CatalogInfo catalog = fam.Domain == DomainType.Pipe ? ctx.Pipe : ctx.Structure;
            string xml = catalog == null ? null : NavisCatalogEditor.FamilyXml(catalog, fam.GUID);
            if (xml == null) { error = "this family's catalog file was not found (only families of the active catalog can be extended)"; return (ObjectId.Null, ObjectId.Null); }
            if (!NavisCatalogContext.Writable(catalog)) { error = $"the {catalog.Name} catalog folder is read-only"; return (ObjectId.Null, ObjectId.Null); }

            Dictionary<string, double> mm = values.ToDictionary(kv => kv.Key.ToString(), kv => Math.Round(kv.Value * 1000, 1));
            // A new table row needs every column: those not given keep the family's current default.
            SizeFilterRecord rec = new SizeFilterRecord(fam);
            foreach (string context in NavisCatalogEditor.TableContexts(xml))
            {
                if (mm.ContainsKey(context) || !Enum.TryParse(context, out PartContextType c)) continue;
                SizeFilterField f = NavisPartMatcher.Param(rec, c);
                if (f != null) mm[context] = Math.Round(NavisPartMatcher.ToMetres(f, f.Value) * 1000, 1);
            }
            if (NavisCatalogEditor.KindOf(xml, "WallThickness") == NavisCatalogEditor.ParamKind.Table && NavisCatalogEditor.TableKeyConflict(xml, mm, "WallThickness"))
            {
                error = "the family's table already has this inner size with another wall, and Civil 3D always uses the first one; Method A makes it exactly";
                return (ObjectId.Null, ObjectId.Null);
            }
            try
            {
                List<string> changes = NavisCatalogEditor.AddSize(xml, mm);
                if (changes.Count > 0) log.Add($"\"{fam.Name}\" in the {catalog.Name} catalog: added {string.Join(", ", changes)} (Method B; a backup of the file was kept).");
            }
            catch (Exception ex) { error = ex.Message; return (ObjectId.Null, ObjectId.Null); }
            ObjectId added2 = PartsListTools.AddSize(tr, fam, values, out error);
            if (added2.IsNull) error = $"the catalog was extended, but Civil 3D did not offer the size yet ({error}); restart Civil 3D and add it from the catalog sizes";
            return (fam.ObjectId, added2);
        }
    }

    /// <summary>Asks for the dimensions of a new size of one family, and how to make it when the catalog does not offer it.</summary>
    internal sealed class FormNewPartSize : Form
    {
        private readonly Dictionary<PartContextType, TextBox> _boxes = new Dictionary<PartContextType, TextBox>();
        private readonly RadioButton _rbA = new RadioButton { AutoSize = true }, _rbB = new RadioButton { AutoSize = true };

        public Dictionary<PartContextType, double> Values { get; } = new Dictionary<PartContextType, double>();
        public NewSizeMethod Method => _rbB.Checked ? NewSizeMethod.ExtendCatalog : NewSizeMethod.AnySizeFamily;

        public FormNewPartSize(PartFamily fam, Dictionary<PartContextType, double> start, NewSizeMethod method)
        {
            Text = "New size - " + fam.Name;
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(520, 420);
            ClientSize = new Size(600, 470);

            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel dims = ToolUi.Card("Dimensions (mm)", "Inside dimensions and wall. When the family already allows them, the size is simply added to the parts list.");
            foreach (PartContextType c in PartsListTools.KeyContexts(fam))
            {
                TextBox box = ToolUi.Number(start.TryGetValue(c, out double v) ? Math.Round(v * 1000, 1) : 0, 90);
                _boxes[c] = box;
                ToolUi.Field(dims, Caption(c), ToolUi.WithUnit(box, "mm"));
            }
            ToolUi.Add(stack, dims);

            AnySizeFamily any = PartSizeCreator.AnySizeFor(fam);
            bool loaded = PartSizeCreator.AnySizeLoaded(any);
            TableLayoutPanel how = ToolUi.Card("When the catalog does not offer this size");
            _rbA.Text = $"Method A: in \"{any.Description}\"" + (loaded ? "" : " (not set up yet)");
            _rbB.Text = $"Method B: add it to \"{fam.Name}\" in the catalog";
            ToolUi.Wide(how, _rbA);
            ToolUi.Wide(how, ToolUi.Note("Any size is exact; the catalog's own families are not changed. Needs a one-time setup and Civil 3D restart."));
            ToolUi.Wide(how, _rbB);
            ToolUi.Wide(how, ToolUi.Note("The size becomes a new row or value of this family in the shared catalog, for every drawing; a backup of the file is kept."));
            ToolUi.Add(stack, how);
            _rbA.Checked = method == NewSizeMethod.AnySizeFamily;
            _rbB.Checked = !_rbA.Checked;

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 10) };
            Button cancel = StyleUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = StyleUi.Primary("Create size", (s, e) => Accept());
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(host);
            Controls.Add(buttons);
        }

        private static string Caption(PartContextType c)
        {
            switch (c)
            {
                case PartContextType.PipeInnerDiameter: return "Inside diameter";
                case PartContextType.PipeInnerWidth: return "Inside width";
                case PartContextType.PipeInnerHeight: return "Inside height";
                case PartContextType.StructInnerDiameter: return "Inside diameter";
                case PartContextType.StructInnerWidth: return "Inside width";
                case PartContextType.StructInnerLength: return "Inside length";
                case PartContextType.WallThickness: return "Wall thickness";
                case PartContextType.FloorThickness: return "Floor thickness";
                default: return c.ToString();
            }
        }

        private void Accept()
        {
            Values.Clear();
            foreach (KeyValuePair<PartContextType, TextBox> kv in _boxes)
            {
                if (!ToolUi.TryNumber(kv.Value, out double mm) || mm < 0 || (mm <= 0 && kv.Key != PartContextType.WallThickness && kv.Key != PartContextType.FloorThickness))
                {
                    MessageBox.Show(this, $"Enter a positive {Caption(kv.Key).ToLowerInvariant()} in millimetres.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Values[kv.Key] = mm / 1000.0;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
