using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Services.UI;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.UIBuilders;

/// <summary>
/// Builds WPF UI elements for the procurement panel (split-pane and legacy views).
/// Separates UI creation from view logic.
/// </summary>
public class ProcurementPanelBuilder
{
    private readonly SettingsService _settingsService;
    private readonly InfoPanelBuilder _infoPanelBuilder;
    private readonly ILogger<ProcurementPanelBuilder>? _logger;

    // UI Element references
    private readonly Panel _splitPaneCardsGrid;
    private readonly Panel _splitPaneExpandedContent;
    private readonly UIElement _splitPaneExpandedPanel;
    private readonly TextBlock _marketTotalCostText;
    private readonly Panel _procurementPlanPanel;
    private readonly Panel _legacyProcurementPanel;

    public ProcurementPanelBuilder(
        SettingsService settingsService,
        InfoPanelBuilder infoPanelBuilder,
        Panel splitPaneCardsGrid,
        Panel splitPaneExpandedContent,
        UIElement splitPaneExpandedPanel,
        TextBlock marketTotalCostText,
        Panel procurementPlanPanel,
        Panel legacyProcurementPanel,
        ILogger<ProcurementPanelBuilder>? logger = null)
    {
        _settingsService = settingsService;
        _infoPanelBuilder = infoPanelBuilder;
        _splitPaneCardsGrid = splitPaneCardsGrid;
        _splitPaneExpandedContent = splitPaneExpandedContent;
        _splitPaneExpandedPanel = splitPaneExpandedPanel;
        _marketTotalCostText = marketTotalCostText;
        _procurementPlanPanel = procurementPlanPanel;
        _legacyProcurementPanel = legacyProcurementPanel;
        _logger = logger;
    }

    /// <summary>
    /// Determines whether to use split-pane or legacy view based on settings.
    /// </summary>
    public bool UseSplitPane => _settingsService.Get<bool>("ui.use_split_pane_market_view", true);

    /// <summary>
    /// Clears all procurement panels.
    /// </summary>
    public void ClearPanels()
    {
        _splitPaneCardsGrid.Children.Clear();
        _splitPaneExpandedContent.Children.Clear();
        _legacyProcurementPanel.Children.Clear();
        _procurementPlanPanel.Children.Clear();
    }

    /// <summary>
    /// Shows placeholder when no plan exists (split-pane view).
    /// </summary>
    public void ShowNoPlanPlaceholderSplitPane()
    {
        _marketTotalCostText.Text = "";
        
        var infoPanel = _infoPanelBuilder.CreateNoPlanPanel(
            "No procurement plan available - fetch market data to generate actionable plan");
        _splitPaneCardsGrid.Children.Add(infoPanel);
        
        _procurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>
    /// Shows placeholder when no plan exists (legacy view).
    /// </summary>
    public void ShowNoPlanPlaceholderLegacy()
    {
        var infoPanel = _infoPanelBuilder.CreateNoPlanPanel(
            "No procurement plan available - fetch market data to generate actionable plan");
        _legacyProcurementPanel.Children.Add(infoPanel);
        
        _procurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>
    /// Shows placeholder when market data needs to be fetched (split-pane).
    /// </summary>
    public void ShowRefreshNeededPlaceholderSplitPane(int materialCount)
    {
        _marketTotalCostText.Text = "";
        
        var placeholderPanel = _infoPanelBuilder.CreateNoDataPanel(
            title: "No market data available",
            actionHint: "Click 'Refresh Market Data' to see world recommendations",
            detail: $"Materials to analyze: {materialCount}");
        
        _splitPaneCardsGrid.Children.Add(placeholderPanel);
        
        _procurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>
    /// Shows placeholder when market data needs to be fetched (legacy).
    /// </summary>
    public void ShowRefreshNeededPlaceholderLegacy(int materialCount)
    {
        var placeholderPanel = _infoPanelBuilder.CreateNoDataPanel(
            title: "No market data available",
            actionHint: "Click 'Refresh Market Data' to see world recommendations and generate a procurement plan",
            detail: $"Materials to analyze: {materialCount}");
        
        _legacyProcurementPanel.Children.Add(placeholderPanel);
        
        _procurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>
    /// Creates a summary panel for legacy view showing grand total and stats.
    /// </summary>
    public Border CreateLegacySummaryPanel(long grandTotal, int itemsWithOptions, int itemsWithoutOptions)
    {
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
        return summaryPanel;
    }

    /// <summary>
    /// Updates the total cost display for split-pane view.
    /// </summary>
    public void UpdateSplitPaneTotal(long grandTotal, int itemsWithOptions)
    {
        _marketTotalCostText.Text = $"Total: {grandTotal:N0}g  \u2022  {itemsWithOptions} items";
    }

    /// <summary>
    /// Clears the expanded panel in split-pane view.
    /// </summary>
    public void ClearExpandedPanel()
    {
        _splitPaneExpandedContent.Children.Clear();
    }

    /// <summary>
    /// Shows or hides the expanded panel.
    /// </summary>
    public void SetExpandedPanelVisibility(bool visible)
    {
        _splitPaneExpandedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Adds content to the expanded panel.
    /// </summary>
    public void AddToExpandedPanel(UIElement element)
    {
        _splitPaneExpandedContent.Children.Add(element);
    }

    /// <summary>
    /// Gets the legacy procurement panel for direct manipulation.
    /// </summary>
    public Panel LegacyPanel => _legacyProcurementPanel;

    /// <summary>
    /// Gets the split-pane cards grid for direct manipulation.
    /// </summary>
    public Panel SplitPaneCardsGrid => _splitPaneCardsGrid;

    /// <summary>
    /// Gets the procurement plan panel for direct manipulation.
    /// </summary>
    public Panel ProcurementPlanPanel => _procurementPlanPanel;
}
