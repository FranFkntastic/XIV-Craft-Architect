using FFXIV_Craft_Architect.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    /// <summary>
    /// Gets the currently selected data center.
    /// </summary>
    public string? GetCurrentDataCenter()
    {
        return DcCombo.SelectedItem as string;
    }

    /// <summary>
    /// Gets the currently selected world.
    /// </summary>
    public string? GetCurrentWorld()
    {
        return WorldCombo.SelectedItem as string;
    }

    /// <summary>
    /// Prompts user to reanalyze cached market data after watch state restore.
    /// </summary>
    private async Task PromptToReanalyzeCachedMarketDataAsync()
    {
        if (_currentPlan == null)
        {
            return;
        }

        var activeProcurementItems = AcquisitionPlanningService.GetActiveProcurementItems(_currentPlan);
        if (activeProcurementItems.Count == 0)
        {
            return;
        }

        var cacheService = App.Services.GetService<Core.Services.IMarketCacheService>();
        if (cacheService == null)
        {
            return;
        }

        var dataCenter = DcCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(dataCenter))
        {
            return;
        }

        var itemIds = activeProcurementItems.Select(m => m.ItemId).ToList();
        var missing = await cacheService.GetMissingAsync(
            itemIds.Select(id => (id, dataCenter)).ToList());
        var cachedCount = itemIds.Count - missing.Count;

        if (cachedCount > 0)
        {
            ProcurementPanel.Children.Clear();

            var infoPanel = _infoPanelBuilder.CreateCacheAvailablePanel(
                cachedCount,
                itemIds.Count,
                "Click 'Refresh Market Data' above to re-analyze using cached data.");

            ProcurementPanel.Children.Add(infoPanel);

            StatusLabel.Text = $"[Watch] State restored. {cachedCount} items have cached market data.";
        }
    }

    /// <summary>
    /// Restores application state from a WatchState after app restart.
    /// </summary>
    public async Task RestoreWatchStateAsync(WatchState state)
    {
        if (state.CurrentPlan == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(state.DataCenter))
        {
            DcCombo.SelectedItem = state.DataCenter;
            OnDataCenterSelected(null, null);

            await Task.Delay(100);

            if (!string.IsNullOrEmpty(state.World))
            {
                WorldCombo.SelectedItem = state.World;
            }
        }

        var selectedDataCenter = DcCombo.SelectedItem as string ?? state.DataCenter ?? "Aether";
        await _recipePlannerCommands.ActivatePlanAsync(
            new Core.Services.CoreActivateRecipePlanRequest(
                state.CurrentPlan,
                ClearCurrentPlanId: false,
                RefreshVendorPricesOnActivation: false,
                RefreshMarketPricesOnActivation: false,
                PriceFetchScope: Core.Models.MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter: selectedDataCenter,
                SelectedRegion: ResolveSelectedRegion(selectedDataCenter)));
        _recipeVm.RefreshFromCoreSession();

        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;

        if (_currentPlan != null)
        {
            DisplayPlanInTreeView(_currentPlan);
        }
        UpdateBuildPlanButtonText();

        StatusLabel.Text = "[Watch] State restored from reload";

        await PromptToReanalyzeCachedMarketDataAsync();
    }
}
