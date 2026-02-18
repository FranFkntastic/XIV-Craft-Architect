using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    private enum MainTab
    {
        RecipePlanner,
        MarketAnalysis,
        ProcurementPlanner
    }

    private MainTab _activeTab = MainTab.RecipePlanner;

    /// <summary>
    /// Switches to the Recipe Planner tab.
    /// </summary>
    private void OnRecipePlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.RecipePlanner);
    }

    /// <summary>
    /// Switches to the Market Analysis tab.
    /// </summary>
    private void OnMarketAnalysisTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.MarketAnalysis);
    }

    /// <summary>
    /// Switches to the Procurement Planner tab.
    /// </summary>
    private void OnProcurementPlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.ProcurementPlanner);
    }

    /// <summary>
    /// Centralized tab activation for shell navigation and side-panel visibility.
    /// </summary>
    private void ActivateTab(MainTab tab)
    {
        _activeTab = tab;

        SetTabActiveState(RecipePlannerTab, tab == MainTab.RecipePlanner);
        SetTabActiveState(MarketAnalysisTab, tab == MainTab.MarketAnalysis);
        SetTabActiveState(ProcurementPlannerTab, tab == MainTab.ProcurementPlanner);

        RecipePlannerContent.Visibility = tab == MainTab.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        MarketAnalysisContent.Visibility = tab == MainTab.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        ProcurementPlannerContent.Visibility = tab == MainTab.ProcurementPlanner ? Visibility.Visible : Visibility.Collapsed;

        RecipePlannerLeftPanel.Visibility = tab == MainTab.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        MarketAnalysisLeftPanel.Visibility = tab == MainTab.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        ProcurementPlannerLeftPanel.Visibility = tab == MainTab.ProcurementPlanner ? Visibility.Visible : Visibility.Collapsed;

        switch (tab)
        {
            case MainTab.RecipePlanner:
                MarketTotalCostText.Text = string.Empty;
                StatusLabel.Text = "Recipe Planner";
                break;
            case MainTab.MarketAnalysis:
                if (_currentPlan != null)
                {
                    PopulateProcurementPanel();
                }

                StatusLabel.Text = "Market Analysis";
                break;
            case MainTab.ProcurementPlanner:
                MarketTotalCostText.Text = string.Empty;
                if (_currentPlan != null)
                {
                    PopulateProcurementPlanSummary();
                }

                StatusLabel.Text = "Procurement Plan";
                break;
        }
    }

    /// <summary>
    /// Sets visual active state for a tab.
    /// </summary>
    private void SetTabActive(Border tab)
    {
        tab.Background = (Brush)FindResource("Brush.Accent.Primary");
        ((TextBlock)tab.Child).Foreground = (Brush)FindResource("Brush.Text.OnAccent");
    }

    /// <summary>
    /// Sets visual inactive state for a tab.
    /// </summary>
    private void SetTabInactive(Border tab)
    {
        tab.Background = Brushes.Transparent;
        ((TextBlock)tab.Child).Foreground = (Brush)FindResource("Brush.Accent.Primary");
    }

    private void SetTabActiveState(Border tab, bool isActive)
    {
        if (isActive)
        {
            SetTabActive(tab);
            return;
        }

        SetTabInactive(tab);
    }

    /// <summary>
    /// Checks if Market Analysis or Procurement Planner tab is visible.
    /// </summary>
    private bool IsMarketViewVisible()
    {
        return _activeTab is MainTab.MarketAnalysis or MainTab.ProcurementPlanner;
    }

    /// <summary>
    /// Handles procurement sort selection change.
    /// </summary>
    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentPlan == null)
        {
            return;
        }

        if (IsMarketViewVisible() && _currentMarketPlans.Any())
        {
            PopulateProcurementPanel();
        }
    }

    /// <summary>
    /// Handles procurement mode (MinimizeTotalCost/MaximizeValue) change.
    /// </summary>
    private void OnProcurementModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsService == null || e.AddedItems.Count == 0)
        {
            return;
        }

        if (ProcurementModeCombo.SelectedIndex >= 0)
        {
            var mode = ProcurementModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _logger.LogInformation("[OnProcurementModeChanged] User changed mode to '{Mode}', saving setting", mode);
            _settingsService.Set("planning.default_recommendation_mode", mode);

            if (IsMarketViewVisible() && _currentPlan != null)
            {
                PopulateProcurementPanel();
            }
        }
    }
}
