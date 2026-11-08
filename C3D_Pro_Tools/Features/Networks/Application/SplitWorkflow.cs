using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using C3DProTools.Features.Networks.Contracts;

// The split operation: validates, plans, applies and reports, following the split contracts. It owns the transaction policy
// (one write session for the run, one nested unit per pipe) and calls the drawing only through ISplitDrawingAccess, so its
// rules are tested without Civil 3D. No dialogs: callers render the preview and the result.
namespace C3DProTools.Features.Networks.Application
{
    internal sealed class SplitWorkflow : ISplitOperation
    {
        private readonly ISplitDrawingAccess _drawing;

        public SplitWorkflow(ISplitDrawingAccess drawing) { _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing)); }

        /// <inheritdoc/>
        public SplitPreview Preview(SplitRequest request, CancellationToken cancel)
        {
            List<OperationIssue> issues = SplitValidation.Validate(request);
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) return Rejected(request, issues);
            DrawingIdentity active = _drawing.ActiveDrawing();
            if (!request.Drawing.Matches(active))
                return Rejected(request, new[] { OperationIssue.Error(SplitIssueCodes.DrawingMismatch, "The split was set up for another drawing. Make that drawing current, or set the split up again here.") });
            if (cancel.IsCancellationRequested) return Cancelled(request);

            DrawingSplitPlan read = _drawing.Plan(request, cancel);
            if (cancel.IsCancellationRequested) return Cancelled(request);
            issues.AddRange(read.Issues);

            int missed = read.MissedPicks;
            List<SplitItemPlan> items = new List<SplitItemPlan>();
            foreach (PlannedPipe planned in read.Pipes)
            {
                (SplitItemPlan item, int dropped) = request.Mode == SplitMode.AtPoints ? DropClosePoints(planned) : (planned.Item, 0);
                missed += dropped;
                if (item.Points.Count == 0) continue;
                // A straight pipe's new pieces are made from its part family; a pipe without one (e.g. a duct bank built by
                // copying) cannot be split that way, so it is named here rather than failing at apply. Curved pipes are
                // broken by Civil 3D itself and need no family.
                if (!item.Curved && item.Snapshot?.PartFamily == null)
                {
                    issues.Add(OperationIssue.Warning(SplitIssueCodes.PipeWithoutFamily, $"{item.PipeName} has no part family, so its new pieces cannot be made; it is left out.", null, item.Pipe));
                    continue;
                }
                Dictionary<Point3, double> along = AlongOf(planned);
                OperationIssue problem = SplitValidation.CheckItem(item, p => along.TryGetValue(p, out double d) ? d : double.NaN);
                if (problem == null) { items.Add(item); continue; }
                // A pipe too short for the parts asked, or one that would get too many pieces, is left out and named.
                issues.Add(problem.Code == SplitIssueCodes.ItemPointsInvalid && request.Mode != SplitMode.AtPoints
                    ? OperationIssue.Warning(SplitIssueCodes.ItemPointsInvalid, $"{item.PipeName} is too short to split that way (no piece may be shorter than {SplitLimits.MinPieceLength} {request.Drawing.Unit}); it is left out.", null, item.Pipe)
                    : problem);
            }
            if (missed > 0)
                issues.Add(OperationIssue.Info(SplitIssueCodes.PicksMissed, $"{missed} picked point(s) were not used: not on a pipe within the pick distance, too near a pipe end, or too near another point on the same pipe.", nameof(SplitRequest.Picks)));
            if (items.Count == 0)
                issues.Add(OperationIssue.Info(SplitIssueCodes.NothingToSplit,
                    issues.Any(i => i.Object != null && i.Severity == IssueSeverity.Warning) ? "No pipe can be split that way; the notes above say why each was left out."
                    : request.Mode == SplitMode.AtPoints ? "None of the picked points lies on a pipe (within the pick distance, and not at an end)."
                    : "No pipe is long enough to split that way."));
            return new SplitPreview
            {
                Drawing = request.Drawing,
                Request = request,
                Status = items.Count > 0 ? OperationStatus.Succeeded : OperationStatus.NoChange,
                Items = items,
                MissedPicks = missed,
                Issues = issues
            };
        }

        /// <inheritdoc/>
        public SplitResult Apply(SplitApplyRequest request, IProgress<SplitProgress> progress, CancellationToken cancel)
        {
            List<OperationIssue> issues = SplitValidation.ValidateApply(request, _drawing.ActiveDrawing());
            SplitPreview plan = request?.Preview;
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) return SplitResult.Rejected(plan, issues);
            HashSet<DrawingObjectRef> chosen = new HashSet<DrawingObjectRef>(request.Chosen);
            List<SplitItemPlan> todo = plan.Items.Where(i => chosen.Contains(i.Pipe)).ToList();
            if (todo.Count == 0) return SplitResult.From(plan, Array.Empty<SplitItemOutcome>(), cancelled: false);
            if (cancel.IsCancellationRequested)
                return SplitResult.From(plan, todo.Select(NotAttempted).ToList(), cancelled: true, new[] { CancelledIssue() });

            ISplitWriteSession session;
            try { session = _drawing.BeginWrite(plan.Drawing); }
            catch (Exception ex)
            {
                C3DProLogger.Warn($"Split {plan.PlanId}: the drawing could not be opened for writing", ex);
                return SplitResult.Rejected(plan, new[] { OperationIssue.Error(SplitIssueCodes.DrawingMismatch, "The drawing could not be opened for changes (it may no longer be the current drawing). Preview the split again.") });
            }

            // The session's whole life is handled here, its disposal included, so every way it ends becomes a result.
            List<SplitItemOutcome> outcomes = new List<SplitItemOutcome>();
            SplitItemPlan inFlight = null;          // the pipe whose call has not returned yet
            bool cancelled = false, committed = false;
            string unknown = null;                  // set when the drawing's final state cannot be established
            IReadOnlyList<OperationIssue> notes = Array.Empty<OperationIssue>();
            try
            {
                for (int n = 0; n < todo.Count; n++)
                {
                    SplitItemPlan item = todo[n];
                    // Cancellation between pipes only: the pipe being split is finished or undone, never left half done.
                    if (cancel.IsCancellationRequested) { cancelled = true; break; }
                    progress?.Report(new SplitProgress(n, todo.Count, item.PipeName));
                    inFlight = item;
                    OperationIssue stale = SplitValidation.Compare(item, session.ReadPipe(item.Pipe));
                    outcomes.Add(stale != null
                        ? new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.Rejected, null, null, stale)
                        : session.SplitPipe(item, plan.Request?.Structure));
                    inFlight = null;
                }
                // Nothing split: the session is left uncommitted, so parts list changes made on the way are undone too.
                if (outcomes.Any(o => o.Status == ItemStatus.Committed))
                {
                    notes = session.Notes;
                    session.Commit();
                    committed = true;
                }
            }
            catch (Exception ex)
            {
                // Unexpected (a pipe's call, or the commit). The session is then disposed without commit; that should undo
                // everything, but it is not verified, so nothing that may have changed is claimed as rolled back.
                C3DProLogger.Warn($"Split {plan.PlanId}: stopped by an unexpected error", ex);
                unknown = inFlight != null
                    ? "The split stopped on an unexpected error. Check the drawing before trying again (UNDO if needed)."
                    : "The changes could not be saved to the drawing. Check the drawing before trying again.";
            }
            try { session.Dispose(); }
            catch (Exception ex)
            {
                C3DProLogger.Warn($"Split {plan.PlanId}: the write session could not be closed cleanly", ex);
                if (committed)
                    issues.Add(OperationIssue.Warning(SplitIssueCodes.SessionNotClosed, "The split was saved, but Civil 3D reported a problem releasing the drawing. Save your work and check the drawing."));
                else if (unknown == null && outcomes.Any(o => o.Status == ItemStatus.Committed))
                    unknown = "The split was not saved, and undoing it could not be confirmed. Check the drawing before trying again.";
            }
            if (unknown != null) return Unknown(plan, outcomes, inFlight, todo, unknown);

            outcomes.AddRange(todo.Skip(outcomes.Count).Select(NotAttempted));
            progress?.Report(new SplitProgress(outcomes.Count(o => o.Status != ItemStatus.NotAttempted), todo.Count, null));
            if (committed) issues.AddRange(notes);
            if (cancelled) issues.Add(CancelledIssue());
            return SplitResult.From(plan, outcomes, cancelled, issues);
        }

        // ---------- Planning rules ----------

        /// <summary>
        /// Picked points closer than the shortest piece to the previous point used on the same pipe are dropped (the first is
        /// kept); returns the pipe's plan with the points left and how many were dropped.
        /// </summary>
        internal static (SplitItemPlan item, int dropped) DropClosePoints(PlannedPipe planned)
        {
            List<Point3> keep = new List<Point3>();
            double last = double.NegativeInfinity;
            int dropped = 0;
            for (int i = 0; i < planned.Item.Points.Count; i++)
            {
                double d = planned.Along[i];
                if (d - last < SplitLimits.MinPieceLength - SplitValidation.PositionTolerance) { dropped++; continue; }
                keep.Add(planned.Item.Points[i]);
                last = d;
            }
            return (dropped == 0 ? planned.Item : planned.Item with { Points = keep }, dropped);
        }

        private static Dictionary<Point3, double> AlongOf(PlannedPipe planned)
        {
            Dictionary<Point3, double> along = new Dictionary<Point3, double>();
            for (int i = 0; i < planned.Item.Points.Count && i < planned.Along.Count; i++) along[planned.Item.Points[i]] = planned.Along[i];
            return along;
        }

        // ---------- Results ----------

        private static SplitPreview Rejected(SplitRequest request, IEnumerable<OperationIssue> issues) =>
            new SplitPreview { Drawing = request?.Drawing, Request = request, Status = OperationStatus.Rejected, Issues = issues.ToList() };

        private static SplitPreview Cancelled(SplitRequest request) =>
            new SplitPreview { Drawing = request.Drawing, Request = request, Status = OperationStatus.Cancelled, Issues = new[] { CancelledIssue() } };

        private static OperationIssue CancelledIssue() => OperationIssue.Info(SplitIssueCodes.Cancelled, "The split was cancelled. Pipes listed as split were split; the others were not touched.");

        private static SplitItemOutcome NotAttempted(SplitItemPlan item) => new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.NotAttempted, null, null);

        /// <summary>
        /// A result whose drawing state is not known: pipes split before (committed only into the session) and the pipe whose
        /// call did not return are <see cref="ItemStatus.OutcomeUnknown"/>; pipes undone on their own or rejected stay so; the
        /// rest were not attempted. The status is then <see cref="OperationStatus.OutcomeUnknown"/>, never a claimed rollback.
        /// </summary>
        private static SplitResult Unknown(SplitPreview plan, List<SplitItemOutcome> outcomes, SplitItemPlan inFlight, List<SplitItemPlan> todo, string message)
        {
            List<SplitItemOutcome> items = outcomes.Select(o => o.Status == ItemStatus.Committed ? o with { Status = ItemStatus.OutcomeUnknown } : o).ToList();
            if (inFlight != null)
                items.Add(new SplitItemOutcome(inFlight.Pipe, inFlight.PipeName, ItemStatus.OutcomeUnknown, null, null,
                    OperationIssue.Error(SplitIssueCodes.ItemFailed, $"{inFlight.PipeName}: the split stopped while this pipe was being split; check it in the drawing.", null, inFlight.Pipe)));
            items.AddRange(todo.Skip(items.Count).Select(NotAttempted));
            // The run itself is in doubt whatever its items say: the drawing must be read again before anything is retried.
            return SplitResult.From(plan, items, cancelled: false, new[] { OperationIssue.Error(SplitIssueCodes.OutcomeUnknown, message) })
                with { Status = OperationStatus.OutcomeUnknown };
        }
    }
}
