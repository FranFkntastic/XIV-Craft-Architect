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
        int hqQuantityNeeded) =>
        SelectBounded(listings, quantityNeeded, hqQuantityNeeded, budget: null).Listings;

    public static Selection SelectBounded(
        IReadOnlyList<Listing> listings,
        int quantityNeeded,
        int hqQuantityNeeded,
        MarketRouteCandidateWorkBudget? budget)
    {
        var fallback = SelectGreedyComplete(listings, quantityNeeded, hqQuantityNeeded, budget);
        var states = new Dictionary<(int Quantity, int HqQuantity), CoverageState>
        {
            [(0, 0)] = new CoverageState(0, null, -1)
        };

        for (var listingIndex = 0; listingIndex < listings.Count; listingIndex++)
        {
            budget?.CheckCancellation();
            var listing = listings[listingIndex].MarketListing;
            var listingCost = ToLongSaturating((decimal)listing.Quantity * listing.PricePerUnit);
            var snapshot = states.ToArray();
            foreach (var (coverage, state) in snapshot)
            {
                if (budget is not null && !budget.TryConsumeQualityTransition())
                {
                    return new Selection(fallback, WasTruncated: true);
                }
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
            return new Selection(fallback, WasTruncated: false);
        }

        var selected = new List<Listing>();
        for (var state = selectedState; state.ListingIndex >= 0; state = state.Previous!)
        {
            selected.Add(listings[state.ListingIndex]);
        }

        selected.Reverse();
        return new Selection(selected, WasTruncated: false);
    }

    private static IReadOnlyList<Listing>? SelectGreedyComplete(
        IReadOnlyList<Listing> listings,
        int quantityNeeded,
        int hqQuantityNeeded,
        MarketRouteCandidateWorkBudget? budget)
    {
        var selected = new List<Listing>();
        var selectedIndexes = new HashSet<int>();
        var hqQuantity = 0L;
        var totalQuantity = 0L;
        for (var index = 0; index < listings.Count && hqQuantity < hqQuantityNeeded; index++)
        {
            budget?.CheckCancellation();
            var listing = listings[index].MarketListing;
            if (!listing.IsHq || listing.Quantity <= 0)
            {
                continue;
            }
            selected.Add(listings[index]);
            selectedIndexes.Add(index);
            hqQuantity += listing.Quantity;
            totalQuantity += listing.Quantity;
        }
        if (hqQuantity < hqQuantityNeeded)
        {
            return null;
        }

        for (var index = 0; index < listings.Count && totalQuantity < quantityNeeded; index++)
        {
            budget?.CheckCancellation();
            if (!selectedIndexes.Add(index) || listings[index].MarketListing.Quantity <= 0)
            {
                continue;
            }
            selected.Add(listings[index]);
            totalQuantity += listings[index].MarketListing.Quantity;
        }
        return totalQuantity >= quantityNeeded ? selected : null;
    }

    internal sealed record Selection(
        IReadOnlyList<Listing>? Listings,
        bool WasTruncated);

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
