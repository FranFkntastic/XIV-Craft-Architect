using System.Windows.Controls;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class AcquisitionEvaluationSidebarView : UserControl
{
    public AcquisitionEvaluationSidebarView()
    {
        InitializeComponent();
    }

    public ComboBox AcquisitionFilterComboControl => AcquisitionFilterCombo;
}
