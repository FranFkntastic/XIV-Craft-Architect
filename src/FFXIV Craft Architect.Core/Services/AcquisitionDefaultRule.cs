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
    /// <summary>
    /// Tie-break for buying listings below the NPC vendor price and the rest from the vendor
    /// (Trade Margins' Market + vendor route): after either single source, before crafting.
    /// </summary>
    public const int MarketWithVendorTopUpTieBreak = 3;

    /// <summary>The cheapest priced candidate; ties prefer vendor, then market, then craft.</summary>
    public static AcquisitionSource? Cheapest(IEnumerable<(AcquisitionSource Source, decimal? Cost)> candidates)
    {
        return Cheapest(candidates.Select(candidate => (candidate.Source, TieBreak(candidate.Source), candidate.Cost)));
    }

    /// <summary>
    /// The cheapest priced candidate for a caller with its own route vocabulary; ties go to the
    /// lower <paramref name="candidates"/> tie-break, taken from <see cref="TieBreak"/> or
    /// <see cref="MarketWithVendorTopUpTieBreak"/>.
    /// </summary>
    public static TRoute? Cheapest<TRoute>(IEnumerable<(TRoute Route, int TieBreak, decimal? Cost)> candidates)
        where TRoute : struct
    {
        return candidates
            .Where(candidate => candidate.Cost.HasValue)
            .OrderBy(candidate => candidate.Cost!.Value)
            .ThenBy(candidate => candidate.TieBreak)
            .Select(candidate => (TRoute?)candidate.Route)
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
            AcquisitionSource.Craft => 4,
            _ => 5
        };
    }
}
