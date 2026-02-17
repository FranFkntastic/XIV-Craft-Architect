using System.Windows;
using System.Windows.Threading;
using FFXIV_Craft_Architect.Services.Interfaces;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Implementation of IDialogService using WPF MessageBox.
/// Ensures dialogs are shown on the UI thread via Dispatcher.Invoke.
/// </summary>
public class DialogService : IDialogService
{
    private Window? _owner;

    /// <summary>
    /// Initializes a new instance of the DialogService.
    /// </summary>
    /// <param name="owner">The parent window for modal dialogs. Can be null initially if set later via SetOwner().</param>
    public DialogService(Window? owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Sets or updates the owner window.
    /// </summary>
    internal void SetOwner(Window owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string message, string title = "Confirm")
    {
        return InvokeOnUiThread(() =>
        {
            var result = MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        });
    }

    /// <inheritdoc />
    public Task ShowInfoAsync(string message, string title = "Information")
    {
        return InvokeOnUiThread(() =>
        {
            MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string message, string title = "Error")
    {
        return InvokeOnUiThread(() =>
        {
            MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string message, Exception exception, string title = "Error")
    {
        var fullMessage = $"{message}\n\n{exception.Message}";
        return ShowErrorAsync(fullMessage, title);
    }

    /// <inheritdoc />
    public Task<string?> PromptAsync(string message, string title = "Input", string defaultValue = "")
    {
        // For now, use a simple input dialog via MessageBox is not possible,
        // so we use a simple dialog approach. This can be enhanced later with a custom window.
        // This implementation throws NotImplementedException as PromptAsync requires a custom input dialog.
        throw new NotImplementedException(
            "PromptAsync requires a custom input dialog. " +
            "Use Microsoft.VisualBasic.Interaction.InputBox or implement a custom window.");
    }

    /// <inheritdoc />
    public Task<DialogResult> YesNoCancelAsync(string message, string title = "Confirm")
    {
        return InvokeOnUiThread(() =>
        {
            var result = MessageBox.Show(_owner, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return result switch
            {
                MessageBoxResult.Yes => DialogResult.Yes,
                MessageBoxResult.No => DialogResult.No,
                _ => DialogResult.Cancel
            };
        });
    }

    /// <summary>
    /// Gets the dispatcher to use for UI thread operations.
    /// Uses the owner window if available, otherwise falls back to Application.Current.Dispatcher.
    /// </summary>
    private Dispatcher GetDispatcher()
    {
        return _owner?.Dispatcher ?? Application.Current.Dispatcher;
    }

    /// <summary>
    /// Invokes an action on the UI thread and returns the result.
    /// If already on UI thread, executes synchronously.
    /// </summary>
    private Task<T> InvokeOnUiThread<T>(Func<T> action)
    {
        var dispatcher = GetDispatcher();
        
        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    /// <summary>
    /// Invokes an action on the UI thread.
    /// If already on UI thread, executes synchronously.
    /// </summary>
    private Task InvokeOnUiThread(Action action)
    {
        var dispatcher = GetDispatcher();
        
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }
}
