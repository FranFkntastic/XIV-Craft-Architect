using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class NativePlanImportService
{
    private readonly RecipeCalculationService _recipeService;

    public NativePlanImportService(RecipeCalculationService recipeService)
    {
        _recipeService = recipeService;
    }

    public NativePlanImportResult Import(string json)
    {
        var storedPlan = TryDeserializeStoredPlan(json);
        if (storedPlan != null)
        {
            return new NativePlanImportResult(
                storedPlan.ProjectItems.Select(ToImportItem).ToList(),
                Plan: null,
                StoredPlan: storedPlan);
        }

        var plan = _recipeService.DeserializePlan(json)
            ?? throw new InvalidOperationException("Could not parse the craftplan JSON.");
        return new NativePlanImportResult(
            plan.RootItems.Select(ToImportItem).ToList(),
            plan,
            StoredPlan: null);
    }

    private static StoredPlan? TryDeserializeStoredPlan(string json)
    {
        try
        {
            var storedPlan = JsonSerializer.Deserialize<StoredPlan>(json);
            if (storedPlan == null)
            {
                return null;
            }

            return storedPlan.ProjectItems.Count > 0 ||
                   !string.IsNullOrWhiteSpace(storedPlan.PlanJson) ||
                   !string.IsNullOrWhiteSpace(storedPlan.MarketIntelligenceJson)
                ? storedPlan
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NativePlanImportItem ToImportItem(StoredProjectItem item) =>
        new(item.Id, item.Name, item.IconId, item.Quantity, item.MustBeHq);

    private static NativePlanImportItem ToImportItem(PlanNode node) =>
        new(node.ItemId, node.Name, node.IconId, node.Quantity, node.MustBeHq);
}

public sealed record NativePlanImportResult(
    IReadOnlyList<NativePlanImportItem> Items,
    CraftingPlan? Plan,
    StoredPlan? StoredPlan);

public sealed record NativePlanImportItem(
    int Id,
    string Name,
    int IconId,
    int Quantity,
    bool MustBeHq);
