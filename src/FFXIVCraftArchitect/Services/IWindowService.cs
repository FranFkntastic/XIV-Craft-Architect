using System.Windows;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service that provides access to the current main window for dialog ownership.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Gets the current main window, or null if not set.
    /// </summary>
    Window? CurrentWindow { get; }

    /// <summary>
    /// Sets the current main window reference.
    /// </summary>
    /// <param name="window">The main window to store as a weak reference.</param>
    void SetCurrentWindow(Window window);
}
