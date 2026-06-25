using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

namespace FFXIV_Craft_Architect.Tests;

public sealed class XivDataGatewayProviderTests
{
    [Fact]
    public async Task SearchItemsAsync_MapsGarlandItemResultsToGatewaySummaries()
    {
        var garland = new StubGarlandService
        {
            SearchResults =
            [
                new GarlandSearchResult
                {
                    Type = "item",
                    IdRaw = 5057,
                    Object = new GarlandSearchObject
                    {
                        Name = "Darksteel Nugget",
                        IconIdRaw = 21203,
                    },
                },
            ],
        };
        var provider = new GarlandXivItemDataProvider(garland);

        var results = await provider.SearchItemsAsync("darksteel", 20, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(5057u, item.ItemId);
        Assert.Equal("Darksteel Nugget", item.Name);
        Assert.Equal(21203, item.IconId);
        Assert.Equal("garland", item.Source);
    }

    [Fact]
    public async Task SearchItemsAsync_FiltersInvalidGarlandRows()
    {
        var garland = new StubGarlandService
        {
            SearchResults =
            [
                new GarlandSearchResult
                {
                    Type = "recipe",
                    IdRaw = 5057,
                    Object = new GarlandSearchObject { Name = "Darksteel Nugget" },
                },
                new GarlandSearchResult
                {
                    Type = "item",
                    IdRaw = 0,
                    Object = new GarlandSearchObject { Name = "Missing Id" },
                },
                new GarlandSearchResult
                {
                    Type = "item",
                    IdRaw = 2,
                    Object = new GarlandSearchObject { Name = "Fire Shard" },
                },
            ],
        };
        var provider = new GarlandXivItemDataProvider(garland);

        var results = await provider.SearchItemsAsync("shard", 20, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(2u, item.ItemId);
        Assert.Equal("Fire Shard", item.Name);
    }

    [Fact]
    public async Task GetItemAsync_MapsGarlandItemDetailToGatewaySummary()
    {
        var garland = new StubGarlandService
        {
            Items =
            {
                [5057] = new GarlandItem
                {
                    Id = 5057,
                    Name = "Darksteel Nugget",
                    IconId = 21203,
                },
            },
        };
        var provider = new GarlandXivItemDataProvider(garland);

        var item = await provider.GetItemAsync(5057, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal("Darksteel Nugget", item!.Name);
        Assert.Equal(5057u, item.ItemId);
        Assert.Equal(21203, item.IconId);
        Assert.Equal("garland", item.Source);
    }

    private sealed class StubGarlandService : IGarlandService
    {
        public List<GarlandSearchResult> SearchResults { get; init; } = [];

        public Dictionary<int, GarlandItem> Items { get; init; } = [];

        public Task<List<GarlandSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(SearchResults);

        public Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default) =>
            Task.FromResult(Items.GetValueOrDefault(itemId));

        public Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default) =>
            Task.FromResult<Recipe?>(null);

        public Task<Dictionary<int, GarlandItem>> GetItemsAsync(
            IEnumerable<int> itemIds,
            bool useParallel = true,
            CancellationToken ct = default) =>
            Task.FromResult(Items);
    }
}
