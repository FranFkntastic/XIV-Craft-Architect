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
        var service = new CraftAppraisalService(builder, new NoOpPriceEvidenceService(), () => quotedAtUtc);
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
            new NoOpPriceEvidenceService(),
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
        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Contains(quote.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));

        var material = Assert.Single(quote.Materials);
        Assert.Equal(3u, material.ItemId);
        Assert.Equal("Example Material", material.ItemName);
        Assert.Equal(2m, material.QuantityPerCraft);
        Assert.Equal(20m, material.TotalQuantity);
        Assert.Equal(40m, material.UnitCost);
        Assert.Equal(800m, material.TotalCost);
        Assert.Equal("MarketBuyNq", material.AcquisitionSource);
        Assert.Equal("MarketEvidence", material.CostSource);
        Assert.Empty(material.Warnings);
    }

    [Fact]
    public async Task AppraiseAsync_MissingPriceEvidenceProducesLowConfidenceWarning()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreateMissingEvidencePlan()),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.Equal("Low", quote.Confidence);
        Assert.False(quote.IsComplete);
        Assert.Equal("IncompletePriceEvidence", quote.AppraisalStatus);
        Assert.Equal(0m, quote.EstimatedUnitCost);
        Assert.Equal(0m, quote.EstimatedTotalCost);
        Assert.Contains(quote.Warnings, warning => warning.Contains("missing price evidence", StringComparison.OrdinalIgnoreCase));
        var material = Assert.Single(quote.Materials);
        Assert.Equal("MissingEvidence", material.CostSource);
        Assert.Contains(material.Warnings, warning => warning.Contains("missing market price evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppraiseAsync_ZeroQuantityThrows()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreatePricedPlan()),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);
        var request = CreateRequest() with { Quantity = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AppraiseAsync(request));
    }

    [Fact]
    public async Task AppraiseAsync_UsesVendorSelectedSourcePrice()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreateVendorPricedPlan()),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Equal(800m, quote.EstimatedTotalCost);
        var material = Assert.Single(quote.Materials);
        Assert.Equal("VendorBuy", material.AcquisitionSource);
        Assert.Equal("VendorPrice", material.CostSource);
        Assert.Equal(40m, material.UnitCost);
    }

    [Fact]
    public async Task AppraiseAsync_AppliesPriceEvidenceBeforeMappingMaterials()
    {
        var plan = CreateMissingEvidencePlan();
        var evidence = new MutatingPriceEvidenceService(targetItemId: 3, unitPrice: 25);
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(plan),
            evidence,
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.True(evidence.WasCalled);
        Assert.True(quote.IsComplete);
        Assert.Equal(25m, Assert.Single(quote.Materials).UnitCost);
    }

    [Fact]
    public async Task AppraiseAsync_DirectMarketItemWithoutEvidenceIsExplicitlyIncomplete()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreateDirectMarketItemPlan()),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.False(quote.IsComplete);
        Assert.Equal("IncompletePriceEvidence", quote.AppraisalStatus);
        Assert.Equal("Low", quote.Confidence);
        var material = Assert.Single(quote.Materials);
        Assert.Equal("MarketBuyNq", material.AcquisitionSource);
        Assert.Equal("MissingEvidence", material.CostSource);
        Assert.Contains(
            "missing market price evidence",
            Assert.Single(material.Warnings),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppraiseAsync_DirectMarketItemWithEvidenceReturnsCompleteDirectAcquisitionQuote()
    {
        var plan = CreateDirectMarketItemPlan();
        plan.RootItems[0].MarketPrice = 8;
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(plan),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Equal(8m, quote.EstimatedUnitCost);
        Assert.Equal(80m, quote.EstimatedTotalCost);
        var material = Assert.Single(quote.Materials);
        Assert.Equal("MarketEvidence", material.CostSource);
    }

    [Fact]
    public async Task AppraiseAsync_DoesNotCollapseSameItemWithDifferentSelectedSources()
    {
        var service = new CraftAppraisalService(
            new CapturingPlanBuilder(CreateMixedSourceSameItemPlan()),
            new NoOpPriceEvidenceService(),
            () => DateTimeOffset.UnixEpoch);

        var quote = await service.AppraiseAsync(CreateRequest());

        Assert.True(quote.IsComplete);
        Assert.Equal(2, quote.Materials.Count(material => material.ItemId == 3));
        Assert.Contains(quote.Materials, material =>
            material.ItemId == 3 &&
            material.AcquisitionSource == "VendorBuy" &&
            material.UnitCost == 40m);
        Assert.Contains(quote.Materials, material =>
            material.ItemId == 3 &&
            material.AcquisitionSource == "MarketBuyNq" &&
            material.UnitCost == 60m);
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

    private static CraftingPlan CreateVendorPricedPlan()
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
            Name = "Vendor Material",
            Quantity = 20,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            CanBuyFromMarket = true,
            MarketPrice = 0,
            VendorPrice = 40,
            Parent = root,
            VendorOptions =
            [
                new VendorInfo
                {
                    Name = "Material Supplier",
                    Location = "Limsa Lominsa",
                    Price = 40,
                    Currency = "gil"
                }
            ],
        };
        root.Children.Add(material);

        return new CraftingPlan
        {
            RootItems = [root],
            DataCenter = "Aether",
            World = "Siren",
        };
    }

    private static CraftingPlan CreateDirectMarketItemPlan()
    {
        var root = new PlanNode
        {
            ItemId = 2,
            Name = "Fire Shard",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            CanCraft = false,
            Yield = 1,
        };

        return new CraftingPlan
        {
            RootItems = [root],
            DataCenter = "Aether",
            World = "Siren",
        };
    }

    private static CraftingPlan CreateMixedSourceSameItemPlan()
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
        var vendorMaterial = new PlanNode
        {
            ItemId = 3,
            Name = "Shared Material",
            Quantity = 2,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            VendorPrice = 40,
            Parent = root,
            VendorOptions =
            [
                new VendorInfo
                {
                    Name = "Material Supplier",
                    Location = "Limsa Lominsa",
                    Price = 40,
                    Currency = "gil"
                }
            ],
        };
        var marketMaterial = new PlanNode
        {
            ItemId = 3,
            Name = "Shared Material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 60,
            Parent = root,
        };
        root.Children.Add(vendorMaterial);
        root.Children.Add(marketMaterial);

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

    private sealed class NoOpPriceEvidenceService : ICraftAppraisalPriceEvidenceService
    {
        public Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
            CraftingPlan plan,
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CraftAppraisalPriceEvidenceResult.Empty);
        }
    }

    private sealed class MutatingPriceEvidenceService(
        int targetItemId,
        decimal unitPrice) : ICraftAppraisalPriceEvidenceService
    {
        public bool WasCalled { get; private set; }

        public Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
            CraftingPlan plan,
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            foreach (var root in plan.RootItems)
            {
                Apply(root);
            }

            return Task.FromResult(new CraftAppraisalPriceEvidenceResult(1, 1, 0, []));
        }

        private void Apply(PlanNode node)
        {
            if (node.ItemId == targetItemId)
            {
                node.MarketPrice = unitPrice;
            }

            foreach (var child in node.Children)
            {
                Apply(child);
            }
        }
    }
}
