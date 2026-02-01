using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // HTTP clients for API services
        services.AddHttpClient<GarlandService>();
        services.AddHttpClient<UniversalisService>();
        services.AddHttpClient<SettingsService>();

        // Services
        services.AddSingleton<GarlandService>();
        services.AddSingleton<UniversalisService>();
        services.AddSingleton<SettingsService>();

        // Windows
        services.AddTransient<MainWindow>();
    }
}
