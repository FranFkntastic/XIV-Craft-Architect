using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Defines the contract for coordinating shopping optimization operations.
/// Orchestrates calculation of optimal shopping plans from market data.
/// </summary>
public interface IShoppingOptimizationCoordinator
{
    /// <summary>
    /// Calculates optimal shopping plans for a crafting plan.
    /// </summary>
    /// <param name="plan">The crafting plan containing items to purchase.</param>
    /// <param name="mode">The recommendation mode (OptimizationMode) for optimization.</param>
    /// <param name="worldOrDc">The world or data center to search.</param>
    /// <param name="searchAllNa">Whether to search all North American data centers.</param>
    /// <param name="progress">Optional progress reporter for status updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing shopping plans and optimization status.</returns>
    Task<OptimizationResult> OptimizeAsync(
        CraftingPlan plan,
        RecommendationMode mode,
        string worldOrDc,
        bool searchAllNa,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a shopping optimization operation.
/// </summary>
/// <param name="Status">The status of the optimization operation.</param>
/// <param name="Plans">The calculated shopping plans.</param>
/// <param name="TotalCost">Total cost of all recommended purchases.</param>
/// <param name="ItemsWithOptions">Number of items that have purchase options.</param>
/// <param name="ItemsWithoutOptions">Number of items without viable options.</param>
/// <param name="Message">Human-readable status message.</param>
public record OptimizationResult(
    OptimizationStatus Status,
    List<DetailedShoppingPlan> Plans,
    decimal TotalCost,
    int ItemsWithOptions,
    int ItemsWithoutOptions,
    string Message);

/// <summary>
/// Status of a shopping optimization operation.
/// </summary>
public enum OptimizationStatus
{
    /// <summary>All items successfully optimized with purchase options.</summary>
    Success,

    /// <summary>Some items optimized, but others failed or had no options.</summary>
    PartialSuccess,

    /// <summary>Optimization failed entirely.</summary>
    Failed,

    /// <summary>Operation was cancelled.</summary>
    Cancelled,

    /// <summary>No plan was available to optimize.</summary>
    NoPlan
}
