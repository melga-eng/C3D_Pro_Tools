using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// The build operation and the service the workspace uses (docs/sheet-production-contract.md, sections 5 and 10). Frozen before
// parallel work: the UI (Antigravity) and the backend (Claude) build against these exact types.
namespace C3DProTools.Features.Sheets.Contracts
{
    /// <summary>The build steps, in the order a build runs them.</summary>
    internal enum BuildStep { PlanLabels = 0, ProfileViews = 1, ProfileLabels = 2, Dims = 3, SheetData = 4, Arrange = 5 }

    internal enum StepState
    {
        /// <summary>The step does not apply to this kind of sheet.</summary>
        NotApplicable = 0,
        /// <summary>No build record for this sheet and step.</summary>
        NotBuilt = 1,
        /// <summary>Built, and its inputs have not changed since.</summary>
        Built = 2,
        /// <summary>Built, but its inputs changed since (the recorded input hash differs).</summary>
        OutOfDate = 3,
        /// <summary>Built, with current issues.</summary>
        HasIssues = 4
    }

    internal enum ChangeKind { Add = 0, Update = 1, Remove = 2 }

    internal enum ChangeTarget { Label = 0, ProfileView = 1, Viewport = 2, Dim = 3, ZoneMark = 4, Text = 5, TitleAttribute = 6, KeyPlan = 7, Pin = 8 }

    internal enum BuildItemKind
    {
        /// <summary>One plan sheet.</summary>
        PlanSheet = 0,
        /// <summary>One tile's profile sheets, together: the packing spans several sheets.</summary>
        ProfileSheetSet = 1
    }

    internal enum BuildPhase { Reading = 0, Planning = 1, Applying = 2 }

    /// <param name="PavementLayers">Layers of the closed pavement polylines for Start / End of pavement marks (comma separated).</param>
    /// <param name="MinAlignmentShownM">An alignment is labelled on a plan viewport that shows at least this much of it.</param>
    internal sealed record BuildSettings(PackingRules Packing, DimRules Dims, ContinuationRules Continuation,
        string PavementLayers = "", double MinAlignmentShownM = 20)
    {
        public static BuildSettings Default => new BuildSettings(new PackingRules(), new DimRules(), new ContinuationRules());
    }

    internal sealed record BuildSheetsRequest
    {
        private readonly IReadOnlyList<string> _layouts = Frozen.List<string>(null);
        private readonly IReadOnlySet<BuildStep> _steps = Frozen.Set<BuildStep>(null);

        public DrawingIdentity Drawing { get; init; }
        /// <summary>The sheets to build; empty builds every Plan and Profile sheet.</summary>
        public IReadOnlyList<string> Layouts { get => _layouts; init => _layouts = Frozen.List(value); }
        public IReadOnlySet<BuildStep> Steps { get => _steps; init => _steps = Frozen.Set(value); }
        public LabelRuleSet Rules { get; init; }
        public BuildSettings Settings { get; init; } = BuildSettings.Default;
    }

    /// <param name="ChangeId">Unique in its preview.</param>
    /// <param name="Existing">The object changed or removed; null for an Add.</param>
    /// <param name="Description">One plain line for the preview list ("Add joint label IC-124-IC3A-06").</param>
    internal sealed record PlannedChange(string ChangeId, BuildStep Step, ChangeKind Kind, ChangeTarget Target, string Layout,
        string RuleId, string SourceKey, DrawingObjectRef? Existing, string Description)
    {
        /// <summary>For a label: the rule style it is made with (its <see cref="BuildTag.StyleSlot"/>); null otherwise.</summary>
        public string StyleSlot { get; init; }
        /// <summary>For a label added to a part: the part it labels; null otherwise.</summary>
        public DrawingObjectRef? Part { get; init; }
        /// <summary>For a pipe label: where along the pipe (0 = start, 1 = end); null otherwise.</summary>
        public double? Ratio { get; init; }
    }

