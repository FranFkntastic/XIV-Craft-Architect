using System.Windows;
using System.Windows.Controls;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Views;

/// <summary>
/// Window for managing project items with a full data grid view.
/// Provides better handling of large item lists compared to the side panel.
/// </summary>
public partial class ProjectItemsWindow : Window
{
    private readonly ILogger<ProjectItemsWindow>? _logger;
    private readonly List<ProjectItem> _items;
    private readonly string? _planName;
    private readonly Action<List<ProjectItem>>? _onItemsChanged;
    private readonly Func<Task<ProjectItem?>>? _onAddItem;

    public ProjectItemsWindow(
        List<ProjectItem> items,
        string? planName = null,
        Action<List<ProjectItem>>? onItemsChanged = null,
        Func<Task<ProjectItem?>>? onAddItem = null,
        ILogger<ProjectItemsWindow>? logger = null)
    {
        InitializeComponent();
        
        _logger = logger;
        
        _items = items;
        _planName = planName;
        _onItemsChanged = onItemsChanged;
        _onAddItem = onAddItem;
        
        // Subscribe to collection changes
        ItemsDataGrid.CellEditEnding += OnCellEditEnding;
        
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        // Set header info
        PlanNameText.Text = string.IsNullOrEmpty(_planName) ? "Project Items" : _planName;
        ItemCountText.Text = $"{_items.Count} items";
        
        // Update stats
        var totalQty = _items.Sum(i => i.Quantity);
        var hqCount = _items.Count(i => i.IsHqRequired);
        TotalQuantityText.Text = totalQty.ToString();
        HqCountText.Text = hqCount.ToString();
        
        // Refresh grid
        ItemsDataGrid.ItemsSource = null;
        ItemsDataGrid.ItemsSource = _items;
        
        // Notify parent of changes
        _onItemsChanged?.Invoke(_items);
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Refresh stats after edit
        if (e.EditAction == DataGridEditAction.Commit)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var totalQty = _items.Sum(i => i.Quantity);
                var hqCount = _items.Count(i => i.IsHqRequired);
                TotalQuantityText.Text = totalQty.ToString();
                HqCountText.Text = hqCount.ToString();
                
                _onItemsChanged?.Invoke(_items);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private async void OnAddItemClick(object sender, RoutedEventArgs e)
    {
        if (_onAddItem != null)
        {
            var newItem = await _onAddItem();
            if (newItem != null)
            {
                _items.Add(newItem);
                RefreshDisplay();
                _logger?.LogDebug("Added item to project: {Name} x{Quantity}", newItem.Name, newItem.Quantity);
            }
        }
        else
        {
            MessageBox.Show(
                "To add items, close this window and use the main item search box on the left panel.",
                "Add Items",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ProjectItem item)
        {
            var result = MessageBox.Show(
                $"Remove '{item.Name}' from the project?",
                "Confirm Remove",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _items.Remove(item);
                RefreshDisplay();
                _logger?.LogDebug("Removed item from project: {Name}", item.Name);
            }
        }
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        
        var result = MessageBox.Show(
            $"Clear all {_items.Count} items from the project?",
            "Confirm Clear All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            _items.Clear();
            RefreshDisplay();
            _logger?.LogInformation("Cleared all project items");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Final callback with current state
        _onItemsChanged?.Invoke(_items);
        base.OnClosing(e);
    }
}
