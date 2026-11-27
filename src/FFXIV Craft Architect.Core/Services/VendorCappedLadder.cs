using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Buying an item an NPC vendor also sells: take one world's listings cheapest-first while their
/// unit price is below the vendor price, then buy the rest from the vendor (unlimited stock, fixed
/// price). Listings are bought whole, so the market part costs the average unit price of the
/// listings walked, the same per-unit basis as the uncapped ladder.
/// </summary>
public static class VendorCappedLadder
{
    /// <summary>One world's capped buy of <c>MarketQuantity + VendorQuantity</c> units.</summary>
    public readonly record struct Buy(int MarketQuantity, int VendorQuantity, decimal MarketUnitPrice, decimal VendorUnitPrice)
    {
        public int Quantity => MarketQuantity + VendorQuantity;
        public decimal Total => MarketUnitPrice * MarketQuantity + VendorUnitPrice * VendorQuantity;
        public decimal UnitPrice => Quantity > 0 ? Total / Quantity : 0;
    }

    /// <summary>
    /// Walks <paramref name="listings"/> cheapest-first, skipping any at or above
    /// <paramref name="vendorUnitPrice"/>, until <paramref name="quantity"/> is covered; the rest
    /// comes from the vendor.
    /// </summary>
    public static Buy Walk(IEnumerable<AnalyzedMarketListing> listings, int quantity, decimal vendorUnitPrice)
    {
        long spent = 0;
        var bought = 0;
        foreach (var listing in listings
                     .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0 && listing.PricePerUnit < vendorUnitPrice)
                     .OrderBy(listing => listing.PricePerUnit))
        {
            if (bought >= quantity)
            {
                break;
            }

            spent += listing.PricePerUnit * listing.Quantity;
            bought += listing.Quantity;
        }

        var market = Math.Min(quantity, bought);
        return new(market, quantity - market, bought > 0 ? spent / (decimal)bought : 0, vendorUnitPrice);
    }

    /// <summary>
    /// The one world whose capped buy of <paramref name="quantity"/> costs least; null when no
    /// world lists the item below <paramref name="vendorUnitPrice"/>, so buying is vendor-only.
    /// </summary>
    public static (WorldMarketAnalysis World, Buy Buy)? BestWorld(
        MarketItemAnalysis analysis, int quantity, decimal vendorUnitPrice)
    {
        if (quantity <= 0 || vendorUnitPrice <= 0)
        {
            return null;
        }

        return analysis.Worlds
            .Select(world => (World: world, Buy: Walk(world.Listings, quantity, vendorUnitPrice)))
            .Where(candidate => candidate.Buy.MarketQuantity > 0)
            .OrderBy(candidate => candidate.Buy.Total)
            .ThenBy(candidate => candidate.World.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.World.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => ((WorldMarketAnalysis, Buy)?)candidate)
            .FirstOrDefault();
    }
}
