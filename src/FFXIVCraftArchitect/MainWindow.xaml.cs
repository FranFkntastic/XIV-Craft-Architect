using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.DependencyInjection;
using Window = System.Windows.Window;

namespace FFXIVCraftArchitect;

/// <summary>
/// Represents an item in the project list
/// </summary>
public class ProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public override string ToString() => $"{Name} x{Quantity}";
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly GarlandService _garlandService;
    private readonly UniversalisService _universalisService;
    private readonly SettingsService _settingsService;
    private readonly ItemCacheService _itemCache;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly PlanPersistenceService _planPersistence;

    // Search state
    private List<GarlandSearchResult> _currentSearchResults = new();
    private GarlandSearchResult? _selectedSearchResult;
    
    // Project state
    private List<ProjectItem> _projectItems = new();
    
    // Current crafting plan
    private CraftingPlan? _currentPlan;

    public MainWindow()
    {
        InitializeComponent();

        // Get services from DI
        _garlandService = App.Services.GetRequiredService<GarlandService>();
        _universalisService = App.Services.GetRequiredService<UniversalisService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _itemCache = App.Services.GetRequiredService<ItemCacheService>();
        _recipeCalcService = App.Services.GetRequiredService<RecipeCalculationService>();
        _planPersistence = App.Services.GetRequiredService<PlanPersistenceService>();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Load world data
        await LoadWorldDataAsync();
    }

    private async Task LoadWorldDataAsync()
    {
        StatusLabel.Text = "Loading world data...";
        try
        {
            var worldData = await _universalisService.GetWorldDataAsync();
            
            DcCombo.ItemsSource = worldData.DataCenters;
            DcCombo.SelectedItem = _settingsService.Get<string>("market.default_datacenter") ?? "Aether";
            OnDataCenterSelected(null, null);
            
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to load world data: {ex.Message}";
        }
    }

    // =========================================================================
    // Event Handlers
    // =========================================================================

    private void OnDataCenterSelected(object? sender, SelectionChangedEventArgs? e)
    {
        var dc = DcCombo.SelectedItem as string;
        if (dc != null && _universalisService != null)
        {
            // Get cached world data (loaded on startup)
            var worldData = _universalisService.GetCachedWorldData();
            if (worldData?.DataCenterToWorlds.TryGetValue(dc, out var worlds) == true)
            {
                var worldList = new List<string> { "Entire Data Center" };
                worldList.AddRange(worlds);
                WorldCombo.ItemsSource = worldList;
                WorldCombo.SelectedItem = "Entire Data Center";
            }
        }
    }

    private void OnSyncInventory(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Sync inventory clicked - not implemented yet";
    }

    private void OnLiveModeToggled(object sender, RoutedEventArgs e)
    {
        var isOn = LiveSwitch.IsChecked == true;
        StatusLabel.Text = isOn ? "Live mode toggled ON - not implemented yet" : "Live mode toggled OFF";
    }

    private void OnItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchItem(sender, e);
        }
    }

    private async void OnSearchItem(object sender, RoutedEventArgs e)
    {
        var query = ItemSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        StatusLabel.Text = $"Searching for '{query}'...";
        SearchResults.ItemsSource = null;
        _selectedSearchResult = null;
        AddToProjectButton.IsEnabled = false;
        
        try
        {
            _currentSearchResults = await _garlandService.SearchAsync(query);
            SearchResults.ItemsSource = _currentSearchResults.Select(r => r.Object?.Name ?? $"Item_{r.Id}").ToList();
            
            // Cache the search results for later use
            _itemCache.StoreItems(_currentSearchResults.Select(r => (r.Id, r.Object?.Name ?? $"Item_{r.Id}", r.Object?.IconId ?? 0)));
            
            StatusLabel.Text = $"Found {_currentSearchResults.Count} results (cached)";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Search failed: {ex.Message}";
        }
    }

    private void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        var index = SearchResults.SelectedIndex;
        if (index >= 0 && index < _currentSearchResults.Count)
        {
            _selectedSearchResult = _currentSearchResults[index];
            AddToProjectButton.IsEnabled = true;
            var name = _selectedSearchResult.Object?.Name ?? $"Item_{_selectedSearchResult.Id}";
            StatusLabel.Text = $"Selected: {name} (ID: {_selectedSearchResult.Id})";
        }
        else
        {
            _selectedSearchResult = null;
            AddToProjectButton.IsEnabled = false;
        }
    }

    private void OnAddToProject(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchResult == null)
            return;

        var itemName = _selectedSearchResult.Object?.Name ?? $"Item_{_selectedSearchResult.Id}";

        // Check if already in project
        var existing = _projectItems.FirstOrDefault(p => p.Id == _selectedSearchResult.Id);
        if (existing != null)
        {
            existing.Quantity++;
            StatusLabel.Text = $"Increased quantity of {existing.Name} to {existing.Quantity}";
        }
        else
        {
            _projectItems.Add(new ProjectItem
            {
                Id = _selectedSearchResult.Id,
                Name = itemName,
                Quantity = 1
            });
            StatusLabel.Text = $"Added {itemName} to project";
        }

        // Refresh project list
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _projectItems;
        
        // Enable build button if we have items
        BuildPlanButton.IsEnabled = _projectItems.Count > 0;
    }

    private async void OnBuildProjectPlan(object sender, RoutedEventArgs e)
    {
        if (_projectItems.Count == 0)
        {
            StatusLabel.Text = "Add items to project first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";

        StatusLabel.Text = $"Building plan for {_projectItems.Count} items...";
        BuildPlanButton.IsEnabled = false;
        
        try
        {
            var targets = _projectItems.Select(p => (p.Id, p.Name, p.Quantity)).ToList();
            _currentPlan = await _recipeCalcService.BuildPlanAsync(targets, dc, world);
            
            // Display in TreeView
            DisplayPlanInTreeView(_currentPlan);
            
            StatusLabel.Text = $"Plan built: {_currentPlan.RootItems.Count} root items, " +
                               $"{_currentPlan.AggregatedMaterials.Count} unique materials";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to build plan: {ex.Message}";
        }
        finally
        {
            BuildPlanButton.IsEnabled = _projectItems.Count > 0;
        }
    }

    /// <summary>
    /// Display the crafting plan in the TreeView with craft/buy toggles.
    /// </summary>
    private void DisplayPlanInTreeView(CraftingPlan plan)
    {
        RecipeTree.Items.Clear();
        
        foreach (var rootItem in plan.RootItems)
        {
            var rootNode = CreateTreeViewItem(rootItem);
            RecipeTree.Items.Add(rootNode);
        }
        
        // Update shopping list with aggregated materials
        ShoppingList.ItemsSource = plan.AggregatedMaterials;
    }

    /// <summary>
    /// Recursively create a TreeViewItem from a PlanNode with editing capabilities.
    /// </summary>
    private TreeViewItem CreateTreeViewItem(PlanNode node)
    {
        // Header panel with name, quantity, and buy/craft toggle
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Buy/Craft checkbox
        var buyCheck = new CheckBox 
        { 
            Content = "Buy",
            IsChecked = node.IsBuy,
            ToolTip = node.IsUncraftable ? "Must be bought (no recipe)" : "Check to buy from market",
            IsEnabled = !node.IsUncraftable,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        buyCheck.Checked += (s, e) => ToggleNodeBuyMode(node, true);
        buyCheck.Unchecked += (s, e) => ToggleNodeBuyMode(node, false);
        panel.Children.Add(buyCheck);
        
        // Item name and details
        var nameText = new TextBlock 
        { 
            Text = $"{node.Name} x{node.Quantity}",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = node.Parent == null ? FontWeights.Bold : FontWeights.Normal
        };
        panel.Children.Add(nameText);
        
        // Add recipe info if craftable
        if (!node.IsUncraftable && !string.IsNullOrEmpty(node.Job))
        {
            var infoText = new TextBlock
            {
                Text = $"  ({node.Job} Lv.{node.RecipeLevel}, Yield: {node.Yield})",
                Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            panel.Children.Add(infoText);
        }
        
        // Price/cost info
        var costText = GetNodeCostText(node);
        if (!string.IsNullOrEmpty(costText))
        {
            var priceText = new TextBlock
            {
                Text = $"  {costText}",
                Foreground = System.Windows.Media.Brushes.Gold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            panel.Children.Add(priceText);
        }
        
        var treeItem = new TreeViewItem
        {
            Header = panel,
            IsExpanded = true,
            Tag = node // Store reference to the PlanNode for editing
        };
        
        // Recursively add children
        foreach (var child in node.Children)
        {
            treeItem.Items.Add(CreateTreeViewItem(child));
        }
        
        return treeItem;
    }

    /// <summary>
    /// Toggle an item between buy and craft mode.
    /// </summary>
    private void ToggleNodeBuyMode(PlanNode node, bool buy)
    {
        _recipeCalcService.ToggleBuyMode(node, buy);
        
        // Refresh the display
        if (_currentPlan != null)
        {
            DisplayPlanInTreeView(_currentPlan);
        }
        
        StatusLabel.Text = $"{node.Name} set to {(buy ? "buy" : "craft")}";
    }

    /// <summary>
    /// Save the current plan to disk.
    /// </summary>
    private async void OnSavePlan(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null)
        {
            StatusLabel.Text = "No plan to save - build a plan first";
            return;
        }

        var success = await _planPersistence.SavePlanAsync(_currentPlan);
        StatusLabel.Text = success 
            ? $"Plan saved: {_currentPlan.Name}" 
            : "Failed to save plan";
    }

    /// <summary>
    /// Load a saved plan from disk.
    /// </summary>
    private async void OnLoadPlan(object sender, RoutedEventArgs e)
    {
        var browser = new PlanBrowserWindow(_planPersistence)
        {
            Owner = this
        };

        if (browser.ShowDialog() == true && !string.IsNullOrEmpty(browser.SelectedPlanPath))
        {
            _currentPlan = await _planPersistence.LoadPlanAsync(browser.SelectedPlanPath);
            
            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
                
                // Sync to project items
                _projectItems = _currentPlan.RootItems.Select(r => new ProjectItem 
                { 
                    Id = r.ItemId, 
                    Name = r.Name, 
                    Quantity = r.Quantity 
                }).ToList();
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _projectItems;
                
                BuildPlanButton.IsEnabled = _projectItems.Count > 0;
                StatusLabel.Text = $"Loaded plan: {_currentPlan.Name}";
            }
            else
            {
                StatusLabel.Text = "Failed to load plan";
            }
        }
    }

    /// <summary>
    /// Import from Teamcraft "Copy as Text" format.
    /// </summary>
    private void OnImportTeamcraft(object sender, RoutedEventArgs e)
    {
        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        
        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var importDialog = new TeamcraftImportWindow(teamcraft, dc, world)
        {
            Owner = this
        };

        if (importDialog.ShowDialog() == true && importDialog.ImportedPlan != null)
        {
            _currentPlan = importDialog.ImportedPlan;
            
            // Sync to project items
            _projectItems = _currentPlan.RootItems.Select(r => new ProjectItem 
            { 
                Id = r.ItemId, 
                Name = r.Name, 
                Quantity = r.Quantity 
            }).ToList();
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _projectItems;
            
            // Display the plan
            DisplayPlanInTreeView(_currentPlan);
            
            BuildPlanButton.IsEnabled = _projectItems.Count > 0;
            StatusLabel.Text = $"Imported plan with {_currentPlan.RootItems.Count} items from Teamcraft";
        }
    }

    /// <summary>
    /// Try to set clipboard text with retry logic (clipboard may be locked by another app).
    /// </summary>
    private bool TrySetClipboard(string text, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                if (i == maxRetries - 1) return false;
                Thread.Sleep(100); // Wait a bit before retry
            }
        }
        return false;
    }

    /// <summary>
    /// Export current plan to Teamcraft URL (copies to clipboard).
    /// </summary>
    private void OnExportTeamcraft(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var url = teamcraft.ExportToTeamcraft(_currentPlan);
        
        if (TrySetClipboard(url))
        {
            StatusLabel.Text = "Teamcraft URL copied to clipboard!";
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use. URL shown in logs.";
            // Could also show a dialog with the text
        }
    }

    /// <summary>
    /// Export plan as plain text to clipboard.
    /// </summary>
    private void OnExportPlainText(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var text = teamcraft.ExportToPlainText(_currentPlan);
        
        if (TrySetClipboard(text))
        {
            StatusLabel.Text = "Plan text copied to clipboard!";
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Export shopping list as CSV.
    /// </summary>
    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var csv = teamcraft.ExportToCsv(_currentPlan);
        
        if (TrySetClipboard(csv))
        {
            StatusLabel.Text = "CSV copied to clipboard!";
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        var logWindow = new LogViewerWindow
        {
            Owner = this
        };
        logWindow.Show();
    }

    private void OnViewInventory(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "View inventory clicked - not implemented yet";
    }

    private void OnReloadApp(object sender, RoutedEventArgs e)
    {
        // Restart the application
        Process.Start(Process.GetCurrentProcess().MainModule?.FileName ?? "");
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Save item cache
        _itemCache.SaveCache();
        
        // Cleanup logic here (stop live mode, etc.)
        base.OnClosing(e);
    }

    // =========================================================================
    // Quantity Editing
    // =========================================================================

    /// <summary>
    /// Prevent non-numeric input in quantity field
    /// </summary>
    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow digits
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>
    /// Select all text when quantity field gets focus.
    /// </summary>
    private void OnQuantityGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    /// <summary>
    /// Handle quantity change - validate and update
    /// </summary>
    private void OnQuantityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            // Get the parent ListBoxItem to find the ProjectItem
            var item = FindParentListBoxItem(textBox);
            if (item?.DataContext is ProjectItem projectItem)
            {
                // Parse and validate quantity
                if (!int.TryParse(textBox.Text, out var quantity) || quantity < 1)
                {
                    quantity = 1;
                    textBox.Text = "1";
                }
                
                projectItem.Quantity = quantity;
                StatusLabel.Text = $"Updated {projectItem.Name} quantity to {quantity}";
                
                // Refresh the list to show updated ToString() if needed
                ProjectList.Items.Refresh();
            }
        }
    }

    /// <summary>
    /// Remove an item from the project
    /// </summary>
    private void OnRemoveProjectItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            // Find the parent ListBoxItem to get the DataContext
            var listBoxItem = FindParentListBoxItem(button);
            if (listBoxItem?.DataContext is ProjectItem projectItem)
            {
                _projectItems.Remove(projectItem);
                StatusLabel.Text = $"Removed {projectItem.Name} from project";
                
                // Disable build button if no items left
                BuildPlanButton.IsEnabled = _projectItems.Count > 0;
                
                // Refresh the list
                ProjectList.Items.Refresh();
            }
        }
    }

    /// <summary>
    /// Get cost text for a node - either market price or estimated craft cost.
    /// </summary>
    private string GetNodeCostText(PlanNode node)
    {
        if (node.IsBuy || node.IsUncraftable)
        {
            // Item is marked to be bought - show market price if available
            if (node.MarketPrice > 0)
            {
                return $"~{node.MarketPrice * node.Quantity:N0}g";
            }
            else
            {
                return "(market price needed)";
            }
        }
        else if (node.Children.Any())
        {
            // Item will be crafted - show sum of child costs
            var childCost = CalculateChildCost(node);
            if (childCost > 0)
            {
                return $"~{childCost:N0}g (craft)";
            }
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Calculate total cost of all children (ingredients).
    /// </summary>
    private decimal CalculateChildCost(PlanNode node)
    {
        decimal total = 0;
        foreach (var child in node.Children)
        {
            if (child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Children.Any())
            {
                // Recursively calculate sub-ingredients
                total += CalculateChildCost(child);
            }
        }
        return total;
    }

    /// <summary>
    /// Helper to find the parent ListBoxItem from a child element
    /// </summary>
    private ListBoxItem? FindParentListBoxItem(DependencyObject child)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(child);
        
        while (parent != null && parent is not ListBoxItem)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        
        return parent as ListBoxItem;
    }
}
