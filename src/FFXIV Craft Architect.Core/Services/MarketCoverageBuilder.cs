using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketCoverageBuilder
{
    private const int DefaultCompactWorldCap = 2;
    private const int WideWorldCap = 5;
    private const int MinimumSupplementalQuantity = 20;
    private const decimal MinimumSupplementalRatio = 0.05m;

    public static MarketCoverageSet Build(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var candidates = new List<MarketCoverageOption>();
        candidates.AddRange(BuildSingleWorldCandidates(plan, MarketCoverageQualityPolicy.NqOrHq));
        candidates.AddRange(BuildSingleWorldCandidates(plan, MarketCoverageQualityPolicy.HqOnly));
        candidates.AddRange(BuildSplitCandidates(plan, MarketCoverageQualityPolicy.NqOrHq));
        candidates.AddRange(BuildSplitCandidates(plan, MarketCoverageQualityPolicy.HqOnly));

        var singleWorld = candidates
            .Where(candidate => candidate.Tier == MarketCoverageTier.SingleWorld)
            .Where(candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.CashOutCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .FirstOrDefault();
        var compactSplit = candidates
            .Where(candidate => candidate.Tier == MarketCoverageTier.CompactSplit)
            .Where(candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.CashOutCost)
            .FirstOrDefault();
        var wideSplit = candidates
            .Where(candidate => candidate.Tier == MarketCoverageTier.WideSplit)
            .Where(candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .FirstOrDefault();
        var cheapestObserved = candidates
            .Where(candidate => candidate.Tier == MarketCoverageTier.CheapestObserved)
            .Where(candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .FirstOrDefault();

        return new MarketCoverageSet(
            plan.ItemId,
            plan.Name,
            plan.QuantityNeeded,
            singleWorld,
            compactSplit,
            wideSplit,
            cheapestObserved,
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

    private static IEnumerable<MarketCoverageOption> BuildSplitCandidates(
        DetailedShoppingPlan plan,
        MarketCoverageQualityPolicy qualityPolicy)
    {
        if (TryCoverAcrossWorlds(
                plan,
                qualityPolicy,
                maxWorlds: DefaultCompactWorldCap,
                out var compactListings))
        {
            var compact = CreateOption(
                plan,
                MarketCoverageTier.CompactSplit,
                qualityPolicy,
                isDefaultEligible: true,
                compactListings);
            if (IsMeaningfulSplit(compact, plan.QuantityNeeded, DefaultCompactWorldCap))
            {
                yield return compact;
            }
        }

        if (TryCoverAcrossWorlds(
                plan,
                qualityPolicy,
                maxWorlds: WideWorldCap,
                out var wideListings))
        {
            var wide = CreateOption(
                plan,
                MarketCoverageTier.WideSplit,
                qualityPolicy,
                isDefaultEligible: false,
                wideListings);
            if (wide.Friction.WorldCount > 1)
            {
                yield return wide;
            }
        }

        if (TryCoverAcrossWorlds(
                plan,
                qualityPolicy,
                maxWorlds: null,
                out var cheapestListings))
        {
            var cheapest = CreateOption(
                plan,
                MarketCoverageTier.CheapestObserved,
                qualityPolicy,
                isDefaultEligible: false,
                cheapestListings);
            if (cheapest.Friction.WorldCount > 1)
            {
                yield return cheapest;
            }
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

    private static bool TryCoverAcrossWorlds(
        DetailedShoppingPlan plan,
        MarketCoverageQualityPolicy qualityPolicy,
        int? maxWorlds,
        out List<MarketCoverageListing> selectedListings)
    {
        selectedListings = [];
        var remaining = plan.QuantityNeeded;
        var selectedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in EnumerateMarketListings(plan, qualityPolicy))
        {
            var worldKey = $"{listing.DataCenter}|{listing.WorldName}";
            if (!selectedWorlds.Contains(worldKey) &&
                maxWorlds.HasValue &&
                selectedWorlds.Count >= maxWorlds.Value)
            {
                continue;
            }

            var used = Math.Min(remaining, listing.QuantityAvailable);
            selectedListings.Add(listing with
            {
                QuantityUsed = used,
                QuantityPurchased = listing.QuantityAvailable
            });
            selectedWorlds.Add(worldKey);
            remaining -= used;
            if (remaining <= 0)
            {
                return true;
            }
        }

        selectedListings.Clear();
        return false;
    }

    private static IEnumerable<MarketCoverageListing> EnumerateMarketListings(
        DetailedShoppingPlan plan,
        MarketCoverageQualityPolicy qualityPolicy)
    {
        return plan.WorldOptions
            .Where(IsMarketWorld)
            .SelectMany(world => world.Listings
                .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                .Where(listing => qualityPolicy != MarketCoverageQualityPolicy.HqOnly || listing.IsHq)
                .Select(listing => new MarketCoverageListing(
                    world.DataCenter,
                    world.WorldName,
                    listing.Quantity,
                    QuantityUsed: 0,
                    QuantityPurchased: 0,
                    listing.PricePerUnit,
                    listing.IsHq)))
            .OrderBy(listing => listing.PricePerUnit)
            .ThenByDescending(listing => listing.QuantityAvailable)
            .ThenBy(listing => listing.DataCenter)
            .ThenBy(listing => listing.WorldName);
    }

    private static bool IsMeaningfulSplit(
        MarketCoverageOption option,
        int quantityNeeded,
        int maxWorlds)
    {
        if (option.Worlds.Count < 2 || option.Worlds.Count > maxWorlds)
        {
            return false;
        }

        var supplementalFloor = GetMinimumSupplementalContribution(quantityNeeded);
        return option.Worlds
            .OrderByDescending(world => world.QuantityCovered)
            .Skip(1)
            .All(world => world.QuantityCovered >= supplementalFloor);
    }

    private static int GetMinimumSupplementalContribution(int quantityNeeded)
    {
        return Math.Min(
            quantityNeeded,
            Math.Max(MinimumSupplementalQuantity, (int)Math.Ceiling(quantityNeeded * MinimumSupplementalRatio)));
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
