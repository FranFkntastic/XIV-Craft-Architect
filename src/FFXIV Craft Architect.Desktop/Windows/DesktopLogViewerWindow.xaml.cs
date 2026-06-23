using FFXIV_Craft_Architect.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FFXIV_Craft_Architect.Desktop.Windows;

public sealed partial class DesktopLogViewerWindow : Window
{
    public DesktopLogViewerWindow(DesktopLogViewerViewModel viewModel)
    {
        InitializeComponent();
        Title = "FFXIV Craft Architect Diagnostic Logs";
        Root.DataContext = viewModel;
        ResizeLogViewerWindow();
    }

    private void ResizeLogViewerWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1220, 760));
    }
}
