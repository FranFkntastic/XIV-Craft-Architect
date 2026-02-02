using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<MainWindow> _logger;

    // Search state
    private List<GarlandSearchResult> _currentSearchResults = new();
    private GarlandSearchResult? _selectedSearchResult;
    
    // Project state
    private List<ProjectItem> _projectItems = new();
    
    // Current crafting plan
    private CraftingPlan? _currentPlan;
    
    // Current market shopping plans for filtering/sorting
    private List<DetailedShoppingPlan> _currentMarketPlans = new();

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
        _logger = App.Services.GetRequiredService<ILogger<MainWindow>>();

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
            
            // Auto-fetch prices if enabled in settings
            if (_settingsService.Get<bool>("market.auto_fetch_prices", true))
            {
                StatusLabel.Text += " - Auto-fetching prices...";
                await Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(100); // Let UI update
                    OnFetchPrices(sender, e);
                });
            }
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
    private void DisplayPlanInTreeView(CraftingPlan plan)
    {
        RecipePlanPanel.Children.Clear();
        
        foreach (var rootItem in plan.RootItems)
        {
            var rootExpander = CreateRecipeExpander(rootItem, 0);
            RecipePlanPanel.Children.Add(rootExpander);
        }
    }
    
    /// <summary>
    /// Creates an Expander-based recipe card with proper styling.
    /// </summary>
    private Expander CreateRecipeExpander(PlanNode node, int depth)
    {
        var headerPanel = CreateNodeHeaderPanel(node);
        
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525"));
        
        var expander = new Expander
        {
            Header = headerPanel,
            IsExpanded = true,
            Background = depth == 0 ? brush : Brushes.Transparent,
            Padding = new Thickness(depth == 0 ? 8 : 0),
            Margin = depth == 0 ? new Thickness(0, 0, 0, 8) : new Thickness(0),
            Tag = node
        };
        
        // Style the Expander header
        expander.Resources["ExpanderHeaderStyle"] = CreateExpanderHeaderStyle();
        
        // Add children if any
        if (node.Children.Count > 0)
        {
            var childrenPanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
            foreach (var child in node.Children)
            {
                childrenPanel.Children.Add(CreateRecipeExpander(child, depth + 1));
            }
            expander.Content = childrenPanel;
        }
        
        return expander;
    }
    
    /// <summary>
    /// Creates the header panel for a recipe node.
    /// </summary>
    private StackPanel CreateNodeHeaderPanel(PlanNode node)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Icon
        var iconBlock = new TextBlock
        {
            Text = GetNodeIcon(node),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        panel.Children.Add(iconBlock);
        
        // Name
        var nameBlock = new TextBlock
        {
            Text = node.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetNodeForeground(node)
        };
        panel.Children.Add(nameBlock);
        
        // Quantity
        var qtyBlock = new TextBlock
        {
            Text = $" ×{node.Quantity}",
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(qtyBlock);
        
        // HQ indicator
        if (node.RequiresHq)
        {
            var hqBlock = new TextBlock
            {
                Text = " [HQ]",
                Foreground = Brushes.Gold,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            panel.Children.Add(hqBlock);
        }
        
        // Recipe info (job, level, yield)
        if (!node.IsUncraftable && !string.IsNullOrEmpty(node.Job) && node.Source == AcquisitionSource.Craft)
        {
            var infoBlock = new TextBlock
            {
                Text = $"  ({node.Job} Lv.{node.RecipeLevel}, Yield: {node.Yield})",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            panel.Children.Add(infoBlock);
        }
        
        // Cost info
        var costText = GetNodeCostText(node);
        if (!string.IsNullOrEmpty(costText))
        {
            var costBlock = new TextBlock
            {
                Text = $"  {costText}",
                Foreground = Brushes.Gold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            panel.Children.Add(costBlock);
        }
        
        return panel;
    }
    
    /// <summary>
    /// Gets the appropriate icon for a node's acquisition source.
    /// </summary>
    private string GetNodeIcon(PlanNode node) => node.Source switch
    {
        AcquisitionSource.Craft => "⚒",
        AcquisitionSource.MarketBuyNq => "🛒",
        AcquisitionSource.MarketBuyHq => "🛒",
        AcquisitionSource.VendorBuy => "🏪",
        _ => "•"
    };
    
    /// <summary>
    /// Gets the foreground color based on acquisition source.
    /// </summary>
    private Brush GetNodeForeground(PlanNode node) => node.Source switch
    {
        AcquisitionSource.Craft => Brushes.White,
        AcquisitionSource.MarketBuyNq => Brushes.LightSkyBlue,
        AcquisitionSource.MarketBuyHq => Brushes.LightGreen,
        AcquisitionSource.VendorBuy => Brushes.LightYellow,
        _ => Brushes.LightGray
    };
    
    /// <summary>
    /// Creates the style for Expander headers.
    /// </summary>
    private Style CreateExpanderHeaderStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.ToggleButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        return style;
    }
    
    /// <summary>
    /// Display plan and update market logistics using saved prices (for plan loading).
    /// </summary>
    private async Task DisplayPlanWithCachedPrices(CraftingPlan plan)
    {
        DisplayPlanInTreeView(plan);
        
        // Restore saved market plans if available
        if (plan.SavedMarketPlans?.Count > 0 == true)
        {
            _currentMarketPlans = plan.SavedMarketPlans;
            ApplyMarketSortAndDisplay();
            
            // Also restore prices from plan nodes
            var savedPrices = ExtractPricesFromPlan(plan);
            if (savedPrices.Count > 0)
            {
                StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices and {_currentMarketPlans.Count} market items. Click 'Refresh Market Data' for current listings.";
            }
            else
            {
                StatusLabel.Text = $"Loaded plan with {_currentMarketPlans.Count} market items. Click 'Refresh Market Data' for current listings.";
            }
        }
        else if (ExtractPricesFromPlan(plan).Count > 0)
        {
            // Have prices but no detailed market plans - show basic market logistics
            var savedPrices = ExtractPricesFromPlan(plan);
            await UpdateMarketLogisticsAsync(savedPrices, useCachedData: true);
            StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices. Click 'Refresh Market Data' for detailed listings.";
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
        // Header panel with name, quantity, and source dropdown
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Acquisition source dropdown with costs
        var sourceCombo = new ComboBox
        {
            Width = 180,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // Calculate costs for each option
        var craftCost = CalculateNodeCraftCost(node);
        var nqCost = node.MarketPrice * node.Quantity;
        var hqCost = node.MarketPrice * 1.5m * node.Quantity; // Estimate HQ at 1.5x NQ if no HQ price
        
        // Add options with costs
        sourceCombo.Items.Add(new ComboBoxItem { 
            Content = $"Craft ({craftCost:N0}g)", 
            Tag = AcquisitionSource.Craft 
        });
        sourceCombo.Items.Add(new ComboBoxItem { 
            Content = $"Buy NQ ({nqCost:N0}g)", 
            Tag = AcquisitionSource.MarketBuyNq 
        });
        sourceCombo.Items.Add(new ComboBoxItem { 
            Content = $"Buy HQ ({hqCost:N0}g)", 
            Tag = AcquisitionSource.MarketBuyHq 
        });
        
        // Select current value
        foreach (ComboBoxItem item in sourceCombo.Items)
        {
            if ((AcquisitionSource)item.Tag == node.Source)
            {
                sourceCombo.SelectedItem = item;
                break;
            }
        }
        
        // If not found, default to first item
        if (sourceCombo.SelectedItem == null)
            sourceCombo.SelectedIndex = 0;
        
        sourceCombo.SelectionChanged += (s, e) => 
        {
            if (sourceCombo.SelectedItem is ComboBoxItem selected)
            {
                var newSource = (AcquisitionSource)selected.Tag;
                SetNodeAcquisitionSource(node, newSource);
            }
        };
        
        panel.Children.Add(sourceCombo);
        
        // Item name and details
        var nameText = new TextBlock 
        { 
            Text = $"{node.Name} x{node.Quantity}",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = node.Parent == null ? FontWeights.Bold : FontWeights.Normal
        };
        panel.Children.Add(nameText);
        
        // Add HQ indicator if buying HQ
        if (node.Source == AcquisitionSource.MarketBuyHq)
        {
            var hqIndicator = new TextBlock
            {
                Text = " [HQ]",
                Foreground = System.Windows.Media.Brushes.Gold,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            panel.Children.Add(hqIndicator);
        }
        
        // Add recipe info if craftable
        if (!node.IsUncraftable && !string.IsNullOrEmpty(node.Job) && node.Source == AcquisitionSource.Craft)
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
    /// Set the acquisition source for a node.
    /// </summary>
    private void SetNodeAcquisitionSource(PlanNode node, AcquisitionSource source)
    {
        _recipeCalcService.SetAcquisitionSource(node, source);
        
        // Refresh the display
        if (_currentPlan != null)
        {
            DisplayPlanInTreeView(_currentPlan);
        }
        
        var sourceText = source switch
        {
            AcquisitionSource.MarketBuyNq => "buy NQ",
            AcquisitionSource.MarketBuyHq => "buy HQ",
            AcquisitionSource.VendorBuy => "buy from vendor",
            _ => "craft"
        };
        
        StatusLabel.Text = $"{node.Name} set to {sourceText}";
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
                await DisplayPlanWithCachedPrices(_currentPlan);
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
    /// Export current plan to Artisan JSON format (copies to clipboard).
    /// Artisan is a Dalamud plugin for FFXIV crafting automation.
    /// </summary>
    private async void OnExportArtisan(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan to export - build a plan first";
            return;
        }

        StatusLabel.Text = "Exporting to Artisan format...";
        
        try
        {
            var artisan = App.Services.GetRequiredService<ArtisanService>();
            var result = await artisan.ExportToArtisanAsync(_currentPlan);
            
            if (await TrySetClipboardAsync(result.Json))
            {
                if (result.Success)
                {
                    StatusLabel.Text = $"Artisan export complete! {result.RecipeCount} recipes copied to clipboard.";
                }
                else
                {
                    var summary = artisan.CreateExportSummary(result);
                    StatusLabel.Text = summary;
                }
            }
            else
            {
                StatusLabel.Text = "Failed to copy - clipboard may be in use.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export to Artisan format");
            StatusLabel.Text = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Import a crafting list from Artisan JSON format.
    /// Expects the user to paste JSON exported from Artisan.
    /// </summary>
    private async void OnImportArtisan(object sender, RoutedEventArgs e)
    {
        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        
        // Get clipboard content
        string clipboardText;
        try
        {
            clipboardText = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Failed to read clipboard. Please try again.";
            _logger.LogError(ex, "Failed to read clipboard for Artisan import");
            return;
        }

        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            StatusLabel.Text = "Clipboard is empty. Copy an Artisan export first.";
            return;
        }

        StatusLabel.Text = "Importing from Artisan...";
        
        try
        {
            var artisan = App.Services.GetRequiredService<ArtisanService>();
            var plan = await artisan.ImportFromArtisanAsync(clipboardText, dc, world);
            
            if (plan != null)
            {
                _currentPlan = plan;
                
                // Update project items display
                _projectItems.Clear();
                foreach (var item in plan.RootItems)
                {
                    _projectItems.Add(new ProjectItem 
                    { 
                        Id = item.ItemId, 
                        Name = item.Name, 
                        Quantity = item.Quantity 
                    });
                }
                
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _projectItems;
                
                // Display the recipe tree
                DisplayPlanInTreeView(_currentPlan);
                
                // Enable buttons
                FetchPricesButton.IsEnabled = true;
                BuildPlanButton.IsEnabled = _projectItems.Count > 0;
                
                StatusLabel.Text = $"Imported plan with {_currentPlan.RootItems.Count} items from Artisan";
            }
            else
            {
                StatusLabel.Text = "Failed to import - invalid Artisan format or no recipes found.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from Artisan format");
            StatusLabel.Text = $"Import failed: {ex.Message}";
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

            // Refresh tree view (market logistics updated below)
            DisplayPlanInTreeView(_currentPlan);
            
            // Update market logistics tab with fresh market data
            await UpdateMarketLogisticsAsync(prices, useCachedData: false);

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
        MarketSummaryCard.Visibility = System.Windows.Visibility.Collapsed;
        RefreshMarketButton.IsEnabled = false;
        
        var placeholderCard = CreateMarketCard("Market Logistics", 
            "Click 'Fetch Prices' to see your purchase plan.\n\n" +
            "This tab will show:\n" +
            "• Items to buy from vendors (cheapest option)\n" +
            "• Items to buy from market board (with world listings)\n" +
            "• Cross-DC travel options (NA only)\n" +
            "• Untradeable items you need to gather/craft", "#2d3d4a");
        MarketCards.Children.Add(placeholderCard);
    }

    /// <summary>
    /// Populate the Market Logistics tab with detailed purchase planning information.
    /// </summary>
    /// <param name="prices">Price dictionary from cache or saved plan.</param>
    /// <param name="useCachedData">If true, use saved prices without re-fetching market listings.</param>
    private async Task UpdateMarketLogisticsAsync(Dictionary<int, PriceInfo> prices, bool useCachedData = false)
    {
        _currentMarketPlans.Clear();
        MarketCards.Children.Clear();
        
        // Clear and hide Craft vs Buy analysis
        CraftVsBuyContent.Children.Clear();
        CraftVsBuyExpander.Visibility = Visibility.Collapsed;

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

        // Update summary card
        UpdateMarketSummaryCard(vendorItems, marketItems, untradeableItems, prices);

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

        // Market items - use cached data or fetch fresh
        if (marketItems.Any())
        {
            if (useCachedData)
            {
                // Use saved prices without re-fetching detailed listings
                var cachedCard = CreateMarketCard($"Market Board Items ({marketItems.Count})",
                    "Using saved prices. Click 'Refresh Market Data' to fetch current listings.\n\n" +
                    "Items to purchase:\n" +
                    string.Join("\n", marketItems.Select(m => 
                        $"• {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g ({prices[m.ItemId].SourceDetails})")),
                    "#3d3e2d");
                MarketCards.Children.Add(cachedCard);
                RefreshMarketButton.IsEnabled = true;
                
                // Still show Craft vs Buy analysis even with cached data
                AddCraftVsBuyAnalysisCard(prices);
            }
            else
            {
                // Fetch fresh market data
                var dc = DcCombo.SelectedItem as string ?? "Aether";
                var searchAllNA = SearchAllNACheck?.IsChecked ?? false;
                
                // Show loading message
                var loadingCard = CreateMarketCard("Market Board Items", 
                    $"Fetching detailed listings for {marketItems.Count} items from {(searchAllNA ? "all NA DCs" : dc)}...", "#3d3e2d");
                MarketCards.Children.Add(loadingCard);
                RefreshMarketButton.IsEnabled = false;
                
                try
                {
                    var progress = new Progress<string>(msg => 
                    {
                        StatusLabel.Text = $"Analyzing market: {msg}";
                    });
                    
                    List<DetailedShoppingPlan> shoppingPlans;
                    var mode = GetCurrentRecommendationMode();
                    
                    if (searchAllNA)
                    {
                        shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                            marketItems, progress, mode: mode);
                    }
                    else
                    {
                        shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                            marketItems, dc, progress, mode: mode);
                    }
                    
                    _currentMarketPlans = shoppingPlans;
                    
                    // Save market plans to the current plan for persistence
                    if (_currentPlan != null)
                    {
                        _currentPlan.SavedMarketPlans = shoppingPlans;
                    }
                    
                    // Remove loading card
                    MarketCards.Children.Remove(loadingCard);
                    
                    // Apply current sort and display
                    ApplyMarketSortAndDisplay();
                }
                catch (Exception ex)
                {
                    MarketCards.Children.Remove(loadingCard);
                    var errorCard = CreateMarketCard("Market Board Items", 
                        $"Error fetching listings: {ex.Message}", "#4a2d2d");
                    MarketCards.Children.Add(errorCard);
                }
                finally
                {
                    RefreshMarketButton.IsEnabled = true;
                }
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
        
        // Craft vs Buy Analysis
        AddCraftVsBuyAnalysisCard(prices);
    }
    
    /// <summary>
    /// Add craft-vs-buy analysis to the collapsible Expander with HQ/NQ quality considerations.
    /// </summary>
    private void AddCraftVsBuyAnalysisCard(Dictionary<int, PriceInfo> prices)
    {
        if (_currentPlan == null) return;
        
        try
        {
            var analyses = _marketShoppingService.AnalyzeCraftVsBuy(_currentPlan, prices);
            var significantAnalyses = analyses.Where(a => a.IsSignificantSavings).ToList();
            
            // Clear previous content
            CraftVsBuyContent.Children.Clear();
            
            if (significantAnalyses.Count == 0)
            {
                CraftVsBuyExpander.Visibility = Visibility.Collapsed;
                return;
            }
            
            CraftVsBuyExpander.Visibility = Visibility.Visible;
            
            // Update header summary
            var craftCount = significantAnalyses.Count(a => a.RecommendationNq == CraftRecommendation.Craft);
            var buyCount = significantAnalyses.Count(a => a.RecommendationNq == CraftRecommendation.Buy);
            CraftVsBuySummaryText.Text = $"{significantAnalyses.Count} items: {craftCount} craft, {buyCount} buy";
            
            // Add warning about HQ for endgame crafting
            var hqWarning = new TextBlock
            {
                Text = "⚠️ For endgame HQ crafts, NQ components may compromise quality. Check HQ prices below.",
                Foreground = Brushes.Orange,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            CraftVsBuyContent.Children.Add(hqWarning);
            
            // Show top analyses - simple format: "Crafting saves Xg" or "Crafting costs Xg more"
            foreach (var analysis in significantAnalyses.Take(8))
            {
                var itemPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                
                // Item name and quantity
                var nameBlock = new TextBlock
                {
                    Text = $"{analysis.ItemName} x{analysis.Quantity}",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = analysis.HasQualityWarning ? Brushes.Orange : Brushes.White
                };
                itemPanel.Children.Add(nameBlock);
                
                // NQ Analysis: simple "Crafting saves Xg" or "Crafting costs Xg more"
                string nqText;
                Brush nqColor;
                if (analysis.PotentialSavingsNq > 0)
                {
                    // Buy price > Craft cost = crafting saves money (green = good to craft)
                    nqText = $"  Crafting saves {analysis.PotentialSavingsNq:N0}g ({analysis.SavingsPercentNq:F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                    nqColor = Brushes.LightGreen;
                }
                else
                {
                    // Buy price < Craft cost = crafting costs more (red = better to buy)
                    nqText = $"  Crafting costs {Math.Abs(analysis.PotentialSavingsNq):N0}g more ({Math.Abs(analysis.SavingsPercentNq):F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                    nqColor = Brushes.LightCoral;
                }
                
                var nqBlock = new TextBlock
                {
                    Text = nqText,
                    Foreground = nqColor,
                    FontSize = 11
                };
                itemPanel.Children.Add(nqBlock);
                
                // HQ Analysis (if available)
                if (analysis.HasHqData)
                {
                    string hqText;
                    Brush hqColor;
                    if (analysis.PotentialSavingsHq > 0)
                    {
                        hqText = $"  HQ: Crafting saves {analysis.PotentialSavingsHq:N0}g ({analysis.SavingsPercentHq:F0}%) - Buy: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        hqColor = Brushes.LightGreen;
                    }
                    else
                    {
                        hqText = $"  HQ: Crafting costs {Math.Abs(analysis.PotentialSavingsHq):N0}g more ({Math.Abs(analysis.SavingsPercentHq):F0}%) - Buy: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        hqColor = Brushes.LightCoral;
                    }
                    
                    var hqBlock = new TextBlock
                    {
                        Text = hqText,
                        Foreground = hqColor,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic
                    };
                    itemPanel.Children.Add(hqBlock);
                    
                    // Warning if NQ looks good but HQ is expensive (endgame relevant)
                    if (analysis.IsEndgameRelevant)
                    {
                        var warningBlock = new TextBlock
                        {
                            Text = "  ⚠️ NQ looks cheap but HQ is costly - may affect HQ craft success",
                            Foreground = Brushes.Orange,
                            FontSize = 10,
                            Margin = new Thickness(0, 2, 0, 0)
                        };
                        itemPanel.Children.Add(warningBlock);
                    }
                }
                
                CraftVsBuyContent.Children.Add(itemPanel);
            }
            
            if (significantAnalyses.Count > 8)
            {
                var moreBlock = new TextBlock
                {
                    Text = $"... and {significantAnalyses.Count - 8} more items",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                CraftVsBuyContent.Children.Add(moreBlock);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate craft-vs-buy analysis");
            CraftVsBuyExpander.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Update the summary card with purchase totals.
    /// </summary>
    private void UpdateMarketSummaryCard(List<MaterialAggregate> vendorItems, List<MaterialAggregate> marketItems, 
        List<MaterialAggregate> untradeableItems, Dictionary<int, PriceInfo> prices)
    {
        MarketSummaryContent.Children.Clear();
        MarketSummaryCard.Visibility = System.Windows.Visibility.Visible;
        
        var header = new TextBlock
        {
            Text = "Purchase Summary",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        MarketSummaryContent.Children.Add(header);
        
        var summaryText = new TextBlock
        {
            Text = $"Total Items: {vendorItems.Count + marketItems.Count + untradeableItems.Count}\n\n" +
                   $"Vendor Purchases: {vendorItems.Count} items\n" +
                   $"- Guaranteed prices, no market fluctuations\n" +
                   $"- Total: {vendorItems.Sum(i => i.TotalCost):N0}g\n\n" +
                   $"Market Board Purchases: {marketItems.Count} items\n" +
                   $"- Click items below for detailed listings\n" +
                   $"- Total: {marketItems.Sum(i => i.TotalCost):N0}g\n\n" +
                   $"Grand Total: {vendorItems.Sum(i => i.TotalCost) + marketItems.Sum(i => i.TotalCost):N0}g",
            FontSize = 12
        };
        MarketSummaryContent.Children.Add(summaryText);
    }

    /// <summary>
    /// Apply current sort selection and display market plans.
    /// </summary>
    private void ApplyMarketSortAndDisplay()
    {
        if (_currentMarketPlans.Count == 0) return;
        
        // Remove existing market item cards (keep vendor/untradeable)
        var cardsToRemove = MarketCards.Children.OfType<Border>()
            .Where(b => b.Tag is DetailedShoppingPlan)
            .ToList();
        foreach (var card in cardsToRemove)
        {
            MarketCards.Children.Remove(card);
        }
        
        // Get the index to insert before (before untradeable items if present)
        var insertIndex = MarketCards.Children.Count;
        
        IEnumerable<DetailedShoppingPlan> sortedPlans = _currentMarketPlans;
        var sortIndex = MarketSortCombo?.SelectedIndex ?? 0;
        
        switch (sortIndex)
        {
            case 0: // By Recommended World (default)
                sortedPlans = _currentMarketPlans
                    .OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ")
                    .ThenBy(p => p.Name);
                break;
            case 1: // Alphabetical
                sortedPlans = _currentMarketPlans.OrderBy(p => p.Name);
                break;
        }
        
        foreach (var plan in sortedPlans)
        {
            var itemCard = CreateExpandableMarketCard(plan);
            itemCard.Tag = plan; // Mark as market item card for filtering
            MarketCards.Children.Insert(insertIndex++, itemCard);
        }
    }

    /// <summary>
    /// Create an expandable market card with header showing recommended option.
    /// </summary>
    private Border CreateExpandableMarketCard(DetailedShoppingPlan plan)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3e2d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 8),
            Tag = plan
        };

        var mainStack = new StackPanel();
        
        // Clickable header
        var headerBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a4a3a")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Cursor = Cursors.Hand
        };
        
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        // Left side: Item name, quantity, DC average
        var leftStack = new StackPanel();
        
        var nameText = new TextBlock
        {
            Text = $"{plan.Name} x{plan.QuantityNeeded}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        };
        leftStack.Children.Add(nameText);
        
        if (!string.IsNullOrEmpty(plan.Error))
        {
            var errorText = new TextBlock
            {
                Text = $"Error: {plan.Error}",
                Foreground = Brushes.Red,
                FontSize = 11
            };
            leftStack.Children.Add(errorText);
        }
        else
        {
            var avgText = new TextBlock
            {
                Text = $"DC Avg: {plan.DCAveragePrice:N0}g",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0)
            };
            leftStack.Children.Add(avgText);
        }
        
        Grid.SetColumn(leftStack, 0);
        headerGrid.Children.Add(leftStack);
        
        // Right side: Recommended world info
        var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        
        if (plan.HasOptions && plan.RecommendedWorld != null)
        {
            var recWorld = plan.RecommendedWorld;
            
            var worldText = new TextBlock
            {
                Text = recWorld.WorldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Brushes.Gold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightStack.Children.Add(worldText);
            
            var costText = new TextBlock
            {
                Text = $"{recWorld.CostDisplay} total",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (recWorld.IsFullyUnderAverage)
            {
                costText.Foreground = Brushes.LightGreen;
            }
            rightStack.Children.Add(costText);
            
            var clickHint = new TextBlock
            {
                Text = "Click to expand ▼",
                FontSize = 9,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };
            rightStack.Children.Add(clickHint);
        }
        else if (string.IsNullOrEmpty(plan.Error))
        {
            var noDataText = new TextBlock
            {
                Text = "No viable listings",
                Foreground = Brushes.Orange,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightStack.Children.Add(noDataText);
        }
        
        Grid.SetColumn(rightStack, 1);
        headerGrid.Children.Add(rightStack);
        
        headerBorder.Child = headerGrid;
        mainStack.Children.Add(headerBorder);
        
        // Expandable content (all world options)
        var contentStack = new StackPanel
        {
            Visibility = System.Windows.Visibility.Collapsed,
            Margin = new Thickness(12, 8, 12, 12)
        };
        
        if (plan.HasOptions)
        {
            var optionsHeader = new TextBlock
            {
                Text = "All World Options:",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            };
            contentStack.Children.Add(optionsHeader);
            
            foreach (var world in plan.WorldOptions)
            {
                var isRecommended = world == plan.RecommendedWorld;
                var worldPanel = CreateWorldOptionPanel(world, isRecommended);
                contentStack.Children.Add(worldPanel);
            }
        }
        
        mainStack.Children.Add(contentStack);
        border.Child = mainStack;
        
        // Click handler to expand/collapse
        headerBorder.MouseLeftButtonDown += (s, e) =>
        {
            if (contentStack.Visibility == System.Windows.Visibility.Collapsed)
            {
                contentStack.Visibility = System.Windows.Visibility.Visible;
                headerBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5a5a4a"));
            }
            else
            {
                contentStack.Visibility = System.Windows.Visibility.Collapsed;
                headerBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a4a3a"));
            }
        };
        
        return border;
    }

    /// <summary>
    /// Create a panel showing purchase options for a specific world.
    /// Shows HQ indicators and value metrics.
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
        else if (world.IsCompetitive)
        {
            var valueText = new TextBlock
            {
                Text = "[GOOD VALUE]",
                Foreground = Brushes.LightGreen,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(valueText);
        }

        stack.Children.Add(headerPanel);

        // Total cost with excess info and value score
        var costText = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 4)
        };
        if (world.HasExcess)
        {
            costText.Text = $"Total: {world.CostDisplay} (buy {world.TotalQuantityPurchased}, use {world.Listings.Where(l => !l.IsAdditionalOption).Sum(l => l.NeededFromStack)}, +{world.ExcessQuantity} extra)";
            costText.ToolTip = "FFXIV requires buying full stacks. You'll have excess items.";
        }
        else
        {
            costText.Text = $"Total: {world.CostDisplay} (~{world.PricePerUnitDisplay} each)";
        }
        if (world.IsFullyUnderAverage)
        {
            costText.Foreground = Brushes.LightGreen;
        }
        stack.Children.Add(costText);
        
        // Show best unit price if competitive
        if (world.IsCompetitive && world.BestSingleListing != null)
        {
            var valueText = new TextBlock
            {
                Text = $"Best listing: {world.BestSingleListing.PricePerUnit:N0}g each (x{world.BestSingleListing.Quantity}){(world.BestSingleListing.IsHq ? " [HQ]" : "")}",
                FontSize = 10,
                Foreground = Brushes.Gold,
                Margin = new Thickness(0, -2, 0, 4)
            };
            stack.Children.Add(valueText);
        }

        // Individual listings
        var listingsText = new TextBlock
        {
            Text = "Listings (full stacks required):",
            FontSize = 10,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 2)
        };
        stack.Children.Add(listingsText);

        foreach (var listing in world.Listings)
        {
            var listingPanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            // Show full stack info with what we need vs excess
            var qtyText = new TextBlock
            {
                FontSize = 10,
                Width = 140,
                Foreground = listing.IsAdditionalOption 
                    ? Brushes.DarkGray 
                    : (listing.ExcessQuantity > 0 ? Brushes.Orange : Brushes.LightGray),
                FontStyle = listing.IsAdditionalOption ? FontStyles.Italic : FontStyles.Normal
            };
            if (listing.IsAdditionalOption)
            {
                qtyText.Text = $"x{listing.Quantity} (extra)";
                qtyText.ToolTip = "Additional option - not needed for quantity, but good value";
            }
            else if (listing.ExcessQuantity > 0)
            {
                qtyText.Text = $"x{listing.Quantity} (use {listing.NeededFromStack})";
                qtyText.ToolTip = $"Must buy full stack of {listing.Quantity}, only need {listing.NeededFromStack}";
            }
            else
            {
                qtyText.Text = $"x{listing.Quantity}";
            }
            listingPanel.Children.Add(qtyText);

            var priceText = new TextBlock
            {
                Text = $"@{listing.PricePerUnit:N0}g",
                FontSize = 10,
                Width = 70,
                Foreground = listing.IsUnderAverage ? Brushes.LightGreen : Brushes.White,
                FontWeight = listing.IsHq ? FontWeights.Bold : FontWeights.Normal
            };
            listingPanel.Children.Add(priceText);

            var subtotalText = new TextBlock
            {
                Text = $"= {listing.SubtotalDisplay}",
                FontSize = 10,
                Foreground = listing.IsAdditionalOption ? Brushes.DarkGray : Brushes.Gray,
                Width = 80
            };
            listingPanel.Children.Add(subtotalText);

            // HQ indicator and retainer name
            var retainerText = new TextBlock
            {
                FontSize = 10,
                Foreground = listing.IsAdditionalOption ? Brushes.DarkGray : Brushes.Gray,
                FontStyle = FontStyles.Italic
            };
            if (listing.IsHq)
            {
                retainerText.Text = $"[HQ] {listing.RetainerName}";
                retainerText.Foreground = Brushes.Gold;
            }
            else
            {
                retainerText.Text = $"({listing.RetainerName})";
            }
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

    /// <summary>
    /// Handle sort selection change in Market Logistics.
    /// </summary>
    private void OnMarketSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyMarketSortAndDisplay();
    }
    
    /// <summary>
    /// Handle recommendation mode change in Market Logistics.
    /// </summary>
    private async void OnMarketModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_currentPlan == null || _currentMarketPlans.Count == 0) return;
        
        // Re-apply current sort which will use the new mode
        ApplyMarketSortAndDisplay();
    }
    
    /// <summary>
    /// Get the current recommendation mode from the combo box.
    /// </summary>
    private RecommendationMode GetCurrentRecommendationMode()
    {
        return MarketModeCombo.SelectedIndex switch
        {
            1 => RecommendationMode.MaximizeValue,
            _ => RecommendationMode.MinimizeTotalCost
        };
    }

    /// <summary>
    /// Refresh market data with current settings.
    /// </summary>
    private async void OnRefreshMarketData(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        // Re-fetch prices which will update market logistics
        OnFetchPrices(sender, e);
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

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var optionsWindow = App.Services.GetRequiredService<OptionsWindow>();
        optionsWindow.Owner = this;
        optionsWindow.ShowDialog();
    }
    
    /// <summary>
    /// Switch to Recipe Plan tab
    /// </summary>
    private void OnRecipePlanTabClick(object sender, MouseButtonEventArgs e)
    {
        RecipePlanTab.Background = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        ((TextBlock)RecipePlanTab.Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a1a"));
        
        MarketLogisticsTab.Background = Brushes.Transparent;
        ((TextBlock)((Border)MarketLogisticsTab).Child).Foreground = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        
        RecipePlanContent.Visibility = Visibility.Visible;
        MarketLogisticsContent.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Switch to Market Logistics tab
    /// </summary>
    private void OnMarketLogisticsTabClick(object sender, MouseButtonEventArgs e)
    {
        RecipePlanTab.Background = Brushes.Transparent;
        ((TextBlock)RecipePlanTab.Child).Foreground = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        
        MarketLogisticsTab.Background = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        ((TextBlock)((Border)MarketLogisticsTab).Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a1a"));
        
        RecipePlanContent.Visibility = Visibility.Collapsed;
        MarketLogisticsContent.Visibility = Visibility.Visible;
    }

    private void OnNewPlan(object sender, RoutedEventArgs e)
    {
        // Clear current plan and project items
        _currentPlan = null;
        _projectItems.Clear();
        ProjectList.ItemsSource = null;
        RecipePlanPanel?.Children.Clear();
        
        // Reset UI state
        BuildPlanButton.IsEnabled = false;
        FetchPricesButton.IsEnabled = false;
        
        StatusLabel.Text = "New plan created. Add items to get started.";
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
        // If buying from market (NQ or HQ)
        if (node.Source == AcquisitionSource.MarketBuyNq || node.Source == AcquisitionSource.MarketBuyHq)
        {
            if (node.MarketPrice > 0)
            {
                var multiplier = node.Source == AcquisitionSource.MarketBuyHq ? 1.5m : 1m;
                return $"~{node.MarketPrice * multiplier * node.Quantity:N0}g";
            }
            else
            {
                return "(market price needed)";
            }
        }
        // If buying from vendor
        else if (node.Source == AcquisitionSource.VendorBuy)
        {
            if (node.MarketPrice > 0)
            {
                return $"~{node.MarketPrice * node.Quantity:N0}g (vendor)";
            }
        }
        // If crafting
        else if (node.Children.Any())
        {
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
    /// Calculate the cost to craft this node (sum of all component costs).
    /// </summary>
    private decimal CalculateNodeCraftCost(PlanNode node)
    {
        if (!node.Children.Any())
            return 0;
        
        decimal total = 0;
        foreach (var child in node.Children)
        {
            if (child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
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

    private void TabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

    }
}
