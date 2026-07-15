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

        return HasScopePriceContext(world)
            ? listing.PriceSanity is MarketListingPriceSanity.Sane or MarketListingPriceSanity.Outlier
            : listing.PriceSanity is MarketListingPriceSanity.Sane or MarketListingPriceSanity.LowOutlier;
    }

    public static bool HasScopePriceContext(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.CompetitiveThresholdUnitPrice > 0 && world.SaneThresholdUnitPrice > 0;
    }
}
