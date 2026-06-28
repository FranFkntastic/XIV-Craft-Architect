using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class AcquisitionSourceChangeImpactService
{
    public IReadOnlyList<int> GetMarketRefreshItemIds(
        RecipeDemandProjection before,
        RecipeDemandProjection after,
        int changedItemId,
        AcquisitionSource previousSource,
        AcquisitionSource newSource)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var candidateItemIds = after.MarketAnalysisCandidates
            .Where(row => row.CanBuyFromMarket)
            .Select(row => row.ItemId)
            .ToHashSet();

        if (IsMarketSource(newSource))
        {
            return candidateItemIds.Contains(changedItemId)
                ? [changedItemId]
                : [];
        }

        if (newSource != AcquisitionSource.Craft || previousSource == AcquisitionSource.Craft)
        {
            return [];
        }

        var beforeDemand = BuildMarketDemandFingerprints(before.ActiveProcurementDemand);
        var afterDemand = BuildMarketDemandFingerprints(after.ActiveProcurementDemand);

        return afterDemand.Keys
            .Where(itemId => itemId != changedItemId)
            .Where(itemId => candidateItemIds.Contains(itemId))
            .Where(itemId => !beforeDemand.TryGetValue(itemId, out var beforeFingerprint)
                || beforeFingerprint != afterDemand[itemId])
            .OrderBy(itemId => itemId)
            .ToList();
    }

    private static Dictionary<int, MarketDemandFingerprint> BuildMarketDemandFingerprints(
        IReadOnlyList<RecipeDemandRow> rows)
    {
        return rows
            .Where(row => row.IsMarketBoardPurchase && row.Quantity > 0)
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => new MarketDemandFingerprint(
                    group.Sum(row => row.Quantity),
                    group.Any(row => row.MustBeHq),
                    string.Join("\n", group.Select(row => SourceKey(row)).Order(StringComparer.Ordinal))));
    }

    private static string SourceKey(RecipeDemandRow row)
    {
        return string.Join(
            '|',
            row.Source,
            row.MustBeHq,
            row.ParentNodeId ?? string.Empty,
            row.ParentItemName ?? string.Empty,
            row.Quantity);
    }

    private static bool IsMarketSource(AcquisitionSource source)
    {
        return source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq;
    }

    private sealed record MarketDemandFingerprint(
        int Quantity,
        bool RequiresHq,
        string SourcesKey);
}
