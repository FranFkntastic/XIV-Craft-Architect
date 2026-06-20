using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeOrderPricingEvidenceResult(
    bool Succeeded,
    IReadOnlyList<TradeOrderMaterialSnapshot> Materials,
    IReadOnlyList<string> Warnings,
    int RefreshedEvidenceCount,
    string? FailureReason)
{
    public static TradeOrderPricingEvidenceResult Unavailable(string reason)
    {
        return new TradeOrderPricingEvidenceResult(false, [], [], 0, reason);
    }

    public static TradeOrderPricingEvidenceResult Available(
        IReadOnlyList<TradeOrderMaterialSnapshot> materials,
        IReadOnlyList<string> warnings,
        int refreshedEvidenceCount)
    {
        return new TradeOrderPricingEvidenceResult(true, materials, warnings, refreshedEvidenceCount, null);
    }
}

public sealed class TradeOrderPricingEvidenceService
{
    private readonly AppState _appState;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly IProcurementRouteExecutionService _procurementRouteExecution;
    private readonly CommissionCostBasisResolver _costBasisResolver;

    public TradeOrderPricingEvidenceService(
        AppState appState,
        WebPlanPersistenceService planPersistence,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        IProcurementRouteExecutionService procurementRouteExecution,
        CommissionCostBasisResolver costBasisResolver)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _planPersistence = planPersistence ?? throw new ArgumentNullException(nameof(planPersistence));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
        _procurementRouteExecution = procurementRouteExecution ?? throw new ArgumentNullException(nameof(procurementRouteExecution));
        _costBasisResolver = costBasisResolver ?? throw new ArgumentNullException(nameof(costBasisResolver));
    }

    public async Task<TradeOrderPricingEvidenceResult> RefreshAsync(
        TradeOrder order,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            return TradeOrderPricingEvidenceResult.Unavailable("Create a linked craft plan before refreshing pricing evidence.");
        }

        var storedPlan = await _planPersistence.LoadPlanPayloadAsync(order.CraftPlanId);
        if (storedPlan == null)
        {
            return TradeOrderPricingEvidenceResult.Unavailable("Linked Craft Architect plan could not be loaded.");
        }

        var loaded = PlanSessionLoadService.Prepare(storedPlan);
        if (loaded.Plan == null)
        {
            return TradeOrderPricingEvidenceResult.Unavailable(
                loaded.Warning ?? "Linked Craft Architect plan could not be prepared for pricing.");
        }

        var activeItems = _recipeLayerWorkflow
            .BuildActiveProcurementItems(loaded.Plan)
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        if (activeItems.Length == 0)
        {
            return TradeOrderPricingEvidenceResult.Unavailable("The linked craft plan does not have active procurement items to price.");
        }

        var selectedDataCenter = GetSelectedDataCenter(order, storedPlan, loaded.Plan);
        if (string.IsNullOrWhiteSpace(selectedDataCenter))
        {
            return TradeOrderPricingEvidenceResult.Unavailable("Select a data center before refreshing pricing evidence.");
        }

        var scope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var selectedRegion = MarketFetchScopeResolver.ResolveRegionForDataCenter(
            selectedDataCenter,
            _appState.SelectedRegion);
        var routeResult = await _procurementRouteExecution.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = loaded.Plan,
                ActiveProcurementItems = activeItems,
                SourceShoppingPlans = loaded.ShoppingPlans,
                Scope = scope,
                SelectedDataCenter = selectedDataCenter,
                SelectedRegion = selectedRegion,
                Lens = _appState.MarketAnalysisLens,
                ProcurementConfig = CreateProcurementMarketConfig(),
                IncludeSplitPurchases = _appState.ProcurementEnableSplitWorldPurchases,
                BlacklistedWorlds = _appState.GetActiveBlacklistedMarketWorlds(),
                ExcludedItemWorlds = _appState.TemporarilyExcludedItemWorlds,
                ExpectedWorldsByDataCenter = GetExpectedMarketWorlds(scope, selectedDataCenter, selectedRegion)
            },
            progress,
            ct);
        ct.ThrowIfCancellationRequested();

        var lines = _costBasisResolver.BuildMarketRecommendationLines(
            activeItems,
            loaded.MarketItemAnalyses,
            routeResult.ShoppingPlans);
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(lines);
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(loaded.Warning))
        {
            warnings.Add(loaded.Warning);
        }

        warnings.AddRange(lines.SelectMany(line => line.Warnings));
        var pricedItemCount = materials.Count(material => material.UnitCost > 0 && material.TotalCost > 0);
        if (pricedItemCount < activeItems.Length)
        {
            warnings.Add($"Pricing evidence is incomplete: {pricedItemCount:N0} of {activeItems.Length:N0} active procurement items are priced.");
        }

        return TradeOrderPricingEvidenceResult.Available(
            materials,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase).ToArray(),
            routeResult.RefreshedEvidence.Count);
    }

    private static string GetSelectedDataCenter(
        TradeOrder order,
        StoredPlan storedPlan,
        CraftingPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(storedPlan.DataCenter))
        {
            return storedPlan.DataCenter;
        }

        if (!string.IsNullOrWhiteSpace(order.SourceSnapshot?.DataCenter))
        {
            return order.SourceSnapshot.DataCenter;
        }

        return plan.DataCenter;
    }

    private MarketAnalysisConfig CreateProcurementMarketConfig()
    {
        return new MarketAnalysisConfig
        {
            EnableSplitWorld = _appState.ProcurementEnableSplitWorldPurchases,
            MaxWorldsPerItem = null,
            TravelTolerance = _appState.ProcurementTravelTolerance
        };
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> GetExpectedMarketWorlds(
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion)
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            scope,
            selectedDataCenter,
            selectedRegion);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataCenter in dataCenters)
        {
            if (_appState.WorldData?.DataCenterToWorlds.TryGetValue(dataCenter, out var worlds) == true)
            {
                result[dataCenter] = worlds;
            }
        }

        return result;
    }
}
