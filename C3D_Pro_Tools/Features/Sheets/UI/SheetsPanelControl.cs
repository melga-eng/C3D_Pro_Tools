using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Workspace;
using UserControl = System.Windows.Controls.UserControl;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBinding = System.Windows.Data.Binding;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfBrushes = System.Windows.Media.Brushes;
using BindingMode = System.Windows.Data.BindingMode;
using UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger;

namespace C3DProTools.Features.Sheets.UI
{
    /// <summary>
    /// The Sheets panel hosted in the Document stage tab of the Workspace palette.
    /// Follows docs/sheet-production-contract.md section 11 and docs/redesign-roadmap.md.
    /// Pure WPF: zero Autodesk API dependencies.
    /// </summary>
    internal sealed class SheetsPanelControl : UserControl
    {
        private readonly SheetsViewModel _vm;

        private Border _statusBanner;
        private TextBlock _statusBannerTitle;
        private TextBlock _statusBannerMessage;

        private Border _progressCard;
        private TextBlock _progressPhase;
        private TextBlock _progressItem;
        private ProgressBar _progressBar;

        private StackPanel _sheetsStack;
        private Border _previewCard;
        private StackPanel _previewItemsStack;
        private Button _btnApply;
        private Border _applyOutcomesCard;
        private StackPanel _applyOutcomesStack;
        private StackPanel _issuesStack;
        private TextBlock _feedbackText;

        public SheetsViewModel ViewModel => _vm;

        public SheetsPanelControl(SheetsViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            DataContext = _vm;

            FontFamily = WpfStyle.Segoe;
            FontSize = WpfStyle.BaseFontSize;
            Foreground = WpfStyle.TextBrush;
            Background = WpfStyle.SurfaceBrush;

            BuildUi();

            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Sheets.CollectionChanged += (s, e) => RebuildSheetsList();
            _vm.PreviewItems.CollectionChanged += (s, e) => RebuildPreviewList();
            _vm.ApplyOutcomes.CollectionChanged += (s, e) => RebuildApplyOutcomesList();
            _vm.FilteredIssues.CollectionChanged += (s, e) => RebuildIssuesList();

            RebuildSheetsList();
            RebuildPreviewList();
            RebuildApplyOutcomesList();
            RebuildIssuesList();
            UpdateStatusBanner();
            UpdateProgressCard();
        }

        private void BuildUi()
        {
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Drawing Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status Banner
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress Card
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Main scroll content
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Bottom feedback bar

            // 1. Drawing Header
            rootGrid.Children.Add(BuildHeader());
            Grid.SetRow(rootGrid.Children[0], 0);

            // 2. Status Banner
            _statusBanner = BuildStatusBanner();
            rootGrid.Children.Add(_statusBanner);
            Grid.SetRow(_statusBanner, 1);

            // 3. Progress Card
            _progressCard = BuildProgressCard();
            rootGrid.Children.Add(_progressCard);
            Grid.SetRow(_progressCard, 2);

            // 4. Main Scroll Content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 8, 12, 12)
            };

            var contentStack = new StackPanel { Orientation = WpfOrientation.Vertical };

            // Sheets List Card
            contentStack.Children.Add(BuildSheetsCard());

            // Build Actions Card
            contentStack.Children.Add(BuildActionsCard());

            // Preview & Changes Card
            _previewCard = BuildPreviewCard();
            contentStack.Children.Add(_previewCard);

            // Apply Outcomes Card (shows status, counts +N/~N/-N and issues per item)
            _applyOutcomesCard = BuildApplyOutcomesCard();
            contentStack.Children.Add(_applyOutcomesCard);

            // Issues List Card
            contentStack.Children.Add(BuildIssuesCard());

            scrollViewer.Content = contentStack;
            rootGrid.Children.Add(scrollViewer);
            Grid.SetRow(scrollViewer, 3);

            // 5. Feedback status bar
            _feedbackText = new TextBlock
            {
                Text = "Ready",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.MutedBrush,
                Margin = new Thickness(12, 4, 12, 6)
            };
            var feedbackBorder = new Border
            {
                Background = WpfStyle.PanelBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = _feedbackText
            };
            rootGrid.Children.Add(feedbackBorder);
            Grid.SetRow(feedbackBorder, 4);

