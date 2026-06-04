using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IMarketAnalysisAutoRunner
{
    Task<MarketAnalysisWorkflowResult> RunAfterPlanActivationAsync(
        CraftingPlan plan,
        long planSessionVersion,
        CancellationToken ct = default);
}
