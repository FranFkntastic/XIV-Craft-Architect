using System.IO;
using System.Windows;

namespace FFXIVCraftArchitect;

/// <summary>
/// Interaction logic for LogViewerWindow.xaml
/// </summary>
public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        LoadLogs();
    }

    private void LoadLogs()
    {
        try
        {
            if (File.Exists(App.LogFilePath))
            {
                LogTextBox.Text = File.ReadAllText(App.LogFilePath);
                // Scroll to end
                LogTextBox.ScrollToEnd();
            }
            else
            {
                LogTextBox.Text = "No debug.log found.";
            }
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"Error reading log file: {ex.Message}";
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadLogs();
    }
}
