using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using WinRT.Interop;

namespace FFXIV_Craft_Architect.Desktop.Services;

public interface IDesktopFileDialogService
{
    Task<string?> OpenFilePathAsync(
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default);

    Task<string?> OpenTextFileAsync(
        string fileTypeLabel,
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default);

    Task<bool> SaveTextFileAsync(
        string suggestedFileName,
        string content,
        string fileTypeLabel,
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default);
}

public interface IDesktopWindowHandleProvider
{
    IntPtr WindowHandle { get; }
}

public sealed class DesktopWindowHandleProvider : IDesktopWindowHandleProvider
{
    public IntPtr WindowHandle { get; private set; }

    public void SetWindowHandle(IntPtr windowHandle)
    {
        WindowHandle = windowHandle;
    }
}

public sealed class WinUiFileDialogService : IDesktopFileDialogService
{
    private readonly IDesktopWindowHandleProvider _windowHandleProvider;

    public WinUiFileDialogService(IDesktopWindowHandleProvider windowHandleProvider)
    {
        _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
    }

    public async Task<string?> OpenFilePathAsync(
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default)
    {
        var picker = CreateOpenPicker(fileExtensions);
        var file = await picker.PickSingleFileAsync();
        ct.ThrowIfCancellationRequested();
        return file?.Path;
    }

    public async Task<string?> OpenTextFileAsync(
        string fileTypeLabel,
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default)
    {
        var picker = CreateOpenPicker(fileExtensions);
        var file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            return null;
        }

        var content = await FileIO.ReadTextAsync(file);
        ct.ThrowIfCancellationRequested();
        return content;
    }

    public async Task<bool> SaveTextFileAsync(
        string suggestedFileName,
        string content,
        string fileTypeLabel,
        IReadOnlyList<string> fileExtensions,
        CancellationToken ct = default)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add(fileTypeLabel, fileExtensions.ToList());
        InitializeWithWindow.Initialize(picker, GetWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return false;
        }

        CachedFileManager.DeferUpdates(file);
        await FileIO.WriteTextAsync(file, content);
        var status = await CachedFileManager.CompleteUpdatesAsync(file);
        ct.ThrowIfCancellationRequested();
        return status != FileUpdateStatus.Failed;
    }

    private FileOpenPicker CreateOpenPicker(IReadOnlyList<string> fileExtensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        foreach (var extension in fileExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        return picker;
    }

    private IntPtr GetWindowHandle()
    {
        var windowHandle = _windowHandleProvider.WindowHandle;
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Desktop window handle is not available for file dialogs.");
        }

        return windowHandle;
    }
}
