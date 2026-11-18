using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeProcurementPaymentImpactTests
{
    [Theory]
    [InlineData(5, 338, null)]
    [InlineData(4, 338, "Plan differs from terms")]
    [InlineData(5, 0, "Not itemized in terms")]
    public void CoveredOreNeverAddsCashOrPoisonsItsProvidedParent(int termsQuantity, int termsPrice, string? unavailable)
    {
        WorkerPlanOccurrence[] occurrences =
        [
            new("rivets", null, 1, false, 1, AcquisitionSource.Craft, false, false),
            new("nugget", "rivets", 2, false, 1, AcquisitionSource.Craft, false, false),
            new("covered-ore", "nugget", 3, false, 3, AcquisitionSource.MarketBuyNq, true, false),
            new("other-ore", "rivets", 3, false, 2, AcquisitionSource.MarketBuyNq, true, false),
            new("unpriced-crystal", "nugget", 4, false, 1, AcquisitionSource.VendorBuy, true, false)
        ];
        var rows = occurrences.DistinctBy(node => node.ItemId).Select(node => new WorkerAcquisitionRowProjection(
            node.NodeId, node.ItemId, node.NodeId, 0, node.Source, default, false, false, false, false, false, false,
            node.Quantity, node.Quantity, "", false, false, [], node.IsActiveProcurement, true, false, "", "",
            false, 0, 0, [], [])).ToArray();
        WorkerTradeProjection snapshot = new(1, true, "plan", "", "", "", default, [], default, 1, 1,
            [], [], [], [], rows, [], [], occurrences);
        var impacts = TradeProcurementPaymentImpact.CalculateAll(snapshot,
            new Dictionary<(int, bool), CommissionMaterialResponsibility> { [(2, false)] = CommissionMaterialResponsibility.Provided },
            requireRecordedCash: true, materials: [new(Guid.Empty, 3, "Ore", termsQuantity, false,
                CommissionMaterialResponsibility.Crafter, termsPrice)]);

        Assert.Equal(0m, impacts[(2, false)].Cash);
        Assert.Equal(0m, impacts[(4, false)].Cash);
        Assert.Equal(unavailable == null ? 676m : (decimal?)null, impacts[(1, false)].Cash);
        Assert.Equal(impacts[(1, false)].Cash, impacts[(3, false)].Cash);
        Assert.Equal(unavailable, impacts[(1, false)].UnavailableReason);
    }
}
