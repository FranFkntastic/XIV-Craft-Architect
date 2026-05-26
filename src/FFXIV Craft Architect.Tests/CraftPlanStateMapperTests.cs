using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CraftPlanStateMapperTests
{
    [Fact]
    public void GetRootProjectItems_MapsRootNodesOnly()
    {
        var child = new PlanNode
        {
            ItemId = 2,
            Name = "Child",
            IconId = 20,
            Quantity = 99,
            MustBeHq = true
        };

        var root = new PlanNode
        {
            ItemId = 1,
            Name = "Root",
            IconId = 10,
            Quantity = 3,
            MustBeHq = false,
            Children = { child }
        };

        var plan = new CraftingPlan
        {
            RootItems = { root }
        };

        var result = CraftPlanStateMapper.GetRootProjectItems(plan);

        var item = Assert.Single(result);
        Assert.Equal(1, item.Id);
        Assert.Equal("Root", item.Name);
        Assert.Equal(10, item.IconId);
        Assert.Equal(3, item.Quantity);
        Assert.False(item.MustBeHq);
    }
}
