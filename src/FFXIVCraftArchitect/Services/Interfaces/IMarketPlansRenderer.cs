using System.Windows;
using System.Windows.Controls;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Models;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// View mode for displaying market plans.
/// </summary>
public enum MarketViewMode
{
    /// <summary>
    /// Legacy procurement panel view with summary border.
    /// </summary>
    Legacy,

    /// <summary>
    /// Split-pane view with collapsed cards and expanded panel.
    /// </summary>
    SplitPane
}

/// <summary>
/// Request parameters for displaying market plans.
/// </summary>
public class MarketDisplayRequest
{
    /// <summary>
    /// The view mode to use for display.
    /// </summary>
    public MarketViewMode ViewMode { get; set; }

    /// <summary>
    /// The market plans to display.
    /// </summary>
    public IEnumerable<DetailedShoppingPlan> Plans { get; set; } = null!;

    /// <summary>
    /// The sort index (0 = by world, 1 = by name, 2 = by cost).
    /// </summary>
    public int SortIndex { get; set; }

    /// <summary>
    /// The target panel to add cards to.
    /// </summary>
    public Panel TargetPanel { get; set; } = null!;

    /// <summary>
    /// Optional text block for displaying total cost (SplitPane mode only).
    /// </summary>
    public TextBlock? TotalCostTextBlock { get; set; }

    /// <summary>
    /// Optional ID of the item to expand (SplitPane mode only).
    /// </summary>
    public int? ExpandedItemId { get; set; }

    /// <summary>
    /// Optional click handler for split-pane cards. Receives the plan when a card is clicked.
    /// </summary>
    public Action<DetailedShoppingPlan>? OnCardClick { get; set; }

    /// <summary>
    /// Optional resource lookup function for finding DataTemplates. Defaults to Application.Current.FindResource.
    /// </summary>
    public Func<string, object>? FindResource { get; set; }
}

/// <summary>
/// Result from displaying market plans, containing created UI elements.
/// </summary>
public class MarketDisplayResult
{
    /// <summary>
    /// The cards created for display, mapped to their corresponding plans.
    /// </summary>
    public List<(Border Card, DetailedShoppingPlan Plan)> Cards { get; } = new();

    /// <summary>
    /// The grand total cost of all plans.
    /// </summary>
    public long GrandTotal { get; set; }

    /// <summary>
    /// The number of items with options.
    /// </summary>
    public int ItemsWithOptions { get; set; }
}

/// <summary>
/// Service for displaying market plans in different view modes.
/// Consolidates common logic between legacy and split-pane views.
/// </summary>
public interface IMarketPlansRenderer
{
    /// <summary>
    /// Displays market plans according to the specified request parameters.
    /// </summary>
    /// <param name="request">The display request containing plans, view mode, and target containers.</param>
    /// <returns>Result containing the created cards and statistics.</returns>
    MarketDisplayResult DisplayMarketPlans(MarketDisplayRequest request);

    /// <summary>
    /// Builds the expanded panel content for a selected shopping plan.
    /// </summary>
    /// <param name="target">The target panel to populate with expanded content.</param>
    /// <param name="plan">The shopping plan to display in expanded form.</param>
    /// <param name="onClose">Action to invoke when the panel requests to close.</param>
    void BuildExpandedPanel(Panel target, DetailedShoppingPlan plan, Action onClose);
}
