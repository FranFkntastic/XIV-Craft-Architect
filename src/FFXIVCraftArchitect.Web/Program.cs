using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FFXIVCraftArchitect.Web;
using FFXIVCraftArchitect.Web.Services;
using FFXIVCraftArchitect.Core.Services;
using FFXIVCraftArchitect.Core.Services.Interfaces;
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
builder.Services.AddScoped<UniversalisService>();
builder.Services.AddScoped<RecipeCalculationService>();
builder.Services.AddScoped<IMarketCacheService, IndexedDbMarketCacheService>();
builder.Services.AddScoped<MarketShoppingService>();
builder.Services.AddScoped<ProcurementAnalysisService>();
builder.Services.AddScoped<IndexedDbMarketCacheService>();

// Register Settings Service (Web implementation)
builder.Services.AddScoped<ISettingsService, WebSettingsService>();

// Register App State (singleton to persist across tab switches)
builder.Services.AddSingleton<AppState>();

// Register IndexedDB service for browser persistence
builder.Services.AddScoped<IndexedDbService>();

await builder.Build().RunAsync();
