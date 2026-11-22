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

        if (TryGetCoverageEstimate(shoppingPlan, quantity, hqOnly, out var coverageEstimate))
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
        int quantity,
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
        var candidate = MarketCoverageSelection.GetCandidates(shoppingPlan.CoverageSet)
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

        var allocatedCost = ScaleCost(
            candidate.ExactNeededCost,
            quantity,
            shoppingPlan.QuantityNeeded);
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
            allocatedCost,
            MarketPurchaseCostEstimateKind.SupportedEvidence,
            world,
            candidate);
        return true;
    }

    /// <summary>
    /// Describes the listings behind a market purchase: how many units the pricing scope lists,
    /// how many listings the purchase draws on, and when the oldest contributing world was observed.
    /// </summary>
    public static MarketListingEvidence DescribeListings(
        DetailedShoppingPlan? shoppingPlan,
        int quantity,
        bool hqOnly)
    {
        if (shoppingPlan == null ||
            quantity <= 0 ||
            !string.IsNullOrWhiteSpace(shoppingPlan.Error))
        {
            return MarketListingEvidence.None;
        }

        var marketWorlds = shoppingPlan.WorldOptions
            .Where(world => !IsVendorWorld(world))
            .ToList();
        var listedQuantity = marketWorlds
            .SelectMany(world => FilterProjectionListings(world.Listings, hqOnly))
            .Sum(listing => (long)listing.Quantity);
        var estimate = Estimate(shoppingPlan, quantity, hqOnly, includeVendor: false);

        int listingCount;
        IEnumerable<string> contributingWorlds;
        if (estimate.Coverage is { } coverage)
        {
            var used = coverage.Listings.Where(listing => listing.QuantityPurchased > 0).ToList();
            listingCount = used.Count;
            contributingWorlds = used.Select(listing => listing.WorldName);
        }
        else if (estimate.World is { } world)
        {
            var worldOption = marketWorlds.FirstOrDefault(option =>
                string.Equals(option.WorldName, world.WorldName, StringComparison.OrdinalIgnoreCase));
            listingCount = worldOption?.ListingsUsed > 0
                ? worldOption.ListingsUsed
                : CountListingsToCover(worldOption?.Listings ?? [], quantity, hqOnly);
            contributingWorlds = [world.WorldName];
        }
        else
        {
            var cheapest = marketWorlds
                .SelectMany(option => FilterProjectionListings(option.Listings, hqOnly)
                    .Select(listing => (option.WorldName, Listing: listing)))
                .OrderBy(entry => entry.Listing.PricePerUnit)
                .ToList();
            var remaining = quantity;
            var used = new List<string>();
            foreach (var entry in cheapest)
            {
                if (remaining <= 0)
                {
                    break;
                }

                used.Add(entry.WorldName);
                remaining -= entry.Listing.Quantity;
            }

            listingCount = used.Count;
            contributingWorlds = used;
        }

        var worldNames = contributingWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observedWorlds = worldNames.Count > 0
            ? marketWorlds.Where(world => worldNames.Contains(world.WorldName))
            : marketWorlds;
        var observedAtUtc = observedWorlds
            .Select(world => world.MarketUploadedAtUtc)
            .Where(uploadedAt => uploadedAt.HasValue)
            .Min();

        return new MarketListingEvidence(
            (int)Math.Min(int.MaxValue, listedQuantity),
            listingCount,
            observedAtUtc);
    }

    private static int CountListingsToCover(
        IEnumerable<ShoppingListingEntry> listings,
        int quantity,
        bool hqOnly)
    {
        var count = 0;
        var remaining = quantity;
        foreach (var listing in FilterProjectionListings(listings, hqOnly).OrderBy(listing => listing.PricePerUnit))
        {
            if (remaining <= 0)
            {
                break;
            }

            count++;
            remaining -= listing.Quantity;
        }

        return count;
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

    private static decimal ScaleCost(decimal totalCost, int quantity, int quantityNeeded)
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
    WorldShoppingSummary? World = null,
    MarketCoverageOption? Coverage = null)
{
    public static MarketPurchaseCostEstimate Unavailable { get; } =
        new(0, MarketPurchaseCostEstimateKind.Unavailable);

    public bool HasCost => Cost > 0;
    public bool IsUnsupportedProjection => Kind == MarketPurchaseCostEstimateKind.UnsupportedProjection;
    public bool IsDefaultEligible => Kind == MarketPurchaseCostEstimateKind.SupportedEvidence;
}

/// <summary>Listings behind a market purchase, for display beside its cost.</summary>
public sealed record MarketListingEvidence(
    int ListedQuantity,
    int ListingCount,
    DateTime? ObservedAtUtc)
{
    public static MarketListingEvidence None { get; } = new(0, 0, null);
}

public enum MarketPurchaseCostEstimateKind
{
    Unavailable,
    SupportedEvidence,
    UnsupportedProjection
}
