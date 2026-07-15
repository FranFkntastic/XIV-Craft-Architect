using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class AcquisitionDiagnosticSelectionServiceTests
{
    [Fact]
    public void TryGetCurrent_ReturnsSelectionOnlyForMatchingPlanSession()
    {
        var service = new AcquisitionDiagnosticSelectionService();
        var row = CreateRow();

        service.SetSelection(row, resolvedMarketPlan: null, planSessionVersion: 12);

        Assert.True(service.TryGetCurrent(12, out var current));
        Assert.Same(row, current.Row);
        Assert.False(service.TryGetCurrent(13, out _));
    }

    [Fact]
    public void Clear_RemovesSelection()
    {
        var service = new AcquisitionDiagnosticSelectionService();
        service.SetSelection(CreateRow(), resolvedMarketPlan: null, planSessionVersion: 12);

        service.Clear();

        Assert.False(service.TryGetCurrent(12, out _));
    }

    private static DecisionRow CreateRow()
    {
        var node = new PlanNode { NodeId = "node-5061", ItemId = 5061, Name = "Darksteel Nugget" };
        return new DecisionRow(
            node,
            node.NodeId,
            node.ItemId,
            node.Name,
            IconId: 0,
            Source: AcquisitionSource.MarketBuyNq,
            SourceReason: AcquisitionSourceReason.UserSelected,
            MustBeHq: false,
            HasChildren: true,
            CanCraft: true,
            CanBeHq: true,
            Yield: 1,
            CanBuyFromMarket: true,
            CanBuyFromVendor: false,
            UnitPrice: 1_185m,
            HqUnitPrice: 1_185m,
            VendorUnitPrice: 0m,
            VendorOptions: [],
            TotalQuantity: 1,
            ActiveQuantity: 1,
            UsedIn: "Project item x1",
            HasSuppressedOccurrences: false,
            IsFullySuppressed: false,
            SuppressedBy: [],
            IsActiveProcurement: true,
            HasEditableOccurrences: true,
            IsMarketCandidate: true,
            MarketEvidence: "Halicarnassus - 1/1",
            EstimatedCost: "1,185g");
    }
}
