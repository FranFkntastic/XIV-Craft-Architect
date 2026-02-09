using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Helpers;
using FFXIVCraftArchitect.Services.Interfaces;
using FFXIVCraftArchitect.ViewModels;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Implementation of ICardFactory for creating standardized card UI elements.
/// </summary>
public class CardFactory : ICardFactory
{
    private static readonly Dictionary<CardType, string> ColorMap = new()
    {
        [CardType.Vendor] = "#3e4a2d",
        [CardType.Market] = "#2d3d4a",
        [CardType.Untradeable] = "#4a3d2d",
        [CardType.Loading] = "#3d3e2d",
        [CardType.Cached] = "#3d3e2d",
        [CardType.Error] = "#4a2d2d",
        [CardType.Neutral] = "#2d2d2d"
    };

    /// <inheritdoc />
    public Border CreateInfoCard(string title, string? content, CardType type)
    {
        var border = CreateBaseBorder(type);
        var stackPanel = new StackPanel();

        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var contentBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(content) ? "(No content)" : content,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas")
        };

        stackPanel.Children.Add(titleBlock);
        stackPanel.Children.Add(contentBlock);
        border.Child = stackPanel;

        return border;
    }

    /// <inheritdoc />
    public Border CreateInfoCard(string title, IEnumerable<string> items, CardType type)
    {
        var border = CreateBaseBorder(type);
        var stackPanel = new StackPanel();

        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };

        stackPanel.Children.Add(titleBlock);

        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            stackPanel.Children.Add(new TextBlock
            {
                Text = "(No items)",
                FontFamily = new FontFamily("Consolas")
            });
        }
        else
        {
            var contentText = string.Join("\n", itemList.Select(item => $"\u2022 {item}"));
            stackPanel.Children.Add(new TextBlock
            {
                Text = contentText,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas")
            });
        }

        border.Child = stackPanel;
        return border;
    }

    /// <inheritdoc />
    public Border CreateDataBoundCard(MarketCardViewModel viewModel, CardStyle style)
    {
        if (viewModel == null)
            throw new ArgumentNullException(nameof(viewModel));

        var border = new Border
        {
            CornerRadius = new CornerRadius(4)
        };

        if (style == CardStyle.Legacy)
        {
            border.Background = ColorHelper.GetMutedAccentBrush();
        }
        else
        {
            // Collapsed style uses neutral background
            var color = (Color)ColorConverter.ConvertFromString("#2d2d2d")!;
            border.Background = new SolidColorBrush(color);
            border.Width = 320;
        }

        var contentControl = new ContentControl
        {
            Content = viewModel
        };

        border.Child = contentControl;
        return border;
    }

    /// <inheritdoc />
    public Border CreatePlaceholder(string title, string message)
    {
        return CreateInfoCard(title, message, CardType.Neutral);
    }

    /// <inheritdoc />
    public Border CreateErrorCard(string title, string errorMessage)
    {
        return CreateInfoCard(title, errorMessage, CardType.Error);
    }

    /// <inheritdoc />
    public Panel CreateMarketPlaceholderPanel(int materialCount, string actionPrompt)
    {
        var placeholderPanel = new StackPanel 
        { 
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0)
        };
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "No market data available",
            Foreground = Brushes.Gray,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = actionPrompt,
            Foreground = Brushes.Gray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = $"Materials to analyze: {materialCount}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        });
        
        return placeholderPanel;
    }

    /// <inheritdoc />
    public Border CreateCollapsedMarketCard(MarketCardViewModel viewModel, bool isExpanded, Action onClick, Func<string, object>? findResource = null)
    {
        var border = new Border
        {
            Background = BrushFrom(isExpanded ? "#3d4a3d" : "#2d2d2d"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Width = 320,
            Cursor = Cursors.Hand,
            BorderBrush = isExpanded ? BrushFrom("#d4a73a") : null,
            BorderThickness = isExpanded ? new Thickness(2) : new Thickness(0)
        };
        
        // Use provided resource lookup or fall back to Application.Current
        var resourceLookup = findResource ?? (key => Application.Current.MainWindow.FindResource(key));
        DataTemplate? template = null;
        try
        {
            template = (DataTemplate?)resourceLookup("CollapsedMarketCardTemplate");
        }
        catch (ResourceReferenceKeyNotFoundException)
        {
            // Template not found - content will still display but without custom template
        }
        
        border.Child = new ContentControl
        {
            Content = viewModel,
            ContentTemplate = template
        };
        
        border.MouseLeftButtonDown += (s, e) => onClick();
        
        return border;
    }

    private static SolidColorBrush BrushFrom(string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor)!;
        return new SolidColorBrush(color);
    }

    private static Border CreateBaseBorder(CardType type)
    {
        return new Border
        {
            Background = BrushFrom(ColorMap[type]),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }
}
