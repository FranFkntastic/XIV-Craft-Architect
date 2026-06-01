using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipeResolutionService
{
    RecipeResolutionResult Resolve(PlanNode node, GarlandItem? itemData);
}
