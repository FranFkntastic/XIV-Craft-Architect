using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    
    // Log file path - same directory as the executable
    public static readonly string LogFilePath = Path.Combine(
        AppContext.BaseDirectory, 
        "debug.log"
    );

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Clear old log on startup
        try { File.Delete(LogFilePath); } catch { }
        
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        // Initialize theme first (before showing window) so accent colors are available
        var themeService = Services.GetRequiredService<ThemeService>();
        themeService.RefreshFromSettings();
        
        // Initialize world status cache (fire and forget - don't block startup)
        var worldStatusService = Services.GetRequiredService<WorldStatusService>();
        _ = Task.Run(async () => 
        {
            if (worldStatusService.NeedsRefresh())
            {
                await worldStatusService.RefreshStatusAsync();
            }
        });
        
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // Save market cache to disk before exiting
        try
        {
            var cacheService = Services.GetRequiredService<MarketCacheService>();
            cacheService.SaveCacheToDiskAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // Log to debug log if possible
            try
            {
                File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss}] [Error] Failed to save market cache: {ex}\n");
            }
            catch { }
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging - writes to debug.log
        services.AddLogging(builder =>
        {
            builder.AddProvider(new FileLoggerProvider(LogFilePath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // HTTP clients for API services
        services.AddHttpClient<GarlandService>();
        services.AddHttpClient<UniversalisService>();
        services.AddHttpClient<ArtisanService>();
        
        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GarlandService>();
        services.AddSingleton<UniversalisService>();
        services.AddSingleton<ItemCacheService>();
        services.AddSingleton<MarketCacheService>();        // Global market data cache
        services.AddSingleton<RecommendationCsvService>(); // Plan-specific recommendations
        services.AddSingleton<RecipeCalculationService>();
        services.AddSingleton<PlanPersistenceService>();
        services.AddSingleton<TeamcraftService>();
        services.AddSingleton<ArtisanService>();
        services.AddSingleton<PriceCheckService>();
        services.AddSingleton<MarketShoppingService>();
        services.AddSingleton<WorldStatusService>();

        // Windows
        services.AddTransient<MainWindow>();
        services.AddTransient<OptionsWindow>();
    }
}

/// <summary>
/// Simple file logger provider - writes logs to a text file
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_filePath, _lock);
    }

    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly object _lock;

    public FileLogger(string filePath, object lockObj)
    {
        _filePath = filePath;
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = $"[{DateTime.Now:HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
        if (exception != null)
        {
            message += $"\n{exception}";
        }

        lock (_lock)
        {
            File.AppendAllText(_filePath, message + Environment.NewLine);
        }
    }
}
