using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Desktop.Services;

public static class DesktopApplicationServices
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        var desktopLogStore = new DesktopLogStore();
        services.AddSingleton(desktopLogStore);

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.AddProvider(new DesktopLogProvider(desktopLogStore));
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        services.AddWinUiDesktopShell();
    }
}
