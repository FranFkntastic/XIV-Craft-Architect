using System.Windows;
using System.Windows.Input;

namespace FFXIV_Craft_Architect.Helpers;

/// <summary>
/// Attached behavior that adds Command support to any UIElement.
/// </summary>
public static class CommandBehavior
{
    /// <summary>
    /// Attached property for the command to execute when the element is clicked.
    /// </summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(CommandBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    /// <summary>
    /// Attached property for the command parameter.
    /// </summary>
    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.RegisterAttached(
            "CommandParameter",
            typeof(object),
            typeof(CommandBehavior),
            new PropertyMetadata(null));

    /// <summary>
    /// Gets the command associated with the element.
    /// </summary>
    public static ICommand? GetCommand(DependencyObject obj)
    {
        return (ICommand?)obj.GetValue(CommandProperty);
    }

    /// <summary>
    /// Sets the command associated with the element.
    /// </summary>
    public static void SetCommand(DependencyObject obj, ICommand? value)
    {
        obj.SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Gets the command parameter.
    /// </summary>
    public static object? GetCommandParameter(DependencyObject obj)
    {
        return obj.GetValue(CommandParameterProperty);
    }

    /// <summary>
    /// Sets the command parameter.
    /// </summary>
    public static void SetCommandParameter(DependencyObject obj, object? value)
    {
        obj.SetValue(CommandParameterProperty, value);
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            // Remove old handler
            element.MouseLeftButtonDown -= OnMouseLeftButtonDown;

            // Add new handler if command is set
            if (e.NewValue is ICommand)
            {
                element.MouseLeftButtonDown += OnMouseLeftButtonDown;
            }
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DependencyObject obj)
        {
            var command = GetCommand(obj);
            var parameter = GetCommandParameter(obj);

            if (command?.CanExecute(parameter) == true)
            {
                command.Execute(parameter);
                e.Handled = true;
            }
        }
    }
}
