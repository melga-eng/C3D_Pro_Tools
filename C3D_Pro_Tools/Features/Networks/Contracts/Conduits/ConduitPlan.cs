using System;
using System.Collections.Generic;
using System.Linq;

// The preview of Generate Conduits: per chosen encasement, the conduits that would be made, how each end was resolved, and
// the fit checks, without changing anything; plus what Apply needs to detect a stale plan. Lists are frozen when set.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>
    /// An encasement pipe as planned. Apply reads it again and compares (<see cref="ConduitValidation.Compare"/>): one that
    /// moved, changed size, network, connections or shape is not filled, and the user is asked to preview again.
    /// </summary>
    /// <param name="OuterWidthM">Outside width (m), rebuilt from inner width and wall when the part reports 0 (duct banks do).</param>
    /// <param name="OuterHeightM">Outside height (m), as CoverRoute.OuterHeightOf reads it.</param>
    internal sealed record EncasementSnapshot(Point3 Start, Point3 End, DrawingObjectRef Network, string NetworkName, DrawingObjectRef? StartStructure,
        DrawingObjectRef? EndStructure, string PartSizeName, double OuterWidthM, double OuterHeightM, bool Curved, double Bulge);

    /// <summary>How a structure's inner plan footprint was found. The preview shows it beside each structure end.</summary>
    internal enum FootprintSource
    {
        /// <summary>The part's own inner size and wall.</summary>
        PartSize,
        /// <summary>A <see cref="StructureInnerBox"/> the user gave for the part size.</summary>
        UserBox,
        /// <summary>Not known: the part reports no inner size and no box was given. The drawn extents are never used as the inner wall.</summary>
        Unknown
    }

    /// <summary>A structure as planned: Apply compares it too, since moving or resizing it changes where conduits stop.</summary>
    /// <param name="RotationRad">Plan rotation (radians), the direction of its X axis.</param>
    /// <param name="InnerWidthM">Along X (m); with <paramref name="Round"/>, the inner diameter. 0 when <paramref name="Source"/> is Unknown.</param>
    internal sealed record StructureSnapshot(DrawingObjectRef Structure, string Name, string PartSizeName, Point3 Location, double RotationRad,
        FootprintSource Source, bool Round, double InnerWidthM, double InnerLengthM, double WallM);

    /// <summary>What an encasement end was found to be.</summary>
    internal enum ConduitEndKind
    {
        /// <summary>At a structure (see <see cref="ConduitEndPlan.FoundBy"/>).</summary>
        Structure,
        /// <summary>At another chosen encasement's end, with no structure.</summary>
        Joint,
        /// <summary>Neither: conduits keep the encasement's end point.</summary>
        Free
    }

    /// <summary>How the structure at an unconnected end was found. Anything but <see cref="Connected"/> is shown in the preview.</summary>
    internal enum StructureMatch { Connected, InsideOuterFace, WithinSearchDistance }

    /// <summary>
    /// One end of one encasement as resolved in the preview: its kind, the structure or neighbour, and the treatment applied
    /// (rules and overrides already combined). The conduits' end points in <see cref="PlannedConduit"/> follow from it.
    /// </summary>
    /// <param name="Structure">At a structure: the structure as planned; otherwise null.</param>
    /// <param name="FoundBy">At a structure: how it was found; otherwise null.</param>
    /// <param name="Treatment">At a structure: where conduits stop; otherwise null.</param>
    /// <param name="Connect">At a structure: conduits are connected to it (required; failure rolls the encasement back).</param>
    /// <param name="Neighbour">At a joint: the other encasement; otherwise null.</param>
    /// <param name="Joint">At a joint: how conduits meet there; otherwise null.</param>
    /// <param name="Overridden">True when a <see cref="ConduitEndOverride"/> set this end's treatment or connection.</param>
    internal sealed record ConduitEndPlan(PipeEnd End, ConduitEndKind Kind, StructureSnapshot Structure = null, StructureMatch? FoundBy = null,
        StructureEndTreatment? Treatment = null, bool Connect = false, DrawingObjectRef? Neighbour = null, JointTreatment? Joint = null, bool Overridden = false)
    {
        private readonly IReadOnlyList<StructureSnapshot> _candidates = Frozen.List<StructureSnapshot>(null);

        /// <summary>
        /// An unconnected end that more than one structure qualifies for by position: every candidate, and
        /// <see cref="Structure"/> null. The item cannot be applied until the user picks one
        /// (<see cref="ConduitEndRules.StructureChoices"/>). Empty otherwise. Frozen when set.
        /// </summary>
        public IReadOnlyList<StructureSnapshot> Candidates { get => _candidates; init => _candidates = Frozen.List(value); }
    }

    /// <summary>
    /// The conduit part as the preview resolved it for one encasement: the identity its fit and connection checks depend on.
    /// Apply resolves the part again for the same destination and compares (<see cref="ConduitValidation.Compare"/>); a
    /// different part, size or diameter makes the item stale. Whether the size is already in the destination network's parts
    /// list is not part of the identity: adding it during Apply (for an earlier encasement) is expected, not a change.
    /// </summary>
    /// <param name="FamilyKey">The family's catalog GUID, or its name when it has none; empty for a part copied from <paramref name="Template"/>.</param>
    /// <param name="OutsideDiameterM">The resolved part's outside diameter (m), read from the part (or the template pipe); never a callout.</param>
    /// <param name="Template">The pipe copied for a family-less part; null when the part is built from its family.</param>
    internal sealed record ConduitPartSnapshot(string FamilyKey, string SizeName, double OutsideDiameterM, DrawingObjectRef? Template = null);

    /// <summary>
    /// Where an encasement's conduits go, as Civil 3D will place them. <paramref name="Network"/> is the existing network
    /// (the encasement's, or a copied part's template network); null only when <paramref name="CreatedAtApply"/>, for the
    /// new network Apply makes (its final name may get a suffix if <paramref name="Name"/> is taken by then).
    /// </summary>
    internal sealed record ConduitDestinationPlan(DrawingObjectRef? Network, string Name, bool CreatedAtApply = false);

    /// <summary>
    /// The section checks for one encasement with the chosen part (<see cref="ConduitSection"/> math in
    /// <see cref="ConduitValidation.CheckSection"/>). Checked against this encasement's own size, never the first chosen.
    /// </summary>
    /// <param name="ConduitOutsideDiameterM">The chosen part's outside diameter (m); 0 when not known, and then only centres are checked.</param>
    /// <param name="ConduitsOutside">Conduits whose outside crosses the encasement's outside edge.</param>
    /// <param name="MinEdgeClearanceM">Least distance from a conduit's outside to the encasement's edge (m); negative when one crosses it.</param>
    /// <param name="MinClearGapM">Least clear gap between neighbouring conduits (m); null with one conduit; negative when two overlap.</param>
    /// <param name="TemplateEnvelopeMatches">Encasement size equals the template's stated envelope; null when the template states none.</param>
    /// <param name="TemplateDiameterMatches">Part diameter equals the template's stated conduit OD; null when either is not known.</param>
    internal sealed record ConduitFitCheck(double EncasementWidthM, double EncasementHeightM, double ConduitOutsideDiameterM, int ConduitsOutside,
        double MinEdgeClearanceM, double? MinClearGapM, bool? TemplateEnvelopeMatches, bool? TemplateDiameterMatches)
    {
        /// <summary>
        /// Neighbouring conduits overlap: the resolved part's diameter is larger than a pitch. Always blocks the encasement,
        /// whatever <see cref="ConduitRequest.AllowConduitsOutsideEncasement"/> says (see <see cref="ConduitValidation.FitIssues"/>).
        /// </summary>
        public bool ConduitsOverlap => MinClearGapM is double gap && gap < -ConduitValidation.PositionTolerance;
    }

    /// <summary>
    /// One conduit to make. <paramref name="Column"/> and <paramref name="Row"/> are 1-based (column 1 leftmost along the
    /// run, row 1 lowest), so conduits keep their position across mitred joints. Points are world coordinates on the
    /// conduit's centreline after end treatment; Z follows the encasement's grade plus the row's height.
    /// </summary>
    /// <param name="OffsetAcrossM">From the encasement's centreline, along the run's right (m), side shift included.</param>
    /// <param name="OffsetUpM">From the encasement's centreline (m), placement included.</param>
    /// <param name="Bulge">
    /// The conduit's plan arc from <paramref name="Start"/> to <paramref name="End"/>: tan(sweep / 4), positive when it turns
    /// counter-clockwise (left) and negative when clockwise, as AutoCAD polylines store it; 0 for a straight conduit. With the
    /// two end points it fixes the arc exactly (a bend and its mirror differ in sign), so Apply makes the arc the preview
    /// showed without recalculating it. Curved encasements follow an offset arc. Phase one rule for their ends: at a structure
    /// only <see cref="StructureEndTreatment.EncasementEnd"/> without connection is supported (anything else is an item error,
    /// <see cref="ConduitIssueCodes.CurvedEndUnsupported"/>); at a joint, a mitre is made only between two straight
    /// encasements, so at any joint where either side is curved both sides keep their encasement ends (the end plan records
    /// <see cref="JointTreatment.EncasementEnd"/>, with an Info note <see cref="ConduitIssueCodes.CurvedJointNotMitred"/>).
    /// </param>
    internal sealed record PlannedConduit(int Column, int Row, double OffsetAcrossM, double OffsetUpM, Point3 Start, Point3 End, double Bulge, double PlanLengthM)
    {
        public bool Curved => Bulge != 0;
    }

    /// <summary>
    /// One chosen encasement in the preview, whether or not it can be filled. <see cref="CanApply"/> is false when an Error
    /// issue left it out (unsupported, unknown footprint, conduits outside, too short…); it is still listed so the user sees
    /// why. Apply makes exactly <see cref="Conduits"/>, in one atomic unit.
    /// </summary>
    /// <param name="RunId">The run (chain of chosen encasements joined end to end without structures) it belongs to; 1-based.</param>
    /// <param name="Reversed">True when its drawing direction is opposite to its run's, so its columns are counted from its right.</param>
    /// <param name="Destination">The network the conduits will be in, as Civil 3D will place them (a copied part stays in its template's).</param>
    /// <param name="Part">The part as resolved for <paramref name="Destination"/>; its diameter is the one <paramref name="Fit"/> used. Null when it could not be resolved (then the item has an Error).</param>
    internal sealed record ConduitSourcePlan(DrawingObjectRef Encasement, string Name, EncasementSnapshot Snapshot, int RunId, bool Reversed,
        ConduitEndPlan StartEnd, ConduitEndPlan EndEnd, ConduitFitCheck Fit, ConduitDestinationPlan Destination, ConduitPartSnapshot Part, IReadOnlyList<PlannedConduit> Conduits)
    {
        private readonly IReadOnlyList<PlannedConduit> _conduits = Frozen.List(Conduits);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);

        public IReadOnlyList<PlannedConduit> Conduits { get => _conduits; init => _conduits = Frozen.List(value); }
        /// <summary>This encasement's notes, warnings and errors. Frozen when set.</summary>
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }

        public bool CanApply => Conduits.Count > 0 && !Issues.Any(i => i.Severity == IssueSeverity.Error);
    }

    /// <summary>
    /// The plan made from a <see cref="ConduitRequest"/>. Status: <see cref="OperationStatus.Succeeded"/> when at least one
    /// encasement can be filled, <see cref="OperationStatus.NoChange"/> when none can (each item says why),
    /// <see cref="OperationStatus.Rejected"/> for an invalid request or wrong drawing, <see cref="OperationStatus.Cancelled"/>
    /// when planning was stopped. Only a succeeded plan can be applied.
    /// </summary>
    internal sealed record ConduitPreview
    {
        private readonly IReadOnlyList<ConduitSourcePlan> _items = Frozen.List<ConduitSourcePlan>(null);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);
        private readonly IReadOnlyList<string> _notes = Frozen.List<string>(null);

        public Guid PlanId { get; init; } = Guid.NewGuid();
        public DrawingIdentity Drawing { get; init; }
        /// <summary>The request planned; Apply uses its options, never newer form values.</summary>
        public ConduitRequest Request { get; init; }
        public OperationStatus Status { get; init; }
        /// <summary>Every chosen encasement, in request order, fillable or not. Frozen when set.</summary>
        public IReadOnlyList<ConduitSourcePlan> Items { get => _items; init => _items = Frozen.List(value); }
        /// <summary>Each encasement is filled with its whole conduit array, or left with none: one failing does not undo the others.</summary>
        public Atomicity Atomicity { get; init; } = Atomicity.PerItem;
        /// <summary>Issues of the whole request (template, part, units, drawing). Item issues are on the items. Frozen when set.</summary>
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
        /// <summary>The template's review notes (uncertainties), repeated for the user before applying. Frozen when set.</summary>
        public IReadOnlyList<string> TemplateNotes { get => _notes; init => _notes = Frozen.List(value); }

        public int ConduitsToCreate => Items.Where(i => i.CanApply).Sum(i => i.Conduits.Count);
    }

    /// <summary>Apply a preview to the encasements the user kept ticked (each must be an applicable item). Apply never plans again.</summary>
    internal sealed record ConduitApplyRequest(ConduitPreview Preview, IReadOnlyList<DrawingObjectRef> Chosen)
    {
        private readonly IReadOnlyList<DrawingObjectRef> _chosen = Frozen.List(Chosen);
        public IReadOnlyList<DrawingObjectRef> Chosen { get => _chosen; init => _chosen = Frozen.List(value); }
    }

    /// <summary>Progress while applying: <paramref name="Completed"/> of <paramref name="Total"/> encasements; <paramref name="Current"/> is the next one.</summary>
    internal sealed record ConduitProgress(int Completed, int Total, string Current);
}
