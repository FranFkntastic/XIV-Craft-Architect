using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployLodestone)]
public sealed class XivDataGatewayEndpointTests
{
    [Fact]
    public async Task SearchItems_ReturnsMatchingItems()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/xivdata/items/search?q=Varn&limit=12");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<XivItemSearchResponse>();
        var item = Assert.Single(payload?.Items ?? []);
        Assert.Equal(7017, item.ItemId);
        Assert.Equal("Varnish", item.Name);
        Assert.Equal("Item", item.ItemType);
        Assert.Equal(21000, item.IconId);
    }

    [Fact]
    public async Task GetItem_ReturnsItemById()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/xivdata/items/7017");

        response.EnsureSuccessStatusCode();
        var item = await response.Content.ReadFromJsonAsync<XivItemSummary>();
        Assert.Equal(7017, item?.ItemId);
        Assert.Equal("Varnish", item?.Name);
    }

    [Fact]
    public async Task SearchItems_RejectsBlankQuery()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/xivdata/items/search?q=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static WebApplicationFactory<NetStoneLodestoneCrafterLookupService> CreateApplication()
    {
        return new WebApplicationFactory<NetStoneLodestoneCrafterLookupService>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IGarlandService>();
                    services.AddSingleton<IGarlandService, FakeGarlandService>();
                });
            });
    }

    private sealed class FakeGarlandService : IGarlandService
    {
        public Task<List<GarlandSearchResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            return Task.FromResult<List<GarlandSearchResult>>(
            [
                new()
                {
                    Type = "item",
                    IdRaw = 7017,
                    Object = new GarlandSearchObject
                    {
                        Name = "Varnish",
                        IconIdRaw = 21000,
                    },
                },
            ]);
        }

        public Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default)
        {
            return Task.FromResult<GarlandItem?>(itemId == 7017
                ? new GarlandItem
                {
                    Id = 7017,
                    Name = "Varnish",
                    IconId = 21000,
                }
                : null);
        }

        public Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<Dictionary<int, GarlandItem>> GetItemsAsync(
            IEnumerable<int> itemIds,
            bool useParallel = true,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record XivItemSearchResponse(IReadOnlyList<XivItemSummary> Items);

    private sealed record XivItemSummary(int ItemId, string Name, string ItemType, int IconId);
}
