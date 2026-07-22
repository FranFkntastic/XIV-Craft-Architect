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
        var nqOrHqListings = EnumerateMarketListings(plan, MarketCoverageQualityPolicy.NqOrHq);
        var hqListings = EnumerateMarketListings(plan, MarketCoverageQualityPolicy.HqOnly);
        candidates.AddRange(BuildSplitCandidates(plan, MarketCoverageQualityPolicy.NqOrHq, nqOrHqListings));
        candidates.AddRange(BuildSplitCandidates(plan, MarketCoverageQualityPolicy.HqOnly, hqListings));

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
        MarketCoverageQualityPolicy qualityPolicy,
        IReadOnlyList<MarketCoverageListing> orderedListings)
    {
        if (TryCoverAcrossWorlds(
                plan,
                qualityPolicy,
                maxWorlds: DefaultCompactWorldCap,
                orderedListings,
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
                orderedListings,
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
                orderedListings,
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
        var eligibleListings = world.Listings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .Where(listing => qualityPolicy != MarketCoverageQualityPolicy.HqOnly || listing.IsHq)
            .ToList();
        if (!IsPriceOrdered(eligibleListings))
        {
            eligibleListings = eligibleListings
                .OrderBy(listing => listing.PricePerUnit)
                .ToList();
        }

        foreach (var listing in eligibleListings)
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
        IReadOnlyList<MarketCoverageListing> orderedListings,
        out List<MarketCoverageListing> selectedListings)
    {
        selectedListings = [];
        var remaining = plan.QuantityNeeded;
        var selectedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in orderedListings)
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

    private static List<MarketCoverageListing> EnumerateMarketListings(
        DetailedShoppingPlan plan,
        MarketCoverageQualityPolicy qualityPolicy)
    {
        var sources = plan.WorldOptions
            .Where(IsMarketWorld)
            .Select(world => world.Listings
                .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                .Where(listing => qualityPolicy != MarketCoverageQualityPolicy.HqOnly || listing.IsHq)
                .Select(listing => new MarketCoverageListing(
                    world.DataCenter,
                    world.WorldName,
                    listing.Quantity,
                    QuantityUsed: 0,
                    QuantityPurchased: 0,
                    listing.PricePerUnit,
                    listing.IsHq))
                .ToList())
            .Where(source => source.Count > 0)
            .ToList();
        for (var index = 0; index < sources.Count; index++)
        {
            if (!IsCoverageOrdered(sources[index]))
            {
                sources[index] = sources[index]
                    .OrderBy(listing => listing.PricePerUnit)
                    .ThenByDescending(listing => listing.QuantityAvailable)
                    .ToList();
            }
        }

        var queue = new PriorityQueue<CoverageCursor, CoveragePriority>(CoveragePriorityComparer.Instance);
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            Enqueue(sourceIndex, listingIndex: 0);
        }

        var ordered = new List<MarketCoverageListing>(sources.Sum(source => source.Count));
        while (queue.TryDequeue(out var cursor, out _))
        {
            ordered.Add(cursor.Listing);
            Enqueue(cursor.SourceIndex, cursor.ListingIndex + 1);
        }
        return ordered;

        void Enqueue(int sourceIndex, int listingIndex)
        {
            if (listingIndex >= sources[sourceIndex].Count)
            {
                return;
            }
            var listing = sources[sourceIndex][listingIndex];
            queue.Enqueue(
                new CoverageCursor(listing, sourceIndex, listingIndex),
                new CoveragePriority(
                    listing.PricePerUnit,
                    listing.QuantityAvailable,
                    listing.DataCenter,
                    listing.WorldName,
                    sourceIndex,
                    listingIndex));
        }
    }

    private static bool IsPriceOrdered(IReadOnlyList<ShoppingListingEntry> listings)
    {
        for (var index = 1; index < listings.Count; index++)
        {
            if (listings[index - 1].PricePerUnit > listings[index].PricePerUnit)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsCoverageOrdered(IReadOnlyList<MarketCoverageListing> listings)
    {
        for (var index = 1; index < listings.Count; index++)
        {
            var previous = listings[index - 1];
            var current = listings[index];
            if (previous.PricePerUnit > current.PricePerUnit ||
                previous.PricePerUnit == current.PricePerUnit &&
                previous.QuantityAvailable < current.QuantityAvailable)
            {
                return false;
            }
        }
        return true;
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

    private readonly record struct CoverageCursor(
        MarketCoverageListing Listing,
        int SourceIndex,
        int ListingIndex);

    private readonly record struct CoveragePriority(
        decimal PricePerUnit,
        int QuantityAvailable,
        string DataCenter,
        string WorldName,
        int SourceIndex,
        int ListingIndex);

    private sealed class CoveragePriorityComparer : IComparer<CoveragePriority>
    {
        public static CoveragePriorityComparer Instance { get; } = new();

        public int Compare(CoveragePriority left, CoveragePriority right)
        {
            var comparison = left.PricePerUnit.CompareTo(right.PricePerUnit);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = right.QuantityAvailable.CompareTo(left.QuantityAvailable);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Comparer<string>.Default.Compare(left.DataCenter, right.DataCenter);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Comparer<string>.Default.Compare(left.WorldName, right.WorldName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.SourceIndex.CompareTo(right.SourceIndex);
            return comparison != 0 ? comparison : left.ListingIndex.CompareTo(right.ListingIndex);
        }
    }
}
