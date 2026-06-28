using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class TradeOrdersPaymentPresentationTests
{
    [Fact]
    public void FormatMaterialPaymentImpactReturnsTotalForCrafterResponsibility()
    {
        var material = CreateMaterial(CommissionMaterialResponsibility.Crafter, 25m, 250m);

        Assert.Equal("250 gil", TradeOrderPaymentDisplayFormatter.FormatMaterialPaymentImpact(material));
    }

    [Fact]
    public void FormatMaterialPaymentImpactReturnsZeroForProvidedResponsibility()
    {
        var material = CreateMaterial(CommissionMaterialResponsibility.Provided, 25m, 250m);

        Assert.Equal("0 gil", TradeOrderPaymentDisplayFormatter.FormatMaterialPaymentImpact(material));
    }

    [Fact]
    public void FormatMaterialPaymentImpactReturnsNotPricedForMissingTotal()
    {
        var material = CreateMaterial(CommissionMaterialResponsibility.Crafter, 0m, 0m);

        Assert.Equal("Not priced", TradeOrderPaymentDisplayFormatter.FormatMaterialPaymentImpact(material));
    }

    private static TradeCommissionPaymentMaterial CreateMaterial(
        CommissionMaterialResponsibility responsibility,
        decimal unitCost,
        decimal totalCost)
    {
        return new TradeCommissionPaymentMaterial(
            ItemId: 100,
            Name: "Cobalt Ore",
            Quantity: 10,
            RequiresHq: false,
            UnitCost: unitCost,
            TotalCost: totalCost,
            Responsibility: responsibility,
            EvidenceSource: "Market",
            UnitCostExplanation: "Market evidence",
            EvidenceTimestampUtc: null,
            Warnings: []);
    }
}
