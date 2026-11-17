using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    internal sealed class SheetItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public event PropertyChangedEventHandler PropertyChanged;

        public SheetStatus Status { get; }
        public string Layout => Status.Layout;
        public SheetKind Kind => Status.Kind;
        public string Tile => Status.Tile;
        public string Part => Status.Part;
        public string DrawingNumber => Status.DrawingNumber;

        /// <summary>
        /// What fits in the sheet list: the last part of a long layout name (SW00670060000C-…-102054 shows 102054), the whole name
        /// otherwise. The full name is the row's tooltip.
        /// </summary>
        public string ShortName
        {
            get
            {
                string name = Layout ?? "";
                int dash = name.LastIndexOf('-');
                return name.Length > 16 && dash >= 0 && dash < name.Length - 1 ? name.Substring(dash + 1) : name;
            }
        }

        /// <summary>"Plan 124-D", "Profile 124", or the kind alone when the tile is unknown.</summary>
        public string KindText =>
            (Kind == SheetKind.Plan ? "Plan" : "Profile") +
            (string.IsNullOrEmpty(Tile) ? "" : " " + Tile + (string.IsNullOrEmpty(Part) ? "" : "-" + Part));
        public int IssueCount => Status.IssueCount;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public StepState PlanLabelsState => GetStep(BuildStep.PlanLabels);
        public StepState ProfileViewsState => GetStep(BuildStep.ProfileViews);
        public StepState ProfileLabelsState => GetStep(BuildStep.ProfileLabels);
        public StepState DimsState => GetStep(BuildStep.Dims);
        public StepState SheetDataState => GetStep(BuildStep.SheetData);
        public StepState ArrangeState => GetStep(BuildStep.Arrange);

        public SheetItemViewModel(SheetStatus status, bool isSelected = true)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
            _isSelected = isSelected;
        }

        private StepState GetStep(BuildStep step)
        {
            return Status.Steps.TryGetValue(step, out var state) ? state : StepState.NotApplicable;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
