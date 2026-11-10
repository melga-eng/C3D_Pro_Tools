using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.UI;
using UserControl = System.Windows.Controls.UserControl;
using Color = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfBrushes = System.Windows.Media.Brushes;

namespace C3DProTools.Features.Workspace
{
    /// <summary>
    /// Root WPF view for the Workspace palette.
    /// Hosts the stage navigation tabs (Model, Coordinate, Document, Standards) and displays
    /// the active stage content. Follows docs/redesign-roadmap.md and StyleUi design principles.
    /// Pure WPF: zero Autodesk API dependencies.
    /// </summary>
    internal sealed class WorkspaceView : UserControl
    {
        private readonly ISheetProductionService _sheetsService;
        private readonly SheetsPanelControl _sheetsPanel;

        private Border _tabModel;
        private Border _tabCoordinate;
        private Border _tabDocument;
        private Border _tabStandards;

        private ContentControl _stageContent;

        public WorkspaceView(ISheetProductionService sheetsService = null)
        {
            _sheetsService = sheetsService ?? new SyntheticSheetProduction();
            var sheetsVm = new SheetsViewModel(_sheetsService);
            _sheetsPanel = new SheetsPanelControl(sheetsVm);

            FontFamily = WpfStyle.Segoe;
            FontSize = WpfStyle.BaseFontSize;
            Background = WpfStyle.SurfaceBrush;

            BuildUi();
            SelectStage("Document");
        }

        private void BuildUi()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Stage tabs
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content

            // 1. Stage Tab Bar
            root.Children.Add(BuildStageTabBar());
            Grid.SetRow(root.Children[0], 0);

            // 2. Content Area
            _stageContent = new ContentControl
            {
                HorizontalContentAlignment = WpfHorizontalAlignment.Stretch,
                VerticalContentAlignment = WpfVerticalAlignment.Stretch
            };
            root.Children.Add(_stageContent);
            Grid.SetRow(_stageContent, 1);

            Content = root;
        }

        private Border BuildStageTabBar()
        {
            var barBorder = new Border
            {
                Background = WpfStyle.HeaderBrush,
                BorderBrush = WpfStyle.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _tabModel = CreateTabItem("Model", "Build & Clean", 0, () => SelectStage("Model"));
            _tabCoordinate = CreateTabItem("Coordinate", "Check & Clear", 1, () => SelectStage("Coordinate"));
            _tabDocument = CreateTabItem("Document", "Sheets & Annotation", 2, () => SelectStage("Document"));
            _tabStandards = CreateTabItem("Standards", "Styles & Rules", 3, () => SelectStage("Standards"));

            grid.Children.Add(_tabModel);
            grid.Children.Add(_tabCoordinate);
            grid.Children.Add(_tabDocument);
            grid.Children.Add(_tabStandards);

            barBorder.Child = grid;
            return barBorder;
        }

        private Border CreateTabItem(string title, string subtitle, int col, Action onClick)
        {
            var border = new Border
            {
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = WpfBrushes.Transparent,
                Padding = new Thickness(6, 6, 6, 8),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                HorizontalAlignment = WpfHorizontalAlignment.Center
            };

            var txtTitle = new TextBlock
            {
                Text = title,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.HeaderMutedBrush,
                HorizontalAlignment = WpfHorizontalAlignment.Center
            };
            var txtSub = new TextBlock
            {
                Text = subtitle,
                FontFamily = WpfStyle.Segoe,
                FontSize = 9,
                Foreground = WpfStyle.HeaderMutedBrush,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            };

            stack.Children.Add(txtTitle);
            stack.Children.Add(txtSub);
            border.Child = stack;

            border.MouseLeftButtonUp += (s, e) => onClick();

            Grid.SetColumn(border, col);
            return border;
        }

        private void SelectStage(string stageName)
        {
            SetTabActive(_tabModel, stageName == "Model");
            SetTabActive(_tabCoordinate, stageName == "Coordinate");
            SetTabActive(_tabDocument, stageName == "Document");
            SetTabActive(_tabStandards, stageName == "Standards");

            switch (stageName)
            {
                case "Document":
                    _stageContent.Content = _sheetsPanel;
                    break;

                case "Model":
                    _stageContent.Content = BuildPlaceholder(
                        "Model Stage",
                        "Build and clean the networks",
                        "Coming in a later phase (docs/redesign-roadmap.md).\n\n" +
                        "This stage will host tools for:\n" +
                        "• Navisworks import and geometry conversion\n" +
                        "• Network from feature lines\n" +
                        "• Network Editor and conduit generation\n" +
                        "• Parts to profile and repair utilities"
                    );
                    break;

                case "Coordinate":
                    _stageContent.Content = BuildPlaceholder(
                        "Coordinate Stage",
                        "Check networks against each other and ground",
                        "Coming in a later phase (docs/redesign-roadmap.md).\n\n" +
                        "This stage will host tools for:\n" +
                        "• Cover Optimizer and profile designer\n" +
                        "• Multi-network utility clash and clearance engine\n" +
                        "• Separation and bend budget checks"
                    );
                    break;

                case "Standards":
                    _stageContent.Content = BuildPlaceholder(
                        "Standards Stage",
                        "Project rules, styles, and submittal cleanup",
                        "Coming in a later phase (docs/redesign-roadmap.md).\n\n" +
                        "This stage will host tools for:\n" +
                        "• Style Manager and standards synchronization\n" +
                        "• Drawing Cleaner and submittal audit\n" +
                        "• Company layer, text, and label style rule enforcement"
                    );
                    break;
            }
        }

        private void SetTabActive(Border tab, bool active)
        {
            if (tab == null) return;
            tab.BorderBrush = active ? WpfStyle.AccentBrush : WpfBrushes.Transparent;
            tab.Background = active ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : WpfBrushes.Transparent;

            if (tab.Child is StackPanel stack && stack.Children.Count >= 2)
            {
                if (stack.Children[0] is TextBlock t1)
                {
                    t1.Foreground = active ? WpfStyle.HeaderTextBrush : WpfStyle.HeaderMutedBrush;
                    t1.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
                }
                if (stack.Children[1] is TextBlock t2)
                {
                    t2.Foreground = active ? WpfStyle.AccentSoftBrush : WpfStyle.HeaderMutedBrush;
                }
            }
        }

        private Border BuildPlaceholder(string title, string subtitle, string details)
        {
            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                VerticalAlignment = WpfVerticalAlignment.Center,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                MaxWidth = 420,
                Margin = new Thickness(24)
            };

            var pill = WpfStyle.CreatePill("COMING IN A LATER PHASE", WpfStyle.AccentBrush, WpfStyle.AccentSoftBrush, fontSize: 10);
            pill.HorizontalAlignment = WpfHorizontalAlignment.Left;
            pill.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(pill);

            var txtTitle = new TextBlock
            {
                Text = title,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.TitleFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = WpfStyle.HeaderBrush,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(txtTitle);

            var txtSub = new TextBlock
            {
                Text = subtitle,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfStyle.MutedBrush,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(txtSub);

            var card = WpfStyle.CreateCard(new TextBlock
            {
                Text = details,
                FontFamily = WpfStyle.Segoe,
                FontSize = WpfStyle.BaseFontSize,
                Foreground = WpfStyle.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });
            stack.Children.Add(card);

            return new Border
            {
                Background = WpfStyle.SurfaceBrush,
                Child = stack
            };
        }
    }
}
