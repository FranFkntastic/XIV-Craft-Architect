using System.Windows;
using System.Windows.Controls;
using FFXIV_Craft_Architect.Services.UI;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.UIBuilders;

public class ProcurementPanelBuilder
{
    private readonly InfoPanelBuilder _infoPanelBuilder;
    private readonly ILogger<ProcurementPanelBuilder>? _logger;

    private readonly Panel _splitPaneCardsGrid;
    private readonly Panel _splitPaneExpandedContent;
    private readonly UIElement _splitPaneExpandedPanel;
    private readonly TextBlock _marketTotalCostText;
    private readonly Panel _procurementPlanPanel;

    public ProcurementPanelBuilder(
        InfoPanelBuilder infoPanelBuilder,
        Panel splitPaneCardsGrid,
        Panel splitPaneExpandedContent,
        UIElement splitPaneExpandedPanel,
        TextBlock marketTotalCostText,
        Panel procurementPlanPanel,
        ILogger<ProcurementPanelBuilder>? logger = null)
    {
        _infoPanelBuilder = infoPanelBuilder;
        _splitPaneCardsGrid = splitPaneCardsGrid;
        _splitPaneExpandedContent = splitPaneExpandedContent;
        _splitPaneExpandedPanel = splitPaneExpandedPanel;
        _marketTotalCostText = marketTotalCostText;
        _procurementPlanPanel = procurementPlanPanel;
        _logger = logger;
    }

    public void ClearPanels()
    {
        _splitPaneCardsGrid.Children.Clear();
        _splitPaneExpandedContent.Children.Clear();
        _procurementPlanPanel.Children.Clear();
    }

    public void ShowNoPlanPlaceholderSplitPane()
    {
        _marketTotalCostText.Text = string.Empty;

        var infoPanel = _infoPanelBuilder.CreateNoPlanPanel(
            "No procurement plan available - fetch market data to generate actionable plan");
        _splitPaneCardsGrid.Children.Add(infoPanel);

        _procurementPlanPanel.Children.Add(new TextBlock
        {
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    public void ShowRefreshNeededPlaceholderSplitPane(int materialCount)
    {
        _marketTotalCostText.Text = string.Empty;

        var placeholderPanel = _infoPanelBuilder.CreateNoDataPanel(
            title: "No market data available",
            actionHint: "Click 'Refresh Market Data' to see world recommendations",
            detail: $"Materials to analyze: {materialCount}");

        _splitPaneCardsGrid.Children.Add(placeholderPanel);

        _procurementPlanPanel.Children.Add(new TextBlock
        {
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    public void UpdateSplitPaneTotal(long grandTotal, int itemsWithOptions)
    {
        _marketTotalCostText.Text = $"Total: {grandTotal:N0}g  -  {itemsWithOptions} items";
    }

    public void ClearExpandedPanel()
    {
        _splitPaneExpandedContent.Children.Clear();
    }

    public void SetExpandedPanelVisibility(bool visible)
    {
        _splitPaneExpandedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AddToExpandedPanel(UIElement element)
    {
        _splitPaneExpandedContent.Children.Add(element);
    }

    public Panel SplitPaneCardsGrid => _splitPaneCardsGrid;

    public Panel ProcurementPlanPanel => _procurementPlanPanel;
}
