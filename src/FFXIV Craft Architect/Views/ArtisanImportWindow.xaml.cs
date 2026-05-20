using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Views;

/// <summary>
/// Dialog for importing crafting lists from Artisan JSON exports.
/// </summary>
public partial class ArtisanImportWindow : Window
{
    private readonly IArtisanService _artisanService;
    private readonly string _dataCenter;
    private readonly string _world;

    /// <summary>
    /// The imported plan, or null if cancelled.
    /// </summary>
    public CraftingPlan? ImportedPlan { get; private set; }

    public ArtisanImportWindow(IArtisanService artisanService, string dataCenter, string world)
    {
        InitializeComponent();
        _artisanService = artisanService;
        _dataCenter = dataCenter;
        _world = world;
    }

    private void OnPasteFromClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipboardText = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                JsonTextBox.Text = clipboardText;
                UpdatePreview(clipboardText);
            }
            else
            {
                StatusText.Text = "Clipboard is empty";
                StatusText.Foreground = ResolveBrush("WarningOrangeBrush", Brushes.Orange);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to read clipboard: {ex.Message}";
            StatusText.Foreground = ResolveBrush("ErrorRedBrush", Brushes.Red);
        }
    }

    private void UpdatePreview(string jsonText)
    {
        try
        {
            var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(jsonText);
            if (artisanList?.Recipes != null && artisanList.Recipes.Count > 0)
            {
                var previewItems = artisanList.Recipes.Take(5)
                    .Select(r => $"• {r.Quantity}x Recipe ID {r.ID}")
                    .ToList();
                
                var preview = string.Join("\n", previewItems);
                if (artisanList.Recipes.Count > 5)
                {
                    preview += $"\n... and {artisanList.Recipes.Count - 5} more";
                }
                
                PreviewText.Text = preview;
                PreviewPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"Found {artisanList.Recipes.Count} recipes";
                StatusText.Foreground = ResolveBrush("LightGreenBrush", Brushes.LightGreen);
                
                if (!string.IsNullOrWhiteSpace(artisanList.Name))
                {
                    StatusText.Text += $" in \"{artisanList.Name}\"";
                }
            }
            else
            {
                PreviewPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "No valid Artisan list found in JSON";
                StatusText.Foreground = ResolveBrush("WarningOrangeBrush", Brushes.Orange);
            }
        }
        catch (JsonException)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Invalid JSON format";
            StatusText.Foreground = ResolveBrush("WarningOrangeBrush", Brushes.Orange);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var jsonText = JsonTextBox.Text;

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            StatusText.Text = "Please paste an Artisan JSON export";
            StatusText.Foreground = ResolveBrush("WarningOrangeBrush", Brushes.Orange);
            return;
        }

        StatusText.Text = "Importing...";
        StatusText.Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray);
        ImportButton.IsEnabled = false;

        try
        {
            var plan = await _artisanService.ImportFromArtisanAsync(jsonText, _dataCenter, _world);

            if (plan != null && plan.RootItems.Count > 0)
            {
                ImportedPlan = plan;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "No items could be imported from this JSON";
                StatusText.Foreground = ResolveBrush("ErrorRedBrush", Brushes.Red);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
            StatusText.Foreground = ResolveBrush("ErrorRedBrush", Brushes.Red);
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }
}
