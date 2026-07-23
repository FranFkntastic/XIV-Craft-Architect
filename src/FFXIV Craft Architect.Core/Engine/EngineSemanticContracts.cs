using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Engine;

public sealed record EngineRootIntentSnapshot(
    string SchemaVersion,
    EngineInputKind InputKind,
    IReadOnlyList<EngineDemandSnapshot> Demands,
    string DeterministicSettingsHash);

public sealed record EngineDemandSnapshot(int ItemId, int Quantity, bool RequiresHq);

public sealed record EngineExpandedGraphSnapshot(
    string SchemaVersion,
    IReadOnlyList<EngineGraphNodeSnapshot> Nodes,
    IReadOnlyList<EngineGraphEdgeSnapshot> Edges);

public sealed record EngineGraphNodeSnapshot(
    string StableNodeId,
    int ItemId,
    int Quantity,
    int? RecipeId,
    string AcquisitionSource);

public sealed record EngineGraphEdgeSnapshot(
    string ParentNodeId,
    string ChildNodeId,
    int ChildOrder,
    int Quantity);

public sealed record EngineAnalysisSemanticSnapshot(
    string SchemaVersion,
    IReadOnlyList<EngineAnalysisItemSnapshot> Items,
    IReadOnlyList<EngineRouteItemSnapshot> ShoppingPlans);

public sealed record EngineAnalysisItemSnapshot(
    int ItemId,
    int QuantityNeeded,
    MarketFetchScope Scope,
    decimal BaselineUnitPrice,
    decimal AverageUnitPrice,
    decimal MedianUnitPrice,
    decimal CompetitiveThresholdUnitPrice,
    decimal SaneThresholdUnitPrice,
    MarketDataQualityBucket WorstDataQualityBucket,
    IReadOnlyList<string> RequestedDataCenters,
    IReadOnlyList<string> PresentDataCenters,
    IReadOnlyList<string> MissingDataCenters,
    IReadOnlyList<EngineWorldAnalysisSnapshot> RankedWorlds);

public sealed record EngineWorldAnalysisSnapshot(
    int Rank,
    string DataCenter,
    string WorldName,
    int ActionableQuantity,
    long CostToCoverTotalGil,
    decimal CostToCoverUnitPrice,
    MarketCoverageBucket CoverageBucket,
    MarketDataQualityBucket DataQualityBucket,
    MarketDataAgeSource DataAgeSource,
    long? UpstreamUploadUnixMilliseconds,
    decimal DataQualityScore);

public sealed record EngineRouteSemanticSnapshot(
    string SchemaVersion,
    IReadOnlyList<EngineRouteStopSnapshot> OrderedStops,
    IReadOnlyList<EngineRouteItemSnapshot> OrderedItems,
    long SelectedGilCost,
    int SelectedWorldStops,
    int SelectedDataCenterTransfers,
    bool IsComplete,
    EngineRouteDecisionSnapshot? Decision,
    string OptimizedPlanHash = "",
    string ActiveProcurementItemsHash = "",
    string EvidenceAnalysesHash = "",
    string EvidencePlansHash = "",
    string AcquisitionDecisionsHash = "",
    string ShoppingPlansHash = "");

public sealed record EngineRouteStopSnapshot(
    int Order,
    string DataCenter,
    string World,
    IReadOnlyList<int> OrderedItemIds);

public sealed record EngineRouteItemSnapshot(
    int Order,
    int ItemId,
    int Quantity,
    long TotalGil,
    IReadOnlyList<EngineRouteLegSnapshot> SelectedLegs,
    string? Error,
    string? MarketDataWarning);

public sealed record EngineRouteLegSnapshot(
    int Order,
    string DataCenter,
    string World,
    int Quantity,
    long TotalGil,
    string AcquisitionSource,
    MarketDataQualityBucket DataQualityBucket,
    MarketDataAgeSource DataAgeSource,
    long? UpstreamUploadUnixMilliseconds);

public sealed record EngineRouteDecisionSnapshot(
    int TravelTolerance,
    decimal? MaximumPremiumRate,
    long CheapestGilCost,
    long SelectedGilCost,
    long SelectedEvidencePenalty,
    int CheapestWorldStops,
    int SelectedWorldStops,
    int CheapestDataCenterTransfers,
    int SelectedDataCenterTransfers,
    bool StartsFromHomeDataCenter,
    string? HomeDataCenter,
    MarketTravelPriority TravelPriority,
    long FixedAcquisitionGilCost,
    bool AcquisitionSearchWasTruncated,
    long AcquisitionCombinationEvaluations,
    bool RouteSearchWasTruncated,
    bool TravelSearchWasTruncated,
    int TravelRoutesEvaluated,
    IReadOnlyList<EngineRouteFrontierOptionSnapshot> FrontierOptions,
    IReadOnlyList<EngineRouteItemDecisionSnapshot> ItemDecisions,
    string ToleranceSelectionsHash = "");

public sealed record EngineRouteFrontierOptionSnapshot(
    int Order,
    int MinimumTolerance,
    int MaximumTolerance,
    long GilCost,
    int WorldStops,
    int DataCenterTransfers);

public sealed record EngineRouteItemDecisionSnapshot(
    int Order,
    int ItemId,
    string ItemName,
    long CheapestEligibleGilCost,
    long SelectedGilCost);

public sealed record ReferenceEnginePreparedInput(
    ReferenceEngineInput Input,
    EngineRootIntentSnapshot RootIntent,
    EngineExpandedGraphSnapshot ExpandedGraph);

public sealed record ReferenceEngineResultSnapshot(
    EngineAnalysisSemanticSnapshot? MarketAnalysis,
    EngineRouteSemanticSnapshot? ProcurementRoute,
    ProcurementRouteExecutionResult? ProcurementRouteResult = null);

public interface IReferenceEngineSemanticSnapshotProvider
{
    ReferenceEnginePreparedInput PrepareInput(EngineRequestEnvelope request);

    EngineAnalysisSemanticSnapshot CaptureAnalysis(MarketAnalysisExecutionResult result);

    EngineRouteSemanticSnapshot CaptureRoute(ProcurementRouteExecutionResult result);

    ReferenceEngineResultSnapshot CaptureTransportedResult(JsonElement result);
}

public static class EngineSemanticSnapshotHash
{
    public static string RootIntent(EngineRootIntentSnapshot snapshot) =>
        EngineCanonicalHash.Compute(new { Domain = "root-intent-v1", Snapshot = snapshot });

    public static string ExpandedGraph(EngineExpandedGraphSnapshot snapshot) =>
        EngineCanonicalHash.Compute(new { Domain = "expanded-graph-v1", Snapshot = snapshot });

    public static string Analysis(EngineAnalysisSemanticSnapshot snapshot) =>
        EngineCanonicalHash.Compute(new { Domain = "analysis-result-v1", Snapshot = snapshot });

    public static string Route(EngineRouteSemanticSnapshot snapshot) =>
        EngineCanonicalHash.Compute(new { Domain = "route-result-v3", Snapshot = snapshot });
}
