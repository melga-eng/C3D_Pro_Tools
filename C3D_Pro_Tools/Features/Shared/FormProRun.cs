using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Labels;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Pavement;
using C3DProTools.ProfileTool;
using C3DProTools.Features.Shared.Contracts;
using Surface = Autodesk.Civil.DatabaseServices.Surface;
using Label = System.Windows.Forms.Label;

// C3D Pro Run: one window for the profile view, network and label steps. You tick what to work on, set the few values each step
// needs, and press a step. The settings are saved in the drawing on every run (and remembered for drawings that have none), so
// nothing has to sit beside the drawing. Each step is the same operation the C3DPROPV / C3DPRONET / C3DPROLBL commands run.
namespace C3DProTools.Features.Shared
{
    internal sealed class FormProRun : Form
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        };

        private readonly Document _doc;
        private JsonObject _root;
        private string _source;
        private ProfileViewsSettings _pv;
        private NetworksSettings _net;
        private LabelsSettings _lbl;
        private PavementSettings _pav;
        private readonly List<string> _surfaces = new List<string>();

        private readonly CheckedListBox _alignments = Picker(), _networks = Picker(), _layouts = Picker(), _labelNetworks = Picker();
        private readonly TextBox _report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = StyleUi.Mono, BackColor = StyleUi.Panel };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(0, 8, 12, 0) };
        private readonly Button _openReport;
        private string _lastFile;

        // Profile views
        private readonly ComboBox _existing = ToolUi.Combo(), _proposed = ToolUi.Combo();
        private readonly TextBox _proposedProfile = StyleUi.Box(), _matchTemplate = StyleUi.Box();
        private readonly TextBox _splitLength = ToolUi.Number(450), _splitGap = ToolUi.Number(30);
        // Networks
        private readonly ComboBox _rimSurface = ToolUi.Combo(), _fitFace = ToolUi.Combo();
        private readonly TextBox _coverMax = ToolUi.Number(6), _maxSlope = ToolUi.Number(7), _clearance = ToolUi.Number(0.3);
        // Labels
        private readonly TextBox _maxDistance = ToolUi.Number(0), _gap = ToolUi.Number(0);
        // Pavement
        private readonly DataGridView _zones = ToolUi.Grid();
        private readonly TextBox _unpavedCover = ToolUi.Number(0.6);
        private readonly ComboBox _unpavedType = ToolUi.Combo(80);
        private readonly CheckBox _coverBand = ToolUi.Check("Cover check: the edge band (shoulder, blast pad) does not have to meet that pavement's rule");
        private readonly CheckBox _labelBand = ToolUi.Check("Start / end labels: mark the pavement edge inside the edge band, not the surface edge");

        public FormProRun(Document doc)
        {
            _doc = doc;
            Text = "C3D Pro Run";
            Font = StyleUi.Base;
            BackColor = StyleUi.Surface;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(980, 820);
            MinimumSize = new Size(760, 600);
            _openReport = StyleUi.Secondary("Open full report", (s, e) => { if (_lastFile != null) try { Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true }); } catch (Exception) { } });
            _openReport.Enabled = false;

            Load();
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(Page("Profile views", ProfilePage()));
            tabs.TabPages.Add(Page("Networks", NetworkPage()));
            tabs.TabPages.Add(Page("Labels", LabelPage()));
            tabs.TabPages.Add(Page("Pavement", PavementPage()));

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 230, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 4, 12, 10), BackColor = StyleUi.Surface };
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottom.Controls.Add(ToolUi.Inline(_status, _openReport), 0, 0);
            bottom.Controls.Add(_report, 0, 1);

            Controls.Add(tabs);
            Controls.Add(bottom);
            Controls.Add(ToolUi.Header("C3D Pro Run", $"{System.IO.Path.GetFileName(doc.Name)}  ·  settings: {_source}"));
            _status.Text = "Tick what to work on, then press a step. \"Check\" changes nothing.";
        }

        // ---- Settings -----------------------------------------------------------------------------------------------

        private void Load()
        {
            string text = SettingsSource.Text(_doc.Name, out _source);
            try { _root = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { _root = null; }
            _root ??= JsonNode.Parse(SettingsSource.Defaults) as JsonObject;
            T Section<T>(string name) where T : new()
            {
                foreach (var kv in _root) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    try { return kv.Value.Deserialize<T>(Json) ?? new T(); } catch (JsonException) { }
                return new T();
            }
            _pv = Section<ProfileViewsSettings>("profileViews");
            _pv.Dims ??= new ProfileDimSettings();
            _net = Section<NetworksSettings>("networks");
            _lbl = Section<LabelsSettings>("labels");
            _pav = Section<PavementSettings>("pavement");
            _pav.Zones ??= new List<PavementZone>();
            _pav.Unpaved ??= new PavementZone { Name = "Unpaved" };

            // What the drawing has to choose from.
            using (Transaction tr = _doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(_doc.Database);
                foreach (ObjectId id in civil.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Surface sf) _surfaces.Add(sf.Name);
                _surfaces.Sort(StringComparer.OrdinalIgnoreCase);
                var alignments = new List<string>();
                foreach (ObjectId id in civil.GetAlignmentIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Alignment al && al.GetProfileViewIds().Count > 0) alignments.Add(al.Name);
                var networks = new List<string>();
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Network n) networks.Add(n.Name);
                var layouts = new List<string>();
                var dict = (DBDictionary)tr.GetObject(_doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict) if (!string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase)) layouts.Add(e.Key);
                Fill(_alignments, alignments, _pv.Alignments);
                Fill(_networks, networks, _net.Networks);
                Fill(_layouts, layouts, _lbl.Layouts);
                Fill(_labelNetworks, networks, _lbl.Networks);
            }
        }

        // Every name of each list and the ticked ones, whatever the filter shows.
        private readonly Dictionary<CheckedListBox, List<string>> _names = new Dictionary<CheckedListBox, List<string>>();
        private readonly Dictionary<CheckedListBox, HashSet<string>> _ticked = new Dictionary<CheckedListBox, HashSet<string>>();

        private void Fill(CheckedListBox list, List<string> names, string patterns)
        {
            _names[list] = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            _ticked[list] = new HashSet<string>(_names[list].Where(n => NamePatterns.Matches(patterns ?? "*", n)), StringComparer.OrdinalIgnoreCase);
            foreach (string n in _names[list]) list.Items.Add(n, _ticked[list].Contains(n));
        }

        /// <summary>The ticked names, hidden by the filter or not, as patterns: "*" when every name is ticked, else the names; null when none is.</summary>
        private string Chosen(CheckedListBox list)
        {
            HashSet<string> on = _ticked[list];
            if (on.Count == 0) return null;
            if (_names[list].All(on.Contains)) return "*";
            return string.Join(",", _names[list].Where(on.Contains));
        }

        private static double Num(TextBox box, double fallback) => ToolUi.TryNumber(box, out double v) ? v : fallback;
        private static string Pick(ComboBox box) => box.SelectedItem as string == "(none)" ? "" : box.SelectedItem as string ?? "";

        /// <summary>The window's values into the settings, saved in the drawing (and remembered for drawings that have none).</summary>
        private void Save()
        {
            _pv.ExistingSurface = Pick(_existing);
            _pv.ProposedSurface = Pick(_proposed);
            _pv.ProposedProfile = _proposedProfile.Text.Trim();
            _pv.CreateMissingTop = _createTop.Checked;
            _pv.TopInsideEncasement = _topInside.Checked;
            _pv.MatchTemplate = _matchTemplate.Text.Trim();
            _pv.SplitLength = Num(_splitLength, _pv.SplitLength);
            _pv.SplitGap = Num(_splitGap, _pv.SplitGap);
            _net.Surface = Pick(_rimSurface);
            _net.FitFace = _fitFace.SelectedItem as string ?? _net.FitFace;
            _net.CoverMax = Num(_coverMax, _net.CoverMax);
            _net.CoverMaxSlope = Num(_maxSlope, _net.CoverMaxSlope);
            _net.CoverClearance = Num(_clearance, _net.CoverClearance);
            _lbl.MaxDistance = Num(_maxDistance, _lbl.MaxDistance);
            _lbl.Gap = Num(_gap, _lbl.Gap);

            _zones.EndEdit();
            List<PavementZone> before = _pav.Zones;
            _pav.Zones = new List<PavementZone>();
            foreach (DataGridViewRow r in _zones.Rows)
            {
                string surface = r.Cells[0].Value as string ?? "";
                if (surface.Length == 0) continue;
                double D(int i) => double.TryParse(Convert.ToString(r.Cells[i].Value, System.Globalization.CultureInfo.InvariantCulture)?.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
                // What the window does not show (a zone's own label styles) is kept.
                PavementZone was = before.FirstOrDefault(z => string.Equals(z.Surface, surface, StringComparison.OrdinalIgnoreCase));
                _pav.Zones.Add(new PavementZone
                {
                    Surface = surface, Name = r.Cells[1].Value as string is { Length: > 0 } n ? n : surface,
                    MinCover = D(2), DuctType = r.Cells[3].Value as string ?? "", EdgeBand = D(4), MaxCover = D(5),
                    StartLabelStyle = was?.StartLabelStyle ?? "", EndLabelStyle = was?.EndLabelStyle ?? "",
                });
            }
            _pav.Unpaved.MinCover = Num(_unpavedCover, _pav.Unpaved.MinCover);
            _pav.Unpaved.DuctType = _unpavedType.SelectedItem as string ?? "";
            // One unpaved minimum: the cover check takes it from the pavement section, or from networks when no pavement is set.
            _net.CoverMin = _pav.Unpaved.MinCover;
            _pav.CoverUsesEdgeBand = _coverBand.Checked;
            _pav.LabelsUseEdgeBand = _labelBand.Checked;

            void Put(string name, object value)
            {
                foreach (string k in _root.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList()) _root.Remove(k);
                _root[name] = JsonSerializer.SerializeToNode(value, value.GetType(), Json);
            }
            Put("profileViews", _pv);
            Put("networks", _net);
            Put("labels", _lbl);
            Put("pavement", _pav);
            SettingsStore.Save(_doc, _root.ToJsonString(Json));
            _source = "the settings saved in this drawing";
        }

        // ---- Running ------------------------------------------------------------------------------------------------

        private void Run(string command, string mode, Func<string> step)
        {
            Save();
            Cursor = Cursors.WaitCursor;
            _status.Text = $"{mode} running…";
            Refresh();
            string log;
            try { log = step(); }
            catch (Exception ex)
            {
                C3DProLogger.Error($"C3D Pro Run {command} {mode} failed", ex);
                log = $"{command} {mode} failed: {ex.Message}";
            }
            finally { Cursor = Cursors.Default; }
            _lastFile = RunReport.File(command, mode);
            try { System.IO.File.WriteAllText(_lastFile, log); } catch (System.IO.IOException) { _lastFile = null; }
            _openReport.Enabled = _lastFile != null;
            _report.Text = RunReport.Summary(log).Replace("\n", Environment.NewLine);
            _status.Text = $"{mode} done" + (mode == "Report" ? " (nothing changed)." : ". One UNDO step per change; the settings are saved in the drawing.");
            try { _doc.Editor.Regen(); } catch (Exception) { }
        }

        private bool Ticked(CheckedListBox list, string what, out string patterns)
        {
            patterns = Chosen(list);
            if (patterns != null) return true;
            MessageBox.Show(this, $"Tick at least one {what}.", Text);
            return false;
        }

        private void RunProfile(string mode)
        {
            if (!Ticked(_alignments, "alignment", out string chosen)) return;
            _pv.Alignments = chosen;
            Run("C3DPROPV", mode, () => ProfileViewsRun.Execute(_doc, _pv, mode, _source));
        }

        private void RunNetworks(string mode)
        {
            if (!Ticked(_networks, "network", out string chosen)) return;
            _net.Networks = chosen;
            Run("C3DPRONET", mode, () => NetworksRun.Execute(_doc, _net, mode, _source));
        }

        private void RunLabels(string mode)
        {
            if (mode != "Rename" && !Ticked(_layouts, "sheet", out string layouts)) return;
            if (!Ticked(_labelNetworks, "network", out string networks)) return;
            if (mode != "Rename") _lbl.Layouts = Chosen(_layouts);
            _lbl.Networks = networks;
            Run("C3DPROLBL", mode, () => LabelsRun.Execute(_doc, _lbl, mode, _source,
                q => MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        }

        // ---- Pages --------------------------------------------------------------------------------------------------

        private static CheckedListBox Picker() => new CheckedListBox { CheckOnClick = true, Height = 190, Dock = DockStyle.Top, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { BackColor = StyleUi.Surface };
            page.Controls.Add(content);
            return page;
        }

        /// <summary>A card with the list, a filter box and All / None.</summary>
        private TableLayoutPanel ListCard(string title, string info, CheckedListBox list)
        {
            TableLayoutPanel card = ToolUi.Card(title, info);
            List<string> all = _names[list];
            HashSet<string> ticked = _ticked[list];
            list.ItemCheck += (s, e) => { string n = (string)list.Items[e.Index]; if (e.NewValue == CheckState.Checked) ticked.Add(n); else ticked.Remove(n); };
            TextBox filter = StyleUi.Box("Filter (part of a name)");
            filter.Width = 260;
            void Show()
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (string n in all.Where(n => filter.Text.Length == 0 || n.IndexOf(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                    list.Items.Add(n, ticked.Contains(n));
                list.EndUpdate();
            }
            filter.TextChanged += (s, e) => Show();
            void SetShown(bool on) { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, on); }
            ToolUi.Wide(card, ToolUi.Inline(filter, StyleUi.Secondary("Tick shown", (s, e) => SetShown(true)), StyleUi.Secondary("Untick shown", (s, e) => SetShown(false))));
            ToolUi.Wide(card, list);
            list.Width = 600;
            list.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            return card;
        }

        private static FlowLayoutPanel Steps(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            foreach (Button b in buttons) { b.Margin = new Padding(0, 0, 8, 6); row.Controls.Add(b); }
            return row;
        }

        private void Surfaces(ComboBox box, string chosen, bool none = true, bool? existingRole = null)
        {
            if (none) box.Items.Add("(none)");
            foreach (string s in _surfaces) box.Items.Add(s);
            // A saved name the drawing does not have is never shown as chosen (settings carried over from another drawing: the MV
            // drawing showed the ICT drawing's surfaces and drew no cover DIMs, 2026-11-21): the drawing's own surface whose name
            // says what it is is chosen instead, and said.
            if (!string.IsNullOrEmpty(chosen) && !_surfaces.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            {
                string found = existingRole.HasValue ? Features.ProfileTool.SurfaceGuess.Pick(existingRole.Value, chosen, _surfaces) : null;
                _surfaceNotes.Add($"\"{chosen}\" is not in this drawing" + (found != null ? $"; \"{found}\" chosen instead (check it)." : "; choose one."));
                chosen = found ?? "";
            }
            else if (string.IsNullOrEmpty(chosen) && existingRole.HasValue)
                chosen = Features.ProfileTool.SurfaceGuess.Pick(existingRole.Value, null, _surfaces) ?? "";
            ToolUi.Select(box, string.IsNullOrEmpty(chosen) ? (none ? "(none)" : null) : chosen);
        }

        private readonly List<string> _surfaceNotes = new List<string>();
        private readonly CheckBox _createTop = new CheckBox { Text = "Create missing TOP profiles (Fit TOP and All)", AutoSize = true };
        private readonly CheckBox _topInside = new CheckBox { Text = "TOP follows the cable inside an encasement (not the encasement)", AutoSize = true };

        private Control ProfilePage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            ToolUi.Add(stack, ListCard("Alignments", "The alignments with profile views. Each step works on the ticked ones.", _alignments));

            TableLayoutPanel steps = ToolUi.Card("Steps", "Check lists what each step would do and changes nothing. All runs TOP, Match, Split, DIMs and Pavement labels in that order.");
            ToolUi.Wide(steps, Steps(
                StyleUi.Secondary("Check", (s, e) => RunProfile("Report")),
                StyleUi.Secondary("Fit TOP", (s, e) => RunProfile("Top")),
                StyleUi.Secondary("Match views", (s, e) => RunProfile("Match")),
                StyleUi.Secondary("Split long views", (s, e) => RunProfile("Split")),
                StyleUi.Secondary("Cover DIMs", (s, e) => RunProfile("Dims")),
                StyleUi.Secondary("Pavement labels", (s, e) => RunProfile("Pavement")),
                StyleUi.Primary("All", (s, e) => RunProfile("All"))));
            ToolUi.Add(stack, steps);

            TableLayoutPanel set = ToolUi.Card("Settings");
            Surfaces(_existing, _pv.ExistingSurface, true, true);
            Surfaces(_proposed, _pv.ProposedSurface, true, false);
            _createTop.Checked = _pv.CreateMissingTop;
            _proposedProfile.Text = _pv.ProposedProfile ?? "";
            _proposedProfile.Width = 200;
            _matchTemplate.Text = _pv.MatchTemplate ?? "";
            _matchTemplate.Width = 260;
            _splitLength.Text = ToolUi.Format(_pv.SplitLength);
            _splitGap.Text = ToolUi.Format(_pv.SplitGap);
            ToolUi.Field(set, "Existing surface", _existing);
            ToolUi.Field(set, "Proposed surface", _proposed);
            if (_surfaceNotes.Count > 0) ToolUi.Wide(set, new Label { AutoSize = true, ForeColor = StyleUi.Danger, Text = "Surfaces: " + string.Join(" ", _surfaceNotes), Margin = new Padding(3, 2, 3, 6) });
            ToolUi.Wide(set, _createTop);
            _topInside.Checked = _pv.TopInsideEncasement;
            ToolUi.Wide(set, _topInside);
            ToolUi.Field(set, "Proposed profile (name contains)", _proposedProfile);
            ToolUi.Wide(set, ToolUi.Note("Pavement labels sit on the alignment's proposed profile; without one, on the proposed surface."));
            ToolUi.Field(set, "Match: template view", _matchTemplate);
            ToolUi.Field(set, "Split views longer than", ToolUi.WithUnit(_splitLength, "m"));
            ToolUi.Field(set, "Gap between split parts", ToolUi.WithUnit(_splitGap, "m"));
            ToolUi.Add(stack, set);
            return host;
        }

        private Control NetworkPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            ToolUi.Add(stack, ListCard("Networks", "Data references are listed but never changed.", _networks));

            TableLayoutPanel steps = ToolUi.Card("Steps", "Check runs every step as a preview and changes nothing. Cover only reads. All runs Repair, Duplicates, Rims, Fit, then Cover.");
            ToolUi.Wide(steps, Steps(
                StyleUi.Secondary("Check", (s, e) => RunNetworks("Report")),
                StyleUi.Secondary("Repair", (s, e) => RunNetworks("Repair")),
                StyleUi.Secondary("Delete duplicates", (s, e) => RunNetworks("Duplicates")),
                StyleUi.Secondary("Rims to surface", (s, e) => RunNetworks("Rims")),
                StyleUi.Secondary("Fit pipe ends", (s, e) => RunNetworks("Fit")),
                StyleUi.Secondary("Cover check", (s, e) => RunNetworks("Cover")),
                StyleUi.Primary("All", (s, e) => RunNetworks("All"))));
            ToolUi.Add(stack, steps);

            TableLayoutPanel set = ToolUi.Card("Settings", "Cover is measured to the top of the encasement. The minimum cover, paved and unpaved, is set on the Pavement tab.");
            Surfaces(_rimSurface, _net.Surface, true, false);
            foreach (string f in new[] { "InnerWall", "OuterWall", "Center" }) _fitFace.Items.Add(f);
            ToolUi.Select(_fitFace, _net.FitFace);
            _coverMax.Text = ToolUi.Format(_net.CoverMax);
            _maxSlope.Text = ToolUi.Format(_net.CoverMaxSlope);
            _clearance.Text = ToolUi.Format(_net.CoverClearance);
            ToolUi.Field(set, "Surface (rims, cover)", _rimSurface);
            ToolUi.Field(set, "Fit pipe ends to", _fitFace);
            ToolUi.Field(set, "Cover maximum", ToolUi.WithUnit(_coverMax, "m"));
            ToolUi.Field(set, "Slope maximum", ToolUi.WithUnit(_maxSlope, "%"));
            ToolUi.Field(set, "Crossing clearance", ToolUi.WithUnit(_clearance, "m"));
            ToolUi.Add(stack, set);
            return host;
        }

        private Control LabelPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            ToolUi.Add(stack, ListCard("Sheets", "The layouts to label and arrange.", _layouts));
            ToolUi.Add(stack, ListCard("Networks", "Whose labels may be added, moved or reset, and whose parts Rename names. Labels of other networks stay and are avoided.", _labelNetworks));

            TableLayoutPanel steps = ToolUi.Card("Steps", "Check lists the labels the rules would add and the overlaps on the sheets, and changes nothing. " +
                "Rename names the parts with the templates set in Annotation Studio (Rename parts); it lists the new names and asks before renaming.");
            ToolUi.Wide(steps, Steps(
                StyleUi.Secondary("Check", (s, e) => RunLabels("Report")),
                StyleUi.Secondary("Add labels", (s, e) => RunLabels("Add")),
                StyleUi.Primary("Arrange", (s, e) => RunLabels("Arrange")),
                StyleUi.Secondary("Reset", (s, e) => RunLabels("Reset")),
                StyleUi.Secondary("Rename parts", (s, e) => RunLabels("Rename"))));
            ToolUi.Add(stack, steps);

            TableLayoutPanel set = ToolUi.Card("Settings", "0 = as set in Annotation Studio.");
            _maxDistance.Text = ToolUi.Format(_lbl.MaxDistance);
            _gap.Text = ToolUi.Format(_lbl.Gap);
            ToolUi.Field(set, "Longest leader", ToolUi.WithUnit(_maxDistance, "mm on paper"));
            ToolUi.Field(set, "Space round a label", ToolUi.WithUnit(_gap, "mm on paper"));
            ToolUi.Add(stack, set);
            return host;
        }

        private Control PavementPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();
            TableLayoutPanel zones = ToolUi.Card("Pavement kinds", "Each kind is a surface that covers exactly its paved area. Where two overlap, the upper row wins; everywhere else is unpaved. " +
                "Used by the cover check (Networks) and the start / end of pavement labels (Profile views).");
            var surface = new DataGridViewComboBoxColumn { HeaderText = "Surface", FlatStyle = FlatStyle.Flat, FillWeight = 180 };
            foreach (string s in _surfaces.Concat(_pav.Zones.Select(z => z.Surface ?? "")).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) surface.Items.Add(s);
            var type = new DataGridViewComboBoxColumn { HeaderText = "Duct type", FlatStyle = FlatStyle.Flat, FillWeight = 60 };
            foreach (string t in new[] { "", "A", "B" }) type.Items.Add(t);
            _zones.Columns.Add(surface);
            _zones.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name on labels", FillWeight = 140 });
            _zones.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Min cover (m)", FillWeight = 70 });
            _zones.Columns.Add(type);
            _zones.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Edge band (m)", FillWeight = 70, ToolTipText = "Shoulder and blast pad inside the surface edge (taxiway: 10.5 + 6 = 16.5)" });
            _zones.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Max cover (m, 0 = general)", FillWeight = 90 });
            _zones.AllowUserToAddRows = true;
            _zones.AllowUserToDeleteRows = true;
            _zones.MultiSelect = false;
            _zones.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _zones.Dock = DockStyle.None;
            _zones.Height = 170;
            _zones.Width = 860;
            _zones.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _zones.DataError += (s, e) => e.ThrowException = false;
            foreach (PavementZone z in _pav.Zones)
                _zones.Rows.Add(z.Surface, z.Name, ToolUi.Format(z.MinCover), type.Items.Contains(z.DuctType ?? "") ? z.DuctType ?? "" : "", ToolUi.Format(z.EdgeBand), ToolUi.Format(z.MaxCover));
            ToolUi.Wide(zones, _zones);
            ToolUi.Wide(zones, ToolUi.Note("Add a kind in the empty last row; select a row and press Delete to remove it."));
            ToolUi.Add(stack, zones);

            TableLayoutPanel unpaved = ToolUi.Card("Unpaved");
            _unpavedCover.Text = ToolUi.Format(_pav.Unpaved.MinCover);
            foreach (string t in new[] { "", "A", "B" }) _unpavedType.Items.Add(t);
            ToolUi.Select(_unpavedType, _pav.Unpaved.DuctType ?? "");
            ToolUi.Field(unpaved, "Min cover", ToolUi.WithUnit(_unpavedCover, "m"));
            ToolUi.Field(unpaved, "Duct type", _unpavedType);
            ToolUi.Add(stack, unpaved);

            TableLayoutPanel band = ToolUi.Card("Edge band (shoulder, blast pad)", "The band width is set per kind above.");
            _coverBand.Checked = _pav.CoverUsesEdgeBand;
            _labelBand.Checked = _pav.LabelsUseEdgeBand;
            ToolUi.Wide(band, _coverBand);
            ToolUi.Wide(band, _labelBand);
            ToolUi.Add(stack, band);

            TableLayoutPanel use = ToolUi.Card("Use these settings",
                "Check cover lists every duct against the cover and duct type of the pavement over it, for the networks ticked on the Networks tab; it changes nothing. " +
                "Label pavement edges puts the start / end of pavement labels on the proposed profile, in the profile views of the alignments ticked on the Profile views tab.");
            ToolUi.Wide(use, Steps(
                StyleUi.Secondary("Check cover under pavement", (s, e) => RunNetworks("Cover")),
                StyleUi.Primary("Label pavement edges", (s, e) => RunProfile("Pavement"))));
            ToolUi.Add(stack, use);
            return host;
        }
    }
}
