using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using C3DProTools.Features.Networks.Contracts;

// Generate Conduits: validates, plans, applies and reports, following the conduit contracts. Owns the transaction policy
// (one write session per Apply, one nested unit per encasement) and reaches the drawing only through IConduitDrawingAccess.
namespace C3DProTools.Features.Networks.Application
{
    internal sealed class ConduitWorkflow : IConduitOperation
    {
        private readonly IConduitDrawingAccess _drawing;

        public ConduitWorkflow(IConduitDrawingAccess drawing) { _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing)); }

        public ConduitPreview Preview(ConduitRequest request, CancellationToken cancel)
        {
            // The drawing's units are read here, not trusted from the caller.
            if (request != null) request = request with { MetresPerDrawingUnit = _drawing.MetresPerDrawingUnit() };
            List<OperationIssue> issues = ConduitValidation.Validate(request);
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) return Rejected(request, issues);
            if (!request.Drawing.Matches(_drawing.ActiveDrawing()))
                return Rejected(request, new[] { OperationIssue.Error(ConduitIssueCodes.DrawingMismatch, "The conduits were set up for another drawing. Make that drawing current, or set them up again here.") });
            if (cancel.IsCancellationRequested) return Cancelled(request);

            ConduitDrawingRead read = _drawing.Read(request, cancel);
            if (cancel.IsCancellationRequested) return Cancelled(request);
            (List<ConduitSourcePlan> items, List<OperationIssue> planIssues) = ConduitPlanner.Plan(request, read, cancel);
            if (cancel.IsCancellationRequested) return Cancelled(request);
            issues.AddRange(planIssues);

            bool any = items.Any(i => i.CanApply);
            if (!any)
                issues.Add(OperationIssue.Info(ConduitIssueCodes.NothingToGenerate, items.Count == 0
                    ? "None of the chosen encasements could be read."
                    : "No chosen encasement can be filled; each one's notes say why."));
            return new ConduitPreview
            {
                Drawing = request.Drawing,
                Request = request,
                Status = any ? OperationStatus.Succeeded : OperationStatus.NoChange,
                Items = items,
                Issues = issues,
                TemplateNotes = request.Template.Notes
            };
        }

        public ConduitResult Apply(ConduitApplyRequest request, IProgress<ConduitProgress> progress, CancellationToken cancel)
        {
            List<OperationIssue> issues = ConduitValidation.ValidateApply(request, _drawing.ActiveDrawing());
            ConduitPreview plan = request?.Preview;
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) return ConduitResult.Rejected(plan, issues);
            HashSet<DrawingObjectRef> chosen = new HashSet<DrawingObjectRef>(request.Chosen);
            List<ConduitSourcePlan> todo = plan.Items.Where(i => chosen.Contains(i.Encasement)).ToList();
            if (todo.Count == 0) return ConduitResult.From(plan, Array.Empty<ConduitItemOutcome>(), cancelled: false);
            if (cancel.IsCancellationRequested)
                return ConduitResult.From(plan, todo.Select(NotAttempted).ToList(), cancelled: true, new[] { CancelledIssue() });

            IConduitWriteSession session;
            try { session = _drawing.BeginWrite(plan.Drawing); }
            catch (Exception ex)
            {
                C3DProLogger.Warn($"Conduits {plan.PlanId}: the drawing could not be opened for writing", ex);
                return ConduitResult.Rejected(plan, new[] { OperationIssue.Error(ConduitIssueCodes.DrawingMismatch, "The drawing could not be opened for changes (it may no longer be the current drawing). Preview again.") });
            }

            Dictionary<DrawingObjectRef, EncasementSnapshot> planned = plan.Items.GroupBy(i => i.Encasement).ToDictionary(g => g.Key, g => g.First().Snapshot);
            ConduitRequest req = plan.Request;
            List<ConduitItemOutcome> outcomes = new List<ConduitItemOutcome>();
            ConduitSourcePlan inFlight = null;
            bool cancelled = false, committed = false;
            string unknown = null;
            IReadOnlyList<OperationIssue> notes = Array.Empty<OperationIssue>();
            try
            {
                for (int n = 0; n < todo.Count; n++)
                {
                    ConduitSourcePlan item = todo[n];
                    // Between encasements only: the one being filled is finished or undone, never left half made.
                    if (cancel.IsCancellationRequested) { cancelled = true; break; }
                    progress?.Report(new ConduitProgress(n, todo.Count, item.Name));
                    inFlight = item;
                    OperationIssue stale = ConduitValidation.Compare(item, session.ReadEncasement, s => session.ReadStructure(s, req), planned,
                        i => session.ResolveDestination(i, req), i => session.ResolvePart(i, req));
                    outcomes.Add(stale != null
                        ? new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.Rejected, null, 0, stale)
                        : session.Fill(item, req));
                    inFlight = null;
                }
                if (outcomes.Any(o => o.Status == ItemStatus.Committed))
                {
                    notes = session.Notes;
                    session.Commit();
                    committed = true;
                }
            }
            catch (Exception ex)
            {
                C3DProLogger.Warn($"Conduits {plan.PlanId}: stopped by an unexpected error", ex);
                unknown = inFlight != null
                    ? "Conduit generation stopped on an unexpected error. Check the drawing before trying again (UNDO if needed)."
                    : "The changes could not be saved to the drawing. Check the drawing before trying again.";
            }
            try { session.Dispose(); }
            catch (Exception ex)
            {
                C3DProLogger.Warn($"Conduits {plan.PlanId}: the write session could not be closed cleanly", ex);
                if (committed)
                    issues.Add(OperationIssue.Warning(ConduitIssueCodes.SessionNotClosed, "The conduits were saved, but Civil 3D reported a problem releasing the drawing. Save your work and check the drawing."));
                else if (unknown == null && outcomes.Any(o => o.Status == ItemStatus.Committed))
                    unknown = "The conduits were not saved, and undoing them could not be confirmed. Check the drawing before trying again.";
            }
            if (unknown != null) return Unknown(plan, outcomes, inFlight, todo, unknown);

            outcomes.AddRange(todo.Skip(outcomes.Count).Select(NotAttempted));
            progress?.Report(new ConduitProgress(outcomes.Count(o => o.Status != ItemStatus.NotAttempted), todo.Count, null));
            if (committed) issues.AddRange(notes);
            issues.AddRange(ConduitValidation.IncompleteRuns(plan, outcomes));
            if (cancelled) issues.Add(CancelledIssue());
            return ConduitResult.From(plan, outcomes, cancelled, issues);
        }

        private static ConduitPreview Rejected(ConduitRequest request, IEnumerable<OperationIssue> issues) =>
            new ConduitPreview { Drawing = request?.Drawing, Request = request, Status = OperationStatus.Rejected, Issues = issues.ToList() };

        private static ConduitPreview Cancelled(ConduitRequest request) =>
            new ConduitPreview { Drawing = request.Drawing, Request = request, Status = OperationStatus.Cancelled, Issues = new[] { CancelledIssue() } };

        private static OperationIssue CancelledIssue() => OperationIssue.Info(ConduitIssueCodes.Cancelled, "Conduit generation was cancelled. Encasements listed as filled were filled; the others were not touched.");

        private static ConduitItemOutcome NotAttempted(ConduitSourcePlan item) => new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.NotAttempted, null);

        /// <summary>A result whose drawing state is not known: nothing that may have changed is claimed as committed or undone.</summary>
        private static ConduitResult Unknown(ConduitPreview plan, List<ConduitItemOutcome> outcomes, ConduitSourcePlan inFlight, List<ConduitSourcePlan> todo, string message)
        {
            List<ConduitItemOutcome> items = outcomes.Select(o => o.Status == ItemStatus.Committed ? o with { Status = ItemStatus.OutcomeUnknown } : o).ToList();
            if (inFlight != null)
                items.Add(new ConduitItemOutcome(inFlight.Encasement, inFlight.Name, ItemStatus.OutcomeUnknown, null, 0,
                    OperationIssue.Error(ConduitIssueCodes.ItemFailed, $"{inFlight.Name}: conduit generation stopped while it was being filled; check it in the drawing.", null, inFlight.Encasement)));
            items.AddRange(todo.Skip(items.Count).Select(NotAttempted));
            List<OperationIssue> issues = new List<OperationIssue> { OperationIssue.Error(ConduitIssueCodes.OutcomeUnknown, message) };
            issues.AddRange(ConduitValidation.IncompleteRuns(plan, items));
            return ConduitResult.From(plan, items, cancelled: false, issues) with { Status = OperationStatus.OutcomeUnknown };
        }
    }
}
