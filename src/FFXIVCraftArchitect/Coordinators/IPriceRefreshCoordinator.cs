using FFXIVCraftArchitect.Core.Models;
using PriceInfo = FFXIVCraftArchitect.Core.Models.PriceInfo;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Defines the contract for coordinating price refresh operations.
/// Orchestrates fetching prices and updating plan nodes without direct UI dependencies.
/// </summary>
public interface IPriceRefreshCoordinator
{
    /// <summary>
    /// Refreshes prices for all items in a crafting plan.
    /// </summary>
    /// <param name="plan">The plan to refresh prices for (mutated in-place).</param>
    /// <param name="worldOrDc">The world or data center to fetch prices from.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing status and fetched prices dictionary.</returns>
    Task<PriceRefreshResult> RefreshPlanAsync(
        CraftingPlan plan,
        string worldOrDc,
        IProgress<PriceRefreshProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes price for a single item.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="itemName">The item name.</param>
    /// <param name="worldOrDc">The world or data center to fetch prices from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the price info.</returns>
    Task<PriceRefreshResult> RefreshItemAsync(
        int itemId,
        string itemName,
        string worldOrDc,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a price refresh operation.
/// </summary>
public record PriceRefreshResult(
    PriceRefreshStatus Status,
    Dictionary<int, PriceInfo> Prices,
    string Message,
    int SuccessCount = 0,
    int FailedCount = 0,
    int CachedCount = 0);

/// <summary>
/// Status of a price refresh operation.
/// </summary>
public enum PriceRefreshStatus
{
    Success,
    PartialSuccess,
    Failed,
    Cancelled,
    NoPlan
}

/// <summary>
/// Progress information during price refresh operations.
/// </summary>
public record PriceRefreshProgress(
    int Current,
    int Total,
    string ItemName,
    PriceRefreshStage Stage,
    string? Message = null);

/// <summary>
/// Stages of the price refresh process.
/// </summary>
public enum PriceRefreshStage
{
    Starting,
    Fetching,
    Updating,
    Complete
}
