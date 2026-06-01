using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipeDemandProjectionService
{
    RecipeDemandProjection Build(CraftingPlan? plan, RecipeOperationSnapshot? snapshot);
}
