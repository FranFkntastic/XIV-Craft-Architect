using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public static class WorkshopHostCraftAppraisalServiceCollectionExtensions
{
    public static IServiceCollection AddWorkshopHostCraftAppraisal(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddScoped<RecipeCalculationService>();
        services.TryAddScoped<IVendorCacheService, VendorCacheService>();
        services.TryAddScoped<IRecipeResolutionService, RecipeResolutionService>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(GarlandService)))
        {
            services.AddHttpClient<GarlandService>();
        }

        services.TryAddScoped<IGarlandService>(provider => provider.GetRequiredService<GarlandService>());
        services.TryAddScoped<ICoreRecipePlanBuilder, CoreRecipeCalculationPlanBuilder>();
        services.TryAddScoped<ICraftAppraisalService, CraftAppraisalService>();
        return services;
    }
}
