using System.ComponentModel;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for the MarketLogisticsCoordinator selection state management.
/// Tests the MVVM binding pattern for the expanded panel in split-pane view.
/// </summary>
public class MarketLogisticsCoordinatorSelectionTests
{
    private readonly MarketLogisticsCoordinator _coordinator;

    public MarketLogisticsCoordinatorSelectionTests()
    {
        var mockCacheService = new Mock<IMarketCacheService>();
        var mockLogger = new Mock<ILogger<MarketLogisticsCoordinator>>();
        var shoppingService = new MarketShoppingService(mockCacheService.Object);
        _coordinator = new MarketLogisticsCoordinator(shoppingService, mockLogger.Object);
    }

    #region Initial State Tests

    [Fact]
    public void InitialState_SelectedExpandedPanel_IsNull()
    {
        Assert.Null(_coordinator.SelectedExpandedPanel);
    }

    [Fact]
    public void InitialState_SelectedItemId_IsNull()
    {
        Assert.Null(_coordinator.SelectedItemId);
    }

    #endregion

    #region SelectItem Tests

    [Fact]
    public void SelectItem_WhenItemExists_SetsSelectedExpandedPanel()
    {
        var plan = CreateTestPlan(itemId: 123, name: "Test Item");
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });

        _coordinator.SelectItem(123);

        Assert.NotNull(_coordinator.SelectedExpandedPanel);
        Assert.Equal(123, _coordinator.SelectedItemId);
        Assert.Contains("Test Item", _coordinator.SelectedExpandedPanel!.HeaderText);
    }

    [Fact]
    public void SelectItem_WhenItemNotFound_DoesNotChangeSelection()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });

        _coordinator.SelectItem(999);

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    [Fact]
    public void SelectItem_WhenSameItemSelected_TogglesOff()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        _coordinator.SelectItem(123);

        _coordinator.SelectItem(123);

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    [Fact]
    public void SelectItem_WhenDifferentItemSelected_ReplacesSelection()
    {
        var plan1 = CreateTestPlan(itemId: 123, name: "Item 1");
        var plan2 = CreateTestPlan(itemId: 456, name: "Item 2");
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan1, plan2 });
        _coordinator.SelectItem(123);

        _coordinator.SelectItem(456);

        Assert.NotNull(_coordinator.SelectedExpandedPanel);
        Assert.Equal(456, _coordinator.SelectedItemId);
        Assert.Contains("Item 2", _coordinator.SelectedExpandedPanel!.HeaderText);
    }

    #endregion

    #region ClearSelection Tests

    [Fact]
    public void ClearSelection_WhenItemSelected_ClearsSelection()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        _coordinator.SelectItem(123);

        _coordinator.ClearSelection();

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    [Fact]
    public void ClearSelection_WhenNothingSelected_DoesNotThrow()
    {
        var exception = Record.Exception(() => _coordinator.ClearSelection());
        Assert.Null(exception);
    }

    #endregion

    #region SetAvailablePlans Tests

    [Fact]
    public void SetAvailablePlans_WhenSelectedItemNotInNewList_ClearsSelection()
    {
        var plan1 = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan1 });
        _coordinator.SelectItem(123);

        var plan2 = CreateTestPlan(itemId: 456);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan2 });

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    [Fact]
    public void SetAvailablePlans_WhenSelectedItemInNewList_KeepsSelection()
    {
        var plan1 = CreateTestPlan(itemId: 123, name: "Original");
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan1 });
        _coordinator.SelectItem(123);

        var plan2 = CreateTestPlan(itemId: 456);
        var plan1Updated = CreateTestPlan(itemId: 123, name: "Updated");
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan1Updated, plan2 });

        Assert.NotNull(_coordinator.SelectedExpandedPanel);
        Assert.Equal(123, _coordinator.SelectedItemId);
    }

    [Fact]
    public void SetAvailablePlans_WithNull_SetsEmptyList()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        _coordinator.SelectItem(123);

        _coordinator.SetAvailablePlans(null!);

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    #endregion

    #region INotifyPropertyChanged Tests

    [Fact]
    public void Coordinator_Implements_INotifyPropertyChanged()
    {
        Assert.IsAssignableFrom<INotifyPropertyChanged>(_coordinator);
    }

    [Fact]
    public void SelectItem_Raises_PropertyChanged_ForSelectedExpandedPanel()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        var propertyChanges = new List<string>();
        ((INotifyPropertyChanged)_coordinator).PropertyChanged += (s, e) => propertyChanges.Add(e.PropertyName ?? "null");

        _coordinator.SelectItem(123);

        Assert.Contains("SelectedExpandedPanel", propertyChanges);
    }

    [Fact]
    public void SelectItem_Raises_PropertyChanged_ForSelectedItemId()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        var propertyChanges = new List<string>();
        ((INotifyPropertyChanged)_coordinator).PropertyChanged += (s, e) => propertyChanges.Add(e.PropertyName ?? "null");

        _coordinator.SelectItem(123);

        Assert.Contains("SelectedItemId", propertyChanges);
    }

    [Fact]
    public void ClearSelection_Raises_PropertyChanged_ForBothProperties()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        _coordinator.SelectItem(123);
        var propertyChanges = new List<string>();
        ((INotifyPropertyChanged)_coordinator).PropertyChanged += (s, e) => propertyChanges.Add(e.PropertyName ?? "null");

        _coordinator.ClearSelection();

        Assert.Contains("SelectedExpandedPanel", propertyChanges);
        Assert.Contains("SelectedItemId", propertyChanges);
    }

    #endregion

    #region ExpandedPanelViewModel Close Tests

    [Fact]
    public void ExpandedPanelViewModel_CloseCommand_ClearsCoordinatorSelection()
    {
        var plan = CreateTestPlan(itemId: 123);
        _coordinator.SetAvailablePlans(new List<DetailedShoppingPlan> { plan });
        _coordinator.SelectItem(123);
        var viewModel = _coordinator.SelectedExpandedPanel;

        viewModel!.CloseCommand.Execute(null);

        Assert.Null(_coordinator.SelectedExpandedPanel);
        Assert.Null(_coordinator.SelectedItemId);
    }

    #endregion

    #region Helper Methods

    private static DetailedShoppingPlan CreateTestPlan(int itemId, string name = "Test Item")
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10,
            DCAveragePrice = 1000,
            WorldOptions = new List<WorldShoppingSummary>
            {
                new WorldShoppingSummary
                {
                    WorldName = "TestWorld",
                    WorldId = 1,
                    TotalCost = 9500,
                    AveragePricePerUnit = 950,
                    IsHomeWorld = true,
                    IsTravelProhibited = false,
                    IsBlacklisted = false,
                    HasSufficientStock = true
                }
            }
        };
    }

    #endregion
}
