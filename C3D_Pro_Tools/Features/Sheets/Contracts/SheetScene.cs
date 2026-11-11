using System;
using System.Collections.Generic;
using System.Threading;

// The read-only snapshot of a drawing that sheet production plans from (docs/sheet-production-contract.md, sections 2–4).
// The Civil 3D adapter builds it; planning never reads the drawing itself. Metres for model values, millimetres for paper.
namespace C3DProTools.Features.Sheets.Contracts
{
    /// <summary>What a layout is, for sheet production (section 2).</summary>
    internal enum SheetKind
    {
        /// <summary>Neither a tile part nor a profile sheet; left alone.</summary>
        Other = 0,
        /// <summary>A tile part: Sheet Manager reads a tile and part from it.</summary>
        Plan = 1,
        /// <summary>Not a tile part, and its main viewport shows a profile view. Its title block scale is never written.</summary>
        Profile = 2
    }

    /// <summary>What a profile shown in a view is for (as ProfileRoleRules tells them apart).</summary>
    internal enum SceneProfileRole { Other = 0, Existing = 1, Proposed = 2, Top = 3 }

    internal sealed record SheetSceneRequest
    {
        private readonly IReadOnlyList<string> _layouts = Frozen.List<string>(null);

        public DrawingIdentity Drawing { get; init; }
        /// <summary>The layouts to read; empty reads every layout.</summary>
        public IReadOnlyList<string> Layouts { get => _layouts; init => _layouts = Frozen.List(value); }
    }

    /// <param name="PaperMmPerModelM">Paper millimetres per model metre: 2.0 at 1:500, 1.333… at 1:750.</param>
    /// <param name="ModelBoundary">The viewport outline in model plan metres (its clip boundary when clipped), closed, in order.</param>
    internal sealed record ViewportSnapshot(DrawingObjectRef Ref, double PaperMmPerModelM, double TwistDeg,
        IReadOnlyList<Point2> ModelBoundary, Rect2 PaperBoxMm, bool Locked)
    {
        private readonly IReadOnlyList<Point2> _boundary = Frozen.List(ModelBoundary);
        public IReadOnlyList<Point2> ModelBoundary { get => _boundary; init => _boundary = Frozen.List(value); }
    }

    /// <param name="Tile">"124"; empty when the sheet is not a tile part.</param>
    /// <param name="Part">"D"; empty when the sheet is not a tile part.</param>
    internal sealed record SheetSnapshot(string Layout, int TabOrder, SheetKind Kind, string Tile, string Part,
        string DrawingNumber, IReadOnlyList<ViewportSnapshot> Viewports)
    {
        private readonly IReadOnlyList<ViewportSnapshot> _viewports = Frozen.List(Viewports);
        public IReadOnlyList<ViewportSnapshot> Viewports { get => _viewports; init => _viewports = Frozen.List(value); }
    }

    /// <param name="TypeCode">The duct bank or pipe type ("IC-3A"), as the adapter reads it from the part; empty when unknown.</param>
    /// <param name="OuterWidthM">Outside width (encasement for duct banks, as CoverRoute.OuterWidthOf).</param>
    /// <param name="OuterHeightM">Outside height (encasement for duct banks, as CoverRoute.OuterHeightOf).</param>
    internal sealed record ScenePipe(DrawingObjectRef Ref, string Name, string Network, string TypeCode, Point3 Start, Point3 End,
        double OuterWidthM, double OuterHeightM);

    internal sealed record SceneStructure(DrawingObjectRef Ref, string Name, string Network, string TypeName, Point3 Insertion);

    internal sealed record NetworkSnapshot(DrawingObjectRef Ref, string Name, IReadOnlyList<ScenePipe> Pipes, IReadOnlyList<SceneStructure> Structures)
    {
        private readonly IReadOnlyList<ScenePipe> _pipes = Frozen.List(Pipes);
        private readonly IReadOnlyList<SceneStructure> _structures = Frozen.List(Structures);
        public IReadOnlyList<ScenePipe> Pipes { get => _pipes; init => _pipes = Frozen.List(value); }
        public IReadOnlyList<SceneStructure> Structures { get => _structures; init => _structures = Frozen.List(value); }
    }

