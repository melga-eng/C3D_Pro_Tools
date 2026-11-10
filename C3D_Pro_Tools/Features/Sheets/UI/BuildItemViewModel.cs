using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using C3DProTools.Features.Shared.Contracts;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.UI
{
    internal sealed class PlannedChangeViewModel
    {
        public PlannedChange Change { get; }
        public string ChangeId => Change.ChangeId;
        public BuildStep Step => Change.Step;
        public ChangeKind Kind => Change.Kind;
        public ChangeTarget Target => Change.Target;
        public string Layout => Change.Layout;
        public string RuleId => Change.RuleId;
        public string SourceKey => Change.SourceKey;
        public string ExistingHandle => Change.Existing.HasValue ? Change.Existing.Value.Handle : "";
        public string Description => Change.Description;

        public PlannedChangeViewModel(PlannedChange change)
        {
            Change = change ?? throw new ArgumentNullException(nameof(change));
        }
    }

    internal sealed class BuildItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public event PropertyChangedEventHandler PropertyChanged;

        public BuildItem Item { get; }
        public string ItemKey => Item.ItemKey;
        public BuildItemKind Kind => Item.Kind;
        public IReadOnlyList<string> Layouts => Item.Layouts;
        public string LayoutsSummary => string.Join(", ", Item.Layouts);
        public IReadOnlyList<PlannedChangeViewModel> Changes { get; }
        public int ChangeCount => Changes.Count;

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

        public BuildItemViewModel(BuildItem item, bool isSelected = true)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            _isSelected = isSelected;
            Changes = item.Changes.Select(c => new PlannedChangeViewModel(c)).ToList();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
