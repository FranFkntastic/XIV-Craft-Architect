using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Web.Services;

public static class ExperimentalEngineServiceCollectionExtensions
{
    public const string ExecutionEnabledConfigurationKey = "EngineRewrite:ExecutionEnabled";

    public static IServiceCollection AddExperimentalProcurementEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(new ExperimentalProcurementEngineCapability(
            configuration.GetValue<bool>(ExecutionEnabledConfigurationKey)));
        services.TryAddScoped<IReferenceEngineSemanticSnapshotProvider, ReferenceEngineSemanticSnapshotProvider>();
        services.AddScoped<CraftArchitectEngineHost>();
        services.AddScoped<IExperimentalProcurementEngineWorkflow, ExperimentalProcurementEngineWorkflow>();
        return services;
    }
}