    /// <summary>
    /// A profile with its geometry: the station/elevation runs the adapter read (vertices, plus samples at ≤ 1 m for surface
    /// profiles), in station order. A gap in the profile (no surface data) is a break between runs. The geometry is part of the
    /// scene, so an edit that keeps the name and role (a regraded surface) still changes the scene stamp.
    /// </summary>
    internal sealed record SceneProfile(DrawingObjectRef Ref, string Name, SceneProfileRole Role, IReadOnlyList<IReadOnlyList<StationElevation>> Runs)
    {
        private readonly IReadOnlyList<IReadOnlyList<StationElevation>> _runs = FreezeRuns(Runs);
        public IReadOnlyList<IReadOnlyList<StationElevation>> Runs { get => _runs; init => _runs = FreezeRuns(value); }

        internal static IReadOnlyList<IReadOnlyList<StationElevation>> FreezeRuns(IEnumerable<IEnumerable<StationElevation>> runs) =>
            Frozen.List(runs == null ? null : System.Linq.Enumerable.Select(runs, r => Frozen.List(r)));
    }

    internal enum ViewPartKind { Pipe = 0, Structure = 1 }

    /// <summary>
    /// A network part as one profile view draws it (section 7 builds DimInput from these). Pipes: stations and outside top and
    /// bottom at both ends. Structures: StartStationM / EndStationM are its outer faces, and the levels are its top and floor.
    /// </summary>
    /// <param name="IsOwnNetwork">True when the part belongs to the view's own network (the ICT duct); false for a crossing part.</param>
    internal sealed record ViewPart(DrawingObjectRef Part, ViewPartKind Kind, bool IsOwnNetwork, double StartStationM, double EndStationM,
        double StartTopM, double StartBottomM, double EndTopM, double EndBottomM);

    internal sealed record AlignmentSnapshot(DrawingObjectRef Ref, string Name, double StartStationM, double EndStationM,
        IReadOnlyList<SceneProfile> Profiles, IReadOnlyList<DrawingObjectRef> ProfileViews)
    {
        private readonly IReadOnlyList<SceneProfile> _profiles = Frozen.List(Profiles);
        private readonly IReadOnlyList<DrawingObjectRef> _views = Frozen.List(ProfileViews);
        public IReadOnlyList<SceneProfile> Profiles { get => _profiles; init => _profiles = Frozen.List(value); }
        public IReadOnlyList<DrawingObjectRef> ProfileViews { get => _views; init => _views = Frozen.List(value); }
    }

    /// <param name="DrawnParts">The network parts this view draws (its own and crossing ones), with their geometry in the view.</param>
    /// <param name="VerticalExaggeration">Vertical scale ÷ horizontal scale: 10 for H 1:500, V 1:50.</param>
    internal sealed record ProfileViewSnapshot(DrawingObjectRef Ref, string Name, DrawingObjectRef Alignment,
        double StartStationM, double EndStationM, double ElevationMinM, double ElevationMaxM, string BandSetName,
        IReadOnlyList<ViewPart> DrawnParts, double VerticalExaggeration)
    {
        private readonly IReadOnlyList<ViewPart> _parts = Frozen.List(DrawnParts);
        public IReadOnlyList<ViewPart> DrawnParts { get => _parts; init => _parts = Frozen.List(value); }
        /// <summary>The view's box in model plan metres (graph and bands), to find the profile sheet that shows it.</summary>
        public Rect2? ModelBox { get; init; }
    }

