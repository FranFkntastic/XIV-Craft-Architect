using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FFXIVCraftArchitect.Core.Infrastructure;

/// <summary>
/// Base class for all ViewModels in the application.
/// Provides INotifyPropertyChanged implementation, IDisposable pattern for cleanup,
/// and helper methods for safe async operations.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event for the specified property.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed.
    /// Automatically determined from the calling member if not specified.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Sets a property value and raises <see cref="PropertyChanged"/> if the value has changed.
    /// Uses <see cref="EqualityComparer{T}.Default"/> for comparison.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">Reference to the backing field.</param>
    /// <param name="value">The new value to set.</param>
    /// <param name="propertyName">Name of the property. Automatically determined if not specified.</param>
    /// <returns>
    /// <c>true</c> if the property value changed and <see cref="PropertyChanged"/> was raised;
    /// <c>false</c> if the new value equals the existing value.
    /// </returns>
    /// <example>
    /// <code>
    /// private string _name = string.Empty;
    ///
    /// public string Name
    /// {
    ///     get => _name;
    ///     set => SetProperty(ref _name, value);
    /// }
    /// </code>
    /// </example>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Sets a property value, raises <see cref="PropertyChanged"/>, and executes a callback if the value changed.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">Reference to the backing field.</param>
    /// <param name="value">The new value to set.</param>
    /// <param name="onChanged">Callback to invoke when the value changes. Called after PropertyChanged is raised.</param>
    /// <param name="propertyName">Name of the property. Automatically determined if not specified.</param>
    /// <returns>
    /// <c>true</c> if the property value changed; <c>false</c> otherwise.
    /// </returns>
    /// <example>
    /// <code>
    /// private int _count;
    ///
    /// public int Count
    /// {
    ///     get => _count;
    ///     set => SetProperty(ref _count, value, () => RecalculateTotal());
    /// }
    /// </code>
    /// </example>
    protected bool SetProperty<T>(ref T field, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        onChanged();
        return true;
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
