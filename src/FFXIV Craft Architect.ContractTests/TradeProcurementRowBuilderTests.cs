using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeProcurementRowBuilderTests
{
    [Fact]
    public void FullySuppressedPrecraftRemainsInPlan()
    {
        var row = Row(
            source: AcquisitionSource.Craft,
            isActiveProcurement: false,
            isFullySuppressed: true,
            suppressedBy: ["Purchased assembly"],
            hasEditableOccurrences: false);

        Assert.True(TradeProcurementRowBuilder.IsPlanPrecraftRow(row));
        Assert.True(TradeProcurementRowBuilder.ShouldIncludePlanRow(row));
        Assert.Equal(
            "Not currently required because Purchased assembly is sourced directly",
            TradeProcurementRowBuilder.GetPlanRouteDescription(row));
    }

    [Theory]
    [InlineData(AcquisitionSource.MarketBuyNq)]
    [InlineData(AcquisitionSource.OnHand)]
    public void DirectlySourcedPrecraftRemainsRequiredWhileItsIngredientsAreSuppressed(
        AcquisitionSource source)
    {
        var row = Row(
            source,
            isActiveProcurement: true,
            isFullySuppressed: false,
            suppressedBy: [],
            hasEditableOccurrences: true);

        var description = TradeProcurementRowBuilder.GetPlanRouteDescription(row);

        Assert.StartsWith("Required item; its ingredients are not required", description);
        Assert.DoesNotContain("Not currently required", description);
    }

    private static TradeOrderProcurementRow Row(
        AcquisitionSource source,
        bool isActiveProcurement,
        bool isFullySuppressed,
        IReadOnlyList<string> suppressedBy,
        bool hasEditableOccurrences) =>
        new(
            RowKey: "20:false",
            ItemId: 20,
            ItemName: "Cobalt Ingot",
            Quantity: 4_995,
            RequiresHq: false,
            SourceLabel: source.ToString(),
            UnitCost: 100,
            TotalCost: 499_500,
            Responsibility: CommissionMaterialResponsibility.Crafter,
            EvidenceSource: "Test evidence",
            EvidenceStatus: "Priced",
            UnitCostExplanation: "Fixture",
            WarningSummary: string.Empty,
            Warnings: [],
            IsLiveAcquisitionRow: true,
            IsActiveProcurement: isActiveProcurement,
            HasSuppressedOccurrences: isFullySuppressed,
            IsFullySuppressed: isFullySuppressed,
            SuppressedBy: suppressedBy,
            ActiveQuantity: isActiveProcurement ? 4_995 : 0,
            UsedIn: "Cobalt Joint Plate",
            HasEditableOccurrences: hasEditableOccurrences,
            Source: source,
            HasChildren: true,
            AvailableSources: [
                AcquisitionSource.Craft,
                AcquisitionSource.MarketBuyNq,
                AcquisitionSource.OnHand
            ]);
}
