using System.Net;
using System.Text;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public class IndexedDbMarketCacheServiceTests
{
    [Fact]
    public async Task EnsurePopulatedAsync_MissingRegionalData_FetchesDataCentersTwoAtATime()
    {
        var jsRuntime = new RecordingMarketCacheJsRuntime();
        var handler = new ConcurrentMarketFetchHandler();
        var universalis = new UniversalisService(new HttpClient(handler));
        var cache = new IndexedDbMarketCacheService(jsRuntime, universalis);

        var requests = new List<(int itemId, string dataCenter)>
        {
            (101, "Aether"),
            (102, "Crystal"),
            (103, "Dynamis"),
            (104, "Primal")
        };

        var fetchedCount = await cache.EnsurePopulatedAsync(requests, TimeSpan.FromMinutes(5));

        Assert.Equal(4, fetchedCount);
        Assert.Equal(4, handler.MarketRequestCount);
        Assert.Equal(2, handler.MaximumConcurrentMarketRequests);
    }

    private sealed class ConcurrentMarketFetchHandler : HttpMessageHandler
    {
        private int _currentMarketRequests;
        private int _marketRequestCount;
        private int _maximumConcurrentMarketRequests;

        public int MarketRequestCount => Volatile.Read(ref _marketRequestCount);
        public int MaximumConcurrentMarketRequests => Volatile.Read(ref _maximumConcurrentMarketRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/worlds", StringComparison.Ordinal))
            {
                return JsonResponse("""[{"id":1,"name":"TestWorld"}]""");
            }

            if (path.EndsWith("/data-centers", StringComparison.Ordinal))
            {
                return JsonResponse("""[{"name":"Aether","region":"NA","worlds":[1]}]""");
            }

            Interlocked.Increment(ref _marketRequestCount);
            var current = Interlocked.Increment(ref _currentMarketRequests);
            UpdateMaximum(current);

            try
            {
                await Task.Delay(100, cancellationToken);
                var itemId = int.Parse(path.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
                return JsonResponse($$"""
                    {
                      "itemID": {{itemId}},
                      "listings": [],
                      "averagePrice": 0
                    }
                    """);
            }
            finally
            {
                Interlocked.Decrement(ref _currentMarketRequests);
            }
        }

        private void UpdateMaximum(int current)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref _maximumConcurrentMarketRequests);
                if (current <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(
                ref _maximumConcurrentMarketRequests,
                current,
                previous) != previous);
        }

        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingMarketCacheJsRuntime : IJSRuntime
    {
        private readonly Dictionary<string, IndexedDbMarketCacheEntry> _entries = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return new ValueTask<TValue>(Invoke<TValue>(identifier, args));
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return new ValueTask<TValue>(Invoke<TValue>(identifier, args));
        }

        private TValue Invoke<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.loadMarketDataBulk")
            {
                return (TValue)(object)new List<IndexedDbMarketCacheEntry>();
            }

            if (identifier == "IndexedDB.getMarketCacheStats")
            {
                return (TValue)(object)new IndexedDbCacheStats();
            }

            if (identifier == "IndexedDB.deleteStaleMarketData" ||
                identifier == "IndexedDB.deleteOldestEntries")
            {
                return (TValue)(object)0;
            }

            if (identifier == "IndexedDB.saveMarketData")
            {
                var key = (string)args![0]!;
                var entry = (IndexedDbMarketCacheEntry)args[1]!;
                _entries[key] = entry;
                return default!;
            }

            if (identifier == "IndexedDB.loadMarketData")
            {
                var key = (string)args![0]!;
                _entries.TryGetValue(key, out var entry);
                return (TValue)(object?)entry!;
            }

            return default!;
        }
    }
}
