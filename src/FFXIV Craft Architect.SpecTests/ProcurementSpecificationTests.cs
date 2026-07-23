using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class ProcurementSpecificationTests
{
    [Fact]
    public void CoverageCashOutChargesWholeListingStack()
    {
        var plan = SpecificationFixtures.Evidence(
            100,
            "Whole stack",
            3,
            SpecificationFixtures.World("Aether", "Siren", 10, 7));

        var option = Assert.Single(MarketCoverageBuilder.Build(plan).AllCandidates,
            candidate => candidate.Tier == MarketCoverageTier.SingleWorld &&
                candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);

        Assert.Equal(21m, option.ExactNeededCost);
        Assert.Equal(70m, option.CashOutCost);
        Assert.Equal(7, option.ExcessQuantity);
    }

    [Fact]
    public void PartialStockCannotProduceCoverageCandidate()
    {
        var plan = SpecificationFixtures.Evidence(
            101,
            "Short stock",
            10,
            SpecificationFixtures.World("Aether", "Siren", 6, 20));

        Assert.Empty(MarketCoverageBuilder.Build(plan).AllCandidates);
    }

    [Fact]
    public void HqOnlyCoverageRejectsNqStock()
    {
        var plan = SpecificationFixtures.Evidence(
            102,
            "Quality gate",
            5,
            SpecificationFixtures.World("Aether", "Siren", (5, 10L, false), (2, 20L, true)));
        var candidates = MarketCoverageBuilder.Build(plan).AllCandidates;

        Assert.Contains(candidates, candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);
        Assert.DoesNotContain(candidates, candidate => candidate.QualityPolicy == MarketCoverageQualityPolicy.HqOnly);
    }

    [Fact]
    public void NqCoverageMayConsumeHqListings()
    {
        var plan = SpecificationFixtures.Evidence(
            103,
            "HQ usable as NQ",
            5,
            SpecificationFixtures.World("Aether", "Siren", 5, 30, hq: true));

        var option = Assert.Single(MarketCoverageBuilder.Build(plan).AllCandidates,
            candidate => candidate.Tier == MarketCoverageTier.SingleWorld &&
                candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);

        Assert.Equal(5, option.QuantityCovered);
        Assert.All(option.Listings, listing => Assert.True(listing.IsHq));
    }

    [Fact]
    public async Task SelectedVendorPurchaseProjectsTheAcquisitionDecisionIntoTheRoute()
    {
        var node = new PlanNode
        {
            ItemId = 104,
            Name = "Vendor material",
            Quantity = 5,
            Source = AcquisitionSource.VendorBuy,
            SelectedVendorIndex = 1,
            VendorOptions =
            [
                new VendorInfo { Name = "Cheap", Location = "Gridania", Price = 12, Currency = "gil" },
                new VendorInfo { Name = "Chosen", Location = "Limsa", Price = 18, Currency = "gil" }
            ]
        };
        var shopping = SpecificationFixtures.Evidence(
            104,
            node.Name,
            5,
            SpecificationFixtures.World("Aether", "Siren", 5, 25),
            SpecificationFixtures.World(
                MarketShoppingConstants.VendorWorldName,
                MarketShoppingConstants.VendorWorldName,
                5,
                12));

        var service = new MarketShoppingService(null!);
        service.ApplySelectedVendorPurchases(
            new CraftingPlan { RootItems = [node] },
            [shopping]);

        Assert.Equal(MarketShoppingConstants.VendorWorldName, shopping.RecommendedWorld?.WorldName);
        Assert.Equal(90, shopping.RecommendedWorld?.TotalCost);
        Assert.Equal("Chosen (Limsa)", shopping.RecommendedWorld?.VendorName);
        var marketWorld = Assert.Single(shopping.WorldOptions);
        Assert.Equal("Siren", marketWorld.WorldName);

        var route = await service.OptimizeProcurementRouteWithDecisionAsync(
            [shopping],
            SpecificationFixtures.Config(tolerance: 0));
        Assert.True(route.IsComplete);
        Assert.Equal(90, route.Decision?.SelectedGilCost);
        Assert.Equal(90, route.Decision?.FixedAcquisitionGilCost);
        Assert.All(route.Decision?.ToleranceSelections ?? [], selection =>
            Assert.Equal(90, selection.GilCost));
    }

    [Fact]
    public async Task ProcurementRoutesTheAcquisitionDecisionWithoutRejudgingEvidenceAge()
    {
        var world = SpecificationFixtures.World("Aether", "Siren", 5, 25);
        world.MarketDataQualityBucket = MarketDataQualityBucket.VeryOld;
        var shopping = SpecificationFixtures.Evidence(105, "Accepted material", 5, world);

        var route = await new MarketShoppingService(null!)
            .OptimizeProcurementRouteWithDecisionAsync(
                [shopping],
                SpecificationFixtures.Config(tolerance: 0));

        Assert.True(route.IsComplete);
        Assert.Equal("Siren", Assert.Single(route.ShoppingPlans).RecommendedWorld?.WorldName);
    }

    [Fact]
    public void VendorAvailabilityDoesNotMasqueradeAsVendorSelection()
    {
        var node = new PlanNode
        {
            ItemId = 105,
            Name = "Market material",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorOptions =
            [
                new VendorInfo { Name = "Supplier", Location = "Limsa", Price = 12, Currency = "gil" }
            ]
        };
        var shopping = SpecificationFixtures.Evidence(
            node.ItemId,
            node.Name,
            node.Quantity,
            SpecificationFixtures.World("Aether", "Siren", 5, 25));

        new MarketShoppingService(null!).ApplySelectedVendorPurchases(
            new CraftingPlan { RootItems = [node] },
            [shopping]);

        Assert.NotEqual(MarketShoppingConstants.VendorWorldName, shopping.RecommendedWorld?.WorldName);
        Assert.Equal("Siren", Assert.Single(shopping.WorldOptions).WorldName);
        Assert.Empty(shopping.Vendors);
    }

    [Fact]
    public void AcquisitionEvaluationSelectsCheaperVendorFromMarketEvidence()
    {
        var node = new PlanNode
        {
            ItemId = 106,
            Name = "Compared material",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 12
        };
        var shopping = SpecificationFixtures.Evidence(
            node.ItemId,
            node.Name,
            node.Quantity,
            SpecificationFixtures.World("Aether", "Siren", 5, 25));

        var changed = AcquisitionPlanningService.ReconcileAcquisitionDecisions(
            new CraftingPlan { RootItems = [node] },
            [shopping]);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
    }

    [Fact]
    public void AcquisitionEvaluationPreservesUserSelectedMarketSource()
    {
        var node = new PlanNode
        {
            ItemId = 107,
            Name = "Pinned material",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 12
        };
        var shopping = SpecificationFixtures.Evidence(
            node.ItemId,
            node.Name,
            node.Quantity,
            SpecificationFixtures.World("Aether", "Siren", 5, 25));

        var changed = AcquisitionPlanningService.ReconcileAcquisitionDecisions(
            new CraftingPlan { RootItems = [node] },
            [shopping]);

        Assert.Equal(0, changed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, node.Source);
    }

    [Fact]
    public void AcquisitionEvaluationFallsBackToCraftWhenAutomaticMarketChoiceHasNoSupportedCoverage()
    {
        var node = new PlanNode
        {
            ItemId = 108,
            Name = "HQ intermediate",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyHq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBeHq = true,
            MustBeHq = true,
            CanCraft = true,
            Children =
            [
                new PlanNode
                {
                    ItemId = 109,
                    Name = "Unpriced ingredient",
                    Quantity = 10,
                    Source = AcquisitionSource.MarketBuyNq,
                    SourceReason = AcquisitionSourceReason.SystemDefault,
                    CanBuyFromMarket = true
                }
            ]
        };
        var shopping = SpecificationFixtures.Evidence(
            node.ItemId,
            node.Name,
            node.Quantity,
            SpecificationFixtures.World("Aether", "Siren", 5, 25));
        shopping.HQAveragePrice = 100;

        var changed = AcquisitionPlanningService.ReconcileAcquisitionDecisions(
            new CraftingPlan { RootItems = [node] },
            [shopping]);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.Craft, node.Source);
    }

    [Fact]
    public void CompactSplitCalculatesExactNeedAndWholeStackCashOutSeparately()
    {
        var plan = SpecificationFixtures.Evidence(
            105,
            "Split material",
            100,
            SpecificationFixtures.World("Aether", "Siren", 60, 10),
            SpecificationFixtures.World("Aether", "Faerie", 50, 20));

        var split = Assert.Single(MarketCoverageBuilder.Build(plan).AllCandidates,
            candidate => candidate.Tier == MarketCoverageTier.CompactSplit &&
                candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);

        Assert.Equal(1_400m, split.ExactNeededCost);
        Assert.Equal(1_600m, split.CashOutCost);
        Assert.Equal(110, split.QuantityToPurchase);
    }

    [Fact]
    public async Task ListingAboveConfiguredModeMultiplierIsExcluded()
    {
        var itemId = 106;
        var cache = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = "Aether",
            DCAveragePrice = 100,
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = "Siren",
                    Listings =
                    [
                        new CachedListing { Quantity = 10, PricePerUnit = 100, RetainerName = "Normal" },
                        new CachedListing { Quantity = 10, PricePerUnit = 400, RetainerName = "Gouge" }
                    ]
                }
            ]
        };
        var request = new MarketAnalysisRequest
        {
            Items = [new MaterialAggregate { ItemId = itemId, Name = "Filtered", TotalQuantity = 10 }],
            Evidence = SpecificationFixtures.EvidenceSet(itemId, cache),
            AnalysisConfig = new MarketAnalysisConfig { MaxPriceMultiplier = 2.5m }
        };

        var result = Assert.Single(await new MarketShoppingService(null!).CalculateDetailedShoppingPlansAsync(request));
        var world = Assert.Single(result.WorldOptions);

        Assert.Equal(1_000, world.TotalCost);
        Assert.Equal(400, Assert.Single(world.ExcludedListings).PricePerUnit);
    }

    [Fact]
    public void DuplicateActiveDemandAggregatesByItemIdentity()
    {
        var first = SpecificationFixtures.MarketNode(107, "Shared", quantity: 2, nodeId: "shared-a");
        var second = SpecificationFixtures.MarketNode(107, "Shared", quantity: 3, nodeId: "shared-b");
        var rootA = new PlanNode
        {
            ItemId = 201,
            Name = "Root A",
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            Children = [first]
        };
        var rootB = new PlanNode
        {
            ItemId = 202,
            Name = "Root B",
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            Children = [second]
        };
        first.Parent = rootA;
        second.Parent = rootB;

        var aggregate = Assert.Single(AcquisitionPlanningService.GetActiveProcurementItems(
            new CraftingPlan { RootItems = [rootA, rootB] }));

        Assert.Equal(107, aggregate.ItemId);
        Assert.Equal(5, aggregate.TotalQuantity);
    }

    [Fact]
    public void SameWorldNameAcrossDataCentersRemainsTwoCoverageStops()
    {
        var plan = SpecificationFixtures.Evidence(
            108,
            "Composite identity",
            100,
            SpecificationFixtures.World("Aether", "Siren", 60, 10),
            SpecificationFixtures.World("Primal", "Siren", 50, 20));

        var split = Assert.Single(MarketCoverageBuilder.Build(plan).AllCandidates,
            candidate => candidate.Tier == MarketCoverageTier.CompactSplit &&
                candidate.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);

        Assert.Equal(2, split.Worlds.Count);
        Assert.Equal(2, split.Friction.DataCenterCount);
    }

    [Fact]
    public async Task StructuredBlacklistExcludesOnlyMatchingCompositeWorld()
    {
        var itemId = 109;
        var aether = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = "Aether",
            DCAveragePrice = 10,
            Worlds = [SpecificationFixtures.CachedWorld("Siren", 1, 10)]
        };
        var primal = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = "Primal",
            DCAveragePrice = 20,
            Worlds = [SpecificationFixtures.CachedWorld("Siren", 1, 20)]
        };
        var request = new MarketAnalysisRequest
        {
            Items = [new MaterialAggregate { ItemId = itemId, Name = "Blacklist", TotalQuantity = 1 }],
            Evidence = SpecificationFixtures.EvidenceSet(itemId, aether, primal),
            BlacklistedMarketWorlds = [new("Aether", "Siren")]
        };

        var result = Assert.Single(await new MarketShoppingService(null!).CalculateDetailedShoppingPlansAsync(request));
        var remaining = Assert.Single(result.WorldOptions);

        Assert.Equal("Primal", remaining.DataCenter);
        Assert.Equal("Siren", remaining.WorldName);
    }
}
