using CommunityToolkit.Mvvm.ComponentModel;
using FFXIVCraftArchitect.Helpers;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// Base class for all ViewModels in the application.
/// Provides INotifyPropertyChanged implementation via CommunityToolkit.Mvvm,
/// IDisposable pattern for cleanup, and helper methods for safe async operations.
/// </summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    // Note: Property change notification is handled by ObservableObject base class
    // Use SetProperty(ref _field, value) from ObservableObject for properties

    /// <summary>
    /// Safely executes a task in a fire-and-forget manner, catching any exceptions
    /// to prevent application crashes. Optionally invokes a callback on exception.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="onError">Optional callback to handle exceptions.</param>
    /// <remarks>
    /// <para>
    /// This method is a wrapper around <see cref="AsyncExtensions.SafeFireAndForget(Task, Action{Exception})"/>
    /// that provides a convenient way to execute async operations from synchronous contexts
    /// without using <c>async void</c>.
    /// </para>
    /// <para>
    /// Usage example:
    /// <code>
    /// private void OnButtonClick()
    /// {
    ///     SafeFireAndForget(LoadDataAsync(), ex => Logger?.LogError(ex, "Failed to load data"));
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    protected void SafeFireAndForget(Task task, Action<Exception>? onError = null)
    {
        task.SafeFireAndForget(onError);
    }

    /// <summary>
    /// Releases all resources used by the ViewModel.
    /// </summary>
    /// <remarks>
    /// Call this method when the ViewModel is no longer needed to ensure proper cleanup
    /// of event subscriptions and other resources. This class implements the standard
    /// dispose pattern to allow derived classes to override cleanup behavior.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        Dispose(true);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the ViewModel and optionally releases managed resources.
    /// </summary>
    /// <param name="disposing">
    /// <c>true</c> to release both managed and unmanaged resources;
    /// <c>false</c> to release only unmanaged resources.
    /// </param>
    /// <remarks>
    /// <para>
    /// Override this method in derived classes to perform custom cleanup:
    /// - Unsubscribe from events
    /// - Dispose of managed resources (Timers, CancellationTokenSources, etc.)
    /// - Clear collections that might hold references
    /// </para>
    /// <para>
    /// Example override:
    /// <code>
    /// protected override void Dispose(bool disposing)
    /// {
    ///     if (disposing)
    ///     {
    ///         _someService.DataChanged -= OnDataChanged;
    ///         _cancellationTokenSource?.Cancel();
    ///         _cancellationTokenSource?.Dispose();
    ///     }
    ///     base.Dispose(disposing);
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Override in derived classes to unsubscribe events and dispose resources
        }
    }

    /// <summary>
    /// Finalizer to ensure resources are cleaned up if Dispose() was not called.
    /// </summary>
    ~ViewModelBase()
    {
        Dispose(false);
    }
}
