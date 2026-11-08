using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// What a split did: per pipe, what was committed, and the whole run's status. Counts come only from committed items.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>What happened to one pipe of the plan.</summary>
    /// <param name="NewPipes">The pieces made (committed items only; a straight pipe's first piece is the original pipe and is not listed).</param>
    /// <param name="NewStructures">The structures placed at its split points (committed items only).</param>
    /// <param name="Issue">Why it was not committed, or a warning about it; null when none.</param>
    internal sealed record SplitItemOutcome(DrawingObjectRef Pipe, string PipeName, ItemStatus Status,
        IReadOnlyList<DrawingObjectRef> NewPipes, IReadOnlyList<DrawingObjectRef> NewStructures, OperationIssue Issue = null)
    {
        private readonly IReadOnlyList<DrawingObjectRef> _newPipes = Frozen.List(NewPipes), _newStructures = Frozen.List(NewStructures);
        public IReadOnlyList<DrawingObjectRef> NewPipes { get => _newPipes; init => _newPipes = Frozen.List(value); }
        public IReadOnlyList<DrawingObjectRef> NewStructures { get => _newStructures; init => _newStructures = Frozen.List(value); }
    }

    /// <summary>
    /// The result of applying a split. Every chosen pipe has an outcome. Counts are of committed items only, so "split 3 pipes"
    /// is never said for a pipe whose split was undone.
    /// </summary>
    internal sealed record SplitResult
    {
        private readonly IReadOnlyList<SplitItemOutcome> _items = Frozen.List<SplitItemOutcome>(null);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);

        public OperationStatus Status { get; init; }
        public DrawingIdentity Drawing { get; init; }
        public Guid PlanId { get; init; }
        public IReadOnlyList<SplitItemOutcome> Items { get => _items; init => _items = Frozen.List(value); }
        /// <summary>Issues of the whole run (wrong drawing, plan not applicable, cancellation) and notes such as parts list changes made.</summary>
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }

        /// <summary>Pipes split and committed.</summary>
        public int PipesSplit => Items.Count(i => i.Status == ItemStatus.Committed);
        /// <summary>New pipes made by committed splits.</summary>
        public int PipesCreated => Items.Where(i => i.Status == ItemStatus.Committed).Sum(i => i.NewPipes.Count);
        /// <summary>Pieces the split pipes now are (each original plus its new pipes), as Network Editor reports it.</summary>
        public int PiecesTotal => PipesSplit + PipesCreated;
        public int StructuresAdded => Items.Where(i => i.Status == ItemStatus.Committed).Sum(i => i.NewStructures.Count);

        /// <summary>A result from item outcomes, its status from <see cref="OperationOutcome.Summarize"/>.</summary>
        public static SplitResult From(SplitPreview plan, IReadOnlyList<SplitItemOutcome> items, bool cancelled, IReadOnlyList<OperationIssue> issues = null) => new SplitResult
        {
            Status = OperationOutcome.Summarize(items.Select(i => i.Status).ToList(), cancelled),
            Drawing = plan?.Drawing,
            PlanId = plan?.PlanId ?? Guid.Empty,
            Items = items,
            Issues = issues
        };

        /// <summary>A result for an apply request refused as a whole (nothing attempted, nothing changed).</summary>
        public static SplitResult Rejected(SplitPreview plan, IReadOnlyList<OperationIssue> issues) => new SplitResult
        {
            Status = OperationStatus.Rejected,
            Drawing = plan?.Drawing,
            PlanId = plan?.PlanId ?? Guid.Empty,
            Issues = issues
        };
    }

    /// <summary>
    /// The split operation, as the form (and other callers) use it. Implemented by the application layer; both calls run on
    /// Civil 3D's main thread, take the document lock themselves, and never show a dialog (the caller renders the result).
    /// </summary>
    internal interface ISplitOperation
    {
        /// <summary>
        /// Validates the request and plans the split from the drawing as it is now. Reads only; nothing is changed. A request
        /// for a drawing that is not open (or not the one it names) is rejected. Cancelling returns a
        /// <see cref="OperationStatus.Cancelled"/> preview (code <see cref="SplitIssueCodes.Cancelled"/>) with no items.
        /// </summary>
        SplitPreview Preview(SplitRequest request, CancellationToken cancel);

        /// <summary>
        /// Applies the chosen items of a preview, pipe by pipe, each in its own transaction (<see cref="Atomicity.PerItem"/>):
        /// a pipe is committed with all its pieces, structures and connections, or left exactly as it was. Before writing, the
        /// drawing is checked against <see cref="SplitPreview.Drawing"/> (a mismatch rejects everything) and each pipe against
        /// its <see cref="PipeSnapshot"/> (a changed or missing pipe is rejected and the rest go on). Cancellation is checked
        /// between pipes only: the pipe being split is finished or undone, later pipes are <see cref="ItemStatus.NotAttempted"/>,
        /// and the result lists what was committed. Never retried automatically.
        /// </summary>
        SplitResult Apply(SplitApplyRequest request, IProgress<SplitProgress> progress, CancellationToken cancel);
    }
}
