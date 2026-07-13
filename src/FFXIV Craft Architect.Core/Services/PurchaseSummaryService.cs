using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Centralized service for creating consistent purchase summaries.
/// </summary>
public class PurchaseSummaryService : IPurchaseSummaryService
{
    /// <inheritdoc />
    public PurchaseSummary CreateSummary(DetailedShoppingPlan plan)
    {
        int quantityToPurchase;
        long totalCost;
        decimal averagePricePerUnit;
        var excessQuantity = 0;

        var coverage = PurchaseRecommendationCost.GetDefaultCoverageOption(plan);
        if (coverage != null)
        {
            quantityToPurchase = coverage.QuantityToPurchase;
            totalCost = PurchaseRecommendationCost.GetRecommendedCashOutCost(plan);
            averagePricePerUnit = coverage.QuantityCovered > 0
                ? coverage.CashOutCost / coverage.QuantityCovered
                : 0;
            excessQuantity = coverage.ExcessQuantity;
        }
        else if (PurchaseRecommendationCost.UsesSplitRecommendation(plan) && plan.RecommendedSplit != null)
        {
            var recommendedSplit = plan.RecommendedSplit;
            quantityToPurchase = recommendedSplit.Sum(s => s.QuantityToBuy);
            totalCost = plan.SplitTotalCost ?? 0;
            averagePricePerUnit = quantityToPurchase > 0
                ? (decimal)totalCost / quantityToPurchase
                : 0;
        }
        else if (plan.RecommendedWorld != null)
        {
            quantityToPurchase = plan.RecommendedWorld.TotalQuantityPurchased;
            totalCost = plan.RecommendedWorld.TotalCost;
            averagePricePerUnit = plan.RecommendedWorld.AveragePricePerUnit;
        }
        else
        {
            quantityToPurchase = 0;
            totalCost = 0;
            averagePricePerUnit = 0;
        }

        excessQuantity = Math.Max(excessQuantity, quantityToPurchase - plan.QuantityNeeded);

        return new PurchaseSummary
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            QuantityToPurchase = quantityToPurchase,
            ExcessQuantity = excessQuantity,
            TotalCost = totalCost,
            AveragePricePerUnit = averagePricePerUnit,
            RecommendedWorld = plan.RecommendedWorld,
            RequiresSplitPurchase = plan.RequiresSplitPurchase,
            RecommendedSplit = plan.RecommendedSplit
        };
    }

    /// <inheritdoc />
    public List<PurchaseSummary> CreateSummaries(IEnumerable<DetailedShoppingPlan> plans)
    {
        return plans.Select(CreateSummary).ToList();
    }

    /// <inheritdoc />
    public PurchaseSummary CreateSplitSummary(
        SplitWorldPurchase split,
        int quantityNeeded,
        string itemName,
        int itemId,
        int iconId)
    {
        return new PurchaseSummary
        {
            ItemId = itemId,
            Name = itemName,
            IconId = iconId,
            QuantityNeeded = quantityNeeded,
            QuantityToPurchase = split.QuantityToBuy,
            ExcessQuantity = split.ExcessAvailable,
            TotalCost = split.TotalCost,
            AveragePricePerUnit = split.EffectivePricePerNeededUnit,
            RequiresSplitPurchase = false,
            RecommendedSplit = null
        };
    }

}
