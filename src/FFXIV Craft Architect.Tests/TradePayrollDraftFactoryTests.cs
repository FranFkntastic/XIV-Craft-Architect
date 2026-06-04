using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollDraftFactoryTests
{
    [Fact]
    public void CreateFromCurrentPlan_IncludesRootCraftedItems()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(
            new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Finished Commission",
                        Quantity = 2,
                        MustBeHq = true,
                        Children =
                        [
                            new PlanNode
                            {
                                ItemId = 200,
                                Name = "Base Material",
                                Quantity = 6
                            }
                        ]
                    }
                ]
            });

        var factory = new TradePayrollDraftFactory(
            new CommissionCostBasisResolver(),
            new CommissionPayrollService());

        var result = factory.CreateFromCurrentPlan(appState);

        Assert.True(result.CanCreate);
        var item = Assert.Single(result.Draft!.Source.CraftedItems);
        Assert.Equal(100, item.Id);
        Assert.Equal("Finished Commission", item.Name);
        Assert.Equal(2, item.Quantity);
        Assert.True(item.MustBeHq);
        Assert.DoesNotContain(result.Draft.Source.CraftedItems, item => item.Id == 200);
    }
}
