using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// The labor-aware craft-versus-buy default shared by commission plans
/// (<see cref="AcquisitionPlanningService"/>) and Trade Margins. Callers supply only
/// eligible candidates: a market route that cannot fill the whole quantity from listings
/// in the pricing scope is never offered as a default.
/// </summary>
public static class AcquisitionDefaultRule
{
    /// <summary>The cheapest priced candidate; ties prefer vendor, then market, then craft.</summary>
    public static AcquisitionSource? Cheapest(IEnumerable<(AcquisitionSource Source, decimal? Cost)> candidates)
    {
        return candidates
            .Where(candidate => candidate.Cost.HasValue)
            .OrderBy(candidate => candidate.Cost!.Value)
            .ThenBy(candidate => TieBreak(candidate.Source))
            .Select(candidate => (AcquisitionSource?)candidate.Source)
            .FirstOrDefault();
    }

    /// <summary>
    /// Crafting cost: the inputs at their chosen cost plus labor for each synth. An unpriced
    /// input leaves the craft unpriced, so labor alone never beats a real purchase.
    /// </summary>
    public static decimal? CraftCost(decimal? inputs, int synths, decimal laborGilPerSynth)
    {
        return inputs is { } cost ? cost + Math.Max(0, laborGilPerSynth) * synths : null;
    }

    public static int TieBreak(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.VendorBuy => 0,
            AcquisitionSource.MarketBuyNq => 1,
            AcquisitionSource.MarketBuyHq => 2,
            AcquisitionSource.Craft => 3,
            _ => 4
        };
    }
}
