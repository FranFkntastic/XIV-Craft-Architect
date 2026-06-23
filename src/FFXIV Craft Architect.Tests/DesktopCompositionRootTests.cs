using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Desktop.Services;
using FFXIV_Craft_Architect.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public class DesktopCompositionRootTests
{
    [Fact]
    public void BuildServiceProvider_ResolvesDesktopInfrastructureServices()
    {
        var services = new ServiceCollection();
        DesktopApplicationServices.ConfigureServices(services);
        services.AddSingleton<ICraftSessionDispatcher, ImmediateCraftSessionDispatcher>();
        using var provider = services.BuildServiceProvider();

        var session = provider.GetRequiredService<CraftSessionState>();
        var operationState = provider.GetRequiredService<CraftOperationState>();
        var marketCache = provider.GetRequiredService<IMarketCacheService>();
        var localInfrastructure = provider.GetRequiredService<DesktopLocalInfrastructureService>();
        var fileDialogs = provider.GetRequiredService<IDesktopFileDialogService>();
        var clipboard = provider.GetRequiredService<IDesktopClipboardService>();
        var recipePlanBuilder = provider.GetRequiredService<IDesktopRecipePlanBuilder>();

        Assert.NotNull(session);
        Assert.NotNull(operationState);
        Assert.IsType<DesktopJsonMarketCacheService>(marketCache);
        Assert.NotNull(localInfrastructure);
        Assert.NotNull(fileDialogs);
        Assert.NotNull(clipboard);
        Assert.IsType<DesktopRecipeCalculationPlanBuilder>(recipePlanBuilder);
    }

    [Fact]
    public void BuildServiceProvider_CanResolveDeterministicSmokeRecipeBuilder()
    {
        var previous = Environment.GetEnvironmentVariable("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD");
        Environment.SetEnvironmentVariable("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD", "1");

        try
        {
            var services = new ServiceCollection();
            DesktopApplicationServices.ConfigureServices(services);
            services.AddSingleton<ICraftSessionDispatcher, ImmediateCraftSessionDispatcher>();
            using var provider = services.BuildServiceProvider();

            var recipePlanBuilder = provider.GetRequiredService<IDesktopRecipePlanBuilder>();

            Assert.IsType<DesktopSmokeRecipePlanBuilder>(recipePlanBuilder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD", previous);
        }
    }
}
