using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkshopHostCraftAppraisalContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void Request_SerializesAsVersionedCamelCaseContract()
    {
        var request = new CraftAppraisalRequest
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            Quantity = 10,
            Scope = new CraftAppraisalScope
            {
                Region = "North America",
                DataCenter = "Aether",
                World = null,
            },
            Options = new CraftAppraisalOptions
            {
                HqPolicy = "Either",
                PricingMode = "CurrentMarketEvidence",
            },
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"itemId\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"quantity\": 10", json, StringComparison.Ordinal);
        Assert.Contains("\"pricingMode\": \"CurrentMarketEvidence\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_RoundTripsWarningsMaterialsAndFreshness()
    {
        var quotedAtUtc = new DateTimeOffset(2026, 7, 4, 14, 30, 0, TimeSpan.Zero);
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
            OutputQuantity = 1,
            EstimatedUnitCost = 80m,
            EstimatedTotalCost = 800m,
            QuotedAtUtc = quotedAtUtc,
            Source = "CraftArchitectLocal",
            Confidence = "Medium",
            IsComplete = true,
            AppraisalStatus = "Complete",
            Materials =
            [
                new CraftAppraisalMaterialQuote
                {
                    ItemId = 3,
                    ItemName = "Example Material",
                    QuantityPerCraft = 2,
                    TotalQuantity = 20,
                    UnitCost = 40m,
                    TotalCost = 800m,
                    AcquisitionSource = "MarketBuyNq",
                    CostSource = "MarketEvidence",
                    CostSourceDetails = "Aether current market evidence",
                    Warnings = ["Market evidence is stale."],
                },
            ],
            Warnings = ["Quote is advisory."],
        };

        var json = JsonSerializer.Serialize(quote, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<CraftAppraisalQuote>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped.SchemaVersion);
        Assert.Equal("gil", roundTripped.Currency);
        Assert.Equal(quotedAtUtc, roundTripped.QuotedAtUtc);
        Assert.Equal("Quote is advisory.", Assert.Single(roundTripped.Warnings));
        Assert.True(roundTripped.IsComplete);
        Assert.Equal("Complete", roundTripped.AppraisalStatus);
        var material = Assert.Single(roundTripped.Materials);
        Assert.Equal(3u, material.ItemId);
        Assert.Equal("MarketBuyNq", material.AcquisitionSource);
        Assert.Equal("Aether current market evidence", material.CostSourceDetails);
        Assert.Equal("Market evidence is stale.", Assert.Single(material.Warnings));
    }

}
