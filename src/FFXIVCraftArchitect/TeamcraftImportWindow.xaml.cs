using System.Windows;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services;

namespace FFXIVCraftArchitect;

/// <summary>
/// Dialog for importing crafting lists from Teamcraft's "Copy as Text" feature.
/// </summary>
public partial class TeamcraftImportWindow : Window
{
    private readonly TeamcraftService _teamcraftService;
    private readonly string _dataCenter;
    private readonly string _world;

    /// <summary>
    /// The imported plan, or null if cancelled.
    /// </summary>
    public CraftingPlan? ImportedPlan { get; private set; }

    public TeamcraftImportWindow(TeamcraftService teamcraftService, string dataCenter, string world)
    {
        InitializeComponent();
        _teamcraftService = teamcraftService;
        _dataCenter = dataCenter;
        _world = world;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var precraftText = PrecraftTextBox.Text;
        var finalItemsText = FinalItemsTextBox.Text;

        if (string.IsNullOrWhiteSpace(precraftText) && string.IsNullOrWhiteSpace(finalItemsText))
        {
            StatusText.Text = "Please enter items to import";
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        StatusText.Text = "Importing...";
        StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
        
        try
        {
            var plan = await _teamcraftService.ImportFromTeamcraftTextAsync(
                "Imported Plan",
                precraftText,
                finalItemsText,
                _dataCenter,
                _world);

            if (plan != null && plan.RootItems.Count > 0)
            {
                ImportedPlan = plan;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "No items found or could not be imported";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
