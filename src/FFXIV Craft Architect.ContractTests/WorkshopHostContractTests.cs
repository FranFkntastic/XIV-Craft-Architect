using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class WorkshopHostContractTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AppraisalRequestJson_IsVersionedAndCarriesItemName()
    {
        var request = new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
            Scope = new CraftAppraisalScope
            {
                Region = "North America",
                DataCenter = "Aether",
                World = "Siren",
            },
        };

        var json = JsonSerializer.Serialize(request, WireJson);

        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"itemName\":\"Varnish\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"pricingMode\":\"CurrentMarketEvidence\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMaterialPriceEvidence_ProducesIncompleteAdvisoryQuote()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 7017,
                    Name = "Varnish",
                    Quantity = 4,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true,
                    MarketPrice = 0,
                },
            ],
        };
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(plan),
            new NoEvidenceService(),
            () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
        });

        Assert.False(quote.IsComplete);
        Assert.Equal("IncompletePriceEvidence", quote.AppraisalStatus);
        Assert.Equal("Low", quote.Confidence);
        Assert.Equal(0m, quote.EstimatedTotalCost);
        Assert.Equal("MissingEvidence", Assert.Single(quote.Materials).CostSource);
        Assert.Contains(quote.Warnings, warning => warning.Contains("missing price evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AcquisitionSource.MarketBuyNq, 25, 100, "MarketEvidence")]
    [InlineData(AcquisitionSource.VendorBuy, 15, 60, "VendorPrice")]
    public async Task SuccessfulMarketAndVendorQuotes_UseSelectedSourceEconomics(
        AcquisitionSource source,
        int unitCost,
        int totalCost,
        string costSource)
    {
        var material = new PlanNode
        {
            ItemId = 7017,
            Name = "Varnish",
            Quantity = 4,
            Source = source,
            CanBuyFromMarket = true,
            CanBuyFromVendor = source == AcquisitionSource.VendorBuy,
            MarketPrice = unitCost,
            VendorPrice = unitCost,
            VendorOptions = source == AcquisitionSource.VendorBuy
                ? [new VendorInfo { Name = "Material Supplier", Location = "Siren", Price = unitCost, Currency = "gil" }]
                : [],
        };
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [material] }),
            new NoEvidenceService(),
            () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
        });

        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Equal("Medium", quote.Confidence);
        Assert.Equal((decimal)unitCost, quote.EstimatedUnitCost);
        Assert.Equal((decimal)totalCost, quote.EstimatedTotalCost);
        var quotedMaterial = Assert.Single(quote.Materials);
        Assert.Equal((4m, 1m, (decimal)unitCost, (decimal)totalCost),
            (quotedMaterial.TotalQuantity, quotedMaterial.QuantityPerCraft, quotedMaterial.UnitCost, quotedMaterial.TotalCost));
        Assert.Equal(source.ToString(), quotedMaterial.AcquisitionSource);
        Assert.Equal(costSource, quotedMaterial.CostSource);
        Assert.Empty(quotedMaterial.Warnings);
    }

    [Fact]
    public async Task MixedSourceQuote_AggregatesMatchingDemandWithoutCollapsingSourceEconomics()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Contract Craft",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
        };
        var firstMarket = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            MarketPrice = 30,
            Parent = root,
        };
        var secondMarket = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            MarketPrice = 30,
            Parent = root,
        };
        var vendor = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 4,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            Parent = root,
            VendorOptions =
            [
                new VendorInfo { Name = "Material Supplier", Location = "Limsa Lominsa", Price = 10, Currency = "gil" },
            ],
        };
        root.Children = [firstMarket, secondMarket, vendor];
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            new NoEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 100,
            ItemName = "Contract Craft",
            Quantity = 2,
        });

        Assert.True(quote.IsComplete);
        Assert.Equal(190m, quote.EstimatedTotalCost);
        Assert.Equal(95m, quote.EstimatedUnitCost);
        Assert.Equal(2, quote.Materials.Count);
        var marketQuote = Assert.Single(quote.Materials, material => material.AcquisitionSource == "MarketBuyNq");
        Assert.Equal((5m, 2.5m, 30m, 150m),
            (marketQuote.TotalQuantity, marketQuote.QuantityPerCraft, marketQuote.UnitCost, marketQuote.TotalCost));
        var vendorQuote = Assert.Single(quote.Materials, material => material.AcquisitionSource == "VendorBuy");
        Assert.Equal((4m, 2m, 10m, 40m),
            (vendorQuote.TotalQuantity, vendorQuote.QuantityPerCraft, vendorQuote.UnitCost, vendorQuote.TotalCost));
    }

    [Fact]
    public void PartialObservationJson_RemainsIncompleteMarketEvidence()
    {
        const string json = """
            {
              "observationId": "observation-1",
              "requestId": "batch-1",
              "attemptId": "attempt-1",
              "sequence": 3,
              "lineId": "line-1",
              "itemId": 5064,
              "itemName": "Silver Ingot",
              "dataCenter": "Aether",
              "worldName": "Siren",
              "readState": "Partial",
              "reportedListingCount": 5,
              "readableListingCount": 1,
              "listingCapacity": 2,
              "isTruncated": true,
              "observedAtUtc": "2026-07-20T12:00:00Z",
              "listings": [{
                "listingId": "listing-1",
                "retainerId": "retainer-1",
                "retainerName": "Contract Seller",
                "quantity": 10,
                "unitPrice": 50,
                "isHq": false
              }]
            }
            """;

        var observation = JsonSerializer.Deserialize<WorkshopHostMarketObservation>(json, WireJson)!;
        var evidence = observation.ToMarketEvidenceSnapshot();

        Assert.Equal(MarketEvidenceCompleteness.Partial, evidence.Completeness);
        Assert.True(evidence.IsTruncated);
        Assert.Equal(5, evidence.ReportedListingCount);
        Assert.Equal(2, evidence.ListingCapacity);
        Assert.Equal("listing-1", Assert.Single(evidence.Listings).ListingId);
    }

    [Fact]
    public void CapabilityWire_RequiresAvailableStatusAndMatchingSchema()
    {
        const string json = """
            {
              "service": "Workshop Host",
              "schemaVersion": 1,
              "capabilities": [{
                "id": "acquisition-batches",
                "status": "available",
                "supportedSchemaVersions": [1],
                "requiredScopes": ["acquisition.write"]
              }, {
                "id": "recipe-graphs",
                "status": "unavailable",
                "supportedSchemaVersions": [1],
                "requiredScopes": []
              }]
            }
            """;

        var capabilities = JsonSerializer.Deserialize<WorkshopHostCapabilityResponse>(json, WireJson)!;

        Assert.True(capabilities.Supports("acquisition-batches", 1));
        Assert.False(capabilities.Supports("acquisition-batches", 2));
        Assert.False(capabilities.Supports("recipe-graphs", 1));
        Assert.False(capabilities.Supports("unknown-capability", 1));
    }

    private sealed class FixedPlanBuilder(CraftingPlan plan) : ICoreRecipePlanBuilder
    {
        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default) => Task.FromResult(plan);

        public Task FetchVendorPricesAsync(CraftingPlan value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoEvidenceService : ICraftAppraisalPriceEvidenceService
    {
        public Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
            CraftingPlan plan,
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CraftAppraisalPriceEvidenceResult.Empty);
    }
}
