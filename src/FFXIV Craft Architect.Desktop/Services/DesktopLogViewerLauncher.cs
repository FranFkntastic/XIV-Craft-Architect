using FFXIV_Craft_Architect.Desktop.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FFXIV_Craft_Architect.Desktop.Services;

public interface IDesktopLogViewerLauncher
{
    void Open();
}

public sealed class DesktopLogViewerLauncher : IDesktopLogViewerLauncher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DesktopLogViewerLauncher> _logger;
    private readonly List<Window> _windows = new();

    public DesktopLogViewerLauncher(
        IServiceProvider services,
        ILogger<DesktopLogViewerLauncher> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Open()
    {
        _logger.LogDebug("Opening desktop diagnostic log viewer window.");
        var window = _services.GetRequiredService<DesktopLogViewerWindow>();
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Activate();
    }
}
