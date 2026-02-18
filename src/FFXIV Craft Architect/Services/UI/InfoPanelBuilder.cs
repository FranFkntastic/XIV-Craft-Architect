using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIV_Craft_Architect.Services.UI;

/// <summary>
/// Builds consistent info/placeholder panels for empty states and notifications.
/// Centralizes styling for centered message panels across the application.
/// </summary>
public class InfoPanelBuilder
{
    /// <summary>
    /// Creates a centered info panel with title, body, and optional hint text.
    /// </summary>
    /// <param name="title">The main title text (required)</param>
    /// <param name="body">The body/description text (optional)</param>
    /// <param name="hint">The hint/action text at the bottom (optional)</param>
    /// <param name="titleStyle">Style for the title (default: Accent)</param>
    /// <param name="centerVertically">Whether to center with top margin (default: true)</param>
    /// <returns>A configured StackPanel ready to be added to a parent</returns>
    public StackPanel CreateInfoPanel(
        string title,
        string? body = null,
        string? hint = null,
        InfoPanelTitleStyle titleStyle = InfoPanelTitleStyle.Accent,
        bool centerVertically = true)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (centerVertically)
        {
            panel.Margin = new Thickness(0, 40, 0, 0);
        }

        // Title
        var titleBlock = CreateTitleBlock(title, titleStyle);
        panel.Children.Add(titleBlock);

        // Body (optional)
        if (!string.IsNullOrEmpty(body))
        {
            var bodyBlock = CreateBodyBlock(body);
            panel.Children.Add(bodyBlock);
        }

        // Hint (optional)
        if (!string.IsNullOrEmpty(hint))
        {
            var hintBlock = CreateHintBlock(hint);
            panel.Children.Add(hintBlock);
        }

        return panel;
    }

    /// <summary>
    /// Creates an info panel for cache availability notifications.
    /// </summary>
    public StackPanel CreateCacheAvailablePanel(int cachedCount, int totalCount, string actionHint)
    {
        return CreateInfoPanel(
            title: "\ud83d\udce6 Market Data Available in Cache",
            body: $"{cachedCount} of {totalCount} items have cached market data.",
            hint: actionHint,
            titleStyle: InfoPanelTitleStyle.AccentGold);
    }

    /// <summary>
    /// Creates a placeholder panel for when no data is available.
    /// </summary>
    public StackPanel CreateNoDataPanel(string title, string actionHint, string? detail = null)
    {
        return CreateInfoPanel(
            title: title,
            body: actionHint,
            hint: detail,
            titleStyle: InfoPanelTitleStyle.Gray);
    }

    /// <summary>
    /// Creates a placeholder panel for when no plan exists.
    /// </summary>
    public StackPanel CreateNoPlanPanel(string actionHint)
    {
        return CreateInfoPanel(
            title: "Build a plan to see market analysis",
            hint: actionHint,
            titleStyle: InfoPanelTitleStyle.Gray,
            centerVertically: false);
    }

    private TextBlock CreateTitleBlock(string text, InfoPanelTitleStyle style)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        block.Foreground = style switch
        {
            InfoPanelTitleStyle.AccentGold => TryFindBrush("Brush.Accent.Primary", Brushes.Gold),
            InfoPanelTitleStyle.Accent => TryFindBrush("Brush.Accent.Primary", Brushes.LightBlue),
            InfoPanelTitleStyle.Gray => TryFindBrush("Brush.Text.Muted", Brushes.Gray),
            _ => TryFindBrush("Brush.Text.Muted", Brushes.Gray)
        };

        return block;
    }

    private TextBlock CreateBodyBlock(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = TryFindBrush("Brush.Text.Secondary", Brushes.LightGray),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
    }

    private TextBlock CreateHintBlock(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TryFindBrush("Brush.Text.Muted", Brushes.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
    }

    private static Brush TryFindBrush(string resourceKey, Brush fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }
        return fallback;
    }
}

/// <summary>
/// Title style options for info panels.
/// </summary>
public enum InfoPanelTitleStyle
{
    /// <summary>Gold accent color (for positive/important notifications)</summary>
    AccentGold,
    /// <summary>Standard accent color (for neutral notifications)</summary>
    Accent,
    /// <summary>Gray color (for placeholders/empty states)</summary>
    Gray
}
