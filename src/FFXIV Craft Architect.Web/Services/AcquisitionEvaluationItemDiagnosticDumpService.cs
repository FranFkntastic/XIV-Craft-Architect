using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class AcquisitionEvaluationItemDiagnosticDumpService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static AcquisitionEvaluationItemDiagnosticDumpService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public AcquisitionEvaluationItemDiagnosticDump BuildDump(
        AppState appState,
        DecisionRow row,
        DetailedShoppingPlan? resolvedMarketPlan,
        MarketEvidenceHydrationRunSnapshot automaticRefresh,
        MarketCacheDecisionSnapshot? cacheDecision,
        DateTime exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(automaticRefresh);

        resolvedMarketPlan ??= appState.ShoppingPlans.FirstOrDefault(plan => plan.ItemId == row.ItemId);
        var quantity = AcquisitionEvaluationCostCalculator.GetCostQuantity(row);
        var nqEstimate = MarketPurchaseCostProjectionService.Estimate(
            resolvedMarketPlan,
            quantity,
            hqOnly: false,
            includeVendor: false);
        var hqEstimate = MarketPurchaseCostProjectionService.Estimate(
            resolvedMarketPlan,
            quantity,
            hqOnly: true,
            includeVendor: false);

        return new AcquisitionEvaluationItemDiagnosticDump(
            SchemaVersion: 1,
            Tool: "acquisition-evaluation-item-diagnostic-dump",
            ExportedAtUtc: exportedAtUtc,
            Build: new DiagnosticSnapshotBuildInfo(
                WebBuildInfo.BuildVersion,
                WebBuildInfo.BranchName,
                WebBuildInfo.CommitSha,
                WebBuildInfo.IsDirty),
            Context: new AcquisitionEvaluationItemDiagnosticContext(
                appState.CurrentPlanId,
                appState.CurrentPlanName ?? appState.CurrentPlan?.Name,
                appState.SelectedDataCenter,
                appState.SelectedRegion,
                appState.SearchEntireRegion,
                appState.MarketAnalysisLens,
                appState.PlanSessionVersion,
                appState.CurrentVersions,
                appState.PublishedMarketAnalysisScope,
                appState.IsMarketEvidenceHydrating),
            Item: new AcquisitionEvaluationDiagnosticItem(
                row.NodeId,
                row.ItemId,
                row.ItemName,
                row.Source,
                row.SourceReason,
                row.MustBeHq,
                row.CanBeHq,
                row.CanBuyFromMarket,
                row.TotalQuantity,
                row.ActiveQuantity,
                row.UnitPrice,
                row.HqUnitPrice,
                row.MarketEvidence,
                row.EstimatedCost,
                row.IsActiveProcurement,
                row.IsFullySuppressed),
            Actionability: new AcquisitionEvaluationItemActionability(
                CreateProjection("NQ", applicable: !row.MustBeHq, nqEstimate, resolvedMarketPlan),
                CreateProjection("HQ", applicable: row.CanBeHq, hqEstimate, resolvedMarketPlan)),
            ShoppingPlans: appState.ShoppingPlans.Where(plan => plan.ItemId == row.ItemId).ToList(),
            MarketAnalyses: appState.MarketItemAnalyses.Where(analysis => analysis.ItemId == row.ItemId).ToList(),
            UnavailableMarketItems: appState.UnavailableMarketItems.Where(item => item.ItemId == row.ItemId).ToList(),
            AutomaticRefresh: automaticRefresh,
            CacheDecision: cacheDecision);
    }

    public string Serialize(AcquisitionEvaluationItemDiagnosticDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    public static string CreateFileName(string itemName, int itemId, DateTime exportedAtUtc)
    {
        var safeName = string.Join("_", itemName.Split(Path.GetInvalidFileNameChars()))
            .Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "item";
        }

        if (safeName.Length > 60)
        {
            safeName = safeName[..60];
        }

        return $"acquisition-{itemId}-{safeName}-{exportedAtUtc:yyyyMMdd-HHmmss}.json";
    }

    private static AcquisitionEvaluationProjectionDiagnostic CreateProjection(
        string quality,
        bool applicable,
        MarketPurchaseCostEstimate estimate,
        DetailedShoppingPlan? marketPlan)
    {
        return new AcquisitionEvaluationProjectionDiagnostic(
            quality,
            applicable,
            estimate.Kind,
            estimate.Cost,
            estimate.HasCost,
            estimate.IsDefaultEligible,
            estimate.World?.DataCenter,
            estimate.World?.WorldName,
            estimate.World?.TotalQuantityPurchased,
            marketPlan?.QuantityNeeded,
            marketPlan?.HqQuantityNeeded,
            marketPlan?.Error,
            marketPlan?.MarketDataWarning);
    }
}

public sealed record AcquisitionEvaluationItemDiagnosticDump(
    int SchemaVersion,
    string Tool,
    DateTime ExportedAtUtc,
    DiagnosticSnapshotBuildInfo Build,
    AcquisitionEvaluationItemDiagnosticContext Context,
    AcquisitionEvaluationDiagnosticItem Item,
    AcquisitionEvaluationItemActionability Actionability,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlyList<MarketItemAnalysis> MarketAnalyses,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems,
    MarketEvidenceHydrationRunSnapshot AutomaticRefresh,
    MarketCacheDecisionSnapshot? CacheDecision);

public sealed record AcquisitionEvaluationItemDiagnosticContext(
    string? CurrentPlanId,
    string? CurrentPlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    bool SearchEntireRegion,
    MarketAcquisitionLens MarketAnalysisLens,
    long PlanSessionVersion,
    AppStateVersionSnapshot StateVersions,
    PublishedMarketAnalysisScopeSnapshot? PublishedMarketAnalysisScope,
    bool IsMarketEvidenceHydrating);

public sealed record AcquisitionEvaluationDiagnosticItem(
    string NodeId,
    int ItemId,
    string ItemName,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool CanBeHq,
    bool CanBuyFromMarket,
    int TotalQuantity,
    int ActiveQuantity,
    decimal RawNqUnitPrice,
    decimal RawHqUnitPrice,
    string MarketEvidence,
    string EstimatedCost,
    bool IsActiveProcurement,
    bool IsFullySuppressed);

public sealed record AcquisitionEvaluationItemActionability(
    AcquisitionEvaluationProjectionDiagnostic Nq,
    AcquisitionEvaluationProjectionDiagnostic Hq);

public sealed record AcquisitionEvaluationProjectionDiagnostic(
    string Quality,
    bool Applicable,
    MarketPurchaseCostEstimateKind EstimateKind,
    decimal Cost,
    bool HasCost,
    bool IsDefaultEligible,
    string? DataCenter,
    string? WorldName,
    int? CoveredQuantity,
    int? PlanQuantityNeeded,
    int? PlanHqQuantityNeeded,
    string? PlanError,
    string? MarketDataWarning);
