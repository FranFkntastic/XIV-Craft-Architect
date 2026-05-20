using System.Windows.Controls;
using System.Windows;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class MarketAnalysisView : UserControl
{
    public MarketAnalysisView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NotifyViewModelOfSizeChange();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        NotifyViewModelOfSizeChange();
    }

    private void NotifyViewModelOfSizeChange()
    {
        var availableHeight = SplitPaneMarketView.ActualHeight;
        if (availableHeight <= 0)
        {
            return;
        }

        var viewModel = GetMarketAnalysisViewModel();
        viewModel?.RecalculateTopPaneHeight(availableHeight);
    }

    private MarketAnalysisViewModel? GetMarketAnalysisViewModel()
    {
        if (DataContext is MarketAnalysisViewModel vm)
        {
            return vm;
        }

        var mainWindow = Window.GetWindow(this);
        if (mainWindow?.DataContext is MainViewModel mainVm)
        {
            return mainVm.MarketAnalysis;
        }

        return null;
    }
}
