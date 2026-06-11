using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FFXIV_Craft_Architect.Web;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
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
builder.Services.AddScoped<IMarketCacheService, IndexedDbMarketCacheService>();
builder.Services.AddScoped<MarketShoppingService>();
builder.Services.AddScoped<IMarketPriceEvaluationService, MarketPriceEvaluationService>();
builder.Services.AddScoped<IMarketPriceLadderAnalysisService, MarketPriceLadderAnalysisService>();
builder.Services.AddScoped<IMarketAnalysisExecutionService, MarketAnalysisExecutionService>();
builder.Services.AddScoped<IProcurementRouteExecutionService, ProcurementRouteExecutionService>();
builder.Services.AddScoped<IndexedDbMarketCacheService>();
builder.Services.AddScoped<CommissionCostBasisResolver>();
builder.Services.AddScoped<CommissionPayrollService>();

// Register Settings Service (Web implementation)
builder.Services.AddScoped<ISettingsService, WebSettingsService>();

// Register App State (singleton to persist across tab switches)
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<AcquisitionDecisionService>();
builder.Services.AddScoped<StoredPlanSnapshotBuilder>();
builder.Services.AddScoped<PlanSessionLoadService>();
builder.Services.AddScoped<WebPlanPersistenceService>();
builder.Services.AddScoped<StartupInitializationService>();
builder.Services.AddScoped<CancellableOperationService>();
builder.Services.AddScoped<RecipePlannerCommandService>();
builder.Services.AddScoped<IRecipeBuildDiagnosticCommandRunner, RecipePlannerDiagnosticCommandRunner>();
builder.Services.AddScoped<RecipeBuildDiagnosticService>();
builder.Services.AddScoped<MarketAnalysisWorkflowService>();
builder.Services.AddScoped<IMarketAnalysisAutoRunner, MarketAnalysisAutoRunner>();
builder.Services.AddScoped<MarketAnalysisDiagnosticDumpService>();
builder.Services.AddScoped<ProcurementWorkflowService>();
builder.Services.AddScoped<AcquisitionEvaluationWorkflowService>();
builder.Services.AddScoped<TradePayrollDraftFactory>();

// Register IndexedDB service for browser persistence
builder.Services.AddScoped<IndexedDbService>();

await builder.Build().RunAsync();
