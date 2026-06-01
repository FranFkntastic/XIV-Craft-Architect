using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CraftSessionServiceCollectionExtensions
{
    public static IServiceCollection AddCraftSessionFoundation(this IServiceCollection services)
    {
        services.AddSingleton<ICraftSessionDispatcher, ImmediateCraftSessionDispatcher>();
        services.AddSingleton<CraftSessionState>();
        services.AddSingleton<CraftOperationState>();
        services.AddSingleton<ICraftOperationCoordinator, CraftOperationCoordinator>();
        services.AddSingleton<CoreStoredPlanSnapshotBuilder>();
        services.AddSingleton<CorePlanSessionLoadService>();
        services.AddSingleton(CoreStoredPlanStoreOptions.CreateDefault());
        services.AddSingleton<ICoreStoredPlanStore, FileCoreStoredPlanStore>();
        services.AddSingleton<CoreSessionPersistenceService>();
        return services;
    }
}
