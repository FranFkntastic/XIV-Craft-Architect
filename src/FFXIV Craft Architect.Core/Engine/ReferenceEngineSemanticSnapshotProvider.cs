using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Engine;

public sealed class ReferenceEngineSemanticSnapshotProvider : IReferenceEngineSemanticSnapshotProvider
{
    private const string InputSchemaVersion = "1";
    private const string OutputSchemaVersion = "10";
    private static readonly JsonSerializerOptions InputJsonOptions = EngineJsonSerializerOptions.CreateWire();

    public ReferenceEnginePreparedInput PrepareInput(EngineRequestEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input.Deserialize<ReferenceEngineInput>(InputJsonOptions)
            ?? throw new InvalidOperationException("Unsupported reference input.");
        var demands = (input.MarketAnalysis?.Items ?? [])
            .Concat(input.ProcurementRoute?.ActiveProcurementItems ?? [])
            .GroupBy(item => item.ItemId)
            .OrderBy(group => group.Key)
            .Select(group => new EngineDemandSnapshot(
                group.Key,
                group.Sum(item => item.TotalQuantity),
                group.Any(item => item.RequiresHq)))
            .ToArray();
        var root = new EngineRootIntentSnapshot(
            InputSchemaVersion,
            request.InputKind,
            demands,
            EngineCanonicalHash.Compute(request.Settings));
        var nodes = demands
            .Select(demand => new EngineGraphNodeSnapshot(
                $"item:{demand.ItemId}",
                demand.ItemId,
                demand.Quantity,
                1000 + demand.ItemId,
                "market"))
            .ToArray();
        var graph = new EngineExpandedGraphSnapshot(InputSchemaVersion, nodes, []);
        return new ReferenceEnginePreparedInput(input, root, graph);
    }

    public EngineAnalysisSemanticSnapshot CaptureAnalysis(MarketAnalysisExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new EngineAnalysisSemanticSnapshot(
            OutputSchemaVersion,
            result.Analyses
                .OrderBy(item => item.ItemId)
                .Select(item => new EngineAnalysisItemSnapshot(
                    item.ItemId,
                    item.QuantityNeeded,
                    item.Scope,
                    item.AnalysisScopeBaselineUnitPrice,
                    item.AnalysisScopeAverageUnitPrice,
                    item.AnalysisScopeMedianUnitPrice,
                    item.CompetitiveThresholdUnitPrice,
                    item.SaneThresholdUnitPrice,
                    item.WorstDataQualityBucket,
                    item.RequestedDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.PresentDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.MissingDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.Worlds.Select((world, rank) => new EngineWorldAnalysisSnapshot(
                        rank,
                        world.DataCenter,
                        world.WorldName,
                        world.ActionableQuantity,
                        world.CostToCoverTotalGil,
                        world.CostToCoverUnitPrice,
                        world.CoverageBucket,
                        world.DataQualityBucket,
                        world.DataAgeSource,
                        ToUnixTimeMilliseconds(world.MarketUploadedAtUtc),
                        world.DataQualityScore)).ToArray()))
                .ToArray(),
            CaptureShoppingPlans(result.ShoppingPlans));
    }

    public EngineRouteSemanticSnapshot CaptureRoute(ProcurementRouteExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var orderedItems = CaptureShoppingPlans(result.ShoppingPlans);
        var stops = orderedItems
            .SelectMany(item => item.SelectedLegs.Select(leg => new { item.ItemId, Leg = leg }))
            .GroupBy(item => (item.Leg.DataCenter, item.Leg.World))
            .Select((group, order) => new EngineRouteStopSnapshot(
                order,
                group.Key.DataCenter,
                group.Key.World,
                group.Select(item => item.ItemId).Distinct().ToArray()))
            .ToArray();
        return new EngineRouteSemanticSnapshot(
            OutputSchemaVersion,
            stops,
            orderedItems,
            result.RouteDecision?.SelectedGilCost ?? orderedItems.Sum(item => item.TotalGil),
            result.RouteDecision?.SelectedWorldStops ?? stops.Length,
            result.RouteDecision?.SelectedDataCenterTransfers ?? 0,
            result.IsComplete,
            CaptureRouteDecision(result.RouteDecision),
            EngineCanonicalHash.Compute(result.OptimizedPlan, InputJsonOptions),
            EngineCanonicalHash.Compute(result.ActiveProcurementItems, InputJsonOptions),
            EngineCanonicalHash.Compute(result.EvidenceAnalyses, InputJsonOptions),
            EngineCanonicalHash.Compute(result.EvidencePlans, InputJsonOptions),
            EngineCanonicalHash.Compute(
                new { Domain = "route-acquisition-decisions-v2", Value = result.AcquisitionDecisions },
                InputJsonOptions),
            EngineCanonicalHash.Compute(
                new { Domain = "route-shopping-plans-v2", Value = result.ShoppingPlans },
                InputJsonOptions));
    }

