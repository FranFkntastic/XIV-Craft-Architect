using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Desktop.Services;

namespace FFXIV_Craft_Architect.Tests;

[Collection(DesktopTestCollection.Name)]
[Trait(TestTraits.Surface, TestTraits.Desktop)]
public class DesktopProjectItemDraftServiceTests
{
    [Fact]
    public void AddTarget_PublishesProjectItemDraftWithoutCreatingRecipePlan()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var drafts = new DesktopProjectItemDraftService();

        var result = drafts.AddTarget(session, 5107, "Cobalt Plate", "12", mustBeHq: true);

        Assert.True(result.Changed);
        Assert.Null(session.ActivePlan);
        var projectItem = Assert.Single(session.ProjectItems);
        Assert.Equal(5107, projectItem.Id);
        Assert.Equal("Cobalt Plate", projectItem.Name);
        Assert.Equal(12, projectItem.Quantity);
        Assert.True(projectItem.MustBeHq);
    }

    [Fact]
    public void EditingDraftAfterBuiltPlan_ClearsStaleRecipePlan()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var drafts = new DesktopProjectItemDraftService();
        var builtPlan = new FFXIV_Craft_Architect.Core.Models.CraftingPlan
        {
            Name = "Built Plan",
            RootItems =
            [
                new FFXIV_Craft_Architect.Core.Models.PlanNode
                {
                    ItemId = 5107,
                    Name = "Cobalt Plate",
                    Quantity = 1
                }
            ]
        };

        session.ActivatePlan(
            builtPlan,
            [
                new FFXIV_Craft_Architect.Core.Models.ProjectItem
                {
                    Id = 5107,
                    Name = "Cobalt Plate",
                    Quantity = 1
                }
            ],
            new FFXIV_Craft_Architect.Core.Models.CraftSessionActiveContext(
                "North America",
                "Aether",
                null,
                FFXIV_Craft_Architect.Core.Models.MarketFetchScope.SelectedDataCenter),
            "built plan");

        var result = drafts.AdjustRootQuantity(session, 5107, 100);

        Assert.True(result.Changed);
        Assert.Null(session.ActivePlan);
        Assert.Equal(101, Assert.Single(session.ProjectItems).Quantity);
    }

    [Fact]
    public void SearchKnownItems_ReturnsLocalCatalogMatches()
    {
        var drafts = new DesktopProjectItemDraftService();

        var results = drafts.SearchKnownItems("cobalt");

        Assert.Contains(results, result => result is { ItemId: 5107, Name: "Cobalt Plate" });
        Assert.Contains(results, result => result is { ItemId: 5094, Name: "Cobalt Rivets" });
    }

    [Fact]
    public void AddTarget_DoesNotFabricateUnknownItemIds()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var drafts = new DesktopProjectItemDraftService();

        var result = drafts.AddTarget(session, "Garland Steel", "12", mustBeHq: true);

        Assert.False(result.Changed);
        Assert.Empty(session.ProjectItems);
        Assert.Contains("Search and add a Garland result", result.Message);
        Assert.Empty(drafts.SearchKnownItems("garland"));
    }

    [Fact]
    public void SearchKnownItems_IgnoresBlankQuery()
    {
        var drafts = new DesktopProjectItemDraftService();

        Assert.Empty(drafts.SearchKnownItems(" "));
    }
}
