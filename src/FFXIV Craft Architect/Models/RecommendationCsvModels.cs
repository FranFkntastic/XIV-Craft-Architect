using CsvHelper.Configuration;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Models;

/// <summary>
/// Plan-specific shopping recommendations stored in CSV format.
/// This is the "strategy" file that records what was recommended for this specific plan.
/// Each row represents one world option for one item.
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
    /// Name of the world this row represents.
    /// </summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this world is the recommended one for this item.
    /// </summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// [Deprecated] Kept for backward compatibility. Use WorldName with IsRecommended=true.
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
    /// Convert this row to a WorldShoppingSummary.
    /// </summary>
    public WorldShoppingSummary ToWorldSummary()
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

        // Use WorldName if available, fall back to RecommendedWorld for backward compat
        var worldName = !string.IsNullOrEmpty(WorldName) ? WorldName : RecommendedWorld;

        return new WorldShoppingSummary
        {
            WorldName = worldName,
            TotalCost = TotalCost,
            AveragePricePerUnit = AveragePricePerUnit,
            ListingsUsed = ListingsUsed,
            IsFullyUnderAverage = IsFullyUnderAverage,
            TotalQuantityPurchased = TotalQuantityPurchased,
            ExcessQuantity = ExcessQuantity,
            Listings = listings,
            BestSingleListing = listings.FirstOrDefault()
        };
    }

    /// <summary>
    /// [Deprecated] Use ToWorldSummary() and group by ItemId at the service layer.
    /// Kept for backward compatibility.
    /// </summary>
    public DetailedShoppingPlan ToShoppingPlan()
    {
        var worldSummary = ToWorldSummary();
        return new DetailedShoppingPlan
        {
            ItemId = ItemId,
            Name = Name,
            QuantityNeeded = QuantityNeeded,
            DCAveragePrice = DCAveragePrice,
            RecommendedWorld = IsRecommended ? worldSummary : null,
            WorldOptions = new List<WorldShoppingSummary> { worldSummary }
        };
    }

    /// <summary>
    /// Create CSV records from a DetailedShoppingPlan (one row per world).
    /// </summary>
    public static List<RecommendationCsvRecord> FromShoppingPlanToRecords(DetailedShoppingPlan plan)
    {
        var records = new List<RecommendationCsvRecord>();
        var worldsToSave = plan.WorldOptions.Any() ? plan.WorldOptions : 
                          (plan.RecommendedWorld != null ? new List<WorldShoppingSummary> { plan.RecommendedWorld } : new List<WorldShoppingSummary>());

        foreach (var world in worldsToSave)
        {
            var isRecommended = plan.RecommendedWorld?.WorldName == world.WorldName;
            var record = new RecommendationCsvRecord
            {
                ItemId = plan.ItemId,
                Name = plan.Name,
                QuantityNeeded = plan.QuantityNeeded,
                DCAveragePrice = plan.DCAveragePrice,
                GeneratedAt = DateTime.UtcNow,
                WorldName = world.WorldName,
                IsRecommended = isRecommended,
                RecommendedWorld = isRecommended ? world.WorldName : string.Empty,
                TotalCost = world.TotalCost,
                AveragePricePerUnit = world.AveragePricePerUnit,
                ListingsUsed = world.ListingsUsed,
                IsFullyUnderAverage = world.IsFullyUnderAverage,
                TotalQuantityPurchased = world.TotalQuantityPurchased,
                ExcessQuantity = world.ExcessQuantity
            };

            // Extract up to 3 listings
            var listings = world.Listings
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

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// [Deprecated] Use FromShoppingPlanToRecords() instead.
    /// Kept for backward compatibility - only saves the recommended world.
    /// </summary>
    public static RecommendationCsvRecord FromShoppingPlan(DetailedShoppingPlan plan)
    {
        var records = FromShoppingPlanToRecords(plan);
        return records.FirstOrDefault() ?? new RecommendationCsvRecord 
        { 
            ItemId = plan.ItemId,
            Name = plan.Name,
            QuantityNeeded = plan.QuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice
        };
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
        Map(m => m.WorldName).Name("worldName");
        Map(m => m.IsRecommended).Name("isRecommended");
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
