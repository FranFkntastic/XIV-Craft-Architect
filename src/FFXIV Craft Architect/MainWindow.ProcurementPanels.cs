using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Helpers;
using FFXIV_Craft_Architect.Services;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;
using ShoppingPlanSortMode = FFXIV_Craft_Architect.Core.Coordinators.ShoppingPlanSortMode;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    private void PopulateProcurementPanel()
    {
        _logger.LogInformation("[PopulateProcurementPanel] START - HasPlan={HasPlan}, MarketPlans={Count}",
            _currentPlan != null,
            _currentMarketPlans?.Count ?? 0);

        _procurementBuilder?.ClearPanels();

        if (_currentPlan == null)
        {
            _marketLogisticsCoordinator.ClearSelection();
            _procurementBuilder?.ShowNoPlanPlaceholderSplitPane();
            return;
        }

        var overlayPlans = _mainVm.ProcurementPlanner.CurrentOverlayShoppingPlans;
        if (_activeTab == MainTab.ProcurementPlanner && overlayPlans.Any())
        {
            PopulateSplitPaneWithPlans(overlayPlans);
            PopulateProcurementPlanSummary();
            return;
        }

        if (_currentMarketPlans?.Any() == true)
        {
            PopulateSplitPaneWithMarketPlans();
            PopulateProcurementPlanSummary();
            return;
        }

        PopulateSplitPaneWithSimpleMaterials();

        _procurementBuilder?.ProcurementPlanPanel.Children.Clear();
            ProcurementPlanPanel.Children.Add(new TextBlock
            {
                Text = "Run Conduct Analysis in Market Analysis to generate an actionable procurement plan with world recommendations",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
    }

    private void PopulateProcurementPlanSummary()
    {
        ProcurementPlanPanel.Children.Clear();
        var procurementPlans = _mainVm.ProcurementPlanner.CurrentOverlayShoppingPlans;

        if (procurementPlans.Any() != true)
        {
            ProcurementPlanPanel.Children.Add(new TextBlock
            {
                Text = "No Core procurement route found. Build a Procurement Plan after running market analysis.",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var worldCards = ProcurementWorldCardBuilder
            .BuildWorldCards(procurementPlans, GetCurrentDataCenter() ?? string.Empty);

        if (!worldCards.Any())
        {
            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new TextBlock
            {
                Text = "No viable market listings found",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12
            });
            return;
        }

        foreach (var worldCard in worldCards)
        {
            var worldName = GetWorldDisplayName(worldCard.WorldName, worldCard.DataCenter);
            var items = worldCard.Items
                .OrderBy(i => i.ItemName)
                .ToList();

            var worldHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            var worldText = new TextBlock
            {
                Text = worldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = ResolveBrush("TextPrimaryBrush", Brushes.White)
            };
            worldHeader.Children.Add(worldText);

            worldHeader.Children.Add(new TextBlock
            {
                Text = $" - {items.Count} items, {worldCard.TotalCost:N0}g total",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });

            _procurementBuilder?.ProcurementPlanPanel.Children.Add(worldHeader);

            foreach (var item in items)
            {
                var itemText = new TextBlock
                {
                    Text = $"  - {item.ItemName} {GetQuantityDisplay(item)} = {item.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                _procurementBuilder?.ProcurementPlanPanel.Children.Add(itemText);
            }

            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new Border { Height = 12 });
        }

        var grandTotal = worldCards.Sum(w => w.TotalCost);
        var totalText = new TextBlock
        {
            Text = $"Grand Total: {grandTotal:N0}g",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = ResolveBrush("Brush.Status.Success", Brushes.LightGreen),
            Margin = new Thickness(0, 8, 0, 0)
        };
        _procurementBuilder?.ProcurementPlanPanel.Children.Add(totalText);
    }

    private void PopulateSplitPaneWithMarketPlans()
    {
        PopulateSplitPaneWithPlans(_currentMarketPlans);
    }

    private void PopulateSplitPaneWithPlans(IReadOnlyList<DetailedShoppingPlan> plans)
    {
        _procurementBuilder?.SplitPaneCardsGrid.Children.Clear();

        var displayPlans = plans.ToList();
        var grandTotal = displayPlans.Sum(ProcurementPlanCost.GetRecommendedCost);
        var itemsWithOptions = displayPlans.Count(ProcurementPlanCost.HasRecommendation);

        _procurementBuilder?.UpdateSplitPaneTotal(grandTotal, itemsWithOptions);

        _marketLogisticsCoordinator.SetAvailablePlans(displayPlans);

        // Separate vendor and market plans
        var vendorPlans = displayPlans
            .Where(p => p.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName)
            .ToList();

        var marketPlans = displayPlans
            .Where(p => p.RecommendedWorld?.WorldName != MarketShoppingConstants.VendorWorldName)
            .ToList();

        var sortMode = (GetActiveProcurementSortIndex()) switch
        {
            1 => ShoppingPlanSortMode.Alphabetical,
            2 => ShoppingPlanSortMode.PriceHighToLow,
            _ => ShoppingPlanSortMode.RecommendedWorld
        };

        if (vendorPlans.Any())
        {
            AddSectionToSplitPane(
                "VENDOR",
                vendorPlans,
                sortMode,
                "Brush.Accent.Primary",
                "Brush.Border.Section.Vendor");
        }

        if (marketPlans.Any())
        {
            AddSectionToSplitPane(
                "MARKET",
                marketPlans,
                sortMode,
                "Brush.Status.Info",
                "Brush.Border.Section.Market");
        }
    }

    private void AddSectionToSplitPane(
        string title,
        List<DetailedShoppingPlan> plans,
        ShoppingPlanSortMode sortMode,
        string foregroundBrushKey,
        string lineBrushKey)
    {
        if (_procurementBuilder == null || plans.Count == 0)
        {
            return;
        }

        var sectionPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 8)
        };

        sectionPanel.Children.Add(CreateSectionHeader(title, plans.Count, foregroundBrushKey, lineBrushKey));

        var cardsWrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = ResolveCollapsedCardWidth(),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0)
        };

        foreach (var plan in _shoppingOptimizationCoordinator.SortPlans(plans, sortMode))
        {
            cardsWrapPanel.Children.Add(CreateCollapsedCardFromTemplate(plan));
        }

        sectionPanel.Children.Add(cardsWrapPanel);
        _procurementBuilder.SplitPaneCardsGrid.Children.Add(sectionPanel);
    }

    private FrameworkElement CreateSectionHeader(
        string title,
        int count,
        string foregroundBrushKey,
        string lineBrushKey)
    {
        var panel = new Grid
        {
            Margin = new Thickness(0, 6, 0, 4)
        };

        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Icon
        var icon = new TextBlock
        {
            Text = title == "VENDOR" ? "🏪" : "🛒",
            FontSize = 11,
            Foreground = ResolveBrush(foregroundBrushKey, Brushes.White),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(icon);

        // Title
        var titleText = new TextBlock
        {
            Text = $"{title} ({count})",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Foreground = ResolveBrush(foregroundBrushKey, Brushes.White),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        header.Children.Add(titleText);

        Grid.SetColumn(header, 0);
        panel.Children.Add(header);

        var divider = new Border
        {
            Height = 1,
            Background = ResolveBrush(lineBrushKey, Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(divider, 2);
        panel.Children.Add(divider);

        return panel;
    }

    private static double ResolveCollapsedCardWidth()
    {
        if (Application.Current?.TryFindResource("Layout.MarketCard.Collapsed.Width") is double width)
        {
            return width;
        }

        return 320;
    }

    private static string GetWorldDisplayName(string worldName, string dataCenter)
    {
        if (string.Equals(worldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(dataCenter))
        {
            return worldName;
        }

        return $"{worldName} ({dataCenter})";
    }

    private static string GetQuantityDisplay(WorldItemPurchase item)
    {
        return item.IsSplitPurchase ? item.QuantityDisplay : item.SimpleQuantityDisplay;
    }

    private void PopulateSplitPaneWithSimpleMaterials()
    {
        _marketLogisticsCoordinator.ClearSelection();

        var materials = _currentPlan?.AggregatedMaterials;

        if (materials?.Any() != true)
        {
            _procurementBuilder?.SplitPaneCardsGrid.Children.Add(new TextBlock
            {
                Text = "No materials to display",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        _procurementBuilder?.ShowRefreshNeededPlaceholderSplitPane(materials.Count);
    }

    private FrameworkElement CreateCollapsedCardFromTemplate(DetailedShoppingPlan plan)
    {
        var isSelected = _marketLogisticsCoordinator.SelectedItemId == plan.ItemId;
        var viewModel = new MarketCardViewModel(plan, _marketLogisticsCoordinator)
        {
            IsSelected = isSelected
        };

        return new ContentControl
        {
            Content = viewModel,
            ContentTemplate = FindResource("CollapsedMarketCardTemplate") as DataTemplate,
            Tag = plan
        };
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private async void ShowBlacklistConfirmationDialog(string worldName, int worldId)
    {
        if (!await _dialogs.ConfirmAsync(
            $"Blacklist {worldName}?\n\n" +
            "This world will be excluded from acquisition recommendations for 30 minutes. " +
            "You can still manually select this world if needed.\n\n" +
            "Use this when a world is currently travel-prohibited (at capacity).",
            "Confirm World Blacklist"))
        {
            return;
        }

        _blacklistService.AddToBlacklist(worldId, worldName, "Travel prohibited - user blacklisted");
        foreach (var world in ResolveMarketWorldKeysForWorldName(worldName))
        {
            _mainVm.ProcurementPlanner.BlacklistMarketWorldTemporarily(
                world,
                "wpf temporary market world blacklisted");
        }

        StatusLabel.Text = $"{worldName} blacklisted for 30 minutes";

        if (IsMarketViewVisible())
        {
            PopulateProcurementPanel();
        }
    }

    private int GetActiveProcurementSortIndex()
    {
        return _activeTab == MainTab.ProcurementPlanner
            ? (ProcurementPlannerSortCombo?.SelectedIndex ?? 0)
            : (MarketAnalysisProcurementSortCombo?.SelectedIndex ?? 0);
    }

    private void OnBuildProcurementPlan(object sender, RoutedEventArgs e)
    {
        RunCoreProcurementAnalysisAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task RunCoreProcurementAnalysisAsync()
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var result = await _mainVm.ProcurementPlanner.RunCoreProcurementAnalysisAsync(
            CreateCoreProcurementWorkflowRequest(),
            new Progress<string>(message => StatusLabel.Text = message));
        if (result.Status == CoreProcurementWorkflowStatus.Published)
        {
            PopulateSplitPaneWithPlans(_mainVm.ProcurementPlanner.CurrentOverlayShoppingPlans);
            PopulateProcurementPlanSummary();
        }

        StatusLabel.Text = _mainVm.ProcurementPlanner.StatusMessage;
    }

    private CoreProcurementWorkflowRequest CreateCoreProcurementWorkflowRequest()
    {
        var selectedDataCenter = GetCurrentDataCenter() ?? _currentPlan?.DataCenter ?? "Aether";
        var selectedRegion = ResolveSelectedRegion(selectedDataCenter);
        var scope = ProcurementSearchAllNaCheck.IsChecked == true
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var lens = ProcurementModeCombo.SelectedIndex == 1
            ? MarketAcquisitionLens.BulkValue
            : MarketAcquisitionLens.MinimumUpfrontCost;
        var includeSplitPurchases = EnableSplitWorldCheck.IsChecked == true;
        var config = MarketAnalysisConfig.FromSettings(_settingsService);
        config.EnableSplitWorld = includeSplitPurchases;

        return new CoreProcurementWorkflowRequest(
            scope,
            selectedDataCenter,
            selectedRegion,
            lens,
            config,
            includeSplitPurchases,
            [],
            new HashSet<MarketWorldKey>(),
            new HashSet<MarketItemWorldKey>(),
            BuildExpectedWorlds(scope, selectedDataCenter, selectedRegion));
    }

    private IReadOnlyList<MarketWorldKey> ResolveMarketWorldKeysForWorldName(string worldName)
    {
        var selectedDataCenter = GetCurrentDataCenter() ?? _currentPlan?.DataCenter ?? "Aether";
        var selectedRegion = ResolveSelectedRegion(selectedDataCenter);
        var scope = ProcurementSearchAllNaCheck.IsChecked == true
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var expectedWorlds = BuildExpectedWorlds(scope, selectedDataCenter, selectedRegion);
        var matches = expectedWorlds
            .SelectMany(pair => pair.Value
                .Where(world => string.Equals(world, worldName, StringComparison.OrdinalIgnoreCase))
                .Select(world => new MarketWorldKey(pair.Key, world)))
            .ToList();

        return matches.Count > 0
            ? matches
            : [new MarketWorldKey(selectedDataCenter, worldName)];
    }
}
