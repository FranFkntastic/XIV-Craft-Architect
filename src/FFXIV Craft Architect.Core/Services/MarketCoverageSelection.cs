using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketCoverageSelection
{
    public static IEnumerable<MarketCoverageOption> GetCandidates(MarketCoverageSet? coverageSet)
    {
        if (coverageSet == null)
        {
            return [];
        }

        return coverageSet.AllCandidates
            .Concat([
                coverageSet.SingleWorld,
                coverageSet.CompactSplit,
                coverageSet.WideSplit,
                coverageSet.CheapestObserved
            ])
            .Where(candidate => candidate != null)
            .Cast<MarketCoverageOption>()
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    public static MarketCoverageOption? GetDefaultOption(
        MarketCoverageSet? coverageSet,
        MarketCoverageQualityPolicy? qualityPolicy = null)
    {
        var candidates = GetCandidates(coverageSet)
            .Where(candidate => candidate.Kind == MarketCoverageKind.SupportedListings)
            .Where(candidate => candidate.IsDefaultEligible);

        if (qualityPolicy.HasValue)
        {
            candidates = candidates.Where(candidate => candidate.QualityPolicy == qualityPolicy.Value);
        }

        return candidates
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .ThenBy(candidate => candidate.CashOutCost)
            .FirstOrDefault();
    }
}
