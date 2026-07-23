using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Captures the current Recipe Planner pricing state without rebuilding, refreshing,
/// reconciling, publishing, or otherwise mutating the active session.
/// </summary>
public sealed partial class RecipePlanDiagnosticDumpService
{
    public const int SchemaVersion = 2;
    public const string ToolName = "recipe-plan-diagnostic-dump";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppState _appState;

    static RecipePlanDiagnosticDumpService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public RecipePlanDiagnosticDumpService(AppState appState)
    {
        _appState = appState;
    }

    public RecipePlanDiagnosticDump BuildDump()
    {
        var plan = _appState.CurrentPlan;
        var publishedAtUtc = _appState.PublishedMarketAnalysisScope?.PublishedAtUtc;
        var displayedQuotes = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            _appState.ShoppingPlans,
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            _appState.IsMarketEvidenceHydrating,
            publishedAtUtc);
        var displayedStates = RecipePlanTreeDisplayBuilder.Build(
            plan,
            _appState.ShoppingPlans,
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            _appState.IsMarketEvidenceHydrating,
            publishedAtUtc);
        var procurementQuotes =
            _appState.ProcurementRouteValidity == ProcurementRoutePublicationValidity.Current
                ? RecipePlanAcquisitionQuoteBuilder.Build(
                    plan,
                    _appState.ProcurementShoppingPlans,
                    RecipePlanAcquisitionQuoteBasis.ProcurementRoute,
                    _appState.IsMarketEvidenceHydrating,
                    publishedAtUtc)
                : new Dictionary<string, RecipePlanAcquisitionQuote>();
        var nodes = new List<RecipePlanNodeDiagnostic>();
        if (plan != null)
        {
            foreach (var root in plan.RootItems)
            {
                AddNode(root, nodes);
            }
        }

