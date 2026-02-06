using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Services.Interfaces;
using FFXIVCraftArchitect.Coordinators;

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
    
    /// <summary>
    /// Watch state restored on startup (used for state persistence across restarts).
    /// </summary>
    public static WatchState? RestoredWatchState { get; set; }
    
    /// <summary>
    /// Reference to the main window for watch state saving.
    /// </summary>
    public static MainWindow? CurrentMainWindow { get; private set; }

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
        
        // Try to restore previous state (from manual restart with save)
        RestoredWatchState = WatchState.Load();
        if (RestoredWatchState != null)
        {
            LogMessage("[State] Restored state from previous session");
        }
        
        var mainWindow = Services.GetRequiredService<MainWindow>();
        CurrentMainWindow = mainWindow;
        mainWindow.Show();
    }
    
    public static void LogMessage(string message)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // SQLite cache auto-saves, just dispose properly
        try
        {
            var cacheService = Services.GetRequiredService<Core.Services.IMarketCacheService>() as IDisposable;
            cacheService?.Dispose();
        }
        catch { }
        
        // Clear watch state on normal exit
        WatchState.Clear();
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
        services.AddHttpClient<Core.Services.ITeamcraftRecipeService, Core.Services.TeamcraftRecipeService>();
        
        // Named HttpClients for services that use IHttpClientFactory
        services.AddHttpClient("Waitingway", c =>
        {
            c.DefaultRequestHeaders.Add("User-Agent", "FFXIV Craft Architect/1.0");
            c.Timeout = TimeSpan.FromSeconds(10);
            c.BaseAddress = new Uri("https://waiting.camora.dev/");
        });
        
        services.AddHttpClient("WorldStatus", c =>
        {
            c.DefaultRequestHeaders.Add("User-Agent", "FFXIV Craft Architect/1.0");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        
        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GarlandService>();
        services.AddSingleton<UniversalisService>();
        services.AddSingleton<ItemCacheService>();
        services.AddSingleton<Core.Services.IMarketCacheService, SqliteMarketCacheService>();  // Global market data cache
        services.AddSingleton<RecommendationCsvService>(); // Plan-specific recommendations
        services.AddSingleton<RecipeCalculationService>();
        services.AddSingleton<PlanPersistenceService>();
        services.AddSingleton<IPlanPersistenceService>(sp => sp.GetRequiredService<PlanPersistenceService>());
        services.AddSingleton<TeamcraftService>();
        services.AddSingleton<ITeamcraftService>(sp => sp.GetRequiredService<TeamcraftService>());
        services.AddSingleton<ArtisanService>();
        services.AddSingleton<IArtisanService>(sp => sp.GetRequiredService<ArtisanService>());
        services.AddSingleton<PriceCheckService>();
        services.AddSingleton<MarketShoppingService>();
        services.AddSingleton<WorldStatusService>();
        services.AddSingleton<WaitingwayTravelService>();
        services.AddSingleton<WorldBlacklistService>();

        // Coordinators
        services.AddTransient<ExportCoordinator>();
        services.AddTransient<ImportCoordinator>();
        services.AddTransient<PlanPersistenceCoordinator>();

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
