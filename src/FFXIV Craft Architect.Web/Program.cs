using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
// HeadOutlet removed - add back if you need dynamic <PageTitle> support

// Register HttpClient for API calls with extended timeout for Universalis
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout = TimeSpan.FromSeconds(60) // Extended timeout for large bulk requests
});

// Register MudBlazor
builder.Services.AddMudServices();

// Register Core Services
builder.Services.AddScoped<GarlandService>();
builder.Services.AddScoped<IGarlandService>(sp => sp.GetRequiredService<GarlandService>());
builder.Services.AddScoped<UniversalisService>();
builder.Services.AddScoped<IUniversalisService>(sp => sp.GetRequiredService<UniversalisService>());
builder.Services.AddScoped<RecipeCalculationService>();
builder.Services.AddScoped<IRecipePlanBuilder, RecipeCalculationPlanBuilder>();
builder.Services.AddScoped<IVendorCacheService, VendorCacheService>();
builder.Services.AddScoped<ITeamcraftRecipeService, TeamcraftRecipeService>();
builder.Services.AddScoped<IRecipeResolutionService, RecipeResolutionService>();
builder.Services.AddScoped<IRecipeOperationSnapshotService, RecipeOperationSnapshotService>();
builder.Services.AddScoped<IRecipeOperationSnapshotLifecycleService, RecipeOperationSnapshotLifecycleService>();
builder.Services.AddScoped<IRecipeDemandProjectionService, RecipeDemandProjectionService>();
builder.Services.AddScoped<IRecipeDemandProjectionParityService, RecipeDemandProjectionParityService>();
builder.Services.AddScoped<IRecipeLayerWorkflowService, RecipeLayerWorkflowService>();
builder.Services.AddScoped<IArtisanService, ArtisanService>();
builder.Services.AddScoped<IndexedDbMarketCacheService>();
builder.Services.AddScoped<IMarketCacheService>(provider =>
    provider.GetRequiredService<IndexedDbMarketCacheService>());
