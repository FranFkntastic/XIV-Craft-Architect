using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketEvidenceReconciliationService
{
    Task<MarketEvidenceReconciliationResult> ReconcileAsync(
        MarketEvidenceReconciliationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null);

    Task<MarketWorldEvidenceReconciliationResult> ReconcileWorldAsync(
        MarketWorldEvidenceReconciliationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null);
}
