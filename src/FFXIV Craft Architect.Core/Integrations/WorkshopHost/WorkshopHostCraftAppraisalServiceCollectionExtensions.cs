using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public static class WorkshopHostCraftAppraisalServiceCollectionExtensions
{
    public static IServiceCollection AddWorkshopHostCraftAppraisal(this IServiceCollection services)
    {
        services.TryAddScoped<ICoreRecipePlanBuilder, CoreRecipeCalculationPlanBuilder>();
        services.TryAddScoped<ICraftAppraisalService, CraftAppraisalService>();
        return services;
    }
}
