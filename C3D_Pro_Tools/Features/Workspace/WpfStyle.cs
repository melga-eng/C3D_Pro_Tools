using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace C3DProTools.Features.Workspace
{
    /// <summary>
    /// WPF colors, brushes, fonts, and control factory methods matching StyleUi.
    /// Used by the Workspace palette and Sheets panel.
    /// </summary>
    internal static class WpfStyle
    {
        // --- Core Colors matching StyleUi ---
        public static readonly Color HeaderColor = Color.FromRgb(15, 23, 42);          // #0F172A
        public static readonly Color HeaderTextColor = Color.FromRgb(241, 245, 249);    // #F1F5F9
        public static readonly Color HeaderMutedColor = Color.FromRgb(148, 163, 184);   // #94A3B8
        public static readonly Color SurfaceColor = Color.FromRgb(248, 250, 252);      // #F8FAFC
        public static readonly Color PanelColor = Colors.White;                        // #FFFFFF
        public static readonly Color BorderColor = Color.FromRgb(226, 232, 240);       // #E2E8F0
        public static readonly Color TextColor = Color.FromRgb(15, 23, 42);            // #0F172A
        public static readonly Color MutedColor = Color.FromRgb(100, 116, 139);        // #64748B
        public static readonly Color AccentColor = Color.FromRgb(37, 99, 235);         // #2563EB
        public static readonly Color AccentHoverColor = Color.FromRgb(29, 78, 216);    // #1D4ED8
        public static readonly Color AccentSoftColor = Color.FromRgb(219, 234, 254);   // #DBEAFE
        public static readonly Color DangerColor = Color.FromRgb(220, 38, 38);         // #DC2626
        public static readonly Color DangerSoftColor = Color.FromRgb(254, 226, 226);   // #FEE2E2
        public static readonly Color WarningColor = Color.FromRgb(180, 83, 9);         // #B45309
        public static readonly Color WarningSoftColor = Color.FromRgb(254, 243, 199);  // #FEF3C7
        public static readonly Color SuccessColor = Color.FromRgb(21, 128, 61);        // #15803D
        public static readonly Color SuccessSoftColor = Color.FromRgb(220, 252, 231);  // #DCFCE7
        public static readonly Color PurpleColor = Color.FromRgb(109, 40, 217);        // #6D28D9
        public static readonly Color PurpleSoftColor = Color.FromRgb(237, 233, 254);   // #EDE9FE

        // --- Brushes (Frozen for WPF performance and cross-thread usage) ---
        public static readonly SolidColorBrush HeaderBrush = CreateFrozenBrush(HeaderColor);
        public static readonly SolidColorBrush HeaderTextBrush = CreateFrozenBrush(HeaderTextColor);
        public static readonly SolidColorBrush HeaderMutedBrush = CreateFrozenBrush(HeaderMutedColor);
        public static readonly SolidColorBrush SurfaceBrush = CreateFrozenBrush(SurfaceColor);
        public static readonly SolidColorBrush PanelBrush = CreateFrozenBrush(PanelColor);
        public static readonly SolidColorBrush BorderBrush = CreateFrozenBrush(BorderColor);
        public static readonly SolidColorBrush TextBrush = CreateFrozenBrush(TextColor);
        public static readonly SolidColorBrush MutedBrush = CreateFrozenBrush(MutedColor);
        public static readonly SolidColorBrush AccentBrush = CreateFrozenBrush(AccentColor);
        public static readonly SolidColorBrush AccentHoverBrush = CreateFrozenBrush(AccentHoverColor);
        public static readonly SolidColorBrush AccentSoftBrush = CreateFrozenBrush(AccentSoftColor);
        public static readonly SolidColorBrush DangerBrush = CreateFrozenBrush(DangerColor);
        public static readonly SolidColorBrush DangerSoftBrush = CreateFrozenBrush(DangerSoftColor);
        public static readonly SolidColorBrush WarningBrush = CreateFrozenBrush(WarningColor);
        public static readonly SolidColorBrush WarningSoftBrush = CreateFrozenBrush(WarningSoftColor);
        public static readonly SolidColorBrush SuccessBrush = CreateFrozenBrush(SuccessColor);
        public static readonly SolidColorBrush SuccessSoftBrush = CreateFrozenBrush(SuccessSoftColor);
        public static readonly SolidColorBrush PurpleBrush = CreateFrozenBrush(PurpleColor);
        public static readonly SolidColorBrush PurpleSoftBrush = CreateFrozenBrush(PurpleSoftColor);

        // --- Typography ---
        public static readonly FontFamily Segoe = new FontFamily("Segoe UI");
        public const double BaseFontSize = 12.0;    // 9pt
        public const double SmallFontSize = 11.0;   // 8pt
        public const double HeadingFontSize = 14.0; // 10.5pt
        public const double TitleFontSize = 18.0;   // 14pt

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // --- Pill / Status Helpers ---

        public static (SolidColorBrush fore, SolidColorBrush back) GetStepStateBrushes(StepState state)
        {
            return state switch
            {
                StepState.Built => (SuccessBrush, SuccessSoftBrush),
                StepState.OutOfDate => (WarningBrush, WarningSoftBrush),
                StepState.HasIssues => (DangerBrush, DangerSoftBrush),
                StepState.NotBuilt => (AccentBrush, AccentSoftBrush),
                StepState.NotApplicable => (MutedBrush, CreateFrozenBrush(Color.FromRgb(241, 245, 249))),
                _ => (MutedBrush, BorderBrush)
            };
        }

        public static (SolidColorBrush fore, SolidColorBrush back) GetSeverityBrushes(IssueSeverity severity)
        {
            return severity switch
            {
                IssueSeverity.Error => (DangerBrush, DangerSoftBrush),
                IssueSeverity.Warning => (WarningBrush, WarningSoftBrush),
                IssueSeverity.Info => (AccentBrush, AccentSoftBrush),
                _ => (MutedBrush, BorderBrush)
            };
        }

        public static (SolidColorBrush fore, SolidColorBrush back, SolidColorBrush border) GetOperationStatusBrushes(OperationStatus status)
        {
            return status switch
            {
                OperationStatus.Succeeded => (SuccessBrush, SuccessSoftBrush, SuccessBrush),
                OperationStatus.NoChange => (AccentBrush, CreateFrozenBrush(Color.FromRgb(239, 246, 255)), AccentBrush),
                OperationStatus.Rejected => (DangerBrush, DangerSoftBrush, DangerBrush),
                OperationStatus.Cancelled => (MutedBrush, CreateFrozenBrush(Color.FromRgb(241, 245, 249)), BorderBrush),
                OperationStatus.RolledBack => (DangerBrush, DangerSoftBrush, DangerBrush),
                OperationStatus.PartiallySucceeded => (WarningBrush, WarningSoftBrush, WarningBrush),
                OperationStatus.OutcomeUnknown => (PurpleBrush, PurpleSoftBrush, PurpleBrush),
                _ => (MutedBrush, SurfaceBrush, BorderBrush)
            };
        }

        public static Border CreatePill(string text, SolidColorBrush fore, SolidColorBrush back, double fontSize = SmallFontSize)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = Segoe,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = fore,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var border = new Border
            {
                Background = back,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(2, 0, 2, 0),
                Child = tb
            };

            return border;
        }

        public static Button CreatePrimaryButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                FontFamily = Segoe,
                FontSize = BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = AccentBrush,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            return btn;
        }

        public static Button CreateSecondaryButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                FontFamily = Segoe,
                FontSize = BaseFontSize,
                FontWeight = FontWeights.Normal,
                Foreground = TextBrush,
                Background = PanelBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            return btn;
        }

        public static Button CreateDangerButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                FontFamily = Segoe,
                FontSize = BaseFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = DangerBrush,
                BorderBrush = DangerBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            return btn;
        }

        public static Border CreateCard(UIElement content, Thickness margin = default)
        {
            return new Border
            {
                Background = PanelBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = margin,
                Child = content
            };
        }
    }
}
