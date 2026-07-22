using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ExternalWireContractTests
{
    [Fact]
    public void UniversalisFixture_MapsRawWireDataToPlannerEvidence()
    {
        const string json = """
            {
              "itemID": 5333,
              "dcName": "Aether",
              "lastUploadTime": 1784563200000,
              "worldUploadTimes": { "63": 1784563199000 },
              "listings": [{
                "pricePerUnit": 145,
                "quantity": 12,
                "worldName": "Siren",
                "dataCenterName": "Aether",
                "retainerName": "Contract Seller",
                "hq": true,
                "lastReviewTime": 1784563100
              }],
              "averagePrice": 150.5,
              "minPriceHQ": 145,
              "minPriceNQ": 149
            }
            """;

        var response = JsonSerializer.Deserialize<UniversalisResponse>(json)!;
        var fetchedAtUtc = new DateTime(2026, 7, 20, 12, 5, 0, DateTimeKind.Utc);
        var evidence = UniversalisMarketDataMapper.ToCachedMarketData(
            response.ItemId,
            response.DataCenterName!,
            response,
            new WorldData { WorldIdToName = new Dictionary<int, string> { [63] = "Siren" } },
            fetchedAtUtc);

        Assert.Equal(5333, response.ItemId);
        Assert.Equal((5333, "Aether", fetchedAtUtc), (evidence.ItemId, evidence.DataCenter, evidence.FetchedAt));
        Assert.Equal(1784563200000, evidence.LastUploadTimeUnixMilliseconds);
        Assert.Equal(150.5m, evidence.DCAveragePrice);
        var world = Assert.Single(evidence.Worlds);
        Assert.Equal(((int?)63, "Siren"), (world.WorldId, world.WorldName));
        Assert.Equal(1784563199000, world.LastUploadTimeUnixMilliseconds);
        Assert.Equal(MarketEvidenceOrigin.Universalis, world.EvidenceOrigin);
        Assert.Equal(MarketEvidenceCompleteness.Complete, world.EvidenceCompleteness);
        var listing = Assert.Single(world.Listings);
        Assert.Equal((145L, 12, "Contract Seller", true, (long?)1784563100L),
            (listing.PricePerUnit, listing.Quantity, listing.RetainerName, listing.IsHq, listing.LastReviewTimeUnix));
    }

    [Fact]
    public void GarlandFixture_PreservesDraftRecipeWithoutInventingUnlockItemId()
    {
        const string json = """
            {
              "item": {
                "id": 24361,
                "name": "Modified Coelacanth-class Bridge",
                "icon": "t/58056",
                "craft": [{
                  "id": "fc563",
                  "job": 0,
                  "rlvl": 1,
                  "lvl": 1,
                  "unlockId": "draft30",
                  "ingredients": [{ "id": 26521, "amount": 1, "name": "Synthetic Fiber" }]
                }]
              }
            }
            """;

        var response = JsonSerializer.Deserialize<GarlandItemResponse>(json)!;
        var craft = Assert.Single(response.Item.Crafts!);

        Assert.Equal(24361, response.Item.Id);
        Assert.Equal("Modified Coelacanth-class Bridge", response.Item.Name);
        Assert.Equal(0, response.Item.IconId);
        Assert.Equal("fc563", craft.Id);
        Assert.Null(craft.UnlockItemId);
        Assert.Equal((26521, 1, "Synthetic Fiber"),
            (Assert.Single(craft.Ingredients).Id, craft.Ingredients[0].Amount, craft.Ingredients[0].Name));
    }

    [Fact]
    public async Task LodestoneFixture_DeserializesNameWorldAndProfileIdentity()
    {
        const string json = """
            {
              "value": [{
                "lodestoneCharacterId": "16331040",
                "displayName": "Level Checker",
                "worldName": "Behemoth",
                "dataCenter": "Primal",
                "lodestoneProfileUrl": "https://na.finalfantasyxiv.com/lodestone/character/16331040/"
              }],
              "failureKind": 0,
              "errorMessage": null
            }
            """;
        var service = new HttpLodestoneCrafterLookupService(
            new HttpClient(new FixedHandler(HttpStatusCode.OK, json)) { BaseAddress = new Uri("https://lookup.test/") },
            new LodestoneLookupClientOptions(new Uri("https://lookup.test/")),
            NullLogger<HttpLodestoneCrafterLookupService>.Instance);

        var result = await service.SearchAsync(new LodestoneCrafterSearchRequest(
            "Level Checker",
            "Behemoth",
            null));

        Assert.True(result.Succeeded);
        var candidate = Assert.Single(result.Value!);
        Assert.Equal(("16331040", "Level Checker", "Behemoth", "Primal"),
            (candidate.LodestoneCharacterId, candidate.DisplayName, candidate.WorldName, candidate.DataCenter));
    }

    [Fact]
    public async Task XivDataSearch_UsesNameQueryAndReturnsStableItemIdentity()
    {
        var provider = new FakeXivItemDataProvider();
        await using var application = CreateXivDataApplication(provider);
        using var client = application.CreateClient();

        var payload = await client.GetFromJsonAsync<XivItemSearchResponse>("/xivdata/items/search?q=Varn&limit=12");

        Assert.Equal("Varn", provider.LastQuery);
        var item = Assert.Single(payload!.Items);
        Assert.Equal((7017, "Varnish", "Item", 21000),
            (item.ItemId, item.Name, item.ItemType, item.IconId));
    }

    [Fact]
    public async Task XivDataUpstreamFailure_ReturnsVersionStableServiceError()
    {
        var provider = new FakeXivItemDataProvider(throwHttpError: true);
        await using var application = CreateXivDataApplication(provider);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/xivdata/items/search?q=Varn");
        var error = await response.Content.ReadFromJsonAsync<XivDataErrorResponse>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upstream_unavailable", error?.ErrorCode);
        Assert.Equal("The Garland item data source is unavailable.", error?.Message);
    }

    private static WebApplicationFactory<Program> CreateXivDataApplication(IXivItemDataProvider provider) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IXivItemDataProvider>();
                    services.AddSingleton(provider);
                });
            });

    private sealed class FakeXivItemDataProvider(bool throwHttpError = false) : IXivItemDataProvider
    {
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<XivItemSummary>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            if (throwHttpError)
            {
                throw new HttpRequestException("Garland unavailable");
            }

            return Task.FromResult<IReadOnlyList<XivItemSummary>>(
                [new XivItemSummary(7017, "Varnish", "Item", 21000)]);
        }

        public Task<XivItemSummary?> GetItemAsync(int itemId, CancellationToken cancellationToken) =>
            Task.FromResult<XivItemSummary?>(new XivItemSummary(itemId, "Varnish", "Item", 21000));
    }

    private sealed class FixedHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
