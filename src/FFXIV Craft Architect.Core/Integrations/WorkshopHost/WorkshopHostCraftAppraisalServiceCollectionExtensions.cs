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
        services.TryAddScoped<ISettingsService, SettingsService>();
        services.TryAddScoped<IVendorCacheService, VendorCacheService>();
        services.TryAddScoped<IRecipeResolutionService, RecipeResolutionService>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(GarlandService)))
        {
            services.AddHttpClient<GarlandService>();
        }

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(UniversalisService)))
        {
            services.AddHttpClient<UniversalisService>();
        }

        services.TryAddScoped<IGarlandService>(provider => provider.GetRequiredService<GarlandService>());
        services.TryAddScoped<IUniversalisService>(provider => provider.GetRequiredService<UniversalisService>());
        services.TryAddScoped<IMarketCacheService>(provider =>
        {
            var universalis = provider.GetRequiredService<UniversalisService>();
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIV_Craft_Architect",
                "WorkshopHostCache");
            return new JsonFileMarketCacheService(universalis, cacheRoot);
        });
        services.TryAddScoped<ICoreRecipePlanBuilder, CoreRecipeCalculationPlanBuilder>();
        services.TryAddScoped<ICraftAppraisalPriceEvidenceService, CraftAppraisalPriceEvidenceService>();
        services.TryAddScoped<ICraftAppraisalService, CraftAppraisalService>();
        return services;
    }
}
