using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketPriceEvaluationService
{
    MarketPriceEvaluationContext Evaluate(
        int itemId,
        MarketFetchScope scope,
        DateTime evaluatedAtUtc,
        IReadOnlyList<CachedMarketData> entries);
}
