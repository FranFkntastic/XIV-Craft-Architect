using System.Collections.ObjectModel;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.ViewModels;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates watch state (app restart preservation) operations.
/// Separates watch state logic from MainWindow.
/// </summary>
public class WatchStateCoordinator
{
    private readonly RecipePlannerViewModel _recipeVm;
    private readonly MarketAnalysisViewModel _marketVm;
    private readonly ILogger<WatchStateCoordinator> _logger;

    public WatchStateCoordinator(
        RecipePlannerViewModel recipeVm,
        MarketAnalysisViewModel marketVm,
        ILogger<WatchStateCoordinator> logger)
    {
        _recipeVm = recipeVm;
        _marketVm = marketVm;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plan suitable for watch state saving, including market plans.
    /// </summary>
    public CraftingPlan? GetCurrentPlanForWatch()
    {
        var currentPlan = _recipeVm.CurrentPlan;
        if (currentPlan == null)
            return null;

        var marketPlans = _marketVm.ShoppingPlans.Select(vm => vm.Plan).ToList();
        if (marketPlans.Count > 0)
        {
            currentPlan.SavedMarketPlans = marketPlans;
        }
        return currentPlan;
    }

    /// <summary>
    /// Prepares a WatchState for saving.
    /// </summary>
    public WatchState PrepareWatchState(string? dataCenter, string? world)
    {
        var state = new WatchState
        {
            CurrentPlan = GetCurrentPlanForWatch(),
            DataCenter = dataCenter,
            World = world
        };
        return state;
    }

    /// <summary>
    /// Restores project items from a plan's root items.
    /// </summary>
    public ObservableCollection<ProjectItem> RestoreProjectItemsFromPlan(CraftingPlan plan)
    {
        var projectItems = new ObservableCollection<ProjectItem>();
        
        foreach (var rootItem in plan.RootItems)
        {
            projectItems.Add(new ProjectItem
            {
                Id = rootItem.ItemId,
                Name = rootItem.Name,
                Quantity = rootItem.Quantity,
                IsHqRequired = rootItem.MustBeHq
            });
        }
        
        return projectItems;
    }

    /// <summary>
    /// Restores market plans from a saved plan.
    /// </summary>
    public void RestoreMarketPlansFromPlan(CraftingPlan plan)
    {
        if (plan.SavedMarketPlans?.Count > 0 == true)
        {
            _marketVm.SetShoppingPlans(plan.SavedMarketPlans);
        }
    }

    /// <summary>
    /// Checks if a plan has cached market data that can be reanalyzed.
    /// </summary>
    public bool HasCachedMarketData(CraftingPlan plan)
    {
        return plan.SavedMarketPlans?.Count > 0 == true ||
               (plan.RootItems?.Any() == true && 
                plan.RootItems.SelectMany(r => GetAllNodes(r))
                    .Any(n => n.MarketPrice > 0 || n.PriceSource != PriceSource.Unknown));
    }

    /// <summary>
    /// Gets the count of cached items in a plan.
    /// </summary>
    public int GetCachedItemCount(CraftingPlan plan)
    {
        if (plan.SavedMarketPlans?.Count > 0)
            return plan.SavedMarketPlans.Count;

        return plan.RootItems?.SelectMany(r => GetAllNodes(r))
            .Count(n => n.MarketPrice > 0 || n.PriceSource != PriceSource.Unknown) ?? 0;
    }

    private IEnumerable<PlanNode> GetAllNodes(PlanNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in GetAllNodes(child))
            {
                yield return descendant;
            }
        }
    }
}
