using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public sealed class IndexedDbProfileSyncInteropTests
{
    [Fact]
    public async Task LoadAllSettingsAsync_CallsIndexedDbLoadAllSettings()
    {
        var js = new RecordingJsRuntime(new Dictionary<string, object?>
        {
            ["IndexedDB.loadAllSettings"] = new Dictionary<string, string>
            {
                ["market.default_datacenter"] = "\"Aether\""
            }
        });
        var service = new IndexedDbService(js);

        var settings = await service.LoadAllSettingsAsync();

        Assert.True(settings.ContainsKey("market.default_datacenter"));
        Assert.Contains(js.Calls, call => call.Identifier == "IndexedDB.loadAllSettings");
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly Dictionary<string, object?> _responses;
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

        public RecordingJsRuntime(Dictionary<string, object?> responses)
        {
            _responses = responses;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args));
            return new ValueTask<TValue>((TValue)_responses[identifier]!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }
}
