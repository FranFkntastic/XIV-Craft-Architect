using System.Windows;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect;

/// <summary>
/// Actions that can be performed from the plan browser.
/// </summary>
public enum PlanBrowserAction
{
    None,
    Load,
    RenameCurrent
}

/// <summary>
/// Window for browsing and managing saved crafting plans.
/// </summary>
public partial class PlanBrowserWindow : Window
{
    private readonly IPlanPersistenceService _planPersistence;
    private readonly IDialogService _dialogs;
    private readonly MainWindow? _mainWindow;
    private List<PlanInfo> _plans = new();

    /// <summary>
    /// The selected plan file path, or null if cancelled.
    /// </summary>
    public string? SelectedPlanPath { get; private set; }

    /// <summary>
    /// The action selected by the user.
    /// </summary>
    public PlanBrowserAction SelectedAction { get; private set; } = PlanBrowserAction.None;

    public PlanBrowserWindow(IPlanPersistenceService planPersistence, DialogServiceFactory dialogFactory, MainWindow? mainWindow = null)
    {
        InitializeComponent();
        _planPersistence = planPersistence;
        _dialogs = dialogFactory.CreateForWindow(this);
        _mainWindow = mainWindow;
        
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
            RenameButton.IsEnabled = false;
        }
    }

    private void OnPlanSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        var hasSelection = selected != null && !string.IsNullOrEmpty(selected.FilePath);
        
        LoadButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        RenameButton.IsEnabled = hasSelection;
    }

    private void OnLoadPlan(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected != null && !string.IsNullOrEmpty(selected.FilePath))
        {
            SelectedPlanPath = selected.FilePath;
            SelectedAction = PlanBrowserAction.Load;
            DialogResult = true;
            Close();
        }
    }

    private async void OnRenamePlan(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected == null || string.IsNullOrEmpty(selected.FilePath))
            return;

        // Load the plan to rename it
        var plan = await _planPersistence.LoadPlanAsync(selected.FilePath);
        if (plan == null) return;

        var renameDialog = new RenamePlanDialog(plan.Name)
        {
            Owner = this
        };

        if (renameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(renameDialog.NewName))
        {
            var oldName = plan.Name;
            plan.Name = renameDialog.NewName;
            plan.MarkModified();
            
            // Delete old file and save with new name
            _planPersistence.DeletePlan(selected.FilePath);
            await _planPersistence.SavePlanAsync(plan);
            
            RefreshPlanList();
        }
    }

    private async void OnDeletePlan(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected == null || string.IsNullOrEmpty(selected.FilePath))
            return;

        if (!await _dialogs.ConfirmAsync(
            $"Are you sure you want to delete '{selected.Name}'?",
            "Confirm Delete"))
        {
            return;
        }

        if (_planPersistence.DeletePlan(selected.FilePath))
        {
            RefreshPlanList();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
