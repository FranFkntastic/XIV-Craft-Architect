using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;

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
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void PopulateProcurementPlanSummary()
    {
        ProcurementPlanPanel.Children.Clear();

        if (_currentMarketPlans?.Any() != true)
        {
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
                Foreground = Brushes.Gray,
                FontSize = 12
            });
            return;
        }

        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.RecommendedWorld?.TotalCost ?? 0);
            var isHomeWorld = items.First().RecommendedWorld?.IsHomeWorld ?? false;

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
                    Text = " * HOME",
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

            _procurementBuilder?.ProcurementPlanPanel.Children.Add(worldHeader);

            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  - {item.Name} x{item.QuantityNeeded} = {item.RecommendedWorld?.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = Brushes.LightGray,
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
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
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

        var sortMode = (ProcurementSortCombo?.SelectedIndex ?? 0) switch
        {
            1 => ShoppingPlanSortMode.Alphabetical,
            2 => ShoppingPlanSortMode.PriceHighToLow,
            _ => ShoppingPlanSortMode.RecommendedWorld
        };
        var sortedPlans = _shoppingOptimizationCoordinator.SortPlans(_currentMarketPlans, sortMode);

        foreach (var plan in sortedPlans)
        {
            var card = CreateCollapsedCardFromTemplate(plan);
            _procurementBuilder?.SplitPaneCardsGrid.Children.Add(card);
        }

        if (_expandedSplitPanePlan != null)
        {
            var planToExpand = _currentMarketPlans.FirstOrDefault(p => p.ItemId == _expandedSplitPanePlan.ItemId);
            if (planToExpand != null)
            {
                BuildExpandedPanel(planToExpand);
            }
            else
            {
                _expandedSplitPanePlan = null;
                _procurementBuilder?.SetExpandedPanelVisibility(false);
            }
        }
    }

    private void PopulateSplitPaneWithSimpleMaterials()
    {
        _procurementBuilder?.ClearExpandedPanel();
        _procurementBuilder?.SetExpandedPanelVisibility(false);

        var materials = _currentPlan?.AggregatedMaterials;

        if (materials?.Any() != true)
        {
            _procurementBuilder?.SplitPaneCardsGrid.Children.Add(new TextBlock
            {
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        _procurementBuilder?.ShowRefreshNeededPlaceholderSplitPane(materials.Count);
    }

    private Border CreateCollapsedCardFromTemplate(DetailedShoppingPlan plan)
    {
        var isExpanded = _expandedSplitPanePlan?.ItemId == plan.ItemId;
        var viewModel = new MarketCardViewModel(plan);

        return _cardFactory.CreateCollapsedMarketCard(
            viewModel,
            isExpanded,
            () => OnCollapsedCardClick(plan));
    }

    private void OnCollapsedCardClick(DetailedShoppingPlan plan)
    {
        if (_expandedSplitPanePlan?.ItemId == plan.ItemId)
        {
            _expandedSplitPanePlan = null;
            SplitPaneExpandedPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _expandedSplitPanePlan = plan;
            BuildExpandedPanel(plan);
        }

        PopulateSplitPaneWithMarketPlans();
    }

    private void BuildExpandedPanel(DetailedShoppingPlan plan)
    {
        _procurementBuilder?.ClearExpandedPanel();
        _procurementBuilder?.SetExpandedPanelVisibility(true);

        var viewModel = new ExpandedPanelViewModel(plan);
        viewModel.CloseRequested += () =>
        {
            _expandedSplitPanePlan = null;
            _procurementBuilder?.SetExpandedPanelVisibility(false);
            PopulateSplitPaneWithMarketPlans();
        };

        var contentControl = new ContentControl
        {
            Content = viewModel
        };

        _procurementBuilder?.AddToExpandedPanel(contentControl);
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
}
