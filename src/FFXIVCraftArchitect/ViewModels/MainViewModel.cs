using System.Collections.ObjectModel;
using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// Main ViewModel that aggregates all child ViewModels for the MainWindow.
/// This enables DataTemplate binding in XAML, replacing code-behind UI generation.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly RecipePlannerViewModel _recipePlanner;
    private readonly MarketAnalysisViewModel _marketAnalysis;

    /// <summary>
    /// Creates a new MainViewModel with the specified child ViewModels.
    /// </summary>
    public MainViewModel(RecipePlannerViewModel recipePlanner, MarketAnalysisViewModel marketAnalysis)
    {
        _recipePlanner = recipePlanner;
        _marketAnalysis = marketAnalysis;
    }

    /// <summary>
    /// ViewModel for the Recipe Planner tab.
    /// </summary>
    public RecipePlannerViewModel RecipePlanner => _recipePlanner;

    /// <summary>
    /// ViewModel for the Market Analysis tab.
    /// </summary>
    public MarketAnalysisViewModel MarketAnalysis => _marketAnalysis;
}
