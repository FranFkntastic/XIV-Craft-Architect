using System.Windows;
using System.Windows.Controls;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class MarketAnalysisSidebarView : UserControl
{
    public event RoutedEventHandler? ConductAnalysisClicked;
    public event RoutedEventHandler? ViewMarketStatusClicked;

    public MarketAnalysisSidebarView()
    {
        InitializeComponent();
    }

    public Wpf.Ui.Controls.Button LeftPanelConductAnalysisButtonControl => LeftPanelConductAnalysisButton;
    public Wpf.Ui.Controls.Button LeftPanelViewMarketStatusButtonControl => LeftPanelViewMarketStatusButton;

    private void OnConductAnalysis(object sender, RoutedEventArgs e) => ConductAnalysisClicked?.Invoke(sender, e);
    private void OnViewMarketStatus(object sender, RoutedEventArgs e) => ViewMarketStatusClicked?.Invoke(sender, e);
}
