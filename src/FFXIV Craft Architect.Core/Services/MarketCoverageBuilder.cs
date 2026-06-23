using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketCoverageBuilder
{
    public static MarketCoverageSet Build(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var candidates = new List<MarketCoverageOption>();
        candidates.AddRange(BuildSingleWorldCandidates(plan, MarketCoverageQualityPolicy.NqOrHq));
        candidates.AddRange(BuildSingleWorldCandidates(plan, MarketCoverageQualityPolicy.HqOnly));

        var singleWorld = candidates
            .Where(candidate => candidate.Tier == MarketCoverageTier.SingleWorld)
            .Where(candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.CashOutCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .FirstOrDefault();

        return new MarketCoverageSet(
            plan.ItemId,
            plan.Name,
            plan.QuantityNeeded,
            singleWorld,
            CompactSplit: null,
            WideSplit: null,
            CheapestObserved: null,
            candidates);
    }

    private static IEnumerable<MarketCoverageOption> BuildSingleWorldCandidates(
        DetailedShoppingPlan plan,
        MarketCoverageQualityPolicy qualityPolicy)
    {
        foreach (var world in plan.WorldOptions.Where(IsMarketWorld))
        {
            if (!TryCoverFromListings(world, plan.QuantityNeeded, qualityPolicy, out var listings))
            {
                continue;
            }

            yield return CreateOption(
                plan,
                MarketCoverageTier.SingleWorld,
                qualityPolicy,
                isDefaultEligible: true,
                listings);
        }
    }

    private static bool TryCoverFromListings(
        WorldShoppingSummary world,
        int quantityNeeded,
        MarketCoverageQualityPolicy qualityPolicy,
        out List<MarketCoverageListing> selectedListings)
    {
        selectedListings = [];
        var remaining = quantityNeeded;

        foreach (var listing in world.Listings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .Where(listing => qualityPolicy != MarketCoverageQualityPolicy.HqOnly || listing.IsHq)
            .OrderBy(listing => listing.PricePerUnit))
        {
            var used = Math.Min(remaining, listing.Quantity);
            selectedListings.Add(new MarketCoverageListing(
                world.DataCenter,
                world.WorldName,
                listing.Quantity,
                used,
                listing.Quantity,
                listing.PricePerUnit,
                listing.IsHq));
            remaining -= used;
            if (remaining <= 0)
            {
                return true;
            }
        }

        selectedListings.Clear();
        return false;
    }

    private static MarketCoverageOption CreateOption(
        DetailedShoppingPlan plan,
        MarketCoverageTier tier,
        MarketCoverageQualityPolicy qualityPolicy,
        bool isDefaultEligible,
        IReadOnlyList<MarketCoverageListing> listings)
    {
        var worlds = listings
            .GroupBy(listing => new { listing.DataCenter, listing.WorldName })
            .Select(group => new MarketCoverageWorld(
                group.Key.DataCenter,
                group.Key.WorldName,
                group.Sum(listing => listing.QuantityUsed),
                group.Sum(listing => listing.QuantityPurchased),
                group.Sum(listing => listing.QuantityUsed * listing.PricePerUnit),
                group.Sum(listing => listing.QuantityPurchased * listing.PricePerUnit)))
            .OrderBy(world => world.DataCenter)
            .ThenBy(world => world.WorldName)
            .ToList();

        var quantityToPurchase = worlds.Sum(world => world.QuantityToPurchase);
        var exactNeededCost = worlds.Sum(world => world.ExactNeededCost);
        var cashOutCost = worlds.Sum(world => world.CashOutCost);
        var candidateId = string.Join(
            "-",
            plan.ItemId,
            plan.QuantityNeeded,
            tier.ToString().ToLowerInvariant(),
            qualityPolicy.ToString().ToLowerInvariant(),
            string.Join("_", worlds.Select(world => $"{world.DataCenter}.{world.WorldName}")));

        return new MarketCoverageOption(
            candidateId,
            tier,
            MarketCoverageKind.SupportedListings,
            qualityPolicy,
            plan.QuantityNeeded,
            quantityToPurchase,
            Math.Max(0, quantityToPurchase - plan.QuantityNeeded),
            exactNeededCost,
            cashOutCost,
            plan.QuantityNeeded > 0 ? exactNeededCost / plan.QuantityNeeded : 0,
            MarketCoveragePriceBand.Unknown,
            worlds,
            listings,
            new MarketCoverageFriction(
                worlds.Count,
                worlds.Select(world => world.DataCenter).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                worlds.Count == 0 ? 0 : worlds.Min(world => world.QuantityCovered),
                worlds.Count == 0 ? 0 : worlds.Max(world => world.QuantityCovered),
                Math.Max(0, quantityToPurchase - plan.QuantityNeeded)),
            MarketCoverageSavings.None,
            isDefaultEligible,
            DegradedReason: null);
    }

    private static bool IsMarketWorld(WorldShoppingSummary world)
    {
        return !string.Equals(
            world.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);
    }
}
