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
            _appState.ShoppingPlans.ToArray(),
            _appState.ProcurementShoppingPlans.ToArray(),
            _appState.MarketItemAnalyses.ToArray(),
            _appState.UnavailableMarketItems.ToArray(),
            _appState.ProcurementRouteDecision);
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
    IReadOnlyList<DetailedShoppingPlan> MarketShoppingPlans,
    IReadOnlyList<DetailedShoppingPlan> ProcurementShoppingPlans,
    IReadOnlyList<MarketItemAnalysis> MarketAnalyses,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems,
    MarketRouteDecision? ProcurementRouteDecision);

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
