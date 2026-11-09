using System;
using System.Collections.Generic;
using System.Threading;
using C3DProTools.Features.Networks.Contracts;

// What Generate Conduits needs from the drawing, on plain values. The Civil 3D adapter implements it; tests use a fake.
// The planner (ConduitPlanner) does all geometry from these values, so its rules are tested without Civil 3D.
namespace C3DProTools.Features.Networks.Application
{
    /// <summary>
    /// A structure as read from the drawing, before its footprint is resolved. Inner sizes are 0 when the part reports none
    /// (many manholes do); the planner then uses a user box or reports the footprint unknown, never the drawn extents.
    /// </summary>
    internal sealed record StructureRead(DrawingObjectRef Structure, string Name, string PartFamilyName, string PartSizeName, DrawingObjectRef Network,
        Point3 Location, double RotationRad, bool Round, double InnerWidthM, double InnerLengthM, double WallM);

    /// <summary>One chosen encasement as read: its snapshot, whether it is supported, and where and with which part its conduits would be made.</summary>
    /// <param name="Unsupported">Why it cannot be filled (segmented or flex pipe…); null when it can.</param>
    /// <param name="Part">The part resolved for <paramref name="Destination"/>; null with <paramref name="PartIssue"/> when it cannot be.</param>
    internal sealed record EncasementRead(DrawingObjectRef Encasement, string Name, EncasementSnapshot Snapshot, string Unsupported,
        ConduitDestinationPlan Destination, ConduitPartSnapshot Part, OperationIssue PartIssue = null);

    /// <summary>The drawing as the planner needs it: the chosen encasements and the structures their ends may meet.</summary>
    /// <param name="Structures">Every structure an encasement end is connected to, plus (when matching unconnected ends) those near any chosen end.</param>
    /// <param name="Issues">Request-level problems found while reading (a chosen encasement that is gone or not a gravity pipe).</param>
    internal sealed record ConduitDrawingRead(IReadOnlyList<EncasementRead> Encasements, IReadOnlyList<StructureRead> Structures, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<EncasementRead> _enc = Frozen.List(Encasements);
        private readonly IReadOnlyList<StructureRead> _str = Frozen.List(Structures);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<EncasementRead> Encasements { get => _enc; init => _enc = Frozen.List(value); }
        public IReadOnlyList<StructureRead> Structures { get => _str; init => _str = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>Generate Conduits' access to the drawing. Every call runs on Civil 3D's main thread.</summary>
    internal interface IConduitDrawingAccess
    {
        /// <summary>The drawing an operation would act on now (the active document); null when none is open.</summary>
        DrawingIdentity ActiveDrawing();

        /// <summary>Metres in one unit of the active drawing (from its insertion units); NaN when they are not set.</summary>
        double MetresPerDrawingUnit();

        /// <summary>Reads what the planner needs for a valid request for the active drawing. Reads only.</summary>
        ConduitDrawingRead Read(ConduitRequest request, CancellationToken cancel);

        /// <summary>
        /// Opens the drawing for writing: document lock and one outer transaction. Throws when <paramref name="drawing"/> is not
        /// the active drawing. Disposing without <see cref="IConduitWriteSession.Commit"/> undoes everything done in it.
        /// </summary>
        IConduitWriteSession BeginWrite(DrawingIdentity drawing);
    }

    /// <summary>One write: one lock, one outer transaction (one UNDO intended), one nested transaction per encasement.</summary>
    internal interface IConduitWriteSession : IDisposable
    {
        EncasementSnapshot ReadEncasement(DrawingObjectRef encasement);
        /// <summary>The structure as planned, re-read and resolved with the request's inner boxes; null when it is gone.</summary>
        StructureSnapshot ReadStructure(DrawingObjectRef structure, ConduitRequest request);
        /// <summary>Where the item's conduits would go now (the stale check compares it with the plan).</summary>
        ConduitDestinationPlan ResolveDestination(ConduitSourcePlan item, ConduitRequest request);
        /// <summary>The part the item's conduits would be made of now; null when it cannot be resolved.</summary>
        ConduitPartSnapshot ResolvePart(ConduitSourcePlan item, ConduitRequest request);

        /// <summary>
        /// Makes all of the item's planned conduits (and, where planned, their connections) in one nested transaction:
        /// <see cref="ItemStatus.Committed"/> with the new conduits, or <see cref="ItemStatus.RolledBack"/> with the reason and
        /// nothing left behind. Never throws for an item that cannot be made.
        /// </summary>
        ConduitItemOutcome Fill(ConduitSourcePlan item, ConduitRequest request);

        /// <summary>Notes so far (parts list changes, conduits that stayed in a copied part's network).</summary>
        IReadOnlyList<OperationIssue> Notes { get; }

        void Commit();
    }
}
