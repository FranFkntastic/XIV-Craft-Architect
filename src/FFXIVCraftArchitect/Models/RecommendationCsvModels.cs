using CsvHelper.Configuration;

namespace FFXIVCraftArchitect.Models;

/// <summary>
/// Plan-specific shopping recommendations stored in CSV format.
/// This is the "strategy" file that records what was recommended for this specific plan.
/// </summary>
public class RecommendationCsvRecord
{
    /// <summary>
    /// Item ID from FFXIV.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// Item name for display.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// How many are needed for this plan.
    /// </summary>
    public int QuantityNeeded { get; set; }

    /// <summary>
    /// Recommended world to purchase from.
    /// </summary>
    public string RecommendedWorld { get; set; } = string.Empty;

    /// <summary>
    /// Total cost at recommended world.
    /// </summary>
    public long TotalCost { get; set; }

    /// <summary>
    /// Average price per unit at recommended world.
    /// </summary>
    public decimal AveragePricePerUnit { get; set; }

    /// <summary>
    /// Number of listings used to fulfill quantity.
    /// </summary>
    public int ListingsUsed { get; set; }

    /// <summary>
    /// Data center average price at time of recommendation.
    /// </summary>
    public decimal DCAveragePrice { get; set; }

    /// <summary>
    /// Whether all listings used were under DC average.
    /// </summary>
    public bool IsFullyUnderAverage { get; set; }

    /// <summary>
    /// Total quantity purchased (may exceed needed due to stack sizes).
    /// </summary>
    public int TotalQuantityPurchased { get; set; }

    /// <summary>
    /// Excess quantity purchased beyond what's needed.
    /// </summary>
    public int ExcessQuantity { get; set; }

    /// <summary>
    /// Timestamp when this recommendation was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    // Listing 1 (primary)
    public string? RetainerName1 { get; set; }
    public long? Price1 { get; set; }
    public int? Qty1 { get; set; }
    public bool? IsHq1 { get; set; }

    // Listing 2 (if needed)
    public string? RetainerName2 { get; set; }
    public long? Price2 { get; set; }
    public int? Qty2 { get; set; }
    public bool? IsHq2 { get; set; }

    // Listing 3 (if needed)
    public string? RetainerName3 { get; set; }
    public long? Price3 { get; set; }
    public int? Qty3 { get; set; }
    public bool? IsHq3 { get; set; }

    /// <summary>
    /// Convert back to a DetailedShoppingPlan for display.
    /// </summary>
    public DetailedShoppingPlan ToShoppingPlan()
    {
        var listings = new List<ShoppingListingEntry>();
        
        // Add listing 1 if present
        if (!string.IsNullOrEmpty(RetainerName1) && Price1.HasValue && Qty1.HasValue)
        {
            listings.Add(new ShoppingListingEntry
            {
                Quantity = Qty1.Value,
                PricePerUnit = Price1.Value,
                RetainerName = RetainerName1,
                IsHq = IsHq1 ?? false,
                IsUnderAverage = Price1.Value < DCAveragePrice,
                NeededFromStack = Math.Min(Qty1.Value, QuantityNeeded),
                ExcessQuantity = Math.Max(0, Qty1.Value - QuantityNeeded),
                IsAdditionalOption = false
            });
        }

        // Add listing 2 if present
        if (!string.IsNullOrEmpty(RetainerName2) && Price2.HasValue && Qty2.HasValue)
        {
            var neededFromStack2 = Math.Min(Qty2.Value, Math.Max(0, QuantityNeeded - (Qty1 ?? 0)));
            listings.Add(new ShoppingListingEntry
            {
                Quantity = Qty2.Value,
                PricePerUnit = Price2.Value,
                RetainerName = RetainerName2,
                IsHq = IsHq2 ?? false,
                IsUnderAverage = Price2.Value < DCAveragePrice,
                NeededFromStack = neededFromStack2,
                ExcessQuantity = Math.Max(0, Qty2.Value - neededFromStack2),
                IsAdditionalOption = false
            });
        }

        // Add listing 3 if present
        if (!string.IsNullOrEmpty(RetainerName3) && Price3.HasValue && Qty3.HasValue)
        {
            var alreadyCovered = (Qty1 ?? 0) + (Qty2 ?? 0);
            var neededFromStack3 = Math.Min(Qty3.Value, Math.Max(0, QuantityNeeded - alreadyCovered));
            listings.Add(new ShoppingListingEntry
            {
                Quantity = Qty3.Value,
                PricePerUnit = Price3.Value,
                RetainerName = RetainerName3,
                IsHq = IsHq3 ?? false,
                IsUnderAverage = Price3.Value < DCAveragePrice,
                NeededFromStack = neededFromStack3,
                ExcessQuantity = Math.Max(0, Qty3.Value - neededFromStack3),
                IsAdditionalOption = false
            });
        }

        var recommendedWorld = new WorldShoppingSummary
        {
            WorldName = RecommendedWorld,
            TotalCost = TotalCost,
            AveragePricePerUnit = AveragePricePerUnit,
            ListingsUsed = ListingsUsed,
            IsFullyUnderAverage = IsFullyUnderAverage,
            TotalQuantityPurchased = TotalQuantityPurchased,
            ExcessQuantity = ExcessQuantity,
            Listings = listings,
            BestSingleListing = listings.FirstOrDefault()
        };

        return new DetailedShoppingPlan
        {
            ItemId = ItemId,
            Name = Name,
            QuantityNeeded = QuantityNeeded,
            DCAveragePrice = DCAveragePrice,
            RecommendedWorld = recommendedWorld,
            WorldOptions = new List<WorldShoppingSummary> { recommendedWorld }
        };
    }

