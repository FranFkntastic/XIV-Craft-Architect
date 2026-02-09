using System.Windows.Input;

namespace FFXIVCraftArchitect.Infrastructure.Commands;

/// <summary>
/// Interface for relay commands that provides a method to raise CanExecuteChanged.
/// Extends <see cref="ICommand"/> with the ability to manually trigger CanExecute reevaluation.
/// </summary>
public interface IRelayCommand : ICommand
{
    /// <summary>
    /// Raises the <see cref="ICommand.CanExecuteChanged"/> event to trigger reevaluation of CanExecute.
    /// </summary>
    void RaiseCanExecuteChanged();
}
