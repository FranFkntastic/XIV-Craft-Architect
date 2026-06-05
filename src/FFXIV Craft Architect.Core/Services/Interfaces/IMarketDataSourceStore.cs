using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketDataSourceStore
{
    Task SaveListingFactsAsync(
        IReadOnlyList<CanonicalMarketListingFact> facts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CanonicalMarketListingFact>> LoadListingFactsAsync(
        MarketDataSourceQuery query,
        CancellationToken cancellationToken = default);
}
