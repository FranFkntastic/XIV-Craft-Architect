using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure.Commands;

/// <summary>
/// A synchronous relay command that implements <see cref="ICommand"/> with a parameter.
/// Executes a delegate with a parameter and optionally checks a condition before execution.
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>
    /// Occurs when changes occur that affect whether the command should execute.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>
    /// Creates a new relay command with a parameter.
    /// </summary>
    /// <param name="execute">The action to execute when the command is invoked.</param>
    /// <param name="canExecute">Optional. A function that determines whether the command can execute. If null, the command can always execute.</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Determines whether this command can execute in its current state.
    /// </summary>
    /// <param name="parameter">The parameter to pass to the canExecute delegate.</param>
    /// <returns>true if this command can be executed; otherwise, false.</returns>
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke(parameter is T t ? t : default) ?? true;
    }

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="parameter">The parameter to pass to the execute delegate.</param>
    public void Execute(object? parameter)
    {
        _execute(parameter is T t ? t : default);
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to trigger reevaluation of CanExecute.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
