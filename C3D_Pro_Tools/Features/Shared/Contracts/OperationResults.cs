using System;
using System.Collections.Generic;
using System.Linq;

// Statuses, issues and item outcomes as ARCHITECTURE.md ("Error and result conventions") defines them. Shared by every
// feature (networks, sheets): moved here from Features/Networks/Contracts when the sheet production feature needed them.
namespace C3DProTools.Features.Shared.Contracts
{
    /// <summary>How an operation (or a preview) ended. Never <see cref="Succeeded"/> before the changes are committed.</summary>
    internal enum OperationStatus
    {
        /// <summary>All requested changes committed and their required checks passed.</summary>
        Succeeded,
        /// <summary>A valid request that needed no edit.</summary>
        NoChange,
        /// <summary>Invalid input, the wrong drawing or a stale plan: nothing was applied.</summary>
        Rejected,
        /// <summary>Stopped at a safe point between items; the result says which items were committed before.</summary>
        Cancelled,
        /// <summary>The atomic unit (every unit, for a batch) failed and its edits were undone.</summary>
        RolledBack,
        /// <summary>Some independent items committed and others did not; each item's outcome says which.</summary>
        PartiallySucceeded,
        /// <summary>Work started but its final state is not known: read the drawing again before retrying.</summary>
        OutcomeUnknown
    }

    /// <summary>How much of an operation is kept or undone together.</summary>
    internal enum Atomicity
    {
        /// <summary>The whole operation is one unit: all of it is kept, or none.</summary>
        WholeOperation,
        /// <summary>Each item (for a split: each pipe, with its new pieces and structures) is kept or undone on its own.</summary>
        PerItem
    }

    /// <summary>What happened to one item of a batch.</summary>
    internal enum ItemStatus
    {
        /// <summary>Its changes are committed.</summary>
        Committed,
        /// <summary>It failed; its own changes were undone and the drawing holds it as it was.</summary>
        RolledBack,
        /// <summary>Not attempted: its object was missing or changed since the preview (stale), or not valid any more.</summary>
        Rejected,
        /// <summary>Not attempted because the operation was cancelled first.</summary>
        NotAttempted,
        /// <summary>Started, but whether its changes are in the drawing is not known (the rollback itself failed).</summary>
        OutcomeUnknown
    }

    internal enum IssueSeverity { Info, Warning, Error }

    /// <summary>
    /// A problem or note for the user. <see cref="Code"/> is stable (for tests and logs); <see cref="Message"/> explains the
    /// problem and what to do next, in plain words. Technical detail goes to the log, not here.
    /// </summary>
    /// <param name="Field">The request field it is about (e.g. "Every"), so the form can show it beside that field; null when none.</param>
    /// <param name="Object">The object it is about, in the operation's drawing; null when none.</param>
    internal sealed record OperationIssue(string Code, IssueSeverity Severity, string Message, string Field = null, DrawingObjectRef? Object = null)
    {
        public static OperationIssue Error(string code, string message, string field = null, DrawingObjectRef? obj = null) => new OperationIssue(code, IssueSeverity.Error, message, field, obj);
        public static OperationIssue Warning(string code, string message, string field = null, DrawingObjectRef? obj = null) => new OperationIssue(code, IssueSeverity.Warning, message, field, obj);
        public static OperationIssue Info(string code, string message, string field = null, DrawingObjectRef? obj = null) => new OperationIssue(code, IssueSeverity.Info, message, field, obj);
    }

    internal static class OperationOutcome
    {
        /// <summary>
        /// The status of a per-item batch from its items' outcomes: any unknown outcome makes the whole result unknown; a
        /// cancelled run is <see cref="OperationStatus.Cancelled"/> (its items still say what was committed); otherwise all
        /// committed is success, none committed is a rollback (or a rejection when nothing was even attempted), and a mix is a
        /// partial success. No items at all is no change.
        /// </summary>
        public static OperationStatus Summarize(IReadOnlyCollection<ItemStatus> items, bool cancelled)
        {
            if (items.Contains(ItemStatus.OutcomeUnknown)) return OperationStatus.OutcomeUnknown;
            if (cancelled) return OperationStatus.Cancelled;
            if (items.Count == 0) return OperationStatus.NoChange;
            int committed = items.Count(s => s == ItemStatus.Committed);
            if (committed == items.Count) return OperationStatus.Succeeded;
            if (committed > 0) return OperationStatus.PartiallySucceeded;
            return items.All(s => s == ItemStatus.Rejected) ? OperationStatus.Rejected : OperationStatus.RolledBack;
        }
    }
}
