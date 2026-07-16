using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketProcurementEvidencePolicy
{
    public static IReadOnlyList<AnalyzedMarketListing> GetEligibleListings(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.Listings
            .Where(listing => IsEligibleListing(world, listing))
            .OrderBy(listing => listing.SortIndex)
            .ToList();
    }

    public static bool IsEligibleListing(WorldMarketAnalysis world, AnalyzedMarketListing listing)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(listing);

        // Price interpretation is descriptive. A listing CA actually loaded remains
        // actionable even when its relationship to the wider market is uncertain.
        // Procurement may rank fringe evidence poorly, but it must not rewrite stock.
        return listing.Quantity > 0 && listing.PricePerUnit > 0;
    }

    public static bool HasScopePriceContext(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.CompetitiveThresholdUnitPrice > 0 && world.SaneThresholdUnitPrice > 0;
    }
}
