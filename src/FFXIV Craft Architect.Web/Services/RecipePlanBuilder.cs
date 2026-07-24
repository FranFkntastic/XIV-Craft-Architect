using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IRecipePlanBuilder
{
    Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default,
        IRecipePlanBuildDiagnosticRecorder? diagnostics = null);

    Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default);
}

public sealed class RecipeCalculationPlanBuilder(
    RecipeCalculationService recipeCalculationService) : IRecipePlanBuilder
{
    public Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default,
        IRecipePlanBuildDiagnosticRecorder? diagnostics = null) =>
        recipeCalculationService.BuildPlanAsync(
            targetItems,
            dataCenter,
            world,
            ct,
            diagnostics);

    public Task FetchVendorPricesAsync(
        CraftingPlan plan,
        CancellationToken ct = default) =>
        recipeCalculationService.FetchVendorPricesAsync(plan, ct);
}

public enum RecipePlannerCommandMessageLevel
{
    Info,
    Success,
    Warning,
    Error
}
