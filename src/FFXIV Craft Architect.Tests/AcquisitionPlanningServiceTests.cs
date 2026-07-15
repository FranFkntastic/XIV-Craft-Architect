using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionPlanningServiceTests
{
    [Fact]
    public void GetMarketAnalysisCandidates_IncludesCraftableIntermediateAndLeafCandidates()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var intermediate = plan.RootItems[0].Children[0];
        intermediate.Source = AcquisitionSource.Craft;

        var candidates = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan);

        Assert.Contains(candidates, item => item.ItemId == 200 && item.TotalQuantity == 2);
        Assert.Contains(candidates, item => item.ItemId == 300 && item.TotalQuantity == 6);
    }
    [Fact]
    public void GetActiveProcurementItems_PrunesChildrenWhenParentIsBought()
    {
        var plan = CreatePlanWithBoughtIntermediate();

        var activeItems = AcquisitionPlanningService.GetActiveProcurementItems(plan);

        var item = Assert.Single(activeItems);
        Assert.Equal(200, item.ItemId);
        Assert.Equal(2, item.TotalQuantity);
    }
    [Fact]
    public void GetProcurementEvidenceSummary_CountsActiveAnalyzedMissingAndSuppressedCandidates()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Aether",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            },
            new()
            {
                ItemId = 300,
                Name = "Raw Material",
                QuantityNeeded = 6,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Primal",
                    TotalCost = 50,
                    TotalQuantityPurchased = 6
                }
            }
        };

        var summary = AcquisitionPlanningService.GetProcurementEvidenceSummary(plan, marketPlans);

        Assert.Equal(1, summary.ActiveProcurementItemCount);
        Assert.Equal(1, summary.ActiveItemsWithEvidence);
        Assert.Equal(0, summary.ActiveItemsMissingEvidence);
        Assert.Equal(1, summary.SuppressedMarketCandidateCount);
        Assert.True(summary.HasCompleteActiveEvidence);
    }
    [Fact]
    public void HasCompleteProcurementEvidence_CompleteSplitEvidence_ReturnsTrue()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedSplit =
                [
                    new SplitWorldPurchase
                    {
                        WorldName = "Siren",
                        QuantityToBuy = 1,
                        TotalCost = 40
                    },
                    new SplitWorldPurchase
                    {
                        WorldName = "Leviathan",
                        QuantityToBuy = 1,
                        TotalCost = 50
                    }
                ]
            }
        };

        var canReuse = AcquisitionPlanningService.HasCompleteProcurementEvidence(
            plan,
            marketPlans);

        Assert.True(canReuse);
    }

    [Fact]
    public void HasCompleteProcurementEvidence_UnderfilledRecommendedSplit_ReturnsFalse()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedSplit =
                [
                    new SplitWorldPurchase
                    {
                        WorldName = "Siren",
                        QuantityToBuy = 1,
                        TotalCost = 40
                    }
                ]
            }
        };

        var canReuse = AcquisitionPlanningService.HasCompleteProcurementEvidence(
            plan,
            marketPlans);

        Assert.False(canReuse);
    }


    [Fact]
    public void CalculateCraftCost_UsesMarketEvidenceForBoughtChildren()
    {
        var ingot = new PlanNode
        {
            ItemId = 1,
            Name = "Silver Ingot",
            Quantity = 120,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var ore = new PlanNode
        {
            ItemId = 2,
            Name = "Silver Ore",
            Quantity = 360,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 324,
            Parent = ingot
        };
        var shard = new PlanNode
        {
            ItemId = 3,
            Name = "Ice Shard",
            Quantity = 240,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 45,
            Parent = ingot
        };
        ingot.Children.Add(ore);
        ingot.Children.Add(shard);

        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 2,
                Name = "Silver Ore",
                QuantityNeeded = 360,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Rafflesia",
                    TotalCost = 45_734,
                    TotalQuantityPurchased = 370
                }
            },
            new()
            {
                ItemId = 3,
                Name = "Ice Shard",
                QuantityNeeded = 240,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Rafflesia",
                    TotalCost = 10_800,
                    TotalQuantityPurchased = 240
                }
            }
        };

        var cost = AcquisitionPlanningService.CalculateCraftCost(ingot, marketPlans);

        Assert.Equal(56_534, cost);
    }
    [Fact]
    public void CalculateCraftCost_DividesByRecipeYield()
    {
        var cloth = new PlanNode
        {
            ItemId = 10,
            Name = "Linen Cloth",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 2
        };
        var flax = new PlanNode
        {
            ItemId = 11,
            Name = "Moko Grass",
            Quantity = 4,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = cloth
        };
        cloth.Children.Add(flax);

        var cost = AcquisitionPlanningService.CalculateCraftCost(cloth, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(200, cost);
    }

    [Fact]
    public void GetAvailableSources_UsesSingleAcquisitionAvailabilityRule()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Flexible Item",
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            CanBeHq = true
        };
        node.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Ingredient",
            Parent = node
        });

        var sources = AcquisitionPlanningService.GetAvailableSources(node);

        Assert.Equal(
            [
                AcquisitionSource.Craft,
                AcquisitionSource.MarketBuyNq,
                AcquisitionSource.MarketBuyHq,
                AcquisitionSource.VendorBuy
            ],
            sources);
    }

    [Fact]
    public void EnsureValidAcquisitionSource_InvalidMarketBuyFallsBackToCraftBeforeUnknown()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Craftable Only",
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = false
        };
        node.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Ingredient",
            Parent = node
        });

        AcquisitionPlanningService.EnsureValidAcquisitionSource(node);

        Assert.Equal(AcquisitionSource.Craft, node.Source);
        Assert.NotEqual(PriceSource.Untradeable, node.PriceSource);
    }



    [Fact]
    public void ApplyCheapestAcquisitionDefaults_RootCraftBeatsExpensiveMarket_SelectsCraft()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Cheap Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 50,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.Craft, root.Source);
    }

    [Fact]
    public void ApplyCheapestAcquisitionDefaults_UsesMarketEvidenceForBuyCost()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Buy",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Expensive Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 500,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 100,
                Name = "Root Buy",
                QuantityNeeded = 1,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 100,
                    TotalQuantityPurchased = 1
                }
            }
        };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, marketPlans);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, root.Source);
    }

    [Fact]
    public void ApplyCheapestAcquisitionDefaults_UserSelectedVendorIsPreservedWhenCraftIsCheaper()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "User Vendor Choice",
            Quantity = 1,
            Source = AcquisitionSource.VendorBuy,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            CanBuyFromVendor = true,
            VendorPrice = 1_000,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Cheap Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 50,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(0, changed);
        Assert.Equal(AcquisitionSource.VendorBuy, root.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, root.SourceReason);
    }

    [Fact]
    public void EnsureAutomaticMarketSourcesAreActionable_ReplacesUnfillableHqWithSupportedNq()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Large market purchase",
            Quantity = 4_995,
            Source = AcquisitionSource.MarketBuyHq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBeHq = true
        };
        var evidence = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = root.Name,
            QuantityNeeded = root.Quantity,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Adamantoise",
                    TotalQuantityPurchased = 5_019,
                    TotalCost = 4_820_411,
                    HasSufficientStock = true,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 5_019,
                            NeededFromStack = 4_995,
                            PricePerUnit = 960,
                            IsHq = false
                        }
                    ]
                }
            ]
        };
        evidence.CoverageSet = MarketCoverageBuilder.Build(evidence);

        var changed = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            new CraftingPlan { RootItems = [root] },
            [evidence]);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, root.Source);
        Assert.Equal(AcquisitionSourceReason.SystemDefault, root.SourceReason);
    }

    [Fact]
    public void EnsureAutomaticMarketSourcesAreActionable_RepairsLegacyUserReasonWithoutHqConstraint()
    {
        var root = CreateUnfillableHqNode(AcquisitionSourceReason.UserSelected, mustBeHq: false);
        var evidence = CreateNqOnlyEvidence(root);

        var changed = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            new CraftingPlan { RootItems = [root] },
            [evidence]);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, root.Source);
        Assert.Equal(AcquisitionSourceReason.SystemDefault, root.SourceReason);
        Assert.False(root.MustBeHq);
    }

    [Fact]
    public void EnsureAutomaticMarketSourcesAreActionable_PreservesExplicitHqConstraint()
    {
        var root = CreateUnfillableHqNode(AcquisitionSourceReason.UserSelected, mustBeHq: true);
        var evidence = CreateNqOnlyEvidence(root);

        var changed = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            new CraftingPlan { RootItems = [root] },
            [evidence]);

        Assert.Equal(0, changed);
        Assert.Equal(AcquisitionSource.MarketBuyHq, root.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, root.SourceReason);
        Assert.True(root.MustBeHq);
    }

    [Fact]
    public void EnsureAutomaticMarketSourcesAreActionable_UsesAggregatedDemandInsteadOfTreeNodeQuantity()
    {
        var root = CreateUnfillableHqNode(AcquisitionSourceReason.SystemDefault, mustBeHq: false);
        root.Quantity = 1;
        var evidence = CreateNqOnlyEvidence(root);
        evidence.QuantityNeeded = 4_995;
        evidence.WorldOptions[0].Listings.Add(new ShoppingListingEntry
        {
            Quantity = 1,
            NeededFromStack = 1,
            PricePerUnit = 100,
            IsHq = true
        });
        evidence.CoverageSet = MarketCoverageBuilder.Build(evidence);

        var changed = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            new CraftingPlan { RootItems = [root] },
            [evidence]);

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, root.Source);
    }

    private static PlanNode CreateUnfillableHqNode(
        AcquisitionSourceReason sourceReason,
        bool mustBeHq)
    {
        return new PlanNode
        {
            ItemId = 100,
            Name = "Large market purchase",
            Quantity = 4_995,
            Source = AcquisitionSource.MarketBuyHq,
            SourceReason = sourceReason,
            MustBeHq = mustBeHq,
            CanBuyFromMarket = true,
            CanBeHq = true
        };
    }

    private static DetailedShoppingPlan CreateNqOnlyEvidence(PlanNode node)
    {
        var evidence = new DetailedShoppingPlan
        {
            ItemId = node.ItemId,
            Name = node.Name,
            QuantityNeeded = node.Quantity,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Adamantoise",
                    TotalQuantityPurchased = 5_019,
                    TotalCost = 4_820_411,
                    HasSufficientStock = true,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 5_019,
                            NeededFromStack = 4_995,
                            PricePerUnit = 960,
                            IsHq = false
                        }
                    ]
                }
            ]
        };
        evidence.CoverageSet = MarketCoverageBuilder.Build(evidence);
        return evidence;
    }



    [Fact]
    public void ReconcileAcquisitionDecisions_InvalidSourceCoercionCountsAsChange()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Invalid Selection",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            CanBuyFromMarket = false,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Child",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.Craft, root.Source);
        Assert.Equal(AcquisitionSourceReason.RequiredByAvailability, root.SourceReason);
    }


    [Fact]
    public void TryGetAcquisitionCost_MarketBuyHq_WithoutHqEvidenceDoesNotUsePlannerPriceFallback()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "HQ Buy",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyHq,
            MustBeHq = true,
            CanBeHq = true,
            CanBuyFromMarket = true,
            HqMarketPrice = 10_000
        };
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 100,
                Name = "HQ Buy",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 500,
                    TotalQuantityPurchased = 2,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 2,
                            PricePerUnit = 250,
                            IsHq = false
                        }
                    ]
                }
            }
        };

        var hasCost = AcquisitionPlanningService.TryGetAcquisitionCost(
            node,
            AcquisitionSource.MarketBuyHq,
            marketPlans,
            out var cost);

        Assert.False(hasCost);
        Assert.Equal(0, cost);
    }


    [Fact]
    public void TryGetSelectedAcquisitionCost_WithCostContext_ReusesMemoizedCraftCost()
    {
        var root = CreateTwoLevelCraftTree();
        var context = AcquisitionPlanningService.CreateCostContext(Array.Empty<DetailedShoppingPlan>());

        Assert.True(AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], context, out var firstCost));
        var cachedEntriesAfterFirstCall = context.CachedCostEntryCount;
        Assert.True(AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], context, out var secondCost));

        Assert.Equal(firstCost, secondCost);
        Assert.Equal(cachedEntriesAfterFirstCall, context.CachedCostEntryCount);
        Assert.True(cachedEntriesAfterFirstCall > 1);
    }
    [Fact]
    public void DeserializePlan_CraftNodeKeepsSourceAfterChildrenAreLinked()
    {
        var service = new RecipeCalculationService(
            new GarlandService(new HttpClient()),
            new StubVendorCacheService());
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Craft Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Yield = 1
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Child",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root,
            MarketPrice = 100
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };

        var json = service.SerializePlan(plan);
        var deserialized = service.DeserializePlan(json);

        var restoredRoot = Assert.Single(deserialized!.RootItems);
        Assert.Equal(AcquisitionSource.Craft, restoredRoot.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, restoredRoot.SourceReason);
        Assert.Single(restoredRoot.Children);
    }




    private static CraftingPlan CreatePlanWithBoughtIntermediate()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = false
        };

        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            Parent = root
        };

        var raw = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Material",
            Quantity = 6,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = false,
            CanBuyFromMarket = true,
            Parent = intermediate
        };

        intermediate.Children.Add(raw);
        root.Children.Add(intermediate);

        return new CraftingPlan
        {
            RootItems = new List<PlanNode> { root }
        };
    }

    private static PlanNode CreateTwoLevelCraftTree()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1,
            Parent = root
        };
        var raw = new PlanNode
        {
            ItemId = 300,
            Name = "Raw",
            Quantity = 5,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            VendorPrice = 4,
            Parent = intermediate
        };

        intermediate.Children.Add(raw);
        root.Children.Add(intermediate);
        return root;
    }

    private static DetailedShoppingPlan CreateMarketPlan(
        int itemId,
        string name,
        string dataCenter,
        string worldName,
        long totalCost = 100)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = dataCenter,
                WorldName = worldName,
                TotalCost = totalCost,
                TotalQuantityPurchased = 2
            },
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = dataCenter,
                    WorldName = worldName,
                    TotalCost = totalCost,
                    TotalQuantityPurchased = 2
                }
            ]
        };
    }

    private static MarketCoverageOption CreateCoverageOption(
        string worldName,
        decimal exactNeededCost,
        decimal cashOutCost,
        MarketCoverageQualityPolicy qualityPolicy = MarketCoverageQualityPolicy.NqOrHq)
    {
        return new MarketCoverageOption(
            CandidateId: $"100-1-singleworld-{qualityPolicy.ToString().ToLowerInvariant()}-{worldName.ToLowerInvariant()}",
            Tier: MarketCoverageTier.SingleWorld,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: qualityPolicy,
            QuantityCovered: 1,
            QuantityToPurchase: 1,
            ExcessQuantity: 0,
            ExactNeededCost: exactNeededCost,
            CashOutCost: cashOutCost,
            AverageUnitCost: exactNeededCost,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: worldName,
                    QuantityCovered: 1,
                    QuantityToPurchase: 1,
                    ExactNeededCost: exactNeededCost,
                    CashOutCost: cashOutCost)
            ],
            Listings:
            [
                new MarketCoverageListing(
                    DataCenter: "Aether",
                    WorldName: worldName,
                    QuantityAvailable: 1,
                    QuantityUsed: 1,
                    QuantityPurchased: 1,
                    PricePerUnit: exactNeededCost,
                    IsHq: qualityPolicy == MarketCoverageQualityPolicy.HqOnly)
            ],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: 1,
                LargestContribution: 1,
                ExcessQuantity: 0),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: true,
            DegradedReason: null);
    }

    private sealed class StubVendorCacheService : IVendorCacheService
    {
        public int Count => 0;

        public void Clear()
        {
        }

        public VendorCacheEntry? Get(int itemId)
        {
            return null;
        }

        public Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default)
        {
            return Task.FromResult<VendorCacheEntry?>(null);
        }

        public Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
            IEnumerable<int> itemIds,
            CancellationToken ct = default)
        {
            return Task.FromResult(new Dictionary<int, VendorCacheEntry>());
        }

        public Task LoadAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void Set(int itemId, VendorCacheEntry entry)
        {
        }
    }
}
