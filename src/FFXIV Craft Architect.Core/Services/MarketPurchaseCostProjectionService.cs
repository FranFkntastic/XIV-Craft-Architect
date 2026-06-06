using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketPurchaseCostProjectionService
{
    public static MarketPurchaseCostEstimate Estimate(
        DetailedShoppingPlan? shoppingPlan,
        int quantity,
        bool hqOnly,
        bool includeVendor = true)
    {
        if (shoppingPlan == null ||
            quantity <= 0 ||
            shoppingPlan.QuantityNeeded <= 0 ||
            !string.IsNullOrWhiteSpace(shoppingPlan.Error))
        {
            return MarketPurchaseCostEstimate.Unavailable;
        }

        if (includeVendor &&
            !hqOnly &&
            TryGetVendorCost(shoppingPlan, quantity, out var vendorCost))
        {
            return new MarketPurchaseCostEstimate(vendorCost, MarketPurchaseCostEstimateKind.SupportedEvidence);
        }

        if (AcquisitionPlanningService.TryGetMarketBoardPurchase(
            shoppingPlan,
            quantity,
            hqOnly,
            out var world,
            out var evidenceCost))
        {
            return new MarketPurchaseCostEstimate(
                evidenceCost,
                MarketPurchaseCostEstimateKind.SupportedEvidence,
                world);
        }

        if (TryGetUnsupportedProjectedCost(shoppingPlan, quantity, hqOnly, out var projectedCost))
        {
            return new MarketPurchaseCostEstimate(
                projectedCost,
                MarketPurchaseCostEstimateKind.UnsupportedProjection);
        }

        return MarketPurchaseCostEstimate.Unavailable;
    }

    public static bool IsUnsupportedProjectedCost(
        DetailedShoppingPlan? shoppingPlan,
        int? quantity = null,
        bool hqOnly = false)
    {
        if (shoppingPlan == null)
        {
            return false;
        }

        var requestedQuantity = quantity ?? shoppingPlan.QuantityNeeded;
        return Estimate(shoppingPlan, requestedQuantity, hqOnly).IsUnsupportedProjection;
    }

    private static bool TryGetVendorCost(
        DetailedShoppingPlan shoppingPlan,
        int quantity,
        out decimal cost)
    {
        cost = 0;
        if (shoppingPlan.RecommendedWorld is not { } recommendedWorld ||
            !IsVendorWorld(recommendedWorld) ||
            recommendedWorld.TotalCost <= 0)
        {
            return false;
        }

        cost = ScaleCost(recommendedWorld.TotalCost, quantity, shoppingPlan.QuantityNeeded);
        return cost > 0;
    }

    private static bool TryGetUnsupportedProjectedCost(
        DetailedShoppingPlan shoppingPlan,
        int quantity,
        bool hqOnly,
        out decimal cost)
    {
        cost = 0;
        var projectedUnitPrice = GetUnsupportedProjectedUnitPrice(shoppingPlan, hqOnly);
        if (projectedUnitPrice <= 0)
        {
            return false;
        }

        cost = Math.Ceiling(projectedUnitPrice * quantity);
        return cost > 0;
    }

    private static decimal GetUnsupportedProjectedUnitPrice(DetailedShoppingPlan shoppingPlan, bool hqOnly)
    {
        var averagePrice = hqOnly
            ? shoppingPlan.HQAveragePrice.GetValueOrDefault()
            : shoppingPlan.DCAveragePrice;
        if (averagePrice > 0)
        {
            return averagePrice;
        }

        var listings = GetProjectionListings(shoppingPlan, hqOnly).ToList();
        var listingQuantity = listings.Sum(listing => listing.Quantity);
        if (listingQuantity > 0)
        {
            return listings.Sum(listing => listing.Quantity * listing.PricePerUnit) / (decimal)listingQuantity;
        }

        return 0;
    }

    private static IEnumerable<ShoppingListingEntry> GetProjectionListings(
        DetailedShoppingPlan shoppingPlan,
        bool hqOnly)
    {
        foreach (var world in shoppingPlan.WorldOptions.Where(world => !IsVendorWorld(world)))
        {
            foreach (var listing in FilterProjectionListings(world.Listings, hqOnly))
            {
                yield return listing;
            }
        }

        if (shoppingPlan.RecommendedWorld is { } recommendedWorld && !IsVendorWorld(recommendedWorld))
        {
            foreach (var listing in FilterProjectionListings(recommendedWorld.Listings, hqOnly))
            {
                yield return listing;
            }
        }

        if (shoppingPlan.RecommendedSplit == null)
        {
            yield break;
        }

        foreach (var listing in FilterProjectionListings(
            shoppingPlan.RecommendedSplit.SelectMany(split => split.Listings),
            hqOnly))
        {
            yield return listing;
        }
    }

    private static IEnumerable<ShoppingListingEntry> FilterProjectionListings(
        IEnumerable<ShoppingListingEntry> listings,
        bool hqOnly)
    {
        return listings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .Where(listing => hqOnly ? listing.IsHq : !listing.IsHq);
    }

    private static decimal ScaleCost(long totalCost, int quantity, int quantityNeeded)
    {
        return quantity == quantityNeeded
            ? totalCost
            : totalCost * quantity / quantityNeeded;
    }

    private static bool IsVendorWorld(WorldShoppingSummary world)
    {
        return string.Equals(
            world.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record MarketPurchaseCostEstimate(
    decimal Cost,
    MarketPurchaseCostEstimateKind Kind,
    WorldShoppingSummary? World = null)
{
    public static MarketPurchaseCostEstimate Unavailable { get; } =
        new(0, MarketPurchaseCostEstimateKind.Unavailable);

    public bool HasCost => Cost > 0;
    public bool IsUnsupportedProjection => Kind == MarketPurchaseCostEstimateKind.UnsupportedProjection;
    public bool IsDefaultEligible => Kind == MarketPurchaseCostEstimateKind.SupportedEvidence;
}

public enum MarketPurchaseCostEstimateKind
{
    Unavailable,
    SupportedEvidence,
    UnsupportedProjection
}
