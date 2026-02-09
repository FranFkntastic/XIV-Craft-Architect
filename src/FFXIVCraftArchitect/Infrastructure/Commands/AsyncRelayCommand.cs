using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure.Commands;

/// <summary>
/// An asynchronous relay command that implements <see cref="ICommand"/>.
/// Executes an async delegate and optionally checks a condition before execution.
/// Prevents reentrancy while the command is executing.
/// </summary>
public class AsyncRelayCommand : IRelayCommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    /// <summary>
    /// Occurs when changes occur that affect whether the command should execute.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>
    /// Creates a new async relay command.
    /// </summary>
    /// <param name="execute">The async action to execute when the command is invoked.</param>
    /// <param name="canExecute">Optional. A function that determines whether the command can execute. If null, the command can always execute when not already executing.</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Gets a value indicating whether the command is currently executing.
    /// </summary>
    public bool IsExecuting => _isExecuting;

    /// <summary>
    /// Gets the execution task if the command is currently executing, otherwise null.
    /// This can be used to await command completion.
    /// </summary>
    public Task? ExecutionTask { get; private set; }

    /// <summary>
    /// Determines whether this command can execute in its current state.
    /// Returns false if the command is already executing (prevents reentrancy).
    /// </summary>
    /// <param name="parameter">This parameter is ignored.</param>
    /// <returns>true if this command can be executed; otherwise, false.</returns>
    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
            return false;

        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>
    /// Executes the command asynchronously.
    /// </summary>
    /// <param name="parameter">This parameter is ignored.</param>
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        await ExecuteAsync();
    }

    /// <summary>
    /// Executes the command asynchronously and returns the execution task.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync()
    {
        if (_isExecuting)
            return;

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            ExecutionTask = _execute();
            await ExecutionTask;
        }
        catch (Exception)
        {
            // Exceptions are not caught here; they bubble up to the caller
            // Consider adding logging or error handling if needed
            throw;
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to trigger reevaluation of CanExecute.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
