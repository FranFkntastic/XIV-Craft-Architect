using System.Windows;
using System.Windows.Media;

namespace FFXIVCraftArchitect.Helpers;

/// <summary>
/// Extension methods for VisualTreeHelper operations.
/// </summary>
public static class VisualTreeHelperExtensions
{
    /// <summary>
    /// Finds the first parent of the specified type in the visual tree.
    /// </summary>
    public static T? FindParent<T>(this DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(child);
        
        while (parent != null && parent is not T)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        
        return parent as T;
    }
}
