using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    /// <summary>
    /// Presentation model for one item outcome after Apply.
    /// Shows its status, changes count (+N / ~N / -N), and any issues raised.
    /// </summary>
    internal sealed class BuildItemOutcomeViewModel
    {
        public BuildItemOutcome Outcome { get; }
        public string ItemKey => Outcome.ItemKey;
        public ItemStatus Status => Outcome.Status;
        public int Added => Outcome.Added;
        public int Updated => Outcome.Updated;
        public int Removed => Outcome.Removed;
        public string CountsSummary => $"+{Added} / ~{Updated} / -{Removed}";
        public IReadOnlyList<OperationIssue> Issues => Outcome.Issues;
        public bool HasIssues => Outcome.Issues != null && Outcome.Issues.Count > 0;
        public string IssuesSummary => HasIssues ? string.Join("; ", Outcome.Issues.Select(i => i.Message)) : "";
        public bool IsCommitted => Status == ItemStatus.Committed;
        public bool IsFailed => Status == ItemStatus.RolledBack || Status == ItemStatus.Rejected;

        public BuildItemOutcomeViewModel(BuildItemOutcome outcome)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        }
    }
}