        return new RecipePlanDiagnosticDump(
            SchemaVersion,
            ToolName,
            DateTime.UtcNow,
            FormatLocalTimestamp(DateTimeOffset.Now),
            new DiagnosticSnapshotBuildInfo(
                WebBuildInfo.BuildVersion,
                WebBuildInfo.BranchName,
                WebBuildInfo.CommitSha,
                WebBuildInfo.IsDirty),
            new RecipePlanDiagnosticContext(
                _appState.CurrentPlanId,
                _appState.CurrentPlanName ?? plan?.Name,
                _appState.SelectedDataCenter,
                _appState.SelectedRegion,
                _appState.DefaultMarketFetchScope,
                _appState.SearchEntireRegion,
                _appState.MarketAnalysisLens,
                _appState.PlanSessionVersion,
                _appState.CurrentVersions,
                _appState.MarketIntelligenceId,
                _appState.PublishedMarketAnalysisScope,
                _appState.ProcurementRouteValidity,
                _appState.ProcurementRoutePublicationBasis,
                _appState.IsMarketEvidenceHydrating,
                RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
                plan?.RootItems.Count ?? 0,
                nodes.Count),
            _appState.ProjectItems,
            nodes,
            displayedQuotes.Values.ToArray(),
            displayedStates.Values.ToArray(),
            procurementQuotes.Values.ToArray(),
            BuildShoppingPlanDiagnostics(
                _appState.ShoppingPlans,
                RecipePlanAcquisitionQuoteBasis.MarketAnalysis),
            BuildShoppingPlanDiagnostics(
                _appState.ProcurementShoppingPlans,
                RecipePlanAcquisitionQuoteBasis.ProcurementRoute),
            _appState.UnavailableMarketItems.ToArray(),
            BuildRouteDiagnostic(_appState.ProcurementRouteDecision),
            _appState.ShoppingPlans.Count,
            _appState.ProcurementShoppingPlans.Count,
            _appState.MarketItemAnalyses.Count);
    }

    public static string Serialize(RecipePlanDiagnosticDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    public static string CreateFileName(string? planName, DateTime exportedAtUtc)
    {
        var safeName = string.IsNullOrWhiteSpace(planName) ? "recipe-plan" : planName.Trim();
        safeName = InvalidFileNameCharacterPattern().Replace(safeName, "_");
        if (safeName.Length > 48)
        {
            safeName = safeName[..48];
        }

        return $"recipe-plan-{safeName}-{exportedAtUtc:yyyyMMdd-HHmmss}.json";
    }

    private static IReadOnlyList<RecipePlanShoppingPlanDiagnostic> BuildShoppingPlanDiagnostics(
        IEnumerable<DetailedShoppingPlan> plans,
        RecipePlanAcquisitionQuoteBasis basis)
    {
        return plans.Select(plan =>
        {
            var coverage = PurchaseRecommendationCost.GetDefaultCoverageOption(plan);
            return new RecipePlanShoppingPlanDiagnostic(
                basis,
                plan.ItemId,
                plan.Name,
                plan.QuantityNeeded,
                plan.HqQuantityNeeded,
                plan.DCAveragePrice,
                plan.HQAveragePrice,
                plan.Error,
                plan.MarketDataWarning,
                coverage == null
                    ? null
                    : new RecipePlanCoverageDiagnostic(
                        coverage.CandidateId,
                        coverage.Kind,
                        coverage.QualityPolicy,
                        coverage.QuantityCovered,
                        coverage.QuantityToPurchase,
                        coverage.ExcessQuantity,
                        coverage.CashOutCost,
                        coverage.AverageUnitCost,
                        coverage.IsDefaultEligible,
                        coverage.DegradedReason,
                        coverage.Worlds,
                        coverage.Listings.Count,
                        coverage.Listings.Sum(listing => listing.QuantityAvailable),
                        coverage.Listings.Sum(listing => listing.QuantityUsed),
                        coverage.Listings.Sum(listing => listing.QuantityPurchased)),
                plan.RecommendedSplit?.Select(split =>
                    new RecipePlanSplitDiagnostic(
                        split.DataCenter,
                        split.WorldName,
                        split.QuantityToBuy,
                        split.TotalCost,
                        split.Listings.Count,
                        split.Listings.Sum(listing => listing.Quantity),
                        split.Listings.Where(listing => listing.IsHq).Sum(listing => listing.Quantity)))
                    .ToArray() ?? Array.Empty<RecipePlanSplitDiagnostic>(),
                plan.RecommendedWorld == null
                    ? null
                    : new RecipePlanWorldDiagnostic(
                        plan.RecommendedWorld.DataCenter,
                        plan.RecommendedWorld.WorldName,
                        plan.RecommendedWorld.TotalCost,
                        plan.RecommendedWorld.AveragePricePerUnit,
                        plan.RecommendedWorld.TotalQuantityPurchased,
                        plan.RecommendedWorld.HasSufficientStock,
                        plan.RecommendedWorld.ShortfallQuantity,
                        plan.RecommendedWorld.Listings.Count,
                        plan.RecommendedWorld.Listings.Sum(listing => listing.Quantity),
                        plan.RecommendedWorld.Listings
                            .Where(listing => listing.IsHq)
                            .Sum(listing => listing.Quantity)));
        }).ToArray();
    }

    private static RecipePlanRouteDiagnostic? BuildRouteDiagnostic(MarketRouteDecision? decision)
    {
        return decision == null
            ? null
            : new RecipePlanRouteDiagnostic(
                decision.TravelTolerance,
                decision.MaximumPremiumRate,
                decision.CheapestGilCost,
                decision.SelectedGilCost,
                decision.FixedAcquisitionGilCost,
                decision.CheapestWorldStops,
                decision.SelectedWorldStops,
                decision.CheapestDataCenterTransfers,
                decision.SelectedDataCenterTransfers,
                decision.RouteSearchWasTruncated,
                decision.RepresentativeRoutes.Count,
                decision.ItemPremiums.Count,
                decision.ToleranceSelections.Count);
    }

    private static string FormatLocalTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("yyyy-MM-dd hh:mm:ss tt zzz", CultureInfo.InvariantCulture);
    }

    private static void AddNode(PlanNode node, ICollection<RecipePlanNodeDiagnostic> nodes)
    {
        nodes.Add(new RecipePlanNodeDiagnostic(
            node.NodeId,
            node.ParentNodeId,
            node.ItemId,
            node.Name,
            node.Quantity,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.CanBeHq,
            node.CanCraft,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.HqMarketPrice,
            node.VendorPrice,
            node.PriceSource,
            node.PriceSourceDetails,
            node.SelectedVendor,
            node.CheapestGilVendor));
        foreach (var child in node.Children)
        {
            AddNode(child, nodes);
        }
    }

    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidFileNameCharacterPattern();
}