    /// <summary>
    /// A pipe of another network crossing a profile view's alignment inside the view's station range (section 3). Top and bottom
    /// are the pipe's outside levels at the crossing point.
    /// </summary>
    internal sealed record CrossingSnapshot(DrawingObjectRef ProfileView, DrawingObjectRef CrossingPipe, string CrossingNetwork,
        string Name, double StationM, double CrossingTopM, double CrossingBottomM, bool ShownInView);

    /// <summary>Whether a build may move a tool label (section 4, "Pinning").</summary>
    internal enum PinState { Auto = 0, Pinned = 1 }

    /// <summary>
    /// A label's placement as the last build wrote it, or as it is now. Compared field by field with the tolerances below: any
    /// difference means the user changed the label, which pins it.
    /// </summary>
    /// <param name="TextXM">Dragged text location, model X (metres); for a label that is not dragged, its own location.</param>
    /// <param name="AnchorRatio">Position along the part (0 = start, 1 = end); null for labels without one (structures).</param>
    internal sealed record RecordedPlacement(string StyleName, bool Dragged, double TextXM, double TextYM, double? AnchorRatio, double RotationRad)
    {
        public const double PositionToleranceM = 0.001;
        public const double RatioTolerance = 1e-6;
        public const double RotationToleranceRad = 1e-4;

        /// <summary>
        /// True when the two differ in any recorded field beyond its tolerance: style name (ordinal), dragged state, position,
        /// anchor ratio (one null and the other not counts as a difference) or rotation (compared modulo 2π).
        /// </summary>
        public bool DiffersFrom(RecordedPlacement other)
        {
            if (other == null) return true;
            if (!string.Equals(StyleName ?? "", other.StyleName ?? "", StringComparison.Ordinal)) return true;
            if (Dragged != other.Dragged) return true;
            if (!(Math.Abs(TextXM - other.TextXM) <= PositionToleranceM) || !(Math.Abs(TextYM - other.TextYM) <= PositionToleranceM)) return true;
            if (AnchorRatio.HasValue != other.AnchorRatio.HasValue) return true;
            if (AnchorRatio.HasValue && !(Math.Abs(AnchorRatio.Value - other.AnchorRatio.Value) <= RatioTolerance)) return true;
            double d = Math.IEEERemainder(RotationRad - other.RotationRad, 2 * Math.PI);
            return !(Math.Abs(d) <= RotationToleranceRad);
        }
    }

    /// <summary>The XData a tool label carries (application <c>C3DPRO_BUILD</c>; section 4).</summary>
    /// <param name="SourceKey">The part handle, plus "start" / "end" / joint index.</param>
    /// <param name="StyleSlot">
    /// The rule style this label was made for: the name in <see cref="LabelRule.LabelStyles"/>, trimmed. It stays what it was at
    /// creation, even when the user restyles the label (that restyle shows in the placement, and pins it). A rule with two
    /// styles gives each target two labels with two slots.
    /// </param>
    internal sealed record BuildTag(string RuleId, string SourceKey, string Layout, string StyleSlot, PinState Pin, RecordedPlacement Placement)
    {
        public const string XDataApplication = "C3DPRO_BUILD";

        /// <summary>
        /// A tool label's identity: rule, source, sheet and style slot, each trimmed, compared ignoring case. Two tags with the
        /// same key are a copy (section 4, "Duplicate tags"); different slots are different labels.
        /// </summary>
        public string IdentityKey => string.Join("\u001F", Norm(RuleId), Norm(SourceKey), Norm(Layout), Norm(StyleSlot));

        private static string Norm(string s) => (s ?? "").Trim().ToUpperInvariant();

        /// <summary>
        /// Pinned when its flag says so, or when the label as it is now differs from the placement the last build recorded.
        /// </summary>
        public bool IsPinned(RecordedPlacement current) => Pin == PinState.Pinned || Placement == null || Placement.DiffersFrom(current);
    }

