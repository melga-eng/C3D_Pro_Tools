using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Networks.Contracts;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>
    /// Network Editor, Generate Conduits: creates a grid of conduit pipes inside each chosen duct bank (encasement) pipe.
    /// Supports the seven standard built-in ICT and MV templates, custom placement, inner wall trimming with verified
    /// structure dimensions, connection and mitring rules, and per-end overrides.
    /// </summary>
    internal sealed partial class FormNetworkEditor
    {
        // Template & Variants
        private readonly ComboBox _cbConTemplate = ToolUi.Combo(280);
        private readonly ComboBox _cbConVariant = ToolUi.Combo(280);
        private readonly Label _lblTemplateGrid = new Label { AutoSize = true, Font = StyleUi.Bold, Margin = new Padding(0, 3, 0, 2) };
        private readonly Label _lblTemplatePitch = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(0, 2, 0, 2) };
        private readonly Label _lblTemplateDesign = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(0, 2, 0, 2), Tag = "note" };
        private readonly Label _lblTemplateNotes = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Font = StyleUi.Small, Margin = new Padding(0, 2, 0, 4), Tag = "note" };

        // Placement & Section
        private readonly RadioButton _rbConCentred = new RadioButton { Text = "Grid centred on encasement centreline", AutoSize = true };
        private readonly RadioButton _rbConBottom = new RadioButton { Text = "Lowest row centre above encasement bottom:", AutoSize = true };
        private readonly TextBox _txtConBottom = ToolUi.Number(0.25, 60);
        private readonly TextBox _txtConShift = ToolUi.Number(0, 60);
        private readonly CheckBox _chkAllowOutside = ToolUi.Check("Allow conduits outside encasement edge (warn instead of blocking)");
        private readonly Panel _conSection = new DoubleBufferedPanel { Width = 420, Height = 240, BackColor = StyleUi.Panel, Margin = new Padding(0, 4, 0, 4) };
        private readonly Label _lblConCheck = ToolUi.Note("");

        // Part Chooser
        private readonly PartChooser _conPart = new PartChooser(DomainType.Pipe);
        private readonly Label _lblPartDiameter = new Label { AutoSize = true, Font = StyleUi.Small, Margin = new Padding(0, 3, 0, 3), Tag = "note" };

        // End Settings
        private readonly ComboBox _cbStructureEnd = ToolUi.Combo(220);
        private readonly CheckBox _chkConnectStructures = ToolUi.Check("Connect conduits to structures in Civil 3D (same network required)");
        private readonly ComboBox _cbJointTreatment = ToolUi.Combo(220);
        private readonly CheckBox _chkMatchUnconnected = ToolUi.Check("Match unconnected ends to nearby manholes (opt-in)");
        private readonly TextBox _txtSearchDist = ToolUi.Number(0.50, 60);
        private readonly Button _btnOverrides = StyleUi.Secondary("Per-end overrides…");
        private readonly Label _lblOverrideCount = ToolUi.Note("0 per-end override(s) configured");
        private readonly Button _btnClearOverrides = StyleUi.Secondary("Clear overrides");
        private readonly Label _lblChoicesCount = ToolUi.Note("");
        private readonly Button _btnClearChoices = StyleUi.Secondary("Clear picks");

        // Manhole Inner Dimensions (Verified Sizes)
        private readonly ComboBox _cbUnknownFootprint = ToolUi.Combo(220);
        private readonly Label _lblMissingDimensions = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 2, 0, 4), Tag = "note" };
        private readonly TextBox _txtBoxPartSize = new TextBox { Width = 220 };
        private readonly TextBox _txtBoxLength = ToolUi.Number(4.60, 60);
        private readonly TextBox _txtBoxWidth = ToolUi.Number(2.30, 60);
        private readonly CheckBox _chkBoxRound = ToolUi.Check("Round structure (diameter = width)");
        private readonly ListBox _listBoxes = new ListBox { Width = 400, Height = 75, IntegralHeight = false };

        // Destination Network
        private readonly RadioButton _rbConSameNet = new RadioButton { Text = "Each encasement's own network (default)", AutoSize = true };
        private readonly RadioButton _rbConNewNet = new RadioButton { Text = "A new network:", AutoSize = true };
        private readonly TextBox _txtConNet = new TextBox { Width = 220, Text = "Conduits" };

        // Results & Errors
        private readonly Label _lblErrConGeneral = new Label { AutoSize = true, ForeColor = StyleUi.Danger, Font = StyleUi.Small, Visible = false, Margin = new Padding(0, 4, 0, 2), Tag = "note" };
        private readonly Panel _pnlConduitResult = new Panel { AutoSize = true, Dock = DockStyle.Top, Visible = false, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12, 10, 12, 10), BackColor = StyleUi.Panel };
        private readonly Label _lblConduitResultTitle = new Label { AutoSize = true, Font = StyleUi.Bold, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _lblConduitResultDetails = new Label { AutoSize = true, Font = StyleUi.Base, Margin = new Padding(0), Tag = "note" };

        // Operation & State
        private readonly IConduitOperation _conduitOp = new ConduitWorkflow(new ConduitCivil3DAccess());
        private bool _isConduitRunning;
        private readonly List<ConduitEndOverride> _endOverrides = new List<ConduitEndOverride>();
        private readonly List<StructureChoice> _structureChoices = new List<StructureChoice>();
        private readonly List<StructureInnerBox> _innerBoxes = new List<StructureInnerBox>();
        private Panel _hostConduits;
        private TableLayoutPanel _cardConduitTemplate;
        private TableLayoutPanel _cardConduitPlacement;
        private TableLayoutPanel _cardConduitPart;
        private TableLayoutPanel _cardConduitEnd;
        private TableLayoutPanel _cardConduitBoxes;
        private TableLayoutPanel _cardConduitNet;
        private string _conduitStatusText;
        private Color? _conduitStatusColor;
        private bool _conLoading;

        private string _encName;
        private double _encWidth, _encHeight;

        private sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

        private Control BuildConduits()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            _hostConduits = host;

            // 1. Template Card
            TableLayoutPanel cardTemplate = ToolUi.Card("Conduit template",
                "Choose a standard conduit configuration. Arrangement labels follow the source drawing (\"3X2\" for ICT, \"5 X 3\" for MV).");
            _cardConduitTemplate = cardTemplate;
            ToolUi.Field(cardTemplate, "Template", _cbConTemplate);
            ToolUi.Field(cardTemplate, "Variant", _cbConVariant);
            ToolUi.Field(cardTemplate, "Grid", _lblTemplateGrid);
            ToolUi.Field(cardTemplate, "Spacing", _lblTemplatePitch);
            ToolUi.Field(cardTemplate, "Design", _lblTemplateDesign);
            ToolUi.Wide(cardTemplate, _lblTemplateNotes);
            ToolUi.Add(stack, cardTemplate);

            // 2. Section & Placement Card
            TableLayoutPanel cardPlacement = ToolUi.Card("Section & placement",
                "Looking along the first chosen encasement. Red conduits cross its outside edge.");
            _cardConduitPlacement = cardPlacement;
            ToolUi.Wide(cardPlacement, _conSection);
            ToolUi.Wide(cardPlacement, _lblConCheck);
            ToolUi.Wide(cardPlacement, _rbConCentred);
            ToolUi.Wide(cardPlacement, ToolUi.Inline(_rbConBottom, _txtConBottom, Muted("m")));
            ToolUi.Field(cardPlacement, "Side shift", ToolUi.WithUnit(_txtConShift, "m (+ = right of run direction)"));
            ToolUi.Wide(cardPlacement, _chkAllowOutside);
            ToolUi.Add(stack, cardPlacement);

            // 3. Conduit Part Card
            TableLayoutPanel cardPart = ToolUi.Card("Conduit part",
                "\"From drawing…\" copies an existing conduit when the part is not in the catalog.");
            _cardConduitPart = cardPart;
            ToolUi.Wide(cardPart, _conPart);
            ToolUi.Wide(cardPart, _lblPartDiameter);
            ToolUi.Add(stack, cardPart);

            // 4. End Settings & Connections Card
            TableLayoutPanel cardEnd = ToolUi.Card("End settings & connections",
                "Conduit trimming and Civil 3D connectivity are configured separately. Trimming to inner wall stops each conduit where its own line meets the manhole's inside face.");
            _cardConduitEnd = cardEnd;
            _cbStructureEnd.Items.AddRange(new object[]
            {
                "Trim to inner wall (default)",
                "Trim to outer wall",
                "Extend to structure centre",
                "Keep encasement end"
            });
            _cbStructureEnd.SelectedIndex = 0;
            ToolUi.Field(cardEnd, "At structure", _cbStructureEnd);
            ToolUi.Wide(cardEnd, _chkConnectStructures);

            _cbJointTreatment.Items.AddRange(new object[]
            {
                "Mitre straight joints (default)",
                "Keep encasement ends at joints"
            });
            _cbJointTreatment.SelectedIndex = 0;
            ToolUi.Field(cardEnd, "At joints", _cbJointTreatment);

            _chkMatchUnconnected.CheckedChanged += (s, e) =>
            {
                _txtSearchDist.Enabled = _chkMatchUnconnected.Checked;
                UpdateChoicesDisplay();
            };
            _btnClearChoices.Click += (s, e) =>
            {
                _structureChoices.Clear();
                UpdateChoicesDisplay();
            };
            _lblChoicesCount.Visible = false;
            _btnClearChoices.Visible = false;
            _btnClearOverrides.Visible = false;
            ToolUi.Wide(cardEnd, ToolUi.Inline(_chkMatchUnconnected, Muted("within"), _txtSearchDist, Muted("m search distance"), _lblChoicesCount, _btnClearChoices));

            _btnOverrides.Click += (s, e) => OpenConduitOverrides();
            _btnClearOverrides.Click += (s, e) =>
            {
                _endOverrides.Clear();
                UpdateOverrideCountLabel();
            };
            ToolUi.Field(cardEnd, "Overrides", ToolUi.Inline(_btnOverrides, _lblOverrideCount, _btnClearOverrides));
            ToolUi.Add(stack, cardEnd);

            // 5. Manhole Inner Dimensions Card
            TableLayoutPanel cardBoxes = ToolUi.Card("Manhole inner dimensions (verified sizes)",
                "When a manhole part reports an inner width of 0, enter its verified interior dimensions in metres so conduits trim to the real inner wall.");
            _cardConduitBoxes = cardBoxes;
            ToolUi.Wide(cardBoxes, _lblMissingDimensions);

            _cbUnknownFootprint.Items.AddRange(new object[]
            {
                "Leave duct bank out (default)",
                "Keep encasement end (warn)"
            });
            _cbUnknownFootprint.SelectedIndex = 0;
            ToolUi.Field(cardBoxes, "Unknown size", _cbUnknownFootprint);

            ToolUi.Field(cardBoxes, "Part size", _txtBoxPartSize);
            ToolUi.Field(cardBoxes, "Dimensions", ToolUi.Inline(_txtBoxLength, Muted("m length (Y) ×"), _txtBoxWidth, Muted("m width (X)")));
            ToolUi.Wide(cardBoxes, _chkBoxRound);

            Button btnBoxAdd = StyleUi.Secondary("Add / Update dimension", (s, e) => AddOrUpdateInnerBox());
            Button btnBoxRemove = StyleUi.Secondary("Remove", (s, e) => RemoveSelectedInnerBox());
            ToolUi.Wide(cardBoxes, ToolUi.Inline(btnBoxAdd, btnBoxRemove));
            ToolUi.Wide(cardBoxes, _listBoxes);
            ToolUi.Add(stack, cardBoxes);

            // 6. Network Card
            TableLayoutPanel cardNet = ToolUi.Card("Network",
                "A conduit part with no family (copied from the drawing) cannot change network in Civil 3D: its conduits stay in the network of the conduit you picked.");
            _cardConduitNet = cardNet;
            FlowLayoutPanel radios = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            radios.Controls.Add(_rbConSameNet);
            radios.Controls.Add(ToolUi.Inline(_rbConNewNet, _txtConNet));
            ToolUi.Wide(cardNet, radios);
            ToolUi.Add(stack, cardNet);

            // 7. Errors & Results
            ToolUi.Wide(stack, _lblErrConGeneral);

            _pnlConduitResult.Paint += (s, e) => { using (Pen p = new Pen(StyleUi.Border)) e.Graphics.DrawRectangle(p, 0, 0, _pnlConduitResult.Width - 1, _pnlConduitResult.Height - 1); };
            _lblConduitResultTitle.Dock = DockStyle.Top;
            _lblConduitResultDetails.Dock = DockStyle.Top;
            _pnlConduitResult.Controls.Add(_lblConduitResultDetails);
            _pnlConduitResult.Controls.Add(_lblConduitResultTitle);
            ToolUi.Add(stack, _pnlConduitResult);

            // Exclusive groups
            ToolUi.Exclusive(_rbConCentred, _rbConBottom);
            ToolUi.Exclusive(_rbConSameNet, _rbConNewNet);

            // Events
            _conSection.Paint += PaintConduitSection;

            _cbConTemplate.SelectedIndexChanged += (s, e) =>
            {
                if (_conLoading) return;
                OnTemplateChanged();
            };

            _cbConVariant.SelectedIndexChanged += (s, e) =>
            {
                if (_conLoading) return;
                _conSection.Invalidate();
            };

            EventHandler placementChanged = (s, e) =>
            {
                if (_conLoading) return;
                _txtConBottom.Enabled = _rbConBottom.Checked;
                _conSection.Invalidate();
                UpdateConduitCheck();
            };
            _rbConCentred.CheckedChanged += placementChanged;
            _rbConBottom.CheckedChanged += placementChanged;
            _txtConBottom.TextChanged += placementChanged;
            _txtConShift.TextChanged += placementChanged;
            _chkAllowOutside.CheckedChanged += placementChanged;

            _conPart.Changed += (s, e) =>
            {
                if (_conLoading) return;
                UpdatePartDiameterDisplay();
                _conSection.Invalidate();
                UpdateConduitCheck();
            };

            _rbConNewNet.CheckedChanged += (s, e) =>
            {
                _txtConNet.Enabled = _rbConNewNet.Checked;
                _conPart.SetContext(_rbConNewNet.Checked ? ObjectId.Null : _partNetwork);
            };

            _listBoxes.SelectedIndexChanged += (s, e) =>
            {
                int idx = _listBoxes.SelectedIndex;
                if (idx >= 0 && idx < _innerBoxes.Count)
                {
                    StructureInnerBox b = _innerBoxes[idx];
                    _txtBoxPartSize.Text = b.PartSizeName;
                    _txtBoxLength.Text = ToolUi.Format(b.InnerLengthM);
                    _txtBoxWidth.Text = ToolUi.Format(b.InnerWidthM);
                    _chkBoxRound.Checked = b.Round;
                }
            };

            void UpdateWrapping() => UpdateConduitWrapping();
            host.Resize += (s, e) => UpdateWrapping();
            cardTemplate.Resize += (s, e) => UpdateWrapping();
            cardPlacement.Resize += (s, e) => UpdateWrapping();
            cardPart.Resize += (s, e) => UpdateWrapping();
            cardEnd.Resize += (s, e) => UpdateWrapping();
            cardBoxes.Resize += (s, e) => UpdateWrapping();
            cardNet.Resize += (s, e) => UpdateWrapping();
            _pnlConduitResult.Resize += (s, e) => UpdateWrapping();

            return host;
        }

        private static Label Muted(string text) => new Label { Text = text, AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(4, 7, 6, 0) };

        /// <summary>The pipe's outermost width; duct banks drawn with no wall report 0, so it is rebuilt from the inside and the wall.</summary>
        private static double OuterWidthOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterDiameterOrWidth; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { inner = pipe.InnerDiameterOrWidth; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return Math.Max(outer, inner + 2 * wall);
        }

        private void UpdateConduitWrapping()
        {
            if (_hostConduits == null) return;
            int w = Math.Max(200, _hostConduits.ClientSize.Width - 32);
            ToolUi.FitNotes(_hostConduits, w);
            if (_lblTemplateNotes != null) _lblTemplateNotes.MaximumSize = new Size(w, 0);
            if (_lblTemplateDesign != null) _lblTemplateDesign.MaximumSize = new Size(w, 0);
            if (_lblPartDiameter != null) _lblPartDiameter.MaximumSize = new Size(w, 0);
            if (_lblMissingDimensions != null) _lblMissingDimensions.MaximumSize = new Size(w, 0);
            if (_lblErrConGeneral != null) _lblErrConGeneral.MaximumSize = new Size(w, 0);
            if (_lblConduitResultDetails != null) _lblConduitResultDetails.MaximumSize = new Size(w - 24, 0);
        }

        private void ClearConduitValidation()
        {
            _lblErrConGeneral.Text = "";
            _lblErrConGeneral.Visible = false;
            _lblMissingDimensions.Text = "";
            _lblMissingDimensions.Visible = false;
            _pnlConduitResult.Visible = false;
            _conduitStatusText = null;
            _conduitStatusColor = null;
            _structureChoices.Clear();
            if (_session.Pipes.Count == 0) _endOverrides.Clear();
            UpdateOverrideCountLabel();
            UpdateChoicesDisplay();
        }

        private void SetConduitStatus(string text, Color color)
        {
            _conduitStatusText = text;
            _conduitStatusColor = color;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private ConduitTemplate SelectedTemplate()
        {
            if (_cbConTemplate.SelectedItem is TemplateItem item) return item.Template;
            return ConduitTemplateLibrary.Standard().FirstOrDefault();
        }

        private string SelectedVariantId()
        {
            ConduitTemplate t = SelectedTemplate();
            if (t == null || t.Variants.Count == 0 || _cbConVariant.SelectedIndex < 0 || _cbConVariant.SelectedIndex >= t.Variants.Count)
                return null;
            return t.Variants[_cbConVariant.SelectedIndex].Id;
        }

        private ConduitSection CurrentSection()
        {
            ConduitPlacement placement = _rbConBottom.Checked ? ConduitPlacement.BottomReferenced : ConduitPlacement.SectionCentred;
            double bottom = 0;
            if (placement == ConduitPlacement.BottomReferenced && ToolUi.TryNumber(_txtConBottom, out double b) && b > 0)
                bottom = b;
            double shift = 0;
            if (ToolUi.TryNumber(_txtConShift, out double s))
                shift = s;
            return new ConduitSection(placement, bottom, shift);
        }

        private sealed class TemplateItem
        {
            public ConduitTemplate Template { get; }
            public TemplateItem(ConduitTemplate t) { Template = t; }
            public override string ToString() => Template.Name;
        }

        private void FillConduitTemplates(string selectKey = null)
        {
            _cbConTemplate.Items.Clear();
            int selIdx = -1;
            int idx = 0;
            foreach (ConduitTemplate t in ConduitTemplateLibrary.Standard())
            {
                _cbConTemplate.Items.Add(new TemplateItem(t));
                if (selectKey != null && string.Equals(t.Key, selectKey, StringComparison.OrdinalIgnoreCase))
                    selIdx = idx;
                idx++;
            }
            if (selIdx >= 0) _cbConTemplate.SelectedIndex = selIdx;
            else if (_cbConTemplate.Items.Count > 0) _cbConTemplate.SelectedIndex = 0;
        }

        private void OnTemplateChanged()
        {
            ConduitTemplate t = SelectedTemplate();
            if (t == null) return;

            // Variants
            _cbConVariant.Items.Clear();
            if (t.Variants.Count > 0)
            {
                foreach (ConstructionVariant v in t.Variants)
                    _cbConVariant.Items.Add($"{v.Id} - {v.Description}");
                _cbConVariant.SelectedIndex = 0;
                _cbConVariant.Enabled = true;
            }
            else
            {
                _cbConVariant.Enabled = false;
            }

            // Grid & Spacing
            _lblTemplateGrid.Text = $"{t.Arrangement.Columns} columns × {t.Arrangement.Rows} rows ({t.Arrangement.Count} conduits)";
            _lblTemplatePitch.Text = t.Arrangement.Count == 1
                ? "Single conduit"
                : $"Pitch: {ToolUi.Format(t.Arrangement.HorizontalPitchM)} m across × {ToolUi.Format(t.Arrangement.VerticalPitchM)} m up (centre to centre)";

            // Design Reference
            string designText = "";
            if (!string.IsNullOrEmpty(t.Design.ConduitCallout))
            {
                designText = $"Design: {t.Design.ConduitCallout}";
                if (t.Design.EnvelopeWidthM.HasValue && t.Design.EnvelopeHeightM.HasValue)
                    designText += $" | Stated envelope: {ToolUi.Format(t.Design.EnvelopeWidthM.Value)} × {ToolUi.Format(t.Design.EnvelopeHeightM.Value)} m";
            }
            _lblTemplateDesign.Text = designText;

            // Notes
            _lblTemplateNotes.Text = t.Notes.Count > 0 ? "Review notes:\n• " + string.Join("\n• ", t.Notes) : "";

            // Section placement default
            if (t.DefaultSection.Placement == ConduitPlacement.BottomReferenced)
            {
                _rbConBottom.Checked = true;
                _txtConBottom.Text = ToolUi.Format(t.DefaultSection.BottomToLowestCentreM);
            }
            else
            {
                _rbConCentred.Checked = true;
            }

            UpdatePartDiameterDisplay();
            _conSection.Invalidate();
            UpdateConduitCheck();
            UpdatePipeSummary();
            UpdateConduitWrapping();
        }

        private void ApplyConduitSettings()
        {
            _conLoading = true;
            FillConduitTemplates(_s.ConduitLayoutName);
            _rbConSameNet.Checked = _s.ConduitSameNetwork;
            _rbConNewNet.Checked = !_s.ConduitSameNetwork;
            _txtConNet.Text = string.IsNullOrWhiteSpace(_s.ConduitNetworkName) ? "Conduits" : _s.ConduitNetworkName;
            _txtConNet.Enabled = !_s.ConduitSameNetwork;
            _conPart.SetContext(ObjectId.Null, _s.ConduitPartsList, _s.ConduitFamily, _s.ConduitSize);
            _conLoading = false;

            OnTemplateChanged();
        }

        private void SaveConduitSettings()
        {
            ConduitTemplate t = SelectedTemplate();
            if (t != null) _s.ConduitLayoutName = t.Key;
            _s.ConduitSameNetwork = _rbConSameNet.Checked;
            _s.ConduitNetworkName = _txtConNet.Text;
            _s.ConduitPartsList = _conPart.ListName;
            _s.ConduitFamily = _conPart.FamilyName;
            _s.ConduitSize = _conPart.SizeName;
        }

        private double ConduitDiameter()
        {
            PartPick pick = _conPart.Pick;
            if (pick == null) return 0;
            return pick.InnerHeight + 2 * pick.Get(PartContextType.WallThickness);
        }

        private void UpdatePartDiameterDisplay()
        {
            PartPick pick = _conPart.Pick;
            ConduitTemplate t = SelectedTemplate();
            double od = ConduitDiameter();

            if (pick == null)
            {
                _lblPartDiameter.Text = "No conduit part chosen. Select a parts list part or use 'From drawing…'.";
                _lblPartDiameter.ForeColor = StyleUi.Muted;
                return;
            }

            if (od <= 0)
            {
                _lblPartDiameter.Text = $"Selected part: {pick.Label}. Outside diameter could not be read (0 mm). Fit cannot be approved.";
                _lblPartDiameter.ForeColor = StyleUi.Danger;
                return;
            }

            string text = $"Selected part: {pick.Label} — Outside diameter: {ToolUi.Format(od)} m ({Math.Round(od * 1000):0} mm).";
            if (t != null && t.Design.ConduitOutsideDiameterM.HasValue)
            {
                double stated = t.Design.ConduitOutsideDiameterM.Value;
                if (Math.Abs(od - stated) > ConduitLimits.SizeMatchToleranceM)
                {
                    text += $" (Differs from template design OD of {Math.Round(stated * 1000):0} mm)";
                    _lblPartDiameter.ForeColor = StyleUi.PillColors("Review").fore;
                }
                else
                {
                    text += $" (Matches template design OD)";
                    _lblPartDiameter.ForeColor = StyleUi.PillColors("Exact").fore;
                }
            }
            else
            {
                _lblPartDiameter.ForeColor = StyleUi.Text;
            }
            _lblPartDiameter.Text = text;
        }

        private void RefreshConduitSection()
        {
            _encName = null;
            _encWidth = _encHeight = 0;
            Database db = AcadApp.DocumentManager.MdiActiveDocument?.Database;
            if (db == null) return;

            HashSet<ObjectId> currentPipes = new HashSet<ObjectId>(_session.Pipes);
            _endOverrides.RemoveAll(o => !DrawingRefs3D.TryResolve(db, o.Encasement, out ObjectId id) || !currentPipes.Contains(id));
            _structureChoices.RemoveAll(c => !DrawingRefs3D.TryResolve(db, c.Encasement, out ObjectId id) || !currentPipes.Contains(id));
            UpdateOverrideCountLabel();
            UpdateChoicesDisplay();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in _session.Pipes)
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

        private void UpdateConduitCheck()
        {
            ConduitTemplate t = SelectedTemplate();
            if (t == null) return;
            ConduitSection s = CurrentSection();
            double d = ConduitDiameter();

            if (_encName == null)
            {
                _lblConCheck.Text = $"{t.Arrangement.Count} conduits per encasement ({t.Name}). Choose the encasement pipes at the top to see them in the section.";
                _lblConCheck.ForeColor = StyleUi.Muted;
            }
            else
            {
                ConduitFitCheck fit = ConduitValidation.CheckSection(t, s, _encWidth, _encHeight, d);
                string checkText = $"{t.Arrangement.Count} conduits in <{_encName}> ({ToolUi.Format(_encWidth)} × {ToolUi.Format(_encHeight)} m outside)";
                if (d > 0)
                {
                    checkText += $", each {ToolUi.Format(Math.Round(d * 1000))} mm outside.";
                    if (fit.ConduitsOutside > 0)
                        checkText += $" {fit.ConduitsOutside} cross the encasement edge (min clearance: {ToolUi.Format(fit.MinEdgeClearanceM)} m).";
                    else
                        checkText += $" Edge clearance: {ToolUi.Format(fit.MinEdgeClearanceM)} m.";
                    if (fit.MinClearGapM.HasValue)
                        checkText += $" Clear gap: {ToolUi.Format(fit.MinClearGapM.Value)} m.";
                }
                else
                {
                    checkText += ". Choose the conduit part to check that they fit.";
                }
                _lblConCheck.Text = checkText;
                _lblConCheck.ForeColor = fit.ConduitsOutside > 0 ? StyleUi.Danger : StyleUi.Muted;
            }
        }

        private void PaintConduitSection(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            ConduitTemplate t = SelectedTemplate();
            if (t == null) return;
            ConduitSection s = CurrentSection();
            double d = ConduitDiameter();
            double drawD = d > 0 ? d : 0.6 * Math.Max(0.05, Math.Min(t.Arrangement.Columns > 1 ? t.Arrangement.HorizontalPitchM : 1, t.Arrangement.Rows > 1 ? t.Arrangement.VerticalPitchM : 1));
            IReadOnlyList<SectionCentre> centres = ConduitValidation.SectionCentres(t.Arrangement, s, _encHeight);

            double minX = -_encWidth / 2, maxX = _encWidth / 2, minY = -_encHeight / 2, maxY = _encHeight / 2;
            foreach (SectionCentre c in centres)
            {
                minX = Math.Min(minX, c.AcrossM - drawD / 2); maxX = Math.Max(maxX, c.AcrossM + drawD / 2);
                minY = Math.Min(minY, c.UpM - drawD / 2); maxY = Math.Max(maxY, c.UpM + drawD / 2);
            }
            if (maxX - minX < 1e-6 || maxY - minY < 1e-6) return;

            const int pad = 18;
            float scale = (float)Math.Min((_conSection.Width - 2 * pad) / (maxX - minX), (_conSection.Height - 2 * pad) / (maxY - minY));
            float cx = _conSection.Width / 2f - (float)((minX + maxX) / 2 * scale);
            float cy = _conSection.Height / 2f + (float)((minY + maxY) / 2 * scale);
            PointF P(double x, double y) => new PointF(cx + (float)(x * scale), cy - (float)(y * scale));

            if (_encWidth > 0 && _encHeight > 0)
            {
                PointF tl = P(-_encWidth / 2, _encHeight / 2);
                using (Brush fill = new HatchBrush(System.Drawing.Drawing2D.HatchStyle.DottedGrid, StyleUi.Border, StyleUi.Panel))
                    g.FillRectangle(fill, tl.X, tl.Y, (float)(_encWidth * scale), (float)(_encHeight * scale));
                using (Pen pen = new Pen(StyleUi.Muted, 1.5f))
                    g.DrawRectangle(pen, tl.X, tl.Y, (float)(_encWidth * scale), (float)(_encHeight * scale));
                using (Pen axis = new Pen(StyleUi.Border) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(axis, P(minX, 0), P(maxX, 0));
                    g.DrawLine(axis, P(0, minY), P(0, maxY));
                }
            }

            double r = drawD / 2;
            foreach (SectionCentre c in centres)
            {
                double rr = d > 0 ? r : 0;
                bool outside = _encWidth > 0 && (Math.Abs(c.AcrossM) + rr > _encWidth / 2 + 1e-6 || Math.Abs(c.UpM) + rr > _encHeight / 2 + 1e-6);
                PointF pt = P(c.AcrossM - r, c.UpM + r);
                float size = (float)(drawD * scale);
                using (Brush fill = new SolidBrush(outside ? StyleUi.PillColors("Failed").back : StyleUi.Panel))
                    g.FillEllipse(fill, pt.X, pt.Y, size, size);
                using (Pen pen = new Pen(outside ? StyleUi.Danger : StyleUi.Accent, 1.5f) { DashStyle = d > 0 ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawEllipse(pen, pt.X, pt.Y, size, size);
            }
        }

        private string ConduitStatus(int gravity)
        {
            ConduitTemplate t = SelectedTemplate();
            int each = t == null ? 0 : t.Arrangement.Count;
            return gravity == 0 ? "Choose the duct bank (encasement) pipes at the top." : $"{gravity} encasement(s) × {each} conduits = {gravity * each} new pipes.";
        }

        private void OpenConduitOverrides()
        {
            List<ObjectId> gravity = GravityPipes();
            List<FormConduitOverrides.EncasementItem> items = new List<FormConduitOverrides.EncasementItem>();
            Database db = AcadApp.DocumentManager.MdiActiveDocument?.Database;
            if (db != null)
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in gravity)
                    {
                        if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                            items.Add(new FormConduitOverrides.EncasementItem(DrawingRefs3D.Ref(id), p.Name));
                    }
                }
            }

            using (FormConduitOverrides dlg = new FormConduitOverrides(items, _endOverrides))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _endOverrides.Clear();
                    _endOverrides.AddRange(dlg.Overrides);
                    UpdateOverrideCountLabel();
                }
            }
        }

        private void UpdateOverrideCountLabel()
        {
            int count = _endOverrides.Count;
            _lblOverrideCount.Text = count == 0
                ? "0 per-end override(s) configured"
                : $"{count} per-end override(s) configured";
            _btnClearOverrides.Visible = count > 0;
        }

        private void UpdateChoicesDisplay()
        {
            int count = _chkMatchUnconnected.Checked ? _structureChoices.Count : 0;
            _lblChoicesCount.Text = count > 0 ? $"{count} manhole pick(s) set" : "";
            _lblChoicesCount.Visible = count > 0;
            _btnClearChoices.Visible = count > 0;
        }

        private void AddOrUpdateInnerBox()
        {
            string partSize = _txtBoxPartSize.Text.Trim();
            if (string.IsNullOrWhiteSpace(partSize))
            {
                MessageBox.Show(this, "Enter the structure part size name (e.g. 4,600.00 x 2,300.00).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ToolUi.TryNumber(_txtBoxLength, out double len) || len <= 0 || len > ConduitLimits.MaxInnerBoxM)
            {
                MessageBox.Show(this, $"Enter an inner length between 0 and {ConduitLimits.MaxInnerBoxM} m.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ToolUi.TryNumber(_txtBoxWidth, out double wid) || wid <= 0 || wid > ConduitLimits.MaxInnerBoxM)
            {
                MessageBox.Show(this, $"Enter an inner width between 0 and {ConduitLimits.MaxInnerBoxM} m.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StructureInnerBox box = new StructureInnerBox(partSize, len, wid, _chkBoxRound.Checked);
            int idx = _innerBoxes.FindIndex(b => string.Equals(b.PartSizeName, partSize, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _innerBoxes[idx] = box;
            else _innerBoxes.Add(box);

            RefreshInnerBoxList();
        }

        private void RemoveSelectedInnerBox()
        {
            int idx = _listBoxes.SelectedIndex;
            if (idx >= 0 && idx < _innerBoxes.Count)
            {
                _innerBoxes.RemoveAt(idx);
                RefreshInnerBoxList();
            }
        }

        private void RefreshInnerBoxList()
        {
            _listBoxes.BeginUpdate();
            _listBoxes.Items.Clear();
            foreach (StructureInnerBox b in _innerBoxes)
            {
                string shape = b.Round ? $"Round (Dia {ToolUi.Format(b.InnerWidthM)} m)" : $"{ToolUi.Format(b.InnerLengthM)} × {ToolUi.Format(b.InnerWidthM)} m";
                _listBoxes.Items.Add($"{b.PartSizeName}: {shape}");
            }
            _listBoxes.EndUpdate();
        }

        private static string FormatEndTreatment(ConduitEndPlan end)
        {
            if (end == null) return "Free end";
            if (end.Kind == ConduitEndKind.Structure)
            {
                string structName = end.Structure?.Name ?? "Structure";
                string treat = end.Treatment switch
                {
                    StructureEndTreatment.InnerWall => "Inner wall",
                    StructureEndTreatment.OuterWall => "Outer wall",
                    StructureEndTreatment.StructureCentre => "Centre",
                    StructureEndTreatment.EncasementEnd => "Encasement end",
                    _ => "Structure"
                };
                string conn = end.Connect ? " (connected)" : "";
                string found = end.FoundBy.HasValue && end.FoundBy.Value != StructureMatch.Connected ? " [by position]" : "";
                return $"{treat} @ {structName}{conn}{found}";
            }
            if (end.Kind == ConduitEndKind.Joint)
            {
                return end.Joint == JointTreatment.Mitre ? "Mitre (joint)" : "Keep end (joint)";
            }
            return "Free end";
        }

        private void RunConduits()
        {
            if (_isConduitRunning) return;
            _isConduitRunning = true;
            if (_btnRun != null) _btnRun.Enabled = false;

            try
            {
                ClearConduitValidation();
                Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
                if (acDoc == null)
                {
                    _lblErrConGeneral.Text = "No active document in Civil 3D.";
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    SetConduitStatus("[Rejected] No active drawing.", StyleUi.Danger);
                    MessageBox.Show(this, "Open a drawing in Civil 3D first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                List<ObjectId> gravity = GravityPipes();
                if (!NeedGravity(gravity))
                {
                    _lblErrConGeneral.Text = "Choose some gravity encasement pipes first.";
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    return;
                }

                ConduitTemplate template = SelectedTemplate();
                if (template == null)
                {
                    MessageBox.Show(this, "Select a conduit template first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                PartPick pick = _conPart.Pick;
                if (pick == null)
                {
                    const string msg = "Choose the conduit part (parts list, family and size, or From drawing…).";
                    _lblErrConGeneral.Text = msg;
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    SetConduitStatus("[Validation] " + msg, StyleUi.Danger);
                    MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                double od = ConduitDiameter();
                if (od <= 0)
                {
                    const string msg = "The conduit part's outside diameter could not be determined. Fit cannot be approved.";
                    _lblErrConGeneral.Text = msg;
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    SetConduitStatus("[Validation] " + msg, StyleUi.Danger);
                    MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ConduitPartChoice partChoice = ConduitCivil3DAccess.ToChoice(pick, od);
                ConduitSection section = CurrentSection();
                string variantId = SelectedVariantId();

                StructureEndTreatment treatment = (StructureEndTreatment)_cbStructureEnd.SelectedIndex;
                bool connect = _chkConnectStructures.Checked;
                JointTreatment joint = (JointTreatment)_cbJointTreatment.SelectedIndex;
                bool matchUnconnected = _chkMatchUnconnected.Checked;
                double searchDist = 0.5;
                if (matchUnconnected && (!ToolUi.TryNumber(_txtSearchDist, out searchDist) || searchDist < 0 || searchDist > ConduitLimits.MaxStructureSearchM))
                {
                    string msg = $"Enter a structure search distance between 0 and {ConduitLimits.MaxStructureSearchM} m.";
                    _lblErrConGeneral.Text = msg;
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    SetConduitStatus("[Validation] " + msg, StyleUi.Danger);
                    MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                UnknownFootprintPolicy footprintPolicy = (UnknownFootprintPolicy)_cbUnknownFootprint.SelectedIndex;
                ConduitDestination destination = _rbConSameNet.Checked ? ConduitDestination.EncasementNetwork : ConduitDestination.NewNetwork;
                string newNetName = string.IsNullOrWhiteSpace(_txtConNet.Text) ? "Conduits" : _txtConNet.Text.Trim();

                HashSet<DrawingObjectRef> currentRefs = new HashSet<DrawingObjectRef>(gravity.Select(DrawingRefs3D.Ref));

                // Send only overrides for the current selection
                List<ConduitEndOverride> activeOverrides = _endOverrides
                    .Where(o => o != null && currentRefs.Contains(o.Encasement))
                    .ToList();

                // Send nearby manhole choices only while matching is enabled AND for current selection
                List<StructureChoice> activeChoices = matchUnconnected
                    ? _structureChoices.Where(c => c != null && currentRefs.Contains(c.Encasement)).ToList()
                    : new List<StructureChoice>();

                ConduitRequest request = new ConduitRequest
                {
                    Drawing = DrawingRefs3D.Identity(acDoc),
                    Encasements = gravity.Select(DrawingRefs3D.Ref).ToList(),
                    Template = template,
                    VariantId = variantId,
                    Section = section,
                    Part = partChoice,
                    Ends = new ConduitEndRules
                    {
                        AtStructure = treatment,
                        ConnectToStructures = connect,
                        AtJoint = joint,
                        MatchUnconnectedEnds = matchUnconnected,
                        StructureSearchM = searchDist,
                        WhenFootprintUnknown = footprintPolicy,
                        InnerBoxes = _innerBoxes,
                        Overrides = activeOverrides,
                        StructureChoices = activeChoices
                    },
                    Destination = destination,
                    NewNetworkName = newNetName,
                    AllowConduitsOutsideEncasement = _chkAllowOutside.Checked
                };

                ConduitPreview preview = _conduitOp.Preview(request, CancellationToken.None);

                // Check for ambiguous structure candidates
                List<FormConduitAmbiguousChoice.AmbiguousItem> ambig = new List<FormConduitAmbiguousChoice.AmbiguousItem>();
                foreach (ConduitSourcePlan item in preview.Items)
                {
                    if (item.StartEnd != null && item.StartEnd.Candidates.Count > 1)
                        ambig.Add(new FormConduitAmbiguousChoice.AmbiguousItem(item.Encasement, item.Name, PipeEnd.Start, item.StartEnd.Candidates));
                    if (item.EndEnd != null && item.EndEnd.Candidates.Count > 1)
                        ambig.Add(new FormConduitAmbiguousChoice.AmbiguousItem(item.Encasement, item.Name, PipeEnd.End, item.EndEnd.Candidates));
                }

                if (ambig.Count > 0)
                {
                    using (FormConduitAmbiguousChoice dlg = new FormConduitAmbiguousChoice(ambig))
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            foreach (StructureChoice ch in dlg.Choices)
                            {
                                _structureChoices.RemoveAll(x => x.Encasement == ch.Encasement && x.End == ch.End);
                                _structureChoices.Add(ch);
                            }
                            UpdateChoicesDisplay();
                            request = request with { Ends = request.Ends with { StructureChoices = _structureChoices.Where(c => currentRefs.Contains(c.Encasement)).ToList() } };
                            preview = _conduitOp.Preview(request, CancellationToken.None);
                        }
                        else
                        {
                            SetConduitStatus("[Cancelled] Manhole choice selection cancelled.", StyleUi.Muted);
                            return;
                        }
                    }
                }

                // Check for unknown footprints
                HashSet<string> missingSizes = new HashSet<string>();
                foreach (ConduitSourcePlan item in preview.Items)
                {
                    if (item.StartEnd?.Kind == ConduitEndKind.Structure && item.StartEnd.Structure != null && item.StartEnd.Structure.Source == FootprintSource.Unknown)
                        missingSizes.Add(item.StartEnd.Structure.PartSizeName);
                    if (item.EndEnd?.Kind == ConduitEndKind.Structure && item.EndEnd.Structure != null && item.EndEnd.Structure.Source == FootprintSource.Unknown)
                        missingSizes.Add(item.EndEnd.Structure.PartSizeName);
                }

                if (missingSizes.Count > 0)
                {
                    _lblMissingDimensions.Text = "Missing verified inner dimensions for:\n• " + string.Join("\n• ", missingSizes) +
                        "\nEnter verified length and width in the card below to trim conduits to inner walls.";
                    _lblMissingDimensions.Visible = true;
                    _txtBoxPartSize.Text = missingSizes.First();
                    UpdateConduitWrapping();
                }
                else
                {
                    _lblMissingDimensions.Visible = false;
                }

                if (preview.Status == OperationStatus.Cancelled)
                {
                    SetConduitStatus("[Cancelled] Preview was cancelled.", StyleUi.Muted);
                    return;
                }

                if (preview.Status == OperationStatus.Rejected && preview.Items.Count == 0)
                {
                    string statusTag = "[Rejected]";
                    string issueMsg = IssueText(preview.Issues);
                    _lblErrConGeneral.Text = issueMsg;
                    _lblErrConGeneral.ForeColor = StyleUi.Danger;
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                    SetConduitStatus($"{statusTag} {issueMsg}", StyleUi.Danger);
                    MessageBox.Show(this, issueMsg, $"{Text} - Rejected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                bool anyApplicable = preview.Items.Any(i => i.CanApply);

                // Build Preview Rows
                string[] columns = { "Duct bank", "Conduits", "Start treatment", "End treatment", "Destination", "Status / Notes" };
                List<PreviewRow> rows = new List<PreviewRow>();
                foreach (ConduitSourcePlan item in preview.Items)
                {
                    string ductBank = item.Name;
                    string conduitCount = item.CanApply ? $"{item.Conduits.Count} ({template.Arrangement.Columns}×{template.Arrangement.Rows})" : "0";
                    string startTreatment = FormatEndTreatment(item.StartEnd);
                    string endTreatment = FormatEndTreatment(item.EndEnd);
                    string destNet = item.Destination != null ? item.Destination.Name + (item.Destination.CreatedAtApply ? " (new)" : "") : "Encasement";
                    string notes = item.Issues.Count > 0
                        ? string.Join("; ", item.Issues.Select(i => i.Message))
                        : "Ready";

                    rows.Add(new PreviewRow
                    {
                        Tag = item.Encasement,
                        Checked = item.CanApply,
                        Enabled = item.CanApply,
                        Cells = new[] { ductBank, conduitCount, startTreatment, endTreatment, destNet, notes }
                    });
                }

                var reqIssues = preview.Issues.Where(i => i.Code != ConduitIssueCodes.NothingToGenerate).ToList();
                string reqIssuesText = reqIssues.Count > 0
                    ? "\n\nOperation notes & warnings:\n• " + string.Join("\n• ", reqIssues.Select(i => i.Message))
                    : "";

                if (reqIssues.Count > 0)
                {
                    _lblErrConGeneral.Text = string.Join("\n", reqIssues.Select(i => "• " + i.Message));
                    _lblErrConGeneral.ForeColor = anyApplicable ? StyleUi.PillColors("Review").fore : StyleUi.Danger;
                    _lblErrConGeneral.Visible = true;
                    UpdateConduitWrapping();
                }

                string intro = anyApplicable
                    ? $"Review the planned conduits for the {preview.Items.Count} chosen duct bank(s). Blocked banks are greyed out with reasons. Uncheck any bank you do not want to fill.{reqIssuesText}"
                    : $"None of the {preview.Items.Count} chosen duct bank(s) can be filled. Review each bank's reason in the table below (all banks are blocked):{reqIssuesText}";

                string applyText = anyApplicable ? "Generate" : "Close";
                string windowTitle = anyApplicable ? "Generate conduits" : "Generate conduits (All blocked)";

                if (!Confirm(windowTitle, intro, columns, rows, applyText) || !anyApplicable)
                {
                    if (!anyApplicable)
                    {
                        string noChangeMsg = "No chosen duct bank can be filled; review each bank's reason in the preview or notes.";
                        _lblErrConGeneral.Text = noChangeMsg + reqIssuesText;
                        _lblErrConGeneral.ForeColor = StyleUi.Danger;
                        _lblErrConGeneral.Visible = true;
                        UpdateConduitWrapping();
                        SetConduitStatus("[No change] " + noChangeMsg, StyleUi.Muted);
                    }
                    return;
                }

                List<DrawingObjectRef> chosen = rows.Where(r => r.Checked && r.Enabled).Select(r => (DrawingObjectRef)r.Tag).ToList();
                if (chosen.Count == 0)
                {
                    MessageBox.Show(this, "No applicable duct banks were selected to generate.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ConduitApplyRequest applyReq = new ConduitApplyRequest(preview, chosen);
                ConduitResult result = _conduitOp.Apply(applyReq, null, CancellationToken.None);
                ReportConduits(result, preview);
            }
            finally
            {
                _isConduitRunning = false;
                if (_btnRun != null) _btnRun.Enabled = true;
            }
        }

        private void ReportConduits(ConduitResult result, ConduitPreview preview)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            int filled = result.EncasementsFilled;
            int created = result.ConduitsCreated;
            int connections = result.ConnectionsMade;

            List<string> details = new List<string>();
            details.Add($"Created {created} conduit(s) across {filled} duct bank(s).");
            if (connections > 0)
                details.Add($"{connections} conduit connection(s) verified at structures.");

            // List each uncommitted / affected bank with its name, status, and issue
            var uncommitted = result.Items.Where(i => i.Status != ItemStatus.Committed).ToList();
            if (uncommitted.Count > 0)
            {
                details.Add($"\nUncommitted duct banks ({uncommitted.Count}):");
                foreach (ConduitItemOutcome item in uncommitted)
                {
                    string statusLabel = item.Status switch
                    {
                        ItemStatus.RolledBack => "Rolled back",
                        ItemStatus.Rejected => "Rejected / Stale",
                        ItemStatus.NotAttempted => "Not attempted",
                        ItemStatus.OutcomeUnknown => "Unknown outcome",
                        _ => item.Status.ToString()
                    };
                    string reason = item.Issue != null ? $": {item.Issue.Message}" : "";
                    details.Add($"• {item.Name} [{statusLabel}]{reason}");
                }
            }

            int unknown = result.Items.Count(i => i.Status == ItemStatus.OutcomeUnknown);
            if (unknown > 0 || result.Status == OperationStatus.OutcomeUnknown)
            {
                details.Add($"\nWarning: Outcome unknown on {unknown} duct bank(s): drawing state may be inconsistent. Inspect the drawing before retrying.");
            }

            // Incomplete runs (at mitred joints)
            List<OperationIssue> incompleteIssues = result.Issues
                .Concat(result.Items.Where(i => i.Issue != null).Select(i => i.Issue))
                .Where(iss => iss.Code == ConduitIssueCodes.RunIncomplete)
                .ToList();
            if (incompleteIssues.Count > 0)
            {
                details.Add("\nIncomplete runs (at mitred joints):");
                foreach (OperationIssue iss in incompleteIssues) details.Add($"• {iss.Message}");
            }

            // Parts list changes
            List<OperationIssue> partsListIssues = result.Issues.Where(iss => iss.Code == ConduitIssueCodes.PartsListChanged).ToList();
            if (partsListIssues.Count > 0)
            {
                details.Add("\nParts lists updated:\n• " + string.Join("\n• ", partsListIssues.Select(iss => iss.Message)));
            }

            // Result-level / operation-level issues
            List<OperationIssue> generalIssues = result.Issues
                .Where(iss => iss.Code != ConduitIssueCodes.RunIncomplete && iss.Code != ConduitIssueCodes.PartsListChanged)
                .ToList();
            if (generalIssues.Count > 0)
            {
                details.Add("\nOperation notes:\n• " + string.Join("\n• ", generalIssues.Select(iss => iss.Message)));
            }

            if (acDoc != null) ViewHelper.Regen(acDoc);

            string title = result.Status == OperationStatus.Succeeded ? $"Created {created} conduits in {filled} duct bank(s)"
                : result.Status == OperationStatus.PartiallySucceeded ? $"Partially completed: created {created} conduits ({uncommitted.Count} uncommitted)"
                : result.Status == OperationStatus.OutcomeUnknown ? "Outcome unknown: inspect drawing"
                : "No conduits created";

            // Show the UNDO note only when changes are known to have committed
            if (filled > 0 && result.Status != OperationStatus.OutcomeUnknown)
            {
                details.Add("\nUNDO reverses the whole change.");
            }
            else if (result.Status == OperationStatus.OutcomeUnknown)
            {
                details.Add("\nDo not use UNDO until you have inspected the drawing state.");
            }

            string fullReport = string.Join("\n", details);

            _lblConduitResultTitle.Text = title;
            _lblConduitResultTitle.ForeColor = result.Status == OperationStatus.Succeeded ? StyleUi.Text : StyleUi.Danger;
            _lblConduitResultDetails.Text = fullReport;
            _pnlConduitResult.Visible = true;
            UpdateConduitWrapping();

            SetConduitStatus(title, result.Status == OperationStatus.Succeeded ? StyleUi.Text : StyleUi.Danger);
            MessageBox.Show(this, fullReport, $"{Text} - Generate Conduits", MessageBoxButtons.OK,
                result.Status == OperationStatus.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
