using FFXIV_Craft_Architect.Desktop.Services;
using FFXIV_Craft_Architect.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FFXIV_Craft_Architect.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow(DesktopShellViewModel shell, DesktopWindowHandleProvider windowHandleProvider)
    {
        InitializeComponent();

        Title = "FFXIV Craft Architect Desktop";
        Root.DataContext = shell;
        windowHandleProvider.SetWindowHandle(WindowNative.GetWindowHandle(this));
        ResizeWorkbenchWindow();
    }

    private void ResizeWorkbenchWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1500, 920));
    }

    private void TargetSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is DesktopPlanSearchResultRow result)
        {
            sender.Text = result.Name;
        }
    }

    private void TargetSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Root.DataContext is not DesktopShellViewModel shell)
        {
            return;
        }

        if (args.ChosenSuggestion is DesktopPlanSearchResultRow result
            && shell.AddSearchResultCommand.CanExecute(result))
        {
            shell.AddSearchResultCommand.Execute(result);
            return;
        }

        if (shell.SearchProjectItemsCommand.CanExecute(null))
        {
            shell.SearchProjectItemsCommand.Execute(null);
        }
    }
}
