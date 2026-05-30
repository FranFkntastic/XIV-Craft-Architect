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
    public void GetMarketAnalysisCandidates_IncludesChildrenAfterIntermediateIsSetToBuyMode()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var intermediate = plan.RootItems[0].Children[0];

        intermediate.SetBuyMode(true);

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
    public void FilterShoppingPlansForActiveProcurement_RemovesChildPlansWhenParentIsBought()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new() { ItemId = 200, Name = "Intermediate", QuantityNeeded = 2 },
            new() { ItemId = 300, Name = "Raw Material", QuantityNeeded = 6 }
        };

        var procurementPlans = AcquisitionPlanningService.FilterShoppingPlansForActiveProcurement(plan, marketPlans);

        var planResult = Assert.Single(procurementPlans);
        Assert.Equal(200, planResult.ItemId);
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
    public void GetProcurementEvidenceSummary_TreatsErroredPlanAsMissingEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                Error = "No market data"
            }
        };

        var summary = AcquisitionPlanningService.GetProcurementEvidenceSummary(plan, marketPlans);

        Assert.Equal(0, summary.ActiveItemsWithEvidence);
        Assert.Equal(1, summary.ActiveItemsMissingEvidence);
        Assert.False(summary.HasCompleteActiveEvidence);
    }

    [Fact]
    public void HasCompleteProcurementEvidence_CompleteSingleWorldEvidence_ReturnsTrue()
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
                    WorldName = "Siren",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var canReuse = AcquisitionPlanningService.HasCompleteProcurementEvidence(
            plan,
            marketPlans);

        Assert.True(canReuse);
    }

    [Fact]
    public void HasCompleteProcurementEvidence_CompleteSingleWorldEvidenceWithSplitAllowed_ReturnsTrue()
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
                    WorldName = "Siren",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var canReuse = AcquisitionPlanningService.HasCompleteProcurementEvidence(
            plan,
            marketPlans);

        Assert.True(canReuse);
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
    public void SelectActiveProcurementEvidence_UnderfilledRecommendedSplitIsMissing()
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
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        QuantityToBuy = 1,
                        TotalCost = 40
                    }
                ]
            }
        };

        var selection = AcquisitionPlanningService.SelectActiveProcurementEvidence(
            plan,
            marketPlans,
            MarketFetchScope.SelectedDataCenter,
            "Aether");

        Assert.Empty(selection.ReusablePlans);
        var missingItem = Assert.Single(selection.MissingItems);
        Assert.Equal(200, missingItem.ItemId);
    }

    [Fact]
    public void GetActiveProcurementItemsMissingEvidence_ReturnsOnlyActiveItemsWithoutUsableEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 300,
                Name = "Raw Material",
                QuantityNeeded = 6,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 60,
                    TotalQuantityPurchased = 6
                }
            }
        };

        var missingItems = AcquisitionPlanningService.GetActiveProcurementItemsMissingEvidence(
            plan,
            marketPlans);

        var missingItem = Assert.Single(missingItems);
        Assert.Equal(200, missingItem.ItemId);
        Assert.Equal("Intermediate", missingItem.Name);
        Assert.Equal(2, missingItem.TotalQuantity);
    }

    [Fact]
    public void GetActiveProcurementItemsMissingEvidence_TreatsErroredActivePlanAsMissing()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                Error = "No market data in cache"
            }
        };

        var missingItems = AcquisitionPlanningService.GetActiveProcurementItemsMissingEvidence(
            plan,
            marketPlans);

        var missingItem = Assert.Single(missingItems);
        Assert.Equal(200, missingItem.ItemId);
    }

    [Fact]
    public void SelectActiveProcurementEvidence_TreatsSelectedDcMismatchAsMissing()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            CreateMarketPlan(200, "Intermediate", "Primal", "Leviathan")
        };

        var selection = AcquisitionPlanningService.SelectActiveProcurementEvidence(
            plan,
            marketPlans,
            MarketFetchScope.SelectedDataCenter,
            "Aether");

        Assert.Empty(selection.ReusablePlans);
        var missingItem = Assert.Single(selection.MissingItems);
        Assert.Equal(200, missingItem.ItemId);
    }

    [Fact]
    public void SelectActiveProcurementEvidence_TreatsSingleDcEvidenceAsMissingForRegionScope()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            CreateMarketPlan(200, "Intermediate", "Aether", "Siren")
        };

        var selection = AcquisitionPlanningService.SelectActiveProcurementEvidence(
            plan,
            marketPlans,
            MarketFetchScope.EntireRegion,
            "Aether");

        Assert.Empty(selection.ReusablePlans);
        var missingItem = Assert.Single(selection.MissingItems);
        Assert.Equal(200, missingItem.ItemId);
    }

    [Fact]
    public void SelectActiveProcurementEvidence_ReusesActivePlanWithRequiredScope()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            CreateMarketPlan(200, "Intermediate", "Aether", "Siren"),
            CreateMarketPlan(300, "Raw Material", "Aether", "Siren")
        };

        var selection = AcquisitionPlanningService.SelectActiveProcurementEvidence(
            plan,
            marketPlans,
            MarketFetchScope.SelectedDataCenter,
            "Aether");

        var reusablePlan = Assert.Single(selection.ReusablePlans);
        Assert.Equal(200, reusablePlan.ItemId);
        Assert.Empty(selection.MissingItems);
    }

    [Fact]
    public void MergeActiveProcurementEvidence_PrefersFetchedMissingPlansAndDoesNotIncludeInactiveCandidates()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var staleActivePlan = CreateMarketPlan(200, "Intermediate", "Aether", "Siren", totalCost: 500);
        var inactiveCandidatePlan = CreateMarketPlan(300, "Raw Material", "Aether", "Siren", totalCost: 100);
        var fetchedActivePlan = CreateMarketPlan(200, "Intermediate", "Aether", "Adamantoise", totalCost: 200);

        var merged = AcquisitionPlanningService.MergeActiveProcurementEvidence(
            plan,
            [staleActivePlan, inactiveCandidatePlan],
            [fetchedActivePlan]);

        var mergedPlan = Assert.Single(merged);
        Assert.Equal(200, mergedPlan.ItemId);
        Assert.Equal("Adamantoise", mergedPlan.RecommendedWorld?.WorldName);
        Assert.Equal(200, mergedPlan.RecommendedWorld?.TotalCost);
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
    public void CalculateCraftCost_PrefersRecommendedSplitCostForBoughtChildren()
    {
        var craft = new PlanNode
        {
            ItemId = 1,
            Name = "Route Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var boughtChild = new PlanNode
        {
            ItemId = 2,
            Name = "Route Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 1_000,
            Parent = craft
        };
        craft.Children.Add(boughtChild);

        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 2,
                Name = "Route Material",
                QuantityNeeded = 10,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 10_000,
                    TotalQuantityPurchased = 10
                },
                RecommendedSplit =
                [
                    new SplitWorldPurchase
                    {
                        WorldName = "Leviathan",
                        QuantityToBuy = 10,
                        TotalCost = 4_000,
                        EffectivePricePerNeededUnit = 400
                    }
                ]
            }
        };

        var cost = AcquisitionPlanningService.CalculateCraftCost(craft, marketPlans);

        Assert.Equal(4_000, cost);
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
    public void TryGetSelectedAcquisitionCost_SumsCraftCostsAcrossOccurrences()
    {
        var first = new PlanNode
        {
            ItemId = 100,
            Name = "Shared Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        first.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Cheap Ingredient",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 50,
            Parent = first
        });
        var second = new PlanNode
        {
            ItemId = 100,
            Name = "Shared Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        second.Children.Add(new PlanNode
        {
            ItemId = 300,
            Name = "Expensive Ingredient",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = second
        });

        var hasCost = AcquisitionPlanningService.TryGetSelectedAcquisitionCost(
            [first, second],
            Array.Empty<DetailedShoppingPlan>(),
            out var cost);

        Assert.True(hasCost);
        Assert.Equal(400, cost);
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
    public void ApplyCheapestAcquisitionDefaults_VendorBeatsMarketAndCraft_SelectsVendor()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Vendor Root",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            MarketPrice = 500,
            VendorPrice = 20,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Craft Child",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.VendorBuy, root.Source);
    }

    [Fact]
    public void ApplyCheapestAcquisitionDefaults_SystemDefaultVendorCanChangeToCheaperCraft()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Craftable Vendor Default",
            Quantity = 1,
            Source = AcquisitionSource.VendorBuy,
            SourceReason = AcquisitionSourceReason.SystemDefault,
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
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            MarketPrice = 50,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(1, changed);
        Assert.Equal(AcquisitionSource.Craft, root.Source);
        Assert.Equal(AcquisitionSourceReason.SystemDefault, root.SourceReason);
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
    public void SetAcquisitionSource_UserSelectionRecordsReason()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Selectable",
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromVendor = true,
            CanCraft = true
        };

        AcquisitionPlanningService.SetAcquisitionSource(
            node,
            AcquisitionSource.VendorBuy,
            AcquisitionSourceReason.UserSelected);

        Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
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
    public void GetAvailableSources_PreservesSpecialCurrencyVendor()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Token Item",
            Quantity = 1,
            Source = AcquisitionSource.VendorSpecialCurrency,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromVendor = true,
            VendorOptions =
            [
                new VendorInfo
                {
                    Name = "Token Vendor",
                    Price = 10,
                    Currency = "bicolor gemstone"
                }
            ]
        };

        node.EnsureValidAcquisitionSource();

        Assert.Contains(AcquisitionSource.VendorSpecialCurrency, AcquisitionPlanningService.GetAvailableSources(node));
        Assert.Equal(AcquisitionSource.VendorSpecialCurrency, node.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
    }

    [Fact]
    public void GetAvailableSources_CraftWithoutChildren_DoesNotAdvertiseCraft()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Incomplete Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
        };

        var sources = AcquisitionPlanningService.GetAvailableSources(node);

        Assert.DoesNotContain(AcquisitionSource.Craft, sources);
        Assert.Contains(AcquisitionSource.MarketBuyNq, sources);
    }

    [Fact]
    public void TryGetAcquisitionCost_MarketBuyHq_UsesShoppingPlanHqListings()
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
                    TotalCost = 20_000,
                    TotalQuantityPurchased = 2,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 2,
                            PricePerUnit = 100,
                            IsHq = true
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

        Assert.True(hasCost);
        Assert.Equal(200, cost);
    }

    [Fact]
    public void TryGetAcquisitionCost_MarketBuyHq_OnlyChargesNeededQuantityFromLargeListing()
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
                    TotalCost = 20_000,
                    TotalQuantityPurchased = 99,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 99,
                            PricePerUnit = 100,
                            IsHq = true
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

        Assert.True(hasCost);
        Assert.Equal(200, cost);
    }

    [Fact]
    public void TryGetSelectedAcquisitionCost_WithCostContext_UsesMarketEvidence()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Craft Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Market Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Parent = root
        };
        root.Children.Add(child);

        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Market Child",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 150,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var context = AcquisitionPlanningService.CreateCostContext(marketPlans);

        var hasCost = AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], context, out var cost);

        Assert.True(hasCost);
        Assert.Equal(150, cost);
    }

    [Fact]
    public void AcquisitionCostContext_TryGetShoppingPlan_ReturnsPlanByItemId()
    {
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new() { ItemId = 200, Name = "Market Child", QuantityNeeded = 2 }
        };

        var context = AcquisitionPlanningService.CreateCostContext(marketPlans);

        Assert.True(context.TryGetShoppingPlan(200, out var shoppingPlan));
        Assert.Equal("Market Child", shoppingPlan!.Name);
        Assert.False(context.TryGetShoppingPlan(999, out _));
    }

    [Fact]
    public void DetermineCheapestAcquisitionSource_WithCostContext_MatchesEnumerableApi()
    {
        var node = new PlanNode
        {
            ItemId = 500,
            Name = "Comparable Item",
            Quantity = 3,
            Source = AcquisitionSource.UnknownSource,
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 400,
            MarketPrice = 100,
            HqMarketPrice = 500
        };
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 500,
                Name = "Comparable Item",
                QuantityNeeded = 3,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 180,
                    TotalQuantityPurchased = 3
                }
            }
        };

        var context = AcquisitionPlanningService.CreateCostContext(marketPlans);

        var oldResult = AcquisitionPlanningService.DetermineCheapestAcquisitionSource(node, marketPlans);
        var contextResult = AcquisitionPlanningService.DetermineCheapestAcquisitionSource(node, context);

        Assert.Equal(oldResult, contextResult);
        Assert.Equal(AcquisitionSource.MarketBuyNq, contextResult);
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
    public void TryGetSelectedAcquisitionCost_WithCostContext_PricesDirectChildrenBySelectedSourceWhenCapabilityFlagsAreStale()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Craft Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Restored Vendor Child",
            Quantity = 2,
            Source = AcquisitionSource.VendorBuy,
            VendorPrice = 25,
            CanBuyFromVendor = false,
            Parent = root
        };
        root.Children.Add(child);
        var context = AcquisitionPlanningService.CreateCostContext(Array.Empty<DetailedShoppingPlan>());

        var hasCost = AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], context, out var cost);

        Assert.True(hasCost);
        Assert.Equal(50, cost);
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

    [Fact]
    public void ApplyCheapestAcquisitionDefaults_HqRequired_DoesNotSelectNqMarket()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "HQ Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            MustBeHq = true,
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBeHq = true,
            MarketPrice = 10,
            HqMarketPrice = 1_000,
            Yield = 1
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Cheap Child",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 20,
            Parent = root
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var changed = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(0, changed);
        Assert.Equal(AcquisitionSource.Craft, root.Source);
    }

    [Fact]
    public void TryGetAcquisitionCost_MarketBuyNq_IgnoresVendorOverrideRecommendation()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            Name = "Vendor Comparable",
            Quantity = 3,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 50
        };
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 100,
                Name = "Vendor Comparable",
                QuantityNeeded = 3,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = MarketShoppingConstants.VendorWorldName,
                    TotalCost = 150,
                    TotalQuantityPurchased = 3
                },
                WorldOptions =
                [
                    new WorldShoppingSummary
                    {
                        WorldName = MarketShoppingConstants.VendorWorldName,
                        TotalCost = 150,
                        TotalQuantityPurchased = 3
                    },
                    new WorldShoppingSummary
                    {
                        WorldName = "Siren",
                        TotalCost = 1_200,
                        TotalQuantityPurchased = 3,
                        AveragePricePerUnit = 400
                    }
                ]
            }
        };

        var hasCost = AcquisitionPlanningService.TryGetAcquisitionCost(
            node,
            AcquisitionSource.MarketBuyNq,
            marketPlans,
            out var cost);

        Assert.True(hasCost);
        Assert.Equal(1_200, cost);
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
