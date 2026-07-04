using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkshopHostCraftAppraisalServiceTests
{
    [Fact]
    public async Task AppraiseAsync_BuildsPlanFromRequestScopeAndHqPolicy()
    {
        var builder = new CapturingPlanBuilder(CreatePricedPlan());
        var quotedAtUtc = new DateTimeOffset(2026, 7, 4, 14, 30, 0, TimeSpan.Zero);
        var service = new CraftAppraisalService(builder, () => quotedAtUtc);
        var request = CreateRequest(hqPolicy: "HqOnly");

        var quote = await service.AppraiseAsync(request);

        Assert.Equal([(2, "Fire Shard", 10, true)], builder.TargetItems);
        Assert.Equal("Aether", builder.DataCenter);
        Assert.Equal("Siren", builder.World);
        Assert.Equal(quotedAtUtc, quote.QuotedAtUtc);
    }

    [Fact]
    public async Task AppraiseAsync_MapsActiveProcurementMaterialsIntoQuote()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreatePricedPlan()),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.Equal(1, quote.SchemaVersion);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal("Fire Shard", quote.ItemName);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal(1u, quote.OutputQuantity);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Equal(800m, quote.EstimatedTotalCost);
        Assert.Equal("gil", quote.Currency);
        Assert.Equal("CraftArchitectLocal", quote.Source);
        Assert.Equal("Medium", quote.Confidence);
        Assert.Contains(quote.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));

        var material = Assert.Single(quote.Materials);
        Assert.Equal(3u, material.ItemId);
        Assert.Equal("Example Material", material.ItemName);
        Assert.Equal(2m, material.QuantityPerCraft);
        Assert.Equal(20m, material.TotalQuantity);
        Assert.Equal(40m, material.UnitCost);
        Assert.Equal(800m, material.TotalCost);
        Assert.Equal("PlanEvidence", material.CostSource);
        Assert.Empty(material.Warnings);
    }

    [Fact]
    public async Task AppraiseAsync_MissingPriceEvidenceProducesLowConfidenceWarning()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreateMissingEvidencePlan()),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.Equal("Low", quote.Confidence);
        Assert.Equal(0m, quote.EstimatedUnitCost);
        Assert.Equal(0m, quote.EstimatedTotalCost);
        Assert.Contains(quote.Warnings, warning => warning.Contains("missing price evidence", StringComparison.OrdinalIgnoreCase));
        var material = Assert.Single(quote.Materials);
        Assert.Equal("MissingEvidence", material.CostSource);
        Assert.Contains(material.Warnings, warning => warning.Contains("missing price evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppraiseAsync_ZeroQuantityThrows()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreatePricedPlan()),
            () => DateTimeOffset.UnixEpoch);
        var request = CreateRequest() with { Quantity = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AppraiseAsync(request));
    }

    private static CraftAppraisalRequest CreateRequest(string hqPolicy = "Either") => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        Scope = new CraftAppraisalScope
        {
            Region = "North America",
            DataCenter = "Aether",
            World = "Siren",
        },
        Options = new CraftAppraisalOptions
        {
            HqPolicy = hqPolicy,
            PricingMode = "CurrentMarketEvidence",
        },
    };

    private static CraftingPlan CreatePricedPlan()
    {
        var root = new PlanNode
        {
            ItemId = 2,
            Name = "Fire Shard",
            Quantity = 10,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1,
        };
        var material = new PlanNode
        {
            ItemId = 3,
            Name = "Example Material",
            Quantity = 20,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 40,
            Parent = root,
        };
        root.Children.Add(material);

        return new CraftingPlan
        {
            RootItems = [root],
            DataCenter = "Aether",
            World = "Siren",
        };
    }

    private static CraftingPlan CreateMissingEvidencePlan()
    {
        var root = CreatePricedPlan().RootItems[0];
        root.Children[0].MarketPrice = 0;
        return new CraftingPlan
        {
            RootItems = [root],
            DataCenter = "Aether",
            World = "Siren",
        };
    }

    private sealed class CapturingPlanBuilder : ICoreRecipePlanBuilder
    {
        private readonly CraftingPlan plan;

        public CapturingPlanBuilder(CraftingPlan plan)
        {
            this.plan = plan;
        }

        public List<(int itemId, string name, int quantity, bool isHqRequired)>? TargetItems { get; private set; }
        public string? DataCenter { get; private set; }
        public string? World { get; private set; }

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            TargetItems = targetItems;
            DataCenter = dataCenter;
            World = world;
            return Task.FromResult(plan);
        }

        public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
