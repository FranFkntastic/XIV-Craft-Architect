using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIV_Craft_Architect.Views.Modules;

public partial class RecipePlannerSidebarView : UserControl
{
    public event KeyEventHandler? ItemSearchKeyDownForwarded;
    public event RoutedEventHandler? SearchClicked;
    public event SelectionChangedEventHandler? ItemSelected;
    public event RoutedEventHandler? AddToProjectClicked;
    public event SelectionChangedEventHandler? ProjectItemSelected;
    public event RoutedEventHandler? QuantityGotFocusForwarded;
    public event TextCompositionEventHandler? QuantityPreviewTextInputForwarded;
    public event RoutedEventHandler? QuantityChangedForwarded;
    public event RoutedEventHandler? RemoveProjectItemClicked;
    public event RoutedEventHandler? BuildProjectPlanClicked;
    public event RoutedEventHandler? BrowsePlanClicked;

    public RecipePlannerSidebarView()
    {
        InitializeComponent();
    }

    public Wpf.Ui.Controls.TextBox ItemSearchControl => ItemSearchTextBox;
    public Border SearchResultsPanelControl => SearchResultsPanel;
    public ListBox SearchResultsControl => SearchResults;
    public Wpf.Ui.Controls.Button AddToProjectButtonControl => AddToProjectButton;
    public ListBox ProjectListControl => ProjectList;
    public TextBlock QuickViewCountTextControl => QuickViewCountText;
    public Wpf.Ui.Controls.Button BuildPlanButtonControl => BuildPlanButton;
    public Wpf.Ui.Controls.Button BrowsePlanButtonControl => BrowsePlanButton;

    private void OnItemSearchKeyDown(object sender, KeyEventArgs e) => ItemSearchKeyDownForwarded?.Invoke(sender, e);
    private void OnSearchItem(object sender, RoutedEventArgs e) => SearchClicked?.Invoke(sender, e);
    private void OnItemSelected(object sender, SelectionChangedEventArgs e) => ItemSelected?.Invoke(sender, e);
    private void OnAddToProject(object sender, RoutedEventArgs e) => AddToProjectClicked?.Invoke(sender, e);
    private void OnProjectItemSelected(object sender, SelectionChangedEventArgs e) => ProjectItemSelected?.Invoke(sender, e);
    private void OnQuantityGotFocus(object sender, RoutedEventArgs e) => QuantityGotFocusForwarded?.Invoke(sender, e);
    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e) => QuantityPreviewTextInputForwarded?.Invoke(sender, e);
    private void OnQuantityChanged(object sender, RoutedEventArgs e) => QuantityChangedForwarded?.Invoke(sender, e);
    private void OnRemoveProjectItem(object sender, RoutedEventArgs e) => RemoveProjectItemClicked?.Invoke(sender, e);
    private void OnBuildProjectPlan(object sender, RoutedEventArgs e) => BuildProjectPlanClicked?.Invoke(sender, e);
    private void OnBrowsePlan(object sender, RoutedEventArgs e) => BrowsePlanClicked?.Invoke(sender, e);
}
