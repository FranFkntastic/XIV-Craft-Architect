using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Reuses immutable purchase candidates while the joint acquisition solver compares
/// many plans derived from the same market evidence snapshot.
/// </summary>
internal sealed class ProcurementRouteOptimizationSession
{
    private readonly Dictionary<PurchaseCandidateCacheKey, IReadOnlyList<MarketPurchaseCandidate>> _standaloneCandidates = [];
    private readonly Dictionary<AdjustedPlanCacheKey, DetailedShoppingPlan> _adjustedPlans = [];

    public ProcurementRouteOptimizationSession(
        MarketAnalysisExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        CandidateWorkBudget = new MarketRouteCandidateWorkBudget(executionOptions, cancellationToken);
    }

    public MarketRouteCandidateWorkBudget CandidateWorkBudget { get; }

    public IReadOnlyList<MarketPurchaseCandidate> GetOrAddCandidates(
        DetailedShoppingPlan plan,
        Func<DetailedShoppingPlan, List<MarketPurchaseCandidate>> candidateFactory)
    {
        var key = new PurchaseCandidateCacheKey(
            plan.ItemId,
            plan.QuantityNeeded,
            plan.HqQuantityNeeded,
            string.Empty);
        if (_standaloneCandidates.TryGetValue(key, out var candidates))
        {
            return candidates;
        }

        candidates = candidateFactory(plan);
        _standaloneCandidates.Add(key, candidates);
        return candidates;
    }

    public IReadOnlyList<MarketPurchaseCandidate> GetOrAddRouteCandidates(
        DetailedShoppingPlan plan,
        MarketRouteState route,
        Func<DetailedShoppingPlan, List<MarketPurchaseCandidate>> candidateFactory)
    {
        var key = new PurchaseCandidateCacheKey(
            plan.ItemId,
            plan.QuantityNeeded,
            plan.HqQuantityNeeded,
            route.CanonicalKey);
        if (_standaloneCandidates.TryGetValue(key, out var candidates))
        {
            return candidates;
        }

        candidates = candidateFactory(plan);
        _standaloneCandidates.Add(key, candidates);
        return candidates;
    }

    /// <summary>
    /// Reuses demand-adjusted shopping plans across variants. Many acquisition variants
    /// share the same per-item demand, so adjusting evidence per (item, demand) once
    /// avoids recloning every world's listings for every variant.
    /// </summary>
    public DetailedShoppingPlan GetOrAddAdjustedPlan(
        DetailedShoppingPlan evidence,
        int quantityNeeded,
        int hqQuantityNeeded,
        Func<DetailedShoppingPlan> planFactory)
    {
        var key = new AdjustedPlanCacheKey(evidence.ItemId, quantityNeeded, hqQuantityNeeded);
        if (_adjustedPlans.TryGetValue(key, out var plan))
        {
            return plan;
        }

        plan = planFactory();
        _adjustedPlans.Add(key, plan);
        return plan;
    }

    private readonly record struct PurchaseCandidateCacheKey(
        int ItemId,
        int QuantityNeeded,
        int HqQuantityNeeded,
        string RouteKey);

    private readonly record struct AdjustedPlanCacheKey(
        int ItemId,
        int QuantityNeeded,
        int HqQuantityNeeded);
}
