using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Web.Services;

public static class WorkerEngineServiceCollectionExtensions
{
    public const string ExecutionEnabledConfigurationKey = "EngineRewrite:ExecutionEnabled";

    public static IServiceCollection AddWorkerEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(new CraftArchitectEngineCapability(
            configuration.GetValue<bool>(ExecutionEnabledConfigurationKey)));
        services.TryAddScoped<IReferenceEngineSemanticSnapshotProvider, ReferenceEngineSemanticSnapshotProvider>();
        services.AddScoped<CraftArchitectEngineHost>();
        services.AddScoped<WorkerProjectionStore>();
        services.AddScoped<WorkerSessionCoordinator>();
        return services;
    }
}
