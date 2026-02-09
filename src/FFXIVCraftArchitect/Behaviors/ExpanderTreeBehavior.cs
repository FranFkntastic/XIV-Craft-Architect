using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIVCraftArchitect.Behaviors;

/// <summary>
/// Attached behavior that provides expand/collapse commands for a tree of Expanders.
/// Attach to a Panel containing Expander elements.
/// </summary>
public static class ExpanderTreeBehavior
{
    /// <summary>
    /// Attached property for the ExpandAll command.
    /// </summary>
    public static readonly DependencyProperty ExpandAllCommandProperty =
        DependencyProperty.RegisterAttached(
            "ExpandAllCommand",
            typeof(ICommand),
            typeof(ExpanderTreeBehavior),
            new PropertyMetadata(null, OnExpandAllCommandChanged));

    /// <summary>
    /// Attached property for the CollapseAll command.
    /// </summary>
    public static readonly DependencyProperty CollapseAllCommandProperty =
        DependencyProperty.RegisterAttached(
            "CollapseAllCommand",
            typeof(ICommand),
            typeof(ExpanderTreeBehavior),
            new PropertyMetadata(null, OnCollapseAllCommandChanged));

    /// <summary>
    /// Gets the ExpandAllCommand attached property value.
    /// </summary>
    public static ICommand? GetExpandAllCommand(DependencyObject obj)
    {
        return (ICommand?)obj.GetValue(ExpandAllCommandProperty);
    }

    /// <summary>
    /// Sets the ExpandAllCommand attached property value.
    /// </summary>
    public static void SetExpandAllCommand(DependencyObject obj, ICommand? value)
    {
        obj.SetValue(ExpandAllCommandProperty, value);
    }

    /// <summary>
    /// Gets the CollapseAllCommand attached property value.
    /// </summary>
    public static ICommand? GetCollapseAllCommand(DependencyObject obj)
    {
        return (ICommand?)obj.GetValue(CollapseAllCommandProperty);
    }

    /// <summary>
    /// Sets the CollapseAllCommand attached property value.
    /// </summary>
    public static void SetCollapseAllCommand(DependencyObject obj, ICommand? value)
    {
        obj.SetValue(CollapseAllCommandProperty, value);
    }

    private static void OnExpandAllCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel) return;

        if (e.OldValue is ICommand oldCommand)
        {
            oldCommand.CanExecuteChanged -= (s, args) => OnCanExecuteChanged(panel, oldCommand, true);
        }

        if (e.NewValue is ICommand newCommand)
        {
            // Note: CanExecuteChanged is handled by WPF CommandManager automatically for button binding
        }
    }

    private static void OnCollapseAllCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel) return;

        if (e.OldValue is ICommand oldCommand)
        {
            oldCommand.CanExecuteChanged -= (s, args) => OnCanExecuteChanged(panel, oldCommand, false);
        }
    }

    private static void OnCanExecuteChanged(Panel panel, ICommand command, bool isExpanded)
    {
        // Handled by WPF CommandManager - no additional action needed
    }

    /// <summary>
    /// Sets the expanded state of all Expanders within the specified container.
    /// </summary>
    /// <param name="container">The container element (typically a Panel containing Expanders).</param>
    /// <param name="isExpanded">True to expand all, false to collapse all.</param>
    public static void SetAllExpandersState(DependencyObject container, bool isExpanded)
    {
        if (container is Panel panel)
        {
            SetAllExpandersInPanel(panel, isExpanded);
        }
    }

    private static void SetAllExpandersInPanel(Panel panel, bool isExpanded)
    {
        foreach (var child in panel.Children)
        {
            if (child is Expander expander)
            {
                expander.IsExpanded = isExpanded;
                SetExpanderChildrenState(expander, isExpanded);
            }
        }
    }

    private static void SetExpanderChildrenState(Expander parent, bool isExpanded)
    {
        if (parent.Content is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Expander childExpander)
                {
                    childExpander.IsExpanded = isExpanded;
                    SetExpanderChildrenState(childExpander, isExpanded);
                }
            }
        }
    }
}
