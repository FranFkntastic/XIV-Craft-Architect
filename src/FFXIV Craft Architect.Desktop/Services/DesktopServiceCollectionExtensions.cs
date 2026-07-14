using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Desktop.ViewModels;
using FFXIV_Craft_Architect.Desktop.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Desktop.Services;

public static class DesktopServiceCollectionExtensions
{
    public static IServiceCollection AddWinUiDesktopShell(this IServiceCollection services)
    {
        services.AddSingleton<ICraftSessionDispatcher, WinUiCraftSessionDispatcher>();
        services.AddSingleton<CraftSessionState>();
        services.AddSingleton<CraftOperationState>();
        services.AddSingleton<ICraftOperationCoordinator, CraftOperationCoordinator>();
        services.AddSingleton<CoreStoredPlanSnapshotBuilder>();
        services.AddSingleton<CorePlanSessionLoadService>();
        services.AddSingleton(CoreStoredPlanStoreOptions.CreateDefault());
        services.AddSingleton<ICoreStoredPlanStore, FileCoreStoredPlanStore>();
        services.AddSingleton<CoreSessionPersistenceService>();
        services.AddSingleton<CoreAcquisitionDecisionService>();
        services.AddSingleton(_ => new UniversalisService(new HttpClient()));
        services.AddSingleton<IUniversalisService>(provider => provider.GetRequiredService<UniversalisService>());
        services.AddSingleton<DesktopJsonMarketCacheService>();
        services.AddSingleton<IMarketCacheService>(sp => sp.GetRequiredService<DesktopJsonMarketCacheService>());
        services.AddSingleton<IMarketPriceEvaluationService, MarketPriceEvaluationService>();
        services.AddSingleton<IMarketPriceLadderAnalysisService, MarketPriceLadderAnalysisService>();
        services.AddSingleton<IMarketAnalysisExecutionService, MarketAnalysisExecutionService>();
        services.AddSingleton<IMarketEvidenceReconciliationService, MarketEvidenceReconciliationService>();
        services.AddSingleton<GarlandService>(sp => new GarlandService(
            new HttpClient(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<GarlandService>>()));
        services.AddSingleton<IGarlandService>(sp => sp.GetRequiredService<GarlandService>());
        services.AddSingleton<IVendorCacheService, VendorCacheService>();
        services.AddSingleton<RecipeCalculationService>();
        services.AddWorkshopHostCraftAppraisal();
        services.AddSingleton<IDesktopRecipePlanBuilder>(sp =>
            IsDeterministicSmokeBuildEnabled()
                ? new DesktopSmokeRecipePlanBuilder()
                : new DesktopRecipeCalculationPlanBuilder(sp.GetRequiredService<RecipeCalculationService>()));
        services.AddSingleton<IDesktopClipboardService, WinUiClipboardService>();
        services.AddSingleton<DesktopWindowHandleProvider>();
        services.AddSingleton<IDesktopWindowHandleProvider>(sp => sp.GetRequiredService<DesktopWindowHandleProvider>());
        services.AddSingleton<IDesktopFileDialogService, WinUiFileDialogService>();
        services.AddSingleton<DesktopSettingsStore>();
        services.AddSingleton<DesktopLocalInfrastructureService>();
        services.AddSingleton<DesktopActivityLogStore>();
        services.AddSingleton<DesktopMarketRefreshQueueService>();
        services.AddSingleton<DesktopProjectItemDraftService>();
        services.AddSingleton<DesktopLogFileShellService>();
        services.AddSingleton<IDesktopLogViewerLauncher, DesktopLogViewerLauncher>();
        services.AddSingleton<DesktopShellViewModel>();
        services.AddTransient<DesktopLogViewerViewModel>();
        services.AddTransient<DesktopLogViewerWindow>();
        services.AddSingleton<MainWindow>();
        return services;
    }

    private static bool IsDeterministicSmokeBuildEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD"),
            "1",
            StringComparison.Ordinal);
}