    /// <summary>
    /// Create a CSV record from a DetailedShoppingPlan.
    /// </summary>
    public static RecommendationCsvRecord FromShoppingPlan(DetailedShoppingPlan plan)
    {
        var record = new RecommendationCsvRecord
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            QuantityNeeded = plan.QuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            GeneratedAt = DateTime.UtcNow
        };

        if (plan.RecommendedWorld != null)
        {
            record.RecommendedWorld = plan.RecommendedWorld.WorldName;
            record.TotalCost = plan.RecommendedWorld.TotalCost;
            record.AveragePricePerUnit = plan.RecommendedWorld.AveragePricePerUnit;
            record.ListingsUsed = plan.RecommendedWorld.ListingsUsed;
            record.IsFullyUnderAverage = plan.RecommendedWorld.IsFullyUnderAverage;
            record.TotalQuantityPurchased = plan.RecommendedWorld.TotalQuantityPurchased;
            record.ExcessQuantity = plan.RecommendedWorld.ExcessQuantity;

            // Extract up to 3 listings
            var listings = plan.RecommendedWorld.Listings
                .Where(l => !l.IsAdditionalOption)
                .Take(3)
                .ToList();

            if (listings.Count > 0)
            {
                record.RetainerName1 = listings[0].RetainerName;
                record.Price1 = listings[0].PricePerUnit;
                record.Qty1 = listings[0].Quantity;
                record.IsHq1 = listings[0].IsHq;
            }

            if (listings.Count > 1)
            {
                record.RetainerName2 = listings[1].RetainerName;
                record.Price2 = listings[1].PricePerUnit;
                record.Qty2 = listings[1].Quantity;
                record.IsHq2 = listings[1].IsHq;
            }

            if (listings.Count > 2)
            {
                record.RetainerName3 = listings[2].RetainerName;
                record.Price3 = listings[2].PricePerUnit;
                record.Qty3 = listings[2].Quantity;
                record.IsHq3 = listings[2].IsHq;
            }
        }

        return record;
    }
}

/// <summary>
/// CSV mapping configuration for RecommendationCsvRecord.
/// </summary>
public sealed class RecommendationCsvMap : ClassMap<RecommendationCsvRecord>
{
    public RecommendationCsvMap()
    {
        Map(m => m.ItemId).Name("itemId");
        Map(m => m.Name).Name("name");
        Map(m => m.QuantityNeeded).Name("quantityNeeded");
        Map(m => m.RecommendedWorld).Name("recommendedWorld");
        Map(m => m.TotalCost).Name("totalCost");
        Map(m => m.AveragePricePerUnit).Name("averagePricePerUnit");
        Map(m => m.ListingsUsed).Name("listingsUsed");
        Map(m => m.DCAveragePrice).Name("dcAveragePrice");
        Map(m => m.IsFullyUnderAverage).Name("isFullyUnderAverage");
        Map(m => m.TotalQuantityPurchased).Name("totalQuantityPurchased");
        Map(m => m.ExcessQuantity).Name("excessQuantity");
        Map(m => m.GeneratedAt).Name("generatedAt");
        
        Map(m => m.RetainerName1).Name("retainerName1").Optional();
        Map(m => m.Price1).Name("price1").Optional();
        Map(m => m.Qty1).Name("qty1").Optional();
        Map(m => m.IsHq1).Name("isHq1").Optional();
        
        Map(m => m.RetainerName2).Name("retainerName2").Optional();
        Map(m => m.Price2).Name("price2").Optional();
        Map(m => m.Qty2).Name("qty2").Optional();
        Map(m => m.IsHq2).Name("isHq2").Optional();
        
        Map(m => m.RetainerName3).Name("retainerName3").Optional();
        Map(m => m.Price3).Name("price3").Optional();
        Map(m => m.Qty3).Name("qty3").Optional();
        Map(m => m.IsHq3).Name("isHq3").Optional();
    }
}
