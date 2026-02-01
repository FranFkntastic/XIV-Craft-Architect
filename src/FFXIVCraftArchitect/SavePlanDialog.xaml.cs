using System.Windows;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect;

/// <summary>
/// Dialog for saving a plan with a custom name or overwriting an existing plan.
/// </summary>
public partial class SavePlanDialog : Window
{
    private readonly PlanPersistenceService _planPersistence;
    private List<PlanInfo> _plans = new();

    /// <summary>
    /// The name entered for the plan.
    /// </summary>
    public string PlanName { get; private set; } = string.Empty;

    /// <summary>
    /// The path of the plan to overwrite, or null for a new save.
    /// </summary>
    public string? OverwritePath { get; private set; }

    /// <summary>
    /// Whether the user chose to overwrite an existing plan.
    /// </summary>
    public bool IsOverwrite { get; private set; }

    public SavePlanDialog(PlanPersistenceService planPersistence, string currentName)
    {
        InitializeComponent();
        _planPersistence = planPersistence;
        PlanNameTextBox.Text = currentName;
        
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPlanList();
        PlanNameTextBox.Focus();
        PlanNameTextBox.SelectAll();
    }

    private void RefreshPlanList()
    {
        _plans = _planPersistence.ListSavedPlans();
        PlansListBox.ItemsSource = _plans;

        if (_plans.Count == 0)
        {
            PlansListBox.ItemsSource = new List<PlanInfo> 
            { 
                new PlanInfo 
                { 
                    Name = "No existing plans",
                    FilePath = ""
                } 
            };
        }
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var name = PlanNameTextBox.Text.Trim();
        SaveNewButton.IsEnabled = !string.IsNullOrWhiteSpace(name);
        
        // Check if name matches an existing plan
        var existing = _plans.FirstOrDefault(p => 
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        
        if (existing != null)
        {
            // Auto-select the matching plan
            PlansListBox.SelectedItem = existing;
            OverwriteButton.IsEnabled = true;
            OverwriteButton.Content = $"Overwrite '{existing.Name}'";
        }
        else
        {
            OverwriteButton.IsEnabled = PlansListBox.SelectedItem is PlanInfo;
            OverwriteButton.Content = "Overwrite";
        }
    }

    private void OnPlanSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected != null && !string.IsNullOrEmpty(selected.FilePath))
        {
            // Update name textbox with selected plan name
            PlanNameTextBox.Text = selected.Name;
            PlanNameTextBox.SelectAll();
            OverwriteButton.IsEnabled = true;
            OverwriteButton.Content = $"Overwrite '{selected.Name}'";
        }
        else
        {
            OverwriteButton.IsEnabled = false;
            OverwriteButton.Content = "Overwrite";
        }
    }

    private void OnSaveNew(object sender, RoutedEventArgs e)
    {
        var name = PlanNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        // Check if a plan with this name already exists
        var existing = _plans.FirstOrDefault(p => 
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var result = MessageBox.Show(
                $"A plan named '{name}' already exists. Overwrite it?",
                "Confirm Overwrite",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
                return;

            OverwritePath = existing.FilePath;
            IsOverwrite = true;
        }
        else
        {
            OverwritePath = null;
            IsOverwrite = false;
        }

        PlanName = name;
        DialogResult = true;
        Close();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        var selected = PlansListBox.SelectedItem as PlanInfo;
        if (selected == null || string.IsNullOrEmpty(selected.FilePath))
            return;

        var result = MessageBox.Show(
            $"Are you sure you want to overwrite '{selected.Name}'?",
            "Confirm Overwrite",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            PlanName = PlanNameTextBox.Text.Trim();
            OverwritePath = selected.FilePath;
            IsOverwrite = true;
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
