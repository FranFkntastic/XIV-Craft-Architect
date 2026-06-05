using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketIntelligenceSummaryHydrator
{
    public static IReadOnlyList<MarketItemAnalysis> HydrateMarketItemAnalyses(
        MarketIntelligencePublicationSummary? summary)
    {
        if (summary == null)
        {
            return [];
        }

        return summary.Items
            .Select(item => new MarketItemAnalysis
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.QuantityNeeded,
                Scope = item.Scope,
                LoadedAtUtc = summary.PublicationContext.PublishedAtUtc,
                AnalysisScopeBaselineUnitPrice = item.BaselineUnitPrice,
                AnalysisScopeAverageUnitPrice = item.AverageUnitPrice,
                AnalysisScopeCompetitiveAverageUnitPrice = item.CompetitiveAverageUnitPrice,
                AnalysisScopeMedianUnitPrice = item.MedianUnitPrice,
                CompetitiveThresholdUnitPrice = item.CompetitiveThresholdUnitPrice,
                SaneThresholdUnitPrice = item.SaneThresholdUnitPrice,
                RequestedDataCenters = summary.PublicationContext.RequestedDataCenters,
                PresentDataCenters = item.Worlds
                    .Select(world => world.World.DataCenter)
                    .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                MissingDataCenters = [],
                WorstDataQualityBucket = item.DataQualityBucket,
                Worlds = item.Worlds.Select(world => HydrateWorldMarketAnalysis(item, world)).ToList(),
                Warning = item.Warning
            })
            .ToArray();
    }

    public static IReadOnlyList<DetailedShoppingPlan> HydrateShoppingPlans(
        MarketIntelligencePublicationSummary? summary)
    {
        if (summary == null)
        {
            return [];
        }

        return summary.Items
            .Select(item => new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                IconId = item.IconId,
                QuantityNeeded = item.QuantityNeeded,
                DCAveragePrice = item.AverageUnitPrice,
                WorldOptions = item.Worlds.Select(HydrateWorldShoppingSummary).ToList(),
                RecommendedWorld = item.RecommendedWorld is null
                    ? null
                    : HydrateRecommendedWorldShoppingSummary(item),
                RecommendedSplit = item.RecommendedSplit.Select(HydrateSplitPurchase).ToList(),
                MarketDataWarning = item.Warning,
                Vendors = item.Vendors.Select(CloneVendor).ToList()
            })
            .ToArray();
    }

    private static WorldMarketAnalysis HydrateWorldMarketAnalysis(
        MarketItemSummary item,
        WorldMarketSummary summary)
    {
        var scopeCompetitiveQuantity = summary.ScopeCompetitiveQuantity > 0
            ? summary.ScopeCompetitiveQuantity
            : summary.CompetitiveQuantity;
        var scopeSaneQuantity = summary.ScopeSaneQuantity > 0
            ? summary.ScopeSaneQuantity
            : summary.TotalListingQuantity;
        var totalSaneQuantity = summary.TotalSaneQuantity > 0
            ? summary.TotalSaneQuantity
            : scopeSaneQuantity;
        var scopeCompetitiveCoverageRatio = summary.ScopeCompetitiveCoverageRatio > 0
            ? summary.ScopeCompetitiveCoverageRatio
            : summary.CompetitiveCoverageRatio;
        var scopeSaneCoverageRatio = summary.ScopeSaneCoverageRatio > 0
            ? summary.ScopeSaneCoverageRatio
            : CalculateCoverageRatio(scopeSaneQuantity, summary.QuantityNeeded);
        var saneCoverageRatio = summary.SaneCoverageRatio > 0
            ? summary.SaneCoverageRatio
            : CalculateCoverageRatio(totalSaneQuantity, summary.QuantityNeeded);
        var scopeCompetitiveAverageUnitPrice = summary.ScopeCompetitiveAverageUnitPrice > 0
            ? summary.ScopeCompetitiveAverageUnitPrice
            : summary.CompetitiveAverageUnitPrice;

        return new WorldMarketAnalysis
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            QuantityNeeded = summary.QuantityNeeded,
            CompetitiveQuantity = summary.CompetitiveQuantity,
            LocalCompetitiveQuantity = summary.LocalCompetitiveQuantity > 0
                ? summary.LocalCompetitiveQuantity
                : summary.CompetitiveQuantity,
            ScopeCompetitiveQuantity = scopeCompetitiveQuantity,
            ScopeSaneQuantity = scopeSaneQuantity,
            ScopeUncompetitiveQuantity = summary.ScopeUncompetitiveQuantity,
            ScopeInsaneQuantity = summary.ScopeInsaneQuantity,
            TotalSaneQuantity = totalSaneQuantity,
            TotalListingQuantity = summary.TotalListingQuantity,
            CompetitiveCoverageRatio = summary.CompetitiveCoverageRatio,
            ScopeCompetitiveCoverageRatio = scopeCompetitiveCoverageRatio,
            ScopeSaneCoverageRatio = scopeSaneCoverageRatio,
            SaneCoverageRatio = saneCoverageRatio,
            AnalysisScopeBaselineUnitPrice = item.BaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = item.AverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = item.CompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = scopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = item.MedianUnitPrice,
            CompetitiveThresholdUnitPrice = item.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = item.SaneThresholdUnitPrice,
            CoverageBucket = summary.CoverageBucket,
            FetchedAtUtc = summary.FetchedAtUtc,
            MarketUploadedAtUtc = summary.MarketUploadedAtUtc,
            DataAge = summary.DataAge,
            DataAgeSource = summary.DataAgeSource,
            DataQualityBucket = summary.DataQualityBucket,
            Scores = summary.Scores.ToList()
        };
    }

    private static WorldShoppingSummary HydrateRecommendedWorldShoppingSummary(MarketItemSummary item)
    {
        var world = item.RecommendedWorld!.Value;
        var matchingWorld = item.Worlds.FirstOrDefault(summary =>
            string.Equals(summary.World.DataCenter, world.DataCenter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(summary.World.WorldName, world.WorldName, StringComparison.OrdinalIgnoreCase));
        var averagePrice = item.RecommendedWorldAveragePricePerUnit > 0
            ? item.RecommendedWorldAveragePricePerUnit
            : matchingWorld?.CompetitiveAverageUnitPrice > 0
            ? matchingWorld.CompetitiveAverageUnitPrice
            : item.CompetitiveAverageUnitPrice;

        return new WorldShoppingSummary
        {
            DataCenter = world.DataCenter,
            WorldName = world.WorldName,
            TotalCost = item.RecommendedTotalCost,
            AveragePricePerUnit = averagePrice,
            TotalQuantityPurchased = item.QuantityNeeded,
            VendorName = item.RecommendedWorldVendorName
        };
    }

    private static WorldShoppingSummary HydrateWorldShoppingSummary(WorldMarketSummary summary)
    {
        return new WorldShoppingSummary
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            AveragePricePerUnit = summary.CompetitiveAverageUnitPrice,
            TotalQuantityPurchased = summary.CompetitiveQuantity
        };
    }

    private static SplitWorldPurchase HydrateSplitPurchase(MarketSplitPurchaseSummary summary)
    {
        return new SplitWorldPurchase
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            QuantityToBuy = summary.QuantityToBuy,
            PricePerUnit = summary.PricePerUnit,
            EffectivePricePerNeededUnit = summary.EffectivePricePerNeededUnit,
            TotalCost = summary.TotalCost,
            IsPartial = summary.IsPartial,
            TravelContext = summary.TravelContext,
            ExcessAvailable = summary.ExcessAvailable
        };
    }

    private static VendorInfo CloneVendor(VendorInfo vendor)
    {
        return new VendorInfo
        {
            Name = vendor.Name,
            Location = vendor.Location,
            Price = vendor.Price,
            Currency = vendor.Currency,
            AlternateLocations = vendor.AlternateLocations.ToList(),
            Coordinates = vendor.Coordinates?.ToList()
        };
    }

    private static decimal CalculateCoverageRatio(int quantity, int quantityNeeded)
    {
        return quantityNeeded > 0
            ? Math.Round(quantity / (decimal)quantityNeeded, 2, MidpointRounding.AwayFromZero)
            : 0;
    }
}
