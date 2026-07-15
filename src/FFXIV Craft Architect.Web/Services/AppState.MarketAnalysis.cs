using System.Collections.Frozen;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public partial class AppState
{
    public void SetMarketEvidenceHydrating(bool isHydrating)
    {
        if (_isMarketEvidenceHydrating == isHydrating)
        {
            return;
        }

        _isMarketEvidenceHydrating = isHydrating;
        NotifyShoppingListChanged();
    }

    public void ApplyMarketAnalysisPublication(
        IEnumerable<MarketItemAnalysis> analyses,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        bool acquisitionDecisionsChanged,
        StoredRecipeOperationSnapshot? recipeBasis = null,
        PublishedMarketAnalysisScopeSnapshot? publishedScope = null)
    {
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        using (BeginStateChangeBatch())
        {
            ReplaceMarketAnalysis(analyses, shoppingPlans, recipeBasis, publishedScope);
            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
            if (acquisitionDecisionsChanged)
            {
                NotifyPlanDecisionChanged();
            }
        }
    }

    public void ReplaceMarketAnalysis(
        IEnumerable<MarketItemAnalysis> analyses,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        StoredRecipeOperationSnapshot? recipeBasis = null,
        PublishedMarketAnalysisScopeSnapshot? publishedScope = null)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        ReplaceListContents(_marketItemAnalyses, analyses);
        ReplaceListContents(_shoppingPlans, shoppingPlans);
        _marketIntelligenceId = _marketItemAnalyses.Count > 0 || _shoppingPlans.Count > 0
            ? Guid.NewGuid()
            : Guid.Empty;
        _marketAnalysisRecipeBasis = CloneRecipeBasis(recipeBasis);
        _publishedMarketAnalysisScope = publishedScope;
        InvalidateProcurementRouteState("Market evidence changed.");
        var viewChanged = PruneMarketAnalysisViewState(_shoppingPlans, _marketItemAnalyses, publishChange: false);
        var navigationChanged = ApplyPendingMarketItemAutoExpand(publishChange: false);
        using (BeginStateChangeBatch())
        {
            NotifyShoppingListChanged();
            NotifyProcurementOverlayChanged();
            if (viewChanged || navigationChanged)
            {
                PublishChange(AppStateChangeScope.MarketAnalysisView);
            }
        }
    }

    public void ReplaceMarketAnalysisItem(
        MarketItemAnalysis analysis,
        DetailedShoppingPlan shoppingPlan)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(shoppingPlan);

        ReplaceListContents(
            _marketItemAnalyses,
            MarketEvidenceCollectionMerger.MergeAnalyses(_marketItemAnalyses, [analysis]));
        ReplaceListContents(
            _shoppingPlans,
            MarketEvidenceCollectionMerger.MergeShoppingPlans(_shoppingPlans, [shoppingPlan]));
        _marketIntelligenceId = Guid.NewGuid();

        _publishedMarketAnalysisScope ??= CreateCurrentMarketAnalysisScopeSnapshot();
        InvalidateProcurementRouteState("Market evidence changed.");
        var viewChanged = PruneMarketAnalysisViewState(_shoppingPlans, _marketItemAnalyses, publishChange: false);
        var navigationChanged = ApplyPendingMarketItemAutoExpand(publishChange: false);
        using (BeginStateChangeBatch())
        {
            NotifyShoppingListChanged();
            NotifyProcurementOverlayChanged();
            if (viewChanged || navigationChanged)
            {
                PublishChange(AppStateChangeScope.MarketAnalysisView);
            }
        }
    }

    public void ReplaceMarketAnalysisItems(
        IEnumerable<MarketItemAnalysis> analyses,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        var updatedAnalyses = MarketEvidenceCollectionMerger.MergeAnalyses(_marketItemAnalyses, analyses);
        var updatedShoppingPlans = MarketEvidenceCollectionMerger.MergeShoppingPlans(_shoppingPlans, shoppingPlans);

        ReplaceListContents(_marketItemAnalyses, updatedAnalyses);
        ReplaceListContents(_shoppingPlans, updatedShoppingPlans);
        _marketIntelligenceId = Guid.NewGuid();

        _publishedMarketAnalysisScope ??= CreateCurrentMarketAnalysisScopeSnapshot();
        InvalidateProcurementRouteState("Market evidence changed.");
        var viewChanged = PruneMarketAnalysisViewState(_shoppingPlans, _marketItemAnalyses, publishChange: false);
        var navigationChanged = ApplyPendingMarketItemAutoExpand(publishChange: false);
        using (BeginStateChangeBatch())
        {
            NotifyShoppingListChanged();
            NotifyProcurementOverlayChanged();
            if (viewChanged || navigationChanged)
            {
                PublishChange(AppStateChangeScope.MarketAnalysisView);
            }
        }
    }

    public void ClearProcurementOverlay()
    {
        ResetProcurementOverlayState();
        NotifyProcurementOverlayChanged();
    }

    public void ReplaceProcurementOverlay(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        MarketRouteDecision? routeDecision = null)
    {
        ReplaceListContents(_procurementShoppingPlans, shoppingPlans);
        ProcurementRouteDecision = routeDecision;
        _procurementRoutePublicationBasis = _procurementShoppingPlans.Count == 0
            ? null
            : CreateCurrentProcurementRouteBasis();
        ProcurementRouteStaleReason = null;
        ProcurementRouteFailure = null;
        NotifyProcurementOverlayChanged();
    }

    public bool ApplyProcurementOptimization(
        CraftingPlan expectedPlan,
        CraftingPlan optimizedPlan,
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        MarketRouteDecision? routeDecision,
        IEnumerable<MarketItemAnalysis>? evidenceAnalyses = null,
        IEnumerable<DetailedShoppingPlan>? evidencePlans = null,
        PublishedMarketAnalysisScopeSnapshot? evidenceScope = null)
    {
        ArgumentNullException.ThrowIfNull(expectedPlan);
        ArgumentNullException.ThrowIfNull(optimizedPlan);
        ArgumentNullException.ThrowIfNull(activeProcurementItems);
        ArgumentNullException.ThrowIfNull(shoppingPlans);
        if (!ReferenceEquals(CurrentPlan, expectedPlan))
        {
            return false;
        }

        using (BeginStateChangeBatch())
        {
            if (evidenceAnalyses != null && evidencePlans != null)
            {
                ReplaceMarketAnalysis(
                    evidenceAnalyses,
                    evidencePlans,
                    MarketAnalysisRecipeBasis,
                    evidenceScope ?? CreateCurrentMarketAnalysisScopeSnapshot());
            }

            var decisionsChanged = !HaveSameAcquisitionDecisions(expectedPlan, optimizedPlan);
            if (decisionsChanged)
            {
                CurrentPlan = optimizedPlan;
                NotifyPlanDecisionChanged();
            }
            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
            ReplaceProcurementOverlay(shoppingPlans, routeDecision);
        }

        return true;
    }

    private static bool HaveSameAcquisitionDecisions(CraftingPlan left, CraftingPlan right)
    {
        var leftDecisions = EnumerateNodes(left.RootItems)
            .ToDictionary(node => node.NodeId, node => (node.Source, node.SourceReason), StringComparer.Ordinal);
        var rightNodes = EnumerateNodes(right.RootItems).ToList();
        return leftDecisions.Count == rightNodes.Count && rightNodes.All(node =>
            leftDecisions.TryGetValue(node.NodeId, out var decision) &&
            decision == (node.Source, node.SourceReason));
    }

    private static IEnumerable<PlanNode> EnumerateNodes(IEnumerable<PlanNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    public void InvalidateProcurementRoute(string reason)
    {
        if (!InvalidateProcurementRouteState(reason))
        {
            return;
        }

        NotifyProcurementOverlayChanged();
    }

    private bool InvalidateProcurementRouteState(string reason)
    {
        if (_procurementShoppingPlans.Count == 0)
        {
            return false;
        }

        ProcurementRouteStaleReason = reason;
        ProcurementRouteFailure = null;
        return true;
    }

    public void MarkProcurementRouteFailed(string message)
    {
        ProcurementRouteFailure = message;
        NotifyProcurementOverlayChanged();
    }

    private void ResetProcurementOverlayState()
    {
        _procurementShoppingPlans.Clear();
        ProcurementRouteDecision = null;
        _procurementRoutePublicationBasis = null;
        ProcurementRouteStaleReason = null;
        ProcurementRouteFailure = null;
    }

    public ProcurementRoutePublicationBasis CreateCurrentProcurementRouteBasis()
    {
        var scope = ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;

        return new ProcurementRoutePublicationBasis(
            PlanSessionVersion,
            CurrentVersions.PlanDecisionVersion,
            _marketIntelligenceId,
            scope,
            SelectedDataCenter,
            SelectedRegion,
            MarketAnalysisLens,
            ProcurementEnableSplitWorldPurchases,
            ProcurementTravelTolerance,
            ProcurementTravelPriority,
            ProcurementStartFromHomeDataCenter,
            GetActiveBlacklistedMarketWorlds(),
            TemporarilyExcludedItemWorlds.ToHashSet());
    }

    public ProcurementRoutePublicationValidity GetProcurementRouteValidity()
    {
        var published = _procurementRoutePublicationBasis;
        if (published == null || _procurementShoppingPlans.Count == 0)
        {
            return ProcurementRoutePublicationValidity.None;
        }

        var current = CreateCurrentProcurementRouteBasis();
        if (!published.HasSameRouteInputsAs(current))
        {
            return ProcurementRoutePublicationValidity.InputsChanged;
        }

        return published.TravelTolerance == current.TravelTolerance
            ? ProcurementRoutePublicationValidity.Current
            : ProcurementRoutePublicationValidity.SelectionChanged;
    }

    public void SetMarketEvidenceSettings(
        string dataCenter,
        string region,
        MarketFetchScope defaultFetchScope,
        bool searchEntireRegion)
    {
        var changesMarketContext =
            !string.Equals(SelectedDataCenter, dataCenter, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SelectedRegion, region, StringComparison.OrdinalIgnoreCase) ||
            SearchEntireRegion != searchEntireRegion;
        var changesSettings =
            changesMarketContext ||
            DefaultMarketFetchScope != defaultFetchScope;

        if (!changesSettings)
        {
            return;
        }

        SelectedDataCenter = dataCenter;
        SelectedRegion = region;
        DefaultMarketFetchScope = defaultFetchScope;
        SearchEntireRegion = searchEntireRegion;

        if (changesMarketContext)
        {
            InvalidateProcurementRouteState("The market location changed.");
            UnavailableMarketItems = Array.Empty<CoreMarketDataUnavailableItem>();
            ClearMarketAnalysisViewState(publishChange: false);
            PublishChange(
                AppStateChangeScope.Settings |
                AppStateChangeScope.MarketAnalysis |
                AppStateChangeScope.ProcurementOverlay,
                raiseShoppingListChanged: true);
            return;
        }

        NotifySettingsChanged();
    }

    public void SetProcurementSettings(
        bool searchEntireRegion,
        bool enableSplitWorldPurchases,
        int travelTolerance,
        int temporaryWorldBlacklistDurationMinutes)
    {
        var normalizedTravelTolerance = Math.Clamp(travelTolerance, 0, 11);
        var normalizedDuration = Math.Max(1, temporaryWorldBlacklistDurationMinutes);
        var changesRouteMeaning =
            ProcurementSearchEntireRegion != searchEntireRegion ||
            ProcurementEnableSplitWorldPurchases != enableSplitWorldPurchases ||
            ProcurementTravelTolerance != normalizedTravelTolerance;
        var changesSettings =
            changesRouteMeaning ||
            TemporaryWorldBlacklistDurationMinutes != normalizedDuration;

        if (!changesSettings)
        {
            return;
        }

        ProcurementSearchEntireRegion = searchEntireRegion;
        ProcurementEnableSplitWorldPurchases = enableSplitWorldPurchases;
        ProcurementTravelTolerance = normalizedTravelTolerance;
        TemporaryWorldBlacklistDurationMinutes = normalizedDuration;

        if (changesRouteMeaning)
        {
            InvalidateProcurementRouteState("Route settings changed.");
            PublishChange(
                AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
                raiseShoppingListChanged: true);
            return;
        }

        NotifySettingsChanged();
    }

    public bool SetProcurementHomeDataCenterOrigin(bool enabled)
    {
        if (ProcurementStartFromHomeDataCenter == enabled)
        {
            return false;
        }

        ProcurementStartFromHomeDataCenter = enabled;
        InvalidateProcurementRouteState("The route origin changed.");
        PublishChange(
            AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
        return true;
    }

    public bool SetProcurementTravelPriority(MarketTravelPriority priority)
    {
        if (ProcurementTravelPriority == priority)
        {
            return false;
        }

        ProcurementTravelPriority = priority;
        InvalidateProcurementRouteState("The travel priority changed.");
        PublishChange(
            AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
        return true;
    }

    public bool SetMarketAnalysisLens(MarketAcquisitionLens lens)
    {
        if (MarketAnalysisLens == lens)
        {
            return false;
        }

        MarketAnalysisLens = lens;
        InvalidateProcurementRouteState("The market acquisition lens changed.");
        PublishChange(
            AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
        return true;
    }

    public bool SetRecommendationMode(RecommendationMode mode)
    {
        if (RecommendationMode == mode)
        {
            return false;
        }

        RecommendationMode = mode;
        NotifySettingsChanged();
        return true;
    }

    public bool SetMarketSplitSettings(bool enableMultiWorldSplits, int maxWorldsPerItem)
    {
        var normalizedMaxWorlds = Math.Max(0, maxWorldsPerItem);
        if (EnableMultiWorldSplits == enableMultiWorldSplits &&
            MaxWorldsPerItem == normalizedMaxWorlds)
        {
            return false;
        }

        EnableMultiWorldSplits = enableMultiWorldSplits;
        MaxWorldsPerItem = normalizedMaxWorlds;
        NotifySettingsChanged();
        return true;
    }

    public bool SetAutoSaveEnabled(bool enabled)
    {
        if (IsAutoSaveEnabled == enabled)
        {
            return false;
        }

        IsAutoSaveEnabled = enabled;
        NotifySettingsChanged();
        return true;
    }

    public bool SetSecretDebugToolsEnabled(bool enabled)
    {
        if (SecretDebugToolsEnabled == enabled)
        {
            return false;
        }

        SecretDebugToolsEnabled = enabled;
        NotifySettingsChanged();
        return true;
    }

    public bool SetMarketSortPreference(MarketSortOption preference)
    {
        if (MarketSortPreference == preference)
        {
            return false;
        }

        MarketSortPreference = preference;
        NotifySettingsChanged();
        return true;
    }

    public bool SetMarketAnalysisEvidenceOverlay(MarketAnalysisEvidenceOverlay overlay)
    {
        if (MarketAnalysisEvidenceOverlay == overlay)
        {
            return false;
        }

        MarketAnalysisEvidenceOverlay = overlay;
        PublishChange(AppStateChangeScope.MarketAnalysisView);
        return true;
    }

    public void SetUnavailableMarketItems(IReadOnlyList<CoreMarketDataUnavailableItem> items)
    {
        UnavailableMarketItems = items.ToArray();
        if (UnavailableMarketItems.Count > 0 && _marketIntelligenceId == Guid.Empty)
        {
            _marketIntelligenceId = Guid.NewGuid();
        }
        else if (UnavailableMarketItems.Count == 0 &&
                 _marketItemAnalyses.Count == 0 &&
                 _shoppingPlans.Count == 0)
        {
            _marketIntelligenceId = Guid.Empty;
        }

        NotifyShoppingListChanged();
    }

    public void ClearUnavailableMarketItems()
    {
        SetUnavailableMarketItems(Array.Empty<CoreMarketDataUnavailableItem>());
    }

    public void RequestMarketItemAutoExpand(int itemId)
    {
        AutoExpandItemId = itemId;
        ApplyPendingMarketItemAutoExpand();
    }

    public bool ConsumeMarketItemAutoExpand(int itemId)
    {
        if (AutoExpandItemId != itemId)
        {
            return false;
        }

        AutoExpandItemId = null;
        return true;
    }

    public bool ApplyPendingMarketItemAutoExpand()
    {
        return ApplyPendingMarketItemAutoExpand(publishChange: true);
    }

    private bool ApplyPendingMarketItemAutoExpand(bool publishChange)
    {
        if (!AutoExpandItemId.HasValue)
        {
            return false;
        }

        var itemId = AutoExpandItemId.Value;
        if (_shoppingPlans.All(plan => plan.ItemId != itemId))
        {
            return false;
        }

        AutoExpandItemId = null;
        if (SelectedMarketAnalysisItemId == itemId)
        {
            return true;
        }

        SelectedMarketAnalysisItemId = itemId;
        if (publishChange)
        {
            PublishChange(AppStateChangeScope.MarketAnalysisView);
        }

        return true;
    }

    public void SelectMarketAnalysisItem(int? itemId)
    {
        if (SelectedMarketAnalysisItemId == itemId)
        {
            return;
        }

        SelectedMarketAnalysisItemId = itemId;
        PublishChange(AppStateChangeScope.MarketAnalysisView);
    }

    public void ToggleMarketAnalysisWorld(int itemId, string dataCenter, string worldName)
    {
        var key = new MarketAnalysisExpandedWorldKey(itemId, dataCenter, worldName);
        if (!_expandedMarketAnalysisWorlds.Add(key))
        {
            _expandedMarketAnalysisWorlds.Remove(key);
        }

        RefreshMarketAnalysisViewState();
        PublishChange(AppStateChangeScope.MarketAnalysisView);
    }

    public void SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn? column, bool descending)
    {
        if (MarketAnalysisGridSortColumn == column && MarketAnalysisGridSortDescending == descending)
        {
            return;
        }

        MarketAnalysisGridSortColumn = column;
        MarketAnalysisGridSortDescending = column.HasValue && descending;
        PublishChange(AppStateChangeScope.MarketAnalysisView);
    }

    public void SetMarketAnalysisWorldGridSort(MarketAnalysisWorldGridSortColumn? column, bool descending)
    {
        if (MarketAnalysisWorldGridSortColumn == column && MarketAnalysisWorldGridSortDescending == descending)
        {
            return;
        }

        MarketAnalysisWorldGridSortColumn = column;
        MarketAnalysisWorldGridSortDescending = column.HasValue && descending;
        PublishChange(AppStateChangeScope.MarketAnalysisView);
    }

    public void SelectTradeOrder(Guid? orderId)
    {
        if (SelectedTradeOrderId == orderId)
        {
            return;
        }

        SelectedTradeOrderId = orderId;
        PublishChange(AppStateChangeScope.TradeOperationsView);
    }

    public void NotifyTradeOperationsDataChanged()
    {
        PublishChange(AppStateChangeScope.TradeOperationsData);
    }

    public void ClearMarketAnalysisState()
    {
        _shoppingPlans.Clear();
        _marketItemAnalyses.Clear();
        ResetProcurementOverlayState();
        _marketIntelligenceId = Guid.Empty;
        _marketAnalysisRecipeBasis = null;
        _publishedMarketAnalysisScope = null;
        UnavailableMarketItems = Array.Empty<CoreMarketDataUnavailableItem>();
        ClearMarketAnalysisViewState(publishChange: false);
        PublishChange(
            AppStateChangeScope.MarketAnalysis | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
    }

    public void ClearMarketAnalysisViewState()
    {
        ClearMarketAnalysisViewState(publishChange: true);
    }

    public void PruneMarketAnalysisViewState(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        IEnumerable<MarketItemAnalysis> analyses)
    {
        _ = PruneMarketAnalysisViewState(shoppingPlans, analyses, publishChange: true);
    }

    private bool PruneMarketAnalysisViewState(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        IEnumerable<MarketItemAnalysis> analyses,
        bool publishChange)
    {
        ArgumentNullException.ThrowIfNull(shoppingPlans);
        ArgumentNullException.ThrowIfNull(analyses);

        var selectedItemId = SelectedMarketAnalysisItemId;
        var expandedWorldKeys = _expandedMarketAnalysisWorlds.ToHashSet();
        var itemIds = shoppingPlans
            .Select(plan => plan.ItemId)
            .ToHashSet();
        if (SelectedMarketAnalysisItemId.HasValue && !itemIds.Contains(SelectedMarketAnalysisItemId.Value))
        {
            SelectedMarketAnalysisItemId = null;
        }

        var validWorldKeys = analyses
            .SelectMany(analysis => analysis.Worlds.Select(world => new MarketAnalysisExpandedWorldKey(
                analysis.ItemId,
                world.DataCenter,
                world.WorldName)))
            .ToHashSet();
        var prunedWorldKeys = _expandedMarketAnalysisWorlds
            .Where(validWorldKeys.Contains)
            .ToHashSet();
        var changed = selectedItemId != SelectedMarketAnalysisItemId ||
            !expandedWorldKeys.SetEquals(prunedWorldKeys);
        _expandedMarketAnalysisWorlds = prunedWorldKeys;
        RefreshMarketAnalysisViewState();

        if (changed && publishChange)
        {
            PublishChange(AppStateChangeScope.MarketAnalysisView);
        }

        return changed;
    }

    private void ClearMarketAnalysisViewState(bool publishChange)
    {
        var changed = SelectedMarketAnalysisItemId.HasValue ||
            _expandedMarketAnalysisWorlds.Count > 0 ||
            MarketAnalysisGridSortColumn.HasValue ||
            MarketAnalysisGridSortDescending ||
            MarketAnalysisWorldGridSortColumn.HasValue ||
            MarketAnalysisWorldGridSortDescending;
        SelectedMarketAnalysisItemId = null;
        _expandedMarketAnalysisWorlds.Clear();
        MarketAnalysisGridSortColumn = null;
        MarketAnalysisGridSortDescending = false;
        MarketAnalysisWorldGridSortColumn = null;
        MarketAnalysisWorldGridSortDescending = false;
        RefreshMarketAnalysisViewState();

        if (changed && publishChange)
        {
            PublishChange(AppStateChangeScope.MarketAnalysisView);
        }
    }

    public void BlacklistMarketWorldTemporarily(MarketWorldKey world)
    {
        var duration = TimeSpan.FromMinutes(Math.Max(1, TemporaryWorldBlacklistDurationMinutes));
        TemporaryMarketWorldBlacklist.Add(world, duration);
        SyncTemporaryBlacklistSets();
        InvalidateProcurementRoute($"{world.WorldName} was temporarily excluded.");
    }

    public void ExcludeItemWorldTemporarily(int itemId, MarketWorldKey world)
    {
        _temporarilyExcludedItemWorlds.Add(new MarketItemWorldKey(itemId, world));
        RefreshTemporaryExclusionViews();
        InvalidateProcurementRoute($"{world.WorldName} was excluded for one item.");
    }

    public bool RemoveTemporaryMarketWorldBlacklist(MarketWorldKey world)
    {
        if (!TemporaryMarketWorldBlacklist.Remove(world))
        {
            return false;
        }

        SyncTemporaryBlacklistSets();
        InvalidateProcurementRoute($"The exclusion for {world.WorldName} was removed.");
        return true;
    }

    public bool RemoveTemporaryItemWorldExclusion(int itemId, MarketWorldKey world)
    {
        if (!_temporarilyExcludedItemWorlds.Remove(new MarketItemWorldKey(itemId, world)))
        {
            return false;
        }

        RefreshTemporaryExclusionViews();
        InvalidateProcurementRoute($"The item exclusion for {world.WorldName} was removed.");
        return true;
    }

    public int ActiveTemporaryExclusionCount =>
        GetActiveBlacklistedMarketWorlds().Count + _temporarilyExcludedItemWorlds.Count;

    public HashSet<MarketWorldKey> GetActiveBlacklistedMarketWorlds()
    {
        SyncTemporaryBlacklistSets();
        return _temporarilyBlacklistedMarketWorlds.ToHashSet();
    }

    public HashSet<string> GetActiveBlacklistedWorldNames()
    {
        SyncTemporaryBlacklistSets();
        return _temporarilyBlacklistedWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetExpectedMarketWorlds(MarketFetchScope scope)
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            scope,
            SelectedDataCenter,
            SelectedRegion);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataCenter in dataCenters)
        {
            if (WorldData?.DataCenterToWorlds.TryGetValue(dataCenter, out var worlds) == true)
            {
                result[dataCenter] = worlds;
            }
        }

        return result;
    }

    public PublishedMarketAnalysisScopeSnapshot CreateCurrentMarketAnalysisScopeSnapshot(DateTime? publishedAtUtc = null)
    {
        var scope = SearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        return CreateMarketAnalysisScopeSnapshot(scope, publishedAtUtc);
    }

    public PublishedMarketAnalysisScopeSnapshot CreateMarketAnalysisScopeSnapshot(
        MarketFetchScope scope,
        DateTime? publishedAtUtc = null)
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            scope,
            SelectedDataCenter,
            SelectedRegion);

        return new PublishedMarketAnalysisScopeSnapshot(
            scope,
            SelectedDataCenter,
            SelectedRegion,
            dataCenters.ToArray(),
            MarketAnalysisLens,
            PlanSessionVersion,
            publishedAtUtc ?? DateTime.UtcNow);
    }

    private MarketIntelligence CreateMarketIntelligence()
    {
        if (_marketItemAnalyses.Count == 0 &&
            _shoppingPlans.Count == 0 &&
            UnavailableMarketItems.Count == 0)
        {
            return MarketIntelligence.Empty;
        }

        var context = _publishedMarketAnalysisScope != null
            ? ToMarketIntelligencePublicationContext(_publishedMarketAnalysisScope)
            : MarketIntelligencePublicationContext.UnknownLegacy(RecommendationMode, MarketAnalysisLens);

        return new MarketIntelligence(
            _marketIntelligenceId == Guid.Empty ? Guid.NewGuid() : _marketIntelligenceId,
            _marketItemAnalyses.ToArray(),
            _shoppingPlans.ToArray(),
            UnavailableMarketItems.ToArray(),
            context,
            CloneRecipeBasis(_marketAnalysisRecipeBasis));
    }

    private MarketIntelligencePublicationContext ToMarketIntelligencePublicationContext(
        PublishedMarketAnalysisScopeSnapshot scope)
    {
        return new MarketIntelligencePublicationContext(
            MarketIntelligencePublicationContextKind.Known,
            scope.Scope,
            scope.SelectedDataCenter,
            scope.SelectedRegion,
            scope.RequestedDataCenters.ToArray(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            RecommendationMode,
            scope.Lens,
            null,
            scope.PlanSessionVersion,
            _marketAnalysisVersion,
            scope.PublishedAtUtc);
    }

    private string? GetMarketAnalysisScopeWarning()
    {
        if (!_marketItemAnalyses.Any() && !_shoppingPlans.Any())
        {
            return null;
        }

        if (_publishedMarketAnalysisScope == null)
        {
            return "Analysis shown for an unknown saved scope. Rerun analysis to confirm current scope.";
        }

        var currentScope = CreateCurrentMarketAnalysisScopeSnapshot(_publishedMarketAnalysisScope.PublishedAtUtc);
        if (ScopesMatch(_publishedMarketAnalysisScope, currentScope))
        {
            return null;
        }

        return $"Analysis shown for {DescribeScope(_publishedMarketAnalysisScope)}. Current scope is {DescribeScope(currentScope)}.";
    }

    private static bool ScopesMatch(PublishedMarketAnalysisScopeSnapshot left, PublishedMarketAnalysisScopeSnapshot right)
    {
        return left.Scope == right.Scope &&
               left.Lens == right.Lens &&
               string.Equals(left.SelectedDataCenter, right.SelectedDataCenter, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.SelectedRegion, right.SelectedRegion, StringComparison.OrdinalIgnoreCase) &&
               left.RequestedDataCenters.Order(StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(right.RequestedDataCenters.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private static string DescribeScope(PublishedMarketAnalysisScopeSnapshot scope)
    {
        return scope.Scope == MarketFetchScope.EntireRegion
            ? $"Entire Region: {scope.SelectedRegion} ({string.Join(", ", scope.RequestedDataCenters)})"
            : $"Selected Data Center: {scope.SelectedDataCenter}";
    }

    public void ClearTemporaryMarketWorldBlacklists()
    {
        TemporaryMarketWorldBlacklist.Clear();
        _temporarilyBlacklistedMarketWorlds.Clear();
        _temporarilyBlacklistedWorlds.Clear();
        _temporarilyExcludedItemWorlds.Clear();
        RefreshTemporaryExclusionViews();
        InvalidateProcurementRoute("Temporary exclusions were cleared.");
    }

    public bool PruneExpiredTemporaryMarketWorldBlacklists()
    {
        var previousCount = _temporarilyBlacklistedMarketWorlds.Count;
        SyncTemporaryBlacklistSets();
        if (_temporarilyBlacklistedMarketWorlds.Count == previousCount)
        {
            return false;
        }

        InvalidateProcurementRoute("A temporary world exclusion expired.");
        return true;
    }

    private static void ReplaceListContents<T>(List<T> target, IEnumerable<T> items)
    {
        target.Clear();
        target.AddRange(items);
    }

    private static ProjectItem CloneProjectItem(ProjectItem item)
    {
        return new ProjectItem
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };
    }

    private static StoredRecipeOperationSnapshot? CloneRecipeBasis(StoredRecipeOperationSnapshot? recipeBasis)
    {
        return recipeBasis == null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<StoredRecipeOperationSnapshot>(
                System.Text.Json.JsonSerializer.Serialize(recipeBasis));
    }

    /// <summary>
    /// Update progress of current operation (0-100).
    /// </summary>
    private void SyncTemporaryBlacklistSets()
    {
        _temporarilyBlacklistedMarketWorlds = TemporaryMarketWorldBlacklist.GetActiveWorlds();
        _temporarilyBlacklistedWorlds = _temporarilyBlacklistedMarketWorlds
            .Select(world => world.WorldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RefreshTemporaryExclusionViews();
    }

    private void RefreshTemporaryExclusionViews()
    {
        _temporarilyBlacklistedWorldsView = _temporarilyBlacklistedWorlds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _temporarilyBlacklistedMarketWorldsView = _temporarilyBlacklistedMarketWorlds.ToFrozenSet();
        _temporarilyExcludedItemWorldsView = _temporarilyExcludedItemWorlds.ToFrozenSet();
    }

    private void RefreshMarketAnalysisViewState()
    {
        _expandedMarketAnalysisWorldsView = _expandedMarketAnalysisWorlds.ToFrozenSet();
    }

    private static bool IsDirtyVersion(long currentVersion, long persistedVersion, bool hasPersistableData)
    {
        return currentVersion != persistedVersion &&
               (persistedVersion >= 0 || currentVersion > 0 || hasPersistableData);
    }

}
