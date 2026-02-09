namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Represents the user's choice from a dialog with Yes/No/Cancel options.
/// </summary>
public enum DialogResult
{
    /// <summary>The user clicked Yes.</summary>
    Yes,
    /// <summary>The user clicked No.</summary>
    No,
    /// <summary>The user clicked Cancel or closed the dialog.</summary>
    Cancel
}

/// <summary>
/// Service for displaying dialogs and message boxes to the user.
/// Abstracts MessageBox interactions to enable testing and consistent behavior.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog with Yes/No buttons.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title. Defaults to "Confirm".</param>
    /// <returns>True if the user clicked Yes, false otherwise.</returns>
    Task<bool> ConfirmAsync(string message, string title = "Confirm");

    /// <summary>
    /// Shows an informational message dialog with OK button.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title. Defaults to "Information".</param>
    Task ShowInfoAsync(string message, string title = "Information");

    /// <summary>
    /// Shows an error message dialog with OK button.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    /// <param name="title">The dialog title. Defaults to "Error".</param>
    Task ShowErrorAsync(string message, string title = "Error");

    /// <summary>
    /// Shows an error message dialog with exception details.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    /// <param name="exception">The exception to include in the message.</param>
    /// <param name="title">The dialog title. Defaults to "Error".</param>
    Task ShowErrorAsync(string message, Exception exception, string title = "Error");

    /// <summary>
    /// Shows a prompt dialog for user text input.
    /// </summary>
    /// <param name="message">The prompt message to display.</param>
    /// <param name="title">The dialog title. Defaults to "Input".</param>
    /// <param name="defaultValue">The default value for the input field.</param>
    /// <returns>The user's input, or null if cancelled.</returns>
    Task<string?> PromptAsync(string message, string title = "Input", string defaultValue = "");

    /// <summary>
    /// Shows a dialog with Yes/No/Cancel buttons.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title. Defaults to "Confirm".</param>
    /// <returns>The user's choice: Yes, No, or Cancel.</returns>
    Task<DialogResult> YesNoCancelAsync(string message, string title = "Confirm");
}
