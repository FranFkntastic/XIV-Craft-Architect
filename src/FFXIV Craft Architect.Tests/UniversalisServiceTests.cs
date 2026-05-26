using System.Net;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class UniversalisServiceTests
{
    [Fact]
    public async Task GetMarketDataBulkAsync_GlobalRetryBatchesMissingItems()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            var itemSegment = request.RequestUri?.Segments.LastOrDefault() ?? string.Empty;

            return itemSegment switch
            {
                "1,2,3,4" => BulkResponse(new[] { 1 }),
                "2,3,4" => BulkResponse(new[] { 2, 3, 4 }),
                "2" => SingleResponse(2),
                "3" => SingleResponse(3),
                "4" => SingleResponse(4),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var service = new UniversalisService(new HttpClient(handler));

        var result = await service.GetMarketDataBulkAsync(
            "Aether",
            new[] { 1, 2, 3, 4 },
            useParallel: false);

        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Keys.Order().ToArray());
        Assert.Contains(requests, path => path.EndsWith("/1,2,3,4", StringComparison.Ordinal));
        Assert.Contains(requests, path => path.EndsWith("/2,3,4", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, path => path.EndsWith("/2", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, path => path.EndsWith("/3", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, path => path.EndsWith("/4", StringComparison.Ordinal));
    }

    private static HttpResponseMessage SingleResponse(int itemId)
    {
        var body = $$"""
            {
              "itemID": {{itemId}},
              "listings": [],
              "averagePrice": 0
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage BulkResponse(IEnumerable<int> itemIds)
    {
        var itemsJson = string.Join(",", itemIds.Select(id =>
            $"\"{id}\":{{\"itemID\":{id},\"listings\":[],\"averagePrice\":0}}"));
        var body = $$"""
            {
              "itemIDs": [{{string.Join(",", itemIds)}}],
              "items": { {{itemsJson}} }
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
