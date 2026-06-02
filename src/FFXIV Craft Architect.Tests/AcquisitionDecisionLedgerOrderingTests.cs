using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionDecisionLedgerOrderingTests
{
    [Fact]
    public void GetOrderedRows_WithNoSort_PreservesLedgerOrder()
    {
        var rows = new[]
        {
            CreateRow("b", "Bronze Ore", 10, AcquisitionSource.Craft, estimatedCost: "2,000g"),
            CreateRow("a", "Adamantite Ore", 3, AcquisitionSource.MarketBuyNq, estimatedCost: "900g")
        };

        var ordered = AcquisitionDecisionLedgerOrdering.GetOrderedRows(
            rows,
            AcquisitionDecisionLedgerSortColumn.Item,
            sortDescending: false,
            hasActiveSort: false);

        Assert.Equal(["Bronze Ore", "Adamantite Ore"], ordered.Select(row => row.ItemName));
    }

    [Fact]
    public void GetOrderedRows_ItemColumnSortsByNameWithNodeTieBreaker()
    {
        var rows = new[]
        {
            CreateRow("node-2", "Bronze Ore", 10, AcquisitionSource.Craft),
            CreateRow("node-1", "Adamantite Ore", 3, AcquisitionSource.MarketBuyNq),
            CreateRow("node-3", "Bronze Ore", 2, AcquisitionSource.VendorBuy)
        };

        var ordered = AcquisitionDecisionLedgerOrdering.GetOrderedRows(
            rows,
            AcquisitionDecisionLedgerSortColumn.Item,
            sortDescending: false);

        Assert.Equal(["node-1", "node-2", "node-3"], ordered.Select(row => row.NodeId));
    }

    [Fact]
    public void GetOrderedRows_CalculatedTotalUsesNumericCost()
    {
        var rows = new[]
        {
            CreateRow("missing", "Missing", 1, AcquisitionSource.MarketBuyNq, estimatedCost: "Needs data"),
            CreateRow("expensive", "Expensive", 1, AcquisitionSource.MarketBuyNq, estimatedCost: "12,000g"),
            CreateRow("cheap", "Cheap", 1, AcquisitionSource.VendorBuy, estimatedCost: "900g")
        };

        var ordered = AcquisitionDecisionLedgerOrdering.GetOrderedRows(
            rows,
            AcquisitionDecisionLedgerSortColumn.CalculatedTotal,
            sortDescending: false);

        Assert.Equal(["cheap", "expensive", "missing"], ordered.Select(row => row.NodeId));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ToggleSort_MatchesSharedAscendingDescendingCycle(
        bool currentDescending,
        bool expectedDescending)
    {
        var next = AcquisitionDecisionLedgerOrdering.ToggleSort(
            AcquisitionDecisionLedgerSortColumn.Source,
            currentDescending,
            AcquisitionDecisionLedgerSortColumn.Source);

        Assert.Equal(AcquisitionDecisionLedgerSortColumn.Source, next.Column);
        Assert.Equal(expectedDescending, next.Descending);
    }

    private static DecisionRow CreateRow(
        string nodeId,
        string name,
        int totalQuantity,
        AcquisitionSource source,
        string estimatedCost = "0g",
        bool isActiveProcurement = true,
        bool isFullySuppressed = false,
        string marketEvidence = "Ready")
    {
        var node = new PlanNode
        {
            NodeId = nodeId,
            ItemId = Math.Abs(nodeId.GetHashCode()),
            Name = name,
            Source = source,
            Quantity = totalQuantity,
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBuyFromVendor = source == AcquisitionSource.VendorBuy,
            Yield = 1
        };

        return new DecisionRow(
            node,
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.Children.Count > 0,
            node.CanCraft,
            node.CanBeHq,
            node.Yield,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.HqMarketPrice,
            node.VendorPrice,
            Array.Empty<RecipeDemandVendorOption>(),
            totalQuantity,
            ActiveQuantity: isActiveProcurement ? totalQuantity : 0,
            UsedIn: "Recipe",
            HasSuppressedOccurrences: isFullySuppressed,
            isFullySuppressed,
            SuppressedBy: isFullySuppressed ? ["Parent"] : [],
            isActiveProcurement,
            HasEditableOccurrences: true,
            IsMarketCandidate: true,
            marketEvidence,
            estimatedCost);
    }
}
