using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Tests;

public sealed class XivDataGatewayEndpointTests
{
    [Fact]
    public async Task SearchItems_ReturnsGatewayJson()
    {
        using var factory = CreateFactory(new StubXivItemDataProvider
        {
            SearchResults =
            [
                new XivItemSummary
                {
                    ItemId = 5057,
                    Name = "Darksteel Nugget",
                    IconId = 21203,
                    Source = "test",
                    SourceUpdatedAtUtc = DateTimeOffset.UnixEpoch,
                },
            ],
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/xivdata/items/search?q=darksteel");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<XivItemSearchResponse>();
        Assert.NotNull(payload);
        Assert.Equal("darksteel", payload!.Query);
        var item = Assert.Single(payload.Items);
        Assert.Equal(5057u, item.ItemId);
        Assert.Equal("Darksteel Nugget", item.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    public async Task SearchItems_RejectsEmptyOrTinyNonNumericQueries(string query)
    {
        using var factory = CreateFactory(new StubXivItemDataProvider());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/xivdata/items/search?q={Uri.EscapeDataString(query)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<XivDataErrorResponse>();
        Assert.Equal("invalid_query", payload?.ErrorCode);
    }

    [Fact]
    public async Task GetItem_ReturnsResolvedItem()
    {
        using var factory = CreateFactory(new StubXivItemDataProvider
        {
            Items =
            {
                [5057] = new XivItemSummary
                {
                    ItemId = 5057,
                    Name = "Darksteel Nugget",
                    Source = "test",
                    SourceUpdatedAtUtc = DateTimeOffset.UnixEpoch,
                },
            },
        });
        using var client = factory.CreateClient();

        var item = await client.GetFromJsonAsync<XivItemSummary>("/xivdata/items/5057");

        Assert.NotNull(item);
        Assert.Equal("Darksteel Nugget", item!.Name);
    }

    [Fact]
    public async Task GetItem_ReturnsNotFoundForUnresolvedItem()
    {
        using var factory = CreateFactory(new StubXivItemDataProvider());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/xivdata/items/99999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<XivDataErrorResponse>();
        Assert.Equal("item_not_found", payload?.ErrorCode);
    }

    [Fact]
    public async Task SearchItems_ReturnsProviderUnavailableWhenProviderFails()
    {
        using var factory = CreateFactory(new StubXivItemDataProvider
        {
            SearchFailure = new HttpRequestException("provider down"),
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/xivdata/items/search?q=darksteel");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<XivDataErrorResponse>();
        Assert.Equal("provider_unavailable", payload?.ErrorCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(IXivItemDataProvider provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IXivItemDataProvider>();
                services.AddSingleton(provider);
            });
        });

    private sealed class StubXivItemDataProvider : IXivItemDataProvider
    {
        public IReadOnlyList<XivItemSummary> SearchResults { get; init; } = [];

        public Dictionary<uint, XivItemSummary> Items { get; init; } = [];

        public Exception? SearchFailure { get; init; }

        public Task<IReadOnlyList<XivItemSummary>> SearchItemsAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            if (SearchFailure != null)
                throw SearchFailure;

            return Task.FromResult(SearchResults);
        }

        public Task<XivItemSummary?> GetItemAsync(
            uint itemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(itemId));
    }
}
