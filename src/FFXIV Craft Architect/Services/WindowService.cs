using System.Windows;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Implementation of <see cref="IWindowService"/> that stores a weak reference to the main window.
/// </summary>
public class WindowService : IWindowService
{
    private WeakReference<Window>? _windowReference;

    /// <summary>
    /// Gets the current main window, or null if not set or the window has been garbage collected.
    /// </summary>
    public Window? CurrentWindow
    {
        get
        {
            if (_windowReference?.TryGetTarget(out var window) == true)
            {
                return window;
            }
            return null;
        }
    }

    /// <summary>
    /// Sets the current main window reference using a weak reference.
    /// </summary>
    /// <param name="window">The main window to store.</param>
    public void SetCurrentWindow(Window window)
    {
        _windowReference = new WeakReference<Window>(window);
    }
}
