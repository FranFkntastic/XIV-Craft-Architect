using MudBlazor;

namespace FFXIVCraftArchitect.Web;

/// <summary>
/// Application theme configuration for FFXIV Craft Architect.
/// Provides a custom dark theme with gold accent colors matching the WPF desktop application.
/// </summary>
public static class AppTheme
{
    /// <summary>
    /// Creates the application MudBlazor theme with FFXIV-inspired styling.
    /// </summary>
    public static MudTheme CreateTheme()
    {
        return new MudTheme
        {
            PaletteDark = new PaletteDark
            {
                Primary = "#d4a73a",      // Gold accent (like WPF)
                Secondary = "#9c27b0",    // Purple
                Success = "#4caf50",      // Green
                Error = "#f44336",        // Red
                Warning = "#ff9800",      // Orange
                Info = "#2196f3",         // Blue
                AppbarBackground = "#1a1a1a",
                Background = "#1e1e1e",
                Surface = "#2d2d2d",
                TextPrimary = "#ffffff",
                TextSecondary = "#cccccc",
                DrawerBackground = "#1e1e1e",
                DrawerText = "#cccccc",
                ActionDefault = "#cccccc",
                ActionDisabled = "#666666",
                Divider = "#444444",
                TableLines = "#444444",
                LinesDefault = "#444444",
            },
            Typography = new Typography
            {
                H6 = new H6 { FontSize = "1.25rem", FontWeight = 500 },
                Subtitle1 = new Subtitle1 { FontSize = "1rem", FontWeight = 400 },
                Body1 = new Body1 { FontSize = "1rem", FontWeight = 400 },
                Body2 = new Body2 { FontSize = "0.875rem", FontWeight = 400 },
                Caption = new Caption { FontSize = "0.75rem", FontWeight = 400 },
                Overline = new Overline { FontSize = "0.625rem", FontWeight = 400 }
            },
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "4px",
                AppbarHeight = "64px"
            }
        };
    }
}