    /// <summary>A network, alignment or profile label in the drawing.</summary>
    /// <param name="Target">The model part or alignment it labels; <c>default</c> (not valid) when that is gone (an orphan label).</param>
    /// <param name="Tag">Its build tag; null for a label placed by hand (never moved or deleted; an obstacle).</param>
    /// <param name="Current">Its placement as it is now, in the same fields as <see cref="BuildTag.Placement"/>.</param>
    internal sealed record LabelSnapshot(DrawingObjectRef Ref, DrawingObjectRef Target, string Layout, BuildTag Tag, RecordedPlacement Current)
    {
        public bool IsToolLabel => Tag != null;
        /// <summary>True when the part it labelled no longer exists.</summary>
        public bool IsOrphan => !Target.IsValid;
        public bool IsPinned => Tag != null && Tag.IsPinned(Current);
    }

    /// <summary>Which kind of part a label style is for.</summary>
    /// <summary>Which kind of label a style is for. CrossingPipe: profile labels of pipes crossing a view (CrossProfileLabelStyles).</summary>
    internal enum LabelStyleKind { Pipe = 0, Structure = 1, CrossingPipe = 2 }

    /// <summary>A label style of the drawing, with its content fingerprint (empty when incomplete; section 10).</summary>
    internal sealed record SceneLabelStyle(LabelStyleKind Kind, string Name, DrawingObjectRef Ref, string Fingerprint);

    /// <summary>What one build step last built on one sheet (an XRecord under <c>C3DPRO_BUILD</c>; section 10).</summary>
    internal sealed record BuildRecord(string Layout, BuildStep Step, string InputHash);

    internal sealed record SheetScene
    {
        private readonly IReadOnlyList<SheetSnapshot> _sheets = Frozen.List<SheetSnapshot>(null);
        private readonly IReadOnlyList<NetworkSnapshot> _networks = Frozen.List<NetworkSnapshot>(null);
        private readonly IReadOnlyList<AlignmentSnapshot> _alignments = Frozen.List<AlignmentSnapshot>(null);
        private readonly IReadOnlyList<ProfileViewSnapshot> _views = Frozen.List<ProfileViewSnapshot>(null);
        private readonly IReadOnlyList<CrossingSnapshot> _crossings = Frozen.List<CrossingSnapshot>(null);
        private readonly IReadOnlyList<LabelSnapshot> _labels = Frozen.List<LabelSnapshot>(null);
        private readonly IReadOnlyList<BuildRecord> _records = Frozen.List<BuildRecord>(null);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);

        public DrawingIdentity Drawing { get; init; }
        public IReadOnlyList<SheetSnapshot> Sheets { get => _sheets; init => _sheets = Frozen.List(value); }
        public IReadOnlyList<NetworkSnapshot> Networks { get => _networks; init => _networks = Frozen.List(value); }
        public IReadOnlyList<AlignmentSnapshot> Alignments { get => _alignments; init => _alignments = Frozen.List(value); }
        public IReadOnlyList<ProfileViewSnapshot> ProfileViews { get => _views; init => _views = Frozen.List(value); }
        public IReadOnlyList<CrossingSnapshot> Crossings { get => _crossings; init => _crossings = Frozen.List(value); }
        public IReadOnlyList<LabelSnapshot> Labels { get => _labels; init => _labels = Frozen.List(value); }
        public IReadOnlyList<BuildRecord> BuildRecords { get => _records; init => _records = Frozen.List(value); }
        private readonly IReadOnlyList<SceneLabelStyle> _labelStyles = Frozen.List<SceneLabelStyle>(null);
        /// <summary>The drawing's pipe and structure label styles, so rules can be checked without the drawing.</summary>
        public IReadOnlyList<SceneLabelStyle> LabelStyles { get => _labelStyles; init => _labelStyles = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>Reads the snapshot. Implemented by the Civil 3D adapter; runs on the AutoCAD main thread and changes nothing.</summary>
    internal interface ISheetScene
    {
        SheetScene Read(SheetSceneRequest request, CancellationToken ct);
    }
}
