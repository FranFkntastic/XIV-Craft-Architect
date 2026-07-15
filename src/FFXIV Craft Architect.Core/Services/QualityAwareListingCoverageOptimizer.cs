using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Finds the lowest cash-out set of complete market-board stacks that satisfies
/// both total quantity and the required HQ quantity.
/// </summary>
internal static class QualityAwareListingCoverageOptimizer
{
    public static IReadOnlyList<Listing>? Select(
        IReadOnlyList<Listing> listings,
        int quantityNeeded,
        int hqQuantityNeeded)
    {
        var states = new Dictionary<(int Quantity, int HqQuantity), CoverageState>
        {
            [(0, 0)] = new CoverageState(0, null, -1)
        };

        for (var listingIndex = 0; listingIndex < listings.Count; listingIndex++)
        {
            var listing = listings[listingIndex].MarketListing;
            var listingCost = ToLongSaturating((decimal)listing.Quantity * listing.PricePerUnit);
            var snapshot = states.ToArray();
            foreach (var (coverage, state) in snapshot)
            {
                var nextCoverage = (
                    (int)Math.Min(quantityNeeded, (long)coverage.Quantity + listing.Quantity),
                    (int)Math.Min(
                        hqQuantityNeeded,
                        (long)coverage.HqQuantity + (listing.IsHq ? listing.Quantity : 0)));
                var nextCost = SaturatingAdd(state.CashOutCost, listingCost);
                if (!states.TryGetValue(nextCoverage, out var existing) || nextCost < existing.CashOutCost)
                {
                    states[nextCoverage] = new CoverageState(nextCost, state, listingIndex);
                }
            }
        }

        if (!states.TryGetValue((quantityNeeded, hqQuantityNeeded), out var selectedState))
        {
            return null;
        }

        var selected = new List<Listing>();
        for (var state = selectedState; state.ListingIndex >= 0; state = state.Previous!)
        {
            selected.Add(listings[state.ListingIndex]);
        }

        selected.Reverse();
        return selected;
    }

    internal sealed record Listing(
        WorldShoppingSummary World,
        ShoppingListingEntry MarketListing);

    private sealed record CoverageState(
        long CashOutCost,
        CoverageState? Previous,
        int ListingIndex);

    private static long ToLongSaturating(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > 0 && right > long.MaxValue - left
            ? long.MaxValue
            : left + right;
}