    public ReferenceEngineResultSnapshot CaptureTransportedResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The transported engine result must be an object.");
        }

        return result.Deserialize<ReferenceEngineResultSnapshot>(InputJsonOptions)
            ?? throw new InvalidOperationException("The transported engine result is empty.");
    }

    private static EngineRouteItemSnapshot[] CaptureShoppingPlans(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans) =>
        shoppingPlans
            .Select((plan, order) =>
            {
                var legs = CaptureSelectedLegs(plan);
                return new EngineRouteItemSnapshot(
                    order,
                    plan.ItemId,
                    plan.QuantityNeeded,
                    legs.Sum(leg => leg.TotalGil),
                    legs,
                    plan.Error,
                    plan.MarketDataWarning);
            })
            .ToArray();

    private static EngineRouteLegSnapshot[] CaptureSelectedLegs(DetailedShoppingPlan plan)
    {
        if (plan.RecommendedWorld is { } world)
        {
            return
            [
                CreateLeg(
                    0,
                    world.DataCenter,
                    world.WorldName,
                    plan.QuantityNeeded,
                    world.TotalCost,
                    world.WorldName == MarketShoppingConstants.VendorWorldName ? "vendor" : "market-single",
                    world)
            ];
        }

        if (plan.RecommendedSplit?.Count > 0)
        {
            return plan.RecommendedSplit
                .Select((split, order) => CreateLeg(
                    order,
                    split.DataCenter,
                    split.WorldName,
                    split.QuantityToBuy,
                    split.TotalCost,
                    "market-split",
                    FindWorld(plan, split.DataCenter, split.WorldName)))
                .ToArray();
        }

        var coverage = GetSelectedCoverage(plan);
        return coverage?.Worlds
            .Select((world, order) => CreateLeg(
                order,
                world.DataCenter,
                world.WorldName,
                world.QuantityToPurchase,
                ToLongSaturating(world.CashOutCost),
                "market-coverage",
                FindWorld(plan, world.DataCenter, world.WorldName)))
            .ToArray() ?? [];
    }

    private static EngineRouteDecisionSnapshot? CaptureRouteDecision(MarketRouteDecision? decision) =>
        decision is null
            ? null
            : new EngineRouteDecisionSnapshot(
                decision.TravelTolerance,
                decision.MaximumPremiumRate,
                decision.CheapestGilCost,
                decision.SelectedGilCost,
                decision.SelectedEvidencePenalty,
                decision.CheapestWorldStops,
                decision.SelectedWorldStops,
                decision.CheapestDataCenterTransfers,
                decision.SelectedDataCenterTransfers,
                decision.StartsFromHomeDataCenter,
                decision.HomeDataCenter,
                decision.TravelPriority,
                decision.FixedAcquisitionGilCost,
                decision.AcquisitionSearchWasTruncated,
                decision.AcquisitionCombinationEvaluations,
                decision.RouteSearchWasTruncated,
                decision.TravelSearchWasTruncated,
                decision.TravelRoutesEvaluated,
                decision.RepresentativeRoutes
                    .Select((option, order) => new EngineRouteFrontierOptionSnapshot(
                        order,
                        option.MinimumTolerance,
                        option.MaximumTolerance,
                        option.GilCost,
                        option.WorldStops,
                        option.DataCenterTransfers))
                    .ToArray(),
                decision.ItemPremiums
                    .Select((item, order) => new EngineRouteItemDecisionSnapshot(
                        order,
                        item.ItemId,
                        item.ItemName,
                        item.CheapestEligibleGilCost,
                        item.SelectedGilCost))
                    .ToArray(),
                EngineCanonicalHash.Compute(
                    new { Domain = "route-tolerance-selections-v1", Value = decision.ToleranceSelections },
                    InputJsonOptions));

    private static EngineRouteLegSnapshot CreateLeg(
        int order,
        string dataCenter,
        string world,
        int quantity,
        long totalGil,
        string acquisitionSource,
        WorldShoppingSummary? evidence) =>
        new(
            order,
            dataCenter,
            world,
            quantity,
            totalGil,
            acquisitionSource,
            evidence?.MarketDataQualityBucket ?? MarketDataQualityBucket.Missing,
            evidence?.MarketDataAgeSource ?? MarketDataAgeSource.Missing,
            ToUnixTimeMilliseconds(evidence?.MarketUploadedAtUtc));

    private static MarketCoverageOption? GetSelectedCoverage(DetailedShoppingPlan plan) =>
        plan.CoverageSet?.AllCandidates.FirstOrDefault(candidate => candidate.IsDefaultEligible)
        ?? plan.CoverageSet?.SingleWorld
        ?? plan.CoverageSet?.CompactSplit
        ?? plan.CoverageSet?.WideSplit
        ?? plan.CoverageSet?.CheapestObserved;

    private static WorldShoppingSummary? FindWorld(
        DetailedShoppingPlan plan,
        string dataCenter,
        string world) =>
        plan.WorldOptions.FirstOrDefault(option =>
            string.Equals(option.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.WorldName, world, StringComparison.OrdinalIgnoreCase));

    private static long? ToUnixTimeMilliseconds(DateTime? timestamp) =>
        timestamp is { } value
            ? new DateTimeOffset(value).ToUnixTimeMilliseconds()
            : null;

    private static long ToLongSaturating(decimal value)
    {
        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }
        if (value <= long.MinValue)
        {
            return long.MinValue;
        }
        return decimal.ToInt64(value);
    }
}
