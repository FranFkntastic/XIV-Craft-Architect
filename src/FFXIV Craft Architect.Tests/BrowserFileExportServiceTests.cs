using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public class BrowserFileExportServiceTests
{
    [Fact]
    public async Task PrepareTextFileSaveAsync_ImportsModuleAndPreparesSuggestedFile()
    {
        var module = new RecordingJsObjectReference();
        var jsRuntime = new RecordingJsRuntime(module);
        var service = new BrowserFileExportService(jsRuntime);

        await service.PrepareTextFileSaveAsync(
            "diagnostic-snapshot",
            "diagnostic.json",
            """{"schemaVersion":1}""",
            "application/json");

        Assert.Equal("import", jsRuntime.LastIdentifier);
        Assert.Equal("./fileExport.js?v=save-picker-1", jsRuntime.LastArgs[0]);
        Assert.Equal("prepareTextFileSave", module.LastIdentifier);
        Assert.Equal("diagnostic-snapshot", module.LastArgs[0]);
        Assert.Equal("diagnostic.json", module.LastArgs[1]);
        Assert.Equal("""{"schemaVersion":1}""", module.LastArgs[2]);
        Assert.Equal("application/json", module.LastArgs[3]);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly IJSObjectReference _module;

        public RecordingJsRuntime(IJSObjectReference module)
        {
            _module = module;
        }

        public string? LastIdentifier { get; private set; }
        public object?[] LastArgs { get; private set; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args ?? [];
            return new ValueTask<TValue>((TValue)_module);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }

    private sealed class RecordingJsObjectReference : IJSObjectReference
    {
        public string? LastIdentifier { get; private set; }
        public object?[] LastArgs { get; private set; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args ?? [];
            return new ValueTask<TValue>((TValue)(object?)null!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
