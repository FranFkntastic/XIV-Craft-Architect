using System.Windows;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect;

/// <summary>
/// Window for browsing and selecting saved crafting plans.
/// </summary>
public partial class PlanBrowserWindow : Window
{
    private readonly PlanPersistenceService _planPersistence;
    private List<PlanInfo> _plans = new();

    /// <summary>
    /// The selected plan file path, or null if cancelled.
    /// </summary>
    public string? SelectedPlanPath { get; private set; }

    public PlanBrowserWindow(PlanPersistenceService planPersistence)
    {
        InitializeComponent();
        _planPersistence = planPersistence;
        
        // Load plans when window opens
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPlanList();
    }

    private void RefreshPlanList()
    {
        _plans = _planPersistence.ListSavedPlans();
        PlansListBox.ItemsSource = _plans;

        if (_plans.Count == 0)
        {
            // Show a placeholder item
            PlansListBox.ItemsSource = new List<PlanInfo> 
            { 
                new PlanInfo 
                { 
                    Name = "No saved plans found",
                    ItemCount = 0,
                    ModifiedAt = DateTime.MinValue,
                    FilePath = ""
                } 
            };
            LoadButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
        }
    }

    private void OnPlanSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        var hasSelection = selected != null && !string.IsNullOrEmpty(selected.FilePath);
        
        LoadButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void OnLoadPlan(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected != null && !string.IsNullOrEmpty(selected.FilePath))
        {
            SelectedPlanPath = selected.FilePath;
            DialogResult = true;
            Close();
        }
    }

    private void OnDeletePlan(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected == null || string.IsNullOrEmpty(selected.FilePath))
            return;

        var result = MessageBox.Show(
            $"Are you sure you want to delete '{selected.Name}'?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            if (_planPersistence.DeletePlan(selected.FilePath))
            {
                RefreshPlanList();
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
