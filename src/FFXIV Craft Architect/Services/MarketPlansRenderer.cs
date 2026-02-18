using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Helpers;
using FFXIV_Craft_Architect.Models;
using FFXIV_Craft_Architect.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Renders market plan UI elements directly into the target container. 
/// This is a UI service, not a business logic service.
/// </summary>
public class MarketPlansRenderer : IMarketPlansRenderer
{
    private readonly ILogger<MarketPlansRenderer> _logger;
    private readonly ICardFactory _cardFactory;

    public MarketPlansRenderer(ILogger<MarketPlansRenderer> logger, ICardFactory cardFactory)
    {
        _logger = logger;
        _cardFactory = cardFactory;
    }

    /// <inheritdoc />
    public MarketDisplayResult DisplayMarketPlans(MarketDisplayRequest request)
    {
        var result = new MarketDisplayResult();
        var plans = request.Plans.ToList();

        // Calculate common statistics
        result.GrandTotal = plans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        result.ItemsWithOptions = plans.Count(p => p.HasOptions);

        // Apply view mode specific UI updates
        switch (request.ViewMode)
        {
            case MarketViewMode.Legacy:
                ApplyLegacySummary(request.TargetPanel, result.GrandTotal, result.ItemsWithOptions, plans.Count);
                break;
            case MarketViewMode.SplitPane:
                ApplySplitPaneSummary(request.TotalCostTextBlock, result.GrandTotal, result.ItemsWithOptions);
                break;
        }

        // Sort plans according to the selected criteria
        var sortedPlans = SortPlans(plans, request.SortIndex);

        // Create and add cards to the target panel
        foreach (var plan in sortedPlans)
        {
            var card = CreateCardForViewMode(plan, request.ViewMode, request.ExpandedItemId, request.OnCardClick, request.FindResource);
            request.TargetPanel.Children.Add(card);
            result.Cards.Add((card, plan));
        }

        _logger.LogDebug(
            "Displayed {Count} market plans in {Mode} mode with sort index {SortIndex}",
            plans.Count,
            request.ViewMode,
            request.SortIndex);

        return result;
    }

    /// <summary>
    /// Applies the summary panel for legacy view mode.
    /// </summary>
    private static void ApplyLegacySummary(Panel targetPanel, long grandTotal, int itemsWithOptions, int totalItems)
    {
        var itemsWithoutOptions = totalItems - itemsWithOptions;

        var summaryPanel = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3d3d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var summaryGrid = new Grid();
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var costText = new TextBlock
        {
            Text = $"Total: {grandTotal:N0}g",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(costText, 0);
        summaryGrid.Children.Add(costText);

        var statsText = new TextBlock
        {
            Text = $"{itemsWithOptions} items with data  \u2022  {itemsWithoutOptions} need fetch",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statsText, 1);
        summaryGrid.Children.Add(statsText);

        summaryPanel.Child = summaryGrid;
        targetPanel.Children.Add(summaryPanel);
    }

    /// <summary>
    /// Applies the summary text for split-pane view mode.
    /// </summary>
    private static void ApplySplitPaneSummary(TextBlock? totalCostTextBlock, long grandTotal, int itemsWithOptions)
    {
        if (totalCostTextBlock != null)
        {
            totalCostTextBlock.Text = $"Total: {grandTotal:N0}g  \u2022  {itemsWithOptions} items";
        }
    }

    /// <summary>
    /// Sorts the plans according to the selected sort index.
    /// </summary>
    private static IEnumerable<DetailedShoppingPlan> SortPlans(List<DetailedShoppingPlan> plans, int sortIndex)
    {
        return sortIndex switch
        {
            0 => plans
                .OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ")
                .ThenBy(p => p.Name),
            1 => plans.OrderBy(p => p.Name),
            2 => plans
                .OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0)
                .ThenBy(p => p.Name),
            _ => plans
        };
    }

    /// <summary>
    /// Creates a card for the specified view mode.
    /// </summary>
    private Border CreateCardForViewMode(
        DetailedShoppingPlan plan,
        MarketViewMode viewMode,
        int? expandedItemId,
        Action<DetailedShoppingPlan>? onCardClick,
        Func<string, object>? findResource)
    {
        return viewMode switch
        {
            MarketViewMode.SplitPane => CreateCollapsedCard(plan, expandedItemId, onCardClick, findResource),
            _ => CreateMarketCard(plan)
        };
    }

    /// <summary>
    /// Creates a standard market card for legacy view mode.
    /// </summary>
    private Border CreateMarketCard(DetailedShoppingPlan plan)
    {
        var viewModel = new MarketCardViewModel(plan);

        var border = new Border
        {
            Background = Application.Current?.TryFindResource("Brush.Surface.Card.Market") as Brush
                ?? Application.Current?.TryFindResource("Brush.Surface.Card") as Brush
                ?? Application.Current?.TryFindResource("CardBackgroundBrush") as Brush
                ?? ColorHelper.GetMutedAccentBrush(),
            BorderBrush = Application.Current?.TryFindResource("Brush.Border.Card.Market") as Brush
                ?? Application.Current?.TryFindResource("Brush.Border.Default") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            Tag = plan
        };

        var contentControl = new ContentControl
        {
            Content = viewModel
        };

        border.Child = contentControl;
        return border;
    }

    /// <summary>
    /// Creates a collapsed card for split-pane view mode.
    /// </summary>
    private Border CreateCollapsedCard(
        DetailedShoppingPlan plan,
        int? expandedItemId,
        Action<DetailedShoppingPlan>? onCardClick,
        Func<string, object>? findResource)
    {
        var isExpanded = expandedItemId == plan.ItemId;
        var viewModel = new MarketCardViewModel(plan);

        // Use CardFactory for consistent styling
        var border = _cardFactory.CreateCollapsedMarketCard(
            viewModel,
            isExpanded,
            () => { if (onCardClick != null) onCardClick(plan); },
            findResource);

        border.Tag = plan;
        return border;
    }

    /// <inheritdoc />
    public void BuildExpandedPanel(Panel target, DetailedShoppingPlan plan, Action onClose)
    {
        target.Children.Clear();
        
        var viewModel = new ExpandedPanelViewModel(plan);
        viewModel.CloseRequested += () =>
        {
            onClose();
        };
        
        var contentControl = new ContentControl
        {
            Content = viewModel
        };
        
        target.Children.Add(contentControl);
    }
}
