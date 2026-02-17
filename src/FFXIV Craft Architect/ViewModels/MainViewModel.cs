using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Services;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// Main ViewModel that aggregates all child ViewModels for the MainWindow.
/// This enables DataTemplate binding in XAML, replacing code-behind UI generation.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly RecipePlannerViewModel _recipePlanner;
    private readonly MarketAnalysisViewModel _marketAnalysis;
    private readonly WorldBlacklistService _blacklistService;

    /// <summary>
    /// Creates a new MainViewModel with the specified child ViewModels.
    /// </summary>
    public MainViewModel(
        RecipePlannerViewModel recipePlanner, 
        MarketAnalysisViewModel marketAnalysis,
        WorldBlacklistService blacklistService)
    {
        _recipePlanner = recipePlanner;
        _marketAnalysis = marketAnalysis;
        _blacklistService = blacklistService;
    }

    /// <summary>
    /// ViewModel for the Recipe Planner tab.
    /// </summary>
    public RecipePlannerViewModel RecipePlanner => _recipePlanner;

    /// <summary>
    /// ViewModel for the Market Analysis tab.
    /// </summary>
    public MarketAnalysisViewModel MarketAnalysis => _marketAnalysis;

    /// <summary>
    /// Gets the count of blacklisted worlds.
    /// </summary>
    public int BlacklistedWorldCount => _blacklistService.GetBlacklistedWorlds().Count;

    /// <summary>
    /// Clears the world blacklist.
    /// </summary>
    [RelayCommand]
    private void ClearBlacklist()
    {
        _blacklistService.ClearBlacklist();
    }
}
