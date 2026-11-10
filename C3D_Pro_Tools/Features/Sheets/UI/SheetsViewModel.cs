using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    internal sealed class SheetsViewModel : INotifyPropertyChanged
    {
        private readonly ISheetProductionService _service;
        private CancellationTokenSource _cts;

        private DrawingIdentity _activeDrawing;
        private bool _isOperationRunning;
        private bool _canCancel;
        private string _progressPhaseText = "";
        private string _progressItemText = "";
        private double _progressPercent = 0.0;
        private int _progressDone = 0;
        private int _progressTotal = 0;

        private bool _statusBannerVisible;
        private OperationStatus _statusBannerStatus = OperationStatus.Succeeded;
        private string _statusBannerTitle = "";
        private string _statusBannerMessage = "";
        private string _selectedSeverityFilter = "All";
        private string _pinInputHandle = "3A12";
        private string _lastFeedbackMessage = "";

        public event PropertyChangedEventHandler PropertyChanged;

        public ISheetProductionService Service => _service;

        public DrawingIdentity ActiveDrawing
        {
            get => _activeDrawing;
            private set
            {
                if (!Equals(_activeDrawing, value))
                {
                    _activeDrawing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DrawingTitle));
                    OnPropertyChanged(nameof(DrawingUnit));
                }
            }
        }

        public string DrawingTitle => ActiveDrawing?.FilePath != null
            ? System.IO.Path.GetFileName(ActiveDrawing.FilePath)
            : "No active drawing";

        public string DrawingUnit => ActiveDrawing?.Unit ?? "m";

        public ObservableCollection<SheetItemViewModel> Sheets { get; } = new ObservableCollection<SheetItemViewModel>();
        public ObservableCollection<IssueItemViewModel> AllIssues { get; } = new ObservableCollection<IssueItemViewModel>();
        public ObservableCollection<IssueItemViewModel> FilteredIssues { get; } = new ObservableCollection<IssueItemViewModel>();
        public ObservableCollection<BuildItemViewModel> PreviewItems { get; } = new ObservableCollection<BuildItemViewModel>();

        public BuildSheetsPreview LatestPreview { get; private set; }
        public BuildSheetsResult LatestResult { get; private set; }

        public bool HasPreview => LatestPreview != null && LatestPreview.Status == OperationStatus.Succeeded && PreviewItems.Count > 0;
        public bool HasResult => LatestResult != null;

        public bool IsOperationRunning
        {
            get => _isOperationRunning;
            private set
            {
                if (_isOperationRunning != value)
                {
                    _isOperationRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsIdle));
                }
            }
        }

        public bool IsIdle => !IsOperationRunning;

        public bool CanCancel
        {
            get => _canCancel;
            private set
            {
                if (_canCancel != value)
                {
                    _canCancel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProgressPhaseText
        {
            get => _progressPhaseText;
            private set { _progressPhaseText = value; OnPropertyChanged(); }
        }

        public string ProgressItemText
        {
            get => _progressItemText;
            private set { _progressItemText = value; OnPropertyChanged(); }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            private set { _progressPercent = value; OnPropertyChanged(); }
        }

        public int ProgressDone
        {
            get => _progressDone;
            private set { _progressDone = value; OnPropertyChanged(); }
        }

        public int ProgressTotal
        {
            get => _progressTotal;
            private set { _progressTotal = value; OnPropertyChanged(); }
        }

        public bool StatusBannerVisible
        {
            get => _statusBannerVisible;
            private set { _statusBannerVisible = value; OnPropertyChanged(); }
        }

        public OperationStatus StatusBannerStatus
        {
            get => _statusBannerStatus;
            private set { _statusBannerStatus = value; OnPropertyChanged(); }
        }

        public string StatusBannerTitle
        {
            get => _statusBannerTitle;
            private set { _statusBannerTitle = value; OnPropertyChanged(); }
        }

        public string StatusBannerMessage
        {
            get => _statusBannerMessage;
            private set { _statusBannerMessage = value; OnPropertyChanged(); }
        }

        public string SelectedSeverityFilter
        {
            get => _selectedSeverityFilter;
            set
            {
                if (_selectedSeverityFilter != value)
                {
                    _selectedSeverityFilter = value;
                    OnPropertyChanged();
                    ApplyIssueFilter();
                }
            }
        }

        public string PinInputHandle
        {
            get => _pinInputHandle;
            set { _pinInputHandle = value; OnPropertyChanged(); }
        }

        public string LastFeedbackMessage
        {
            get => _lastFeedbackMessage;
            set { _lastFeedbackMessage = value; OnPropertyChanged(); }
        }

        public int TotalErrorCount => AllIssues.Count(i => i.Severity == IssueSeverity.Error);
        public int TotalWarningCount => AllIssues.Count(i => i.Severity == IssueSeverity.Warning);
        public int TotalInfoCount => AllIssues.Count(i => i.Severity == IssueSeverity.Info);

        // --- Commands ---
        public ICommand RefreshCommand { get; }
        public ICommand BuildSelectedCommand { get; }
        public ICommand BuildTwiceTestBusyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand PinLabelsCommand { get; }
        public ICommand ReleaseLabelsCommand { get; }
        public ICommand SelectAllSheetsCommand { get; }
        public ICommand DeselectAllSheetsCommand { get; }
        public ICommand SelectAllPreviewItemsCommand { get; }
        public ICommand DeselectAllPreviewItemsCommand { get; }
        public ICommand ZoomIssueCommand { get; }
        public ICommand FixIssueCommand { get; }
        public ICommand SimulatePreviewStatusCommand { get; }
        public ICommand SimulateApplyStatusCommand { get; }

        public SheetsViewModel(ISheetProductionService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            ActiveDrawing = _service.ActiveDrawing;

            _service.ActiveDrawingChanged += (s, e) =>
            {
                ActiveDrawing = _service.ActiveDrawing;
                _ = LoadOverviewAsync();
            };

            if (_service is SyntheticSheetProduction syn)
            {
                syn.ZoomRequested += (layout, target) =>
                {
                    LastFeedbackMessage = $"Zoomed to layout '{layout}'" + (target.HasValue ? $" [handle {target.Value.Handle}]" : "");
                };
            }

            RefreshCommand = new RelayCommand(async () => await LoadOverviewAsync(), () => IsIdle);
            BuildSelectedCommand = new RelayCommand(async () => await BuildSelectedAsync(), () => IsIdle && Sheets.Any(s => s.IsSelected));
            BuildTwiceTestBusyCommand = new RelayCommand(async () => await BuildTwiceTestBusyAsync(), () => IsIdle);
            CancelCommand = new RelayCommand(() => Cancel(), () => CanCancel);
            ApplyCommand = new RelayCommand(async () => await ApplyChosenAsync(), () => IsIdle && HasPreview && PreviewItems.Any(p => p.IsSelected));
            PinLabelsCommand = new RelayCommand(async () => await PinLabelsAsync(true), () => IsIdle);
            ReleaseLabelsCommand = new RelayCommand(async () => await PinLabelsAsync(false), () => IsIdle);
            SelectAllSheetsCommand = new RelayCommand(() => SetAllSheetsSelection(true));
            DeselectAllSheetsCommand = new RelayCommand(() => SetAllSheetsSelection(false));
            SelectAllPreviewItemsCommand = new RelayCommand(() => SetAllPreviewItemsSelection(true));
            DeselectAllPreviewItemsCommand = new RelayCommand(() => SetAllPreviewItemsSelection(false));
            ZoomIssueCommand = new RelayCommand(async p => await ZoomIssueAsync(p as IssueItemViewModel));
            FixIssueCommand = new RelayCommand(async p => await FixIssueAsync(p as IssueItemViewModel), _ => IsIdle);
            SimulatePreviewStatusCommand = new RelayCommand(p => SetSimulatedPreviewStatus(p));
            SimulateApplyStatusCommand = new RelayCommand(p => SetSimulatedApplyStatus(p));

            _ = LoadOverviewAsync();
        }

        public async Task LoadOverviewAsync()
        {
            if (ActiveDrawing == null) return;

            try
            {
                var overview = await _service.GetOverviewAsync(ActiveDrawing, CancellationToken.None).ConfigureAwait(true);
                Sheets.Clear();
                foreach (var s in overview.Sheets)
                {
                    Sheets.Add(new SheetItemViewModel(s, isSelected: true));
                }

                AllIssues.Clear();
                foreach (var issue in overview.Issues)
                {
                    string layout = "";
                    if (issue.Message.Contains("124-C")) layout = "124-C";
                    else if (issue.Message.Contains("124-B")) layout = "124-B";
                    else if (issue.Message.Contains("PROF-2")) layout = "124-PROF-2";
                    else if (issue.Message.Contains("PROF-1")) layout = "124-PROF-1";
                    else layout = "124-A";

                    AllIssues.Add(new IssueItemViewModel(issue, layout));
                }

                ApplyIssueFilter();
                OnPropertyChanged(nameof(TotalErrorCount));
                OnPropertyChanged(nameof(TotalWarningCount));
                OnPropertyChanged(nameof(TotalInfoCount));
            }
            catch (Exception ex)
            {
                ShowStatusBanner(OperationStatus.Rejected, "Failed to load sheet overview", ex.Message);
            }
        }

        public async Task BuildSelectedAsync(IReadOnlyList<string> specificLayouts = null, IReadOnlySet<BuildStep> specificSteps = null)
        {
            if (IsOperationRunning) return;

            var targetLayouts = specificLayouts ?? Sheets.Where(s => s.IsSelected).Select(s => s.Layout).ToList();
            if (targetLayouts.Count == 0)
            {
                ShowStatusBanner(OperationStatus.Rejected, "No sheets selected", "Please select at least one sheet to build.");
                return;
            }

            var steps = specificSteps ?? new HashSet<BuildStep>
            {
                BuildStep.PlanLabels,
                BuildStep.ProfileViews,
                BuildStep.ProfileLabels,
                BuildStep.Dims,
                BuildStep.SheetData,
                BuildStep.Arrange
            };

            var ruleSet = new LabelRuleSet("SyntheticRules", 1, new List<LabelRule>());
            var request = new BuildSheetsRequest
            {
                Drawing = ActiveDrawing,
                Layouts = targetLayouts,
                Steps = steps,
                Rules = ruleSet,
                Settings = BuildSettings.Default
            };

            await ExecutePreviewRequestAsync(request);
        }

        public async Task BuildTwiceTestBusyAsync()
        {
            if (IsOperationRunning) return;

            var targetLayouts = Sheets.Select(s => s.Layout).Take(2).ToList();
            var request = new BuildSheetsRequest
            {
                Drawing = ActiveDrawing,
                Layouts = targetLayouts,
                Steps = new HashSet<BuildStep> { BuildStep.PlanLabels },
                Rules = new LabelRuleSet("TestRules", 1, new List<LabelRule>()),
                Settings = BuildSettings.Default
            };

            // Launch first slow preview
            var progress = new Progress<BuildProgress>(ReportProgress);
            _cts = new CancellationTokenSource();
            IsOperationRunning = true;
            CanCancel = true;
            StatusBannerVisible = false;

            Task<BuildSheetsPreview> task1 = _service.PreviewAsync(request, progress, _cts.Token);

            // Give task 1 50ms to grab the lock
            await Task.Delay(50);

            // Call PreviewAsync a second time immediately!
            Task<BuildSheetsPreview> task2 = _service.PreviewAsync(request, null, CancellationToken.None);

            BuildSheetsPreview preview2 = await task2;

            // Wait for task 1 to finish
            BuildSheetsPreview preview1 = await task1;

            IsOperationRunning = false;
            CanCancel = false;

            // Verify preview2 returned Rejected with SheetCodes.Busy
            if (preview2.Status == OperationStatus.Rejected && preview2.Issues.Any(i => i.Code == SheetCodes.Busy))
            {
                ShowStatusBanner(
                    OperationStatus.Rejected,
                    "Busy Rejection Verified (sheets.busy)",
                    $"First preview finished with status '{preview1.Status}'. Second simultaneous preview was immediately rejected with code '{SheetCodes.Busy}': {preview2.Issues.First().Message}"
                );
            }
            else
            {
                ShowStatusBanner(
                    preview2.Status,
                    "Test Completed",
                    $"First preview: {preview1.Status}. Second preview: {preview2.Status}."
                );
            }

            HandlePreviewResult(preview1);
        }

        private async Task ExecutePreviewRequestAsync(BuildSheetsRequest request)
        {
            _cts = new CancellationTokenSource();
            IsOperationRunning = true;
            CanCancel = true;
            StatusBannerVisible = false;
            ProgressPercent = 0.0;
            ProgressPhaseText = "Starting preview...";
            ProgressItemText = "";

            var progress = new Progress<BuildProgress>(ReportProgress);

            try
            {
                BuildSheetsPreview preview = await _service.PreviewAsync(request, progress, _cts.Token).ConfigureAwait(true);
                HandlePreviewResult(preview);
            }
            finally
            {
                IsOperationRunning = false;
                CanCancel = false;
            }
        }

        private void HandlePreviewResult(BuildSheetsPreview preview)
        {
            LatestPreview = preview;
            OnPropertyChanged(nameof(LatestPreview));
            OnPropertyChanged(nameof(HasPreview));

            PreviewItems.Clear();
            if (preview == null) return;

            // Correct handling of every preview status: Succeeded, NoChange, Rejected, Cancelled, OutcomeUnknown
            switch (preview.Status)
            {
                case OperationStatus.Succeeded:
                    int totalChanges = 0;
                    foreach (var item in preview.Items)
                    {
                        var itemVm = new BuildItemViewModel(item, isSelected: true);
                        PreviewItems.Add(itemVm);
                        totalChanges += itemVm.ChangeCount;
                    }
                    ShowStatusBanner(
                        OperationStatus.Succeeded,
                        "Preview Ready",
                        $"Planned {totalChanges} change(s) across {preview.Items.Count} item(s). Review changes below and click Apply."
                    );
                    break;

                case OperationStatus.NoChange:
                    ShowStatusBanner(
                        OperationStatus.NoChange,
                        "No Changes Needed",
                        "The drawing linework and annotations already match current rules. No changes planned."
                    );
                    break;

                case OperationStatus.Rejected:
                    string issueSummary = preview.Issues.Count > 0
                        ? string.Join("; ", preview.Issues.Select(i => i.Message))
                        : "Preview request or drawing check failed.";
                    ShowStatusBanner(
                        OperationStatus.Rejected,
                        "Preview Rejected",
                        $"The preview was rejected: {issueSummary}"
                    );
                    break;

                case OperationStatus.Cancelled:
                    ShowStatusBanner(
                        OperationStatus.Cancelled,
                        "Preview Cancelled",
                        "Preview was cancelled by user. Drawing state remains untouched."
                    );
                    break;

                case OperationStatus.OutcomeUnknown:
                    ShowStatusBanner(
                        OperationStatus.OutcomeUnknown,
                        "Outcome Unknown",
                        "Could not verify preview state against drawing. Please save and reload the drawing."
                    );
                    break;

                default:
                    ShowStatusBanner(
                        preview.Status,
                        $"Preview Ended: {preview.Status}",
                        "Preview completed."
                    );
                    break;
            }
        }

        public async Task ApplyChosenAsync()
        {
            if (IsOperationRunning || LatestPreview == null || LatestPreview.Status != OperationStatus.Succeeded)
                return;

            var chosenKeys = PreviewItems.Where(p => p.IsSelected).Select(p => p.ItemKey).ToList();
            if (chosenKeys.Count == 0)
            {
                ShowStatusBanner(OperationStatus.Rejected, "No Items Selected", "Select at least one item from the preview list to apply.");
                return;
            }

            var applyRequest = new BuildSheetsApplyRequest
            {
                Preview = LatestPreview,
                ItemKeys = chosenKeys
            };

            _cts = new CancellationTokenSource();
            IsOperationRunning = true;
            CanCancel = true;
            ProgressPercent = 0.0;
            ProgressPhaseText = "Applying changes...";
            ProgressItemText = "";

            var progress = new Progress<BuildProgress>(ReportProgress);

            try
            {
                BuildSheetsResult result = await _service.ApplyAsync(applyRequest, progress, _cts.Token).ConfigureAwait(true);
                HandleApplyResult(result);
            }
            finally
            {
                IsOperationRunning = false;
                CanCancel = false;
            }
        }

        private void HandleApplyResult(BuildSheetsResult result)
        {
            LatestResult = result;
            OnPropertyChanged(nameof(LatestResult));
            OnPropertyChanged(nameof(HasResult));

            if (result == null) return;

            // Correct handling of every result status: Succeeded, NoChange, Rejected, Cancelled, PartiallySucceeded, OutcomeUnknown, RolledBack
            switch (result.Status)
            {
                case OperationStatus.Succeeded:
                    int totalCommitted = result.Items.Count(i => i.Status == ItemStatus.Committed);
                    int totalAdded = result.Items.Sum(i => i.Added);
                    int totalUpdated = result.Items.Sum(i => i.Updated);
                    int totalRemoved = result.Items.Sum(i => i.Removed);
                    ShowStatusBanner(
                        OperationStatus.Succeeded,
                        "Changes Applied Successfully",
                        $"All {totalCommitted} item(s) committed ({totalAdded} added, {totalUpdated} updated, {totalRemoved} removed)."
                    );
                    _ = LoadOverviewAsync();
                    break;

                case OperationStatus.PartiallySucceeded:
                    int committed = result.Items.Count(i => i.Status == ItemStatus.Committed);
                    int rolledBack = result.Items.Count(i => i.Status == ItemStatus.RolledBack);
                    int notAttempted = result.Items.Count(i => i.Status == ItemStatus.NotAttempted);
                    ShowStatusBanner(
                        OperationStatus.PartiallySucceeded,
                        "Partially Succeeded",
                        $"Partial apply: {committed} committed, {rolledBack} rolled back, {notAttempted} not attempted."
                    );
                    _ = LoadOverviewAsync();
                    break;

                case OperationStatus.NoChange:
                    ShowStatusBanner(
                        OperationStatus.NoChange,
                        "No Changes Applied",
                        "The apply operation resulted in no changes to the drawing."
                    );
                    break;

                case OperationStatus.Rejected:
                    string reason = result.Issues.Count > 0 ? result.Issues.First().Message : "Apply was rejected.";
                    ShowStatusBanner(
                        OperationStatus.Rejected,
                        "Apply Rejected",
                        $"Changes could not be applied: {reason}"
                    );
                    break;

                case OperationStatus.Cancelled:
                    int priorCommitted = result.Items != null ? result.Items.Count(i => i.Status == ItemStatus.Committed) : 0;
                    ShowStatusBanner(
                        OperationStatus.Cancelled,
                        "Apply Cancelled",
                        $"Apply was cancelled between items. {priorCommitted} item(s) were safely committed prior to stopping."
                    );
                    _ = LoadOverviewAsync();
                    break;

                case OperationStatus.RolledBack:
                    ShowStatusBanner(
                        OperationStatus.RolledBack,
                        "Changes Rolled Back",
                        "The operation failed and all changes were rolled back to preserve drawing integrity."
                    );
                    break;

                case OperationStatus.OutcomeUnknown:
                    ShowStatusBanner(
                        OperationStatus.OutcomeUnknown,
                        "Outcome Unknown (Action Required)",
                        "Drawing transaction outcome could not be verified. Please inspect or reload the drawing before retrying."
                    );
                    break;

                default:
                    ShowStatusBanner(
                        result.Status,
                        $"Result: {result.Status}",
                        "Apply operation finished."
                    );
                    break;
            }
        }

        public async Task PinLabelsAsync(bool pin)
        {
            if (IsOperationRunning) return;

            string handle = (PinInputHandle ?? "3A12").Trim();
            if (!DrawingObjectRef.TryCreate(handle, out var objRef))
            {
                ShowStatusBanner(OperationStatus.Rejected, "Invalid Handle", $"'{handle}' is not a valid drawing object handle.");
                return;
            }

            IsOperationRunning = true;
            CanCancel = true;
            try
            {
                var preview = await _service.PreviewPinAsync(ActiveDrawing, new[] { objRef }, pin, CancellationToken.None).ConfigureAwait(true);
                HandlePreviewResult(preview);

                if (preview.Status == OperationStatus.Succeeded)
                {
                    LastFeedbackMessage = $"Planned {(pin ? "Pin" : "Release")} for label handle {handle}. Click Apply to commit.";
                }
            }
            finally
            {
                IsOperationRunning = false;
                CanCancel = false;
            }
        }

        public async Task ZoomIssueAsync(IssueItemViewModel issue)
        {
            if (issue == null) return;
            try
            {
                await _service.ZoomToAsync(ActiveDrawing, issue.Layout, issue.ObjectRef, CancellationToken.None).ConfigureAwait(true);
                LastFeedbackMessage = $"Zoomed to layout '{issue.Layout}'" + (issue.ObjectRef.HasValue ? $" [handle {issue.ObjectHandle}]" : "");
            }
            catch (Exception ex)
            {
                LastFeedbackMessage = $"Zoom failed: {ex.Message}";
            }
        }

        public async Task FixIssueAsync(IssueItemViewModel issue)
        {
            if (issue == null || IsOperationRunning) return;

            string layout = string.IsNullOrEmpty(issue.Layout) ? "124-C" : issue.Layout;
            var steps = new HashSet<BuildStep>();

            if (issue.Code.StartsWith("dims.", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(BuildStep.Dims);
            }
            else if (issue.Code.StartsWith("continuation.", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(BuildStep.SheetData);
            }
            else
            {
                steps.Add(BuildStep.PlanLabels);
                steps.Add(BuildStep.Arrange);
            }

            LastFeedbackMessage = $"Fixing issue '{issue.Code}' on sheet {layout}...";
            await BuildSelectedAsync(new[] { layout }, steps);
        }

        public void Cancel()
        {
            if (CanCancel && _cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                ProgressPhaseText = "Cancelling operation...";
            }
        }

        private void ReportProgress(BuildProgress bp)
        {
            ProgressPhaseText = bp.Phase switch
            {
                BuildPhase.Reading => "Reading scene linework and styles...",
                BuildPhase.Planning => "Planning labels, views and dimensions...",
                BuildPhase.Applying => "Applying changes to drawing...",
                _ => bp.Phase.ToString()
            };

            ProgressItemText = bp.CurrentItemKey ?? "";
            ProgressDone = bp.Done;
            ProgressTotal = bp.Total;
            ProgressPercent = bp.Total > 0 ? (double)bp.Done / bp.Total * 100.0 : 0.0;
        }

        private void ShowStatusBanner(OperationStatus status, string title, string message)
        {
            StatusBannerStatus = status;
            StatusBannerTitle = title;
            StatusBannerMessage = message;
            StatusBannerVisible = true;
        }

        public void DismissStatusBanner()
        {
            StatusBannerVisible = false;
        }

        private void SetAllSheetsSelection(bool select)
        {
            foreach (var s in Sheets)
            {
                s.IsSelected = select;
            }
        }

        private void SetAllPreviewItemsSelection(bool select)
        {
            foreach (var p in PreviewItems)
            {
                p.IsSelected = select;
            }
        }

        private void ApplyIssueFilter()
        {
            FilteredIssues.Clear();
            foreach (var issue in AllIssues)
            {
                if (SelectedSeverityFilter == "All" ||
                    (SelectedSeverityFilter == "Error" && issue.Severity == IssueSeverity.Error) ||
                    (SelectedSeverityFilter == "Warning" && issue.Severity == IssueSeverity.Warning) ||
                    (SelectedSeverityFilter == "Info" && issue.Severity == IssueSeverity.Info))
                {
                    FilteredIssues.Add(issue);
                }
            }
        }

        public void SetSimulatedPreviewStatus(object statusObj)
        {
            if (_service is SyntheticSheetProduction syn)
            {
                if (statusObj is OperationStatus status)
                {
                    syn.SimulatedPreviewStatus = status;
                    LastFeedbackMessage = $"Preview simulation override set to: {status}";
                }
                else
                {
                    syn.SimulatedPreviewStatus = null;
                    LastFeedbackMessage = "Preview simulation override cleared (normal mode).";
                }
            }
        }

        public void SetSimulatedApplyStatus(object statusObj)
        {
            if (_service is SyntheticSheetProduction syn)
            {
                if (statusObj is OperationStatus status)
                {
                    syn.SimulatedApplyStatus = status;
                    LastFeedbackMessage = $"Apply simulation override set to: {status}";
                }
                else
                {
                    syn.SimulatedApplyStatus = null;
                    LastFeedbackMessage = "Apply simulation override cleared (normal mode).";
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
