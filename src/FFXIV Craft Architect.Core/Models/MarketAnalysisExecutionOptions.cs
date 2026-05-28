namespace FFXIV_Craft_Architect.Core.Models;

public sealed class MarketAnalysisExecutionOptions
{
    public static MarketAnalysisExecutionOptions Synchronous { get; } = new()
    {
        YieldEveryItems = 0,
        ProgressEveryItems = 0
    };

    public static MarketAnalysisExecutionOptions Interactive { get; } = new();

    public int YieldEveryItems { get; init; } = 4;

    public int ProgressEveryItems { get; init; } = 1;

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
