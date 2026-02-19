using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using FFXIV_Craft_Architect.Core.Coordinators;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Models;
using FFXIV_Craft_Architect.Services;
using FFXIV_Craft_Architect.Services.Interfaces;
using FFXIV_Craft_Architect.Services.UI;
using FFXIV_Craft_Architect.ViewModels;
using FFXIV_Craft_Architect.Coordinators;
using CoreIGarlandService = FFXIV_Craft_Architect.Core.Services.Interfaces.IGarlandService;
using CoreIUniversalisService = FFXIV_Craft_Architect.Core.Services.Interfaces.IUniversalisService;
using CoreIPlanPersistenceService = FFXIV_Craft_Architect.Core.Services.Interfaces.IPlanPersistenceService;
using CoreItemCacheService = FFXIV_Craft_Architect.Core.Services.ItemCacheService;
using CoreRecommendationCsvService = FFXIV_Craft_Architect.Core.Services.RecommendationCsvService;
using CorePlanPersistenceService = FFXIV_Craft_Architect.Core.Services.PlanPersistenceService;
using CoreWorldBlacklistService = FFXIV_Craft_Architect.Core.Services.WorldBlacklistService;
using CoreIPriceRefreshCoordinator = FFXIV_Craft_Architect.Core.Coordinators.IPriceRefreshCoordinator;
using CorePriceRefreshCoordinator = FFXIV_Craft_Architect.Core.Coordinators.PriceRefreshCoordinator;
using CoreIShoppingOptimizationCoordinator = FFXIV_Craft_Architect.Core.Coordinators.IShoppingOptimizationCoordinator;
using CoreShoppingOptimizationCoordinator = FFXIV_Craft_Architect.Core.Coordinators.ShoppingOptimizationCoordinator;
using CoreIWatchListCoordinator = FFXIV_Craft_Architect.Core.Coordinators.IWatchListCoordinator;
using CoreWatchListCoordinator = FFXIV_Craft_Architect.Core.Coordinators.WatchListCoordinator;

namespace FFXIV_Craft_Architect;

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
        var worldStatusService = Services.GetRequiredService<IWorldStatusService>();
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
            var cacheService = Services.GetRequiredService<IMarketCacheService>() as IDisposable;
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
        services.AddHttpClient<ITeamcraftRecipeService, TeamcraftRecipeService>();
        services.AddHttpClient<ArtisanService>();
        
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
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GarlandService>();
        services.AddSingleton<CoreIGarlandService>(sp => sp.GetRequiredService<GarlandService>());
        services.AddSingleton<UniversalisService>();
        services.AddSingleton<CoreIUniversalisService>(sp => sp.GetRequiredService<UniversalisService>());
        services.AddSingleton<CoreItemCacheService>();
        services.AddSingleton<IMarketCacheService, SqliteMarketCacheService>();
        services.AddSingleton<CoreRecommendationCsvService>();
        services.AddSingleton<RecipeCalculationService>();
        services.AddSingleton<CorePlanPersistenceService>();
        services.AddSingleton<CoreIPlanPersistenceService>(sp => sp.GetRequiredService<CorePlanPersistenceService>());
        services.AddSingleton<TeamcraftService>();
        services.AddSingleton<ITeamcraftService>(sp => sp.GetRequiredService<TeamcraftService>());
        services.AddSingleton<ArtisanService>();
        services.AddSingleton<IArtisanService>(sp => sp.GetRequiredService<ArtisanService>());
        services.AddSingleton<PriceCheckService>();
        services.AddSingleton<MarketShoppingService>();
        
        // WorldStatusService uses IHttpClientFactory
        services.AddSingleton<IWorldStatusService>(sp => 
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorldStatusService>>();
            return new WorldStatusService(factory, logger);
        });
        
        // WaitingwayTravelService - DISABLED pending re-implementation
        // services.AddSingleton<Core.Services.WaitingwayTravelService>();
        
        services.AddSingleton<WorldDataCoordinator>();
        services.AddSingleton<CoreWorldBlacklistService>();
        services.AddSingleton<DialogServiceFactory>();
        
        // UI Builders
        services.AddSingleton<InfoPanelBuilder>();
        services.AddSingleton<ICardFactory, CardFactory>();

        // Coordinators
        services.AddSingleton<CoreIPriceRefreshCoordinator, CorePriceRefreshCoordinator>();
        services.AddSingleton<CoreIShoppingOptimizationCoordinator, CoreShoppingOptimizationCoordinator>();
        services.AddSingleton<IMarketLogisticsCoordinator, MarketLogisticsCoordinator>();
        services.AddSingleton<CoreIWatchListCoordinator, CoreWatchListCoordinator>();
        services.AddTransient<ExportCoordinator>();
        services.AddTransient<ImportCoordinator>();
        services.AddTransient<PlanPersistenceCoordinator>();
        services.AddTransient<WatchStateCoordinator>();
        
        // ViewModels
        services.AddSingleton<MarketAnalysisViewModel>();
        services.AddSingleton<RecipePlannerViewModel>();
        services.AddSingleton<MainViewModel>();

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
