using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class UniversalisRateLimitContractTests
{
    [Fact]
    public async Task ConcurrentBulkFetches_StayBelowUniversalisConnectionLimit()
    {
        var handler = new ConcurrencyRecordingHandler();
        var service = new UniversalisService(new HttpClient(handler));

        var fetches = Enumerable.Range(1, 12)
            .Select(itemId => service.GetMarketDataBulkAsync(
                $"DataCenter-{itemId}",
                [itemId],
                useParallel: true))
            .ToArray();

        var results = await Task.WhenAll(fetches);

        Assert.All(results, result => Assert.Single(result));
        Assert.Equal(12, handler.RequestCount);
        Assert.InRange(handler.MaxConcurrentRequests, 1, UniversalisService.MaxConcurrentApiRequests);
        Assert.True(handler.MaxConcurrentRequests < 8);
    }

    [Fact]
    public async Task RateLimitedChunk_RetriesOnceThenReturnsData()
    {
        var handler = new RateLimitThenSuccessHandler();
        var service = new UniversalisService(new HttpClient(handler));

        var result = await service.GetMarketDataBulkAsync("Aether", [5339], useParallel: false);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(5339, Assert.Single(result).Key);
    }

    private sealed class ConcurrencyRecordingHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maxConcurrentRequests;
        private int _requestCount;

        public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return JsonResponse(request);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref _maxConcurrentRequests);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maxConcurrentRequests, active, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class RateLimitThenSuccessHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber != 1)
            {
                return Task.FromResult(JsonResponse(request));
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request)
    {
        var itemId = int.Parse(request.RequestUri!.Segments[^1]);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"itemID\":{itemId},\"listings\":[]}}",
                Encoding.UTF8,
                "application/json"),
        };
    }
}
