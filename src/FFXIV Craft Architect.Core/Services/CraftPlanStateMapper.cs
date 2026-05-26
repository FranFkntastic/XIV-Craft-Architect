using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CraftPlanStateMapper
{
    public static List<ProjectItem> GetRootProjectItems(CraftingPlan? plan)
    {
        return plan?.RootItems
            .Select(root => new ProjectItem
            {
                Id = root.ItemId,
                Name = root.Name,
                IconId = root.IconId,
                Quantity = root.Quantity,
                MustBeHq = root.MustBeHq
            })
            .ToList() ?? new List<ProjectItem>();
    }
}
