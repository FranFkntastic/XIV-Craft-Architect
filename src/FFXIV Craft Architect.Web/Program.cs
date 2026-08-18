using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.Diagnostics;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
// HeadOutlet removed - add back if you need dynamic <PageTitle> support

builder.Services.AddSingleton<ClientRequestLog>();
// Register HttpClient for API calls with extended timeout for Universalis.
builder.Services.AddScoped(sp => CreateDiagnosticHttpClient(
    sp,
    new Uri(builder.HostEnvironment.BaseAddress),
    TimeSpan.FromSeconds(60)));

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
builder.Services.AddScoped<IArtisanService, ArtisanService>();
builder.Services.AddScoped<IndexedDbMarketCacheService>();
builder.Services.AddScoped<IMarketCacheService>(provider =>
    provider.GetRequiredService<IndexedDbMarketCacheService>());
builder.Services.AddScoped<MarketShoppingService>();
builder.Services.AddScoped<IMarketPriceEvaluationService, MarketPriceEvaluationService>();
builder.Services.AddScoped<IMarketPriceLadderAnalysisService, MarketPriceLadderAnalysisService>();
builder.Services.AddScoped<IMarketAnalysisExecutionService, MarketAnalysisExecutionService>();
builder.Services.AddScoped<IMarketEvidenceReconciliationService, MarketEvidenceReconciliationService>();
builder.Services.AddScoped<IProcurementRouteExecutionService, ProcurementRouteExecutionService>();
builder.Services.AddScoped<CommissionCostBasisResolver>();
builder.Services.AddScoped<CommissionPayrollService>();
builder.Services.AddWorkshopHostCraftAppraisal();
builder.Services.AddScoped<IWorkshopHostAcquisitionClient>(provider =>
    new WorkshopHostAcquisitionClient(provider.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<CraftAppraisalQuoteExportService>();

// Register Settings Service (Web implementation)
builder.Services.AddScoped<WebSettingsService>();
builder.Services.AddScoped<ISettingsService>(provider =>
    provider.GetRequiredService<WebSettingsService>());

// Register App State (singleton to persist across tab switches)
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<WebPlanPersistenceService>();
builder.Services.AddScoped<PackagedWorldDirectoryService>();
builder.Services.AddScoped<StartupInitializationService>();
builder.Services.AddScoped<CancellableOperationService>();
builder.Services.AddScoped<PlanLifecycleWorkflowService>();
builder.Services.AddScoped<NativePlanImportClassifier>();
builder.Services.AddScoped<GitHubIssueReportService>();
builder.Services.AddScoped<BrowserFileExportService>();
builder.Services.AddSingleton(new ProcurementRouteAvailability(
    bool.TryParse(builder.Configuration["ProcurementRoutes:GenerationEnabled"], out var routeGenerationEnabled) &&
    routeGenerationEnabled));
builder.Services.AddWorkerEngine(builder.Configuration);
builder.Services.AddScoped<MarketMafiosoAcquisitionWorkflowService>();
builder.Services.AddScoped<MarketMafiosoIntegrationState>();
builder.Services.AddScoped<TradePayrollDraftFactory>();
builder.Services.AddScoped<ITradePayrollDraftStore, IndexedDbTradePayrollDraftStore>();
builder.Services.AddScoped<TradePayrollPersistenceService>();
builder.Services.AddScoped<TradeOrderDraftFactory>();
builder.Services.AddScoped<TradeOrderPricingWorkflowService>();
builder.Services.AddScoped<TradeOrderLifecycleService>();
builder.Services.AddScoped<TradeCrafterProfileImportMapper>();
builder.Services.AddScoped<TradeCompanyProfilePackageService>();
builder.Services.AddScoped<TradeOperationsPersistenceService>();
builder.Services.AddScoped<TradeWorkspaceProfileResolver>();
builder.Services.AddScoped<TradeCompanyCollaborationClient>();
builder.Services.AddScoped<CompanyHubClient>();
builder.Services.AddScoped<TradeCompanyCollaborationService>();
builder.Services.AddScoped<TradeCommissionOperationsClient>();
builder.Services.AddScoped<TradeCommissionOperationsService>();
builder.Services.AddScoped(services => new CommissionBriefClient(
    services.GetRequiredService<ProfileHostClientOptions>(),
    CreateDiagnosticHttpClient(services, null, TimeSpan.FromSeconds(20))));
builder.Services.AddScoped<CommissionBriefLocalStateService>();
builder.Services.AddSingleton(new ProfileHostClientOptions(
    ResolveProfileHostBaseAddress(
        builder.Configuration["ProfileHost:BaseAddress"],
        builder.HostEnvironment.BaseAddress)));
builder.Services.AddScoped<ProfileHostClient>();
builder.Services.AddScoped<DiscordIdentityClient>();
builder.Services.AddScoped<ProfileSyncLocalStateService>();
builder.Services.AddScoped<HostedOrderProjectionStore>();
builder.Services.AddScoped<HostedOrderSyncCoordinator>();
builder.Services.AddScoped<TradeOrderArchiveSummaryStore>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, SettingsProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, PlansProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradeCompanyProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter, TradeCrafterProfileSyncAdapter>();
builder.Services.AddScoped<TradeOrderProfileSyncAdapter>();
builder.Services.AddScoped<IProfileSyncCollectionAdapter>(services =>
    services.GetRequiredService<TradeOrderProfileSyncAdapter>());
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
        CreateDiagnosticHttpClient(sp, options.BaseAddress, TimeSpan.FromSeconds(30)),
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

static string ResolveProfileHostBaseAddress(string? configuredBaseAddress, string hostBaseAddress)
{
    var candidate = string.IsNullOrWhiteSpace(configuredBaseAddress)
        ? new Uri(new Uri(hostBaseAddress), "api/").AbsoluteUri
        : configuredBaseAddress;
    return ProfileHostClient.NormalizeHostUrl(candidate);
}

static HttpClient CreateDiagnosticHttpClient(
    IServiceProvider services,
    Uri? baseAddress,
    TimeSpan timeout)
{
    var handler = new DiagnosticRequestHandler(
        services.GetRequiredService<ClientRequestLog>(),
        services.GetRequiredService<ILogger<DiagnosticRequestHandler>>())
    {
        InnerHandler = new HttpClientHandler()
    };
    return new HttpClient(handler)
    {
        BaseAddress = baseAddress,
        Timeout = timeout
    };
}
