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
    private readonly PriceCheckService _priceCheckService;
    private readonly MarketShoppingService _marketShoppingService;

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
        _priceCheckService = App.Services.GetRequiredService<PriceCheckService>();
        _marketShoppingService = App.Services.GetRequiredService<MarketShoppingService>();

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
            UpdateBuildPlanButtonText();
            
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
    /// Update the Build Plan button text based on whether a plan exists.
    /// </summary>
    private void UpdateBuildPlanButtonText()
    {
        var hasPlan = _currentPlan != null && _currentPlan.RootItems.Count > 0;
        BuildPlanButton.Content = hasPlan ? "Rebuild Project Plan" : "Build Project Plan";
        FetchPricesButton.IsEnabled = hasPlan;
    }

    /// <summary>
    /// Display the crafting plan in the TreeView with craft/buy toggles.
    /// </summary>
    private async void DisplayPlanInTreeView(CraftingPlan plan)
    {
        RecipeTree.Items.Clear();
        
        foreach (var rootItem in plan.RootItems)
        {
            var rootNode = CreateTreeViewItem(rootItem);
            RecipeTree.Items.Add(rootNode);
        }
        
        // Check if plan has saved prices and update Market Logistics
        var savedPrices = ExtractPricesFromPlan(plan);
        if (savedPrices.Count > 0)
        {
            await UpdateMarketLogisticsAsync(savedPrices);
            StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices";
        }
        else
        {
            ShowMarketLogisticsPlaceholder();
        }
    }
    
    /// <summary>
    /// Extract price information from a loaded plan's nodes.
    /// </summary>
    private Dictionary<int, PriceInfo> ExtractPricesFromPlan(CraftingPlan plan)
    {
        var prices = new Dictionary<int, PriceInfo>();
        
        foreach (var root in plan.RootItems)
        {
            ExtractPricesFromNode(root, prices);
        }
        
        return prices;
    }
    
    /// <summary>
    /// Recursively extract prices from plan nodes.
    /// </summary>
    private void ExtractPricesFromNode(PlanNode node, Dictionary<int, PriceInfo> prices)
    {
        // Only include nodes with actual price data
        if (node.MarketPrice > 0 || node.PriceSource != PriceSource.Unknown)
        {
            if (!prices.ContainsKey(node.ItemId))
            {
                prices[node.ItemId] = new PriceInfo
                {
                    ItemId = node.ItemId,
                    ItemName = node.Name,
                    UnitPrice = node.MarketPrice,
                    Source = node.PriceSource,
                    SourceDetails = node.PriceSourceDetails
                };
            }
        }
        
        foreach (var child in node.Children)
        {
            ExtractPricesFromNode(child, prices);
        }
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

        var saveDialog = new SavePlanDialog(_planPersistence, _currentPlan.Name)
        {
            Owner = this
        };

        if (saveDialog.ShowDialog() == true)
        {
            // Update plan name if changed
            _currentPlan.Name = saveDialog.PlanName;
            
            var success = await _planPersistence.SavePlanAsync(
                _currentPlan, 
                customName: saveDialog.PlanName,
                overwritePath: saveDialog.OverwritePath);
            
            StatusLabel.Text = success 
                ? saveDialog.IsOverwrite 
                    ? $"Plan overwritten: {_currentPlan.Name}" 
                    : $"Plan saved: {_currentPlan.Name}" 
                : "Failed to save plan";
        }
    }

    /// <summary>
    /// Open plan browser to view, load, rename, or delete saved plans.
    /// </summary>
    private async void OnViewPlans(object sender, RoutedEventArgs e)
    {
        var browser = new PlanBrowserWindow(_planPersistence, this)
        {
            Owner = this
        };

        if (browser.ShowDialog() == true && browser.SelectedAction == PlanBrowserAction.Load && 
            !string.IsNullOrEmpty(browser.SelectedPlanPath))
        {
            _currentPlan = await _planPersistence.LoadPlanAsync(browser.SelectedPlanPath);
            
            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
                UpdateBuildPlanButtonText();
                
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
        else if (browser.SelectedAction == PlanBrowserAction.RenameCurrent && _currentPlan != null)
        {
            // Rename was requested for current plan
            OnRenamePlan(sender, e);
        }
    }

    /// <summary>
    /// Rename the current plan.
    /// </summary>
    private async void OnRenamePlan(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null)
        {
            StatusLabel.Text = "No plan to rename - build or load a plan first";
            return;
        }

        var inputDialog = new RenamePlanDialog(_currentPlan.Name)
        {
            Owner = this
        };

        if (inputDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDialog.NewName))
        {
            var oldName = _currentPlan.Name;
            _currentPlan.Name = inputDialog.NewName;
            _currentPlan.MarkModified();
            
            // Auto-save after rename
            var success = await _planPersistence.SavePlanAsync(_currentPlan);
            if (success)
            {
                StatusLabel.Text = $"Renamed plan from '{oldName}' to '{_currentPlan.Name}'";
            }
            else
            {
                StatusLabel.Text = "Renamed in memory but failed to save";
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
    /// Uses async delay to avoid blocking UI thread.
    /// </summary>
    private async Task<bool> TrySetClipboardAsync(string text, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // Use STA-safe clipboard operation
                await Dispatcher.InvokeAsync(() => Clipboard.SetText(text));
                return true;
            }
            catch
            {
                if (i == maxRetries - 1) return false;
                await Task.Delay(100); // Non-blocking delay
            }
        }
        return false;
    }

    /// <summary>
    /// Export current plan to Teamcraft URL (copies to clipboard).
    /// </summary>
    private async void OnExportTeamcraft(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var url = teamcraft.ExportToTeamcraft(_currentPlan);
        
        if (await TrySetClipboardAsync(url))
        {
            StatusLabel.Text = "Teamcraft URL copied to clipboard!";
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Export plan as plain text to clipboard.
    /// </summary>
    private async void OnExportPlainText(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var text = teamcraft.ExportToPlainText(_currentPlan);
        
        if (await TrySetClipboardAsync(text))
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
    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        var teamcraft = App.Services.GetRequiredService<TeamcraftService>();
        var csv = teamcraft.ExportToCsv(_currentPlan);
        
        if (await TrySetClipboardAsync(csv))
        {
            StatusLabel.Text = "CSV copied to clipboard!";
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Fetch prices for all items in the current plan from Universalis and Garland.
    /// </summary>
    private async void OnFetchPrices(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        var worldOrDc = string.IsNullOrEmpty(world) || world == "Entire Data Center" ? dc : world;

        StatusLabel.Text = "Fetching prices...";
        FetchPricesButton.IsEnabled = false;

        try
        {
            // Collect all unique items from the plan
            var allItems = new List<(int itemId, string name)>();
            CollectAllItems(_currentPlan.RootItems, allItems);

            // Fetch prices in bulk
            var progress = new Progress<(int current, int total, string itemName)>(p =>
            {
                StatusLabel.Text = $"Fetching prices... {p.current}/{p.total} ({p.itemName})";
            });

            var prices = await _priceCheckService.GetBestPricesBulkAsync(
                allItems, 
                worldOrDc, 
                default, 
                progress,
                forceRefresh: true);

            // Update plan nodes with prices
            UpdatePlanWithPrices(_currentPlan.RootItems, prices);

            // Refresh display
            DisplayPlanInTreeView(_currentPlan);
            
            // Update market logistics tab with detailed purchase plan
            await UpdateMarketLogisticsAsync(prices);

            // Calculate total cost
            var totalCost = _currentPlan.AggregatedMaterials.Sum(m => m.TotalCost);
            var vendorItems = prices.Count(p => p.Value.Source == PriceSource.Vendor);
            var marketItems = prices.Count(p => p.Value.Source == PriceSource.Market);

            StatusLabel.Text = $"Prices fetched! Total: {totalCost:N0}g ({vendorItems} vendor, {marketItems} market)";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to fetch prices: {ex.Message}";
        }
        finally
        {
            FetchPricesButton.IsEnabled = _currentPlan != null && _currentPlan.RootItems.Count > 0;
        }
    }

    /// <summary>
    /// Recursively collect all items from the plan.
    /// </summary>
    private void CollectAllItems(List<PlanNode> nodes, List<(int itemId, string name)> items)
    {
        foreach (var node in nodes)
        {
            // Avoid duplicates
            if (!items.Any(i => i.itemId == node.ItemId))
            {
                items.Add((node.ItemId, node.Name));
            }

            if (node.Children?.Any() == true)
            {
                CollectAllItems(node.Children, items);
            }
        }
    }

    /// <summary>
    /// Update plan nodes with fetched prices.
    /// </summary>
    private void UpdatePlanWithPrices(List<PlanNode> nodes, Dictionary<int, PriceInfo> prices)
    {
        foreach (var node in nodes)
        {
            if (prices.TryGetValue(node.ItemId, out var priceInfo))
            {
                node.MarketPrice = priceInfo.UnitPrice;
                // Also track the source for market logistics
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }

            if (node.Children?.Any() == true)
            {
                UpdatePlanWithPrices(node.Children, prices);
            }
        }
    }

    /// <summary>
    /// Show placeholder in Market Logistics when no prices fetched yet.
    /// </summary>
    private void ShowMarketLogisticsPlaceholder()
    {
        MarketCards.Children.Clear();
        
        var placeholderCard = CreateMarketCard("Market Logistics", 
            "Click 'Fetch Prices' to see your purchase plan.\n\n" +
            "This tab will show:\n" +
            "• Items to buy from vendors (cheapest option)\n" +
            "• Items to buy from market board\n" +
            "• Untradeable items you need to gather/craft\n" +
            "• Total estimated cost", "#2d3d4a");
        MarketCards.Children.Add(placeholderCard);
    }

    /// <summary>
    /// Populate the Market Logistics tab with detailed purchase planning information.
    /// </summary>
    private async Task UpdateMarketLogisticsAsync(Dictionary<int, PriceInfo> prices)
    {
        MarketCards.Children.Clear();

        // Group materials by price source
        var vendorItems = new List<MaterialAggregate>();
        var marketItems = new List<MaterialAggregate>();
        var untradeableItems = new List<MaterialAggregate>();

        foreach (var material in _currentPlan?.AggregatedMaterials ?? new List<MaterialAggregate>())
        {
            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
                        vendorItems.Add(material);
                        break;
                    case PriceSource.Market:
                        marketItems.Add(material);
                        break;
                    case PriceSource.Untradeable:
                        untradeableItems.Add(material);
                        break;
                }
            }
            else
            {
                marketItems.Add(material);
            }
        }

        // Summary card
        var summaryCard = CreateMarketCard("Purchase Summary", $"""
            Total Items: {vendorItems.Count + marketItems.Count + untradeableItems.Count}
            
            Vendor Purchases: {vendorItems.Count} items
            - Guaranteed prices, no market fluctuations
            - Total: {vendorItems.Sum(i => i.TotalCost):N0}g
            
            Market Board Purchases: {marketItems.Count} items
            - See detailed listings below
            - Total: {marketItems.Sum(i => i.TotalCost):N0}g
            
            Grand Total: {vendorItems.Sum(i => i.TotalCost) + marketItems.Sum(i => i.TotalCost):N0}g
            """, "#2d4a3e");
        MarketCards.Children.Add(summaryCard);

        // Vendor items card
        if (vendorItems.Any())
        {
            var vendorText = new System.Text.StringBuilder();
            vendorText.AppendLine("Buy these from vendors (cheapest option):");
            vendorText.AppendLine();
            foreach (var item in vendorItems.OrderByDescending(i => i.TotalCost))
            {
                var source = prices[item.ItemId].SourceDetails;
                vendorText.AppendLine($"• {item.Name} x{item.TotalQuantity} = {item.TotalCost:N0}g ({source})");
            }
            var vendorCard = CreateMarketCard($"Vendor Items ({vendorItems.Count})", vendorText.ToString(), "#3e4a2d");
            MarketCards.Children.Add(vendorCard);
        }

        // Market items with detailed listings
        if (marketItems.Any())
        {
            var dc = DcCombo.SelectedItem as string ?? "Aether";
            
            // Show loading message
            var loadingCard = CreateMarketCard("Market Board Items", 
                $"Fetching detailed listings for {marketItems.Count} items from {dc}...", "#3d3e2d");
            MarketCards.Children.Add(loadingCard);
            
            try
            {
                var progress = new Progress<string>(msg => 
                {
                    StatusLabel.Text = $"Analyzing market: {msg}";
                });
                
                var shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                    marketItems, dc, progress);
                
                // Remove loading card
                MarketCards.Children.Remove(loadingCard);
                
                // Add detailed cards for each item
                foreach (var plan in shoppingPlans.OrderByDescending(p => p.QuantityNeeded * p.DCAveragePrice))
                {
                    var itemCard = CreateDetailedMarketCard(plan);
                    MarketCards.Children.Add(itemCard);
                }
            }
            catch (Exception ex)
            {
                MarketCards.Children.Remove(loadingCard);
                var errorCard = CreateMarketCard("Market Board Items", 
                    $"Error fetching listings: {ex.Message}", "#4a2d2d");
                MarketCards.Children.Add(errorCard);
            }
        }

        // Untradeable items card
        if (untradeableItems.Any())
        {
            var untradeText = new System.Text.StringBuilder();
            untradeText.AppendLine("These items must be gathered or crafted:");
            untradeText.AppendLine();
            foreach (var item in untradeableItems)
            {
                untradeText.AppendLine($"• {item.Name} x{item.TotalQuantity}");
            }
            var untradeCard = CreateMarketCard($"Untradeable Items ({untradeableItems.Count})", untradeText.ToString(), "#4a3d2d");
            MarketCards.Children.Add(untradeCard);
        }
    }

    /// <summary>
    /// Create a detailed market card showing world-by-world listings for an item.
    /// </summary>
    private Border CreateDetailedMarketCard(DetailedShoppingPlan plan)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3e2d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var stack = new StackPanel();
        
        // Header with item name and quantity
        var headerText = new TextBlock
        {
            Text = $"{plan.Name} x{plan.QuantityNeeded}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(headerText);

        if (!string.IsNullOrEmpty(plan.Error))
        {
            var errorText = new TextBlock
            {
                Text = $"Error: {plan.Error}",
                Foreground = Brushes.Red,
                FontSize = 12
            };
            stack.Children.Add(errorText);
            border.Child = stack;
            return border;
        }

        // DC Average price
        var avgText = new TextBlock
        {
            Text = $"DC Average: {plan.DCAveragePrice:N0}g each",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(avgText);

        if (!plan.HasOptions)
        {
            var noListingsText = new TextBlock
            {
                Text = "No viable listings found on market board.",
                Foreground = Brushes.Orange,
                FontSize = 12
            };
            stack.Children.Add(noListingsText);
            border.Child = stack;
            return border;
        }

        // World options
        var worldsHeader = new TextBlock
        {
            Text = "Purchase Options by World:",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 4)
        };
        stack.Children.Add(worldsHeader);

        foreach (var world in plan.WorldOptions)
        {
            var isRecommended = world == plan.RecommendedWorld;
            var worldBorder = CreateWorldOptionPanel(world, isRecommended);
            stack.Children.Add(worldBorder);
        }

        border.Child = stack;
        return border;
    }

    /// <summary>
    /// Create a panel showing purchase options for a specific world.
    /// </summary>
    private Border CreateWorldOptionPanel(WorldShoppingSummary world, bool isRecommended)
    {
        var backgroundColor = isRecommended ? "#2d4a3e" : "#2d2d2d";
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 2, 0, 4),
            BorderBrush = isRecommended ? Brushes.Gold : null,
            BorderThickness = isRecommended ? new Thickness(1) : new Thickness(0)
        };

        var stack = new StackPanel();

        // World name and total
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        
        var worldText = new TextBlock
        {
            Text = world.WorldName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0)
        };
        headerPanel.Children.Add(worldText);

        if (isRecommended)
        {
            var recText = new TextBlock
            {
                Text = "[RECOMMENDED]",
                Foreground = Brushes.Gold,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(recText);
        }

        stack.Children.Add(headerPanel);

        // Total cost and average price
        var costText = new TextBlock
        {
            Text = $"Total: {world.CostDisplay} (~{world.PricePerUnitDisplay} each)",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 4)
        };
        if (world.IsFullyUnderAverage)
        {
            costText.Foreground = Brushes.LightGreen;
        }
        stack.Children.Add(costText);

        // Individual listings
        var listingsText = new TextBlock
        {
            Text = "Listings:",
            FontSize = 10,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 2)
        };
        stack.Children.Add(listingsText);

        foreach (var listing in world.Listings)
        {
            var listingPanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            var qtyText = new TextBlock
            {
                Text = $"x{listing.Quantity}",
                FontSize = 10,
                Width = 35,
                Foreground = Brushes.LightGray
            };
            listingPanel.Children.Add(qtyText);

            var priceText = new TextBlock
            {
                Text = $"@{listing.PricePerUnit:N0}g",
                FontSize = 10,
                Width = 70,
                Foreground = listing.IsUnderAverage ? Brushes.LightGreen : Brushes.White
            };
            listingPanel.Children.Add(priceText);

            var subtotalText = new TextBlock
            {
                Text = $"= {listing.SubtotalDisplay}",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = 80
            };
            listingPanel.Children.Add(subtotalText);

            var retainerText = new TextBlock
            {
                Text = $"({listing.RetainerName})",
                FontSize = 10,
                Foreground = Brushes.Gray,
                FontStyle = FontStyles.Italic
            };
            listingPanel.Children.Add(retainerText);

            stack.Children.Add(listingPanel);
        }

        border.Child = stack;
        return border;
    }

    /// <summary>
    /// Create a card for the Market Logistics tab.
    /// </summary>
    private Border CreateMarketCard(string title, string content, string backgroundColor)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var stack = new StackPanel();
        
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var contentBlock = new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas")
        };
        
        stack.Children.Add(titleBlock);
        stack.Children.Add(contentBlock);
        border.Child = stack;
        
        return border;
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
