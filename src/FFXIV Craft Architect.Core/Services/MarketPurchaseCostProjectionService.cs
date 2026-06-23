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

        if (TryGetCoverageEstimate(shoppingPlan, hqOnly, out var coverageEstimate))
        {
            return coverageEstimate;
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

    private static bool TryGetCoverageEstimate(
        DetailedShoppingPlan shoppingPlan,
        bool hqOnly,
        out MarketPurchaseCostEstimate estimate)
    {
        estimate = MarketPurchaseCostEstimate.Unavailable;
        if (shoppingPlan.CoverageSet == null)
        {
            return false;
        }

        var qualityPolicy = hqOnly
            ? MarketCoverageQualityPolicy.HqOnly
            : MarketCoverageQualityPolicy.NqOrHq;
        var candidate = GetCoverageCandidates(shoppingPlan.CoverageSet)
            .Where(candidate => candidate.Kind == MarketCoverageKind.SupportedListings)
            .Where(candidate => candidate.QualityPolicy == qualityPolicy)
            .Where(candidate => candidate.IsDefaultEligible)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .ThenBy(candidate => candidate.CashOutCost)
            .FirstOrDefault();

        if (candidate == null || candidate.ExactNeededCost <= 0)
        {
            return false;
        }

        var world = candidate.Worlds.Count == 1
            ? new WorldShoppingSummary
            {
                DataCenter = candidate.Worlds[0].DataCenter,
                WorldName = candidate.Worlds[0].WorldName,
                TotalCost = ToLongSaturating(candidate.CashOutCost),
                TotalQuantityPurchased = candidate.Worlds[0].QuantityToPurchase,
                AveragePricePerUnit = candidate.AverageUnitCost
            }
            : null;

        estimate = new MarketPurchaseCostEstimate(
            candidate.ExactNeededCost,
            MarketPurchaseCostEstimateKind.SupportedEvidence,
            world);
        return true;
    }

    private static IEnumerable<MarketCoverageOption> GetCoverageCandidates(MarketCoverageSet coverageSet)
    {
        return coverageSet.AllCandidates
            .Concat([
                coverageSet.SingleWorld,
                coverageSet.CompactSplit,
                coverageSet.WideSplit,
                coverageSet.CheapestObserved
            ])
            .Where(candidate => candidate != null)
            .Cast<MarketCoverageOption>()
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
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
        if (TryGetProjectedListingsCost(shoppingPlan, quantity, hqOnly, out var listingsCost))
        {
            cost = listingsCost;
            return true;
        }

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
        var averagePrice = GetProjectedAverageUnitPrice(shoppingPlan, hqOnly);
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

    private static decimal GetProjectedAverageUnitPrice(DetailedShoppingPlan shoppingPlan, bool hqOnly)
    {
        var hqAveragePrice = shoppingPlan.HQAveragePrice.GetValueOrDefault();
        if (hqOnly)
        {
            return hqAveragePrice;
        }

        return shoppingPlan.DCAveragePrice switch
        {
            > 0 when hqAveragePrice > 0 => Math.Min(shoppingPlan.DCAveragePrice, hqAveragePrice),
            > 0 => shoppingPlan.DCAveragePrice,
            _ => hqAveragePrice
        };
    }

    private static bool TryGetProjectedListingsCost(
        DetailedShoppingPlan shoppingPlan,
        int quantity,
        bool hqOnly,
        out decimal cost)
    {
        cost = 0;
        var remaining = quantity;
        foreach (var listing in GetProjectionListings(shoppingPlan, hqOnly)
            .OrderBy(listing => listing.PricePerUnit))
        {
            var quantityToBuy = Math.Min(remaining, listing.Quantity);
            cost += quantityToBuy * listing.PricePerUnit;
            remaining -= quantityToBuy;
            if (remaining <= 0)
            {
                return cost > 0;
            }
        }

        cost = 0;
        return false;
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
            .Where(listing => !hqOnly || listing.IsHq);
    }

    private static decimal ScaleCost(long totalCost, int quantity, int quantityNeeded)
    {
        return quantity == quantityNeeded
            ? totalCost
            : totalCost * quantity / quantityNeeded;
    }

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
