using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class CraftingPlanIdentityTests
{
    [Fact]
    public void FindNodeByNodeId_ResolvesExactOccurrenceWhenItemsRepeat()
    {
        var first = new PlanNode { ItemId = 100, Name = "First", NodeId = "first" };
        var second = new PlanNode { ItemId = 100, Name = "Second", NodeId = "second" };
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 1,
                    Name = "Root",
                    NodeId = "root",
                    Children = [first, second]
                }
            ]
        };

        Assert.Same(second, plan.FindNodeByNodeId("second"));
        Assert.Null(plan.FindNodeByNodeId("missing"));
    }
}