public sealed record RecipePlanDiagnosticDump(
    int SchemaVersion,
    string Tool,
    DateTime ExportedAtUtc,
    string ExportedAtLocal,
    DiagnosticSnapshotBuildInfo Build,
    RecipePlanDiagnosticContext Context,
    IReadOnlyList<ProjectItem> ProjectItems,
    IReadOnlyList<RecipePlanNodeDiagnostic> Nodes,
    IReadOnlyList<RecipePlanAcquisitionQuote> DisplayedQuotes,
    IReadOnlyList<RecipeNodeDisplayState> DisplayedStates,
    IReadOnlyList<RecipePlanAcquisitionQuote> ProcurementComparisonQuotes,
    IReadOnlyList<RecipePlanShoppingPlanDiagnostic> MarketShoppingPlans,
    IReadOnlyList<RecipePlanShoppingPlanDiagnostic> ProcurementShoppingPlans,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems,
    RecipePlanRouteDiagnostic? ProcurementRoute,
    int MarketShoppingPlanCount,
    int ProcurementShoppingPlanCount,
    int MarketAnalysisCount);

public sealed record RecipePlanDiagnosticContext(
    string? CurrentPlanId,
    string? CurrentPlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope DefaultMarketFetchScope,
    bool SearchEntireRegion,
    MarketAcquisitionLens MarketAnalysisLens,
    long PlanSessionVersion,
    AppStateVersionSnapshot StateVersions,
    Guid MarketIntelligenceId,
    PublishedMarketAnalysisScopeSnapshot? PublishedMarketAnalysisScope,
    ProcurementRoutePublicationValidity ProcurementRouteValidity,
    ProcurementRoutePublicationBasis? ProcurementRoutePublicationBasis,
    bool IsMarketEvidenceHydrating,
    RecipePlanAcquisitionQuoteBasis DisplayedPriceBasis,
    int RootItemCount,
    int TotalNodeCount);

public sealed record RecipePlanNodeDiagnostic(
    string NodeId,
    string? ParentNodeId,
    int ItemId,
    string Name,
    int Quantity,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool CanBeHq,
    bool CanCraft,
    bool CanBuyFromMarket,
    bool CanBuyFromVendor,
    decimal MarketPrice,
    decimal HqMarketPrice,
    decimal VendorPrice,
    PriceSource PriceSource,
    string PriceSourceDetails,
    VendorInfo? SelectedVendor,
    VendorInfo? CheapestGilVendor);

public sealed record RecipePlanShoppingPlanDiagnostic(
    RecipePlanAcquisitionQuoteBasis Basis,
    int ItemId,
    string Name,
    int QuantityNeeded,
    int HqQuantityNeeded,
    decimal DataCenterAveragePrice,
    decimal? HqAveragePrice,
    string? Error,
    string? MarketDataWarning,
    RecipePlanCoverageDiagnostic? DefaultCoverage,
    IReadOnlyList<RecipePlanSplitDiagnostic> RecommendedSplit,
    RecipePlanWorldDiagnostic? RecommendedWorld);

public sealed record RecipePlanCoverageDiagnostic(
    string CandidateId,
    MarketCoverageKind Kind,
    MarketCoverageQualityPolicy QualityPolicy,
    int QuantityCovered,
    int QuantityToPurchase,
    int ExcessQuantity,
    decimal CashOutCost,
    decimal AverageUnitCost,
    bool IsDefaultEligible,
    string? DegradedReason,
    IReadOnlyList<MarketCoverageWorld> Worlds,
    int ListingCount,
    int ListingQuantityAvailable,
    int ListingQuantityUsed,
    int ListingQuantityPurchased);

public sealed record RecipePlanSplitDiagnostic(
    string DataCenter,
    string WorldName,
    int QuantityToBuy,
    long TotalCost,
    int ListingCount,
    int ListingQuantity,
    int HqListingQuantity);

public sealed record RecipePlanWorldDiagnostic(
    string DataCenter,
    string WorldName,
    long TotalCost,
    decimal AveragePricePerUnit,
    int TotalQuantityPurchased,
    bool HasSufficientStock,
    int ShortfallQuantity,
    int ListingCount,
    int ListingQuantity,
    int HqListingQuantity);

public sealed record RecipePlanRouteDiagnostic(
    int TravelTolerance,
    decimal? MaximumPremiumRate,
    long CheapestGilCost,
    long SelectedGilCost,
    long FixedAcquisitionGilCost,
    int CheapestWorldStops,
    int SelectedWorldStops,
    int CheapestDataCenterTransfers,
    int SelectedDataCenterTransfers,
    bool RouteSearchWasTruncated,
    int RepresentativeRouteCount,
    int ItemDecisionCount,
    int ToleranceSelectionCount);
