using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketIntelligenceStore
{
    Task SavePublicationAsync(
        MarketIntelligencePublicationWrite publication,
        CancellationToken cancellationToken = default);

    Task SaveRunRecordsAsync(
        Guid publicationId,
        IReadOnlyList<MarketAnalysisRunRecord> runRecords,
        CancellationToken cancellationToken = default);

    Task<MarketIntelligencePublicationSummary?> LoadPublicationSummaryAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default);

    Task<MarketIntelligenceDetailManifest?> LoadDetailManifestAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketListingDetail>> LoadDetailsAsync(
        MarketIntelligenceDetailQuery query,
        CancellationToken cancellationToken = default);

    Task<MarketAnalysisRunRecord?> LoadRunRecordAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task PruneDetailsAsync(
        MarketIntelligencePruneRequest request,
        CancellationToken cancellationToken = default);
}
