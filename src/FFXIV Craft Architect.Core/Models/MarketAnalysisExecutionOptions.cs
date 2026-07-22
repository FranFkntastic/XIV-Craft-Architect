namespace FFXIV_Craft_Architect.Core.Models;

public sealed class MarketAnalysisExecutionOptions
{
    public static MarketAnalysisExecutionOptions Synchronous { get; } = new()
    {
        YieldEveryItems = 0,
        ProgressEveryItems = 0,
        MaxTravelRouteEvaluations = null
    };

    public static MarketAnalysisExecutionOptions Interactive { get; } = new();

    public int YieldEveryItems { get; init; } = 4;

    public int ProgressEveryItems { get; init; } = 1;

    public int? MaxTravelRouteEvaluations { get; init; } = 8;

    public int MaxCandidateWorldSetEvaluations { get; init; } = 250_000;

    public int MaxQualityCoverageTransitions { get; init; } = 250_000;

    public int MaxSplitSeedEvaluations { get; init; } = 250_000;

    public bool ShouldYieldAfterItem(int completedItems)
    {
        return YieldEveryItems > 0 &&
            completedItems > 0 &&
            completedItems % YieldEveryItems == 0;
    }

    public bool ShouldReportProgress(int completedItems)
    {
        return ProgressEveryItems > 0 &&
            completedItems > 0 &&
            completedItems % ProgressEveryItems == 0;
    }
}

internal sealed class MarketRouteCandidateWorkBudget(
    MarketAnalysisExecutionOptions options,
    CancellationToken cancellationToken)
{
    private int _worldSetEvaluations;
    private int _qualityCoverageTransitions;
    private int _splitSeedEvaluations;

    public bool WasTruncated { get; private set; }

    public bool TryConsumeWorldSet()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryConsume(ref _worldSetEvaluations, options.MaxCandidateWorldSetEvaluations);
    }

    public bool TryConsumeQualityTransition()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryConsume(ref _qualityCoverageTransitions, options.MaxQualityCoverageTransitions);
    }

    public bool TryConsumeSplitSeed()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryConsume(ref _splitSeedEvaluations, options.MaxSplitSeedEvaluations);
    }

    public void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();

    private bool TryConsume(ref int consumed, int limit)
    {
        if (limit <= 0 || consumed >= limit)
        {
            WasTruncated = true;
            return false;
        }
        consumed++;
        return true;
    }
}
