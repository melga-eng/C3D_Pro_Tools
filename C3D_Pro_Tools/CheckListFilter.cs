using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace C3DProTools
{
    /// <summary>
    /// Adds a name search to a CheckedListBox. Ticks are remembered by name, so they survive the search text
    /// changing; the All / None buttons apply to the items the search currently shows.
    /// </summary>
    internal sealed class CheckListFilter
    {
        private readonly TextBox _search;
        private readonly CheckedListBox _list;
        private readonly List<string> _items = new List<string>();
        private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.Ordinal);
        private bool _refreshing;

        /// <summary>Raised after the set of ticked items changes.</summary>
        public event EventHandler CheckedChanged;

        public CheckListFilter(TextBox search, CheckedListBox list, Button all, Button none)
        {
            _search = search;
            _list = list;
            _list.CheckOnClick = true;
            _search.TextChanged += (s, e) => Refresh();
            _list.ItemCheck += List_ItemCheck;
            all.Click += (s, e) => SetShown(true);
            none.Click += (s, e) => SetShown(false);
        }

        public int Count => _items.Count;

        public List<string> CheckedItems => _items.FindAll(_checked.Contains);

        public void SetItems(IEnumerable<string> items, bool checkAll)
        {
            _items.Clear();
            _checked.Clear();
            _items.AddRange(items);
            if (checkAll) _checked.UnionWith(_items);
            Refresh();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Ticks exactly the given items (unknown names are ignored) and clears the search so they are all visible.</summary>
        public void SetCheckedOnly(IEnumerable<string> names)
        {
            _checked.Clear();
            foreach (string name in names)
            {
                if (_items.Contains(name)) _checked.Add(name);
            }
            if (_search.Text.Length > 0) _search.Text = "";
            Refresh();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }

        private void List_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_refreshing) return;
            string name = _list.Items[e.Index].ToString();
            if (e.NewValue == CheckState.Checked) _checked.Add(name);
            else _checked.Remove(name);
            // ItemCheck fires before the box changes, so report the change once the list has applied it.
            _list.BeginInvoke(new Action(() => CheckedChanged?.Invoke(this, EventArgs.Empty)));
        }

        private void SetShown(bool value)
        {
            foreach (object item in _list.Items)
            {
                if (value) _checked.Add(item.ToString());
                else _checked.Remove(item.ToString());
            }
            Refresh();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Refresh()
        {
            string filter = _search.Text.Trim();
            _refreshing = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (string name in _items)
                {
                    if (filter.Length == 0 || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        _list.Items.Add(name, _checked.Contains(name));
                }
            }
            finally
            {
                _list.EndUpdate();
                _refreshing = false;
            }
        }
    }
}