    /// <summary>One atomic unit of a build: it commits whole or rolls back whole.</summary>
    internal sealed record BuildItem(string ItemKey, BuildItemKind Kind, IReadOnlyList<string> Layouts, IReadOnlyList<PlannedChange> Changes)
    {
        private readonly IReadOnlyList<string> _layouts = Frozen.List(Layouts);
        private readonly IReadOnlyList<PlannedChange> _changes = Frozen.List(Changes);
        public IReadOnlyList<string> Layouts { get => _layouts; init => _layouts = Frozen.List(value); }
        public IReadOnlyList<PlannedChange> Changes { get => _changes; init => _changes = Frozen.List(value); }
    }

    /// <summary>
    /// A planned build. <see cref="Status"/>: Succeeded (a plan with items), NoChange (nothing to do), Rejected (busy, invalid,
    /// wrong drawing; see the issues) or Cancelled. Only a Succeeded preview can be applied.
    /// </summary>
    /// <param name="SceneStamp">SHA-256 over the scene, rules, settings, style content fingerprints and contract version.</param>
    internal sealed record BuildSheetsPreview(OperationStatus Status, string PreviewId, DrawingIdentity Drawing, string SceneStamp,
        BuildSheetsRequest Request, IReadOnlyList<BuildItem> Items, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<BuildItem> _items = Frozen.List(Items);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<BuildItem> Items { get => _items; init => _items = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }

        /// <summary>A preview that plans nothing, with the given status and issues (Rejected, Cancelled, NoChange).</summary>
        public static BuildSheetsPreview Empty(OperationStatus status, DrawingIdentity drawing, BuildSheetsRequest request, IEnumerable<OperationIssue> issues = null) =>
            new BuildSheetsPreview(status, Guid.NewGuid().ToString("N"), drawing, "", request, null, Frozen.List(issues));
    }

    internal sealed record BuildSheetsApplyRequest
    {
        private readonly IReadOnlyList<string> _keys = Frozen.List<string>(null);

        public BuildSheetsPreview Preview { get; init; }
        /// <summary>The items to apply; empty applies every item of the preview.</summary>
        public IReadOnlyList<string> ItemKeys { get => _keys; init => _keys = Frozen.List(value); }
    }

