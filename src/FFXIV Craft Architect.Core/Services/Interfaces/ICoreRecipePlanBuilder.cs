using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface ICoreRecipePlanBuilder
{
    Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default);

    Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default);
}
