using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

internal static class MarketListingClassification
{
    public static MarketListingCompetitiveness ClassifyCompetitiveness(
        long pricePerUnit,
        MarketListingPriceSanity priceSanity,
        MarketPriceThresholds thresholds,
        bool excludeOutliers)
    {
        if (priceSanity == MarketListingPriceSanity.Insane ||
            (excludeOutliers && priceSanity == MarketListingPriceSanity.Outlier))
        {
            return MarketListingCompetitiveness.Excluded;
        }

        if (thresholds.DealCeilingUnitPrice > 0 && pricePerUnit <= thresholds.DealCeilingUnitPrice)
        {
            return MarketListingCompetitiveness.Deal;
        }

        if (thresholds.CompetitiveCeilingUnitPrice > 0 && pricePerUnit <= thresholds.CompetitiveCeilingUnitPrice)
        {
            return MarketListingCompetitiveness.Competitive;
        }

        if (thresholds.SaneCeilingUnitPrice > 0 && pricePerUnit <= thresholds.SaneCeilingUnitPrice)
        {
            return MarketListingCompetitiveness.Uncompetitive;
        }

        return MarketListingCompetitiveness.Excluded;
    }
}