    internal sealed record BuildItemOutcome(string ItemKey, ItemStatus Status, int Added, int Updated, int Removed,
        IReadOnlyList<DrawingObjectRef> Created, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<DrawingObjectRef> _created = Frozen.List(Created);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<DrawingObjectRef> Created { get => _created; init => _created = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <param name="OperationId">Names this apply in the log.</param>
    internal sealed record BuildSheetsResult(OperationStatus Status, string OperationId, string PreviewId,
        IReadOnlyList<BuildItemOutcome> Items, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<BuildItemOutcome> _items = Frozen.List(Items);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<BuildItemOutcome> Items { get => _items; init => _items = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    internal sealed record BuildProgress(BuildPhase Phase, int Done, int Total, string CurrentItemKey);

    internal sealed record SheetStatus(string Layout, SheetKind Kind, string Tile, string Part, string DrawingNumber,
        IReadOnlyDictionary<BuildStep, StepState> Steps, int IssueCount)
    {
        private readonly IReadOnlyDictionary<BuildStep, StepState> _steps = Frozen.Dictionary(Steps);
        public IReadOnlyDictionary<BuildStep, StepState> Steps { get => _steps; init => _steps = Frozen.Dictionary(value); }
    }

    internal sealed record SheetOverview(DrawingIdentity Drawing, IReadOnlyList<SheetStatus> Sheets, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<SheetStatus> _sheets = Frozen.List(Sheets);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<SheetStatus> Sheets { get => _sheets; init => _sheets = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>
    /// The backend of a build, on the AutoCAD main thread (implemented by the application layer with the Civil 3D adapter).
    /// Expected outcomes are values: nothing here throws OperationCanceledException.
    /// </summary>
    internal interface IBuildSheets
    {
        BuildSheetsPreview Preview(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
        BuildSheetsResult Apply(BuildSheetsApplyRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
    }

    /// <summary>
    /// What the workspace palette talks to (section 10). Drawing work runs on the AutoCAD main thread through the document's
    /// command context; tasks complete on the caller's synchronization context. A second Preview, PreviewPin or Apply while one
    /// runs returns Rejected with <see cref="SheetCodes.Busy"/>. Outcomes are values: OperationCanceledException never escapes.
    /// </summary>
    internal interface ISheetProductionService
    {
        /// <summary>The drawing the workspace works on; null when none is open.</summary>
        DrawingIdentity ActiveDrawing { get; }
        event EventHandler ActiveDrawingChanged;

        Task<SheetOverview> GetOverviewAsync(DrawingIdentity drawing, CancellationToken ct);
        Task<BuildSheetsPreview> PreviewAsync(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
        Task<BuildSheetsResult> ApplyAsync(BuildSheetsApplyRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
        /// <summary>Plans pinning (<paramref name="pin"/> true) or releasing the labels: a preview of Pin changes only.</summary>
        Task<BuildSheetsPreview> PreviewPinAsync(DrawingIdentity drawing, IReadOnlyList<DrawingObjectRef> labels, bool pin, CancellationToken ct);
        Task ZoomToAsync(DrawingIdentity drawing, string layout, DrawingObjectRef? target, CancellationToken ct);
    }

    /// <summary>Checks a request or apply request says enough on its own (the adapter then checks it against the drawing).</summary>
    internal static class BuildValidation
    {
        public static IReadOnlyList<OperationIssue> ValidateRequest(BuildSheetsRequest request)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (request == null) { issues.Add(OperationIssue.Error(SheetCodes.RequestDrawing, "There is no build request.")); return issues; }
            if (request.Drawing == null || !request.Drawing.IsComplete)
                issues.Add(OperationIssue.Error(SheetCodes.RequestDrawing, "The request does not name an open drawing.", "Drawing"));
            if (request.Steps.Count == 0 || request.Steps.Any(s => !Enum.IsDefined(s)))
                issues.Add(OperationIssue.Error(SheetCodes.RequestNoSteps, "Choose at least one build step.", "Steps"));

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string layout in request.Layouts)
            {
                if (string.IsNullOrWhiteSpace(layout)) issues.Add(OperationIssue.Error(SheetCodes.RequestLayout, "A sheet in the list has no name.", "Layouts"));
                else if (!seen.Add(layout.Trim())) issues.Add(OperationIssue.Error(SheetCodes.RequestLayout, $"Sheet \"{layout}\" is listed twice.", "Layouts"));
            }

            BuildSettings s = request.Settings;
            if (s == null || s.Packing == null || s.Dims == null || s.Continuation == null)
                issues.Add(OperationIssue.Error(SheetCodes.RequestSettings, "The build settings are incomplete.", "Settings"));
            else if (!double.IsFinite(s.MinAlignmentShownM) || s.MinAlignmentShownM < 0)
                issues.Add(OperationIssue.Error(SheetCodes.RequestSettings, "The shortest alignment length to label must be 0 or more.", "Settings"));

            issues.AddRange(LabelRuleValidation.Validate(request.Rules));
            return issues;
        }

        public static IReadOnlyList<OperationIssue> ValidateApply(BuildSheetsApplyRequest request)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            BuildSheetsPreview p = request?.Preview;
            if (p == null || p.Status != OperationStatus.Succeeded)
            {
                issues.Add(OperationIssue.Error(SheetCodes.PreviewNotApplicable, "Only a finished preview with changes can be applied. Preview again."));
                return issues;
            }
            HashSet<string> known = new HashSet<string>(p.Items.Select(i => i.ItemKey), StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in request.ItemKeys)
            {
                if (!known.Contains(key ?? "")) issues.Add(OperationIssue.Error(SheetCodes.ApplyUnknownItem, $"\"{key}\" is not an item of this preview.", "ItemKeys"));
                else if (!seen.Add(key)) issues.Add(OperationIssue.Error(SheetCodes.ApplyUnknownItem, $"\"{key}\" is listed twice.", "ItemKeys"));
            }
            return issues;
        }
    }
}
