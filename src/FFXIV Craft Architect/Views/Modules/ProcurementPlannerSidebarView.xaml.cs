using System.Windows;
using System.Windows.Controls;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class ProcurementPlannerSidebarView : UserControl
{
    public event SelectionChangedEventHandler? ProcurementSortChanged;
    public event RoutedEventHandler? BuildProcurementPlanClicked;

    public ProcurementPlannerSidebarView()
    {
        InitializeComponent();
    }

    public ComboBox LeftPanelProcurementSortComboControl => LeftPanelProcurementSortCombo;
    public Wpf.Ui.Controls.Button LeftPanelProcurementAnalysisButtonControl => LeftPanelProcurementAnalysisButton;

    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e) => ProcurementSortChanged?.Invoke(sender, e);
    private void OnBuildProcurementPlan(object sender, RoutedEventArgs e) => BuildProcurementPlanClicked?.Invoke(sender, e);
}
