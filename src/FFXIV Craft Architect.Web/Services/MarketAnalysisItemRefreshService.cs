using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisItemRefreshWorkflowRequest(
    int ItemId,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null)
{
    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public Func<bool>? IsCurrentConfiguration { get; init; }
}

public sealed record MarketAnalysisItemRefreshWorkflowResult(
    MarketAnalysisItemRefreshStatus Status,
    string? ItemName = null)
{
    public static MarketAnalysisItemRefreshWorkflowResult Noop(MarketAnalysisItemRefreshStatus status)
    {
        return new MarketAnalysisItemRefreshWorkflowResult(status, null);
    }
}

public enum MarketAnalysisItemRefreshStatus
{
    NoPlan,
    NotFound,
    NoData,
    Refreshed,
    StaleDecision,
    StaleConfiguration,
    StalePlan,
    Superseded
}

public sealed class MarketAnalysisItemRefreshService
{
    private readonly MarketAnalysisSubsetRefreshService _subsetRefreshService;

    public MarketAnalysisItemRefreshService(
        MarketAnalysisSubsetRefreshService subsetRefreshService)
    {
        _subsetRefreshService = subsetRefreshService;
    }

    public async Task<MarketAnalysisItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
        MarketAnalysisItemRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var subsetResult = await _subsetRefreshService.RefreshMarketDataAsync(
            new MarketAnalysisSubsetRefreshWorkflowRequest(
                [request.ItemId],
                request.IsCurrentOperation,
                request.ExecutionOptions)
            {
                Scope = request.Scope,
                IsCurrentConfiguration = request.IsCurrentConfiguration
            },
            progress,
            ct);

        return new MarketAnalysisItemRefreshWorkflowResult(
            MapStatus(subsetResult.Status),
            subsetResult.RefreshedItemNamesById.TryGetValue(request.ItemId, out var itemName)
                ? itemName
                : null);
    }

    private static MarketAnalysisItemRefreshStatus MapStatus(MarketAnalysisSubsetRefreshStatus status)
    {
        return status switch
        {
            MarketAnalysisSubsetRefreshStatus.NoPlan => MarketAnalysisItemRefreshStatus.NoPlan,
            MarketAnalysisSubsetRefreshStatus.NotFound => MarketAnalysisItemRefreshStatus.NotFound,
            MarketAnalysisSubsetRefreshStatus.NoData => MarketAnalysisItemRefreshStatus.NoData,
            MarketAnalysisSubsetRefreshStatus.Refreshed => MarketAnalysisItemRefreshStatus.Refreshed,
            MarketAnalysisSubsetRefreshStatus.StaleDecision => MarketAnalysisItemRefreshStatus.StaleDecision,
            MarketAnalysisSubsetRefreshStatus.StaleConfiguration => MarketAnalysisItemRefreshStatus.StaleConfiguration,
            MarketAnalysisSubsetRefreshStatus.StalePlan => MarketAnalysisItemRefreshStatus.StalePlan,
            MarketAnalysisSubsetRefreshStatus.Superseded => MarketAnalysisItemRefreshStatus.Superseded,
            _ => MarketAnalysisItemRefreshStatus.NoData
        };
    }
}
