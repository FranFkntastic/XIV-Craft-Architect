using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Engine;

public sealed class ReferenceEngineSemanticSnapshotProvider : IReferenceEngineSemanticSnapshotProvider
{
    private const string SchemaVersion = "1";
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
            SchemaVersion,
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
        var graph = new EngineExpandedGraphSnapshot(SchemaVersion, nodes, []);
        return new ReferenceEnginePreparedInput(input, root, graph);
    }

    public EngineAnalysisSemanticSnapshot CaptureAnalysis(MarketAnalysisExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new EngineAnalysisSemanticSnapshot(
            SchemaVersion,
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
                        world.MarketUploadedAtUtc is { } upload
                            ? new DateTimeOffset(upload).ToUnixTimeMilliseconds()
                            : null,
                        world.DataQualityScore)).ToArray()))
                .ToArray());
    }

    public EngineRouteSemanticSnapshot CaptureRoute(ProcurementRouteExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var orderedItems = result.ShoppingPlans.Select((plan, order) =>
        {
            var world = plan.RecommendedWorld;
            return new EngineRouteItemSnapshot(
                order,
                plan.ItemId,
                plan.QuantityNeeded,
                world?.TotalCost ?? 0,
                world?.WorldName ?? "missing",
                world?.MarketDataQualityBucket ?? MarketDataQualityBucket.Missing,
                world?.MarketDataAgeSource ?? MarketDataAgeSource.Missing,
                world?.MarketUploadedAtUtc is { } upload
                    ? new DateTimeOffset(upload).ToUnixTimeMilliseconds()
                    : null);
        }).ToArray();
        var stops = result.ShoppingPlans
            .Where(plan => plan.RecommendedWorld is not null)
            .GroupBy(plan => (plan.RecommendedWorld!.DataCenter, plan.RecommendedWorld.WorldName))
            .Select((group, order) => new EngineRouteStopSnapshot(
                order,
                group.Key.DataCenter,
                group.Key.WorldName,
                group.Select(plan => plan.ItemId).ToArray()))
            .ToArray();
        return new EngineRouteSemanticSnapshot(
            SchemaVersion,
            stops,
            orderedItems,
            result.RouteDecision?.SelectedGilCost ?? orderedItems.Sum(item => item.TotalGil),
            result.RouteDecision?.SelectedWorldStops ?? stops.Length,
            result.RouteDecision?.SelectedDataCenterTransfers ?? 0,
            result.MissingItems.Count == 0);
    }
}
