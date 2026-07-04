using System.Text;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class BrowserFileExportService
{
    private const string ModulePath = "./fileExport.js?v=save-picker-1";

    private readonly IJSRuntime _jsRuntime;
    private Task<IJSObjectReference>? _moduleTask;

    public BrowserFileExportService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await GetModuleAsync(cancellationToken);
    }

    public async Task PrepareTextFileSaveAsync(
        string key,
        string fileName,
        string content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var module = await GetModuleAsync(cancellationToken);
        await module.InvokeVoidAsync(
            "prepareTextFileSave",
            cancellationToken,
            key,
            fileName,
            content,
            contentType);
    }

    public async Task SaveTextFileAsync(
        string key,
        string fileName,
        string content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await PrepareTextFileSaveAsync(key, fileName, content, contentType, cancellationToken);

        var module = await GetModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("savePreparedFile", cancellationToken, key);
    }

    private Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        _moduleTask ??= _jsRuntime
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
            .AsTask();

        return _moduleTask;
    }
}
