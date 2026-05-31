using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipeDemandProjectionParityService
{
    RecipeDemandParityReport Compare(CraftingPlan? plan, RecipeOperationSnapshot? snapshot);
}
