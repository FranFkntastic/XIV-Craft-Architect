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
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            CreateProjection(plan));

        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 100);
        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 200 && row.MarketEvidence.StartsWith("Siren"));
        Assert.Equal(snapshot.Rows.Count, snapshot.VisibleRows.Count);
        Assert.Contains(snapshot.ActiveProcurementItems, item => item.ItemId == 200);
    }

    [Fact]
    public void Build_ActiveFilterShowsOnlyActiveProcurementRows()
    {
        var plan = CreatePlan();

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.Active,
            CreateProjection(plan));

        Assert.All(snapshot.VisibleRows, row => Assert.True(row.IsActiveProcurement));
    }

    [Fact]
    public void Build_UsesUnavailableMarketItemsForMissingDataEvidence()
    {
        var plan = CreatePlan();

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            [new MarketDataUnavailableItem(200, "Intermediate")],
            AcquisitionFilter.All,
            CreateProjection(plan));

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);
        Assert.Equal("Needs data", row.MarketEvidence);
    }

    [Fact]
    public void Build_EstimateMatchesPrimaryNodeSelectedOptionCost()
    {
        var firstRoot = CreateRoot(100, "First Root");
        var secondRoot = CreateRoot(101, "Second Root");
        var firstShared = CreateCraftedSharedChild(firstRoot, quantity: 2, rawQuantity: 10);
        var secondShared = CreateCraftedSharedChild(secondRoot, quantity: 6, rawQuantity: 30);
        firstRoot.Children.Add(firstShared);
        secondRoot.Children.Add(secondShared);
        var plan = new CraftingPlan { RootItems = [firstRoot, secondRoot] };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            CreateProjection(plan));

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("200g", row.EstimatedCost);
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
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal(12, row.TotalQuantity);
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
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("Final Craft x3", row.UsedIn);
        Assert.Equal(15, row.TotalQuantity);
    }

    [Fact]
    public void Build_EstimateUsesRecipeDemandProjectionQuantity()
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

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("70g", row.EstimatedCost);
        Assert.Equal(2, material.Quantity);
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
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("700g", row.EstimatedCost);
        Assert.Equal(2, material.Quantity);
    }

    [Fact]
    public void Build_UsesRecipeDemandProjectionMembershipForDecisionRoles()
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
                CreateDemandRow(RecipeDemandViewKind.PlanOccurrence, material, quantity: 2)
            ],
            MarketAnalysisCandidates: Array.Empty<RecipeDemandRow>(),
            ActiveProcurementDemand: Array.Empty<RecipeDemandRow>(),
            SuppressedDemand: Array.Empty<RecipeDemandRow>());

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.False(row.IsActiveProcurement);
        Assert.False(row.IsMarketCandidate);
        Assert.Empty(snapshot.MarketAnalysisCandidates);
        Assert.Empty(snapshot.ActiveProcurementItems);
        Assert.Empty(AcquisitionEvaluationSnapshotBuilder.ApplyFilter(snapshot.Rows, AcquisitionFilter.Active));
        Assert.Empty(AcquisitionEvaluationSnapshotBuilder.ApplyFilter(snapshot.Rows, AcquisitionFilter.Market));
    }

    [Fact]
    public void Build_UsesRecipeDemandProjectionReadFieldsForDecisionRows()
    {
        var root = CreateRoot(100, "Final Craft");
        var material = new PlanNode
        {
            ItemId = 200,
            Name = "Plan Material",
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
                    quantity: 2,
                    itemName: "Projected Material",
                    source: AcquisitionSource.VendorBuy,
                    canBuyFromMarket: false,
                    canBuyFromVendor: true,
                    unitPrice: 30,
                    vendorUnitPrice: 12,
                    canCraft: true,
                    canBeHq: true,
                    yield: 2,
                    hqUnitPrice: 40)
            ],
            MarketAnalysisCandidates: Array.Empty<RecipeDemandRow>(),
            ActiveProcurementDemand:
            [
                CreateDemandRow(
                    RecipeDemandViewKind.ActiveProcurement,
                    material,
                    quantity: 2,
                    itemName: "Projected Material",
                    source: AcquisitionSource.VendorBuy,
                    canBuyFromMarket: false,
                    canBuyFromVendor: true,
                    unitPrice: 30,
                    vendorUnitPrice: 12,
                    canCraft: true,
                    canBeHq: true,
                    yield: 2,
                    hqUnitPrice: 40)
            ],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All,
            projection);

        var row = snapshot.Rows.Single(row => row.ItemId == 200);

        Assert.Equal("Projected Material", row.ItemName);
        Assert.Equal(AcquisitionSource.VendorBuy, row.Source);
        Assert.False(row.MustBeHq);
        Assert.False(row.HasChildren);
        Assert.True(row.CanCraft);
        Assert.True(row.CanBeHq);
        Assert.Equal(2, row.Yield);
        Assert.False(row.CanBuyFromMarket);
        Assert.True(row.CanBuyFromVendor);
        Assert.Equal(30, row.UnitPrice);
        Assert.Equal(40, row.HqUnitPrice);
        Assert.Equal(12, row.VendorUnitPrice);
        Assert.Same(material, row.Node);
    }

    [Fact]
    public void CompareWithLegacyTraversal_MixedAcquisitionPlan_ReturnsNoMismatches()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            projection);

        Assert.True(report.Matches);
        Assert.Empty(report.Mismatches);
    }

    [Fact]
    public void CompareWithLegacyTraversal_ProjectionActiveQuantityDiffers_ReportsMismatch()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);
        var wrongRows = projection.ActiveProcurementDemand
            .Select(row => row.ItemId == 400 ? row with { Quantity = row.Quantity + 10 } : row)
            .ToList();
        var mismatchedProjection = projection with { ActiveProcurementDemand = wrongRows };

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            mismatchedProjection);

        var mismatch = Assert.Single(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.ActiveProcurementItem &&
            item.Field == AcquisitionEvaluationParityField.TotalQuantity &&
            item.ItemId == 400);
        Assert.Equal("8", mismatch.Expected);
        Assert.Equal("28", mismatch.Actual);
    }

    [Fact]
    public void CompareWithLegacyTraversal_ProjectionRowReadFieldsDiffer_ReportMismatches()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);
        var wrongRows = projection.AllPlanDemand
            .Select(row => row.ItemId == 400
                ? row with
                {
                    Source = AcquisitionSource.VendorBuy,
                    CanBuyFromMarket = false,
                    CanBuyFromVendor = true,
                    CanBeHq = false,
                    HqUnitPrice = 999,
                    VendorUnitPrice = 11
                }
                : row)
            .ToList();
        var mismatchedProjection = projection with { AllPlanDemand = wrongRows };

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            mismatchedProjection);

        Assert.Contains(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.Row &&
            item.Field == AcquisitionEvaluationParityField.Source &&
            item.ItemId == 400);
        Assert.Contains(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.Row &&
            item.Field == AcquisitionEvaluationParityField.CanBuyFromMarket &&
            item.ItemId == 400);
    }

    [Fact]
    public void CompareWithLegacyTraversal_RepeatedProjectionPrimaryNodeDiffers_ReportsNodeIdMismatch()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);
        var reorderedRows = projection.AllPlanDemand
            .Where(row => row.ItemId != 400)
            .Concat(projection.AllPlanDemand.Where(row => row.ItemId == 400).Reverse())
            .ToList();
        var mismatchedProjection = projection with { AllPlanDemand = reorderedRows };

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            mismatchedProjection);

        Assert.Contains(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.Row &&
            item.Field == AcquisitionEvaluationParityField.NodeId &&
            item.ItemId == 400);
    }

    [Fact]
    public void CompareWithLegacyTraversal_ProjectionAggregateReadFieldsDiffer_ReportMismatches()
    {
        var plan = CreateMixedAcquisitionPlan();
        var projection = CreateProjection(plan);
        var wrongRows = projection.MarketAnalysisCandidates
            .Select(row => row.ItemId == 400
                ? row with
                {
                    IconId = 999,
                    UnitPrice = 12345
                }
                : row)
            .ToList();
        var mismatchedProjection = projection with { MarketAnalysisCandidates = wrongRows };

        var report = AcquisitionEvaluationSnapshotBuilder.CompareWithLegacyTraversal(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            mismatchedProjection);

        Assert.Contains(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.MarketAnalysisCandidate &&
            item.Field == AcquisitionEvaluationParityField.IconId &&
            item.ItemId == 400);
        Assert.Contains(report.Mismatches, item =>
            item.View == AcquisitionEvaluationParityView.MarketAnalysisCandidate &&
            item.Field == AcquisitionEvaluationParityField.UnitPrice &&
            item.ItemId == 400);
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
