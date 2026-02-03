using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FFXIVCraftArchitect.Web;
using FFXIVCraftArchitect.Core.Services;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head");

// Register HttpClient for API calls
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register MudBlazor
builder.Services.AddMudServices();

// Register Core Services
builder.Services.AddScoped<GarlandService>();
builder.Services.AddScoped<UniversalisService>();
builder.Services.AddScoped<RecipeCalculationService>();
builder.Services.AddScoped<MarketShoppingService>();

await builder.Build().RunAsync();
