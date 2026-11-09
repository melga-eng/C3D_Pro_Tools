using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// What Generate Conduits did: per encasement, what was committed. Counts come only from committed items.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>What happened to one encasement of the plan.</summary>
    /// <param name="NewConduits">The conduits made (committed items only).</param>
    /// <param name="ConnectionsMade">Conduit ends connected to structures and verified after connecting (committed items only).</param>
    /// <param name="Issue">Why it was not committed, or a warning about it; null when none.</param>
    internal sealed record ConduitItemOutcome(DrawingObjectRef Encasement, string Name, ItemStatus Status, IReadOnlyList<DrawingObjectRef> NewConduits,
        int ConnectionsMade = 0, OperationIssue Issue = null)
    {
        private readonly IReadOnlyList<DrawingObjectRef> _new = Frozen.List(NewConduits);
        public IReadOnlyList<DrawingObjectRef> NewConduits { get => _new; init => _new = Frozen.List(value); }
    }

    /// <summary>The result of applying a conduit plan. Every chosen encasement has an outcome.</summary>
    internal sealed record ConduitResult
    {
        private readonly IReadOnlyList<ConduitItemOutcome> _items = Frozen.List<ConduitItemOutcome>(null);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);

        public OperationStatus Status { get; init; }
        public DrawingIdentity Drawing { get; init; }
        public Guid PlanId { get; init; }
        public IReadOnlyList<ConduitItemOutcome> Items { get => _items; init => _items = Frozen.List(value); }
        /// <summary>Run-level issues (wrong drawing, cancellation) and notes such as parts list changes or conduits that stayed in a template's network.</summary>
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }

        public int EncasementsFilled => Items.Count(i => i.Status == ItemStatus.Committed);
        public int ConduitsCreated => Items.Where(i => i.Status == ItemStatus.Committed).Sum(i => i.NewConduits.Count);
        public int ConnectionsMade => Items.Where(i => i.Status == ItemStatus.Committed).Sum(i => i.ConnectionsMade);

        public static ConduitResult From(ConduitPreview plan, IReadOnlyList<ConduitItemOutcome> items, bool cancelled, IReadOnlyList<OperationIssue> issues = null) => new ConduitResult
        {
            Status = OperationOutcome.Summarize(items.Select(i => i.Status).ToList(), cancelled),
            Drawing = plan?.Drawing,
            PlanId = plan?.PlanId ?? Guid.Empty,
            Items = items,
            Issues = issues
        };

        public static ConduitResult Rejected(ConduitPreview plan, IReadOnlyList<OperationIssue> issues) => new ConduitResult
        {
            Status = OperationStatus.Rejected,
            Drawing = plan?.Drawing,
            PlanId = plan?.PlanId ?? Guid.Empty,
            Issues = issues
        };
    }

    /// <summary>
    /// Generate Conduits, as the form uses it. Implemented by the application layer; both calls run on Civil 3D's main thread,
    /// take the document lock themselves and never show a dialog.
    /// </summary>
    internal interface IConduitOperation
    {
        /// <summary>
        /// Validates the request and plans every chosen encasement from the drawing as it is now. Reads only. A wrong drawing
        /// or invalid request is rejected; cancelling returns a <see cref="OperationStatus.Cancelled"/> preview with no items.
        /// </summary>
        ConduitPreview Preview(ConduitRequest request, CancellationToken cancel);

        /// <summary>
        /// Fills the chosen encasements of a preview, one atomic unit each (<see cref="Atomicity.PerItem"/>): all of an
        /// encasement's conduits (and, when asked, their verified connections) are committed, or none. One outer transaction
        /// for the run, so a single UNDO is intended (to verify live). Before writing, the drawing is checked against the
        /// preview's (mismatch rejects all) and each encasement, its structures and its joint neighbours against their
        /// snapshots (a change rejects that encasement). Cancellation between encasements only. Never retried automatically.
        /// </summary>
        ConduitResult Apply(ConduitApplyRequest request, IProgress<ConduitProgress> progress, CancellationToken cancel);
    }
}
