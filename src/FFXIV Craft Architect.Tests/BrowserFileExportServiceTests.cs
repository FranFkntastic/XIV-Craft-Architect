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

    [Fact]
    public async Task SaveTextFileAsync_PreparesThenStartsBrowserSave()
    {
        var module = new RecordingJsObjectReference();
        var jsRuntime = new RecordingJsRuntime(module);
        var service = new BrowserFileExportService(jsRuntime);

        await service.SaveTextFileAsync(
            "craft-appraisal-quote",
            "quote.json",
            """{"schemaVersion":1}""",
            "application/json");

        Assert.Equal(["prepareTextFileSave", "savePreparedFile"], module.Identifiers);
        Assert.Equal("craft-appraisal-quote", module.Invocations[0].Args[0]);
        Assert.Equal("quote.json", module.Invocations[0].Args[1]);
        Assert.Equal("""{"schemaVersion":1}""", module.Invocations[0].Args[2]);
        Assert.Equal("application/json", module.Invocations[0].Args[3]);
        Assert.Equal("craft-appraisal-quote", module.Invocations[1].Args[0]);
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
        public List<JsInvocation> Invocations { get; } = [];
        public IReadOnlyList<string> Identifiers => Invocations.Select(invocation => invocation.Identifier).ToArray();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args ?? [];
            Invocations.Add(new JsInvocation(identifier, LastArgs));
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

    private sealed record JsInvocation(string Identifier, object?[] Args);
}
