using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Desktop.Services;

public interface IDesktopRecipePlanBuilder
{
    Task<CraftingPlan> BuildPlanAsync(
        IReadOnlyList<(int ItemId, string Name, int Quantity, bool MustBeHq)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default);
}