            Content = rootGrid;
        }

        private Border BuildHeader()
        {
            var grid = new Grid { Margin = new Thickness(12, 10, 12, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            var title = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush
            };
            title.SetBinding(TextBlock.TextProperty, new WpfBinding("DrawingTitle"));

            var subTitle = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.MutedBrush,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var sessionBinding = new WpfBinding("ActiveDrawing.SessionKey") { StringFormat = "Drawing unit: m · Session: {0}" };
            subTitle.SetBinding(TextBlock.TextProperty, sessionBinding);

            titleStack.Children.Add(title);
            titleStack.Children.Add(subTitle);
            grid.Children.Add(titleStack);
            Grid.SetColumn(titleStack, 0);

            var refreshBtn = WpfStyle.CreateSecondaryButton("Refresh");
            refreshBtn.Command = _vm.RefreshCommand;
            refreshBtn.VerticalAlignment = WpfVerticalAlignment.Center;
            grid.Children.Add(refreshBtn);
            Grid.SetColumn(refreshBtn, 1);

            return new Border
            {
                Background = WpfStyle.PanelBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid
            };
        }

        private Border BuildStatusBanner()
        {
            var border = new Border
            {
                Margin = new Thickness(12, 8, 12, 4),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            _statusBannerTitle = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                FontWeight = FontWeights.Bold
            };
            _statusBannerMessage = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            textStack.Children.Add(_statusBannerTitle);
            textStack.Children.Add(_statusBannerMessage);
            grid.Children.Add(textStack);
            Grid.SetColumn(textStack, 0);

            var closeBtn = new Button
            {
                Content = "✕",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = WpfStyle.MutedBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = WpfVerticalAlignment.Top,
                Padding = new Thickness(4, 0, 4, 0)
            };
            closeBtn.Click += (s, e) => _vm.DismissStatusBanner();
            grid.Children.Add(closeBtn);
            Grid.SetColumn(closeBtn, 1);

            border.Child = grid;
            return border;
        }

        private Border BuildProgressCard()
        {
            var border = new Border
            {
                Background = WpfStyle.PanelBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 10),
                Margin = new Thickness(12, 6, 12, 4),
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _progressPhase = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.AccentBrush
            };
            _progressPhase.SetBinding(TextBlock.TextProperty, new WpfBinding("ProgressPhaseText"));
            topGrid.Children.Add(_progressPhase);
            Grid.SetColumn(_progressPhase, 0);

            var cancelBtn = WpfStyle.CreateDangerButton("Cancel");
            cancelBtn.Padding = new Thickness(8, 2, 8, 2);
            cancelBtn.FontSize = WpfStyle.SmallFontSize;
            cancelBtn.Command = _vm.CancelCommand;
            topGrid.Children.Add(cancelBtn);
            Grid.SetColumn(cancelBtn, 1);

            stack.Children.Add(topGrid);

            _progressItem = new TextBlock
            {
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.MutedBrush,
                Margin = new Thickness(0, 3, 0, 6)
            };
            _progressItem.SetBinding(TextBlock.TextProperty, new WpfBinding("ProgressItemText"));
            stack.Children.Add(_progressItem);

            _progressBar = new ProgressBar
            {
                Height = 8,
                Foreground = WpfStyle.AccentBrush,
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(1)
            };
            _progressBar.SetBinding(ProgressBar.ValueProperty, new WpfBinding("ProgressPercent"));
            stack.Children.Add(_progressBar);

            border.Child = stack;
            return border;
        }

        private Border BuildSheetsCard()
        {
            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            // Header row with Select All / None links
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Sheets in Drawing",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush
            };
            headerGrid.Children.Add(title);
            Grid.SetColumn(title, 0);

            var linksStack = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var linkAll = CreateHyperlinkButton("Select All", () => _vm.SelectAllSheetsCommand.Execute(null));
            var linkNone = CreateHyperlinkButton("None", () => _vm.DeselectAllSheetsCommand.Execute(null));
            linksStack.Children.Add(linkAll);
            linksStack.Children.Add(new TextBlock { Text = " · ", Foreground = WpfStyle.MutedBrush, VerticalAlignment = WpfVerticalAlignment.Center });
            linksStack.Children.Add(linkNone);
            headerGrid.Children.Add(linksStack);
            Grid.SetColumn(linksStack, 1);