builder.Services.AddScoped<MarketShoppingService>();
builder.Services.AddScoped<JointAcquisitionRouteOptimizationService>();
builder.Services.AddScoped<IMarketPriceEvaluationService, MarketPriceEvaluationService>();
builder.Services.AddScoped<IMarketPriceLadderAnalysisService, MarketPriceLadderAnalysisService>();
builder.Services.AddScoped<IMarketAnalysisExecutionService, MarketAnalysisExecutionService>();
builder.Services.AddScoped<IMarketEvidenceReconciliationService, MarketEvidenceReconciliationService>();
builder.Services.AddScoped<IProcurementRouteExecutionService, ProcurementRouteExecutionService>();
builder.Services.AddScoped<CommissionCostBasisResolver>();
builder.Services.AddScoped<CommissionPayrollService>();
builder.Services.AddScoped<TradeLaborStandardCalibrationService>();
builder.Services.AddScoped<ITradeLaborBenchmarkPlanBuilder, TradeLaborBenchmarkPlanBuilder>();
builder.Services.AddWorkshopHostCraftAppraisal();
builder.Services.AddScoped<IWorkshopHostAcquisitionClient>(provider =>
    new WorkshopHostAcquisitionClient(provider.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<CraftAppraisalQuoteExportService>();

// Register Settings Service (Web implementation)
builder.Services.AddScoped<ISettingsService, WebSettingsService>();

// Register App State (singleton to persist across tab switches)
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<AcquisitionDecisionService>();
builder.Services.AddScoped<StoredPlanSnapshotBuilder>();
builder.Services.AddScoped<PlanSessionLoadService>();
builder.Services.AddScoped<WebPlanPersistenceService>();
builder.Services.AddScoped<IMarketAnalysisPersistence, IndexedDbMarketAnalysisPersistence>();
builder.Services.AddScoped<MarketEvidenceHydrationService>();
builder.Services.AddScoped<PackagedWorldDirectoryService>();
builder.Services.AddScoped<StartupInitializationService>();
builder.Services.AddScoped<CancellableOperationService>();
builder.Services.AddScoped<RecipePlannerCommandService>();
builder.Services.AddScoped<NativePlanImportClassifier>();
builder.Services.AddScoped<IRecipeBuildDiagnosticCommandRunner, RecipePlannerDiagnosticCommandRunner>();
builder.Services.AddScoped<RecipeBuildDiagnosticService>();
builder.Services.AddScoped<MarketAnalysisWorkflowService>();
builder.Services.AddScoped<MarketAnalysisSubsetRefreshService>();
builder.Services.AddScoped<MarketAnalysisItemRefreshService>();
builder.Services.AddScoped<IMarketAnalysisAutoRunner, MarketAnalysisAutoRunner>();
builder.Services.AddScoped<MarketAnalysisDiagnosticDumpService>();
builder.Services.AddScoped<DiagnosticSnapshotBundleService>();
builder.Services.AddScoped<AcquisitionEvaluationItemDiagnosticDumpService>();
builder.Services.AddScoped<AcquisitionDiagnosticSelectionService>();
builder.Services.AddScoped<GitHubIssueReportService>();
builder.Services.AddScoped<BrowserFileExportService>();
builder.Services.AddScoped<ProcurementWorkflowService>();
builder.Services.AddScoped<IProcurementWorkflowService>(provider =>
    provider.GetRequiredService<ProcurementWorkflowService>());
builder.Services.AddScoped<ProcurementRouteReconciliationService>();
builder.Services.AddScoped<MarketMafiosoAcquisitionWorkflowService>();
builder.Services.AddScoped<MarketMafiosoIntegrationState>();
builder.Services.AddScoped<AcquisitionEvaluationWorkflowService>();
builder.Services.AddScoped<AcquisitionSourceChangeImpactService>();
builder.Services.AddScoped<TradePayrollDraftFactory>();
builder.Services.AddScoped<ITradePayrollDraftStore, IndexedDbTradePayrollDraftStore>();
builder.Services.AddScoped<TradePayrollPersistenceService>();
builder.Services.AddScoped<TradeOrderDraftFactory>();
builder.Services.AddScoped<TradeOrderCraftPlanBuildService>();
builder.Services.AddScoped<TradeOrderPricingWorkflowService>();
builder.Services.AddScoped<TradeCrafterProfileImportMapper>();
builder.Services.AddScoped<TradeCompanyProfilePackageService>();
builder.Services.AddScoped<TradeOperationsPersistenceService>();
builder.Services.AddScoped<TradeLaborBenchmarkCalibrationWorkflowService>();
builder.Services.AddScoped<ProfileHostClient>();
builder.Services.AddScoped<ProfileSyncLocalStateService>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, SettingsProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, PlansProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradeCompanyProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradeCrafterProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradeOrderProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradePayrollDraftProfileSyncAdapter>();
builder.Services.AddScoped<ProfileSyncService>();
builder.Services.AddScoped(_ => new LodestoneLookupClientOptions(ResolveLodestoneLookupBaseAddress(
    builder.Configuration["LodestoneLookup:BaseAddress"],
    builder.HostEnvironment.BaseAddress)));
builder.Services.AddScoped<ILodestoneCrafterLookupService>(sp =>
{
    var options = sp.GetRequiredService<LodestoneLookupClientOptions>();
    var logger = sp.GetRequiredService<ILogger<HttpLodestoneCrafterLookupService>>();
    return new HttpLodestoneCrafterLookupService(
        new HttpClient
        {
            BaseAddress = options.BaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        },
        options,
        logger);
});

// Register IndexedDB service for browser persistence
builder.Services.AddScoped<IndexedDbService>();

await builder.Build().RunAsync();

static Uri ResolveLodestoneLookupBaseAddress(string? configuredBaseAddress, string hostBaseAddress)
{
    if (string.IsNullOrWhiteSpace(configuredBaseAddress))
    {
        return new Uri("http://localhost:5128/");
    }

    var trimmed = configuredBaseAddress.Trim();
    return Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri)
        ? absoluteUri
        : new Uri(new Uri(hostBaseAddress), trimmed);
}
