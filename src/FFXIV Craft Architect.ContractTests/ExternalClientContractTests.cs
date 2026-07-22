using System.Net;
using System.Text;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ExternalClientContractTests
{
    [Fact]
    public async Task WorkshopHostSuccess_PostsExactBatchContract()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {
              "id": "batch-1",
              "revision": 1,
              "status": "PendingPickup",
              "origin": "CraftArchitect",
              "targetCharacterName": "Eriana Ning",
              "targetWorld": "Siren",
              "region": "North America",
              "lines": []
            }
            """);
        var client = new WorkshopHostAcquisitionClient(new HttpClient(handler));

        var result = await client.CreateBatchAsync(
            new WorkshopHostConnectionOptions
            {
                ApiBaseUrl = "https://workshop.test/api",
                ApiKey = "contract-key",
            },
            new WorkshopHostAcquisitionBatchCreateRequest
            {
                IdempotencyKey = "batch-1",
                TargetCharacterName = "Eriana Ning",
                TargetWorld = "Siren",
                WorldMode = "Selected",
                SelectedWorlds = ["Siren"],
                SweepScope = "DataCenter",
                SweepDataCenters = ["Aether"],
                ExpiresInSeconds = 600,
                Lines =
                [
                    new WorkshopHostAcquisitionBatchLineCreateRequest
                    {
                        ItemId = 5064,
                        ItemName = "Silver Ingot",
                        ItemKind = "Material",
                        TargetQuantity = 10,
                        MaxQuantity = 12,
                        HqPolicy = "NqOnly",
                        MaxUnitPrice = 100,
                        GilCap = 1_000,
                    },
                ],
            });

        Assert.Equal("batch-1", result.Id);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://workshop.test/api/acquisition/batches", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("contract-key", Assert.Single(handler.Headers["X-Api-Key"]));
        Assert.Equal("application/json", Assert.Single(handler.Headers["Accept"]));
        Assert.Equal(
            "{\"schemaVersion\":1,\"idempotencyKey\":\"batch-1\",\"origin\":\"CraftArchitect\",\"targetCharacterName\":\"Eriana Ning\",\"targetWorld\":\"Siren\",\"region\":\"North America\",\"worldMode\":\"Selected\",\"selectedWorlds\":[\"Siren\"],\"sweepScope\":\"DataCenter\",\"sweepDataCenters\":[\"Aether\"],\"expiresInSeconds\":600,\"lines\":[{\"itemId\":5064,\"itemName\":\"Silver Ingot\",\"itemKind\":\"Material\",\"quantityMode\":\"TargetQuantity\",\"targetQuantity\":10,\"maxQuantity\":12,\"hqPolicy\":\"NqOnly\",\"maxUnitPrice\":100,\"gilCap\":1000}]}",
            handler.Body);
    }

    [Fact]
    public async Task WorkshopHostError_PreservesStatusAndRemoteDetails()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Forbidden,
            "{\"error\":\"scope_denied\"}");
        var client = new WorkshopHostAcquisitionClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCapabilitiesAsync(
            new WorkshopHostConnectionOptions
            {
                ApiBaseUrl = "https://workshop.test/api",
                ApiKey = "contract-key",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("capability discovery", exception.Message, StringComparison.Ordinal);
        Assert.Contains("scope_denied", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://workshop.test/api/capabilities", handler.RequestUri?.AbsoluteUri);
        Assert.Null(handler.Body);
        Assert.Equal("contract-key", Assert.Single(handler.Headers["X-Api-Key"]));
        Assert.Equal("application/json", Assert.Single(handler.Headers["Accept"]));
    }

    [Fact]
    public async Task ProfileClientConflict_ReturnsRemoteRevisionForResolution()
    {
        const string body = """
            {
              "success": false,
              "conflict": true,
              "remoteObject": {
                "collection": "plans",
                "objectId": "plan-1",
                "payloadJson": "{}",
                "revision": 12,
                "updatedAtUtc": "2026-07-20T12:00:00Z",
                "deleted": false
              }
            }
            """;
        var handler = new RecordingHandler(HttpStatusCode.Conflict, body);
        var client = new ProfileHostClient(new HttpClient(handler));

        var result = await client.PutObjectAsync(
            "https://profile.test/",
            "cap_contract-key",
            ProfileSyncCollections.Plans,
            "plan-1",
            new ProfileSyncPutRequest { PayloadJson = "{}", ExpectedRevision = 10 },
            CancellationToken.None);

        Assert.True(result.Conflict);
        Assert.Equal(12, result.RemoteObject?.Revision);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("https://profile.test/profile-host/objects/plans/plan-1", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("cap_contract-key", Assert.Single(handler.Headers["X-Profile-Key"]));
        Assert.Equal("{\"payloadJson\":\"{}\",\"expectedRevision\":10}", handler.Body);
    }

    [Fact]
    public async Task ProfileClientNonConflictError_ThrowsWithHttpStatus()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"unauthorized\"}");
        var client = new ProfileHostClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteObjectAsync(
            "https://profile.test/",
            "cap_wrong-key",
            ProfileSyncCollections.Plans,
            "plan-1",
            expectedRevision: 1,
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(
            "https://profile.test/profile-host/objects/plans/plan-1?expectedRevision=1",
            handler.RequestUri?.AbsoluteUri);
        Assert.Null(handler.Body);
        Assert.Equal("cap_wrong-key", Assert.Single(handler.Headers["X-Profile-Key"]));
    }

    [Fact]
    public async Task LodestoneClientMalformedSuccess_MapsToParseFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "<!doctype html>", "text/html");
        var service = new HttpLodestoneCrafterLookupService(
            new HttpClient(handler),
            new LodestoneLookupClientOptions(new Uri("https://lookup.test/")),
            NullLogger<HttpLodestoneCrafterLookupService>.Instance);

        var result = await service.SearchAsync(new LodestoneCrafterSearchRequest(
            "Level Checker",
            "Behemoth",
            null));

        Assert.False(result.Succeeded);
        Assert.Equal(LodestoneCrafterLookupFailureKind.ParseFailed, result.FailureKind);
        Assert.Contains("non-JSON response", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "https://lookup.test/lodestone/crafters/search?name=Level%20Checker&world=Behemoth",
            handler.RequestUri?.AbsoluteUri);
        Assert.Null(handler.Body);
        Assert.False(handler.Headers.ContainsKey("X-Api-Key"));
        Assert.False(handler.Headers.ContainsKey("X-Profile-Key"));
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json") : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public IReadOnlyDictionary<string, string[]> Headers { get; private set; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
        }
    }
}
