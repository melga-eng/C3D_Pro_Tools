using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;
using Label = System.Windows.Forms.Label;

namespace C3DProTools
{
    /// <summary>The Cover and Draw sections: lists of routes, adding them, checking, optimising, previewing and applying.</summary>
    internal sealed partial class FormRouteDesigner
    {
        private const int CInclude = 0, CName = 1, CSource = 2, CPipes = 3, CLength = 4, CStatus = 5;

        private readonly List<CoverJob> _coverJobs = new List<CoverJob>(), _drawJobs = new List<CoverJob>();
        private readonly DataGridView _coverGrid = ToolUi.Grid(), _drawGrid = ToolUi.Grid();
        private readonly ComboBox _cbAddNetwork = ToolUi.Combo(220);
        /// <summary>The options each job was last prepared with; a job prepared with other options is prepared again before use.</summary>
        private readonly Dictionary<CoverJob, string> _preparedWith = new Dictionary<CoverJob, string>();
        private int _pathCount;

        // Draw options
        private readonly RadioButton _rbDrawNew = new RadioButton { Text = "New network", AutoSize = true }, _rbDrawExisting = new RadioButton { Text = "Existing network", AutoSize = true };
        private readonly TextBox _txtDrawNet = new TextBox { Width = 200 };
        private readonly ComboBox _cbDrawNetwork = ToolUi.Combo();
        private readonly PartChooser _drawPipe = new PartChooser(DomainType.Pipe), _drawStructure = new PartChooser(DomainType.Structure);
        private readonly CheckBox _chkBends = ToolUi.Check("Structures at bends"), _chkEnds = ToolUi.Check("Structures at both ends of each path");
        private readonly TextBox _txtStructSpacing = ToolUi.Number(0);
        private readonly CheckBox _chkReversePaths = ToolUi.Check("Run against the paths' direction");

        // =====================================================================================
        // Pages
        // =====================================================================================

