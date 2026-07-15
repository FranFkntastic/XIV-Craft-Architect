using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class DiagnosticSnapshotBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppState _appState;
    private readonly StoredPlanSnapshotBuilder _storedPlanSnapshotBuilder;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly MarketEvidenceHydrationService? _marketEvidenceHydration;
    private readonly IMarketCacheService? _marketCache;

    static DiagnosticSnapshotBundleService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public DiagnosticSnapshotBundleService(
        AppState appState,
        StoredPlanSnapshotBuilder storedPlanSnapshotBuilder,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        MarketEvidenceHydrationService? marketEvidenceHydration = null,
        IMarketCacheService? marketCache = null)
    {
        _appState = appState;
        _storedPlanSnapshotBuilder = storedPlanSnapshotBuilder;
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _marketEvidenceHydration = marketEvidenceHydration;
        _marketCache = marketCache;
    }

    public DiagnosticSnapshotBundle BuildBundle(DateTime exportedAtUtc)
    {
        var planName = _appState.CurrentPlanName ??
            _appState.CurrentPlan?.Name ??
            "Unsaved Diagnostic Plan";
        var planId = _appState.CurrentPlanId ??
            $"diagnostic-{exportedAtUtc:yyyyMMddHHmmss}";
        var storedPlan = _storedPlanSnapshotBuilder.Build(
            planId,
            planName,
            exportedAtUtc,
            includeSourcePlanIdentity: true,
            includeLegacyMarketAnalysisFields: true);
        var demandProjection = _recipeLayerWorkflow.BuildDemandProjection(_appState.CurrentPlan);
        var acquisitionSnapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            _appState.CurrentPlan,
            _appState.ShoppingPlans,
            _appState.UnavailableMarketItems,
            AcquisitionFilter.All,
            demandProjection);

        return new DiagnosticSnapshotBundle(
            SchemaVersion: 1,
            Tool: "craft-architect-diagnostic-snapshot",
            ExportedAtUtc: exportedAtUtc,
            Build: CreateBuildInfo(),
            Context: CreateContext(),
            StoredPlan: storedPlan,
            AcquisitionRows: acquisitionSnapshot.Rows,
            MarketAnalysisCandidates: acquisitionSnapshot.MarketAnalysisCandidates,
            ActiveProcurementItems: acquisitionSnapshot.ActiveProcurementItems,
            ShoppingPlans: _appState.ShoppingPlans.ToList(),
            ProcurementShoppingPlans: _appState.ProcurementShoppingPlans.ToList(),
            MarketItemAnalyses: _appState.MarketItemAnalyses.ToList(),
            AutomaticMarketRefresh: _marketEvidenceHydration?.LastRun,
            MarketCacheDecision: (_marketCache as IMarketCacheDiagnosticsProvider)?.LastDecisionSnapshot);
    }

    public string Serialize(DiagnosticSnapshotBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    private static DiagnosticSnapshotBuildInfo CreateBuildInfo()
    {
        return new DiagnosticSnapshotBuildInfo(
            BuildVersion: FFXIV_Craft_Architect.Web.WebBuildInfo.BuildVersion,
            Branch: FFXIV_Craft_Architect.Web.WebBuildInfo.BranchName,
            Commit: FFXIV_Craft_Architect.Web.WebBuildInfo.CommitSha,
            Dirty: FFXIV_Craft_Architect.Web.WebBuildInfo.IsDirty);
    }

    private DiagnosticSnapshotContext CreateContext()
    {
        return new DiagnosticSnapshotContext(
            CurrentPlanId: _appState.CurrentPlanId,
            CurrentPlanName: _appState.CurrentPlanName ?? _appState.CurrentPlan?.Name,
            SelectedDataCenter: _appState.SelectedDataCenter,
            SelectedRegion: _appState.SelectedRegion,
            SearchEntireRegion: _appState.SearchEntireRegion,
            RecommendationMode: _appState.RecommendationMode,
            MarketAnalysisLens: _appState.MarketAnalysisLens,
            MarketSortPreference: _appState.MarketSortPreference,
            MarketAnalysisGridSortColumn: _appState.MarketAnalysisGridSortColumn,
            MarketAnalysisGridSortDescending: _appState.MarketAnalysisGridSortDescending,
            MarketAnalysisWorldGridSortColumn: _appState.MarketAnalysisWorldGridSortColumn,
            MarketAnalysisWorldGridSortDescending: _appState.MarketAnalysisWorldGridSortDescending,
            SelectedMarketAnalysisItemId: _appState.SelectedMarketAnalysisItemId,
            CurrentOperation: _appState.CurrentOperation,
            StatusMessage: _appState.StatusMessage);
    }
}

public sealed record DiagnosticSnapshotBundle(
    int SchemaVersion,
    string Tool,
    DateTime ExportedAtUtc,
    DiagnosticSnapshotBuildInfo Build,
    DiagnosticSnapshotContext Context,
    StoredPlan StoredPlan,
    IReadOnlyList<DecisionRow> AcquisitionRows,
    IReadOnlyList<MaterialAggregate> MarketAnalysisCandidates,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlyList<DetailedShoppingPlan> ProcurementShoppingPlans,
    IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses,
    MarketEvidenceHydrationRunSnapshot? AutomaticMarketRefresh,
    MarketCacheDecisionSnapshot? MarketCacheDecision);

public sealed record DiagnosticSnapshotBuildInfo(
    string BuildVersion,
    string Branch,
    string Commit,
    bool Dirty);

public sealed record DiagnosticSnapshotContext(
    string? CurrentPlanId,
    string? CurrentPlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    bool SearchEntireRegion,
    RecommendationMode RecommendationMode,
    MarketAcquisitionLens MarketAnalysisLens,
    MarketSortOption MarketSortPreference,
    MarketAnalysisGridSortColumn? MarketAnalysisGridSortColumn,
    bool MarketAnalysisGridSortDescending,
    MarketAnalysisWorldGridSortColumn? MarketAnalysisWorldGridSortColumn,
    bool MarketAnalysisWorldGridSortDescending,
    int? SelectedMarketAnalysisItemId,
    string? CurrentOperation,
    string? StatusMessage);
