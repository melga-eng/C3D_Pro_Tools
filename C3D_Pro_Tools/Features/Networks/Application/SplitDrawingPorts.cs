using System;
using System.Collections.Generic;
using System.Threading;
using C3DProTools.Features.Networks.Contracts;

// What the split workflow needs from the drawing, on plain values. The Civil 3D adapter (Features/Networks/Civil3D)
// implements it; tests use a fake. Nothing here references Autodesk or UI types.
namespace C3DProTools.Features.Networks.Application
{
    /// <summary>A pipe the drawing would split, with each split point's plan distance from the pipe's start.</summary>
    /// <param name="Along">One distance per point of <see cref="SplitItemPlan.Points"/>, in the same order (drawing units).</param>
    internal sealed record PlannedPipe(SplitItemPlan Item, IReadOnlyList<double> Along)
    {
        private readonly IReadOnlyList<double> _along = Frozen.List(Along);
        public IReadOnlyList<double> Along { get => _along; init => _along = Frozen.List(value); }
    }

    /// <summary>The drawing's plan for a request: the pipes it would split, and the picked points not on any pipe.</summary>
    /// <param name="Issues">Problems found while reading (e.g. a chosen pipe that is gone); the rest is planned.</param>
    internal sealed record DrawingSplitPlan(IReadOnlyList<PlannedPipe> Pipes, int MissedPicks, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<PlannedPipe> _pipes = Frozen.List(Pipes);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<PlannedPipe> Pipes { get => _pipes; init => _pipes = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>The split's access to the drawing. Every call runs on Civil 3D's main thread.</summary>
    internal interface ISplitDrawingAccess
    {
        /// <summary>The drawing an operation would act on now (the active document); null when no drawing is open.</summary>
        DrawingIdentity ActiveDrawing();

        /// <summary>
        /// Reads the drawing and finds where each pipe would be split, as the request asks (the existing split engine). Reads
        /// only. The request is valid and for <see cref="ActiveDrawing"/> (the workflow checked both).
        /// </summary>
        DrawingSplitPlan Plan(SplitRequest request, CancellationToken cancel);

        /// <summary>
        /// Opens the drawing for writing: takes the document lock and starts the outer transaction. Throws when
        /// <paramref name="drawing"/> is not the active drawing any more. Disposing without <see cref="ISplitWriteSession.Commit"/>
        /// undoes everything done in the session.
        /// </summary>
        ISplitWriteSession BeginWrite(DrawingIdentity drawing);
    }

    /// <summary>One write to the drawing: one document lock, one outer transaction (one UNDO for the whole run, as today).</summary>
    internal interface ISplitWriteSession : IDisposable
    {
        /// <summary>The pipe as it is now, for the stale check; null when it is gone or not a gravity pipe.</summary>
        PipeSnapshot ReadPipe(DrawingObjectRef pipe);

        /// <summary>
        /// Splits one pipe in its own nested transaction: <see cref="ItemStatus.Committed"/> (into the session) with its new
        /// pipes and structures, or <see cref="ItemStatus.RolledBack"/> with the reason, the pipe left as it was. Never throws
        /// for a pipe that cannot be split.
        /// </summary>
        SplitItemOutcome SplitPipe(SplitItemPlan item, SplitStructureChoice structure);

        /// <summary>Notes from the session so far (parts list changes made on the way).</summary>
        IReadOnlyList<OperationIssue> Notes { get; }

        /// <summary>Commits the outer transaction: only then are the committed items in the drawing.</summary>
        void Commit();
    }
}
