using System.Collections.Frozen;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public partial class AppState
{
    public void NotifyPlanChanged()
    {
        PublishChange(AppStateChangeScope.PlanStructure, raisePlanChanged: true);
    }

    public bool AddProjectItem(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_projectItems.Any(existing => existing.Id == item.Id))
        {
            return false;
        }

        _projectItems.Add(CloneProjectItem(item));
        NotifyPlanChanged();
        return true;
    }

    public bool RemoveProjectItem(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        var removed = existingItem != null && _projectItems.Remove(existingItem);
        if (removed)
        {
            NotifyPlanChanged();
        }

        return removed;
    }

    public void ReplaceProjectItems(IEnumerable<ProjectItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        ReplaceListContents(_projectItems, items.Select(CloneProjectItem));
        NotifyPlanChanged();
    }

    public void ToggleProjectItemHq(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        if (existingItem == null)
        {
            return;
        }

        existingItem.MustBeHq = !existingItem.MustBeHq;
        NotifyPlanChanged();
    }

    public bool UpdateProjectItemQuantity(ProjectItem item, int quantity)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        if (existingItem == null)
        {
            return false;
        }

        var normalizedQuantity = Math.Clamp(quantity, 1, 9999);
        if (existingItem.Quantity == normalizedQuantity)
        {
            return false;
        }

        existingItem.Quantity = normalizedQuantity;
        NotifyPlanChanged();
        return true;
    }

    public void NotifyShoppingListChanged()
    {
        PublishChange(
            AppStateChangeScope.MarketAnalysis | AppStateChangeScope.ShoppingItems,
            raiseShoppingListChanged: true);
    }

    public void NotifyShoppingItemsChanged()
    {
        PublishChange(AppStateChangeScope.ShoppingItems, raiseShoppingListChanged: true);
    }

    public void NotifyPlanDecisionChanged()
    {
        PublishChange(AppStateChangeScope.PlanDecision, raisePlanChanged: true);
    }

    public void NotifyPlanPriceChanged()
    {
        PublishChange(AppStateChangeScope.PlanPrice, raisePlanChanged: true);
    }

    public void NotifyProcurementOverlayChanged()
    {
        PublishChange(AppStateChangeScope.ProcurementOverlay, raiseShoppingListChanged: true);
    }

    public void ApplyPlanDecisionChange(
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        bool clearProcurementOverlay)
    {
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        using (BeginStateChangeBatch())
        {
            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
            if (clearProcurementOverlay)
            {
                ClearProcurementOverlay();
            }

            NotifyPlanDecisionChanged();
        }
    }

    public void ApplyPlanDefaultsReconciled(
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        bool acquisitionDecisionsChanged)
    {
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        using (BeginStateChangeBatch())
        {
            if (acquisitionDecisionsChanged)
            {
                NotifyPlanDecisionChanged();
            }
            else
            {
                NotifyPlanChanged();
            }

            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
        }
    }

    public void ApplyPlanPriceChange()
    {
        NotifyPlanPriceChanged();
    }

    public void RequestPlanAndMarketRefresh()
    {
        NotifyShoppingListChanged();
        NotifyPlanChanged();
    }

    public void ReplaceShoppingItemsFromActivePlan(IReadOnlyList<MaterialAggregate> activeProcurementItems)
    {
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        ReplaceListContents(_shoppingItems, activeProcurementItems
            .Where(item => item.TotalQuantity > 0)
            .Select(item => new MarketShoppingItem
            {
                Id = item.ItemId,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.TotalQuantity
            }));
        NotifyShoppingItemsChanged();
    }

    public void ApplyBuiltRecipePlan(
        CraftingPlan plan,
        IReadOnlyList<MaterialAggregate> activeProcurementItems)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        CurrentPlan = plan;
        AdvancePlanSession();
        AutoExpandItemId = null;
        using (BeginStateChangeBatch())
        {
            ClearMarketAnalysisState();
            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
            NotifyPlanChanged();
        }
    }

    public void ApplyImportedProjectItems(IEnumerable<ProjectItem> projectItems)
    {
        ArgumentNullException.ThrowIfNull(projectItems);

        using (BeginStateChangeBatch())
        {
            ReplaceProjectItems(projectItems);
            CurrentPlan = null;
            AdvancePlanSession();
            AutoExpandItemId = null;
            ClearCurrentPlanId();
            _shoppingItems.Clear();
            NotifyShoppingItemsChanged();
            ClearMarketAnalysisState();
        }
    }

    public void ActivateRecipePlan(
        CraftingPlan plan,
        IEnumerable<ProjectItem> projectItems,
        string? selectedDataCenter,
        bool clearCurrentPlanId,
        IReadOnlyList<MaterialAggregate> activeProcurementItems)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(projectItems);
        ArgumentNullException.ThrowIfNull(activeProcurementItems);

        using (BeginStateChangeBatch())
        {
            CurrentPlan = plan;
            AdvancePlanSession();
            AutoExpandItemId = null;
            ReplaceProjectItems(projectItems);

            if (!string.IsNullOrWhiteSpace(selectedDataCenter))
            {
                SelectedDataCenter = selectedDataCenter;
            }

            if (clearCurrentPlanId)
            {
                ClearCurrentPlanId();
            }

            ClearMarketAnalysisState();
            ReplaceShoppingItemsFromActivePlan(activeProcurementItems);
        }
    }

    public bool IsCurrentPlanSession(CraftingPlan? plan, long planSessionVersion)
    {
        return ReferenceEquals(CurrentPlan, plan) &&
               _planSessionVersion == planSessionVersion;
    }

    public void NotifySettingsChanged()
    {
        PublishChange(AppStateChangeScope.Settings);
    }

    public void ExpandAllRecipeNodes()
    {
        _recipeTreeAllExpanded = true;
        _recipeTreeExpandVersion++;
        OnRecipeTreeExpandChanged?.Invoke();
    }

    /// <summary>
    /// Collapse all nodes in the recipe tree.
    /// </summary>
    public void CollapseAllRecipeNodes()
    {
        _recipeTreeAllExpanded = false;
        _recipeTreeExpandVersion++;
        OnRecipeTreeExpandChanged?.Invoke();
    }

    /// <summary>
    /// Set status message and optionally show busy state.
    /// </summary>
    public void SyncShoppingToProject()
    {
        ReplaceListContents(_projectItems, _shoppingItems.Select(s => new ProjectItem
        {
            Id = s.Id,
            Name = s.Name,
            IconId = s.IconId,
            Quantity = s.Quantity,
            MustBeHq = false
        }));
        NotifyPlanChanged();
    }

    /// <summary>
    /// Convert project items to shopping items for Market Logistics
    /// </summary>
    public void SyncProjectToShopping()
    {
        ReplaceListContents(_shoppingItems, _projectItems.Select(p => new MarketShoppingItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity
        }));
        NotifyShoppingListChanged();
    }

    /// <summary>
    /// Clear all current plan data.
    /// </summary>
    public void ClearPlan()
    {
        CurrentPlan = null;
        AdvancePlanSession();
        _projectItems.Clear();
        _shoppingItems.Clear();
        AutoExpandItemId = null;
        ClearMarketAnalysisState();
        ClearCurrentPlanId();
        NotifyPlanChanged();
        NotifyShoppingListChanged();
    }

}
