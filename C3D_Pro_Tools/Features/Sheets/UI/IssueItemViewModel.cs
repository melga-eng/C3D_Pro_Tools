using System;
using C3DProTools.Features.Shared.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    internal sealed class IssueItemViewModel
    {
        public OperationIssue Issue { get; }
        public string Code => Issue.Code;
        public IssueSeverity Severity => Issue.Severity;
        public string Message => Issue.Message;
        public string Field => Issue.Field ?? "";
        public string ObjectHandle => Issue.Object.HasValue ? Issue.Object.Value.Handle : "";
        public DrawingObjectRef? ObjectRef => Issue.Object;
        public string Layout { get; }

        public IssueItemViewModel(OperationIssue issue, string layout = "")
        {
            Issue = issue ?? throw new ArgumentNullException(nameof(issue));
            Layout = layout ?? "";
        }
    }
}
