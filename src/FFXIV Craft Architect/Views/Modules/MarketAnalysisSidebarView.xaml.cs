using System.Windows;
using System.Windows.Controls;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class MarketAnalysisSidebarView : UserControl
{
    public event RoutedEventHandler? ConductAnalysisClicked;
    public event RoutedEventHandler? ViewMarketStatusClicked;
    public event RoutedEventHandler? EnableSplitWorldChanged;

    public MarketAnalysisSidebarView()
    {
        InitializeComponent();
    }

    public Wpf.Ui.Controls.Button LeftPanelConductAnalysisButtonControl => LeftPanelConductAnalysisButton;
    public Wpf.Ui.Controls.Button LeftPanelViewMarketStatusButtonControl => LeftPanelViewMarketStatusButton;
    public ComboBox DcComboControl => DcCombo;
    public ComboBox WorldComboControl => WorldCombo;
    public ComboBox ProcurementSortComboControl => ProcurementSortCombo;
    public ComboBox ProcurementModeComboControl => ProcurementModeCombo;
    public CheckBox ProcurementSearchAllNaCheckControl => ProcurementSearchAllNaCheck;
    public CheckBox EnableSplitWorldCheckControl => EnableSplitWorldCheck;

    public bool ForceRefetch => ForceRefetchCheckBox.IsChecked == true;
    public bool EnableSplitWorld => EnableSplitWorldCheck.IsChecked == true;

    private void OnConductAnalysis(object sender, RoutedEventArgs e) => ConductAnalysisClicked?.Invoke(sender, e);
    private void OnViewMarketStatus(object sender, RoutedEventArgs e) => ViewMarketStatusClicked?.Invoke(sender, e);

    private void OnForceRefetchChanged(object sender, RoutedEventArgs e)
    {
        LeftPanelConductAnalysisButton.ToolTip = ForceRefetch
            ? "Fetch fresh prices from API then analyze"
            : "Analyze using cached market data (fetch if needed)";
    }

    private void OnEnableSplitWorldChanged(object sender, RoutedEventArgs e)
    {
        EnableSplitWorldChanged?.Invoke(sender, e);
    }
}
