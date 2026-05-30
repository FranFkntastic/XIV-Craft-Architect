using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IProcurementRouteExecutionService
{
    Task<ProcurementRouteExecutionResult> AnalyzeAsync(
        ProcurementRouteExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null);
}
