using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect.Views;

/// <summary>
/// Dialog for searching and selecting an item to add to the plan.
/// </summary>
public partial class ItemSearchDialog : Window
{
    private readonly GarlandService _garlandService;
    private List<SearchResultViewModel> _searchResults = new();

    public ItemSearchDialog(GarlandService garlandService)
    {
        InitializeComponent();
        _garlandService = garlandService;
    }

    /// <summary>
    /// The selected item from the search results.
    /// </summary>
    public SearchResultViewModel? SelectedItem { get; private set; }

    #region Event Handlers

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearch(sender, e);
        }
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        try
        {
            var results = await _garlandService.SearchAsync(query);
            _searchResults = results.Select(r => new SearchResultViewModel
            {
                Id = r.Id,
                Name = r.Object.Name,
                IconId = r.Object.IconId,
                IconUrl = $"https://www.garlandtools.org/files/icons/item/{r.Object.IconId}.png"
            }).ToList();

            ResultsListBox.ItemsSource = _searchResults;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Search failed: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = ResultsListBox.SelectedItem != null;
    }

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        SelectedItem = ResultsListBox.SelectedItem as SearchResultViewModel;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion
}

/// <summary>
/// ViewModel for search results in the item search dialog.
/// </summary>
public class SearchResultViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public string IconUrl { get; set; } = string.Empty;
}
