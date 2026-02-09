using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure.Commands;

/// <summary>
/// A synchronous relay command that implements <see cref="ICommand"/>.
/// Executes a delegate and optionally checks a condition before execution.
/// </summary>
public class RelayCommand : IRelayCommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// Occurs when changes occur that affect whether the command should execute.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>
    /// Creates a new relay command.
    /// </summary>
    /// <param name="execute">The action to execute when the command is invoked.</param>
    /// <param name="canExecute">Optional. A function that determines whether the command can execute. If null, the command can always execute.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Determines whether this command can execute in its current state.
    /// </summary>
    /// <param name="parameter">This parameter is ignored.</param>
    /// <returns>true if this command can be executed; otherwise, false.</returns>
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="parameter">This parameter is ignored.</param>
    public void Execute(object? parameter)
    {
        _execute();
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to trigger reevaluation of CanExecute.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
