using System.ComponentModel;
using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Implementation of the navigation service that manages application tab state.
/// </summary>
public class NavigationService : INavigationService
{
    private ApplicationTab _currentTab = ApplicationTab.RecipePlanner;

    /// <inheritdoc />
    public ApplicationTab CurrentTab
    {
        get => _currentTab;
        private set
        {
            if (_currentTab != value)
            {
                var previousTab = _currentTab;
                _currentTab = value;
                OnPropertyChanged(nameof(CurrentTab));
                OnPropertyChanged(nameof(IsMarketViewVisible));
                Navigated?.Invoke(this, new NavigationEventArgs(previousTab, value));
            }
        }
    }

    /// <inheritdoc />
    public bool IsMarketViewVisible => 
        CurrentTab == ApplicationTab.MarketAnalysis || 
        CurrentTab == ApplicationTab.ProcurementPlanner;

    /// <inheritdoc />
    public event EventHandler<NavigationEventArgs>? Navigated;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public void NavigateTo(ApplicationTab tab)
    {
        if (!IsActive(tab))
        {
            CurrentTab = tab;
        }
    }

    /// <inheritdoc />
    public bool IsActive(ApplicationTab tab) => CurrentTab == tab;

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
