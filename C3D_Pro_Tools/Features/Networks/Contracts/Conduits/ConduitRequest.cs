using System.Collections.Generic;

// What the user asks Generate Conduits to do. Built by the form from its fields; validated by ConduitValidation in the
// application layer (authoritative). Every object reference means an object in Request.Drawing.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>
    /// Limits and tolerances. <b>Units:</b> conduit contracts are in <b>metres</b> (names end in "M"). Phase one supports
    /// metre drawings only: a request whose <see cref="ConduitRequest.MetresPerDrawingUnit"/> is not 1 is rejected, and no
    /// drawing setting is ever changed. Coordinates (<see cref="Point3"/>) are in drawing units, which are then metres.
    /// </summary>
    internal static class ConduitLimits
    {
        public const int MaxColumns = 30, MaxRows = 30;
        /// <summary>Largest pitch, side shift and bottom height accepted (m). A safeguard against a mistyped value, not a design rule.</summary>
        public const double MaxPitchM = 5.0, MaxSideShiftM = 5.0, MaxBottomToLowestCentreM = 5.0;
        /// <summary>Largest distance an unconnected encasement end may be from a structure to count as ending at it (m).</summary>
        public const double MaxStructureSearchM = 2.0;
        /// <summary>Largest inner box side accepted for a structure size (m).</summary>
        public const double MaxInnerBoxM = 20.0;
        /// <summary>A conduit shorter than this in plan after trimming is not made (m); equals NetworkEditOps.MinPipeLength.</summary>
        public const double MinConduitLengthM = 0.05;
        /// <summary>Two encasement ends within this plan and vertical distance are one joint (m); equals NetworkEditOps.JointTolerance.</summary>
        public const double JointToleranceM = 0.01;
        /// <summary>A part or encasement within this of a template's stated size matches it (m); beyond it the preview warns.</summary>
        public const double SizeMatchToleranceM = 0.002;
    }

    /// <summary>Which end of an encasement pipe, in its own drawing direction.</summary>
    internal enum PipeEnd { Start, End }

    /// <summary>
    /// Where a conduit stops at a structure the encasement ends at. Geometry only: whether the conduit is also connected to
    /// the structure in Civil 3D is <see cref="ConduitEndRules.ConnectToStructures"/>, set separately.
    /// </summary>
    internal enum StructureEndTreatment
    {
        /// <summary>Default. Each conduit stops where its own line meets the structure's inner wall face (trimmed, or extended to it).</summary>
        InnerWall = 0,
        /// <summary>Each conduit stops where its own line meets the outer wall face.</summary>
        OuterWall = 1,
        /// <summary>Each conduit runs to the structure's centre point, as Civil 3D draws connected pipes.</summary>
        StructureCentre = 2,
        /// <summary>The conduit keeps the encasement's end point (offset), as conduit generation did before phase one.</summary>
        EncasementEnd = 3
    }

    /// <summary>Where conduits meet at a joint of two chosen encasements with no structure there.</summary>
    internal enum JointTreatment
    {
        /// <summary>Default. Both conduits end at the mitre of their two offset lines: one continuous run, no gap or overlap.</summary>
        Mitre = 0,
        /// <summary>Each conduit keeps its encasement's end point (offset); small gaps or overlaps at deflections.</summary>
        EncasementEnd = 1
    }

    /// <summary>What to do at a structure whose inner size cannot be read and has no <see cref="StructureInnerBox"/>.</summary>
    internal enum UnknownFootprintPolicy
    {
        /// <summary>Default. The encasement is left out (no conduits made) and the preview names the structure size to give a box for.</summary>
        LeaveOut = 0,
        /// <summary>That end keeps the encasement's end point; the preview warns.</summary>
        KeepEncasementEnd = 1
    }

    /// <summary>
    /// The verified inner plan size of every structure of one part, given by the user when the part reports none (many
    /// manholes in the user's drawings report an inner width of 0). Scoped to a project and a part identity, never a handle:
    /// it applies to a structure whose part size name matches (ignoring case) and, when <paramref name="PartFamilyName"/> is
    /// given, whose family name matches too. <paramref name="Project"/> names the project the dimensions were verified for;
    /// settings offer only the current project's boxes, and the request carries those. Width lies along the structure's X axis
    /// (its rotation), length along Y, as StructureFootprint uses; the wall thickness is read from the part (0 when none).
    /// </summary>
    /// <param name="Project">The project the box was verified for (e.g. "NCRD"); null or empty for none named.</param>
    /// <param name="PartFamilyName">The structure family name; null or empty matches any family with this size name.</param>
    internal sealed record StructureInnerBox(string PartSizeName, double InnerLengthM, double InnerWidthM, bool Round = false, string Project = null, string PartFamilyName = null);

    /// <summary>The user's pick of a structure for an unconnected end that more than one structure qualifies for by position.</summary>
    internal sealed record StructureChoice(DrawingObjectRef Encasement, PipeEnd End, DrawingObjectRef Structure);

    /// <summary>A different treatment for one end of one encasement than the request's rules give.</summary>
    /// <param name="Treatment">Applies when that end is at a structure; null keeps the rule's.</param>
    /// <param name="Connect">Applies when that end is at a structure; null keeps the rule's.</param>
    internal sealed record ConduitEndOverride(DrawingObjectRef Encasement, PipeEnd End, StructureEndTreatment? Treatment = null, bool? Connect = null);

    /// <summary>
    /// How conduit ends are made. An encasement end is classified in the preview as at a structure (connected to it, or, when
    /// <see cref="MatchUnconnectedEnds"/> is on, by position), at a joint (another chosen encasement's end, in either
    /// direction, with no structure), or free (neither: the conduit keeps the encasement's end point).
    /// </summary>
    internal sealed record ConduitEndRules
    {
        private readonly IReadOnlyList<StructureInnerBox> _boxes = Frozen.List<StructureInnerBox>(null);
        private readonly IReadOnlyList<ConduitEndOverride> _overrides = Frozen.List<ConduitEndOverride>(null);
        private readonly IReadOnlyList<StructureChoice> _choices = Frozen.List<StructureChoice>(null);

        public StructureEndTreatment AtStructure { get; init; } = StructureEndTreatment.InnerWall;

        /// <summary>
        /// False by default: conduits are never connected to structures. True: every conduit end at a structure is connected
        /// to it, which then is required: a connection that fails rolls back that encasement's conduits. Possible only for
        /// conduits in the structure's own network (see <see cref="ConduitDestination"/>); otherwise the preview warns.
        /// </summary>
        public bool ConnectToStructures { get; init; }

        public JointTreatment AtJoint { get; init; } = JointTreatment.Mitre;

        /// <summary>
        /// Off by default (opt-in): only ends connected to a structure count as ending at it. On: an unconnected end also
        /// counts when it lies inside a structure's actual outer face or within <see cref="StructureSearchM"/> of it. The
        /// preview shows every match made this way; when more than one structure qualifies, the end lists the candidates and
        /// the encasement cannot be applied until a <see cref="StructureChoices"/> entry picks one.
        /// </summary>
        public bool MatchUnconnectedEnds { get; init; }

        /// <summary>Search limit for <see cref="MatchUnconnectedEnds"/>: plan distance from a structure's outer face (m; 0 to <see cref="ConduitLimits.MaxStructureSearchM"/>). Default 0.5.</summary>
        public double StructureSearchM { get; init; } = 0.5;

        /// <summary>Structures picked for ambiguous unconnected ends; at most one per encasement end, each one of that end's candidates. Frozen when set.</summary>
        public IReadOnlyList<StructureChoice> StructureChoices { get => _choices; init => _choices = Frozen.List(value); }

        public UnknownFootprintPolicy WhenFootprintUnknown { get; init; } = UnknownFootprintPolicy.LeaveOut;

        /// <summary>Verified inner boxes for the current project; one per part identity (family + size name). Used before the part's own size when given. Frozen when set.</summary>
        public IReadOnlyList<StructureInnerBox> InnerBoxes { get => _boxes; init => _boxes = Frozen.List(value); }

        /// <summary>Per-end exceptions; at most one per encasement end. Frozen when set.</summary>
        public IReadOnlyList<ConduitEndOverride> Overrides { get => _overrides; init => _overrides = Frozen.List(value); }
    }

    /// <summary>Which network the conduits go in.</summary>
    internal enum ConduitDestination
    {
        /// <summary>Default. Each encasement's own network.</summary>
        EncasementNetwork = 0,
        /// <summary>One new network, named <see cref="ConduitRequest.NewNetworkName"/> (made unique if taken).</summary>
        NewNetwork = 1
    }

    /// <summary>
    /// The conduit part, as the part chooser picked it: a parts list part (list, family, size), or only a <see cref="Template"/>
    /// pipe to copy (a conduit part with no family, as the user's drawings often have). A copied part cannot change network
    /// in Civil 3D: its conduits stay in the template pipe's network, and the preview shows that network as the destination.
    /// </summary>
    /// <param name="OutsideDiameterM">
    /// The part's actual outside diameter (m), read by the adapter from the part or the template pipe, never taken from a
    /// callout ("200 mm UPVC"). 0 means it could not be read; the request is then rejected, since fit cannot be approved.
    /// </param>
    internal sealed record ConduitPartChoice(DrawingObjectRef PartsList, string FamilyGuid, string FamilyName, string SizeName, double OutsideDiameterM,
        DrawingObjectRef? Template = null)
    {
        private readonly IReadOnlyDictionary<string, double> _dimensions = Frozen.Map(null);

        public string PartsListName { get; init; } = "";
        public DrawingObjectRef? Family { get; init; }
        public DrawingObjectRef? Size { get; init; }
        /// <summary>The size's dimensions by part parameter name, in metres, to find or add it in a network's list. Frozen when set.</summary>
        public IReadOnlyDictionary<string, double> Dimensions { get => _dimensions; init => _dimensions = Frozen.Map(value); }

        public bool IsComplete =>
            (Template?.IsValid ?? false)
            || (PartsList.IsValid && !string.IsNullOrWhiteSpace(SizeName) && !(string.IsNullOrWhiteSpace(FamilyGuid) && string.IsNullOrWhiteSpace(FamilyName)));

        public string Label => Template.HasValue && !PartsList.IsValid ? $"{SizeName} (copied from the drawing)" : $"{FamilyName} · {SizeName}";
    }

    /// <summary>
    /// A request to plan conduits in chosen encasement pipes (no edit until a plan from it is applied). Carries the template
    /// by value, so editing the library after the preview cannot change the plan. Lists are frozen when set.
    /// </summary>
    internal sealed record ConduitRequest
    {
        private readonly IReadOnlyList<DrawingObjectRef> _encasements = Frozen.List<DrawingObjectRef>(null);

        public DrawingIdentity Drawing { get; init; }

        /// <summary>Metres in one drawing unit, read by the adapter from the drawing. Must be 1 in phase one.</summary>
        public double MetresPerDrawingUnit { get; init; } = 1.0;

        /// <summary>The gravity pipes to fill (duct bank encasements), in the order chosen. The first of each run sets its direction.</summary>
        public IReadOnlyList<DrawingObjectRef> Encasements { get => _encasements; init => _encasements = Frozen.List(value); }

        public ConduitTemplate Template { get; init; }

        /// <summary>The construction variant chosen, by <see cref="ConstructionVariant.Id"/>; null for none. Recorded only: no effect on geometry.</summary>
        public string VariantId { get; init; }

        /// <summary>Where the grid sits in each encasement; null uses the template's <see cref="ConduitTemplate.DefaultSection"/>.</summary>
        public ConduitSection Section { get; init; }

        public ConduitPartChoice Part { get; init; }

        public ConduitEndRules Ends { get; init; } = new ConduitEndRules();

        public ConduitDestination Destination { get; init; } = ConduitDestination.EncasementNetwork;
        public string NewNetworkName { get; init; } = "Conduits";

        /// <summary>
        /// False (default): an encasement any of whose conduits would cross its outside edge is left out, with the reason.
        /// True: it is filled anyway and the preview warns. Never resizes the encasement.
        /// </summary>
        public bool AllowConduitsOutsideEncasement { get; init; }

        /// <summary>The section in effect: <see cref="Section"/>, or the template's default.</summary>
        public ConduitSection EffectiveSection => Section ?? Template?.DefaultSection ?? ConduitSection.Centred;
    }
}
