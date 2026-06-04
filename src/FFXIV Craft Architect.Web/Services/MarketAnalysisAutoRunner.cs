using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketAnalysisAutoRunner : IMarketAnalysisAutoRunner
{
    private readonly AppState _appState;
    private readonly MarketAnalysisWorkflowService _marketAnalysisWorkflow;

    public MarketAnalysisAutoRunner(
        AppState appState,
        MarketAnalysisWorkflowService marketAnalysisWorkflow)
    {
        _appState = appState;
        _marketAnalysisWorkflow = marketAnalysisWorkflow;
    }

    public async Task<MarketAnalysisWorkflowResult> RunAfterPlanActivationAsync(
        CraftingPlan plan,
        long planSessionVersion,
        CancellationToken ct = default)
    {
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        return await _marketAnalysisWorkflow.RunAnalysisAsync(
            new MarketAnalysisWorkflowRequest(ForceRefreshData: false),
            ct: ct);
    }
}
