using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CoreAcquisitionEvaluationSnapshotBuilderTests
{
    [Fact]
    public void Build_UsesCoreUnavailableItemIdsForMissingDataEvidence()
    {
        var plan = CreatePlan();

        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int> { 200 },
            CoreAcquisitionFilter.All,
            CreateProjection(plan));

        var row = snapshot.Rows.Single(row => row.ItemId == 200);
        Assert.Equal("Needs data", row.MarketEvidence);
    }

    [Fact]
    public void Build_UsesRecipeDemandProjectionRowsForQuantitiesAndMembership()
    {
        var root = CreateRoot(100, "Final Craft");
        var material = new PlanNode
        {
            ItemId = 200,
            Name = "Projected Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = root
        };
        root.Children.Add(material);
        var plan = new CraftingPlan { RootItems = [root] };
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 7),
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 5, suppressedByNodeId: root.NodeId, suppressedByItemId: root.ItemId, suppressedByItemName: root.Name)
            ],
            MarketAnalysisCandidates: [CreateDemandRow(RecipeDemandViewKind.MarketAnalysisCandidate, material, quantity: 11)],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, material, quantity: 7)],
            SuppressedDemand: [CreateDemandRow(RecipeDemandViewKind.Suppressed, material, quantity: 5, suppressedByNodeId: root.NodeId, suppressedByItemId: root.ItemId, suppressedByItemName: root.Name)]);

        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);
        Assert.Equal(7, row.TotalQuantity);
        Assert.Equal(7, row.ActiveQuantity);
        Assert.True(row.IsActiveProcurement);
        Assert.True(row.HasSuppressedOccurrences);
        Assert.Equal(["Final Craft"], row.SuppressedBy);
        Assert.Equal(11, snapshot.MarketAnalysisCandidates.Single(item => item.ItemId == 200).TotalQuantity);
        Assert.Equal(7, snapshot.ActiveProcurementItems.Single(item => item.ItemId == 200).TotalQuantity);
    }

    [Fact]
    public void Build_SuppressedCraftRowPreservesSelectedSource()
    {
        var root = CreateRoot(100, "Suppressing Parent");
        root.Source = AcquisitionSource.MarketBuyNq;
        root.CanBuyFromMarket = true;

        var material = new PlanNode
        {
            ItemId = 200,
            Name = "Suppressed Material",
            Quantity = 10,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 1_000,
            Yield = 1,
            Parent = root
        };
        material.Children.Add(new PlanNode
        {
            ItemId = 201,
            Name = "Expensive Child",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Parent = material
        });
        root.Children.Add(material);

        var plan = new CraftingPlan { RootItems = [root] };
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.PlanOccurrence,
                    material,
                    quantity: 10,
                    suppressedByNodeId: root.NodeId,
                    suppressedByItemId: root.ItemId,
                    suppressedByItemName: root.Name)
            ],
            MarketAnalysisCandidates: [],
            ActiveProcurementDemand: [],
            SuppressedDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.Suppressed,
                    material,
                    quantity: 10,
                    suppressedByNodeId: root.NodeId,
                    suppressedByItemId: root.ItemId,
                    suppressedByItemName: root.Name)
            ]);

        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);

        Assert.True(row.IsFullySuppressed);
        Assert.Equal(AcquisitionSource.Craft, row.Source);
        Assert.Equal("100,000g", row.EstimatedCost);
    }

    [Fact]
    public void Build_MixedActiveAndSuppressedRows_UsesActiveOccurrenceForQuantityUsedInAndReadState()
    {
        var activeParent = CreateRoot(100, "Active Parent");
        var boughtParent = CreateRoot(101, "Bought Parent");
        var activeMaterial = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Material",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 25,
            Parent = activeParent
        };
        var suppressedMaterial = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Material",
            Quantity = 7,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            VendorPrice = 5,
            Parent = boughtParent
        };
        activeParent.Children.Add(activeMaterial);
        boughtParent.Children.Add(suppressedMaterial);
        var plan = new CraftingPlan { RootItems = [activeParent, boughtParent] };
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.PlanOccurrence,
                    suppressedMaterial,
                    quantity: 7,
                    suppressedByNodeId: boughtParent.NodeId,
                    suppressedByItemId: boughtParent.ItemId,
                    suppressedByItemName: boughtParent.Name),
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, activeMaterial, quantity: 5)
            ],
            MarketAnalysisCandidates: [],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, activeMaterial, quantity: 5)],
            SuppressedDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.Suppressed,
                    suppressedMaterial,
                    quantity: 7,
                    suppressedByNodeId: boughtParent.NodeId,
                    suppressedByItemId: boughtParent.ItemId,
                    suppressedByItemName: boughtParent.Name)
            ]);

        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);
        Assert.Same(activeMaterial, row.Node);
        Assert.Equal(activeMaterial.NodeId, row.NodeId);
        Assert.Equal(5, row.TotalQuantity);
        Assert.Equal("Active Parent x1", row.UsedIn);
        Assert.Equal(AcquisitionSource.MarketBuyNq, row.Source);
        Assert.Equal(25, row.UnitPrice);
        Assert.Equal("125g", row.EstimatedCost);
        Assert.True(row.HasSuppressedOccurrences);
        Assert.Equal(["Bought Parent"], row.SuppressedBy);
    }

    [Fact]
    public void Build_HqEstimateWithoutHqEvidenceDoesNotUsePlannerUnitPriceFallback()
    {
        var root = CreateRoot(100, "Final Craft");
        var material = new PlanNode
        {
            ItemId = 200,
            Name = "HQ Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyHq,
            MustBeHq = true,
            CanBeHq = true,
            CanBuyFromMarket = true,
            HqMarketPrice = 10_000,
            Parent = root
        };
        root.Children.Add(material);
        var plan = new CraftingPlan { RootItems = [root] };
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "HQ Material",
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

        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All,
            CreateProjection(plan));

        var row = snapshot.Rows.Single(row => row.ItemId == 200);

        Assert.Equal("-", row.EstimatedCost);
    }

    [Fact]
    public void CompareWithLegacyTraversal_MixedAcquisitionPlan_ReturnsNoMismatches()
    {
        var plan = CreateMixedAcquisitionPlan();

        var report = CoreAcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CreateProjection(plan));

        Assert.True(report.Matches);
        Assert.Empty(report.Mismatches);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = CreateRoot(100, "Final Craft");
        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Parent = root
        };
        var child = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Child",
            Quantity = 4,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = intermediate
        };

        intermediate.Children.Add(child);
        root.Children.Add(intermediate);
        return new CraftingPlan { RootItems = [root] };
    }

    private static CraftingPlan CreateMixedAcquisitionPlan()
    {
        var vendorRoot = CreateRoot(100, "Vendor Root");
        var vendorChild = new PlanNode
        {
            ItemId = 200,
            Name = "Vendor Child",
            Quantity = 2,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 50,
            Parent = vendorRoot
        };
        vendorChild.Children.Add(new PlanNode
        {
            ItemId = 300,
            Name = "Suppressed Grandchild",
            Quantity = 7,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 5,
            Parent = vendorChild
        });
        vendorRoot.Children.Add(vendorChild);

        var marketRoot = CreateRoot(101, "Market Root");
        marketRoot.Children.Add(new PlanNode
        {
            ItemId = 400,
            Name = "Repeated Market Child",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyHq,
            MustBeHq = true,
            CanBeHq = true,
            CanBuyFromMarket = true,
            HqMarketPrice = 100,
            Parent = marketRoot
        });
        marketRoot.Children.Add(new PlanNode
        {
            ItemId = 500,
            Name = "Unknown Leaf",
            Quantity = 4,
            Source = AcquisitionSource.UnknownSource,
            Parent = marketRoot
        });
        marketRoot.Children.Add(new PlanNode
        {
            ItemId = 400,
            Name = "Repeated Market Child",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 25,
            Parent = marketRoot
        });

        return new CraftingPlan { RootItems = [vendorRoot, marketRoot] };
    }

    private static RecipeDemandProjection CreateProjection(CraftingPlan? plan) =>
        new RecipeDemandProjectionService().Build(plan, snapshot: null);

    private static PlanNode CreateRoot(int itemId, string name)
    {
        return new PlanNode
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
    }

    private static RecipeDemandRow CreateDemandRow(
        RecipeDemandViewKind viewKind,
        PlanNode node,
        int quantity,
        string? suppressedByNodeId = null,
        int? suppressedByItemId = null,
        string? suppressedByItemName = null)
    {
        return new RecipeDemandRow(
            viewKind,
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            quantity,
            RecipeDemandQuantityBasis.PlanNodeQuantity,
            node.MustBeHq,
            node.Source,
            node.SourceReason,
            node.Children.Count > 0,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.Parent?.NodeId,
            node.Parent?.Name,
            null,
            null,
            null,
            null,
            suppressedByNodeId,
            suppressedByItemId,
            suppressedByItemName,
            node.CanCraft,
            node.CanBeHq,
            node.Yield,
            node.HqMarketPrice,
            node.VendorPrice,
            parentOutputQuantity: node.Parent?.Quantity ?? node.Quantity);
    }
}
