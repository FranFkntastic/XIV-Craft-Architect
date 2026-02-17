using System.Windows;
using FFXIV_Craft_Architect.Services.Interfaces;

namespace FFXIV_Craft_Architect.Helpers;

/// <summary>
/// Attached behavior for navigation tabs that provides tab identification and active state.
/// </summary>
public static class NavigationTabBehavior
{
    /// <summary>
    /// Identifies the TabName attached property.
    /// </summary>
    public static readonly DependencyProperty TabNameProperty =
        DependencyProperty.RegisterAttached(
            "TabName",
            typeof(ApplicationTab?),
            typeof(NavigationTabBehavior),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the IsActive attached property.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive",
            typeof(bool),
            typeof(NavigationTabBehavior),
            new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>
    /// Gets the TabName value.
    /// </summary>
    public static ApplicationTab? GetTabName(DependencyObject obj)
    {
        return (ApplicationTab?)obj.GetValue(TabNameProperty);
    }

    /// <summary>
    /// Sets the TabName value.
    /// </summary>
    public static void SetTabName(DependencyObject obj, ApplicationTab? value)
    {
        obj.SetValue(TabNameProperty, value);
    }

    /// <summary>
    /// Gets the IsActive value.
    /// </summary>
    public static bool GetIsActive(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsActiveProperty);
    }

    /// <summary>
    /// Sets the IsActive value.
    /// </summary>
    public static void SetIsActive(DependencyObject obj, bool value)
    {
        obj.SetValue(IsActiveProperty, value);
    }

    /// <summary>
    /// Called when IsActive changes. Can be used to trigger visual state changes.
    /// </summary>
    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Visual state changes are handled by XAML triggers
        // This method serves as an extension point for custom behavior
    }
}
