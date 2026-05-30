using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketAnalysisExecutionService
{
    Task<MarketAnalysisExecutionResult> ExecuteAsync(
        MarketAnalysisExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null);
}
