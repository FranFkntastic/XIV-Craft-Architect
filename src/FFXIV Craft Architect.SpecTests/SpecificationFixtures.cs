using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

internal static class SpecificationFixtures
{
    public static MarketAnalysisConfig Config(
        int tolerance = 11,
        MarketTravelPriority priority = MarketTravelPriority.WorldVisitsFirst) => new()
        {
            TravelTolerance = tolerance,
            TravelPriority = priority,
            MaxWorldsPerItem = 8,
            MaxPriceMultiplier = 2.5m
        };

    public static JointAcquisitionRouteOptimizationService JointService() =>
        new(new MarketShoppingService(null!));

    public static PlanNode MarketNode(
        int itemId,
        string name,
        int quantity = 1,
        bool hq = false,
        string? nodeId = null) => new()
        {
            ItemId = itemId,
            Name = name,
            Quantity = quantity,
            NodeId = nodeId ?? $"node-{itemId}",
            Source = hq ? AcquisitionSource.MarketBuyHq : AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            CanBeHq = hq,
            MustBeHq = hq
        };

    public static PlanNode MakeOrBuyNode(
        int itemId,
        string name,
        PlanNode child,
        string? nodeId = null)
    {
        var node = new PlanNode
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            NodeId = nodeId ?? $"node-{itemId}",
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = [child]
        };
        child.Parent = node;
        return node;
    }

    public static DetailedShoppingPlan Evidence(
        int itemId,
        string name,
        int quantityNeeded,
        params WorldShoppingSummary[] worlds) => new()
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantityNeeded,
            WorldOptions = [.. worlds]
        };

    public static WorldShoppingSummary World(
        string dataCenter,
        string worldName,
        int stackQuantity,
        long unitPrice,
        bool hq = false) => World(
            dataCenter,
            worldName,
            (stackQuantity, unitPrice, hq));

    public static WorldShoppingSummary World(
        string dataCenter,
        string worldName,
        params (int Quantity, long UnitPrice, bool IsHq)[] stacks)
    {
        var listings = stacks.Select(stack => new ShoppingListingEntry
        {
            Quantity = stack.Quantity,
            NeededFromStack = stack.Quantity,
            PricePerUnit = stack.UnitPrice,
            IsHq = stack.IsHq
        }).ToList();
        var quantity = listings.Sum(listing => listing.Quantity);
        var cost = listings.Sum(listing => (long)listing.Quantity * listing.PricePerUnit);

        return new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            TotalCost = cost,
            AveragePricePerUnit = quantity == 0 ? 0 : cost / (decimal)quantity,
            ListingsUsed = listings.Count,
            Listings = listings,
            TotalQuantityPurchased = quantity,
            HasSufficientStock = true,
            MarketDataQualityBucket = MarketDataQualityBucket.Current,
            MarketDataQualityScore = 100
        };
    }

    public static CachedWorldData CachedWorld(
        string worldName,
        int quantity,
        long unitPrice,
        int? worldId = null,
        bool hq = false) => new()
        {
            WorldId = worldId,
            WorldName = worldName,
            Listings =
            [
                new CachedListing
                {
                    Quantity = quantity,
                    PricePerUnit = unitPrice,
                    RetainerName = $"{worldName} Retainer",
                    IsHq = hq
                }
            ]
        };

    public static MarketEvidenceSet EvidenceSet(
        int itemId,
        params CachedMarketData[] entries)
    {
        var byKey = entries.ToDictionary(entry => (itemId, entry.DataCenter));
        var requests = entries.Select(entry => (itemId, entry.DataCenter)).ToList();
        return new MarketEvidenceSet(
            byKey,
            requests,
            requests.Count == 1 ? MarketFetchScope.SelectedDataCenter : MarketFetchScope.EntireRegion,
            entries.Select(entry => entry.DataCenter).ToList(),
            entries.FirstOrDefault()?.DataCenter ?? string.Empty,
            "North America",
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc));
    }
}
