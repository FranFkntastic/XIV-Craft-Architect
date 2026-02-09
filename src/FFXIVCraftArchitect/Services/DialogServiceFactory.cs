using System.Windows;
using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Factory for creating DialogService instances bound to specific windows.
/// Each window should call CreateForWindow(this) to get its own dialog service.
/// </summary>
public class DialogServiceFactory
{
    /// <summary>
    /// Creates a new IDialogService bound to the specified owner window.
    /// </summary>
    /// <param name="owner">The window that will own any dialogs shown by this service.</param>
    /// <returns>A new IDialogService instance.</returns>
    public IDialogService CreateForWindow(Window owner)
    {
        if (owner == null)
            throw new ArgumentNullException(nameof(owner));

        return new DialogService(owner);
    }
}
