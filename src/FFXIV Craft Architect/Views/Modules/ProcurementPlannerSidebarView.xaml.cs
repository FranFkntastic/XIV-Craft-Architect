using System.Windows;
using System.Windows.Controls;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class ProcurementPlannerSidebarView : UserControl
{
    public event SelectionChangedEventHandler? ProcurementSortChanged;
    public event RoutedEventHandler? ConductAnalysisClicked;

    public ProcurementPlannerSidebarView()
    {
        InitializeComponent();
    }

    public ComboBox LeftPanelProcurementSortComboControl => LeftPanelProcurementSortCombo;
    public Wpf.Ui.Controls.Button LeftPanelProcurementAnalysisButtonControl => LeftPanelProcurementAnalysisButton;

    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e) => ProcurementSortChanged?.Invoke(sender, e);
    private void OnConductAnalysis(object sender, RoutedEventArgs e) => ConductAnalysisClicked?.Invoke(sender, e);
}
