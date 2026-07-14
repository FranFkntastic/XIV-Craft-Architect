using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkshopHostAcquisitionClientTests
{
    [Fact]
    public async Task CreateBatchAsyncPostsCraftArchitectIntentToCanonicalEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "batch-1",
              "revision": 1,
              "status": "PendingPickup",
              "origin": "CraftArchitect",
              "targetCharacterName": "Eriana Ning",
              "targetWorld": "Sargatanas",
              "region": "North America",
              "lines": [{ "lineId": "line-1", "itemId": 5064, "targetQuantity": 10 }]
            }
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dev.xivcraftarchitect.com/"),
        };
        var client = new WorkshopHostAcquisitionClient(httpClient);

        var result = await client.CreateBatchAsync(
            Connection(),
            new WorkshopHostAcquisitionBatchCreateRequest
            {
                IdempotencyKey = "ca-plan-1",
                TargetCharacterName = "Eriana Ning",
                TargetWorld = "Sargatanas",
                WorldMode = "Selected",
                SelectedWorlds = ["Siren"],
                Lines =
                [
                    new WorkshopHostAcquisitionBatchLineCreateRequest
                    {
                        ItemId = 5064,
                        ItemName = "Silver Ingot",
                        TargetQuantity = 10,
                        MaxQuantity = 10,
                        MaxUnitPrice = 100,
                        GilCap = 1_000,
                    },
                ],
            });

        Assert.Equal("batch-1", result.Id);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("secret", Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        using var json = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("CraftArchitect", json.RootElement.GetProperty("origin").GetString());
        Assert.Equal("Eriana Ning", json.RootElement.GetProperty("targetCharacterName").GetString());
        Assert.Equal("Siren", json.RootElement.GetProperty("selectedWorlds")[0].GetString());
    }

    [Fact]
    public async Task GetTimelineAsyncConvertsPartialObservationToMarketEvidence()
    {
        using var handler = new CapturingHandler("""
            {
              "request": { "id": "batch-1", "status": "Running", "lines": [] },
              "marketObservations": [{
                "observationId": "observation-1",
                "requestId": "batch-1",
                "attemptId": "attempt-1",
                "sequence": 3,
                "lineId": "line-1",
                "itemId": 5064,
                "itemName": "Silver Ingot",
                "dataCenter": "Aether",
                "worldName": "Siren",
                "readState": "Partial",
                "reportedListingCount": 5,
                "readableListingCount": 1,
                "listingCapacity": 2,
                "isTruncated": true,
                "observedAtUtc": "2026-07-14T15:00:00Z",
                "listings": [{
                  "listingId": "listing-1",
                  "retainerId": "retainer-1",
                  "retainerName": "Seller",
                  "quantity": 10,
                  "unitPrice": 50,
                  "isHq": false
                }]
              }]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopHostAcquisitionClient(httpClient);

        var timeline = await client.GetTimelineAsync(Connection(), "batch-1");
        var evidence = Assert.Single(timeline.MarketObservations).ToMarketEvidenceSnapshot();

        Assert.Equal(MarketEvidenceCompleteness.Partial, evidence.Completeness);
        Assert.True(evidence.IsTruncated);
        Assert.Equal(5, evidence.ReportedListingCount);
        Assert.Equal("listing-1", Assert.Single(evidence.Listings).ListingId);
    }

    private static WorkshopHostConnectionOptions Connection() =>
        new()
        {
            ApiBaseUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            ApiKey = "secret",
        };

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            };
        }
    }
}
