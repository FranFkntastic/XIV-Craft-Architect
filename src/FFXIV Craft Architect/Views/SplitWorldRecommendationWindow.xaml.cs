using System.Windows;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Views;

public partial class SplitWorldRecommendationWindow : Window
{
    public SplitWorldRecommendationWindow(SplitWorldWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (_, _) => Close();
    }
}
