using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        var logWindow = new LogViewerWindow(_dialogFactory)
        {
            Owner = this
        };
        logWindow.Show();
    }

    private async void OnRestartApp(object sender, RoutedEventArgs e)
    {
        if (!await _dialogs.ConfirmAsync(
            "Restart the application? Your current plan will be preserved.",
            "Restart App"))
        {
            return;
        }

        var state = _watchStateCoordinator.PrepareWatchState(
            GetCurrentDataCenter(),
            GetCurrentWorld());
        state.Save();

        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDirectory = Path.GetDirectoryName(exePath);
        var exeName = "FFXIV_Craft_Architect.exe";
        var fullExePath = Path.Combine(exeDirectory ?? ".", exeName);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullExePath,
                WorkingDirectory = exeDirectory,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not restart: {ex.Message}. State saved.";
            Environment.ExitCode = 42;
        }

        Application.Current.Shutdown();
    }

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var optionsWindow = App.Services.GetRequiredService<OptionsWindow>();
        optionsWindow.Owner = this;
        var result = optionsWindow.ShowDialog();

        if (result == true)
        {
            var diagnosticLoggingEnabled = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
            _recipeVm.SetDiagnosticLoggingEnabled(diagnosticLoggingEnabled);
            _logger.LogInformation("Diagnostic logging {Status}", diagnosticLoggingEnabled ? "enabled" : "disabled");
        }
    }

    private void OnDebugOptions(object sender, RoutedEventArgs e)
    {
        OnOptions(sender, e);
    }

    private void OnCacheDiagnostics(object sender, RoutedEventArgs e)
    {
        var window = new CacheDiagnosticsWindow();
        window.Show();
    }

    private async void OnViewBlacklistedWorlds(object sender, RoutedEventArgs e)
    {
        var blacklisted = _blacklistService.GetBlacklistedWorlds();

        if (blacklisted.Count == 0)
        {
            await _dialogs.ShowInfoAsync(
                "No worlds are currently blacklisted.\n\n" +
                "Worlds can be blacklisted from the Market Analysis view when travel is prohibited.",
                "Blacklisted Worlds");
            return;
        }

        var worldList = string.Join("\n", blacklisted.Select(w =>
            $"• {w.WorldName} (expires in {w.ExpiresInDisplay})"));

        if (!await _dialogs.ConfirmAsync(
            $"Currently Blacklisted Worlds ({blacklisted.Count}):\n\n{worldList}\n\n" +
            "Click 'Yes' to clear all blacklisted worlds.",
            "Blacklisted Worlds"))
        {
            return;
        }

        _blacklistService.ClearBlacklist();
        StatusLabel.Text = "All blacklisted worlds cleared";

        if (IsMarketViewVisible())
        {
            PopulateProcurementPanel();
        }
    }

    private void OnNewPlan(object sender, RoutedEventArgs e)
    {
        _recipeVm.ClearCommand.Execute(null);

        ProjectList.ItemsSource = null;
        RecipePlanPanel?.Children.Clear();

        BuildPlanButton.IsEnabled = false;
        BrowsePlanButton.IsEnabled = false;

        StatusLabel.Text = "New plan created. Add items to get started.";
    }
}
