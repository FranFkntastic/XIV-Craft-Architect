using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Reuses immutable purchase candidates while the joint acquisition solver compares
/// many plans derived from the same market evidence snapshot.
/// </summary>
internal sealed class ProcurementRouteOptimizationSession
{
    private readonly Dictionary<PurchaseCandidateCacheKey, IReadOnlyList<MarketPurchaseCandidate>> _standaloneCandidates = [];

    public IReadOnlyList<MarketPurchaseCandidate> GetOrAddCandidates(
        DetailedShoppingPlan plan,
        Func<DetailedShoppingPlan, List<MarketPurchaseCandidate>> candidateFactory)
    {
        var key = new PurchaseCandidateCacheKey(plan.ItemId, plan.QuantityNeeded, plan.HqQuantityNeeded);
        if (_standaloneCandidates.TryGetValue(key, out var candidates))
        {
            return candidates;
        }

        candidates = candidateFactory(plan);
        _standaloneCandidates.Add(key, candidates);
        return candidates;
    }

    private readonly record struct PurchaseCandidateCacheKey(
        int ItemId,
        int QuantityNeeded,
        int HqQuantityNeeded);
}
