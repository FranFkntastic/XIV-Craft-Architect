using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class MarketMafiosoAcquisitionWorkflowServiceTests
{
    [Fact]
    public async Task CreateSingleWorldHandoffAsync_SeparatesCharacterHomeWorldFromPurchaseWorld()
    {
        var client = new RecordingClient();
        var settings = new DictionarySettings
        {
            ["marketmafioso.workshop_host_url"] = "https://example.test/marketmafioso/api/",
            ["marketmafioso.api_key"] = "secret",
            ["marketmafioso.target_character"] = "Eriana Ning",
            ["marketmafioso.target_world"] = "Gilgamesh",
        };
        var service = new MarketMafiosoAcquisitionWorkflowService(client, settings);

        await service.CreateSingleWorldHandoffAsync(new MarketMafiosoSingleWorldHandoff(
            2,
            "Fire Shard",
            "North America",
            "Aether",
            "Faerie",
            20,
            100,
            2_000));

        var request = Assert.IsType<WorkshopHostAcquisitionBatchCreateRequest>(client.CreateRequest);
        Assert.Equal("Eriana Ning", request.TargetCharacterName);
        Assert.Equal("Gilgamesh", request.TargetWorld);
        Assert.Equal("Selected", request.WorldMode);
        Assert.Equal(["Faerie"], request.SelectedWorlds);
        var line = Assert.Single(request.Lines);
        Assert.Equal("TargetQuantity", line.QuantityMode);
        Assert.Equal(20u, line.TargetQuantity);
        Assert.Equal(100u, line.MaxUnitPrice);
        Assert.Equal(2_000u, line.GilCap);
    }

    [Fact]
    public async Task CreateSingleWorldHandoffAsync_ReusesPersistedIdempotencyKeyAfterLostResponse()
    {
        var client = new RecordingClient { FailNextCreate = true };
        var settings = ConfiguredSettings();
        var service = new MarketMafiosoAcquisitionWorkflowService(client, settings);
        var handoff = Handoff();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CreateSingleWorldHandoffAsync(handoff));
        var firstKey = Assert.IsType<WorkshopHostAcquisitionBatchCreateRequest>(client.CreateRequests[0]).IdempotencyKey;

        await service.CreateSingleWorldHandoffAsync(handoff);

        Assert.Equal(firstKey, client.CreateRequests[1].IdempotencyKey);
        var pending = Assert.IsType<MarketMafiosoPendingSubmission>(settings[MarketMafiosoAcquisitionWorkflowService.PendingSubmissionSetting]);
        Assert.Empty(pending.IdempotencyKey);
    }

    [Fact]
    public async Task CreateSingleWorldHandoffAsync_UsesNewIdempotencyKeyWhenDraftChanges()
    {
        var client = new RecordingClient { FailNextCreate = true };
        var settings = ConfiguredSettings();
        var service = new MarketMafiosoAcquisitionWorkflowService(client, settings);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CreateSingleWorldHandoffAsync(Handoff()));
        client.FailNextCreate = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CreateSingleWorldHandoffAsync(Handoff() with { Quantity = 21 }));

        Assert.NotEqual(client.CreateRequests[0].IdempotencyKey, client.CreateRequests[1].IdempotencyKey);
    }

    [Theory]
    [InlineData("PendingPickup", "check the dashboard")]
    [InlineData("Claimed", "Accept the claimed request")]
    [InlineData("AcceptedInPlugin", "Refresh Evidence")]
    [InlineData("Running", "still working")]
    public void DescribeNextEvidenceStep_MapsLifecycleToConcreteUserAction(string status, string expected)
    {
        var message = MarketMafiosoAcquisitionWorkflowService.DescribeNextEvidenceStep(status, "Siren");

        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsyncReportsAcquisitionCapability()
    {
        var client = new RecordingClient();
        var service = new MarketMafiosoAcquisitionWorkflowService(client, ConfiguredSettings());

        var result = await service.TestConnectionAsync(
            "https://example.test/marketmafioso/api/",
            "secret");

        Assert.True(result.AcquisitionQueueAvailable);
        Assert.Equal("MarketMafioso", result.Service);
        Assert.Equal("https://example.test/marketmafioso/api/", client.LastConnection?.ApiBaseUrl);
    }

    [Fact]
    public async Task WaitForEvidenceAsyncPollsUntilMatchingObservationArrives()
    {
        var client = new RecordingClient();
        client.Timelines.Enqueue(new WorkshopHostAcquisitionTimeline
        {
            Request = new WorkshopHostAcquisitionRequestView { Status = "AcceptedInPlugin" },
        });
        client.Timelines.Enqueue(new WorkshopHostAcquisitionTimeline
        {
            Request = new WorkshopHostAcquisitionRequestView { Status = "AcceptedInPlugin" },
            MarketObservations =
            [
                new WorkshopHostMarketObservation
                {
                    ItemId = 2,
                    WorldName = "Siren",
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
        });
        var service = new MarketMafiosoAcquisitionWorkflowService(client, ConfiguredSettings());

        var result = await service.WaitForEvidenceAsync(
            "request-1",
            2,
            "Siren",
            TimeSpan.Zero);

        Assert.Equal("Siren", result.Observation?.WorldName);
        Assert.Equal(2, client.TimelineCalls);
    }

    private static DictionarySettings ConfiguredSettings() => new()
    {
        ["marketmafioso.workshop_host_url"] = "https://example.test/marketmafioso/api/",
        ["marketmafioso.api_key"] = "secret",
        ["marketmafioso.target_character"] = "Eriana Ning",
        ["marketmafioso.target_world"] = "Siren",
    };

    private static MarketMafiosoSingleWorldHandoff Handoff() => new(
        2,
        "Fire Shard",
        "North America",
        "Aether",
        "Faerie",
        20,
        100,
        2_000);

    private sealed class RecordingClient : IWorkshopHostAcquisitionClient
    {
        public WorkshopHostAcquisitionBatchCreateRequest? CreateRequest { get; private set; }
        public List<WorkshopHostAcquisitionBatchCreateRequest> CreateRequests { get; } = [];
        public bool FailNextCreate { get; set; }
        public WorkshopHostConnectionOptions? LastConnection { get; private set; }
        public Queue<WorkshopHostAcquisitionTimeline> Timelines { get; } = new();
        public int TimelineCalls { get; private set; }

        public Task<WorkshopHostCapabilityResponse> GetCapabilitiesAsync(
            WorkshopHostConnectionOptions connection,
            CancellationToken cancellationToken = default)
        {
            LastConnection = connection;
            return Task.FromResult(new WorkshopHostCapabilityResponse
            {
                Service = "MarketMafioso",
                SchemaVersion = 1,
                Capabilities =
                [
                    new WorkshopHostCapabilityDescriptor
                    {
                        Id = "acquisition.queue",
                        Status = "available",
                        SupportedSchemaVersions = [1],
                    },
                ],
            });
        }

        public Task<WorkshopHostAcquisitionRequestView> CreateBatchAsync(
            WorkshopHostConnectionOptions connection,
            WorkshopHostAcquisitionBatchCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            CreateRequests.Add(request);
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new HttpRequestException("Response was lost after the server accepted the request.");
            }
            return Task.FromResult(new WorkshopHostAcquisitionRequestView { Id = "request-1" });
        }

        public Task<WorkshopHostAcquisitionTimeline> GetTimelineAsync(
            WorkshopHostConnectionOptions connection,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            TimelineCalls++;
            return Task.FromResult(Timelines.Count > 0
                ? Timelines.Dequeue()
                : new WorkshopHostAcquisitionTimeline());
        }
    }

    private sealed class DictionarySettings : Dictionary<string, object>, ISettingsService
    {
        public T? Get<T>(string keyPath, T? defaultValue = default) =>
            TryGetValue(keyPath, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string keyPath, T value) => this[keyPath] = value!;

        public Task<T?> GetAsync<T>(string keyPath, T? defaultValue = default) =>
            Task.FromResult(Get(keyPath, defaultValue));

        public Task SetAsync<T>(string keyPath, T value)
        {
            Set(keyPath, value);
            return Task.CompletedTask;
        }

        public Task ResetToDefaultsAsync()
        {
            Clear();
            return Task.CompletedTask;
        }
    }
}
