using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    /// <summary>
    /// Synthetic implementation of <see cref="ISheetProductionService"/> populated with fake data from tile 124.
    /// Used by the workspace palette in Phase 1 before the live Civil 3D adapter is connected.
    /// Follows docs/sheet-production-contract.md sections 10 and 11.
    /// </summary>
    internal sealed class SyntheticSheetProduction : ISheetProductionService
    {
        private int _inFlight = 0; // 0 = idle, 1 = running an async operation
        private DrawingIdentity _activeDrawing;

        public event EventHandler ActiveDrawingChanged;
        public event Action<string, DrawingObjectRef?> ZoomRequested;

        /// <summary>Optional override to simulate a specific preview status (for UI verification).</summary>
        public OperationStatus? SimulatedPreviewStatus { get; set; }

        /// <summary>Optional override to simulate a specific apply status (for UI verification).</summary>
        public OperationStatus? SimulatedApplyStatus { get; set; }

        public DrawingIdentity ActiveDrawing
        {
            get => _activeDrawing;
            set
            {
                if (!Equals(_activeDrawing, value))
                {
                    _activeDrawing = value;
                    ActiveDrawingChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public SyntheticSheetProduction()
        {
            _activeDrawing = new DrawingIdentity(
                "session-tile-124-001",
                "{0F8E1C2A-0000-4000-8000-000000000124}",
                @"D:\Pinnacle Working Files\000501\_PDF Submittals\ICT-Tile-124\ICT-Tile-124.dwg",
                "m"
            );
        }

        /// <summary>
        /// Resolves the layout location of an object in the synthetic drawing, or false/null if unknown.
        /// Does not guess from text; maps known object handles to their layout.
        /// </summary>
        public bool TryGetLocation(DrawingObjectRef? obj, out string layout)
        {
            layout = null;
            if (!obj.HasValue) return false;

            string handle = obj.Value.Handle;
            switch (handle)
            {
                case "3A12":
                    layout = "124-PROF-1";
                    return true;
                case "3B45":
                case "3C90":
                case "5A20":
                case "5A30":
                    layout = "124-PROF-2";
                    return true;
                case "4D11":
                case "2C05":
                    layout = "124-C";
                    return true;
                case "2A10":
                    layout = "124-A";
                    return true;
                case "2B15":
                case "2B20":
                case "2B30":
                    layout = "124-B";
                    return true;
                default:
                    return false;
            }
        }

        public Task<SheetOverview> GetOverviewAsync(DrawingIdentity drawing, CancellationToken ct)
        {
            // Tile 124 fake data: 4 plan sheets (124-A to D) and 2 profile sheets (124-PROF-1, 124-PROF-2).
            // Every StepState (NotApplicable, NotBuilt, Built, OutOfDate, HasIssues) is present at least once.
            var sheets = new List<SheetStatus>
            {
                // Sheet 1: 124-A (Plan) - Completely Built
                new SheetStatus(
                    "124-A",
                    SheetKind.Plan,
                    "124",
                    "A",
                    "ICT-124-A-102051",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.Built,
                        [BuildStep.ProfileViews] = StepState.NotApplicable,
                        [BuildStep.ProfileLabels] = StepState.NotApplicable,
                        [BuildStep.Dims] = StepState.NotApplicable,
                        [BuildStep.SheetData] = StepState.Built,
                        [BuildStep.Arrange] = StepState.Built
                    },
                    0
                ),

                // Sheet 2: 124-B (Plan) - OutOfDate for labels & arrange
                new SheetStatus(
                    "124-B",
                    SheetKind.Plan,
                    "124",
                    "B",
                    "ICT-124-B-102052",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.OutOfDate,
                        [BuildStep.ProfileViews] = StepState.NotApplicable,
                        [BuildStep.ProfileLabels] = StepState.NotApplicable,
                        [BuildStep.Dims] = StepState.NotApplicable,
                        [BuildStep.SheetData] = StepState.Built,
                        [BuildStep.Arrange] = StepState.OutOfDate
                    },
                    1
                ),

                // Sheet 3: 124-C (Plan) - HasIssues for labels & arrange
                new SheetStatus(
                    "124-C",
                    SheetKind.Plan,
                    "124",
                    "C",
                    "ICT-124-C-102053",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.HasIssues,
                        [BuildStep.ProfileViews] = StepState.NotApplicable,
                        [BuildStep.ProfileLabels] = StepState.NotApplicable,
                        [BuildStep.Dims] = StepState.NotApplicable,
                        [BuildStep.SheetData] = StepState.Built,
                        [BuildStep.Arrange] = StepState.HasIssues
                    },
                    2
                ),

                // Sheet 4: 124-D (Plan) - NotBuilt
                new SheetStatus(
                    "124-D",
                    SheetKind.Plan,
                    "124",
                    "D",
                    "ICT-124-D-102054",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.NotBuilt,
                        [BuildStep.ProfileViews] = StepState.NotApplicable,
                        [BuildStep.ProfileLabels] = StepState.NotApplicable,
                        [BuildStep.Dims] = StepState.NotApplicable,
                        [BuildStep.SheetData] = StepState.NotBuilt,
                        [BuildStep.Arrange] = StepState.NotBuilt
                    },
                    0
                ),

                // Sheet 5: 124-PROF-1 (Profile) - Fully Built
                new SheetStatus(
                    "124-PROF-1",
                    SheetKind.Profile,
                    "124",
                    "P1",
                    "ICT-124-PROF-434131",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.NotApplicable,
                        [BuildStep.ProfileViews] = StepState.Built,
                        [BuildStep.ProfileLabels] = StepState.Built,
                        [BuildStep.Dims] = StepState.Built,
                        [BuildStep.SheetData] = StepState.Built,
                        [BuildStep.Arrange] = StepState.Built
                    },
                    0
                ),

                // Sheet 6: 124-PROF-2 (Profile) - ProfileLabels OutOfDate, Dims HasIssues, Arrange NotBuilt
                new SheetStatus(
                    "124-PROF-2",
                    SheetKind.Profile,
                    "124",
                    "P2",
                    "ICT-124-PROF-434132",
                    new Dictionary<BuildStep, StepState>
                    {
                        [BuildStep.PlanLabels] = StepState.NotApplicable,
                        [BuildStep.ProfileViews] = StepState.Built,
                        [BuildStep.ProfileLabels] = StepState.OutOfDate,
                        [BuildStep.Dims] = StepState.HasIssues,
                        [BuildStep.SheetData] = StepState.Built,
                        [BuildStep.Arrange] = StepState.NotBuilt
                    },
                    2
                )
            };

            // Every issue severity (Info, Warning, Error) is present at least once:
            DrawingObjectRef.TryCreate("3A12", out var objInfo);
            DrawingObjectRef.TryCreate("3B45", out var objWarn);
            DrawingObjectRef.TryCreate("3C90", out var objErr);
            DrawingObjectRef.TryCreate("4D11", out var objErr2);

            var issues = new List<OperationIssue>
            {
                OperationIssue.Info(
                    DimCodes.CoverSpacing,
                    "Candidate cover dimension at STA 1+240 dropped (spacing < 10 m)",
                    field: "Dims",
                    obj: objInfo
                ),
                OperationIssue.Warning(
                    DimCodes.ClearanceBelowMinimum,
                    "Clearance between duct bank and storm culvert is 0.25 m (minimum 0.30 m)",
                    field: "Dims",
                    obj: objWarn
                ),
                OperationIssue.Error(
                    DimCodes.ClearanceClash,
                    "Clearance clash detected with existing 200mm DI water main at STA 0+850",
                    field: "Crossings",
                    obj: objErr
                ),
                OperationIssue.Error(
                    ContinuationCodes.BoundaryInvalid,
                    "Sheet 124-C match line boundary polygon is non-closed or self-intersecting",
                    field: "Continuation",
                    obj: objErr2
                )
            };

            return Task.FromResult(new SheetOverview(drawing ?? _activeDrawing, sheets, issues));
        }

        public async Task<BuildSheetsPreview> PreviewAsync(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            // Busy rejection: when Preview is called while another operation is running
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                var busyIssue = OperationIssue.Error(SheetCodes.Busy, "Another operation is already in progress.");
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request?.Drawing ?? _activeDrawing, request, new[] { busyIssue });
            }

            try
            {
                // Slow Preview (a few seconds, with progress) so progress and Cancel can be tested
                int totalSteps = 6;
                string[] stepNames = {
                    "Reading scene geometry...",
                    "Checking styles and fingerprints...",
                    "Planning plan sheet labels (124-A to 124-D)...",
                    "Packing profile views (124-PROF)...",
                    "Calculating cover and clearance dimensions...",
                    "Solving continuation notes and layout arrange..."
                };

                for (int i = 0; i < totalSteps; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return BuildSheetsPreview.Empty(
                            OperationStatus.Cancelled,
                            request?.Drawing ?? _activeDrawing,
                            request,
                            new[] { OperationIssue.Info("sheets.cancelled", "Preview was cancelled by user.") }
                        );
                    }

                    BuildPhase phase = i < 2 ? BuildPhase.Reading : BuildPhase.Planning;
                    progress?.Report(new BuildProgress(phase, i + 1, totalSteps, stepNames[i]));

                    try
                    {
                        await Task.Delay(450, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return BuildSheetsPreview.Empty(
                            OperationStatus.Cancelled,
                            request?.Drawing ?? _activeDrawing,
                            request,
                            new[] { OperationIssue.Info("sheets.cancelled", "Preview was cancelled by user.") }
                        );
                    }
                }

                // Check for simulation override
                if (SimulatedPreviewStatus.HasValue)
                {
                    switch (SimulatedPreviewStatus.Value)
                    {
                        case OperationStatus.NoChange:
                            return BuildSheetsPreview.Empty(OperationStatus.NoChange, request?.Drawing ?? _activeDrawing, request,
                                new[] { OperationIssue.Info("sheets.noChange", "Drawing linework and labels already match current rules.") });

                        case OperationStatus.Rejected:
                            return BuildSheetsPreview.Empty(OperationStatus.Rejected, request?.Drawing ?? _activeDrawing, request,
                                new[] { OperationIssue.Error(SheetCodes.RequestDrawing, "Drawing session validation failed: simulated rejection.") });

                        case OperationStatus.Cancelled:
                            return BuildSheetsPreview.Empty(OperationStatus.Cancelled, request?.Drawing ?? _activeDrawing, request,
                                new[] { OperationIssue.Info("sheets.cancelled", "Preview was cancelled by simulated trigger.") });

                        case OperationStatus.OutcomeUnknown:
                            return BuildSheetsPreview.Empty(OperationStatus.OutcomeUnknown, request?.Drawing ?? _activeDrawing, request,
                                new[] { OperationIssue.Warning("sheets.unknown", "Drawing state could not be verified; reload drawing.") });
                    }
                }

                // Build planned items from selected or all layouts
                var selectedLayouts = (request?.Layouts != null && request.Layouts.Count > 0)
                    ? request.Layouts.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(new[] { "124-A", "124-B", "124-C", "124-D", "124-PROF-1", "124-PROF-2" }, StringComparer.OrdinalIgnoreCase);

                var items = new List<BuildItem>();

                // Plan sheet items
                if (selectedLayouts.Contains("124-A"))
                {
                    items.Add(new BuildItem(
                        "Item-124-A",
                        BuildItemKind.PlanSheet,
                        new[] { "124-A" },
                        new[]
                        {
                            new PlannedChange("chg-01", BuildStep.PlanLabels, ChangeKind.Add, ChangeTarget.Label, "124-A", "rule-mh", "MH-124-01", null, "Add manhole label MH-124-01 (Type B, E: 450123.4, N: 2789123.4)"),
                            new PlannedChange("chg-02", BuildStep.PlanLabels, ChangeKind.Add, ChangeTarget.Label, "124-A", "rule-joint", "IC-124-06", null, "Add joint label IC-124-IC3A-06 (IL 42.15)"),
                            new PlannedChange("chg-03", BuildStep.SheetData, ChangeKind.Update, ChangeTarget.Text, "124-A", "rule-cont", "CONT-NORTH", DrawingObjectRef.TryCreate("2A10", out var r1) ? r1 : null, "Update continuation note: FOR CONTINUATION SEE DRAWING ICT-124-B-102052")
                        }
                    ));
                }

                if (selectedLayouts.Contains("124-B"))
                {
                    items.Add(new BuildItem(
                        "Item-124-B",
                        BuildItemKind.PlanSheet,
                        new[] { "124-B" },
                        new[]
                        {
                            new PlannedChange("chg-04", BuildStep.PlanLabels, ChangeKind.Update, ChangeTarget.Label, "124-B", "rule-mh", "MH-124-04", DrawingObjectRef.TryCreate("2B15", out var r2) ? r2 : null, "Update manhole label MH-124-04 coordinate block"),
                            new PlannedChange("chg-05", BuildStep.PlanLabels, ChangeKind.Remove, ChangeTarget.Label, "124-B", "rule-mh", "OLD-MH-09", DrawingObjectRef.TryCreate("2B20", out var r3) ? r3 : null, "Remove obsolete manhole label OLD-MH-09"),
                            new PlannedChange("chg-06", BuildStep.Arrange, ChangeKind.Update, ChangeTarget.Label, "124-B", "rule-arrange", "IC-124-11", DrawingObjectRef.TryCreate("2B30", out var r4) ? r4 : null, "Reposition duct joint label IC-124-11 (+12mm leader)")
                        }
                    ));
                }

                if (selectedLayouts.Contains("124-C"))
                {
                    items.Add(new BuildItem(
                        "Item-124-C",
                        BuildItemKind.PlanSheet,
                        new[] { "124-C" },
                        new[]
                        {
                            new PlannedChange("chg-07", BuildStep.PlanLabels, ChangeKind.Add, ChangeTarget.Label, "124-C", "rule-joint", "IC-124-22", null, "Add duct joint label IC-124-IC3A-22 (IL 41.80)"),
                            new PlannedChange("chg-08", BuildStep.SheetData, ChangeKind.Update, ChangeTarget.TitleAttribute, "124-C", "rule-title", "REV-03", DrawingObjectRef.TryCreate("2C05", out var r5) ? r5 : null, "Update title block revision attribute to 'C'")
                        }
                    ));
                }

                if (selectedLayouts.Contains("124-D"))
                {
                    items.Add(new BuildItem(
                        "Item-124-D",
                        BuildItemKind.PlanSheet,
                        new[] { "124-D" },
                        new[]
                        {
                            new PlannedChange("chg-09", BuildStep.PlanLabels, ChangeKind.Add, ChangeTarget.Label, "124-D", "rule-mh", "MH-124-15", null, "Add manhole label MH-124-15 (Type A)"),
                            new PlannedChange("chg-10", BuildStep.SheetData, ChangeKind.Add, ChangeTarget.KeyPlan, "124-D", "rule-keyplan", "KP-124-D", null, "Add key plan highlight for Part D")
                        }
                    ));
                }

                // Profile sheet set item
                if (selectedLayouts.Contains("124-PROF-1") || selectedLayouts.Contains("124-PROF-2"))
                {
                    var profLayouts = new List<string>();
                    if (selectedLayouts.Contains("124-PROF-1")) profLayouts.Add("124-PROF-1");
                    if (selectedLayouts.Contains("124-PROF-2")) profLayouts.Add("124-PROF-2");

                    items.Add(new BuildItem(
                        "Item-124-ProfileSet",
                        BuildItemKind.ProfileSheetSet,
                        profLayouts,
                        new[]
                        {
                            new PlannedChange("chg-11", BuildStep.ProfileViews, ChangeKind.Add, ChangeTarget.ProfileView, "124-PROF-1", "rule-pv", "PV-ALIGN-1", null, "Add profile view Align-1 (1:750, STA 0+000 to 0+450)"),
                            new PlannedChange("chg-12", BuildStep.Dims, ChangeKind.Add, ChangeTarget.Dim, "124-PROF-1", "rule-dim-clr", "CROSS-W-01", null, "Add clearance DIM 0.65m at crossing STA 0+215 (Water 200 DI)"),
                            new PlannedChange("chg-13", BuildStep.Dims, ChangeKind.Add, ChangeTarget.Dim, "124-PROF-1", "rule-dim-cvr", "GB-0+310", null, "Add cover DIM 1.45m at grade break STA 0+310"),
                            new PlannedChange("chg-14", BuildStep.ProfileLabels, ChangeKind.Update, ChangeTarget.Label, "124-PROF-2", "rule-pl-slope", "SLOPE-02", DrawingObjectRef.TryCreate("5A20", out var r6) ? r6 : null, "Update duct slope label (S: 0.50 %)"),
                            new PlannedChange("chg-15", BuildStep.SheetData, ChangeKind.Update, ChangeTarget.ProfileView, "124-PROF-2", "rule-bands", "BANDS-02", DrawingObjectRef.TryCreate("5A30", out var r7) ? r7 : null, "Update profile band set (TOD, FGL, NGL)")
                        }
                    ));
                }

                var issues = new List<OperationIssue>
                {
                    OperationIssue.Info(DimCodes.CoverSpacing, "2 minor grade break cover dimensions spaced < 10m merged", "Dims")
                };

                return new BuildSheetsPreview(
                    OperationStatus.Succeeded,
                    Guid.NewGuid().ToString("N"),
                    request?.Drawing ?? _activeDrawing,
                    "sha256-scene-stamp-tile-124-synthetic-v0.3",
                    request,
                    items,
                    issues
                );
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        public async Task<BuildSheetsResult> ApplyAsync(BuildSheetsApplyRequest request, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            // Busy rejection
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                var busyIssue = OperationIssue.Error(SheetCodes.Busy, "Another operation is already in progress.");
                return new BuildSheetsResult(
                    OperationStatus.Rejected,
                    Guid.NewGuid().ToString("N"),
                    request?.Preview?.PreviewId ?? "",
                    null,
                    new[] { busyIssue }
                );
            }

            try
            {
                var p = request?.Preview;
                if (p == null || p.Status != OperationStatus.Succeeded)
                {
                    return new BuildSheetsResult(
                        OperationStatus.Rejected,
                        Guid.NewGuid().ToString("N"),
                        p?.PreviewId ?? "",
                        null,
                        new[] { OperationIssue.Error(SheetCodes.PreviewNotApplicable, "Only a succeeded preview can be applied.") }
                    );
                }

                // Check for simulated apply status override
                if (SimulatedApplyStatus.HasValue)
                {
                    switch (SimulatedApplyStatus.Value)
                    {
                        case OperationStatus.NoChange:
                            return new BuildSheetsResult(OperationStatus.NoChange, Guid.NewGuid().ToString("N"), p.PreviewId, null,
                                new[] { OperationIssue.Info("sheets.noChange", "No changes applied.") });

                        case OperationStatus.Rejected:
                            return new BuildSheetsResult(OperationStatus.Rejected, Guid.NewGuid().ToString("N"), p.PreviewId, null,
                                new[] { OperationIssue.Error(SheetCodes.ApplyPostconditionFailed, "Postcondition failed: label outline exceeded bounds.") });

                        case OperationStatus.Cancelled:
                            return new BuildSheetsResult(OperationStatus.Cancelled, Guid.NewGuid().ToString("N"), p.PreviewId, null,
                                new[] { OperationIssue.Info("sheets.cancelled", "Apply cancelled by simulated trigger.") });

                        case OperationStatus.PartiallySucceeded:
                            var partOutcomes = new List<BuildItemOutcome>();
                            if (p.Items.Count > 0)
                            {
                                partOutcomes.Add(new BuildItemOutcome(p.Items[0].ItemKey, ItemStatus.Committed, 3, 1, 0, null, null));
                            }
                            if (p.Items.Count > 1)
                            {
                                partOutcomes.Add(new BuildItemOutcome(p.Items[1].ItemKey, ItemStatus.RolledBack, 0, 0, 0, null,
                                    new[] { OperationIssue.Error("apply.failed", $"Item '{p.Items[1].ItemKey}' failed validation and was rolled back.") }));
                            }
                            return new BuildSheetsResult(OperationStatus.PartiallySucceeded, Guid.NewGuid().ToString("N"), p.PreviewId, partOutcomes, null);

                        case OperationStatus.RolledBack:
                            var rolledBackOutcomes = (p.Items != null && p.Items.Count > 0)
                                ? p.Items.Select(item => new BuildItemOutcome(item.ItemKey, ItemStatus.RolledBack, 0, 0, 0, null,
                                    new[] { OperationIssue.Error("apply.rolledBack", $"Item '{item.ItemKey}' rolled back.") })).ToList()
                                : new List<BuildItemOutcome> { new BuildItemOutcome("Item-124-A", ItemStatus.RolledBack, 0, 0, 0, null,
                                    new[] { OperationIssue.Error("apply.rolledBack", "Item rolled back.") }) };
                            return new BuildSheetsResult(OperationStatus.RolledBack, Guid.NewGuid().ToString("N"), p.PreviewId, rolledBackOutcomes,
                                new[] { OperationIssue.Error("apply.failed", "All changes were rolled back to preserve drawing integrity.") });

                        case OperationStatus.OutcomeUnknown:
                            return new BuildSheetsResult(OperationStatus.OutcomeUnknown, Guid.NewGuid().ToString("N"), p.PreviewId, null,
                                new[] { OperationIssue.Error("apply.unknown", "AutoCAD transaction state unknown; reload drawing.") });
                    }
                }

                // Filter items to apply
                var targetItems = (request.ItemKeys != null && request.ItemKeys.Count > 0)
                    ? p.Items.Where(i => request.ItemKeys.Contains(i.ItemKey)).ToList()
                    : p.Items.ToList();

                var outcomes = new List<BuildItemOutcome>();
                bool cancelled = false;

                for (int i = 0; i < targetItems.Count; i++)
                {
                    var item = targetItems[i];

                    // Check cancellation between items (docs/sheet-production-contract.md section 10)
                    if (ct.IsCancellationRequested || cancelled)
                    {
                        cancelled = true;
                        outcomes.Add(new BuildItemOutcome(item.ItemKey, ItemStatus.NotAttempted, 0, 0, 0, null, null));
                        continue;
                    }

                    progress?.Report(new BuildProgress(BuildPhase.Applying, i + 1, targetItems.Count, item.ItemKey));

                    try
                    {
                        await Task.Delay(400, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        outcomes.Add(new BuildItemOutcome(item.ItemKey, ItemStatus.NotAttempted, 0, 0, 0, null, null));
                        continue;
                    }

                    // Count changes
                    int added = item.Changes.Count(c => c.Kind == ChangeKind.Add);
                    int updated = item.Changes.Count(c => c.Kind == ChangeKind.Update);
                    int removed = item.Changes.Count(c => c.Kind == ChangeKind.Remove);

                    outcomes.Add(new BuildItemOutcome(item.ItemKey, ItemStatus.Committed, added, updated, removed, null, null));
                }

                OperationStatus overallStatus = cancelled
                    ? OperationStatus.Cancelled
                    : OperationOutcome.Summarize(outcomes.Select(o => o.Status).ToList(), cancelled);

                return new BuildSheetsResult(
                    overallStatus,
                    Guid.NewGuid().ToString("N"),
                    p.PreviewId,
                    outcomes,
                    cancelled ? new[] { OperationIssue.Info("sheets.cancelled", "Apply stopped at safe point between items.") } : null
                );
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        public async Task<BuildSheetsPreview> PreviewPinAsync(DrawingIdentity drawing, IReadOnlyList<DrawingObjectRef> labels, bool pin, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, drawing ?? _activeDrawing, null,
                    new[] { OperationIssue.Error(SheetCodes.Busy, "Another operation is in progress.") });
            }

            try
            {
                await Task.Delay(200, ct).ConfigureAwait(false);

                var changes = new List<PlannedChange>();
                if (labels != null)
                {
                    int idx = 1;
                    foreach (var lbl in labels)
                    {
                        changes.Add(new PlannedChange(
                            $"pin-chg-{idx++}",
                            BuildStep.PlanLabels,
                            ChangeKind.Update,
                            ChangeTarget.Pin,
                            "124-A",
                            "rule-pin",
                            lbl.Handle,
                            lbl,
                            $"{(pin ? "Pin" : "Release")} label {lbl.Handle} (lock position against automated arrangement)"
                        ));
                    }
                }

                var item = new BuildItem("PinChanges", BuildItemKind.PlanSheet, new[] { "124-A" }, changes);

                return new BuildSheetsPreview(
                    OperationStatus.Succeeded,
                    Guid.NewGuid().ToString("N"),
                    drawing ?? _activeDrawing,
                    "sha256-pin-stamp",
                    null,
                    new[] { item },
                    null
                );
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        public Task ZoomToAsync(DrawingIdentity drawing, string layout, DrawingObjectRef? target, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(layout))
            {
                throw new InvalidOperationException("Cannot zoom: sheet layout location is unknown.");
            }
            ZoomRequested?.Invoke(layout, target);
            return Task.CompletedTask;
        }
    }
}
