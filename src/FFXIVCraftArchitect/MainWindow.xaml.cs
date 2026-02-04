using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Helpers;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FFXIVCraftArchitect.Services.PriceCheckService;
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
    public bool IsHqRequired { get; set; } = false;
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
    
    // Market data status window for real-time fetch visualization
    private MarketDataStatusWindow? _marketDataStatusWindow;

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
        
        // Apply default recommendation mode setting
        var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
        MarketModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;
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
            
            // Show the search results panel
            SearchResultsPanel.Visibility = Visibility.Visible;
            
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

        // Refresh project list (limited to first 5 for quick view)
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _projectItems.Take(5).ToList();
        UpdateQuickViewCount();
        
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
            var targets = _projectItems.Select(p => (p.Id, p.Name, p.Quantity, p.IsHqRequired)).ToList();
            _currentPlan = await _recipeCalcService.BuildPlanAsync(targets, dc, world);
            
            // Display in TreeView
            DisplayPlanInTreeView(_currentPlan);
            UpdateBuildPlanButtonText();
            
            // Populate shopping list
            PopulateShoppingList();
            
            // Enable procurement refresh button
            ProcurementRefreshButton.IsEnabled = true;
            
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
    /// Populates the shopping list panel with aggregated materials.
    /// </summary>
    private void PopulateShoppingList()
    {
        if (_currentPlan?.AggregatedMaterials == null || ShoppingListPanel == null)
            return;

        ShoppingListPanel.Children.Clear();
        decimal totalCost = 0;

        foreach (var material in _currentPlan.AggregatedMaterials.OrderBy(m => m.Name))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            // Item name
            var nameBlock = new TextBlock
            {
                Text = material.Name,
                Foreground = Brushes.White,
                Padding = new Thickness(12, 6, 4, 6),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 0);
            row.Children.Add(nameBlock);

            // Quantity
            var qtyBlock = new TextBlock
            {
                Text = material.TotalQuantity.ToString(),
                Foreground = Brushes.LightGray,
                Padding = new Thickness(4, 6, 4, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(qtyBlock, 1);
            row.Children.Add(qtyBlock);

            // Price (if available)
            var price = material.UnitPrice > 0 ? material.UnitPrice * material.TotalQuantity : 0;
            totalCost += price;
            var priceText = price > 0 ? $"{price:N0}g" : "-";
            var priceBlock = new TextBlock
            {
                Text = priceText,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
                Padding = new Thickness(4, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(priceBlock, 2);
            row.Children.Add(priceBlock);

            // Alternate row background
            if (ShoppingListPanel.Children.Count % 2 == 1)
            {
                row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }

            ShoppingListPanel.Children.Add(row);
        }

        // Update total cost
        if (TotalCostText != null)
        {
            TotalCostText.Text = $"{totalCost:N0}g";
        }
    }

    private void OnProjectItemSelected(object sender, SelectionChangedEventArgs e)
    {
        // Handle project item selection if needed
    }

    /// <summary>
    /// Update the Build Plan button text based on whether a plan exists.
    /// </summary>
    private void UpdateBuildPlanButtonText()
    {
        var hasPlan = _currentPlan != null && _currentPlan.RootItems.Count > 0;
        BuildPlanButton.Content = hasPlan ? "Rebuild Project Plan" : "Build Project Plan";
    }

    /// <summary>
    /// Display the crafting plan in the TreeView with craft/buy toggles.
    /// </summary>
    private void DisplayPlanInTreeView(CraftingPlan plan)
    {
        // Preserve scroll position to prevent jarring jumps when toggling HQ
        var scrollViewer = RecipePlanPanel.FindParent<ScrollViewer>();
        var scrollOffset = scrollViewer?.VerticalOffset ?? 0;
        
        RecipePlanPanel.Children.Clear();
        
        foreach (var rootItem in plan.RootItems)
        {
            var rootExpander = CreateRecipeExpander(rootItem, 0);
            RecipePlanPanel.Children.Add(rootExpander);
        }
        
        // Restore scroll position after layout is updated
        if (scrollViewer != null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                scrollViewer.ScrollToVerticalOffset(scrollOffset);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
    

    
    /// <summary>
    /// Creates an Expander-based recipe card with proper styling.
    /// Parent nodes (with children) get an expand arrow to show/hide subcrafts.
    /// Leaf nodes (no children) show an acquisition source dropdown instead.
    /// </summary>
    private UIElement CreateRecipeExpander(PlanNode node, int depth)
    {
        // Leaf node: Show simple panel with acquisition dropdown on the right
        if (node.Children.Count == 0)
        {
            return CreateLeafNodePanel(node, depth);
        }
        
        // Parent node: Create Expander with arrow for subcrafts AND dropdown on the right
        // This allows buying complex crafted items (like submarine parts) instead of crafting
        var headerPanel = CreateNodeHeaderPanel(node, showDropdown: true);
        
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
        
        // Add children
        var childrenPanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
        foreach (var child in node.Children)
        {
            childrenPanel.Children.Add(CreateRecipeExpander(child, depth + 1));
        }
        expander.Content = childrenPanel;
        
        return expander;
    }
    
    /// <summary>
    /// Creates a simple panel for leaf nodes (no children) with acquisition dropdown on the right.
    /// HQ items show a clickable star toggle (outline when off, gold when on) - only for HQ-capable items.
    /// </summary>
    private StackPanel CreateLeafNodePanel(PlanNode node, int depth)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Add spacing for alignment (where expand arrow would be on parent nodes)
        panel.Margin = new Thickness(20, 0, 0, 0);
        
        // Name - declare early so star click handler can reference it
        var nameBlock = new TextBlock
        {
            Text = node.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = node.MustBeHq ? ColorHelper.GetAccentBrush() : GetNodeForeground(node)
        };
        
        // HQ Star toggle (clickable) - only for items that can be HQ
        if (node.CanBeHq)
        {
            var starBlock = new TextBlock
            {
                Text = node.MustBeHq ? "★" : "☆",
                Foreground = node.MustBeHq ? Brushes.Gold : Brushes.Gray,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = node.MustBeHq ? "Click to allow NQ" : "Click to require HQ",
                Margin = new Thickness(0, 0, 4, 0)
            };
            starBlock.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                
                // Optimistic UI: toggle visual state immediately
                var newState = !node.MustBeHq;
                starBlock.Text = newState ? "★" : "☆";
                starBlock.Foreground = newState ? Brushes.Gold : Brushes.Gray;
                starBlock.ToolTip = newState ? "Click to allow NQ" : "Click to require HQ";
                
                // Update name color immediately too
                nameBlock.Foreground = newState ? ColorHelper.GetAccentBrush() : GetNodeForeground(node);
                
                // Then apply the actual change (which will refresh the tree)
                try
                {
                    ToggleNodeHqRequirement(node);
                }
                catch (Exception ex)
                {
                    // Revert on error
                    starBlock.Text = node.MustBeHq ? "★" : "☆";
                    starBlock.Foreground = node.MustBeHq ? Brushes.Gold : Brushes.Gray;
                    nameBlock.Foreground = node.MustBeHq ? ColorHelper.GetAccentBrush() : GetNodeForeground(node);
                    StatusLabel.Text = $"Failed to toggle HQ: {ex.Message}";
                }
            };
            panel.Children.Add(starBlock);
        }
        
        // Add name block after star
        panel.Children.Add(nameBlock);
        
        // Quantity
        var qtyBlock = new TextBlock
        {
            Text = $" ×{node.Quantity}",
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(qtyBlock);
        
        // Price estimate display
        var priceText = GetNodePriceDisplay(node);
        if (!string.IsNullOrEmpty(priceText))
        {
            var priceBlock = new TextBlock
            {
                Text = priceText,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                FontSize = 11
            };
            panel.Children.Add(priceBlock);
        }
        
        // Acquisition source dropdown (MOVED TO RIGHT SIDE)
        var sourceCombo = CreateAcquisitionSourceDropdown(node);
        if (sourceCombo != null)
        {
            panel.Children.Add(sourceCombo);
        }
        
        return panel;
    }
    
    /// <summary>
    /// Creates the header panel for a parent recipe node (with children).
    /// Parent nodes use an expand arrow. HQ items show a clickable star toggle - only for HQ-capable items.
    /// </summary>
    private StackPanel CreateNodeHeaderPanel(PlanNode node, bool showDropdown)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Name - declare early so star click handler can reference it
        var nameBlock = new TextBlock
        {
            Text = node.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = node.MustBeHq ? ColorHelper.GetAccentBrush() : GetNodeForeground(node)
        };
        
        // HQ Star toggle (clickable) - only for items that can be HQ
        if (node.CanBeHq)
        {
            var starBlock = new TextBlock
            {
                Text = node.MustBeHq ? "★" : "☆",
                Foreground = node.MustBeHq ? Brushes.Gold : Brushes.Gray,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = node.MustBeHq ? "Click to allow NQ" : "Click to require HQ",
                Margin = new Thickness(0, 0, 4, 0)
            };
            starBlock.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                
                // Optimistic UI: toggle visual state immediately
                var newState = !node.MustBeHq;
                starBlock.Text = newState ? "★" : "☆";
                starBlock.Foreground = newState ? Brushes.Gold : Brushes.Gray;
                starBlock.ToolTip = newState ? "Click to allow NQ" : "Click to require HQ";
                
                // Update name color immediately too
                nameBlock.Foreground = newState ? ColorHelper.GetAccentBrush() : GetNodeForeground(node);
                
                // Then apply the actual change (which will refresh the tree)
                try
                {
                    ToggleNodeHqRequirement(node);
                }
                catch (Exception ex)
                {
                    // Revert on error
                    starBlock.Text = node.MustBeHq ? "★" : "☆";
                    starBlock.Foreground = node.MustBeHq ? Brushes.Gold : Brushes.Gray;
                    nameBlock.Foreground = node.MustBeHq ? ColorHelper.GetAccentBrush() : GetNodeForeground(node);
                    StatusLabel.Text = $"Failed to toggle HQ: {ex.Message}";
                }
            };
            panel.Children.Add(starBlock);
        }
        
        // Add name block after star
        panel.Children.Add(nameBlock);
        
        // Quantity
        var qtyBlock = new TextBlock
        {
            Text = $" ×{node.Quantity}",
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(qtyBlock);
        
        // Price estimate display
        var priceText = GetNodePriceDisplay(node);
        if (!string.IsNullOrEmpty(priceText))
        {
            var priceBlock = new TextBlock
            {
                Text = priceText,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                FontSize = 11
            };
            panel.Children.Add(priceBlock);
        }
        
        // Recipe info (job, level, yield) - only when crafting
        if (!node.IsUncraftable && !string.IsNullOrEmpty(node.Job) && node.Source == AcquisitionSource.Craft)
        {
            var infoBlock = new TextBlock
            {
                Text = $"  ({node.Job} Lv.{node.RecipeLevel}, Yield: {node.Yield})",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(8, 0, 8, 0)
            };
            panel.Children.Add(infoBlock);
        }
        
        // Acquisition source dropdown (on the right for all nodes, including branch nodes)
        // This allows buying complex crafted items like submarine parts instead of crafting them
        if (showDropdown)
        {
            var sourceCombo = CreateAcquisitionSourceDropdown(node);
            if (sourceCombo != null)
            {
                panel.Children.Add(sourceCombo);
            }
        }
        
        return panel;
    }
    
    /// <summary>
    /// Creates a dropdown for selecting acquisition source with cost info.
    /// Filters options based on item type (craftable, gathered, vendor, etc.)
    /// Shows NQ/HQ prices with real market data.
    /// </summary>
    private ComboBox? CreateAcquisitionSourceDropdown(PlanNode node)
    {
        var combo = new ComboBox
        {
            Width = 130,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        // Determine what options to show based on item characteristics
        var hasRecipe = !node.IsUncraftable && node.Children.Count > 0;
        var hasMarketNqPrice = node.MarketPrice > 0;
        // Only show Buy HQ if there's an actual HQ price (not just estimated)
        var hasMarketHqPrice = node.HqMarketPrice > 0 && node.CanBeHq;
        var isVendorItem = node.PriceSource == PriceSource.Vendor;
        
        // Calculate costs
        var craftCost = hasRecipe ? CalculateNodeCraftCost(node) : 0;
        var nqCost = hasMarketNqPrice ? node.MarketPrice * node.Quantity : 0;
        // Use actual HQ price only (no more estimation)
        var hqCost = hasMarketHqPrice ? node.HqMarketPrice * node.Quantity : 0;
        
        // Add Craft option (only for craftable items)
        if (hasRecipe)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"Craft ~{craftCost:N0}g",
                Tag = AcquisitionSource.Craft,
                Foreground = Brushes.White,
                ToolTip = $"Craft: {craftCost:N0}g"
            });
        }
        
        // Add Vendor option (for vendor items)
        if (isVendorItem)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"Vendor {node.MarketPrice * node.Quantity:N0}g",
                Tag = AcquisitionSource.VendorBuy,
                Foreground = Brushes.LightYellow,
                ToolTip = $"Vendor: {node.MarketPrice * node.Quantity:N0}g"
            });
        }
        
        // Add Buy NQ option (if market price available)
        if (hasMarketNqPrice)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"Buy NQ {nqCost:N0}g",
                Tag = AcquisitionSource.MarketBuyNq,
                Foreground = Brushes.LightSkyBlue,
                ToolTip = $"Market NQ: {nqCost:N0}g"
            });
        }
        
        // Add Buy HQ option (if item can be HQ and market price available)
        if (node.CanBeHq && hasMarketHqPrice)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"Buy HQ {hqCost:N0}g",
                Tag = AcquisitionSource.MarketBuyHq,
                Foreground = Brushes.LightGreen,
                ToolTip = $"Market HQ: {hqCost:N0}g"
            });
        }
        
        // If no options available, don't show dropdown
        if (combo.Items.Count == 0)
            return null;
        
        // Select current value
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((AcquisitionSource)item.Tag == node.Source)
            {
                combo.SelectedItem = item;
                break;
            }
        }
        
        // Default to first if not found
        if (combo.SelectedItem == null && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        
        // Handle selection change
        combo.SelectionChanged += (s, e) =>
        {
            if (combo.SelectedItem is ComboBoxItem selected)
            {
                var newSource = (AcquisitionSource)selected.Tag;
                SetNodeAcquisitionSource(node, newSource);
            }
        };
        
        return combo;
    }
    
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
    /// Gets a price display string for a node based on its acquisition source.
    /// Shows craft cost for crafted items and market price for bought items.
    /// </summary>
    private string GetNodePriceDisplay(PlanNode node)
    {
        return node.Source switch
        {
            AcquisitionSource.Craft when node.Children.Any() => 
                $"(~{CalculateNodeCraftCost(node):N0}g)",
            AcquisitionSource.VendorBuy when node.MarketPrice > 0 => 
                $"(~{node.MarketPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyNq when node.MarketPrice > 0 => 
                $"(~{node.MarketPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyHq when node.HqMarketPrice > 0 => 
                $"(~{node.HqMarketPrice * node.Quantity:N0}g)",
            _ => ""
        };
    }

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
            
            // Enable refresh button since we have market data to refresh
            RefreshMarketButton.IsEnabled = true;
            ViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
            
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
    /// Set whether an item must be HQ quality. This is saved with the plan.
    /// If setting to HQ and currently buying NQ, switches to buy HQ.
    /// </summary>
    private void SetNodeMustBeHq(PlanNode node, bool mustBeHq)
    {
        node.MustBeHq = mustBeHq;
        
        // If setting HQ requirement and currently buying NQ, switch to buy HQ
        if (mustBeHq && node.Source == AcquisitionSource.MarketBuyNq)
        {
            _recipeCalcService.SetAcquisitionSource(node, AcquisitionSource.MarketBuyHq);
        }
        // If clearing HQ requirement and currently buying HQ, switch to buy NQ
        else if (!mustBeHq && node.Source == AcquisitionSource.MarketBuyHq)
        {
            _recipeCalcService.SetAcquisitionSource(node, AcquisitionSource.MarketBuyNq);
        }
        
        // Mark plan as modified
        _currentPlan?.MarkModified();
        
        // Refresh the display
        if (_currentPlan != null)
        {
            DisplayPlanInTreeView(_currentPlan);
        }
        
        StatusLabel.Text = $"{node.Name} {(mustBeHq ? "must be" : "can be")} HQ";
    }

    /// <summary>
    /// Toggle the HQ requirement for a node (called when clicking the star).
    /// </summary>
    private void ToggleNodeHqRequirement(PlanNode node)
    {
        SetNodeMustBeHq(node, !node.MustBeHq);
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
                    Quantity = r.Quantity,
                    IsHqRequired = r.MustBeHq
                }).ToList();
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _projectItems.Take(5).ToList();
                UpdateQuickViewCount();
                
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
                Quantity = r.Quantity,
                IsHqRequired = r.MustBeHq
            }).ToList();
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _projectItems.Take(5).ToList();
            UpdateQuickViewCount();
            
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
                        Quantity = item.Quantity,
                        IsHqRequired = item.MustBeHq
                    });
                }
                
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _projectItems.Take(5).ToList();
                UpdateQuickViewCount();
                
                // Display the recipe tree
                DisplayPlanInTreeView(_currentPlan);
                
                // Enable buttons
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
    /// Uses the Market Data Status window for real-time visualization.
    /// Each item is updated independently as data arrives (decoupled from full fetch).
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

        // Initialize or reuse the status window
        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow();
            _marketDataStatusWindow.Owner = this;
        }

        // Collect all unique items from the plan
        var allItems = new List<(int itemId, string name, int quantity)>();
        CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
        
        // Initialize the status window with items to fetch
        _marketDataStatusWindow.InitializeItems(allItems);
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();

        StatusLabel.Text = $"Fetching prices for {allItems.Count} items...";

        try
        {
            // Fetch prices with detailed progress reporting
            var progress = new Progress<(int current, int total, string itemName, PriceFetchStage stage, string? message)>(p =>
            {
                // Use the detailed message if available, otherwise build one
                var statusText = p.message ?? p.stage switch
                {
                    PriceFetchStage.CheckingCache => $"Checking cache... {p.current}/{p.total}",
                    PriceFetchStage.FetchingGarlandData => $"Loading item data: {p.itemName} ({p.current}/{p.total})",
                    PriceFetchStage.FetchingMarketData => $"Fetching market prices... {p.current}/{p.total}",
                    PriceFetchStage.ProcessingResults => $"Processing results... {p.current}/{p.total}",
                    PriceFetchStage.Complete => $"Complete! ({p.total} items)",
                    _ => $"Fetching prices... {p.current}/{p.total}"
                };
                
                StatusLabel.Text = statusText;
                
                // Update status window for specific item progress during Garland fetching
                if (p.stage == PriceFetchStage.FetchingGarlandData && !string.IsNullOrEmpty(p.itemName))
                {
                    var item = allItems.FirstOrDefault(i => i.name == p.itemName);
                    if (item.itemId > 0)
                    {
                        _marketDataStatusWindow.SetItemFetching(item.itemId);
                    }
                }
            });

            var prices = await _priceCheckService.GetBestPricesBulkAsync(
                allItems.Select(i => (i.itemId, i.name)).ToList(), 
                worldOrDc, 
                default, 
                progress,
                forceRefresh: true);

            // Update status window and plan nodes item by item (decoupled)
            int successCount = 0;
            int failedCount = 0;
            int cachedCount = 0;
            
            foreach (var kvp in prices)
            {
                int itemId = kvp.Key;
                var priceInfo = kvp.Value;
                
                if (priceInfo.Source == PriceSource.Unknown)
                {
                    // Failed
                    _marketDataStatusWindow.SetItemFailed(itemId, priceInfo.SourceDetails);
                    failedCount++;
                }
                else if (priceInfo.Source == PriceSource.Vendor || priceInfo.Source == PriceSource.Market)
                {
                    // Fresh data
                    _marketDataStatusWindow.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails);
                    successCount++;
                }
                else
                {
                    // Cached or other
                    _marketDataStatusWindow.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails);
                    cachedCount++;
                }

                // Update plan node immediately (decoupled from full fetch)
                UpdateSingleNodePrice(_currentPlan.RootItems, itemId, priceInfo);
            }

            // Refresh tree view with updated prices
            DisplayPlanInTreeView(_currentPlan);
            
            // Update market logistics tab with fresh market data
            await UpdateMarketLogisticsAsync(prices, useCachedData: false);
            
            // Refresh procurement panel if visible
            if (ProcurementPlannerContent.Visibility == Visibility.Visible)
            {
                PopulateProcurementPanel();
            }

            // Calculate total cost
            var totalCost = _currentPlan.AggregatedMaterials.Sum(m => m.TotalCost);

            if (failedCount > 0 && successCount == 0)
            {
                StatusLabel.Text = $"Price fetch failed! Using cached prices. Total: {totalCost:N0}g";
            }
            else if (failedCount > 0)
            {
                StatusLabel.Text = $"Prices updated! Total: {totalCost:N0}g ({successCount} success, {failedCount} failed, {cachedCount} cached)";
            }
            else
            {
                StatusLabel.Text = $"Prices fetched! Total: {totalCost:N0}g ({successCount} items)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnFetchPrices] Failed to fetch prices");
            StatusLabel.Text = $"Failed to fetch prices: {ex.Message}. Cached prices preserved.";
            
            // Mark all pending items as failed
            foreach (var item in allItems)
            {
                _marketDataStatusWindow.SetItemFailed(item.itemId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Open the Market Data Status window.
    /// </summary>
    private void OnViewMarketStatus(object sender, RoutedEventArgs e)
    {
        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow();
            _marketDataStatusWindow.Owner = this;
            
            // If we have a current plan, initialize with those items
            if (_currentPlan != null && _currentPlan.RootItems.Count > 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusWindow.InitializeItems(allItems);
                
                // Mark items with existing prices as cached/success
                MarkExistingPricesInStatusWindow(_currentPlan.RootItems);
            }
        }
        
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();
    }

    /// <summary>
    /// Mark items that already have prices in the status window.
    /// </summary>
    private void MarkExistingPricesInStatusWindow(List<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.MarketPrice > 0)
            {
                _marketDataStatusWindow?.SetItemCached(node.ItemId, node.MarketPrice, node.PriceSourceDetails);
            }
            
            if (node.Children?.Any() == true)
            {
                MarkExistingPricesInStatusWindow(node.Children);
            }
        }
    }

    /// <summary>
    /// Update a single node with price info (decoupled from full fetch).
    /// </summary>
    private void UpdateSingleNodePrice(List<PlanNode> nodes, int itemId, PriceInfo priceInfo)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId)
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice ?? 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }
            
            if (node.Children?.Any() == true)
            {
                UpdateSingleNodePrice(node.Children, itemId, priceInfo);
            }
        }
    }

    /// <summary>
    /// Recursively collect all items from the plan with quantities.
    /// </summary>
    private void CollectAllItemsWithQuantity(List<PlanNode> nodes, List<(int itemId, string name, int quantity)> items)
    {
        foreach (var node in nodes)
        {
            // Avoid duplicates
            if (!items.Any(i => i.itemId == node.ItemId))
            {
                items.Add((node.ItemId, node.Name, node.Quantity));
            }

            if (node.Children?.Any() == true)
            {
                CollectAllItemsWithQuantity(node.Children, items);
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
                // Only set HQ price if item is known to be HQ-capable (crafted items)
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice ?? 0;
                }
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
        MarketSummaryExpander.Visibility = System.Windows.Visibility.Collapsed;
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
                    default:
                        // Unknown or other sources - treat as market item
                        marketItems.Add(material);
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
                ViewMarketStatusButton.IsEnabled = true;
                MenuViewMarketStatus.IsEnabled = true;
                
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
                ViewMarketStatusButton.IsEnabled = false;
                MenuViewMarketStatus.IsEnabled = false;
                
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
                    
                    // Clear the analyzing message
                    StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
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
                ViewMarketStatusButton.IsEnabled = true;
                MenuViewMarketStatus.IsEnabled = true;
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
            
            // Count based on effective recommendation (considers HQ requirement)
            var craftCount = significantAnalyses.Count(a => a.EffectiveRecommendation == CraftRecommendation.Craft);
            var buyCount = significantAnalyses.Count(a => a.EffectiveRecommendation == CraftRecommendation.Buy);
            var hqRequiredCount = significantAnalyses.Count(a => a.IsHqRequired);
            
            var headerText = $"{significantAnalyses.Count} items: {craftCount} craft, {buyCount} buy";
            if (hqRequiredCount > 0)
                headerText += $" ({hqRequiredCount} HQ required)";
            CraftVsBuySummaryText.Text = headerText;
            
            // Show appropriate warning based on whether any items require HQ
            var warningText = hqRequiredCount > 0
                ? "⚠️ Some items require HQ. HQ prices are used for recommendations. NQ may compromise craft quality."
                : "⚠️ For endgame HQ crafts, NQ components may compromise quality. Check HQ prices below.";
            
            var hqWarning = new TextBlock
            {
                Text = warningText,
                Foreground = hqRequiredCount > 0 ? Brushes.Gold : Brushes.Orange,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            CraftVsBuyContent.Children.Add(hqWarning);
            
            // Show top analyses - use effective recommendation (considers HQ requirement)
            foreach (var analysis in significantAnalyses.Take(8))
            {
                var itemPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                
                // Item name and quantity - show HQ indicator if required
                var itemName = analysis.IsHqRequired 
                    ? $"{analysis.ItemName} x{analysis.Quantity} [HQ Required]"
                    : $"{analysis.ItemName} x{analysis.Quantity}";
                var nameBlock = new TextBlock
                {
                    Text = itemName,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = analysis.IsHqRequired ? Brushes.Gold : (analysis.HasQualityWarning ? Brushes.Orange : Brushes.White)
                };
                itemPanel.Children.Add(nameBlock);
                
                // Primary analysis line: NQ or HQ based on requirement
                string primaryText;
                Brush primaryColor;
                
                if (analysis.IsHqRequired && analysis.HasHqData)
                {
                    // Show HQ analysis as primary
                    if (analysis.PotentialSavingsHq > 0)
                    {
                        primaryText = $"  Crafting saves {analysis.PotentialSavingsHq:N0}g ({analysis.SavingsPercentHq:F0}%) - Buy HQ: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightGreen;
                    }
                    else
                    {
                        primaryText = $"  Crafting costs {Math.Abs(analysis.PotentialSavingsHq):N0}g more ({Math.Abs(analysis.SavingsPercentHq):F0}%) - Buy HQ: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightCoral;
                    }
                }
                else
                {
                    // Show NQ analysis as primary
                    if (analysis.PotentialSavingsNq > 0)
                    {
                        primaryText = $"  Crafting saves {analysis.PotentialSavingsNq:N0}g ({analysis.SavingsPercentNq:F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightGreen;
                    }
                    else
                    {
                        primaryText = $"  Crafting costs {Math.Abs(analysis.PotentialSavingsNq):N0}g more ({Math.Abs(analysis.SavingsPercentNq):F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightCoral;
                    }
                }
                
                var primaryBlock = new TextBlock
                {
                    Text = primaryText,
                    Foreground = primaryColor,
                    FontSize = 11
                };
                itemPanel.Children.Add(primaryBlock);
                
                // Show alternate pricing (HQ if NQ is primary, NQ if HQ is primary)
                if (analysis.HasHqData)
                {
                    string altText;
                    Brush altColor;
                    
                    if (analysis.IsHqRequired)
                    {
                        // Show NQ as alternate when HQ is required
                        if (analysis.PotentialSavingsNq > 0)
                        {
                            altText = $"  NQ alternative: Save {analysis.PotentialSavingsNq:N0}g ({analysis.SavingsPercentNq:F0}%) - Buy: {analysis.BuyCostNq:N0}g";
                            altColor = Brushes.Gray;
                        }
                        else
                        {
                            altText = $"  NQ alternative: Cost {Math.Abs(analysis.PotentialSavingsNq):N0}g more ({Math.Abs(analysis.SavingsPercentNq):F0}%) - Buy: {analysis.BuyCostNq:N0}g";
                            altColor = Brushes.Gray;
                        }
                    }
                    else
                    {
                        // Show HQ as alternate when NQ is primary
                        if (analysis.PotentialSavingsHq > 0)
                        {
                            altText = $"  HQ: Crafting saves {analysis.PotentialSavingsHq:N0}g ({analysis.SavingsPercentHq:F0}%) - Buy: {analysis.BuyCostHq:N0}g";
                            altColor = Brushes.LightGreen;
                        }
                        else
                        {
                            altText = $"  HQ: Crafting costs {Math.Abs(analysis.PotentialSavingsHq):N0}g more ({Math.Abs(analysis.SavingsPercentHq):F0}%) - Buy: {analysis.BuyCostHq:N0}g";
                            altColor = Brushes.LightCoral;
                        }
                    }
                    
                    var altBlock = new TextBlock
                    {
                        Text = altText,
                        Foreground = altColor,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic
                    };
                    itemPanel.Children.Add(altBlock);
                    
                    // Warning if NQ looks good but HQ is expensive (endgame relevant)
                    if (analysis.IsEndgameRelevant && !analysis.IsHqRequired)
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
        MarketSummaryExpander.Visibility = System.Windows.Visibility.Visible;
        
        // Update header with totals
        var grandTotal = vendorItems.Sum(i => i.TotalCost) + marketItems.Sum(i => i.TotalCost);
        MarketSummaryHeaderText.Text = $"{vendorItems.Count + marketItems.Count} items • {grandTotal:N0}g total";
        
        var summaryText = new TextBlock
        {
            Text = $"Vendor: {vendorItems.Count} items ({vendorItems.Sum(i => i.TotalCost):N0}g)  •  " +
                   $"Market: {marketItems.Count} items ({marketItems.Sum(i => i.TotalCost):N0}g)",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
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
            Background = ColorHelper.GetMutedAccentBrush(),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            Tag = plan
        };

        var mainStack = new StackPanel();
        
        // Clickable header - compact layout
        var headerBorder = new Border
        {
            Background = ColorHelper.GetMutedAccentBrushLight(),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 6, 8, 6),
            Cursor = Cursors.Hand
        };
        
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        // Left side: Item name, quantity, DC average - single line compact
        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        
        var nameText = new TextBlock
        {
            Text = plan.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftStack.Children.Add(nameText);
        
        var qtyText = new TextBlock
        {
            Text = $" ×{plan.QuantityNeeded}",
            FontSize = 12,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        leftStack.Children.Add(qtyText);
        
        if (!string.IsNullOrEmpty(plan.Error))
        {
            var errorText = new TextBlock
            {
                Text = $"  •  Error: {plan.Error}",
                Foreground = Brushes.Red,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            leftStack.Children.Add(errorText);
        }
        else
        {
            var avgText = new TextBlock
            {
                Text = $"  •  Avg: {plan.DCAveragePrice:N0}g",
                Foreground = Brushes.Gray,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            leftStack.Children.Add(avgText);
        }
        
        Grid.SetColumn(leftStack, 0);
        headerGrid.Children.Add(leftStack);
        
        // Right side: Recommended world info - compact single line
        var rightStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        
        if (plan.HasOptions && plan.RecommendedWorld != null)
        {
            var recWorld = plan.RecommendedWorld;
            
            var worldText = new TextBlock
            {
                Text = recWorld.WorldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = ColorHelper.GetAccentBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            rightStack.Children.Add(worldText);
            
            var costText = new TextBlock
            {
                Text = $"  {recWorld.CostDisplay}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (recWorld.IsFullyUnderAverage)
            {
                costText.Foreground = Brushes.LightGreen;
            }
            rightStack.Children.Add(costText);
        }
        else if (string.IsNullOrEmpty(plan.Error))
        {
            var noDataText = new TextBlock
            {
                Text = "No viable listings",
                Foreground = Brushes.Orange,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightStack.Children.Add(noDataText);
        }
        
        Grid.SetColumn(rightStack, 1);
        headerGrid.Children.Add(rightStack);
        
        headerBorder.Child = headerGrid;
        mainStack.Children.Add(headerBorder);
        
        // Expandable content (all world options) - start expanded to show all data
        var contentStack = new StackPanel
        {
            Visibility = System.Windows.Visibility.Visible,
            Margin = new Thickness(8, 6, 8, 8)
        };
        headerBorder.Background = ColorHelper.GetMutedAccentBrushExpanded();
        
        if (plan.HasOptions)
        {
            var optionsHeader = new TextBlock
            {
                Text = "All Worlds (non-congested):",
                FontWeight = FontWeights.SemiBold,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Brushes.Gray
            };
            contentStack.Children.Add(optionsHeader);
            
            // Show all non-congested worlds, sorted by cost
            foreach (var world in plan.WorldOptions
                .Where(w => !w.IsCongested || w.IsHomeWorld) // Show non-congested OR home world
                .OrderBy(w => w.IsHomeWorld ? 0 : 1) // Home world first
                .ThenBy(w => w.TotalCost)) // Then by cost
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
                headerBorder.Background = ColorHelper.GetMutedAccentBrushExpanded();
            }
            else
            {
                contentStack.Visibility = System.Windows.Visibility.Collapsed;
                headerBorder.Background = ColorHelper.GetMutedAccentBrushLight();
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
        // Congested worlds get a distinctive muted background
        var backgroundColor = world.IsCongested 
            ? "#3d2d2d"  // Muted reddish for congested
            : world.IsHomeWorld
                ? "#3d3520"  // Gold-tinted for home world
                : isRecommended 
                    ? "#2d4a3e"  // Greenish for recommended
                    : "#2d2d2d"; // Default gray
        
        var borderBrush = world.IsHomeWorld
            ? Brushes.Gold  // Gold border for home world
            : world.IsCongested
                ? Brushes.IndianRed
                : isRecommended 
                    ? Brushes.Gold 
                    : null;
        
        var borderThickness = (world.IsHomeWorld || world.IsCongested || isRecommended) 
            ? new Thickness(1) 
            : new Thickness(0);
        
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 1, 0, 2),
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            Opacity = (world.IsCongested && !world.IsHomeWorld) ? 0.85 : 1.0  // Fade congested non-home worlds
        };

        var stack = new StackPanel();

        // World name and badges - single line compact
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        
        // World name with appropriate styling
        var worldText = new TextBlock
        {
            Text = world.WorldName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = world.IsHomeWorld 
                ? Brushes.Gold  // Gold for home world
                : world.IsCongested 
                    ? Brushes.IndianRed  // Red for congested
                    : Brushes.White
        };
        headerPanel.Children.Add(worldText);
        
        // Home world badge (shown first, takes priority)
        if (world.IsHomeWorld)
        {
            var homeBadge = new TextBlock
            {
                Text = "★ HOME",
                Foreground = Brushes.Gold,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Your home world - you can purchase here even when congested"
            };
            headerPanel.Children.Add(homeBadge);
        }
        
        // Congested warning badge (only show if NOT home world)
        if (world.IsCongested && !world.IsHomeWorld)
        {
            var congestedBadge = new TextBlock
            {
                Text = "⚠ CONGESTED",
                Foreground = Brushes.IndianRed,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "This world is congested and cannot be traveled to for purchases"
            };
            headerPanel.Children.Add(congestedBadge);
        }

        if (isRecommended)
        {
            var recText = new TextBlock
            {
                Text = "★",
                Foreground = ColorHelper.GetAccentBrush(),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            headerPanel.Children.Add(recText);
        }
        else if (world.IsCompetitive)
        {
            var valueText = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.LightGreen,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            headerPanel.Children.Add(valueText);
        }

        stack.Children.Add(headerPanel);

        // Total cost - compact single line
        var costText = new TextBlock
        {
            FontSize = 10,
            Margin = new Thickness(0, 1, 0, 2)
        };
        if (world.HasExcess)
        {
            costText.Text = $"{world.CostDisplay} total  •  {world.ExcessQuantity} excess";
            costText.ToolTip = "FFXIV requires buying full stacks. You'll have excess items.";
        }
        else
        {
            costText.Text = $"{world.CostDisplay} total  •  ~{world.PricePerUnitDisplay}/ea";
        }
        if (world.IsFullyUnderAverage)
        {
            costText.Foreground = Brushes.LightGreen;
        }
        stack.Children.Add(costText);
        
        // Show best unit price if competitive - inline
        if (world.IsCompetitive && world.BestSingleListing != null)
        {
            var valueText = new TextBlock
            {
                Text = $"Best: {world.BestSingleListing.PricePerUnit:N0}g/ea x{world.BestSingleListing.Quantity}{(world.BestSingleListing.IsHq ? " HQ" : "")}",
                FontSize = 9,
                Foreground = ColorHelper.GetAccentBrush(),
                Margin = new Thickness(0, 0, 0, 2)
            };
            stack.Children.Add(valueText);
        }

        // Individual listings header - compact
        var listingsText = new TextBlock
        {
            Text = "Listings:",
            FontSize = 9,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 1)
        };
        stack.Children.Add(listingsText);

        foreach (var listing in world.Listings)
        {
            var listingPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
            
            // Compact quantity display
            var qtyText = new TextBlock
            {
                FontSize = 9,
                Width = 100,
                Foreground = listing.IsAdditionalOption 
                    ? Brushes.DarkGray 
                    : (listing.ExcessQuantity > 0 ? Brushes.Orange : Brushes.LightGray),
                FontStyle = listing.IsAdditionalOption ? FontStyles.Italic : FontStyles.Normal
            };
            if (listing.IsAdditionalOption)
            {
                qtyText.Text = $"x{listing.Quantity} extra";
                qtyText.ToolTip = "Additional option - not needed for quantity, but good value";
            }
            else if (listing.ExcessQuantity > 0)
            {
                qtyText.Text = $"x{listing.Quantity} (need {listing.NeededFromStack})";
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
                FontSize = 9,
                Width = 60,
                Foreground = listing.IsUnderAverage ? Brushes.LightGreen : Brushes.White,
                FontWeight = listing.IsHq ? FontWeights.Bold : FontWeights.Normal
            };
            listingPanel.Children.Add(priceText);

            var subtotalText = new TextBlock
            {
                Text = $"= {listing.SubtotalDisplay}",
                FontSize = 9,
                Foreground = listing.IsAdditionalOption ? Brushes.DarkGray : Brushes.Gray,
                Width = 70
            };
            listingPanel.Children.Add(subtotalText);

            // HQ indicator and retainer name - compact
            var retainerText = new TextBlock
            {
                FontSize = 9,
                Foreground = listing.IsAdditionalOption ? Brushes.DarkGray : Brushes.Gray,
                FontStyle = FontStyles.Italic
            };
            if (listing.IsHq)
            {
                retainerText.Text = $"HQ {listing.RetainerName}";
                retainerText.Foreground = Brushes.Gold;
            }
            else
            {
                retainerText.Text = listing.RetainerName;
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

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var optionsWindow = App.Services.GetRequiredService<OptionsWindow>();
        optionsWindow.Owner = this;
        optionsWindow.ShowDialog();
    }
    
    /// <summary>
    /// Switch to Recipe Planner tab
    /// </summary>
    private void OnRecipePlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        // Update tab styling
        RecipePlannerTab.Background = (SolidColorBrush)FindResource("GoldAccentBrush");
        ((TextBlock)RecipePlannerTab.Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a1a"));
        
        ProcurementPlannerTab.Background = Brushes.Transparent;
        ((TextBlock)ProcurementPlannerTab.Child).Foreground = (SolidColorBrush)FindResource("GoldAccentBrush");
        
        // Switch content visibility
        RecipePlannerContent.Visibility = Visibility.Visible;
        ProcurementPlannerContent.Visibility = Visibility.Collapsed;
        
        StatusLabel.Text = "Recipe Planner";
    }
    
    /// <summary>
    /// Switch to Market Analysis tab and populate with market logistics data
    /// </summary>
    private void OnProcurementPlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        // Update tab styling
        RecipePlannerTab.Background = Brushes.Transparent;
        ((TextBlock)RecipePlannerTab.Child).Foreground = (SolidColorBrush)FindResource("GoldAccentBrush");
        
        ProcurementPlannerTab.Background = (SolidColorBrush)FindResource("GoldAccentBrush");
        ((TextBlock)ProcurementPlannerTab.Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a1a"));
        
        // Switch content visibility
        RecipePlannerContent.Visibility = Visibility.Collapsed;
        ProcurementPlannerContent.Visibility = Visibility.Visible;
        
        // Populate procurement panel if we have a plan
        if (_currentPlan != null)
        {
            PopulateProcurementPanel();
        }
        
        StatusLabel.Text = "Market Analysis";
    }
    
    /// <summary>
    /// Populate the Market Analysis panel with expandable cards and actionable procurement plan.
    /// Uses DetailedShoppingPlan data when available for world recommendations.
    /// </summary>
    private void PopulateProcurementPanel()
    {
        if (_currentPlan == null)
        {
            ProcurementPanel.Children.Clear();
            ProcurementPanel.Children.Add(new TextBlock 
            { 
                Text = "Build a plan to see market analysis",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            
            // Clear procurement plan
            ProcurementPlanPanel.Children.Clear();
            ProcurementPlanPanel.Children.Add(new TextBlock 
            { 
                Text = "No procurement plan available - fetch market data to generate actionable plan",
                Foreground = Brushes.Gray,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        
        // Clear existing content
        ProcurementPanel.Children.Clear();
        
        // Check if we have detailed market plans with world recommendations
        if (_currentMarketPlans?.Any() == true)
        {
            PopulateProcurementWithMarketPlans();
            PopulateProcurementPlanSummary();
            return;
        }
        
        // Fall back to simple aggregated materials view if no market data yet
        PopulateProcurementWithSimpleMaterials();
        
        // Clear procurement plan
        ProcurementPlanPanel.Children.Clear();
        ProcurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }
    
    /// <summary>
    /// Populate the actionable procurement plan summary at the bottom of Market Analysis.
    /// Groups items by recommended world for efficient shopping.
    /// </summary>
    private void PopulateProcurementPlanSummary()
    {
        ProcurementPlanPanel.Children.Clear();
        
        if (_currentMarketPlans?.Any() != true)
            return;
        
        // Group items by recommended world
        var itemsByWorld = _currentMarketPlans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .ToList();
        
        if (!itemsByWorld.Any())
        {
            ProcurementPlanPanel.Children.Add(new TextBlock 
            { 
                Text = "No viable market listings found",
                Foreground = Brushes.Gray,
                FontSize = 12
            });
            return;
        }
        
        // Create shopping list by world
        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.RecommendedWorld?.TotalCost ?? 0);
            var isHomeWorld = items.First().RecommendedWorld?.IsHomeWorld ?? false;
            
            // World header
            var worldHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            
            var worldText = new TextBlock
            {
                Text = worldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = isHomeWorld ? Brushes.Gold : Brushes.White
            };
            worldHeader.Children.Add(worldText);
            
            if (isHomeWorld)
            {
                worldHeader.Children.Add(new TextBlock
                {
                    Text = " ★ HOME",
                    Foreground = Brushes.Gold,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }
            
            worldHeader.Children.Add(new TextBlock
            {
                Text = $" - {items.Count} items, {worldTotal:N0}g total",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            
            ProcurementPlanPanel.Children.Add(worldHeader);
            
            // Items for this world
            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  • {item.Name} ×{item.QuantityNeeded} = {item.RecommendedWorld?.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = Brushes.LightGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                ProcurementPlanPanel.Children.Add(itemText);
            }
            
            // Spacer between worlds
            ProcurementPlanPanel.Children.Add(new Border { Height = 12 });
        }
        
        // Grand total
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var totalText = new TextBlock
        {
            Text = $"Grand Total: {grandTotal:N0}g",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        ProcurementPlanPanel.Children.Add(totalText);
    }
    
    /// <summary>
    /// Populate procurement panel with detailed world recommendations from _currentMarketPlans.
    /// </summary>
    private void PopulateProcurementWithMarketPlans()
    {
        // Calculate totals
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);
        var itemsWithoutOptions = _currentMarketPlans.Count(p => !p.HasOptions);
        
        // Summary header
        var summaryPanel = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3d3d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16)
        };
        
        var summaryStack = new StackPanel();
        summaryStack.Children.Add(new TextBlock
        {
            Text = $"Total Procurement Cost: {grandTotal:N0}g",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50"))
        });
        summaryStack.Children.Add(new TextBlock
        {
            Text = $"Items with market data: {itemsWithOptions}  •  Need price fetch: {itemsWithoutOptions}",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0)
        });
        summaryPanel.Child = summaryStack;
        ProcurementPanel.Children.Add(summaryPanel);
        
        // Get sort preference
        var sortIndex = ProcurementSortCombo?.SelectedIndex ?? 0;
        IEnumerable<DetailedShoppingPlan> sortedPlans = _currentMarketPlans;
        
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
            case 2: // Price (High to Low)
                sortedPlans = _currentMarketPlans
                    .OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0)
                    .ThenBy(p => p.Name);
                break;
        }
        
        // Create expandable cards for each item
        foreach (var plan in sortedPlans)
        {
            var card = CreateExpandableMarketCard(plan);
            ProcurementPanel.Children.Add(card);
        }
    }
    
    /// <summary>
    /// Fallback: Populate with simple material aggregation when no market data fetched yet.
    /// </summary>
    private void PopulateProcurementWithSimpleMaterials()
    {
        var materials = _currentPlan?.AggregatedMaterials;
        
        if (materials?.Any() != true)
        {
            ProcurementPanel.Children.Add(new TextBlock 
            { 
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }
        
        // Show placeholder prompting user to fetch prices
        var placeholderPanel = new StackPanel 
        { 
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0)
        };
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "No market data available",
            Foreground = Brushes.Gray,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to see world recommendations and generate a procurement plan",
            Foreground = Brushes.Gray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = $"Materials to analyze: {materials.Count}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        });
        
        ProcurementPanel.Children.Add(placeholderPanel);
    }
    

    
    /// <summary>
    /// Handle sort selection change in Procurement Planner
    /// </summary>
    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip if not fully loaded yet
        if (_currentPlan == null)
            return;
            
        // Re-populate to apply new sort (only if we have market plans to sort)
        if (ProcurementPlannerContent.Visibility == Visibility.Visible && _currentMarketPlans?.Any() == true)
        {
            PopulateProcurementPanel();
        }
    }
    
    /// <summary>
    /// Handle mode selection change in Procurement Planner
    /// </summary>
    private void OnProcurementModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip if services aren't initialized yet (during InitializeComponent)
        if (_settingsService == null)
            return;
            
        // Save setting and refresh if visible
        if (ProcurementModeCombo.SelectedIndex >= 0)
        {
            var mode = ProcurementModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _settingsService.Set("planning.default_recommendation_mode", mode);
            
            // Re-populate to apply new mode if visible
            if (ProcurementPlannerContent.Visibility == Visibility.Visible && _currentPlan != null)
            {
                PopulateProcurementPanel();
            }
        }
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
            var item = textBox.FindParent<ListBoxItem>();
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
            var listBoxItem = button.FindParent<ListBoxItem>();
            if (listBoxItem?.DataContext is ProjectItem projectItem)
            {
                _projectItems.Remove(projectItem);
                StatusLabel.Text = $"Removed {projectItem.Name} from project";
                
                // Disable build button if no items left
                BuildPlanButton.IsEnabled = _projectItems.Count > 0;
                
                // Refresh the list and count
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _projectItems.Take(5).ToList();
                UpdateQuickViewCount();
            }
        }
    }

    /// <summary>
    /// Update the quick view count indicator showing total items and if more are available.
    /// </summary>
    private void UpdateQuickViewCount()
    {
        if (_projectItems.Count <= 5)
        {
            QuickViewCountText.Text = $"({_projectItems.Count})";
        }
        else
        {
            QuickViewCountText.Text = $"(showing 5 of {_projectItems.Count})";
        }
    }

    /// <summary>
    /// Open the Project Items management window for better handling of large item lists.
    /// </summary>
    private void OnManageItemsClick(object sender, RoutedEventArgs e)
    {
        var planName = _currentPlan?.Name;
        var logger = App.Services.GetRequiredService<ILogger<Views.ProjectItemsWindow>>();
        
        var window = new Views.ProjectItemsWindow(
            _projectItems,
            planName,
            onItemsChanged: (items) =>
            {
                // Update build button state when items change
                BuildPlanButton.IsEnabled = items.Count > 0;
            },
            onAddItem: null,  // Add item from main window search instead
            logger: logger)
        {
            Owner = this
        };
        
        window.ShowDialog();
        
        // Refresh the side panel list after closing
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _projectItems.Take(5).ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _projectItems.Count > 0;
        
        StatusLabel.Text = $"Project items updated: {_projectItems.Count} items";
    }

    /// <summary>
    /// Calculate the cost to craft this node (sum of all component costs).
    /// Respects acquisition source - if a child is set to craft, recursively calculate.
    /// </summary>
    private decimal CalculateNodeCraftCost(PlanNode node)
    {
        if (!node.Children.Any())
            return 0;
        
        decimal total = 0;
        foreach (var child in node.Children)
        {
            // If child is set to buy from market, use market price
            if (child.Source == AcquisitionSource.MarketBuyNq && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.MarketBuyHq && child.HqMarketPrice > 0)
            {
                total += child.HqMarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.VendorBuy && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            // If child is set to craft and has children, recursively calculate
            else if (child.Source == AcquisitionSource.Craft && child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
            // Fallback: if child has market price, use it
            else if (child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            // Fallback: if child has children, recursively calculate
            else if (child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
        }
        return total;
    }



}