            stack.Children.Add(headerGrid);

            // Container for sheet rows
            _sheetsStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            stack.Children.Add(_sheetsStack);

            return WpfStyle.CreateCard(stack, new Thickness(0, 0, 0, 10));
        }

        private void RebuildSheetsList()
        {
            if (_sheetsStack == null) return;
            _sheetsStack.Children.Clear();

            if (_vm.Sheets.Count == 0)
            {
                _sheetsStack.Children.Add(new TextBlock
                {
                    Text = "No sheets loaded.",
                    Foreground = WpfStyle.MutedBrush,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 6, 0, 6)
                });
                return;
            }

            foreach (var sheet in _vm.Sheets)
            {
                _sheetsStack.Children.Add(CreateSheetRow(sheet));
            }
        }

        private UIElement CreateSheetRow(SheetItemViewModel sheet)
        {
            var border = new Border
            {
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Checkbox
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Pixel) }); // Layout & Kind
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Step Pills
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Issues Badge

            // Checkbox
            var chk = new CheckBox
            {
                VerticalAlignment = WpfVerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                DataContext = sheet
            };
            chk.SetBinding(CheckBox.IsCheckedProperty, new WpfBinding("IsSelected") { Mode = BindingMode.TwoWay });
            grid.Children.Add(chk);
            Grid.SetColumn(chk, 0);

            // Layout & Kind
            var nameStack = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = WpfVerticalAlignment.Center };
            var txtLayout = new TextBlock
            {
                Text = sheet.Layout,
                FontFamily = WpfStyle.Segoe,
                FontWeight = FontWeights.Bold,
                FontSize = WpfStyle.BaseFontSize,
                Foreground = WpfStyle.HeaderBrush
            };
            nameStack.Children.Add(txtLayout);

            var kindPill = WpfStyle.CreatePill(
                sheet.Kind == SheetKind.Plan ? "Plan" : "Profile",
                sheet.Kind == SheetKind.Plan ? WpfStyle.SuccessBrush : WpfStyle.AccentBrush,
                sheet.Kind == SheetKind.Plan ? WpfStyle.SuccessSoftBrush : WpfStyle.AccentSoftBrush,
                fontSize: 10
            );
            kindPill.HorizontalAlignment = WpfHorizontalAlignment.Left;
            kindPill.Margin = new Thickness(0, 2, 0, 0);
            nameStack.Children.Add(kindPill);

            grid.Children.Add(nameStack);
            Grid.SetColumn(nameStack, 1);

            // 6 Build Steps Pills
            var stepsWrap = new WrapPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVerticalAlignment.Center };

            stepsWrap.Children.Add(CreateStepPill("Labels", sheet.PlanLabelsState));
            stepsWrap.Children.Add(CreateStepPill("P.Views", sheet.ProfileViewsState));
            stepsWrap.Children.Add(CreateStepPill("P.Labels", sheet.ProfileLabelsState));
            stepsWrap.Children.Add(CreateStepPill("Dims", sheet.DimsState));
            stepsWrap.Children.Add(CreateStepPill("Data", sheet.SheetDataState));
            stepsWrap.Children.Add(CreateStepPill("Arrange", sheet.ArrangeState));

            grid.Children.Add(stepsWrap);
            Grid.SetColumn(stepsWrap, 2);

            // Issue Count Badge
            if (sheet.IssueCount > 0)
            {
                var issuePill = WpfStyle.CreatePill(
                    $"{sheet.IssueCount} issue{(sheet.IssueCount > 1 ? "s" : "")}",
                    WpfStyle.DangerBrush,
                    WpfStyle.DangerSoftBrush,
                    fontSize: 10
                );
                issuePill.VerticalAlignment = WpfVerticalAlignment.Center;
                issuePill.Margin = new Thickness(6, 0, 0, 0);
                grid.Children.Add(issuePill);
                Grid.SetColumn(issuePill, 3);
            }

            border.Child = grid;
            return border;
        }

        private Border CreateStepPill(string label, StepState state)
        {
            (var fore, var back) = WpfStyle.GetStepStateBrushes(state);
            string stateText = state switch
            {
                StepState.Built => "Built",
                StepState.OutOfDate => "Out of date",
                StepState.HasIssues => "Issues",
                StepState.NotBuilt => "Not built",
                StepState.NotApplicable => "—",
                _ => state.ToString()
            };

            return WpfStyle.CreatePill($"{label}: {stateText}", fore, back, fontSize: 10);
        }

        private Border BuildActionsCard()
        {
            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var title = new TextBlock
            {
                Text = "Actions",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(title);

            // Primary action row
            var btnRow = new WrapPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var btnBuild = WpfStyle.CreatePrimaryButton("Build Selected");
            btnBuild.Command = _vm.BuildSelectedCommand;
            btnBuild.Margin = new Thickness(0, 0, 8, 4);
            btnRow.Children.Add(btnBuild);

            var btnBusyTest = WpfStyle.CreateSecondaryButton("Test Busy (Preview ×2)");
            btnBusyTest.Command = _vm.BuildTwiceTestBusyCommand;
            btnBusyTest.ToolTip = "Launches two simultaneous previews to verify busy rejection (sheets.busy)";
            btnBusyTest.Margin = new Thickness(0, 0, 8, 4);
            btnRow.Children.Add(btnBusyTest);

            stack.Children.Add(btnRow);

            // Pin / Release controls
            var pinRow = new WrapPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var lblPin = new TextBlock
            {
                Text = "Label Handle:",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                VerticalAlignment = WpfVerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            pinRow.Children.Add(lblPin);

            var txtPin = new TextBox
            {
                Width = 80,
                Height = 26,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                VerticalContentAlignment = WpfVerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            txtPin.SetBinding(TextBox.TextProperty, new WpfBinding("PinInputHandle") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            pinRow.Children.Add(txtPin);

            var btnPin = WpfStyle.CreateSecondaryButton("Pin Label");
            btnPin.Command = _vm.PinLabelsCommand;
            btnPin.Margin = new Thickness(0, 0, 6, 0);
            pinRow.Children.Add(btnPin);

            var btnRelease = WpfStyle.CreateSecondaryButton("Release Label");
            btnRelease.Command = _vm.ReleaseLabelsCommand;
            pinRow.Children.Add(btnRelease);

            stack.Children.Add(pinRow);

            // Status Simulator Controls (to test all preview and result statuses)
            var simExpander = new Expander
            {
                Header = new TextBlock { Text = "Status Handling Simulator (Test All Statuses)", FontSize = WpfStyle.SmallFontSize, Foreground = WpfStyle.MutedBrush },
                Margin = new Thickness(0, 4, 0, 0)
            };
            var simStack = new StackPanel { Orientation = WpfOrientation.Vertical, Margin = new Thickness(6, 6, 6, 4) };

            var simText = new TextBlock
            {
                Text = "Force service return status to test UI handling of every preview and result status:",
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.MutedBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            simStack.Children.Add(simText);

            var simBtns = new WrapPanel { Orientation = WpfOrientation.Horizontal };
            simBtns.Children.Add(CreateSimButton("Normal", null, isApply: false));
            simBtns.Children.Add(CreateSimButton("Preview NoChange", OperationStatus.NoChange, isApply: false));
            simBtns.Children.Add(CreateSimButton("Preview Rejected", OperationStatus.Rejected, isApply: false));
            simBtns.Children.Add(CreateSimButton("Preview Cancelled", OperationStatus.Cancelled, isApply: false));
            simBtns.Children.Add(CreateSimButton("Preview OutcomeUnknown", OperationStatus.OutcomeUnknown, isApply: false));
            simBtns.Children.Add(CreateSimButton("Apply PartiallySucceeded", OperationStatus.PartiallySucceeded, isApply: true));
            simBtns.Children.Add(CreateSimButton("Apply OutcomeUnknown", OperationStatus.OutcomeUnknown, isApply: true));
            simBtns.Children.Add(CreateSimButton("Apply RolledBack", OperationStatus.RolledBack, isApply: true));
            simBtns.Children.Add(CreateSimButton("Apply Rejected", OperationStatus.Rejected, isApply: true));
            simStack.Children.Add(simBtns);

            simExpander.Content = simStack;
            stack.Children.Add(simExpander);

            return WpfStyle.CreateCard(stack, new Thickness(0, 0, 0, 10));
        }

        private Button CreateSimButton(string text, OperationStatus? status, bool isApply)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 10,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (s, e) =>
            {
                if (isApply)
                    _vm.SimulateApplyStatusCommand.Execute(status);
                else
                    _vm.SimulatePreviewStatusCommand.Execute(status);
            };
            return btn;
        }

        private Border BuildPreviewCard()
        {
            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Planned Changes (Preview)",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush
            };
            headerGrid.Children.Add(title);
            Grid.SetColumn(title, 0);

            var linksStack = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var linkAll = CreateHyperlinkButton("Select All", () => _vm.SelectAllPreviewItemsCommand.Execute(null));
            var linkNone = CreateHyperlinkButton("None", () => _vm.DeselectAllPreviewItemsCommand.Execute(null));
            linksStack.Children.Add(linkAll);
            linksStack.Children.Add(new TextBlock { Text = " · ", Foreground = WpfStyle.MutedBrush, VerticalAlignment = WpfVerticalAlignment.Center });
            linksStack.Children.Add(linkNone);
            headerGrid.Children.Add(linksStack);
            Grid.SetColumn(linksStack, 1);

            stack.Children.Add(headerGrid);

            _previewItemsStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            stack.Children.Add(_previewItemsStack);

            _btnApply = WpfStyle.CreatePrimaryButton("Apply Chosen Items");
            _btnApply.Command = _vm.ApplyCommand;
            _btnApply.HorizontalAlignment = WpfHorizontalAlignment.Left;
            _btnApply.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(_btnApply);

            var border = WpfStyle.CreateCard(stack, new Thickness(0, 0, 0, 10));
            border.Visibility = Visibility.Collapsed;
            return border;
        }

        private void RebuildPreviewList()
        {
            if (_previewItemsStack == null || _previewCard == null) return;
            _previewItemsStack.Children.Clear();

            if (_vm.PreviewItems.Count == 0)
            {
                _previewCard.Visibility = Visibility.Collapsed;
                return;
            }

            _previewCard.Visibility = Visibility.Visible;

            foreach (var item in _vm.PreviewItems)
            {
                _previewItemsStack.Children.Add(CreatePreviewItemRow(item));
            }
        }

        private UIElement CreatePreviewItemRow(BuildItemViewModel item)
        {
            var itemBorder = new Border
            {
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            // Item header row
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chk = new CheckBox
            {
                VerticalAlignment = WpfVerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                DataContext = item
            };
            chk.SetBinding(CheckBox.IsCheckedProperty, new WpfBinding("IsSelected") { Mode = BindingMode.TwoWay });
            topGrid.Children.Add(chk);
            Grid.SetColumn(chk, 0);

            var titleStack = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVerticalAlignment.Center };
            var txtKey = new TextBlock
            {
                Text = item.ItemKey,
                FontFamily = WpfStyle.Segoe,
                FontWeight = FontWeights.Bold,
                FontSize = WpfStyle.BaseFontSize,
                Foreground = WpfStyle.HeaderBrush,
                Margin = new Thickness(0, 0, 8, 0)
            };
            titleStack.Children.Add(txtKey);

            var kindPill = WpfStyle.CreatePill(
                item.Kind == BuildItemKind.PlanSheet ? "Plan Sheet" : "Profile Sheet Set",
                WpfStyle.TextBrush,
                WpfStyle.BorderBrush,
                fontSize: 10
            );
            titleStack.Children.Add(kindPill);
            topGrid.Children.Add(titleStack);
            Grid.SetColumn(titleStack, 1);

            var txtChangesCount = new TextBlock
            {
                Text = $"{item.ChangeCount} change(s)",
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.MutedBrush,
                VerticalAlignment = WpfVerticalAlignment.Center
            };
            topGrid.Children.Add(txtChangesCount);
            Grid.SetColumn(txtChangesCount, 2);

            stack.Children.Add(topGrid);

            // Changes list under item
            var changesStack = new StackPanel { Orientation = WpfOrientation.Vertical, Margin = new Thickness(24, 6, 0, 0) };
            foreach (var chg in item.Changes)
            {
                var chgRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                chgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                chgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                chgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Kind badge
                (var kFore, var kBack) = chg.Kind switch
                {
                    ChangeKind.Add => (WpfStyle.SuccessBrush, WpfStyle.SuccessSoftBrush),
                    ChangeKind.Update => (WpfStyle.WarningBrush, WpfStyle.WarningSoftBrush),
                    ChangeKind.Remove => (WpfStyle.DangerBrush, WpfStyle.DangerSoftBrush),
                    _ => (WpfStyle.MutedBrush, WpfStyle.BorderBrush)
                };
                var pillKind = WpfStyle.CreatePill(chg.Kind.ToString().ToUpperInvariant(), kFore, kBack, fontSize: 9);
                pillKind.Margin = new Thickness(0, 0, 6, 0);
                chgRow.Children.Add(pillKind);
                Grid.SetColumn(pillKind, 0);

                // Target badge
                var pillTarget = WpfStyle.CreatePill(chg.Target.ToString(), WpfStyle.MutedBrush, WpfStyle.SurfaceBrush, fontSize: 9);
                pillTarget.Margin = new Thickness(0, 0, 6, 0);
                chgRow.Children.Add(pillTarget);
                Grid.SetColumn(pillTarget, 1);

                // Description
                var txtDesc = new TextBlock
                {
                    Text = chg.Description,
                    FontFamily = WpfStyle.Segoe,
                    FontSize = WpfStyle.SmallFontSize,
                    Foreground = WpfStyle.TextBrush,
                    VerticalAlignment = WpfVerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                chgRow.Children.Add(txtDesc);
                Grid.SetColumn(txtDesc, 2);

                changesStack.Children.Add(chgRow);
            }

            stack.Children.Add(changesStack);
            itemBorder.Child = stack;
            return itemBorder;
        }

        private Border BuildApplyOutcomesCard()
        {
            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var title = new TextBlock
            {
                Text = "Apply Outcomes (Committed Items)",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(title);

            _applyOutcomesStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            stack.Children.Add(_applyOutcomesStack);

            var border = WpfStyle.CreateCard(stack, new Thickness(0, 0, 0, 10));
            border.Visibility = Visibility.Collapsed;
            return border;
        }

        private void RebuildApplyOutcomesList()
        {
            if (_applyOutcomesStack == null || _applyOutcomesCard == null) return;
            _applyOutcomesStack.Children.Clear();

            if (_vm.ApplyOutcomes.Count == 0)
            {
                _applyOutcomesCard.Visibility = Visibility.Collapsed;
                return;
            }

            _applyOutcomesCard.Visibility = Visibility.Visible;

            foreach (var outcome in _vm.ApplyOutcomes)
            {
                _applyOutcomesStack.Children.Add(CreateOutcomeRow(outcome));
            }
        }

        private UIElement CreateOutcomeRow(BuildItemOutcomeViewModel outcome)
        {
            var border = new Border
            {
                Background = outcome.IsCommitted ? WpfStyle.SurfaceBrush : WpfStyle.DangerSoftBrush,
                BorderBrush = outcome.IsCommitted ? WpfStyle.BorderBrush : WpfStyle.DangerBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Item Key
            var txtKey = new TextBlock
            {
                Text = outcome.ItemKey,
                FontFamily = WpfStyle.Segoe,
                FontWeight = FontWeights.Bold,
                FontSize = WpfStyle.BaseFontSize,
                Foreground = WpfStyle.HeaderBrush,
                VerticalAlignment = WpfVerticalAlignment.Center
            };
            topGrid.Children.Add(txtKey);
            Grid.SetColumn(txtKey, 0);

            // Status Pill
            (var sFore, var sBack) = outcome.Status switch
            {
                ItemStatus.Committed => (WpfStyle.SuccessBrush, WpfStyle.SuccessSoftBrush),
                ItemStatus.RolledBack => (WpfStyle.DangerBrush, WpfStyle.DangerSoftBrush),
                ItemStatus.Rejected => (WpfStyle.DangerBrush, WpfStyle.DangerSoftBrush),
                ItemStatus.NotAttempted => (WpfStyle.MutedBrush, WpfStyle.BorderBrush),
                ItemStatus.OutcomeUnknown => (WpfStyle.PurpleBrush, WpfStyle.PurpleSoftBrush),
                _ => (WpfStyle.MutedBrush, WpfStyle.BorderBrush)
            };
            var statusPill = WpfStyle.CreatePill(outcome.Status.ToString().ToUpperInvariant(), sFore, sBack, fontSize: 10);
            statusPill.VerticalAlignment = WpfVerticalAlignment.Center;
            statusPill.Margin = new Thickness(8, 0, 8, 0);
            topGrid.Children.Add(statusPill);
            Grid.SetColumn(statusPill, 1);

            // Counts Summary (+N / ~N / -N)
            var txtCounts = new TextBlock
            {
                Text = outcome.CountsSummary,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = outcome.IsCommitted ? WpfStyle.TextBrush : WpfStyle.MutedBrush,
                VerticalAlignment = WpfVerticalAlignment.Center
            };
            topGrid.Children.Add(txtCounts);
            Grid.SetColumn(txtCounts, 2);

            stack.Children.Add(topGrid);

            // Item Issues if any
            if (outcome.HasIssues)
            {
                var txtIssues = new TextBlock
                {
                    Text = outcome.IssuesSummary,
                    FontFamily = WpfStyle.Segoe,
                    FontSize = WpfStyle.SmallFontSize,
                    Foreground = WpfStyle.DangerBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                stack.Children.Add(txtIssues);
            }

            border.Child = stack;
            return border;
        }

        private Border BuildIssuesCard()
        {
            var stack = new StackPanel { Orientation = WpfOrientation.Vertical };

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Drawing Issues",
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.HeadingFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderBrush
            };
            headerGrid.Children.Add(title);
            Grid.SetColumn(title, 0);

            // Filter buttons
            var filtersStack = new StackPanel { Orientation = WpfOrientation.Horizontal };
            filtersStack.Children.Add(CreateFilterButton("All"));
            filtersStack.Children.Add(CreateFilterButton("Error"));
            filtersStack.Children.Add(CreateFilterButton("Warning"));
            filtersStack.Children.Add(CreateFilterButton("Info"));
            headerGrid.Children.Add(filtersStack);
            Grid.SetColumn(filtersStack, 1);

            stack.Children.Add(headerGrid);

            _issuesStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            stack.Children.Add(_issuesStack);

            return WpfStyle.CreateCard(stack);
        }

        private Button CreateFilterButton(string filter)
        {
            var btn = new Button
            {
                Content = filter,
                FontSize = WpfStyle.SmallFontSize,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(2, 0, 0, 0),
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (s, e) => _vm.SelectedSeverityFilter = filter;
            return btn;
        }

        private void RebuildIssuesList()
        {
            if (_issuesStack == null) return;
            _issuesStack.Children.Clear();

            if (_vm.FilteredIssues.Count == 0)
            {
                _issuesStack.Children.Add(new TextBlock
                {
                    Text = "No issues matching filter.",
                    Foreground = WpfStyle.MutedBrush,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 6, 0, 6)
                });
                return;
            }

            foreach (var issue in _vm.FilteredIssues)
            {
                _issuesStack.Children.Add(CreateIssueRow(issue));
            }
        }

        private UIElement CreateIssueRow(IssueItemViewModel issue)
        {
            var border = new Border
            {
                Background = WpfStyle.SurfaceBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Severity pill
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Message & Code
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Zoom & Fix buttons

            // Severity pill
            (var fore, var back) = WpfStyle.GetSeverityBrushes(issue.Severity);
            var pill = WpfStyle.CreatePill(issue.Severity.ToString().ToUpperInvariant(), fore, back, fontSize: 9);
            pill.VerticalAlignment = WpfVerticalAlignment.Top;
            pill.Margin = new Thickness(0, 2, 8, 0);
            grid.Children.Add(pill);
            Grid.SetColumn(pill, 0);

            // Message & details
            var textStack = new StackPanel { Orientation = WpfOrientation.Vertical, Margin = new Thickness(0, 0, 8, 0) };
            string locationText = issue.HasLocation ? $" · Sheet {issue.Layout}" : " · (location unknown)";
            string handleText = !string.IsNullOrEmpty(issue.ObjectHandle) ? $" · Handle {issue.ObjectHandle}" : "";

            var txtCode = new TextBlock
            {
                Text = issue.Code + locationText + handleText,
                FontFamily = WpfStyle.Segoe,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = issue.HasLocation ? WpfStyle.MutedBrush : WpfStyle.DangerBrush
            };
            textStack.Children.Add(txtCode);

            var txtMsg = new TextBlock
            {
                Text = issue.Message,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                Foreground = WpfStyle.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            textStack.Children.Add(txtMsg);
            grid.Children.Add(textStack);
            Grid.SetColumn(textStack, 1);

            // Action buttons: Zoom and Fix (disabled when location is unknown - Codex finding 5)
            var btnStack = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVerticalAlignment.Center };

            var btnZoom = WpfStyle.CreateSecondaryButton("Zoom");
            btnZoom.Padding = new Thickness(8, 3, 8, 3);
            btnZoom.FontSize = WpfStyle.SmallFontSize;
            btnZoom.Command = _vm.ZoomIssueCommand;
            btnZoom.CommandParameter = issue;
            btnZoom.IsEnabled = issue.HasLocation;
            if (!issue.HasLocation) btnZoom.ToolTip = "Location unknown: cannot zoom to sheet";
            btnZoom.Margin = new Thickness(0, 0, 4, 0);
            btnStack.Children.Add(btnZoom);

            var btnFix = WpfStyle.CreateSecondaryButton("Fix");
            btnFix.Padding = new Thickness(8, 3, 8, 3);
            btnFix.FontSize = WpfStyle.SmallFontSize;
            btnFix.Command = _vm.FixIssueCommand;
            btnFix.CommandParameter = issue;
            btnFix.IsEnabled = issue.HasLocation;
            if (!issue.HasLocation) btnFix.ToolTip = "Location unknown: cannot determine which sheet step to build";
            btnStack.Children.Add(btnFix);

            grid.Children.Add(btnStack);
            Grid.SetColumn(btnStack, 2);

            border.Child = grid;
            return border;
        }

        private Button CreateHyperlinkButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.SmallFontSize,
                Foreground = WpfStyle.AccentBrush,
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(0)
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.StatusBannerVisible):
                case nameof(_vm.StatusBannerStatus):
                case nameof(_vm.StatusBannerTitle):
                case nameof(_vm.StatusBannerMessage):
                    UpdateStatusBanner();
                    break;

                case nameof(_vm.IsOperationRunning):
                    UpdateProgressCard();
                    break;

                case nameof(_vm.LastFeedbackMessage):
                    if (_feedbackText != null) _feedbackText.Text = _vm.LastFeedbackMessage;
                    break;

                case nameof(_vm.HasApplyOutcomes):
                    RebuildApplyOutcomesList();
                    break;
            }
        }

        private void UpdateStatusBanner()
        {
            if (_statusBanner == null) return;
            if (!_vm.StatusBannerVisible)
            {
                _statusBanner.Visibility = Visibility.Collapsed;
                return;
            }

            _statusBanner.Visibility = Visibility.Visible;
            _statusBannerTitle.Text = _vm.StatusBannerTitle;
            _statusBannerMessage.Text = _vm.StatusBannerMessage;

            (var fore, var back, var border) = WpfStyle.GetOperationStatusBrushes(_vm.StatusBannerStatus);
            _statusBanner.Background = back;
            _statusBanner.BorderBrush = border;
            _statusBannerTitle.Foreground = fore;
            _statusBannerMessage.Foreground = fore;
        }

        private void UpdateProgressCard()
        {
            if (_progressCard == null) return;
            _progressCard.Visibility = _vm.IsOperationRunning ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
