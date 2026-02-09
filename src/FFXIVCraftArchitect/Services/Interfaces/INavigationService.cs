using System.ComponentModel;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Defines the available application tabs.
/// </summary>
public enum ApplicationTab
{
    RecipePlanner,
    MarketAnalysis,
    ProcurementPlanner
}

/// <summary>
/// Event arguments for navigation events.
/// </summary>
public class NavigationEventArgs : EventArgs
{
    public ApplicationTab PreviousTab { get; }
    public ApplicationTab NewTab { get; }

    public NavigationEventArgs(ApplicationTab previousTab, ApplicationTab newTab)
    {
        PreviousTab = previousTab;
        NewTab = newTab;
    }
}

/// <summary>
/// Service for managing application navigation state.
/// Provides centralized tab management with change notification.
/// </summary>
public interface INavigationService : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the currently active tab.
    /// </summary>
    ApplicationTab CurrentTab { get; }

    /// <summary>
    /// Gets a value indicating whether the market view is currently visible
    /// (MarketAnalysis or ProcurementPlanner tabs).
    /// </summary>
    bool IsMarketViewVisible { get; }

    /// <summary>
    /// Navigates to the specified tab.
    /// </summary>
    /// <param name="tab">The tab to navigate to.</param>
    void NavigateTo(ApplicationTab tab);

    /// <summary>
    /// Determines whether the specified tab is currently active.
    /// </summary>
    /// <param name="tab">The tab to check.</param>
    /// <returns>True if the tab is active; otherwise, false.</returns>
    bool IsActive(ApplicationTab tab);

    /// <summary>
    /// Raised when navigation to a new tab completes.
    /// </summary>
    event EventHandler<NavigationEventArgs>? Navigated;
}
