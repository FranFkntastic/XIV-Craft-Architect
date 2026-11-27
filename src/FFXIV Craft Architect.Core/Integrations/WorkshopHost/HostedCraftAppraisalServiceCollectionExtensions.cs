using FFXIV_Craft_Architect.Core.Integrations.XivData;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public static class HostedCraftAppraisalServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything a host needs to serve craft appraisal quotes, plan snapshots and
    /// item lookups in-process: <see cref="IHostedCraftAppraisalCoordinator"/>,
    /// <see cref="CraftAppraisalPlanStore"/>, <see cref="IXivItemDataProvider"/>, the
    /// normalized <see cref="CraftAppraisalApiOptions"/>, the memory cache, and a file-backed
    /// <see cref="IMarketCacheService"/> rooted at <see cref="CraftAppraisalApiOptions.CacheDirectory"/>.
    /// The host maps its own HTTP routes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Appraisal settings; normalized against <paramref name="contentRoot"/> and validated here.</param>
    /// <param name="contentRoot">Directory that empty cache and plan directories resolve under.</param>
    public static IServiceCollection AddHostedCraftAppraisal(
        this IServiceCollection services,
        CraftAppraisalApiOptions options,
        string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var resolved = options.Normalize(contentRoot);
        resolved.Validate();

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IGarlandService)))
        {
            services.AddHttpClient<IGarlandService, GarlandService>();
        }

        services.AddSingleton<IXivItemDataProvider, GarlandXivItemDataProvider>();
        services.AddSingleton(resolved);
        services.AddMemoryCache();
        services.AddWorkshopHostCraftAppraisal();
        services.AddSingleton<IMarketCacheService>(provider =>
            new JsonFileMarketCacheService(
                provider.GetRequiredService<UniversalisService>(),
                resolved.CacheDirectory));
        services.AddSingleton<CraftAppraisalPlanStore>();
        services.AddSingleton<IHostedCraftAppraisalCoordinator, HostedCraftAppraisalCoordinator>();
        return services;
    }
}
