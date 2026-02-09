using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure.Commands;

/// <summary>
/// A simple delegate-based implementation of <see cref="ICommand"/>.
/// Used for behaviors and scenarios where command instances are passed as parameters.
/// </summary>
public class DelegateCommand : ICommand
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
    /// Creates a new delegate command.
    /// </summary>
    /// <param name="execute">The action to execute when the command is invoked.</param>
    /// <param name="canExecute">Optional. A function that determines whether the command can execute.</param>
    public DelegateCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        _execute();
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to trigger reevaluation.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
