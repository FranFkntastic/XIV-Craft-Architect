using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketPriceLadderAnalysisService
{
    Task<List<MarketItemAnalysis>> AnalyzeAsync(
        MarketAnalysisRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null);

    DetailedShoppingPlan ProjectToShoppingPlan(
        MarketItemAnalysis analysis,
        MarketAcquisitionLens lens,
        MarketAnalysisConfig? config = null);

    Task<DetailedShoppingPlan> ProjectToShoppingPlanAsync(
        MarketItemAnalysis analysis,
        MarketAcquisitionLens lens,
        MarketAnalysisConfig? config = null,
        MarketAnalysisExecutionOptions? executionOptions = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
