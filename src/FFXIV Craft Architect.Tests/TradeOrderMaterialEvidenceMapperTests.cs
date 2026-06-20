using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class TradeOrderMaterialEvidenceMapperTests
{
    [Fact]
    public void ToMaterialSnapshots_PreservesEvidenceAndRoundsTotalCost()
    {
        var timestamp = new DateTime(2026, 6, 19, 14, 30, 0, DateTimeKind.Utc);

        var snapshot = Assert.Single(TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(
            [
                new CommissionPayrollInputLine(
                    12,
                    "Cobalt Ore",
                    3,
                    12.5m,
                    RequiresHq: true,
                    CommissionMaterialResponsibility.Crafter,
                    "Acquisition recommendation",
                    "Cobalt Ore unit cost uses Siren recommendation.",
                    timestamp,
                    ["Very old market data."])
            ]));

        Assert.Equal(12, snapshot.ItemId);
        Assert.Equal("Cobalt Ore", snapshot.Name);
        Assert.Equal(3, snapshot.Quantity);
        Assert.True(snapshot.RequiresHq);
        Assert.Equal(12.5m, snapshot.UnitCost);
        Assert.Equal(38m, snapshot.TotalCost);
        Assert.Equal("Acquisition recommendation", snapshot.EvidenceSource);
        Assert.Equal("Cobalt Ore unit cost uses Siren recommendation.", snapshot.UnitCostExplanation);
        Assert.Equal(timestamp, snapshot.EvidenceTimestampUtc);
        Assert.Equal(["Very old market data."], snapshot.Warnings);
    }
}
