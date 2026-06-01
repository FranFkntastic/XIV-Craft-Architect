using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Coordinators;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Helpers;
using FFXIV_Craft_Architect.Models;
using FFXIV_Craft_Architect.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;
using DialogChoice = FFXIV_Craft_Architect.Services.Interfaces.DialogResult;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    private MarketDataStatusWindow? _marketDataStatusWindow;
    private readonly MarketDataStatusSession _marketDataStatusSession = new();

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
            _marketDataStatusWindow.CacheCheckRequested += OnMarketDataStatusCacheCheckRequested;
        }

        var initialItems = new List<(int itemId, string name, int quantity)>();
        _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, initialItems);
        _marketDataStatusSession.InitializeItems(initialItems);
        _marketDataStatusWindow.RefreshView();

        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();

        OnCheckMarketCacheAsync().SafeFireAndForget(OnAsyncError);
    }

    private void OnConductAnalysis(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var forceRefresh = MarketAnalysisSidebarModule.ForceRefetch;
        OnConductAnalysisAsync(forceRefresh).SafeFireAndForget(OnAsyncError);
    }

    private async Task OnConductAnalysisAsync(bool forceRefresh)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var searchAllNA = ProcurementSearchAllNaCheck?.IsChecked ?? false;

        if (forceRefresh)
        {
            StatusLabel.Text = "Force refetch enabled - fetching market data before analysis...";
            await OnFetchPricesAsync(forceRefresh: true);
            await RunMarketAnalysisFromCacheAsync(searchAllNA);
            return;
        }

        var inspection = await _marketVm.InspectPlanCacheAsync(_currentPlan, dc, searchAllNA);
        var missingCount = inspection.CacheCandidateItemIds.Count(itemId =>
            !inspection.ItemCacheByItemId.TryGetValue(itemId, out var snapshot) || !snapshot.HasCache);

        if (missingCount > 0)
        {
            var availableCount = inspection.CacheCandidateItemIds.Count - missingCount;
            var choice = await _dialogs.YesNoCancelAsync(
                $"Market cache is incomplete for this plan.\n\n" +
                $"Cached items: {availableCount}\n" +
                $"Missing items: {missingCount}\n\n" +
                "Choose how to continue:\n" +
                "- Yes: Proceed with partial analysis\n" +
                "- No: Fetch market data now, then continue analysis\n" +
                "- Cancel: Stop analysis",
                "Incomplete Market Cache");

            if (choice == DialogChoice.Cancel)
            {
                StatusLabel.Text = "Analysis canceled (incomplete cache)";
                return;
            }

            if (choice == DialogChoice.No)
            {
                StatusLabel.Text = "Fetching missing market data before analysis...";
                await OnFetchPricesAsync(forceRefresh: false);
            }
        }

        await RunMarketAnalysisFromCacheAsync(searchAllNA);
    }

    private void OnMarketDataStatusRefreshRequested(object? sender, EventArgs e)
    {
        if (_marketDataStatusWindow != null && _marketDataStatusWindow.IsVisible)
        {
            if (_currentPlan != null && _currentPlan.RootItems.Count > 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusSession.InitializeItems(allItems);
                _marketDataStatusWindow.RefreshView();
            }
        }

        OnFetchPricesAsync(forceRefresh: true).SafeFireAndForget(OnAsyncError);
    }

    private void OnMarketDataStatusCacheCheckRequested(object? sender, EventArgs e)
    {
        OnCheckMarketCacheAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnFetchPricesAsync(bool forceRefresh = false)
    {
        _logger.LogInformation("[OnFetchPricesAsync] START - forceRefresh={ForceRefresh}", forceRefresh);

        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            _logger.LogWarning("[OnFetchPricesAsync] ABORT - No plan available");
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? string.Empty;
        var worldOrDc = string.IsNullOrEmpty(world) || world == "Entire Data Center" ? dc : world;
        var searchAllNA = ProcurementSearchAllNaCheck?.IsChecked ?? false;

        _logger.LogInformation(
            "[OnFetchPricesAsync] DC={DC}, World={World}, SearchAllNA={SearchAllNA}, PlanItems={ItemCount}",
            dc,
            world,
            searchAllNA,
            _currentPlan.RootItems.Count);

        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow(_dialogFactory, _marketDataStatusSession)
            {
                Owner = this
            };
            _marketDataStatusWindow.RefreshMarketDataRequested += OnMarketDataStatusRefreshRequested;
            _marketDataStatusWindow.CacheCheckRequested += OnMarketDataStatusCacheCheckRequested;
        }

        var allItems = new List<(int itemId, string name, int quantity)>();
        _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);

        _logger.LogInformation(
            "[OnFetchPricesAsync] RootItems.Count={RootCount}, AggregatedMaterials.Count={AggCount}",
            _currentPlan.RootItems.Count,
            _currentPlan.AggregatedMaterials?.Count ?? 0);
        _logger.LogInformation(
            "[OnFetchPricesAsync] Collected {Count} items for price check: [{Items}]",
            allItems.Count,
            string.Join(", ", allItems.Select(i => $"{i.name}({i.itemId})x{i.quantity}")));

        if (_currentPlan.AggregatedMaterials?.Any() == true)
        {
            _logger.LogInformation(
                "[OnFetchPricesAsync] AggregatedMaterials: [{Materials}]",
                string.Join(", ", _currentPlan.AggregatedMaterials.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));
        }

        _marketDataStatusSession.InitializeItems(allItems);
        _marketDataStatusWindow.RefreshView();
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();

        void ProgressHandler(object? _, PriceRefreshProgress p)
        {
            StatusLabel.Text = p.Message ?? p.Stage switch
            {
                PriceRefreshStage.Starting => $"Checking cache... {p.Current}/{p.Total}",
                PriceRefreshStage.Fetching when string.IsNullOrWhiteSpace(p.ItemName) =>
                    $"Fetching market prices... {p.Current}/{p.Total}",
                PriceRefreshStage.Fetching => $"Loading item data: {p.ItemName} ({p.Current}/{p.Total})",
                PriceRefreshStage.Updating => $"Processing results... {p.Current}/{p.Total}",
                PriceRefreshStage.Complete => $"Complete! ({p.Total} items)",
                _ => $"Fetching prices... {p.Current}/{p.Total}"
            };

            if (p.Stage == PriceRefreshStage.Fetching && !string.IsNullOrEmpty(p.ItemName))
            {
                var item = allItems.FirstOrDefault(i => i.name == p.ItemName);
                if (item.itemId > 0)
                {
                    _marketDataStatusSession.SetItemFetching(item.itemId);
                    _marketDataStatusWindow?.RefreshView();
                }
            }
        }

        _marketVm.PriceRefreshProgressReported += ProgressHandler;

        try
        {
            var refreshResult = await _marketVm.RefreshPlanPricesAsync(
                _currentPlan,
                dc,
                worldOrDc,
                searchAllNA,
                forceRefresh,
                default);

            allItems = refreshResult.AllItems;
            var prices = refreshResult.Prices;

            foreach (var kvp in prices)
            {
                var itemId = kvp.Key;
                var priceInfo = kvp.Value;

                if (priceInfo.Source == PriceSource.Unknown)
                {
                    if (!refreshResult.WarmCacheForCraftedItems && !refreshResult.CacheCandidateItemIds.Contains(itemId))
                    {
                        _marketDataStatusSession.SetItemSkipped(itemId, "Skipped (crafted item; market warming disabled)");
                    }
                    else
                    {
                        _marketDataStatusSession.SetItemFailed(itemId, priceInfo.SourceDetails, dataTypeText: "Unknown");
                    }
                }
                else if (priceInfo.Source == PriceSource.Market)
                {
                    var isFetchedThisRun = refreshResult.ScopeDataCenters.Any(itemDc =>
                        refreshResult.FetchedThisRunKeys.Contains((itemId, itemDc)));
                    var dataScopeText = BuildMarketDataScopeText(itemId, refreshResult.DataScopeByItemId, refreshResult.ScopeDataCenters.Count);

                    if (isFetchedThisRun)
                    {
                        _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, dataScopeText, "Universalis (this run)", "Market");
                    }
                    else
                    {
                        _marketDataStatusSession.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, dataScopeText, "Market", priceInfo.LastUpdated);
                    }
                }
                else if (priceInfo.Source == PriceSource.Vendor)
                {
                    _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", "Garland", MarketShoppingConstants.VendorWorldName);
                }
                else if (priceInfo.Source == PriceSource.Untradeable)
                {
                    _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", "N/A", "Untradeable");
                }
                else
                {
                    _marketDataStatusSession.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", GetDataTypeText(priceInfo.Source), priceInfo.LastUpdated);
                }
            }

            _marketDataStatusWindow?.RefreshView();

            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
            }

            StatusLabel.Text = $"{refreshResult.Message} Run Conduct Analysis to update market recommendations.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnFetchPricesAsync] FAILED - Exception: {Message}\nStackTrace: {StackTrace}", ex.Message, ex.StackTrace);
            StatusLabel.Text = $"Failed to fetch prices: {ex.Message}. Cached prices preserved.";

            foreach (var item in allItems)
            {
                _marketDataStatusSession.SetItemFailed(item.itemId, ex.Message, dataTypeText: "Unknown");
            }
            _marketDataStatusWindow?.RefreshView();
        }
        finally
        {
            _marketVm.PriceRefreshProgressReported -= ProgressHandler;
        }

        _logger.LogInformation("[OnFetchPricesAsync] END");
    }

    private async Task RunMarketAnalysisFromCacheAsync(bool searchAllNA)
    {
        if (_currentPlan == null)
        {
            return;
        }

        StatusLabel.Text = "Running market analysis from cached data...";

        _logger.LogInformation("[RunMarketAnalysisFromCacheAsync] Extracting prices from plan...");
        var prices = _recipeCalcService.ExtractPricesFromPlan(_currentPlan);
        _logger.LogInformation("[RunMarketAnalysisFromCacheAsync] Extracted {Count} prices. Keys: [{Keys}]",
            prices.Count, string.Join(", ", prices.Keys));

        // Log which items are in AggregatedMaterials but NOT in prices
        var aggMaterials = _currentPlan.AggregatedMaterials ?? new List<MaterialAggregate>();
        var missingPrices = aggMaterials.Where(m => !prices.ContainsKey(m.ItemId)).ToList();
        if (missingPrices.Any())
        {
            _logger.LogWarning("[RunMarketAnalysisFromCacheAsync] {Count} items in AggregatedMaterials have no price data: [{Items}]",
                missingPrices.Count, string.Join(", ", missingPrices.Select(m => $"{m.Name}({m.ItemId})")));
        }

        await UpdateMarketLogisticsAsync(prices, useCachedData: false, searchAllNA: searchAllNA);

        StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} recommendation entries updated.";
    }

    private async Task OnCheckMarketCacheAsync()
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var searchAllNA = ProcurementSearchAllNaCheck?.IsChecked ?? false;

        StatusLabel.Text = "Checking cache coverage...";

        var inspection = await _marketVm.InspectPlanCacheAsync(_currentPlan, dc, searchAllNA);
        _marketDataStatusSession.InitializeItems(inspection.AllItems);
        PopulateStatusSessionFromCacheInspection(inspection);
        _marketDataStatusWindow?.RefreshView();

        var cachedFreshCount = inspection.ItemCacheByItemId.Values.Count(v => v.HasFreshCache);
        var cachedStaleCount = inspection.ItemCacheByItemId.Values.Count(v => v.HasCache && !v.HasFreshCache);
        var cacheMissingCount = inspection.CacheCandidateItemIds.Count(v =>
            !inspection.ItemCacheByItemId.TryGetValue(v, out var snapshot) || !snapshot.HasCache);

        StatusLabel.Text =
            $"Cache check complete: {cachedFreshCount} fresh, {cachedStaleCount} stale, {cacheMissingCount} missing";
    }

    private void PopulateStatusSessionFromCacheInspection(PlanCacheInspectionContext inspection)
    {
        foreach (var (itemId, _, _) in inspection.AllItems)
        {
            if (!inspection.CacheCandidateItemIds.Contains(itemId))
            {
                _marketDataStatusSession.SetItemSkipped(itemId, "Not in market cache scope");
                continue;
            }

            if (!inspection.ItemCacheByItemId.TryGetValue(itemId, out var cacheSnapshot) || !cacheSnapshot.HasCache)
            {
                _marketDataStatusSession.SetItemNoCache(itemId, "No cache entry for selected scope");
                continue;
            }

            var dataScopeText =
                $"{cacheSnapshot.CachedDataCenterCount}/{inspection.ScopeDataCenters.Count} DC / {cacheSnapshot.CachedWorldCount} worlds";
            var sourceDetails = cacheSnapshot.HasFreshCache ? "Fresh cache entry" : "Stale cache entry";
            var dataTypeText = cacheSnapshot.HasFreshCache ? "Market" : "Market (stale)";

            _marketDataStatusSession.SetItemCached(
                itemId,
                cacheSnapshot.CachedUnitPrice,
                sourceDetails,
                dataScopeText,
                dataTypeText,
                cacheSnapshot.LatestFetchedAtUtc);
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
        LeftPanelConductAnalysisButton.IsEnabled = hasPlan;

        var placeholderCard = CreateMarketInfoCard(
            "Market Board Items",
            "Click 'Fetch Market Data' to get current market board prices and shopping recommendations.",
            MarketInfoCardKind.Neutral);
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

        // Pass plan to check for VendorBuy acquisition source in addition to PriceSource
        var categorized = _marketShoppingService.CategorizeMaterials(aggMaterials, prices, _currentPlan);

        _logger.LogInformation("[UpdateMarketLogisticsAsync] Categorized: Vendor={VendorCount}, Market={MarketCount}, Untradeable={UntradeableCount}",
            categorized.VendorItems.Count, categorized.MarketItems.Count, categorized.UntradeableItems.Count);

        UpdateMarketSummaryCard(categorized.VendorItems, categorized.MarketItems, categorized.UntradeableItems, prices);

        // Create vendor plans for split-pane view
        List<DetailedShoppingPlan> vendorPlans = new();
        if (categorized.VendorItems.Any())
        {
            AddVendorItemsCard(categorized.VendorItems, prices);
            vendorPlans = CreateVendorShoppingPlansForViewModel(categorized.VendorItems, prices);
        }

        var displayOnlyShoppingPlans = false;

        // Handle market items
        List<DetailedShoppingPlan> marketPlans = new();
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

                        marketPlans = cachedResult.ShoppingPlans
                            .Where(plan => marketItemIds.Contains(plan.ItemId))
                            .ToList();
                    }
                }
            }
            else
            {
                _logger.LogInformation("[UpdateMarketLogisticsAsync] Fetching live market data for {Count} items (SearchAllNA={SearchAllNA})", categorized.MarketItems.Count, searchAllNA);
                marketPlans = await FetchAndDisplayLiveMarketDataForPlansAsync(categorized.MarketItems, searchAllNA);
                displayOnlyShoppingPlans = true;
            }
        }
        else
        {
            _logger.LogInformation("[UpdateMarketLogisticsAsync] No market items - vendor-only or empty");
        }

        // Combine vendor and market plans for split-pane view
        var allPlans = new List<DetailedShoppingPlan>();
        allPlans.AddRange(vendorPlans);
        allPlans.AddRange(marketPlans);
        if (displayOnlyShoppingPlans)
        {
            _marketVm.DisplayShoppingPlans(allPlans);
        }
        else
        {
            _marketVm.SetShoppingPlans(allPlans);
        }

        if (categorized.UntradeableItems.Any())
        {
            AddUntradeableItemsCard(categorized.UntradeableItems);
        }

        _logger.LogInformation("[UpdateMarketLogisticsAsync] END - _marketVm.ShoppingPlans.Count={Count} (Vendor={Vendor}, Market={Market})", 
            _marketVm.ShoppingPlans.Count, vendorPlans.Count, marketPlans.Count);
    }

    private void ClearMarketLogisticsPanels()
    {
        _marketVm.ClearDisplay();
        ProcurementPanel.Children.Clear();
    }

    private void AddVendorItemsCard(List<MaterialAggregate> vendorItems, Dictionary<int, PriceInfo> prices)
    {
        _logger.LogInformation("[AddVendorItemsCard] START - {Count} vendor items, Prices has {PriceCount} entries",
            vendorItems.Count, prices.Count);

        var vendorText = new System.Text.StringBuilder();
        vendorText.AppendLine("Buy these from vendors (cheapest option):");
        vendorText.AppendLine();

        foreach (var item in vendorItems.OrderByDescending(i => i.TotalCost))
        {
            _logger.LogDebug("[AddVendorItemsCard] Processing {Name} (ItemId={ItemId})", item.Name, item.ItemId);

            if (!prices.TryGetValue(item.ItemId, out var priceInfo))
            {
                _logger.LogWarning("[AddVendorItemsCard] No price info for {Name} (ItemId={ItemId}) - skipping", item.Name, item.ItemId);
                continue;
            }

            var source = priceInfo.SourceDetails;
            vendorText.AppendLine($"• {item.Name} x{item.TotalQuantity} = {item.TotalCost:N0}g ({source})");
        }

        var vendorCard = CreateMarketInfoCard(
            $"Vendor Items ({vendorItems.Count})",
            vendorText.ToString(),
            MarketInfoCardKind.Vendor);
        ProcurementPanel.Children.Add(vendorCard);

        _logger.LogInformation("[AddVendorItemsCard] END - card added to ProcurementPanel");
    }

    /// <summary>
    /// Creates DetailedShoppingPlan objects for vendor items suitable for display in the split-pane view.
    /// These plans have WorldName = "Vendor" and include vendor information.
    /// </summary>
    private List<DetailedShoppingPlan> CreateVendorShoppingPlansForViewModel(
        List<MaterialAggregate> vendorItems,
        Dictionary<int, PriceInfo> prices)
    {
        _logger.LogInformation("[CreateVendorShoppingPlansForViewModel] START - {Count} vendor items, Prices has {PriceCount} entries",
            vendorItems.Count, prices.Count);

        var plans = new List<DetailedShoppingPlan>();

        foreach (var item in vendorItems)
        {
            _logger.LogDebug("[CreateVendorShoppingPlansForViewModel] Processing {Name} (ItemId={ItemId})",
                item.Name, item.ItemId);

            if (!prices.TryGetValue(item.ItemId, out var priceInfo))
            {
                _logger.LogWarning("[CreateVendorShoppingPlansForViewModel] No price info for {Name} (ItemId={ItemId}) - skipping",
                    item.Name, item.ItemId);
                continue;
            }

            var unitPrice = priceInfo.UnitPrice;
            var totalCost = (long)(unitPrice * item.TotalQuantity);
            var selectedVendor = priceInfo.Vendors?
                .Where(v => v.IsGilVendor)
                .OrderBy(v => Math.Abs(v.Price - unitPrice))
                .ThenBy(v => v.Name)
                .FirstOrDefault();

            _logger.LogDebug("[CreateVendorShoppingPlansForViewModel] {Name}: UnitPrice={UnitPrice}, TotalCost={TotalCost}, Vendors={VendorCount}",
                item.Name, unitPrice, totalCost, priceInfo.Vendors?.Count ?? 0);

            var vendorWorldSummary = new WorldShoppingSummary
            {
                WorldName = MarketShoppingConstants.VendorWorldName,
                WorldId = 0,
                TotalCost = totalCost,
                AveragePricePerUnit = unitPrice,
                ListingsUsed = 1,
                TotalQuantityPurchased = item.TotalQuantity,
                HasSufficientStock = true,
                IsHomeWorld = false,
                IsTravelProhibited = false,
                IsBlacklisted = false,
                Classification = WorldClassification.Standard,
                VendorName = selectedVendor?.DisplayName ?? MarketShoppingConstants.VendorWorldName,
                Listings = new List<ShoppingListingEntry>
                {
                    new()
                    {
                        Quantity = item.TotalQuantity,
                        PricePerUnit = (long)unitPrice,
                        RetainerName = MarketShoppingConstants.VendorWorldName,
                        IsUnderAverage = true,
                        IsHq = false,
                        NeededFromStack = item.TotalQuantity,
                        ExcessQuantity = 0
                    }
                }
            };

            var plan = new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                IconId = item.IconId,
                QuantityNeeded = item.TotalQuantity,
                DCAveragePrice = unitPrice,
                Vendors = priceInfo.Vendors?.ToList() ?? new List<VendorInfo>(),
                RecommendedWorld = vendorWorldSummary,
                WorldOptions = new List<WorldShoppingSummary> { vendorWorldSummary }
            };

            plans.Add(plan);
            _logger.LogDebug("[CreateVendorShoppingPlansForViewModel] Created plan for {Name}", item.Name);
        }

        _logger.LogInformation("[CreateVendorShoppingPlansForViewModel] END - {Count} plans created", plans.Count);
        return plans;
    }

    private void AddCachedMarketDataCard(List<MaterialAggregate> marketItems, Dictionary<int, PriceInfo> prices)
    {
        _logger.LogInformation("[AddCachedMarketDataCard] START - {Count} market items", marketItems.Count);

        var itemLines = new List<string>();
        foreach (var m in marketItems)
        {
            if (prices.TryGetValue(m.ItemId, out var priceInfo))
            {
                itemLines.Add($"• {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g ({priceInfo.SourceDetails})");
            }
            else
            {
                _logger.LogWarning("[AddCachedMarketDataCard] No price info for {Name} (ItemId={ItemId})", m.Name, m.ItemId);
                itemLines.Add($"• {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g (no price data)");
            }
        }

        var cachedCard = CreateMarketInfoCard(
            $"Market Board Items ({marketItems.Count})",
            "Using saved prices. Click 'Refresh Market Data' to fetch current listings.\n\n" +
            "Items to purchase:\n" + string.Join("\n", itemLines),
            MarketInfoCardKind.Cached);
        ProcurementPanel.Children.Add(cachedCard);

        LeftPanelConductAnalysisButton.IsEnabled = true;
        LeftPanelViewMarketStatusButton.IsEnabled = true;
        MenuViewMarketStatus.IsEnabled = true;

        _logger.LogInformation("[AddCachedMarketDataCard] END - card added");
    }

    private async Task FetchAndDisplayLiveMarketDataAsync(List<MaterialAggregate> marketItems, bool searchAllNA = false)
    {
        var plans = await FetchAndDisplayLiveMarketDataForPlansAsync(marketItems, searchAllNA);
        // Plans are set in UpdateMarketLogisticsAsync, just update UI
        PopulateProcurementPanel();
    }

    /// <summary>
    /// Fetches live market data and returns shopping plans (does not set them in ViewModel).
    /// This allows the caller to combine vendor and market plans.
    /// </summary>
    private async Task<List<DetailedShoppingPlan>> FetchAndDisplayLiveMarketDataForPlansAsync(
        List<MaterialAggregate> marketItems, 
        bool searchAllNA = false)
    {
        _logger.LogInformation("[FetchAndDisplayLiveMarketDataForPlansAsync] START - {Count} market items, SearchAllNA={SearchAllNA}",
            marketItems.Count, searchAllNA);

        var dc = DcCombo.SelectedItem as string ?? "Aether";

        var loadingCard = _marketLogisticsCoordinator.CreateLoadingState(dc, marketItems.Count, searchAllNA);
        ProcurementPanel.Children.Add(loadingCard);

        LeftPanelConductAnalysisButton.IsEnabled = false;
        LeftPanelViewMarketStatusButton.IsEnabled = false;
        MenuViewMarketStatus.IsEnabled = false;

        try
        {
            var mode = GetCurrentRecommendationMode();
            var result = await _marketVm.RunCoreMarketAnalysisAsync(
                CreateCoreMarketAnalysisWorkflowRequest(dc, searchAllNA, mode),
                default);

            if (!result.Published)
            {
                ProcurementPanel.Children.Remove(loadingCard);
                var message = string.IsNullOrWhiteSpace(_marketVm.StatusMessage)
                    ? "Market analysis did not publish."
                    : _marketVm.StatusMessage;
                var errorCard = CreateMarketInfoCard("Error", message, MarketInfoCardKind.Error);
                ProcurementPanel.Children.Add(errorCard);
                StatusLabel.Text = message;
                return new List<DetailedShoppingPlan>();
            }

            var marketItemIds = marketItems
                .Select(item => item.ItemId)
                .ToHashSet();
            var plans = _marketVm.ShoppingPlans
                .Select(vm => vm.Plan)
                .Where(plan => marketItemIds.Contains(plan.ItemId))
                .ToList();
            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = plans;
            }

            ProcurementPanel.Children.Remove(loadingCard);
            StatusLabel.Text = _marketVm.StatusMessage;

            _logger.LogInformation("[FetchAndDisplayLiveMarketDataForPlansAsync] END - {Count} plans returned", plans.Count);
            return plans;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FetchAndDisplayLiveMarketDataForPlansAsync] FAILED - Exception: {Message}", ex.Message);
            ProcurementPanel.Children.Remove(loadingCard);
            var errorCard = CreateMarketInfoCard("Error", $"Error fetching listings: {ex.Message}", MarketInfoCardKind.Error);
            ProcurementPanel.Children.Add(errorCard);
            return new List<DetailedShoppingPlan>();
        }
        finally
        {
            LeftPanelConductAnalysisButton.IsEnabled = true;
            LeftPanelViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
        }
    }

    private CoreMarketAnalysisWorkflowRequest CreateCoreMarketAnalysisWorkflowRequest(
        string selectedDataCenter,
        bool searchAllNA,
        RecommendationMode mode)
    {
        var scope = searchAllNA
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var selectedRegion = ResolveSelectedRegion(selectedDataCenter);
        var lens = mode == RecommendationMode.MaximizeValue
            ? MarketAcquisitionLens.BulkValue
            : MarketAcquisitionLens.MinimumUpfrontCost;

        return new CoreMarketAnalysisWorkflowRequest(
            ForceRefreshData: false,
            Scope: scope,
            SelectedDataCenter: selectedDataCenter,
            SelectedRegion: selectedRegion,
            Lens: lens,
            ExpectedWorldsByDataCenter: BuildExpectedWorlds(scope, selectedDataCenter, selectedRegion));
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildExpectedWorlds(
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion)
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(scope, selectedDataCenter, selectedRegion);
        return dataCenters.ToDictionary(
            dataCenter => dataCenter,
            dataCenter => (IReadOnlyList<string>)_worldDataCoordinator.GetWorldsForDataCenter(dataCenter),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveSelectedRegion(string selectedDataCenter)
    {
        if (MarketFetchScopeResolver.GetDataCenters(MarketFetchScope.EntireRegion, selectedDataCenter, "North America")
            .Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase))
        {
            return "North America";
        }

        if (MarketFetchScopeResolver.GetDataCenters(MarketFetchScope.EntireRegion, selectedDataCenter, "Europe")
            .Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase))
        {
            return "Europe";
        }

        if (MarketFetchScopeResolver.GetDataCenters(MarketFetchScope.EntireRegion, selectedDataCenter, "Japan")
            .Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase))
        {
            return "Japan";
        }

        if (MarketFetchScopeResolver.GetDataCenters(MarketFetchScope.EntireRegion, selectedDataCenter, "Oceania")
            .Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase))
        {
            return "Oceania";
        }

        return "North America";
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

        var untradeCard = CreateMarketInfoCard(
            $"Untradeable Items ({untradeableItems.Count})",
            untradeText.ToString(),
            MarketInfoCardKind.Untradeable);
        ProcurementPanel.Children.Add(untradeCard);
    }

    private static ContentControl CreateMarketInfoCard(string title, string content, MarketInfoCardKind kind)
    {
        return new ContentControl
        {
            Content = new MarketInfoCardViewModel(title, content, kind)
        };
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
