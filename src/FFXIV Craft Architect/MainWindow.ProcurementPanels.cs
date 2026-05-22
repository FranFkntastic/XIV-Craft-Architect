using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
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

        if (_currentMarketPlans?.Any() != true)
        {
            ProcurementPlanPanel.Children.Add(new TextBlock
            {
                Text = "No market analysis data found. Run Conduct Analysis in Market Analysis first.",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var itemsByWorld = _currentMarketPlans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .ToList();

        if (!itemsByWorld.Any())
        {
            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new TextBlock
            {
                Text = "No viable market listings found",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12
            });
            return;
        }

        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.RecommendedWorld?.TotalCost ?? 0);

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
                Text = $" - {items.Count} items, {worldTotal:N0}g total",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });

            _procurementBuilder?.ProcurementPlanPanel.Children.Add(worldHeader);

            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  - {item.Name} x{item.QuantityNeeded} = {item.RecommendedWorld?.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                _procurementBuilder?.ProcurementPlanPanel.Children.Add(itemText);
            }

            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new Border { Height = 12 });
        }

        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
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
        _procurementBuilder?.SplitPaneCardsGrid.Children.Clear();

        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);

        _procurementBuilder?.UpdateSplitPaneTotal(grandTotal, itemsWithOptions);

        _marketLogisticsCoordinator.SetAvailablePlans(_currentMarketPlans);

        // Separate vendor and market plans
        var vendorPlans = _currentMarketPlans
            .Where(p => p.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName)
            .ToList();

        var marketPlans = _currentMarketPlans
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
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        PopulateProcurementPlanSummary();

        StatusLabel.Text = _currentMarketPlans.Any()
            ? "Procurement plan built from existing market analysis data"
            : "No market analysis data found. Run Conduct Analysis in Market Analysis first.";
    }
}
