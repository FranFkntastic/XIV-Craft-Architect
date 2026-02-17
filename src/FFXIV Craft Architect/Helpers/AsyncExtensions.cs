using System.Windows;
using System.Windows.Threading;

namespace FFXIV_Craft_Architect.Helpers;

/// <summary>
/// Extension methods for async Task operations.
/// </summary>
public static class AsyncExtensions
{
    /// <summary>
    /// Safely executes a task in a fire-and-forget manner, catching any exceptions
    /// to prevent application crashes. Optionally invokes a callback on exception.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="onException">Optional callback to handle exceptions. If provided, 
    /// the callback is invoked on the UI thread with the caught exception.</param>
    /// <remarks>
    /// <para>
    /// This method is designed to replace the dangerous <c>async void</c> pattern
    /// which can crash the application if an unhandled exception occurs.
    /// </para>
    /// <para>
    /// Usage example:
    /// <code>
    /// // BEFORE (async void - crashes on exception):
    /// private async void OnButtonClick(object sender, EventArgs e)
    /// {
    ///     await DoWorkAsync();  // Exception here crashes app
    /// }
    /// 
    /// // AFTER (safe fire-and-forget):
    /// private void OnButtonClick(object sender, EventArgs e)
    /// {
    ///     DoWorkAsync().SafeFireAndForget(ex => 
    ///         _logger.LogError(ex, "Work failed"));
    /// }
    /// 
    /// private async Task DoWorkAsync() { ... }
    /// </code>
    /// </para>
    /// </remarks>
    public static void SafeFireAndForget(this Task task, Action<Exception>? onException = null)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var exception = t.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? t.Exception;

                if (onException != null)
                {
                    // Marshal to UI thread if needed for the callback
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && dispatcher.CheckAccess() == false)
                    {
                        dispatcher.Invoke(() => onException(exception));
                    }
                    else
                    {
                        onException(exception);
                    }
                }
                // If no callback provided, exception is silently handled (fire-and-forget behavior)
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Safely executes a task with a result in a fire-and-forget manner, catching any exceptions
    /// to prevent application crashes. Optionally invokes a callback on exception.
    /// </summary>
    /// <typeparam name="T">The type of the task result.</typeparam>
    /// <param name="task">The task to execute.</param>
    /// <param name="onException">Optional callback to handle exceptions. If provided, 
    /// the callback is invoked on the UI thread with the caught exception.</param>
    /// <remarks>
    /// <para>
    /// This overload ignores the task result. Use when you need fire-and-forget behavior
    /// for tasks that return values but you don't need the result.
    /// </para>
    /// </remarks>
    public static void SafeFireAndForget<T>(this Task<T> task, Action<Exception>? onException = null)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var exception = t.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? t.Exception;

                if (onException != null)
                {
                    // Marshal to UI thread if needed for the callback
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && dispatcher.CheckAccess() == false)
                    {
                        dispatcher.Invoke(() => onException(exception));
                    }
                    else
                    {
                        onException(exception);
                    }
                }
                // If no callback provided, exception is silently handled (fire-and-forget behavior)
            }
        }, TaskScheduler.Default);
    }
}
