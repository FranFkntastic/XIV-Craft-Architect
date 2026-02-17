using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Views;

/// <summary>
/// Dialog for browsing and managing root items in a crafting plan.
/// </summary>
public partial class PlanBrowserDialog : Window
{
    private readonly GarlandService _garlandService;
    private readonly ObservableCollection<RootItemViewModel> _items;
    private bool _hasChanges = false;

    public PlanBrowserDialog(GarlandService garlandService, IEnumerable<ProjectItem> rootItems)
    {
        InitializeComponent();
        _garlandService = garlandService;
        _items = new ObservableCollection<RootItemViewModel>(
            rootItems.Select((item, index) => new RootItemViewModel
            {
                Index = index,
                Id = item.Id,
                Name = item.Name,
                Quantity = item.Quantity,
                IsHqRequired = item.IsHqRequired,
                IconUrl = $"https://www.garlandtools.org/files/icons/item/{GetIconId(item.Id)}.png"
            }));
        
        RootItemsGrid.ItemsSource = _items;
        UpdateItemCount();
    }

    /// <summary>
    /// Returns the modified root items after the dialog closes.
    /// </summary>
    public List<ProjectItem> GetModifiedItems()
    {
        return _items.Select(vm => new ProjectItem
        {
            Id = vm.Id,
            Name = vm.Name,
            Quantity = vm.Quantity,
            IsHqRequired = vm.IsHqRequired
        }).ToList();
    }

    /// <summary>
    /// Returns true if any changes were made.
    /// </summary>
    public bool HasChanges => _hasChanges;

    #region Event Handlers

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = RootItemsGrid.SelectedItem != null;
        MoveUpButton.IsEnabled = hasSelection && RootItemsGrid.SelectedIndex > 0;
        MoveDownButton.IsEnabled = hasSelection && RootItemsGrid.SelectedIndex < _items.Count - 1;
        RemoveButton.IsEnabled = hasSelection;
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        var index = RootItemsGrid.SelectedIndex;
        if (index > 0)
        {
            SwapItems(index, index - 1);
            RootItemsGrid.SelectedIndex = index - 1;
            _hasChanges = true;
        }
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        var index = RootItemsGrid.SelectedIndex;
        if (index >= 0 && index < _items.Count - 1)
        {
            SwapItems(index, index + 1);
            RootItemsGrid.SelectedIndex = index + 1;
            _hasChanges = true;
        }
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        var selected = RootItemsGrid.SelectedItem as RootItemViewModel;
        if (selected != null)
        {
            _items.Remove(selected);
            ReindexItems();
            UpdateItemCount();
            UpdateButtonStates();
            _hasChanges = true;
        }
    }

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is RootItemViewModel item)
        {
            _items.Remove(item);
            ReindexItems();
            UpdateItemCount();
            UpdateButtonStates();
            _hasChanges = true;
        }
    }

    private void OnAddNewItem(object sender, RoutedEventArgs e)
    {
        // Open item search dialog
        var searchDialog = new ItemSearchDialog(_garlandService);
        if (searchDialog.ShowDialog() == true && searchDialog.SelectedItem != null)
        {
            var result = searchDialog.SelectedItem;
            _items.Add(new RootItemViewModel
            {
                Index = _items.Count,
                Id = result.Id,
                Name = result.Name,
                Quantity = 1,
                IsHqRequired = false,
                IconUrl = $"https://www.garlandtools.org/files/icons/item/{result.IconId}.png"
            });
            UpdateItemCount();
            _hasChanges = true;
        }
    }

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            _hasChanges = true;
            
            // Validate quantity is a positive number
            if (e.Column.Header?.ToString() == "Qty" && e.EditingElement is TextBox textBox)
            {
                if (!int.TryParse(textBox.Text, out int qty) || qty < 1)
                {
                    MessageBox.Show("Quantity must be a positive number.", "Invalid Input", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Cancel = true;
                }
            }
        }
    }

    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow numeric input
        var regex = new Regex("[^0-9]");
        e.Handled = regex.IsMatch(e.Text);
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion

    #region Helper Methods

    private void SwapItems(int index1, int index2)
    {
        var temp = _items[index1];
        _items[index1] = _items[index2];
        _items[index2] = temp;
        _items[index1].Index = index1;
        _items[index2].Index = index2;
    }

    private void ReindexItems()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Index = i;
        }
    }

    private void UpdateItemCount()
    {
        ItemCountText.Text = $"{_items.Count} item{(_items.Count == 1 ? "" : "s")}";
    }

    private void UpdateButtonStates()
    {
        var hasSelection = RootItemsGrid.SelectedItem != null;
        var index = RootItemsGrid.SelectedIndex;
        MoveUpButton.IsEnabled = hasSelection && index > 0;
        MoveDownButton.IsEnabled = hasSelection && index < _items.Count - 1;
        RemoveButton.IsEnabled = hasSelection;
    }

    private static int GetIconId(int itemId)
    {
        // Garland uses a specific icon ID format
        // This is a simplified version - in production, fetch from item data
        return 0;
    }

    #endregion
}

/// <summary>
/// ViewModel for root items in the plan browser.
/// </summary>
public class RootItemViewModel : INotifyPropertyChanged
{
    public int Index { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    
    private int _quantity = 1;
    public int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            OnPropertyChanged(nameof(Quantity));
        }
    }
    
    private bool _isHqRequired = false;
    public bool IsHqRequired
    {
        get => _isHqRequired;
        set
        {
            _isHqRequired = value;
            OnPropertyChanged(nameof(IsHqRequired));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
