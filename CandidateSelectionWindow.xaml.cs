using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace CadenceCisLibraryManager;

public partial class CandidateSelectionWindow : Window
{
    private readonly List<CandidateItem> _allItems;
    private readonly ObservableCollection<CandidateItem> _filteredItems = [];

    public CandidateSelectionWindow(string title, IEnumerable<string> candidates, IEnumerable<string>? selectedValues = null)
    {
        InitializeComponent();
        Title = title;
        var selectedSet = new HashSet<string>(selectedValues ?? [], StringComparer.OrdinalIgnoreCase);
        _allItems = candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new CandidateItem(value, selectedSet.Contains(value)))
            .ToList();

        CandidatesListBox.ItemsSource = _filteredItems;
        ApplyFilter();
    }

    public IReadOnlyList<string> SelectedValues => _allItems
        .Where(item => item.IsSelected)
        .Select(item => item.Value)
        .ToList();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allItems)
        {
            item.IsSelected = false;
        }

        foreach (var item in _filteredItems)
        {
            item.IsSelected = CandidatesListBox.SelectedItems.Contains(item);
        }

        DialogResult = true;
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        var visibleItems = string.IsNullOrWhiteSpace(keyword)
            ? _allItems
            : _allItems.Where(item => item.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        _filteredItems.Clear();
        foreach (var item in visibleItems)
        {
            _filteredItems.Add(item);
        }

        CandidatesListBox.SelectedItems.Clear();
        foreach (var item in visibleItems.Where(item => item.IsSelected))
        {
            CandidatesListBox.SelectedItems.Add(item);
        }
    }

    private sealed class CandidateItem(string value, bool isSelected)
    {
        public string Value { get; } = value;

        public bool IsSelected { get; set; } = isSelected;
    }
}
