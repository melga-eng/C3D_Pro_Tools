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
        public static readonly Color HeaderColor = Color.FromRgb(14, 26, 45);          // #0E1A2D (Deep Prussian blueprint navy)
        public static readonly Color HeaderTextColor = Color.FromRgb(244, 247, 251);    // #F4F7FB (Starch white)
        public static readonly Color HeaderMutedColor = Color.FromRgb(141, 161, 190);   // #8DA1BE (Blueprint chalk cyan metadata)
        public static readonly Color SurfaceColor = Color.FromRgb(243, 239, 232);      // #F3EFE8 (Warm architectural drafting vellum)
        public static readonly Color PanelColor = Colors.White;                        // #FFFFFF (Clean drafting card & grid background)
        public static readonly Color BorderColor = Color.FromRgb(217, 209, 195);       // #D9D1C3 (Drafting rule buff borders & grid lines)
        public static readonly Color TextColor = Color.FromRgb(23, 28, 36);            // #171C24 (Drafting ink black)
        public static readonly Color MutedColor = Color.FromRgb(87, 83, 74);           // #57534A (pencil lead sepia; 4.5:1 or more on every vellum fill)
        public static readonly Color AccentColor = Color.FromRgb(27, 88, 156);         // #1B589C (Prussian cobalt blueprint ink)
        public static readonly Color AccentHoverColor = Color.FromRgb(20, 67, 119);    // #144377 (Deep cobalt hover)
        public static readonly Color AccentSoftColor = Color.FromRgb(228, 237, 247);   // #E4EDF7 (Blueprint wash row selection)
        public static readonly Color DangerColor = Color.FromRgb(185, 28, 28);         // #B91C1C (Red correction pen alert)
        public static readonly Color DangerSoftColor = Color.FromRgb(252, 226, 226);   // #FCE2E2 (Crimson deviation alert background)
        public static readonly Color WarningColor = Color.FromRgb(146, 64, 14);        // #92400E (Ochre review / medium)
        public static readonly Color WarningSoftColor = Color.FromRgb(253, 241, 214);  // #FDF1D6 (Ochre review background)
        public static readonly Color SuccessColor = Color.FromRgb(21, 101, 53);        // #156535 (Exact / High survey precision)
        public static readonly Color SuccessSoftColor = Color.FromRgb(225, 242, 231);  // #E1F2E7 (Exact / High background)
        public static readonly Color PurpleColor = Color.FromRgb(90, 58, 139);         // #5A3A8B (Blueprint indigo / structure)
        public static readonly Color PurpleSoftColor = Color.FromRgb(239, 235, 248);   // #EFEBF8 (Blueprint indigo background)

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
        public static readonly FontFamily Segoe = new FontFamily("Aptos, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Consolas, Cascadia Mono");
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
                StepState.NotApplicable => (MutedBrush, CreateFrozenBrush(Color.FromRgb(234, 229, 220))),
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
                OperationStatus.NoChange => (AccentBrush, AccentSoftBrush, AccentBrush),
                OperationStatus.Rejected => (DangerBrush, DangerSoftBrush, DangerBrush),
                OperationStatus.Cancelled => (MutedBrush, CreateFrozenBrush(Color.FromRgb(234, 229, 220)), BorderBrush),
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
