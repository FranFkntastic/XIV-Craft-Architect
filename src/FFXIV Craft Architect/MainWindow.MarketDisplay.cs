using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Helpers;
using FFXIV_Craft_Architect.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    /// <summary>
    /// Opens or activates the market data status window.
    /// </summary>
    private void OnViewMarketStatus(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow(_dialogFactory, _marketDataStatusSession);
            _marketDataStatusWindow.Owner = this;
            _marketDataStatusWindow.RefreshMarketDataRequested += OnMarketDataStatusRefreshRequested;

            if (_marketDataStatusSession.TotalCount == 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusSession.InitializeItems(allItems);
                MarkExistingPricesInStatusWindow(_currentPlan.RootItems);
            }

            _marketDataStatusWindow.RefreshView();
        }

        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();
    }

    /// <summary>
    /// Marks items with existing prices in the status window.
    /// </summary>
    private void MarkExistingPricesInStatusWindow(List<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.MarketPrice > 0)
            {
                _marketDataStatusSession.SetItemCached(node.ItemId, node.MarketPrice, node.PriceSourceDetails, "-", "Market");
            }

            if (node.Children?.Any() == true)
            {
                MarkExistingPricesInStatusWindow(node.Children);
            }
        }
    }

    private static string BuildMarketDataScopeText(
        int itemId,
        IReadOnlyDictionary<int, (int CachedDataCenterCount, int CachedWorldCount)> scopeByItemId,
        int totalDataCenterCount)
    {
        if (!scopeByItemId.TryGetValue(itemId, out var scope) || scope.CachedDataCenterCount == 0)
        {
            return $"0/{totalDataCenterCount} DC / 0 worlds";
        }

        return $"{scope.CachedDataCenterCount}/{totalDataCenterCount} DC / {scope.CachedWorldCount} worlds";
    }

    private static string GetDataTypeText(PriceSource source)
    {
        return source switch
        {
            PriceSource.Market => "Market",
            PriceSource.Vendor => "Vendor",
            PriceSource.Untradeable => "Untradeable",
            PriceSource.Unknown => "Unknown",
            _ => source.ToString()
        };
    }

    /// <summary>
    /// Shows placeholder in market logistics panel when no market data is available.
    /// </summary>
    private void ShowMarketLogisticsPlaceholder()
    {
        ProcurementPanel.Children.Clear();
        var hasPlan = _currentPlan?.RootItems.Count > 0;
        ProcurementRefreshButton.IsEnabled = hasPlan;
        LeftPanelConductAnalysisButton.IsEnabled = hasPlan;

        var placeholderCard = _marketLogisticsCoordinator.CreatePlaceholderCard();
        ProcurementPanel.Children.Add(placeholderCard);
    }

    /// <summary>
    /// Updates the market logistics display with categorized items and shopping plans.
    /// </summary>
    private async Task UpdateMarketLogisticsAsync(Dictionary<int, PriceInfo> prices, bool useCachedData = false, bool searchAllNA = false)
    {
        _logger.LogInformation("[UpdateMarketLogisticsAsync] START - Prices.Count={Count}, UseCachedData={UseCached}", prices.Count, useCachedData);

        ClearMarketLogisticsPanels();

        var aggMaterials = _currentPlan?.AggregatedMaterials ?? new List<MaterialAggregate>();
        _logger.LogInformation("[UpdateMarketLogisticsAsync] AggregatedMaterials.Count={Count}, Items=[{Items}]",
            aggMaterials.Count, string.Join(", ", aggMaterials.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));

        var categorized = _marketShoppingService.CategorizeMaterials(aggMaterials, prices);

        _logger.LogInformation("[UpdateMarketLogisticsAsync] Categorized: Vendor={VendorCount}, Market={MarketCount}, Untradeable={UntradeableCount}",
            categorized.VendorItems.Count, categorized.MarketItems.Count, categorized.UntradeableItems.Count);

        UpdateMarketSummaryCard(categorized.VendorItems, categorized.MarketItems, categorized.UntradeableItems, prices);

        if (categorized.VendorItems.Any())
        {
            AddVendorItemsCard(categorized.VendorItems, prices);
        }

        if (categorized.MarketItems.Any())
        {
            if (useCachedData)
            {
                _logger.LogInformation("[UpdateMarketLogisticsAsync] Using cached data path");
                AddCachedMarketDataCard(categorized.MarketItems, prices);

                if (_currentPlan != null)
                {
                    var cachedResult = _marketLogisticsCoordinator.CalculateCachedLogistics(
                        _currentPlan,
                        prices,
                        _currentPlan.SavedMarketPlans);

                    if (cachedResult.Success)
                    {
                        var marketItemIds = categorized.MarketItems
                            .Select(item => item.ItemId)
                            .ToHashSet();

                        var marketPlans = cachedResult.ShoppingPlans
                            .Where(plan => marketItemIds.Contains(plan.ItemId))
                            .ToList();

                        _marketVm.SetShoppingPlans(marketPlans);
                    }
                }
            }
            else
            {
                _logger.LogInformation("[UpdateMarketLogisticsAsync] Fetching live market data for {Count} items (SearchAllNA={SearchAllNA})", categorized.MarketItems.Count, searchAllNA);
                await FetchAndDisplayLiveMarketDataAsync(categorized.MarketItems, searchAllNA);
            }
        }
        else
        {
            _logger.LogWarning("[UpdateMarketLogisticsAsync] No market items to fetch - setting empty shopping plans");
            _marketVm.SetShoppingPlans(new List<DetailedShoppingPlan>());
        }

        if (categorized.UntradeableItems.Any())
        {
            AddUntradeableItemsCard(categorized.UntradeableItems);
        }

        _logger.LogInformation("[UpdateMarketLogisticsAsync] END - _marketVm.ShoppingPlans.Count={Count}", _marketVm.ShoppingPlans.Count);
    }

    private void ClearMarketLogisticsPanels()
    {
        _marketVm.Clear();
        ProcurementPanel.Children.Clear();
    }

    private void AddVendorItemsCard(List<MaterialAggregate> vendorItems, Dictionary<int, PriceInfo> prices)
    {
        var vendorText = new System.Text.StringBuilder();
        vendorText.AppendLine("Buy these from vendors (cheapest option):");
        vendorText.AppendLine();
        foreach (var item in vendorItems.OrderByDescending(i => i.TotalCost))
        {
            var source = prices[item.ItemId].SourceDetails;
            vendorText.AppendLine($"• {item.Name} x{item.TotalQuantity} = {item.TotalCost:N0}g ({source})");
        }

        var vendorCard = _cardFactory.CreateInfoCard(
            $"Vendor Items ({vendorItems.Count})",
            vendorText.ToString(),
            CardType.Vendor);
        ProcurementPanel.Children.Add(vendorCard);
    }

    private void AddCachedMarketDataCard(List<MaterialAggregate> marketItems, Dictionary<int, PriceInfo> prices)
    {
        var cachedCard = _cardFactory.CreateInfoCard(
            $"Market Board Items ({marketItems.Count})",
            "Using saved prices. Click 'Refresh Market Data' to fetch current listings.\n\n" +
            "Items to purchase:\n" +
            string.Join("\n", marketItems.Select(m =>
                $"• {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g ({prices[m.ItemId].SourceDetails})")),
            CardType.Cached);
        ProcurementPanel.Children.Add(cachedCard);

        ProcurementRefreshButton.IsEnabled = true;
        LeftPanelConductAnalysisButton.IsEnabled = true;
        RebuildFromCacheButton.IsEnabled = true;
        LeftPanelViewMarketStatusButton.IsEnabled = true;
        MenuViewMarketStatus.IsEnabled = true;
    }

    private async Task FetchAndDisplayLiveMarketDataAsync(List<MaterialAggregate> marketItems, bool searchAllNA = false, List<DetailedShoppingPlan>? preFetchedPlans = null)
    {
        _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] START - {Count} market items, SearchAllNA={SearchAllNA}, HasPreFetched={HasPreFetched}",
            marketItems.Count, searchAllNA, preFetchedPlans != null);

        var dc = DcCombo.SelectedItem as string ?? "Aether";

        if (preFetchedPlans != null)
        {
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] Using pre-fetched plans, skipping fetch");

            _marketVm.SetShoppingPlans(preFetchedPlans);

            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = preFetchedPlans;
            }

            PopulateProcurementPanel();

            StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
            return;
        }

        var loadingCard = _marketLogisticsCoordinator.CreateLoadingState(dc, marketItems.Count, searchAllNA);
        ProcurementPanel.Children.Add(loadingCard);

        ProcurementRefreshButton.IsEnabled = false;
        LeftPanelConductAnalysisButton.IsEnabled = false;
        LeftPanelViewMarketStatusButton.IsEnabled = false;
        MenuViewMarketStatus.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(msg => { StatusLabel.Text = $"Analyzing market: {msg}"; });

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

            _marketVm.SetShoppingPlans(shoppingPlans);

            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = shoppingPlans;
            }

            ProcurementPanel.Children.Remove(loadingCard);
            PopulateProcurementPanel();

            StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FetchAndDisplayLiveMarketDataAsync] FAILED - Exception: {Message}", ex.Message);
            ProcurementPanel.Children.Remove(loadingCard);
            var errorCard = _marketLogisticsCoordinator.CreateErrorCard($"Error fetching listings: {ex.Message}");
            ProcurementPanel.Children.Add(errorCard);
        }
        finally
        {
            ProcurementRefreshButton.IsEnabled = true;
            LeftPanelConductAnalysisButton.IsEnabled = true;
            LeftPanelViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
        }
    }

    private void AddUntradeableItemsCard(List<MaterialAggregate> untradeableItems)
    {
        var untradeText = new System.Text.StringBuilder();
        untradeText.AppendLine("These items must be gathered or crafted:");
        untradeText.AppendLine();
        foreach (var item in untradeableItems)
        {
            untradeText.AppendLine($"• {item.Name} x{item.TotalQuantity}");
        }

        var untradeCard = _cardFactory.CreateInfoCard(
            $"Untradeable Items ({untradeableItems.Count})",
            untradeText.ToString(),
            CardType.Untradeable);
        ProcurementPanel.Children.Add(untradeCard);
    }

    private void UpdateMarketSummaryCard(List<MaterialAggregate> vendorItems, List<MaterialAggregate> marketItems,
        List<MaterialAggregate> untradeableItems, Dictionary<int, PriceInfo> prices)
    {
        if (_currentPlan == null)
        {
            var fallbackTotal = vendorItems.Sum(i => i.TotalCost) + marketItems.Sum(i => i.TotalCost);
            MarketTotalCostText.Text = $"{vendorItems.Count + marketItems.Count} items • {fallbackTotal:N0}g total";
            return;
        }

        var summaryData = _marketLogisticsCoordinator.CalculateSummaryData(_currentPlan, prices);
        var totalItems = summaryData.VendorItemCount + summaryData.MarketItemCount;
        var grandTotal = summaryData.TotalVendorCost + summaryData.TotalMarketCost;
        MarketTotalCostText.Text = $"{totalItems} items • {grandTotal:N0}g total";
    }

    private RecommendationMode GetCurrentRecommendationMode()
    {
        return ProcurementModeCombo.SelectedIndex switch
        {
            1 => RecommendationMode.MaximizeValue,
            _ => RecommendationMode.MinimizeTotalCost
        };
    }
}
