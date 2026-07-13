using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationSnapshotBuilderTests
{
    [Fact]
    public void Build_UsesMarketEvidenceLookupAndProducesRows()
    {
        var plan = CreatePlan();
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
                    TotalCost = 300,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            CreateProjection(plan));

        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 100);
        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 200 && row.MarketEvidence.StartsWith("Siren"));
        Assert.Equal(snapshot.Rows.Count, snapshot.VisibleRows.Count);
        Assert.Contains(snapshot.ActiveProcurementItems, item => item.ItemId == 200);
    }
    [Fact]
    public void Build_UsesUnavailableMarketItemsForMissingDataEvidence()
    {
        var plan = CreatePlan();

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            [new CoreMarketDataUnavailableItem(200, "Intermediate")],
            AcquisitionFilter.All,
            CreateProjection(plan));

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);
        Assert.Equal("Needs data", row.MarketEvidence);
    }
    [Fact]
    public void Build_UsesRecipeDemandProjectionRowsForDecisionQuantities()
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

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal(7, row.TotalQuantity);
        Assert.Equal(7, row.ActiveQuantity);
        Assert.True(row.IsActiveProcurement);
        Assert.True(row.HasSuppressedOccurrences);
        Assert.False(row.IsFullySuppressed);
        Assert.Equal(["Final Craft"], row.SuppressedBy);
        Assert.Equal("Final Craft x1", row.UsedIn);
        Assert.Equal(11, snapshot.MarketAnalysisCandidates.Single(item => item.ItemId == 200).TotalQuantity);
        Assert.Equal(7, snapshot.ActiveProcurementItems.Single(item => item.ItemId == 200).TotalQuantity);
    }

    [Fact]
    public void Build_MixedActiveAndSuppressedCraftPath_UsesOnlyActiveOccurrencesForQuantityAndUsedIn()
    {
        var activeParent = CreateRoot(100, "Active Parent");
        var boughtParent = CreateRoot(101, "Bought Parent");
        boughtParent.Source = AcquisitionSource.MarketBuyNq;
        var activeMaterial = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Craft",
            Quantity = 5,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = activeParent
        };
        var suppressedMaterial = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Craft",
            Quantity = 7,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = boughtParent
        };
        activeParent.Children.Add(activeMaterial);
        boughtParent.Children.Add(suppressedMaterial);
        var plan = new CraftingPlan { RootItems = [activeParent, boughtParent] };
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, activeMaterial, quantity: 5, parentOutputQuantity: 1),
                CreateDemandRow(
                    RecipeDemandViewKind.PlanOccurrence,
                    suppressedMaterial,
                    quantity: 7,
                    parentOutputQuantity: 1,
                    suppressedByNodeId: boughtParent.NodeId,
                    suppressedByItemId: boughtParent.ItemId,
                    suppressedByItemName: boughtParent.Name)
            ],
            MarketAnalysisCandidates: [],
            ActiveProcurementDemand: [],
            SuppressedDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.Suppressed,
                    suppressedMaterial,
                    quantity: 7,
                    parentOutputQuantity: 1,
                    suppressedByNodeId: boughtParent.NodeId,
                    suppressedByItemId: boughtParent.ItemId,
                    suppressedByItemName: boughtParent.Name)
            ]);

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);

        Assert.Equal(5, row.TotalQuantity);
        Assert.Equal("Active Parent x1", row.UsedIn);
        Assert.True(row.HasSuppressedOccurrences);
        Assert.False(row.IsFullySuppressed);
        Assert.Equal(["Bought Parent"], row.SuppressedBy);
    }
    [Fact]
    public void Build_UsedInUsesRecipeDemandParentOutputQuantity()
    {
        var root = CreateRoot(100, "Final Craft");
        root.Quantity = 3;
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
                CreateDemandRow(
                    RecipeDemandViewKind.PlanOccurrence,
                    material,
                    quantity: 15,
                    parentOutputQuantity: 3)
            ],
            MarketAnalysisCandidates: [CreateDemandRow(RecipeDemandViewKind.MarketAnalysisCandidate, material, quantity: 15, parentOutputQuantity: 3)],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, material, quantity: 15, parentOutputQuantity: 3)],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("Final Craft x3", row.UsedIn);
        Assert.Equal(15, row.TotalQuantity);
    }
    [Fact]
    public void Build_EstimateUsesRecipeDemandProjectionQuantityForMarketEvidenceCost()
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
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 7)
            ],
            MarketAnalysisCandidates: [CreateDemandRow(RecipeDemandViewKind.MarketAnalysisCandidate, material, quantity: 7)],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, material, quantity: 7)],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Projected Material",
                QuantityNeeded = 10,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 1_000,
                    TotalQuantityPurchased = 10
                }
            }
        };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("700g", row.EstimatedCost);
        Assert.Equal(2, material.Quantity);
    }

    [Fact]
    public void Build_EstimateUsesUnsupportedMarketProjectionInsteadOfPlannerUnitPrice()
    {
        var root = CreateRoot(100, "Final Craft");
        var material = new PlanNode
        {
            ItemId = 200,
            Name = "Cassia Lumber",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 3,
            Parent = root
        };
        root.Children.Add(material);
        var plan = new CraftingPlan { RootItems = [root] };
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 999)
            ],
            MarketAnalysisCandidates: [CreateDemandRow(RecipeDemandViewKind.MarketAnalysisCandidate, material, quantity: 999)],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, material, quantity: 999)],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Cassia Lumber",
                QuantityNeeded = 999,
                DCAveragePrice = 6_408,
                WorldOptions =
                [
                    new WorldShoppingSummary
                    {
                        DataCenter = "Aether",
                        WorldName = "Adamantoise",
                        TotalCost = 3_000,
                        TotalQuantityPurchased = 1
                    }
                ]
            }
        };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("Projected only", row.MarketEvidence);
        Assert.Equal("6,401,592g", row.EstimatedCost);
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
        var projection = new RecipeDemandProjection(
            AllPlanDemand:
            [
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 2, canBeHq: true, hqUnitPrice: 10_000)
            ],
            MarketAnalysisCandidates: [CreateDemandRow(RecipeDemandViewKind.MarketAnalysisCandidate, material, quantity: 2, canBeHq: true, hqUnitPrice: 10_000)],
            ActiveProcurementDemand: [CreateDemandRow(RecipeDemandViewKind.ActiveProcurement, material, quantity: 2, canBeHq: true, hqUnitPrice: 10_000)],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
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

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("-", row.EstimatedCost);
    }



    [Fact]
    public void Build_SuppressedCraftRowUsesCheapestRepresentativeSource()
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
                    canBuyFromMarket: true,
                    unitPrice: 1_000,
                    canCraft: true,
                    suppressedByNodeId: root.NodeId,
                    suppressedByItemId: root.ItemId,
                    suppressedByItemName: root.Name)
            ],
            MarketAnalysisCandidates: Array.Empty<RecipeDemandRow>(),
            ActiveProcurementDemand: Array.Empty<RecipeDemandRow>(),
            SuppressedDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.Suppressed,
                    material,
                    quantity: 10,
                    canBuyFromMarket: true,
                    unitPrice: 1_000,
                    canCraft: true,
                    suppressedByNodeId: root.NodeId,
                    suppressedByItemId: root.ItemId,
                    suppressedByItemName: root.Name)
            ]);
        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);

        Assert.True(row.IsFullySuppressed);
        Assert.Equal(AcquisitionSource.MarketBuyNq, row.Source);
        Assert.Equal("10,000g", row.EstimatedCost);
    }



    [Fact]
    public void CompareWithLegacyTraversal_MixedAcquisitionPlan_ReturnsNoMismatches()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>(),
            projection);

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
        var suppressedGrandchild = new PlanNode
        {
            ItemId = 300,
            Name = "Suppressed Grandchild",
            Quantity = 7,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 5,
            Parent = vendorChild
        };
        vendorChild.Children.Add(suppressedGrandchild);
        vendorRoot.Children.Add(vendorChild);

        var marketRoot = CreateRoot(101, "Market Root");
        var hqChild = new PlanNode
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
        };
        var unknownLeaf = new PlanNode
        {
            ItemId = 500,
            Name = "Unknown Leaf",
            Quantity = 4,
            Source = AcquisitionSource.UnknownSource,
            Parent = marketRoot
        };
        var repeatedChild = new PlanNode
        {
            ItemId = 400,
            Name = "Repeated Market Child",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 25,
            Parent = marketRoot
        };
        marketRoot.Children.Add(hqChild);
        marketRoot.Children.Add(unknownLeaf);
        marketRoot.Children.Add(repeatedChild);

        return new CraftingPlan { RootItems = [vendorRoot, marketRoot] };
    }

    private static RecipeDemandProjection CreateProjection(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService().Build(plan, snapshot: null);
    }

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

    private static PlanNode CreateCraftedSharedChild(PlanNode parent, int quantity, int rawQuantity)
    {
        var shared = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Intermediate",
            Quantity = quantity,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 1_000,
            Yield = 1,
            Parent = parent
        };
        shared.Children.Add(new PlanNode
        {
            ItemId = 300,
            Name = "Raw Child",
            Quantity = rawQuantity,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 5,
            Parent = shared
        });
        return shared;
    }

    private static RecipeDemandRow CreateDemandRow(
        RecipeDemandViewKind viewKind,
        PlanNode node,
        int quantity,
        string? itemName = null,
        AcquisitionSource? source = null,
        bool? canBuyFromMarket = null,
        bool? canBuyFromVendor = null,
        decimal? unitPrice = null,
        bool canCraft = false,
        bool canBeHq = false,
        int yield = 1,
        decimal hqUnitPrice = 0,
        decimal vendorUnitPrice = 0,
        string? suppressedByNodeId = null,
        int? suppressedByItemId = null,
        string? suppressedByItemName = null,
        int? parentOutputQuantity = null)
    {
        return new RecipeDemandRow(
            viewKind,
            node.NodeId,
            node.ItemId,
            itemName ?? node.Name,
            node.IconId,
            quantity,
            RecipeDemandQuantityBasis.PlanNodeQuantity,
            node.MustBeHq,
            source ?? node.Source,
            node.SourceReason,
            node.Children.Count > 0,
            canBuyFromMarket ?? node.CanBuyFromMarket,
            canBuyFromVendor ?? node.CanBuyFromVendor,
            unitPrice ?? node.MarketPrice,
            node.Parent?.NodeId,
            node.Parent?.Name,
            null,
            null,
            null,
            null,
            suppressedByNodeId,
            suppressedByItemId,
            suppressedByItemName,
            canCraft,
            canBeHq,
            yield,
            hqUnitPrice,
            vendorUnitPrice,
            parentOutputQuantity: parentOutputQuantity ?? node.Parent?.Quantity ?? node.Quantity);
    }
}
