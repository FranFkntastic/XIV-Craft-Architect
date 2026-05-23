using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class PlanBulkEditServiceTests
{
    [Fact]
    public void RequireHqMaterials_SelectedNode_UpdatesEligibleDescendantsOnly()
    {
        var root = Node("Root Craft", mustBeHq: true, canBeHq: true, source: AcquisitionSource.Craft);
        var marketMaterial = Node("Market Material", canBeHq: true, source: AcquisitionSource.MarketBuyNq);
        var crystal = Node("Crystal", canBeHq: false, source: AcquisitionSource.MarketBuyNq);
        var vendorMaterial = Node("Vendor Material", canBeHq: true, source: AcquisitionSource.VendorBuy);
        var unknownMaterial = Node("Unknown Material", canBeHq: true, source: AcquisitionSource.UnknownSource);
        root.Children.AddRange([marketMaterial, crystal, vendorMaterial, unknownMaterial]);

        var result = PlanBulkEditService.RequireHqMaterials(root, includeNested: true);

        Assert.Equal(1, result.ChangedNodes);
        Assert.True(marketMaterial.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, marketMaterial.Source);
        Assert.False(crystal.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyNq, crystal.Source);
        Assert.False(vendorMaterial.MustBeHq);
        Assert.Equal(AcquisitionSource.VendorBuy, vendorMaterial.Source);
        Assert.False(unknownMaterial.MustBeHq);
        Assert.Equal(AcquisitionSource.UnknownSource, unknownMaterial.Source);
        Assert.True(root.MustBeHq);
    }

    [Fact]
    public void RequireHqMaterials_DirectOnly_DoesNotUpdateGrandchildren()
    {
        var root = Node("Root Craft", canBeHq: true, source: AcquisitionSource.Craft);
        var intermediate = Node("Intermediate", canBeHq: true, source: AcquisitionSource.Craft);
        var grandchild = Node("Grandchild", canBeHq: true, source: AcquisitionSource.MarketBuyNq);
        root.Children.Add(intermediate);
        intermediate.Children.Add(grandchild);

        var result = PlanBulkEditService.RequireHqMaterials(root, includeNested: false);

        Assert.Equal(1, result.ChangedNodes);
        Assert.True(intermediate.MustBeHq);
        Assert.False(grandchild.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyNq, grandchild.Source);
    }

    [Fact]
    public void RequireHqMaterialsForHqRoots_UpdatesOnlyHqRootCrafts()
    {
        var hqRoot = Node("HQ Root", mustBeHq: true, canBeHq: true, source: AcquisitionSource.Craft);
        var nqRoot = Node("NQ Root", mustBeHq: false, canBeHq: true, source: AcquisitionSource.Craft);
        var hqMaterial = Node("HQ Material", canBeHq: true, source: AcquisitionSource.MarketBuyNq);
        var nqMaterial = Node("NQ Material", canBeHq: true, source: AcquisitionSource.MarketBuyNq);
        hqRoot.Children.Add(hqMaterial);
        nqRoot.Children.Add(nqMaterial);

        var plan = new CraftingPlan
        {
            RootItems = [hqRoot, nqRoot]
        };

        var result = PlanBulkEditService.RequireHqMaterialsForHqRoots(plan, includeNested: true);

        Assert.Equal(1, result.ChangedNodes);
        Assert.True(hqMaterial.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, hqMaterial.Source);
        Assert.False(nqMaterial.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyNq, nqMaterial.Source);
    }

    private static PlanNode Node(
        string name,
        bool mustBeHq = false,
        bool canBeHq = false,
        AcquisitionSource source = AcquisitionSource.Craft)
    {
        return new PlanNode
        {
            ItemId = name.GetHashCode(),
            Name = name,
            MustBeHq = mustBeHq,
            CanBeHq = canBeHq,
            CanBuyFromMarket = true,
            CanCraft = source == AcquisitionSource.Craft,
            Source = source
        };
    }
}
