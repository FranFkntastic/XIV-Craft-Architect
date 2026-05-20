using System.Windows;
using System.Windows.Media;

namespace FFXIV_Craft_Architect.Views;

public partial class ExportResultWindow : Window
{
    public ExportResultWindow(string title, string content, string? description = null)
    {
        InitializeComponent();
        
        TitleText.Text = title;
        ContentTextBox.Text = content;
        DescriptionText.Text = description ?? "Copy the content below or select and press Ctrl+C:";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ContentTextBox.Text);
            StatusText.Text = "Copied to clipboard!";
            StatusText.Foreground = ResolveBrush("LightGreenBrush", Brushes.LightGreen);
        }
        catch
        {
            StatusText.Text = "Clipboard unavailable - use Ctrl+C instead";
            StatusText.Foreground = ResolveBrush("WarningOrangeBrush", Brushes.Orange);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }
}