        private Control BuildCoverPage()
        {
            TableLayoutPanel page = JobPage(_coverGrid, "Routes of existing pipes",
                "Parallel pipes running through the same structures stay separate routes. Double-click a route to preview its profile.",
                new Control[]
                {
                    StyleUi.Primary("Pick start && end…", (s, e) => AddPicked()),
                    StyleUi.Secondary("Select pipes…", (s, e) => AddSelected()),
                    new Label { Text = "or a whole network", AutoSize = true, ForeColor = StyleUi.Muted, Margin = new Padding(10, 9, 4, 3) },
                    _cbAddNetwork,
                    StyleUi.Secondary("Add", (s, e) => AddNetworkRoutes()),
                    StyleUi.Secondary("Remove", (s, e) => RemoveSelected(_coverGrid, _coverJobs)),
                    StyleUi.Secondary("Clear", (s, e) => { _coverJobs.Clear(); FillJobs(_coverGrid, _coverJobs); })
                },
                new Control[]
                {
                    StyleUi.Secondary("Check cover", (s, e) => RunJobs(_coverJobs, false)),
                    StyleUi.Secondary("Optimize", (s, e) => RunJobs(_coverJobs, true)),
                    StyleUi.Secondary("Preview…", (s, e) => Preview(_coverGrid)),
                    StyleUi.Secondary("Show", (s, e) => ShowJobs(_coverGrid, _coverJobs))
                },
                StyleUi.Primary("Apply ticked…", (s, e) => ApplyTicked(_coverJobs)));
            _cbAddNetwork.Margin = new Padding(3, 6, 3, 3);
            foreach (string n in _gravityNetworks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbAddNetwork.Items.Add(n);
            if (_cbAddNetwork.Items.Count > 0) _cbAddNetwork.SelectedIndex = 0;
            return page;
        }

        private Control BuildDrawPage()
        {
            return JobPage(_drawGrid, "Paths to draw new pipes along",
                "Polylines (2D or 3D), lines, arcs, splines, alignments and feature lines. Arcs become curved pipes. Set the network and parts on the right.",
                new Control[]
                {
                    StyleUi.Primary("Select paths…", (s, e) => AddPaths()),
                    StyleUi.Secondary("Remove", (s, e) => RemoveSelected(_drawGrid, _drawJobs)),
                    StyleUi.Secondary("Clear", (s, e) => { _drawJobs.Clear(); FillJobs(_drawGrid, _drawJobs); })
                },
                new Control[]
                {
                    StyleUi.Secondary("Optimize", (s, e) => RunJobs(_drawJobs, true)),
                    StyleUi.Secondary("Preview…", (s, e) => Preview(_drawGrid)),
                    StyleUi.Secondary("Show", (s, e) => ShowJobs(_drawGrid, _drawJobs))
                },
                StyleUi.Primary("Draw ticked…", (s, e) => ApplyTicked(_drawJobs)));
        }

        /// <summary>Title, explanation, the "add" tools, the route list and the actions under it.</summary>
        private TableLayoutPanel JobPage(DataGridView grid, string title, string info, Control[] tools, Control[] actions, Button apply)
        {
            TableLayoutPanel page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = StyleUi.Surface, Padding = new Padding(12, 10, 8, 8) };
            page.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(SizeType.Percent, 100));
            Label heading = new Label { Text = title, Font = StyleUi.Heading, AutoSize = true, Margin = new Padding(3, 0, 3, 2) };
            Label note = ToolUi.Note(info);
            note.Margin = new Padding(3, 0, 3, 6);
            FlowLayoutPanel toolbar = StyleUi.Row(tools);
            toolbar.Dock = DockStyle.Fill;

            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", FillWeight = 6, MinimumWidth = 30 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Route", FillWeight = 34, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "From", FillWeight = 16, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pipes", FillWeight = 8, MinimumWidth = 56, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Length (m)", FillWeight = 10, MinimumWidth = 86, ReadOnly = true, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 36, ReadOnly = true });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == CInclude && grid.Rows[e.RowIndex].Tag is CoverJob job) { job.Include = (bool)(grid.Rows[e.RowIndex].Cells[CInclude].Value ?? false); ShowSection(); }
            };
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Preview(grid); };
            Panel gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = StyleUi.Border, Margin = new Padding(3, 4, 3, 4) };
            gridHost.Controls.Add(grid);

            TableLayoutPanel bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel left = StyleUi.Row(actions);
            left.Dock = DockStyle.Fill;
            left.WrapContents = false;
            apply.MinimumSize = new Size(150, 34);
            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(apply, 1, 0);

            foreach (Control c in new Control[] { heading, note, toolbar, gridHost, bar })
            {
                page.RowStyles.Add(new RowStyle(c == gridHost ? SizeType.Percent : SizeType.AutoSize, 100));
                page.Controls.Add(c, 0, page.RowCount++);
            }
            page.Resize += (s, e) => note.MaximumSize = new Size(Math.Max(200, page.ClientSize.Width - 40), 0);
            return page;
        }

        private IEnumerable<Control> BuildDrawOptions()
        {
            TableLayoutPanel target = ToolUi.Card("Network for the new pipes");
            ToolUi.Wide(target, ToolUi.Inline(_rbDrawNew, _txtDrawNet));
            ToolUi.Wide(target, ToolUi.Inline(_rbDrawExisting, _cbDrawNetwork));
            ToolUi.Wide(target, ToolUi.Note("A new network uses the pipe's parts list. Paths starting or ending inside a structure of the existing network connect to it."));
            yield return target;

            TableLayoutPanel pipe = ToolUi.Card("Pipe");
            ToolUi.Wide(pipe, _drawPipe);
            yield return pipe;
            TableLayoutPanel structure = ToolUi.Card("Structure", "Placed at bends, path ends, the spacing below and (gravity) new breaks.");
            ToolUi.Wide(structure, _drawStructure);
            yield return structure;

            TableLayoutPanel layout = ToolUi.Card("Layout");
            ToolUi.Wide(layout, _chkBends, _tips, "Without structures, pipes meet at bends at one level (cable ducts); with them, levels may step there.");
            ToolUi.Wide(layout, _chkEnds);
            ToolUi.Field(layout, "Structures every", ToolUi.WithUnit(_txtStructSpacing, "m (0 = any)"));
            ToolUi.Wide(layout, _chkReversePaths);
            yield return layout;

            ToolUi.Exclusive(_rbDrawNew, _rbDrawExisting);
            foreach (string n in _gravityNetworks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _cbDrawNetwork.Items.Add(n);
            _rbDrawNew.CheckedChanged += (s, e) => { UpdateDrawTarget(); InvalidateResults(); };
            _cbDrawNetwork.SelectedIndexChanged += (s, e) => { UpdateDrawTarget(); InvalidateResults(); };
            _drawPipe.Changed += (s, e) => InvalidateResults();
        }

        private void ApplyDrawSettings()
        {
            _rbDrawExisting.Checked = _s.DrawToExisting && _cbDrawNetwork.Items.Count > 0;
            _rbDrawNew.Checked = !_rbDrawExisting.Checked;
            _txtDrawNet.Text = _s.DrawNewNetworkName;
            ToolUi.Select(_cbDrawNetwork, _s.DrawNetwork);
            _chkBends.Checked = _s.StructuresAtBends;
            _chkEnds.Checked = _s.StructuresAtEnds;
            _txtStructSpacing.Text = ToolUi.Format(_s.MaxStructureSpacing);
            _chkReversePaths.Checked = _s.ReversePaths;
            UpdateDrawTarget(true);
        }

        private void SaveDrawSettings()
        {
            _s.DrawToExisting = _rbDrawExisting.Checked;
            _s.DrawNetwork = _cbDrawNetwork.Text;
            _s.DrawNewNetworkName = _txtDrawNet.Text;
            _s.DrawPartsList = _drawPipe.ListName;
            _s.DrawPipeFamily = _drawPipe.FamilyName;
            _s.DrawPipeSize = _drawPipe.SizeName;
            _s.DrawStructureList = _drawStructure.ListName;
            _s.DrawStructureFamily = _drawStructure.FamilyName;
            _s.DrawStructureSize = _drawStructure.SizeName;
            _s.StructuresAtBends = _chkBends.Checked;
            _s.StructuresAtEnds = _chkEnds.Checked;
            if (ToolUi.TryNumber(_txtStructSpacing, out double spacing) && spacing >= 0) _s.MaxStructureSpacing = spacing;
            _s.ReversePaths = _chkReversePaths.Checked;
        }

        private ObjectId DrawNetworkId() => _rbDrawExisting.Checked && _gravityNetworks.TryGetValue(_cbDrawNetwork.Text, out ObjectId id) ? id : ObjectId.Null;

        /// <summary>Opens the part choosers on the existing network's own list, or on the remembered lists for a new network.</summary>
        private void UpdateDrawTarget(bool first = false)
        {
            _txtDrawNet.Enabled = _rbDrawNew.Checked;
            _cbDrawNetwork.Enabled = _rbDrawExisting.Checked;
            ObjectId net = DrawNetworkId();
            _drawPipe.SetContext(net, net.IsNull ? (first ? _s.DrawPartsList : _drawPipe.ListName) : null,
                first ? _s.DrawPipeFamily : _drawPipe.FamilyName, first ? _s.DrawPipeSize : _drawPipe.SizeName);
            _drawStructure.SetContext(net, net.IsNull ? (first ? _s.DrawStructureList ?? _s.DrawPartsList : _drawStructure.ListName) : null,
                first ? _s.DrawStructureFamily : _drawStructure.FamilyName, first ? _s.DrawStructureSize : _drawStructure.SizeName);
        }

        // =====================================================================================
        // Route lists
        // =====================================================================================

        private void FillJobs(DataGridView grid, List<CoverJob> jobs)
        {
            int keep = grid.CurrentRow?.Index ?? -1;
            grid.Rows.Clear();
            foreach (CoverJob j in jobs)
            {
                int i = grid.Rows.Add(j.Include, j.Name, j.Source, j.IsNew ? (j.Solution?.Pipes.Count.ToString() ?? "") : j.PipeCount.ToString(),
                    j.Length > 0 ? j.Length.ToString("0.0") : "", j.Error != null ? $"{j.Status}: {j.Error}" : j.Status);
                DataGridViewRow row = grid.Rows[i];
                row.Tag = j;
                bool bad = j.Error != null || (j.Proposed != null ? j.ProposedFailing > 0 : j.CurrentFailing > 0);
                row.Cells[CStatus].Style.ForeColor = j.Applied ? Color.FromArgb(21, 128, 61) : bad ? StyleUi.Danger : j.Proposed != null || (j.Current != null && !bad) ? Color.FromArgb(21, 128, 61) : StyleUi.Muted;
                row.Cells[CStatus].ToolTipText = j.Error ?? j.Status;
            }
            if (keep >= 0 && keep < grid.Rows.Count) grid.CurrentCell = grid.Rows[keep].Cells[CName];
            if (grid == _coverGrid) UpdateManholeContext();
            ShowSection();
        }

        private string CoverSummary()
        {
            if (_coverJobs.Count == 0) return "Add routes: pick a start and an end pipe, select pipes, or add a whole network.";
            int ticked = _coverJobs.Count(j => j.Include);
            return $"{_coverJobs.Count} route(s), {ticked} ticked, {_coverJobs.Sum(j => j.PipeCount)} pipes.";
        }

        private string DrawSummary()
        {
            if (_drawJobs.Count == 0) return "Select the paths to draw new pipes along.";
            return $"{_drawJobs.Count} path(s), {_drawJobs.Count(j => j.Include)} ticked, {_drawJobs.Sum(j => j.Path.Length):0.0} m in all.";
        }

        private void InvalidateResults()
        {
            foreach (CoverJob j in _coverJobs.Concat(_drawJobs)) if (!j.Applied) j.Reset();
            _preparedWith.Clear();
            if (_coverGrid.IsHandleCreated) FillJobs(_coverGrid, _coverJobs);
            if (_drawGrid.IsHandleCreated) FillJobs(_drawGrid, _drawJobs);
        }

        private static CoverJob SelectedJob(DataGridView grid) => grid.CurrentRow?.Tag as CoverJob;

        private void RemoveSelected(DataGridView grid, List<CoverJob> jobs)
        {
            foreach (DataGridViewRow row in grid.SelectedRows) if (row.Tag is CoverJob j) jobs.Remove(j);
            FillJobs(grid, jobs);
        }

        /// <summary>Adds routes whose pipes are not already in another route; reports the ones skipped.</summary>
        private void AddJobs(List<CoverJob> found)
        {
            HashSet<ObjectId> used = new HashSet<ObjectId>(_coverJobs.Where(j => !j.Applied).SelectMany(j => j.Pipes.Select(p => p.id)));
            int skipped = 0;
            foreach (CoverJob j in found)
            {
                if (j.Pipes.Any(p => used.Contains(p.id))) { skipped++; continue; }
                foreach ((ObjectId id, bool _) in j.Pipes) used.Add(id);
                _coverJobs.Add(j);
            }
            FillJobs(_coverGrid, _coverJobs);
            SetStatus($"Added {found.Count - skipped} route(s)" + (skipped > 0 ? $"; {skipped} skipped because their pipes are already in a route on the list." : "."));
        }

        private bool ReadRouteSettings(out double gap, out double center)
        {
            gap = center = 0;
            if (ToolUi.TryNumber(_txtGap, out gap) && ToolUi.TryNumber(_txtCenterTol, out center) && gap > 0 && center > 0) return true;
            MessageBox.Show(this, "Enter a positive max gap and centre tolerance (Reading routes).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void AddPicked()
        {
            if (!ReadRouteSettings(out double gap, out double center)) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            List<CoverJob> found = new List<CoverJob>();
            Hide();
            try
            {
                using (TransientMarkers markers = new TransientMarkers())
                {
                    while (true)
                    {
                        PromptEntityOptions start = new PromptEntityOptions($"\nRoute {found.Count + 1}: select the START pipe [Done]: ");
                        start.SetRejectMessage("\nSelect a gravity network pipe.");
                        start.AddAllowedClass(typeof(Pipe), false);
                        start.Keywords.Add("Done");
                        start.AllowNone = true;
                        PromptEntityResult a = ed.GetEntity(start);
                        if (a.Status != PromptStatus.OK) break;
                        PromptEntityOptions end = new PromptEntityOptions($"\nRoute {found.Count + 1}: select the END pipe: ");
                        end.SetRejectMessage("\nSelect a gravity network pipe.");
                        end.AddAllowedClass(typeof(Pipe), false);
                        PromptEntityResult b = ed.GetEntity(end);
                        if (b.Status != PromptStatus.OK) break;

                        CoverJob job;
                        string error;
                        using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            job = CoverJobs.FromPick(tr, a.ObjectId, b.ObjectId, gap, center, _rbGravity.Checked, out error);
                            if (job != null)
                                foreach ((ObjectId id, bool _) in job.Pipes)
                                    if (tr.GetObject(id, OpenMode.ForRead) is Pipe p) markers.AddPath(new[] { p.StartPoint, p.EndPoint }, 4, 0.3);
                        }
                        if (job == null) { ed.WriteMessage("\n" + error); continue; }
                        found.Add(job);
                        ed.WriteMessage($"\nRoute {found.Count}: {job.Name}.");
                        ed.UpdateScreen();
                    }
                }
            }
            finally { Show(); }
            if (found.Count > 0) AddJobs(found);
        }

        private void AddSelected()
        {
            if (!ReadRouteSettings(out double gap, out double center)) return;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            List<ObjectId> ids = new List<ObjectId>();
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect the pipes of the routes (they are split into routes at junctions): " });
                if (psr.Status != PromptStatus.OK) return;
                ids.AddRange(psr.Value.GetObjectIds());
            }
            finally { Show(); }
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                List<ObjectId> pipes = ids.Where(id => tr.GetObject(id, OpenMode.ForRead) is Pipe).ToList();
                if (pipes.Count == 0) { MessageBox.Show(this, "The selection has no gravity pipes.", Text); return; }
                AddJobs(CoverJobs.FromPipes(tr, pipes, gap, center, _rbGravity.Checked, "Selection"));
            }
        }

        private void AddNetworkRoutes()
        {
            if (!(_cbAddNetwork.SelectedItem is string name) || !_gravityNetworks.TryGetValue(name, out ObjectId netId)) return;
            if (!ReadRouteSettings(out double gap, out double center)) return;
            Database db = AcadApp.DocumentManager.MdiActiveDocument.Database;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                AddJobs(CoverJobs.FromNetwork(tr, netId, gap, center, _rbGravity.Checked));
        }

        private void AddPaths()
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            List<ObjectId> ids = new List<ObjectId>();
            Hide();
            try
            {
                PromptSelectionResult psr = acDoc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect polylines, lines, arcs, splines, alignments or feature lines: " });
                if (psr.Status != PromptStatus.OK) return;
                ids.AddRange(psr.Value.GetObjectIds());
            }
            finally { Show(); }
            List<string> errors = new List<string>();
            int added = 0;
            using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in ids)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Pipe || obj is Structure || obj is PressurePart || !(obj is Curve || obj is Alignment || obj is FeatureLine)) continue;
                    if (_drawJobs.Any(j => j.Path.Id == id)) continue;
                    PathGeometry path = PathGeometry.Read(tr, id, out string error);
                    if (path == null) { errors.Add(error); continue; }
                    _drawJobs.Add(CoverJobs.FromPath(path, ++_pathCount));
                    added++;
                }
            FillJobs(_drawGrid, _drawJobs);
            SetStatus($"Added {added} path(s)." + (errors.Count > 0 ? " Not usable: " + string.Join("; ", errors) : ""));
        }

        // =====================================================================================
        // Check, optimise, preview, apply
        // =====================================================================================

        private string OptionsKey() => string.Join("|", new[]
        {
            _rbGravity.Checked.ToString(), _cbSurface.Text, _cbReference.SelectedIndex.ToString(), _txtMinCover.Text, _txtMaxCover.Text, _txtSpacing.Text, _txtMinLength.Text,
            _txtMaxLength.Text, _txtMinSlope.Text, _txtMaxSlope.Text, _txtMaxSlopeChange.Text, _cbPrefer.SelectedIndex.ToString(), _txtMaxDrop.Text, _txtClearance.Text,
            _cbFlow.SelectedIndex.ToString(), _cbStart.SelectedIndex.ToString(), _cbEnd.SelectedIndex.ToString(), _txtGap.Text, _txtCenterTol.Text,
            _rbDrawExisting.Checked.ToString(), _cbDrawNetwork.Text, _drawPipe.FamilyName, _drawPipe.SizeName, _drawPipe.ListName, _chkBends.Checked.ToString(), _chkEnds.Checked.ToString(),
            _txtStructSpacing.Text, _chkReversePaths.Checked.ToString()
        });

        /// <summary>
        /// Prepares (and with <paramref name="solve"/> optimises) the ticked jobs of a list. Reading the routes uses the drawing
        /// here; optimising runs in the background with the Cancel button shown and the rest of the window locked. False when
        /// the options are not usable or the optimising was cancelled.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> Prepare(List<CoverJob> jobs, bool solve, IEnumerable<CoverJob> only = null)
        {
            if (!TryReadOptions(out CoverOptions o)) return false;
            if (!_surfaces.ContainsKey(_cbSurface.Text)) { MessageBox.Show(this, "Choose the surface cover is measured from.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            List<CoverJob> todo = (only ?? jobs.Where(j => j.Include && !j.Applied)).ToList();
            if (todo.Count == 0) { MessageBox.Show(this, "Tick at least one route that is not applied yet.", Text); return false; }

            CoverJobs.Setup setup = ReadSetup();
            bool isNew = todo[0].IsNew;
            if (isNew && !ReadDrawSetup(setup, out _)) return false;

            Cursor = Cursors.WaitCursor;
            try
            {
                Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
                string key = OptionsKey();
                HashSet<ObjectId> batch = new HashSet<ObjectId>(jobs.Where(j => j.Include).SelectMany(j => j.Pipes.Select(p => p.id)));
                using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                {
                    Autodesk.Civil.DatabaseServices.Surface surface = OpenSurface(tr);
                    setup.ZonePolygons = CoverJobs.ReadZonePolygons(tr, acDoc.Database, o.Rules);
                    int n = 0;
                    foreach (CoverJob job in todo)
                    {
                        SetStatus($"Reading route {++n} of {todo.Count}: {job.Name}…");
                        try { CoverJobs.Prepare(tr, CivilApplication.ActiveDocument, job, surface, o, setup, batch); }
                        catch (System.Exception ex) { job.Error = ex.Message; job.Status = "Error"; }
                        _preparedWith[job] = key;
                    }
                    tr.Commit();
                }
                if (solve)
                {
                    SetStatus($"Optimising {todo.Count} route(s)… (Cancel stops it)");
                    using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource())
                    {
                        foreach (CoverJob job in todo) if (job.Options != null) job.Options.Cancel = cts.Token;
                        _solving = cts;
                        _btnCancelSolve.Visible = true;
                        _split.Enabled = false;
                        _nav.Enabled = false;
                        try
                        {
                            // Pure data only (routes, options, solver): nothing here touches the drawing.
                            await System.Threading.Tasks.Task.Run(() => CoverJobs.SolveAll(todo));
                        }
                        catch (OperationCanceledException)
                        {
                            foreach (CoverJob job in todo.Where(j => j.Solution == null && j.Error == null)) job.Status = "Optimising cancelled";
                            SetStatus("Optimising cancelled; routes already optimised keep their results.");
                            return false;
                        }
                        finally
                        {
                            _solving = null;
                            _btnCancelSolve.Visible = false;
                            _split.Enabled = true;
                            _nav.Enabled = true;
                            foreach (CoverJob job in todo) if (job.Options != null) job.Options.Cancel = default;
                        }
                    }
                }
            }
            finally { Cursor = Cursors.Default; }
            return true;
        }

        private async void RunJobs(List<CoverJob> jobs, bool solve)
        {
            DataGridView grid = jobs == _coverJobs ? _coverGrid : _drawGrid;
            if (!await Prepare(jobs, solve)) return;
            FillJobs(grid, jobs);
            List<CoverJob> done = jobs.Where(j => j.Include && !j.Applied).ToList();
            int errors = done.Count(j => j.Error != null);
            SetStatus(solve
                ? $"Optimised {done.Count - errors} route(s): {done.Where(j => j.Proposed != null).Sum(j => j.Solution.Pipes.Count)} pipes proposed, {done.Sum(j => j.ProposedFailing)} failing." + (errors > 0 ? $" {errors} route(s) have no layout (see Status)." : "")
                : $"Checked {done.Count} route(s): {done.Sum(j => j.CurrentFailing)} of {done.Sum(j => j.Current?.Count ?? 0)} pipes fail the limits.");
        }

        private async void Preview(DataGridView grid)
        {
            CoverJob job = SelectedJob(grid);
            if (job == null) return;
            List<CoverJob> jobs = grid == _coverGrid ? _coverJobs : _drawJobs;
            if (job.Applied) { MessageBox.Show(this, "This route is already applied. Add it again to check the result.", Text); return; }
            // A route prepared with other options (or never) is prepared and optimised again first.
            if (job.Route == null || !_preparedWith.TryGetValue(job, out string key) || key != OptionsKey() || job.Solution == null && job.Error == null)
            {
                if (!await Prepare(jobs, true, new[] { job })) return;
                FillJobs(grid, jobs);
            }
            if (job.Route == null) { MessageBox.Show(this, job.Error ?? "The route could not be read.", Text); return; }

            string summary = $"{job.Name}: {job.Length:0.00} m" + (job.IsNew ? "" : $", {job.Route.Pipes.Count} pipe(s), {job.Route.Structures.Count} structure(s)") +
                             (job.Reversed ? ", stationed in the flow direction" : "") + (job.Route.Crossings.Count > 0 ? $", {job.Route.Crossings.Count} crossing pipe(s)" : "") + ".\n" +
                             (job.Current != null ? $"Now: {job.CurrentFailing} of {job.Current.Count} pipes failing. " : "") +
                             (job.Solution != null ? $"Proposed: {job.Solution.Pipes.Count} pipe(s), {job.ProposedFailing} failing" + (job.Solution.Relaxation > 0.001 ? $" (up to {job.Solution.Relaxation:0.00} m past the cover limits where nothing fits; red pipes)" : "") + "."
                                                   : job.Error ?? "");
            using (FormCoverPreview preview = new FormCoverPreview("Profile & Cover Designer - " + job.Name, summary, job.Route, job.Options, job.Current, job.Proposed, job.Solution != null,
                       job.IsNew ? "Draw this path" : "Apply this route"))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
            }
            Apply(new List<CoverJob> { job }, jobs);
        }

        private async void ApplyTicked(List<CoverJob> jobs)
        {
            if (!await Prepare(jobs, true)) return;
            DataGridView grid = jobs == _coverJobs ? _coverGrid : _drawGrid;
            FillJobs(grid, jobs);
            List<CoverJob> ready = jobs.Where(j => j.Include && !j.Applied && j.Solution != null).ToList();
            if (ready.Count == 0) { MessageBox.Show(this, "None of the ticked routes has a layout to apply (see Status).", Text); return; }
            List<PreviewRow> rows = ready.Select(j => new PreviewRow
            {
                Tag = j,
                Checked = true,
                Cells = j.IsNew
                    ? new[] { j.Name, $"{j.Length:0.0}", j.Solution.Pipes.Count.ToString(), j.Route.Structures.Count(s => s.Id.IsNull).ToString(), j.ProposedFailing.ToString(), j.Status }
                    : new[] { j.Name, $"{j.Length:0.0}", $"{j.Route.Pipes.Count} → {j.Solution.Pipes.Count}", j.CurrentFailing.ToString(), j.ProposedFailing.ToString(), j.Status }
            }).ToList();
            string[] columns = ready[0].IsNew
                ? new[] { "Path", "Length (m)", "New pipes", "New structures", "Failing", "Status" }
                : new[] { "Route", "Length (m)", "Pipes now → proposed", "Failing now", "Failing after", "Status" };
            string intro = ready[0].IsNew
                ? $"{ready.Count} path(s) will be drawn into " + (_rbDrawExisting.Checked ? $"the network \"{_cbDrawNetwork.Text}\"." : $"a new network \"{_txtDrawNet.Text}\".")
                : $"{ready.Count} route(s) will be re-levelled and re-split. Untick any to leave unchanged.";
            using (FormStylePreview confirm = new FormStylePreview(ready[0].IsNew ? "Draw paths" : "Apply routes", intro, columns, rows, ready[0].IsNew ? "Draw" : "Apply", false,
                       "Profile & Cover Designer - Confirm"))
            {
                if (confirm.ShowDialog(this) != DialogResult.OK) return;
                Apply(confirm.Chosen.Cast<CoverJob>().ToList(), jobs);
            }
        }

        /// <summary>Applies solved jobs (each in its own transaction), checks what was built and, if asked, creates the profiles.</summary>
        private void Apply(List<CoverJob> chosen, List<CoverJob> jobs)
        {
            if (chosen.Count == 0) return;
            DataGridView grid = jobs == _coverJobs ? _coverGrid : _drawGrid;
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            CoverJobs.Setup setup = ReadSetup();
            DrawTarget target = null;
            if (chosen[0].IsNew)
            {
                if (!ReadDrawSetup(setup, out target)) return;
                List<string> partsLog = new List<string>();
                using (acDoc.LockDocument())
                using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                {
                    if (!CreateDrawTarget(tr, target)) return;
                    // The parts go into the target network's own list (or it is switched to the chosen list).
                    // A part picked from the drawing is copied when Civil 3D cannot build it (family or catalog missing).
                    target.PipeTemplateId = _drawPipe.Pick.TemplateId;
                    target.StructureTemplateId = _drawStructure.Pick?.TemplateId ?? ObjectId.Null;
                    if (target.PipeTemplateId.IsNull || PartCopy.IsFamily(tr, _drawPipe.Pick.FamilyId))
                        (target.PipeFamilyId, target.PipeSizeId) = PartsListTools.Resolve(tr, target.NetworkId, ObjectId.Null, _drawPipe.Pick, partsLog);
                    if (_drawStructure.Pick != null && (target.StructureTemplateId.IsNull || PartCopy.IsFamily(tr, _drawStructure.Pick.FamilyId)))
                        (target.StructureFamilyId, target.StructureSizeId) = PartsListTools.Resolve(tr, target.NetworkId, ObjectId.Null, _drawStructure.Pick, partsLog);
                    if (target.PipeSizeId.IsNull && target.PipeTemplateId.IsNull)
                    {
                        MessageBox.Show(this, "The pipe could not be put in the network's parts list:\n" + string.Join("\n", partsLog) +
                                              "\n\nUse \"From drawing…\" in the pipe part to pick an existing pipe: it is then copied, with no catalog needed.", Text);
                        return;
                    }
                    tr.Commit();
                }
                if (partsLog.Count > 0) ed.WriteMessage("\n[Parts lists] " + string.Join("\n[Parts lists] ", partsLog));
            }
            Dictionary<ObjectId, (ObjectId family, ObjectId size)> manholes = new Dictionary<ObjectId, (ObjectId, ObjectId)>();
            List<string> manholeLog = new List<string>();

            int applied = 0, failed = 0;
            List<(CoverJob job, PipeRunBuilder builder, List<RunStep> steps)> forProfiles = new List<(CoverJob, PipeRunBuilder, List<RunStep>)>();
            using (acDoc.LockDocument())
            {
                foreach (CoverJob job in chosen)
                {
                    SetStatus($"Applying {job.Name}…");
                    try
                    {
                        using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                        {
                            bool ok = true;
                            if (job.IsNew) CoverJobs.ApplyNew(tr, job, target, ed);
                            else
                            {
                                // New manholes come from the route network's own list (the chosen part is added to it when missing).
                                PartPick manhole = _manholePart.Pick;
                                if (manhole != null && !manholes.ContainsKey(job.NetworkId))
                                    manholes[job.NetworkId] = manhole.TemplateId.IsNull || PartCopy.IsFamily(tr, manhole.FamilyId)
                                        ? PartsListTools.Resolve(tr, job.NetworkId, ObjectId.Null, manhole, manholeLog) : (ObjectId.Null, ObjectId.Null);
                                (ObjectId fam, ObjectId size) = manhole == null ? (ObjectId.Null, ObjectId.Null) : manholes[job.NetworkId];
                                ok = CoverJobs.ApplyExisting(tr, job, fam, size, ed, manhole?.TemplateId ?? ObjectId.Null);
                            }
                            tr.Commit();
                            if (!ok) { failed++; continue; }
                        }
                        applied++;
                        using (Transaction tr = acDoc.Database.TransactionManager.StartTransaction())
                        {
                            List<CoverRow> built = CoverJobs.CheckBuilt(tr, job, OpenSurface(tr), setup, out CoverRoute route, out PipeRunBuilder builder, out List<RunStep> steps);
                            if (built != null)
                            {
                                int failing = built.Count(r => r.Failed);
                                job.Status += $"; checked in the drawing: {failing} of {built.Count} failing";
                                if (_chkCreateProfile.Checked) forProfiles.Add((job, builder, steps));
                            }
                            tr.Commit();
                        }
                        job.Include = false;
                    }
                    catch (System.Exception ex)
                    {
                        job.Error = ex.Message;
                        job.Status = "Failed";
                        failed++;
                    }
                }
            }
            if (manholeLog.Count > 0) ed.WriteMessage("\n[Parts lists] " + string.Join("\n[Parts lists] ", manholeLog));
            ed.Regen();
            FillJobs(grid, jobs);

            int profiles = 0;
            if (forProfiles.Count > 0)
            {
                Hide();
                try
                {
                    foreach ((CoverJob job, PipeRunBuilder builder, List<RunStep> steps) in forProfiles)
                    {
                        List<ObjectId> drawIds = new List<ObjectId>(job.Result.PipeIds);
                        drawIds.AddRange(job.Result.StructureIds);
                        RunProfileResult created = ProfileCreation.CreateForRun(builder, steps, builder.BuildRun(steps), drawIds, _settings, ed);
                        if (created == null) break;
                        profiles++;
                    }
                }
                finally { Show(); }
            }
            SetStatus($"Applied {applied} route(s)" + (failed > 0 ? $", {failed} not applied (see Status)" : "") + (profiles > 0 ? $", {profiles} profile view(s) created" : "") +
                      ". UNDO after closing the window reverses the changes.");
        }

        /// <summary>The layout and target of new pipes from the Draw options; false (with a message) when something is missing.</summary>
        private bool ReadDrawSetup(CoverJobs.Setup setup, out DrawTarget target)
        {
            target = new DrawTarget();
            PartPick pipe = _drawPipe.Pick, structure = _drawStructure.Pick;
            if (pipe == null) { MessageBox.Show(this, "Choose the pipe for the new pipes (parts list, family, size).", Text); return false; }
            if (!ToolUi.TryNumber(_txtStructSpacing, out double spacing) || spacing < 0) { MessageBox.Show(this, "Enter the structure spacing as a number (0 = no limit).", Text); return false; }
            setup.Layout = new CoverRoute.PathLayout
            {
                InnerHeight = pipe.InnerHeight, OuterHeight = pipe.InnerHeight + 2 * pipe.Wall, StructuresAtBends = _chkBends.Checked, StructuresAtEnds = _chkEnds.Checked,
                MaxStructureSpacing = spacing, InitialDepth = 1.0
            };
            if ((setup.Layout.StructuresAtBends || setup.Layout.StructuresAtEnds || spacing > 0 || _rbGravity.Checked) && structure == null)
            {
                MessageBox.Show(this, "Choose the structure (the layout places structures).", Text);
                return false;
            }
            setup.ReversePaths = _chkReversePaths.Checked;
            setup.TargetNetworkId = DrawNetworkId();
            target.NetworkId = setup.TargetNetworkId;
            return true;
        }

        /// <summary>Makes the new network the pipes are drawn into (once per batch), with the chosen parts list and surface.</summary>
        private bool CreateDrawTarget(Transaction tr, DrawTarget target)
        {
            if (!target.NetworkId.IsNull) return true;
            string name = string.IsNullOrWhiteSpace(_txtDrawNet.Text) ? "New Route" : _txtDrawNet.Text.Trim();
            ObjectId netId = Network.Create(CivilApplication.ActiveDocument, ref name);
            Network net = (Network)tr.GetObject(netId, OpenMode.ForWrite);
            if (!_drawPipe.Pick.PartsListId.IsNull) net.PartsListId = _drawPipe.Pick.PartsListId;
            if (_surfaces.TryGetValue(_cbSurface.Text, out ObjectId surfaceId)) net.ReferenceSurfaceId = surfaceId;
            target.NetworkId = netId;
            // Later batches go into the same network.
            _gravityNetworks[name] = netId;
            if (!_cbDrawNetwork.Items.Contains(name)) _cbDrawNetwork.Items.Add(name);
            if (!_cbAddNetwork.Items.Contains(name)) _cbAddNetwork.Items.Add(name);
            return true;
        }

        private void ShowJobs(DataGridView grid, List<CoverJob> jobs)
        {
            if (jobs.Count == 0) return;
            CoverJob selected = SelectedJob(grid);
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Hide();
            try
            {
                using (TransientMarkers markers = new TransientMarkers())
                {
                    Extents3d? all = null, sel = null;
                    void Grow(ref Extents3d? e, Point3d p) { Extents3d x = e ?? new Extents3d(p, p); x.AddPoint(p); e = x; }
                    using (Transaction tr = acDoc.Database.TransactionManager.StartOpenCloseTransaction())
                        foreach (CoverJob j in jobs)
                        {
                            List<Point3d> pts = new List<Point3d>();
                            if (j.IsNew) pts.AddRange(j.Path.Sample(1.0));
                            else
                                foreach ((ObjectId id, bool _) in j.Pipes)
                                    if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Pipe p)
                                    {
                                        ArcInfo? arc = null;
                                        if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
                                        pts.AddRange(PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, 1.0, 200));
                                    }
                            if (pts.Count < 2) continue;
                            short color = j == selected ? (short)1 : j.Include ? (short)4 : (short)8;
                            markers.AddPath(pts, color, j == selected ? 0.45 : 0.3);
                            markers.AddRing(pts[0], 0.6, color, 0.12);
                            foreach (Point3d p in pts) { Grow(ref all, p); if (j == selected) Grow(ref sel, p); }
                        }
                    Extents3d? zoom = sel ?? all;
                    if (zoom.HasValue) ViewHelper.ZoomTo(zoom.Value, 1.2, 20.0);
                    acDoc.Editor.UpdateScreen();
                    acDoc.Editor.GetString(new PromptStringOptions("\nRed = selected route, cyan = ticked, grey = unticked; a ring marks each start. Press Enter to return: ") { AllowSpaces = true });
                }
            }
            finally { Show(); }
        }
    }
}
