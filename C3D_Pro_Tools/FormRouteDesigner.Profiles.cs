using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Label = System.Windows.Forms.Label;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace C3DProTools
{
    /// <summary>
    /// The Profiles section: add the gravity and pressure parts lying along alignments to their profile views (reviewed in
    /// plan first), or create an alignment, surface profiles and a profile view from a run of selected parts.
    /// </summary>
    internal sealed partial class FormRouteDesigner
    {
        // Parts whose station range misses a profile view's range by more than this are not added to it.
        private const double StationRangeTolerance = 0.001;
        // Preview colours (AutoCAD colour indexes).
        private const short ColorRunPath = 4, ColorStart = 1, ColorPi = 7, ColorManholePi = 3, ColorPipeEndPi = 2, ColorGapPi = 6, ColorUnused = 8, ColorCandidate = 2;

        private readonly Dictionary<string, ObjectId> _networks = new Dictionary<string, ObjectId>();
        private readonly Dictionary<string, ObjectId> _alignments = new Dictionary<string, ObjectId>();
        private CheckListFilter _networkFilter, _alignmentFilter;
        private readonly Label _lblNetworks = new Label { AutoSize = true, Font = StyleUi.Bold }, _lblAlignments = new Label { AutoSize = true, Font = StyleUi.Bold };
        private readonly TextBox _txtTolerance = ToolUi.Number(0.5);
        private readonly CheckBox _chkIncludeCrossings = ToolUi.Check("Also add pipes that cross the alignment");
        private readonly ComboBox _cbStartMode = ToolUi.Combo(300);
        private readonly CheckBox _chkCheckFlow = ToolUi.Check("Check the run's flow direction and offer to make it consistent");
        private readonly TextBox _txtTopPrefix = new TextBox { Width = 160 };
        private readonly ComboBox _cbTopStyle = ToolUi.Combo(300), _cbTopLabels = ToolUi.Combo(300);
        private readonly RadioButton _rbTopPerRoute = new RadioButton { Text = "One profile per route (parallel pipes each get their own)", AutoSize = true };
        private readonly RadioButton _rbTopHighest = new RadioButton { Text = "One profile along the highest top of all pipes in the view", AutoSize = true };
        private readonly Dictionary<string, ObjectId> _profileStyles = new Dictionary<string, ObjectId>(), _profileLabelSets = new Dictionary<string, ObjectId>();

        private sealed class RunTotals
        {
            public int Processed, Skipped, NoParts;
            public int Added, AlreadyShown, OutsideRange, Failed;
        }

        private Control BuildProfilesPage()
        {
            (Panel host, TableLayoutPanel stack) = ToolUi.Page();

            TableLayoutPanel networks = ToolUi.Card("Pipe networks");
            ToolUi.Wide(networks, ToolUi.Note("Searched for parts along alignments, and for other parts along a newly created alignment."));
            ToolUi.Wide(networks, CheckList(_lblNetworks, out TextBox netSearch, out CheckedListBox netList, out Button netAll, out Button netNone,
                StyleUi.Secondary("Pick in drawing…", (s, e) => PickFromDrawing("\nSelect pipes, structures or pressure parts whose networks to use: ", _networks, _networkFilter,
                    obj => obj is Part part ? part.NetworkId : obj is PressurePart pp ? pp.NetworkId : ObjectId.Null, "network"))));
            ToolUi.Add(stack, networks);

            TableLayoutPanel project = ToolUi.Card("Add parts to existing profile views",
                "For each ticked alignment the parts lying along it are found and highlighted in plan for review; then you pick its profile view(s) and they are drawn.");
            ToolUi.Wide(project, CheckList(_lblAlignments, out TextBox alSearch, out CheckedListBox alList, out Button alAll, out Button alNone,
                StyleUi.Secondary("Pick in drawing…", (s, e) => PickFromDrawing("\nSelect alignments or their profile views: ", _alignments, _alignmentFilter,
                    obj => obj is Alignment a ? a.ObjectId : obj is ProfileView v ? v.AlignmentId : ObjectId.Null, "alignment"))));
            ToolUi.Field(project, "Parts within", ToolUi.WithUnit(_txtTolerance, "m of the alignment"));
            ToolUi.Wide(project, _chkIncludeCrossings);
            Button run = StyleUi.Primary("Detect and draw in profile views…", (s, e) => RunProject());
            run.Margin = new Padding(0, 8, 0, 0);
            ToolUi.Wide(project, run);
            ToolUi.Add(stack, project);

            TableLayoutPanel create = ToolUi.Card("Create an alignment and profile view from parts",
                "Select a run of pipes and structures; the alignment follows the pipes (through manhole centres where the pipes point at them), " +
                "not their flow direction. You confirm the path, then choose the styles, labels and where the view goes.");
            _cbStartMode.Items.AddRange(new object[] { "At the first pipe I pick", "At the upstream end (higher invert)", "In the flow direction of most pipes" });
            ToolUi.Field(create, "Stationing starts", _cbStartMode);
            ToolUi.Wide(create, _chkCheckFlow);
            Button createButton = StyleUi.Primary("Select parts and create…", (s, e) => RunCreate());
            createButton.Margin = new Padding(0, 8, 0, 0);
            ToolUi.Wide(create, createButton);
            ToolUi.Add(stack, create);

            TableLayoutPanel top = ToolUi.Card("Top-of-pipe profile",
                "Pick profile views: a layout profile is drawn along the outside top of the pipes shown in each (top of wall or encasement). " +
                "Pipes crossing the alignment are left out. Running it again updates the profiles of the same name.");
            ToolUi.Wide(top, _rbTopPerRoute);
            ToolUi.Wide(top, _rbTopHighest);
            ToolUi.Field(top, "Name starts with", _txtTopPrefix, _tips, "Followed by the route's first and last pipe, or the view's name for one profile.");
            ToolUi.Field(top, "Profile style", _cbTopStyle);
            ToolUi.Field(top, "Label set", _cbTopLabels);
            Button topButton = StyleUi.Primary("Select profile views and create…", (s, e) => RunTopProfiles());
            topButton.Margin = new Padding(0, 8, 0, 0);
            ToolUi.Wide(top, topButton);
            ToolUi.Add(stack, top);
            PopulateProfileStyles();

            _networkFilter = new CheckListFilter(netSearch, netList, netAll, netNone);
            _alignmentFilter = new CheckListFilter(alSearch, alList, alAll, alNone);
            _networkFilter.CheckedChanged += (s, e) => UpdateHeaders();
            _alignmentFilter.CheckedChanged += (s, e) => UpdateHeaders();
            PopulateNetworks();
            PopulateAlignments();
            UpdateHeaders();
            return host;
        }

        /// <summary>A heading, a search box, the checked list and its All / None / Pick buttons.</summary>
        private static Control CheckList(Label heading, out TextBox search, out CheckedListBox list, out Button all, out Button none, Button pick)
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            t.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(SizeType.Percent, 100));
            search = new TextBox { PlaceholderText = "Search…", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            list = new CheckedListBox { Dock = DockStyle.Fill, Height = 150, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0) };
            all = StyleUi.Secondary("All");
            none = StyleUi.Secondary("None");
            heading.Margin = new Padding(0, 4, 0, 0);
            t.Controls.Add(heading, 0, 0);
            t.Controls.Add(search, 0, 1);
            t.Controls.Add(list, 0, 2);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            FlowLayoutPanel buttons = StyleUi.Row(all, none, pick);
            buttons.WrapContents = false;
            t.Controls.Add(buttons, 0, 3);
            return t;
        }

        private void ApplyProfileSettings()
        {
            _txtTolerance.Text = ToolUi.Format(_settings.Tolerance);
            _chkIncludeCrossings.Checked = _settings.IncludeCrossings;
            _cbStartMode.SelectedIndex = Math.Max(0, Math.Min(2, (int)_settings.StartMode));
            _chkCheckFlow.Checked = _settings.CheckFlow;
            _txtTopPrefix.Text = _settings.TopPrefix ?? "";
            (_settings.TopHighest ? _rbTopHighest : _rbTopPerRoute).Checked = true;
            ToolUi.Select(_cbTopStyle, _settings.TopStyle);
            ToolUi.Select(_cbTopLabels, _settings.TopLabelSet);
        }

        private void SaveProfileSettings()
        {
            if (ToolUi.TryNumber(_txtTolerance, out double tol) && tol > 0) _settings.Tolerance = tol;
            _settings.IncludeCrossings = _chkIncludeCrossings.Checked;
            _settings.StartMode = (RunStartMode)Math.Max(0, _cbStartMode.SelectedIndex);
            _settings.CheckFlow = _chkCheckFlow.Checked;
            _settings.TopPrefix = _txtTopPrefix.Text;
            _settings.TopHighest = _rbTopHighest.Checked;
            _settings.TopStyle = _cbTopStyle.SelectedItem as string;
            _settings.TopLabelSet = _cbTopLabels.SelectedItem as string;
            if (ToolUi.TryNumber(_txtGap, out double gap) && gap > 0) _settings.GapLimit = gap;
            if (ToolUi.TryNumber(_txtCenterTol, out double center) && center > 0) _settings.CenterTolerance = center;
            _settings.Save();
        }

        private void UpdateHeaders()
        {
            _lblNetworks.Text = $"Networks ({_networkFilter.CheckedItems.Count} of {_networkFilter.Count} ticked)";
            _lblAlignments.Text = _alignmentFilter.Count == 0 ? "Alignments: none in this drawing has a profile view"
                : $"Alignments with profile views ({_alignmentFilter.CheckedItems.Count} of {_alignmentFilter.Count} ticked)";
        }

        private void PopulateNetworks()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            List<string> names = new List<string>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId netId in doc.GetPipeNetworkIds())
                    if (tr.GetObject(netId, OpenMode.ForRead) is Network network) names.Add(AddUnique(_networks, $"{network.Name} [Gravity]", netId));
                foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
                    if (tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork network) names.Add(AddUnique(_networks, $"{network.Name} [Pressure]", netId));
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            _networkFilter.SetItems(names, true);
        }

        private void PopulateAlignments()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            List<string> names = new List<string>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                // Siteless alignments (including data-shortcut references) plus those that live in sites.
                List<ObjectId> alignmentIds = new List<ObjectId>();
                foreach (ObjectId id in doc.GetAlignmentIds()) alignmentIds.Add(id);
                foreach (ObjectId siteId in doc.GetSiteIds())
                    if (tr.GetObject(siteId, OpenMode.ForRead) is Site site)
                        foreach (ObjectId id in site.GetAlignmentIds()) alignmentIds.Add(id);
                foreach (ObjectId id in alignmentIds)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Alignment alignment)) continue;
                    int views = alignment.GetProfileViewIds().Count;
                    if (views == 0) continue;
                    names.Add(AddUnique(_alignments, $"{alignment.Name}  ({views} profile view{(views == 1 ? "" : "s")})", id));
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            _alignmentFilter.SetItems(names, false);
        }

        /// <summary>Re-reads the alignment list (a new alignment with a profile view now belongs in it) without losing ticks.</summary>
        private void PopulateAlignmentsKeepingTicks()
        {
            List<string> ticked = _alignmentFilter.CheckedItems;
            _alignments.Clear();
            PopulateAlignments();
            _alignmentFilter.SetCheckedOnly(ticked);
        }

        private static string AddUnique(Dictionary<string, ObjectId> map, string name, ObjectId id)
        {
            string key = name;
            for (int i = 2; map.ContainsKey(key); i++) key = $"{name} #{i}";
            map[key] = id;
            return key;
        }

        /// <summary>Lets the user select objects in the drawing and ticks exactly the list entries they resolve to.</summary>
        private void PickFromDrawing(string prompt, Dictionary<string, ObjectId> map, CheckListFilter filter, Func<DBObject, ObjectId> resolve, string what)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            Dictionary<ObjectId, string> namesById = new Dictionary<ObjectId, string>();
            foreach (KeyValuePair<string, ObjectId> kvp in map) namesById[kvp.Value] = kvp.Key;
            Hide();
            try
            {
                PromptSelectionResult psr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = prompt });
                if (psr.Status != PromptStatus.OK) return;
                HashSet<string> picked = new HashSet<string>();
                int unlisted = 0;
                using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        ObjectId target = resolve(tr.GetObject(id, OpenMode.ForRead));
                        if (target.IsNull) continue;
                        if (namesById.TryGetValue(target, out string name)) picked.Add(name); else unlisted++;
                    }
                if (picked.Count == 0)
                {
                    ed.WriteMessage(unlisted > 0 ? $"\nThe selected {what}(s) are not in the list (alignments need at least one profile view). Ticks unchanged."
                                                 : $"\nNothing in the selection belongs to a {what}. Ticks unchanged.");
                    return;
                }
                filter.SetCheckedOnly(picked);
                ed.WriteMessage($"\nTicked {picked.Count} {what}(s) from the selection." + (unlisted > 0 ? $" {unlisted} selected {what}(s) are not in the list and were ignored." : ""));
            }
            finally { Show(); }
        }

        private void PopulateProfileStyles()
        {
            CivilDocument doc = CivilApplication.ActiveDocument;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in doc.Styles.ProfileStyles)
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.StyleBase s) _profileStyles[s.Name] = id;
                foreach (ObjectId id in doc.Styles.LabelSetStyles.ProfileLabelSetStyles)
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.StyleBase s) _profileLabelSets[s.Name] = id;
            }
            _cbTopStyle.Items.AddRange(_profileStyles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray<object>());
            _cbTopLabels.Items.AddRange(_profileLabelSets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray<object>());
        }

        // =====================================================================================
        // Top-of-pipe profiles
        // =====================================================================================

        private void RunTopProfiles()
        {
            SaveProfileSettings();
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            List<ObjectId> views = new List<ObjectId>();
            Hide();
            try
            {
                PromptSelectionResult psr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect profile views (other objects are ignored): " });
                if (psr.Status != PromptStatus.OK) return;
                views.AddRange(psr.Value.GetObjectIds());
            }
            finally { Show(); }
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                views.RemoveAll(id => !(tr.GetObject(id, OpenMode.ForRead) is ProfileView));
            if (views.Count == 0) { MessageBox.Show(this, "The selection has no profile views.", Text); return; }
            TopProfileOptions o = new TopProfileOptions
            {
                Prefix = _txtTopPrefix.Text, Highest = _rbTopHighest.Checked,
                StyleId = _cbTopStyle.SelectedItem is string st && _profileStyles.TryGetValue(st, out ObjectId sid) ? sid : ObjectId.Null,
                LabelSetId = _cbTopLabels.SelectedItem is string ls && _profileLabelSets.TryGetValue(ls, out ObjectId lid) ? lid : ObjectId.Null
            };
            TopProfileResult total = new TopProfileResult();
            try
            {
                using (acDoc.LockDocument())
                using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument doc = CivilDocument.GetCivilDocument(acDoc.Database);
                    foreach (ObjectId v in views)
                    {
                        if (!(tr.GetObject(v, OpenMode.ForRead) is ProfileView)) continue;
                        TopProfileResult r = TopOfPipeProfile.Create(tr, doc, v, o);
                        total.Created += r.Created; total.Updated += r.Updated;
                        total.Names.AddRange(r.Names); total.Notes.AddRange(r.Notes);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex) { MessageBox.Show(this, "Creating the profiles failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            string text = $"Created {total.Created} and updated {total.Updated} top-of-pipe profile(s) in {views.Count} view(s)."
                + (total.Names.Count > 0 ? "\n\n" + string.Join("\n", total.Names.Take(20)) + (total.Names.Count > 20 ? "\n…" : "") : "")
                + (total.Notes.Count > 0 ? "\n\n" + string.Join("\n", total.Notes) : "");
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================================
        // Add parts to existing profile views
        // =====================================================================================

        private void RunProject()
        {
            if (!ToolUi.TryNumber(_txtTolerance, out double tolerance) || tolerance <= 0)
            {
                MessageBox.Show(this, "Enter a positive distance from the alignment (in metres).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SaveProfileSettings();
            List<ObjectId> networkIds = _networkFilter.CheckedItems.ConvertAll(name => _networks[name]);
            List<string> alignmentNames = _alignmentFilter.CheckedItems;
            if (networkIds.Count == 0 || alignmentNames.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one pipe network and one alignment.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Editor ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
            RunTotals totals = new RunTotals();
            bool cancelled = false;
            // Hidden for the whole run, so the drawing stays visible while reviewing and picking profile views.
            Hide();
            try
            {
                for (int i = 0; i < alignmentNames.Count; i++)
                    if (!ProcessAlignment(_alignments[alignmentNames[i]], i + 1, alignmentNames.Count, networkIds, tolerance, _chkIncludeCrossings.Checked, totals, ed)) { cancelled = true; break; }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Adding parts stopped with an error. Alignments finished before it keep their changes.\n\n{ex.GetType().Name}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { Show(); }

            MessageBox.Show(this,
                (cancelled ? "Run cancelled. Alignments finished before cancelling keep their changes.\n\n" : "Done.\n\n") +
                $"Alignments processed: {totals.Processed}\nAlignments skipped: {totals.Skipped}\nAlignments with no parts found: {totals.NoParts}\n\n" +
                $"Parts added to profile views: {totals.Added}\nAlready shown (skipped): {totals.AlreadyShown}\n" +
                $"Outside a profile view's station range: {totals.OutsideRange}\nFailed: {totals.Failed}" + (totals.Failed > 0 ? "\n\nSee the command line for details." : ""),
                "Add parts to profile views", MessageBoxButtons.OK, totals.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>Detect, review, pick views and draw for one alignment. Returns false when the user cancels the run.</summary>
        private bool ProcessAlignment(ObjectId alignmentId, int index, int count, List<ObjectId> networkIds, double tolerance, bool includeCrossings, RunTotals totals, Editor ed)
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            string alignmentName;
            Extents3d zoomExtents;
            HashSet<ObjectId> profileViewIds = new HashSet<ObjectId>();
            List<DetectedPart> parts;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Alignment alignment = (Alignment)tr.GetObject(alignmentId, OpenMode.ForRead);
                alignmentName = alignment.Name;
                zoomExtents = alignment.GeometricExtents;
                foreach (ObjectId pvId in alignment.GetProfileViewIds()) profileViewIds.Add(pvId);
                parts = ProfilePartDetection.Detect(tr, alignment, networkIds, tolerance, includeCrossings, profileViewIds);
                foreach (DetectedPart part in parts)
                {
                    string from = alignment.GetStationStringWithEquations(part.StationMin);
                    part.StationText = part.StationMax - part.StationMin < StationRangeTolerance ? from : $"{from} - {alignment.GetStationStringWithEquations(part.StationMax)}";
                }
                tr.Commit();
            }
            if (parts.Count == 0)
            {
                ed.WriteMessage($"\n[Profiles] {alignmentName}: no parts found within {tolerance:0.###} m.");
                totals.NoParts++;
                return true;
            }

            using (PartHighlighter highlighter = new PartHighlighter(parts))
            {
                foreach (DetectedPart part in parts) foreach (Point3d p in part.Path) zoomExtents.AddPoint(p);
                ViewHelper.ZoomTo(zoomExtents);
                ed.UpdateScreen();
                DialogResult choice;
                bool useAllViews;
                using (FormPartsReview review = new FormPartsReview(alignmentName, index, count, profileViewIds.Count, parts, highlighter))
                {
                    choice = review.ShowDialog();
                    useAllViews = review.UseAllProfileViews;
                }
                if (choice == DialogResult.Cancel) return false;
                List<DetectedPart> chosen = parts.FindAll(p => p.Include);
                if (choice != DialogResult.OK || chosen.Count == 0) { ed.WriteMessage($"\n[Profiles] {alignmentName}: skipped."); totals.Skipped++; return true; }

                List<ObjectId> views = useAllViews ? new List<ObjectId>(profileViewIds) : PickProfileViews(ed, alignmentName, profileViewIds);
                if (views.Count == 0) { ed.WriteMessage($"\n[Profiles] {alignmentName}: no profile view chosen, skipped."); totals.Skipped++; return true; }
                AddPartsToViews(chosen, views, totals, ed);
                totals.Processed++;
            }
            return true;
        }

        /// <summary>Zooms to the alignment's profile views and lets the user select one or more of them.</summary>
        private static List<ObjectId> PickProfileViews(Editor ed, string alignmentName, HashSet<ObjectId> profileViewIds)
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            // A default Extents3d already contains the origin, so start from the first view's own extents.
            Extents3d? extents = null;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId pvId in profileViewIds)
                {
                    try
                    {
                        Extents3d viewExtents = ((Entity)tr.GetObject(pvId, OpenMode.ForRead)).GeometricExtents;
                        if (extents.HasValue) viewExtents.AddExtents(extents.Value);
                        extents = viewExtents;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
            if (extents.HasValue) ViewHelper.ZoomTo(extents.Value, 1.1, 10.0);

            RXClass profileViewClass = RXObject.GetClass(typeof(ProfileView));
            PromptSelectionOptions pso = new PromptSelectionOptions { MessageForAdding = $"\nSelect profile view(s) of alignment \"{alignmentName}\" to draw the parts in: " };
            while (true)
            {
                PromptSelectionResult psr = ed.GetSelection(pso);
                if (psr.Status != PromptStatus.OK) return new List<ObjectId>();
                List<ObjectId> chosen = new List<ObjectId>();
                int otherViews = 0;
                foreach (ObjectId id in psr.Value.GetObjectIds())
                {
                    if (profileViewIds.Contains(id)) chosen.Add(id);
                    else if (id.ObjectClass.IsDerivedFrom(profileViewClass)) otherViews++;
                }
                if (otherViews > 0) ed.WriteMessage($"\n{otherViews} selected profile view(s) belong to another alignment and were ignored.");
                if (chosen.Count > 0) return chosen;
                ed.WriteMessage($"\nNo profile view of \"{alignmentName}\" was selected. Try again, or press Esc to skip this alignment.");
            }
        }

        private static void AddPartsToViews(List<DetectedPart> parts, List<ObjectId> views, RunTotals totals, Editor ed)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId pvId in views)
                {
                    ProfileView view = (ProfileView)tr.GetObject(pvId, OpenMode.ForRead);
                    double start = view.StationStart, end = view.StationEnd;
                    foreach (DetectedPart part in parts)
                    {
                        if (part.StationMax < start - StationRangeTolerance || part.StationMin > end + StationRangeTolerance) { totals.OutsideRange++; continue; }
                        try
                        {
                            DBObject obj = tr.GetObject(part.Id, OpenMode.ForWrite);
                            if (IsShownIn(obj, pvId)) { totals.AlreadyShown++; continue; }
                            if (obj is Part gravityPart) gravityPart.AddToProfileView(pvId);
                            else if (obj is PressurePart pressurePart) pressurePart.AddToProfileView(pvId);
                            totals.Added++;
                        }
                        catch (System.Exception ex)
                        {
                            ed.WriteMessage($"\n[Error] Adding {part.Kind} <{part.Name}> to profile view <{view.Name}>: {ex.Message}");
                            totals.Failed++;
                        }
                    }
                }
                tr.Commit();
            }
        }

        private static bool IsShownIn(DBObject obj, ObjectId profileViewId)
        {
            ObjectIdCollection views = obj is Part gravityPart ? gravityPart.GetProfileViewsDisplayingMe()
                                     : obj is PressurePart pressurePart ? pressurePart.GetProfileViewsDisplayingMe() : null;
            return views != null && views.Contains(profileViewId);
        }

        // =====================================================================================
        // Create an alignment and profile view from parts
        // =====================================================================================

        private void RunCreate()
        {
            if (!ToolUi.TryNumber(_txtTolerance, out double tolerance) || tolerance <= 0) tolerance = _settings.Tolerance;
            if (!ReadRouteSettings(out double gap, out double center)) return;
            SaveProfileSettings();
            List<ObjectId> networkIds = _networkFilter.CheckedItems.ConvertAll(name => _networks[name]);
            string summary = null;
            Hide();
            ProfileCreation.Step = "reading the selected parts";
            try
            {
                summary = CreateFromParts(tolerance, networkIds, gap, center, (RunStartMode)_cbStartMode.SelectedIndex, _chkCheckFlow.Checked);
            }
            catch (System.Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[Error] Create alignment, while {ProfileCreation.Step}:\n{ex}\n");
                MessageBox.Show($"Creating the alignment stopped with an error while {ProfileCreation.Step}. Steps finished before it keep their changes.\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\nThe full details are on the command line.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Show(); }
            if (summary != null) MessageBox.Show(this, summary, "Create alignment and profile view", MessageBoxButtons.OK, MessageBoxIcon.Information);
            PopulateAlignmentsKeepingTicks();
        }

        /// <summary>Returns the summary to show, or null when the user cancelled (nothing was created).</summary>
        private string CreateFromParts(double tolerance, List<ObjectId> networkIds, double gapLimit, double centerTolerance, RunStartMode startMode, bool checkFlow)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            Database db = acDoc.Database;

            // 1. Selection: the start pipe first (when it decides the stationing), then the rest of the run.
            List<ObjectId> selection = new List<ObjectId>();
            ObjectId startPipeId = ObjectId.Null;
            Point3d pickPoint = Point3d.Origin;
            if (startMode == RunStartMode.FirstPipePicked)
            {
                PromptEntityOptions peo = new PromptEntityOptions("\nSelect the FIRST pipe of the run (pick near the end where stationing starts): ");
                peo.SetRejectMessage("\nSelect a gravity or pressure pipe.");
                peo.AddAllowedClass(typeof(Pipe), false);
                peo.AddAllowedClass(typeof(PressurePipe), false);
                PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return null;
                startPipeId = per.ObjectId;
                pickPoint = per.PickedPoint;
                selection.Add(startPipeId);
                PromptSelectionResult rest = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect the other pipes and structures / fittings of the run (Enter when done): " });
                if (rest.Status == PromptStatus.Cancel) return null;
                if (rest.Status == PromptStatus.OK) selection.AddRange(rest.Value.GetObjectIds());
            }
            else
            {
                PromptSelectionResult all = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect the pipes and structures / fittings of the run: " });
                if (all.Status != PromptStatus.OK) return null;
                selection.AddRange(all.Value.GetObjectIds());
            }

            // 2. Read the parts and order them into one run.
            PipeRunBuilder builder;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                builder = PipeRunBuilder.Read(tr, selection, gapLimit, centerTolerance);
            if (builder.Pipes.Count == 0) { MessageBox.Show("The selection has no gravity or pressure pipes.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }

            RunStep start = startMode == RunStartMode.FirstPipePicked ? builder.StartFromPick(builder.IndexOf(startPipeId), pickPoint) : builder.LeafStart();
            ProfileCreation.Step = "ordering the pipes into one run";
            List<RunStep> steps = builder.Walk(start, branch => PromptBranch(ed, builder, branch));
            if (steps == null) return null;
            if (startMode == RunStartMode.Upstream && builder.EntryInvert(steps[0]) < builder.ExitInvert(steps[steps.Count - 1])) steps = PipeRunBuilder.Reverse(steps);
            else if (startMode == RunStartMode.MajorityFlow)
            {
                int forward = steps.FindAll(s => s.Forward).Count;
                if (steps.Count - forward > forward) steps = PipeRunBuilder.Reverse(steps);
            }

            PipeRun run = builder.BuildRun(steps);
            if (run.Vertices.Count < 2) { MessageBox.Show("The run is too short to create an alignment.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }

            // 3. Preview the path and its PIs before anything is created.
            ProfileCreation.Step = "previewing the path";
            if (!ConfirmPreview(ed, builder, run)) return null;

            // 4. Optional flow-direction check of the run's gravity pipes.
            int reversedPipes = 0;
            if (checkFlow)
            {
                ProfileCreation.Step = "checking the flow direction";
                reversedPipes = CheckRunFlow(builder, steps, ed);
                if (reversedPipes < 0) return null;
            }

            // 5-7. Styles and labels, the view's insertion point, then the alignment, profiles and view with the parts drawn.
            List<ObjectId> drawIds = new List<ObjectId>();
            foreach (RunStep s in steps) drawIds.Add(builder.Pipes[s.Pipe].Id);
            drawIds.AddRange(builder.SelectedHubIds);
            RunProfileResult created = ProfileCreation.CreateForRun(builder, steps, run, drawIds, _settings, ed);
            if (created == null) return null;

            // 8. Anything else lying along the new alignment.
            int otherParts = 0;
            if (networkIds.Count > 0)
                using (acDoc.LockDocument())
                    otherParts = OfferOtherParts(db, created.AlignmentId, created.AlignmentName, created.ViewId, networkIds, tolerance, _chkIncludeCrossings.Checked, drawIds, created.Setup, created.Parts, ed);

            PartAddResult parts = created.Parts;
            int gapPis = run.Vertices.FindAll(v => v.Kind == RunVertexKind.GapMidpoint).Count;
            int offsetPis = run.Vertices.FindAll(v => v.Kind == RunVertexKind.PipeEnd).Count;
            return $"Alignment \"{created.AlignmentName}\" created with its profile view.\n\n" +
                   $"Pipes in the run: {steps.Count}   PIs: {run.Vertices.Count} (gap midpoints: {gapPis}, pipe-end PIs at off-centre manholes / curves: {offsetPis})\n" +
                   $"Pipes reversed by the flow check: {reversedPipes}\n" +
                   (run.UnusedPipes.Count > 0 ? $"Selected pipes not on the run (not drawn): {run.UnusedPipes.Count}\n" : "") +
                   $"Surface profiles: {created.Profiles.Count} of {created.Setup.Surfaces.Count}\n\n" +
                   $"Parts drawn in the profile view: {parts.Added} (of which other parts found along the alignment: {otherParts})\n" +
                   $"Already shown: {parts.AlreadyShown}   Failed: {parts.Failed}\n" +
                   $"Labels added: {parts.Labels}   Labels failed: {parts.LabelsFailed}" +
                   (parts.Failed + parts.LabelsFailed > 0 ? "\n\nSee the command line for details." : "");
        }

        private int OfferOtherParts(Database db, ObjectId alignmentId, string alignmentName, ObjectId viewId, List<ObjectId> networkIds,
            double tolerance, bool includeCrossings, List<ObjectId> alreadyDrawn, ProfileSetup setup, PartAddResult result, Editor ed)
        {
            List<DetectedPart> others;
            HashSet<ObjectId> views = new HashSet<ObjectId> { viewId };
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Alignment alignment = (Alignment)tr.GetObject(alignmentId, OpenMode.ForRead);
                others = ProfilePartDetection.Detect(tr, alignment, networkIds, tolerance, includeCrossings, views);
                foreach (DetectedPart part in others)
                {
                    string from = alignment.GetStationStringWithEquations(part.StationMin);
                    part.StationText = part.StationMax - part.StationMin < StationRangeTolerance ? from : $"{from} - {alignment.GetStationStringWithEquations(part.StationMax)}";
                }
                tr.Commit();
            }
            HashSet<ObjectId> drawn = new HashSet<ObjectId>(alreadyDrawn);
            others.RemoveAll(p => drawn.Contains(p.Id) || p.AlreadyShownIn > 0);
            if (others.Count == 0) return 0;
            if (MessageBox.Show($"{others.Count} more part(s) were found along the new alignment (for example manholes or pipes that were not selected).\n\n" +
                                "Review them and add the ones you want to the profile view?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 0;
            List<ObjectId> chosen = new List<ObjectId>();
            using (PartHighlighter highlighter = new PartHighlighter(others))
            {
                using (FormPartsReview review = new FormPartsReview(alignmentName, 1, 1, 1, others, highlighter))
                    if (review.ShowDialog() != DialogResult.OK) return 0;
                foreach (DetectedPart part in others) if (part.Include) chosen.Add(part.Id);
            }
            if (chosen.Count == 0) return 0;
            int before = result.Added;
            ProfileCreation.AddAndLabel(db, chosen, viewId, setup, result, ed);
            return result.Added - before;
        }

        /// <summary>
        /// At a junction with several selected pipes continuing: zooms there, highlights the run so far and the candidates,
        /// and asks which pipe to follow. Returns the candidate's index, -1 for "End" (stop the run here), -2 to cancel.
        /// </summary>
        private static int PromptBranch(Editor ed, PipeRunBuilder builder, RunBranch branch)
        {
            using (TransientMarkers markers = new TransientMarkers())
            {
                foreach (RunStep s in branch.RunSoFar) markers.AddPath(builder.Pipes[s.Pipe].Path, ColorRunPath, 0.3);
                foreach (int c in branch.Candidates) markers.AddPath(builder.Pipes[c].Path, ColorCandidate, 0.45);
                markers.AddRing(branch.Point, 1.5, ColorGapPi, 0.25);
                ViewHelper.ZoomTo(branch.Point, 40.0);
                ed.UpdateScreen();
                PromptEntityOptions peo = new PromptEntityOptions($"\nBranch with {branch.Candidates.Count} pipes: select the (yellow) pipe the run continues along [End]: ");
                peo.SetRejectMessage("\nSelect a gravity or pressure pipe.");
                peo.AddAllowedClass(typeof(Pipe), false);
                peo.AddAllowedClass(typeof(PressurePipe), false);
                peo.Keywords.Add("End");
                peo.AppendKeywordsToMessage = false;
                while (true)
                {
                    PromptEntityResult per = ed.GetEntity(peo);
                    if (per.Status == PromptStatus.Keyword) return -1;
                    if (per.Status != PromptStatus.OK) return -2;
                    int index = builder.IndexOf(per.ObjectId);
                    if (branch.Candidates.Contains(index)) return index;
                    ed.WriteMessage("\nThat pipe is not one of the highlighted (yellow) pipes continuing from this junction.");
                }
            }
        }

        private bool ConfirmPreview(Editor ed, PipeRunBuilder builder, PipeRun run)
        {
            using (TransientMarkers markers = new TransientMarkers())
            {
                using (Polyline path = PipeRunBuilder.ToPolyline(run)) markers.AddPolyline(path, ColorRunPath, 0.35);
                foreach (int p in run.UnusedPipes) markers.AddPath(builder.Pipes[p].Path, ColorUnused, 0.3);
                for (int i = 0; i < run.Vertices.Count; i++)
                {
                    RunVertex v = run.Vertices[i];
                    short color = v.Kind == RunVertexKind.ManholeCenter ? ColorManholePi : v.Kind == RunVertexKind.PipeEnd ? ColorPipeEndPi : v.Kind == RunVertexKind.GapMidpoint ? ColorGapPi : ColorPi;
                    markers.AddRing(v.Point, i == 0 ? 1.2 : 0.6, i == 0 ? ColorStart : color, 0.15);
                }
                ViewHelper.ZoomTo(ProfileCreation.RunExtents(builder, run), 1.2, 15.0);
                ed.UpdateScreen();
                double length;
                using (Polyline path = PipeRunBuilder.ToPolyline(run)) length = path.Length;
                int gapPis = run.Vertices.FindAll(v => v.Kind == RunVertexKind.GapMidpoint).Count;
                int offsetPis = run.Vertices.FindAll(v => v.Kind == RunVertexKind.PipeEnd).Count;
                int manholePis = run.Vertices.FindAll(v => v.Kind == RunVertexKind.ManholeCenter).Count;
                return MessageBox.Show(
                    $"Planned alignment: {run.Steps.Count} pipe(s), {length:0.00} m, {run.Vertices.Count} PIs.\n\n" +
                    $"Red ring = start (station 0)\nGreen = manhole centre PIs ({manholePis})\nYellow = pipe-end PIs at off-centre manholes / curves ({offsetPis})\n" +
                    $"Magenta = gap midpoints ({gapPis})\nWhite = other PIs\n" +
                    (run.UnusedPipes.Count > 0 ? $"Grey = {run.UnusedPipes.Count} selected pipe(s) not on the run (gap over the limit or another branch)\n" : "") +
                    "\nCreate the alignment along this path?", "Create alignment - Preview", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
        }

        /// <summary>Offers to align the run's gravity pipes when some point against the others. Returns pipes reversed, or -1 if cancelled.</summary>
        private static int CheckRunFlow(PipeRunBuilder builder, List<RunStep> steps, Editor ed)
        {
            FlowRun flow = builder.FlowRunFor(steps);
            if (!flow.IsMixed) return 0;
            List<ObjectId> toReverse;
            using (FormFlowDirectionChoice choice = new FormFlowDirectionChoice(new List<FlowRun> { flow }))
            {
                if (choice.ShowDialog() != DialogResult.OK) return -1;
                toReverse = choice.PipesToReverse;
            }
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            int reversed = 0;
            using (acDoc.LockDocument())
            using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in toReverse)
                {
                    Pipe pipe = (Pipe)tr.GetObject(id, OpenMode.ForWrite);
                    try { PipeFlowAnalysis.ReversePipe(pipe); reversed++; }
                    catch (System.Exception ex) { ed.WriteMessage($"\n[Error] Reversing pipe <{pipe.Name}>: {ex.Message}"); }
                }
                tr.Commit();
            }
            return reversed;
        }
    }
}
