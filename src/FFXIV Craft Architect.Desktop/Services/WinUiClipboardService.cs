using Windows.ApplicationModel.DataTransfer;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class WinUiClipboardService : IDesktopClipboardService
{
    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text ?? string.Empty);
        Clipboard.SetContent(package);
    }
}
