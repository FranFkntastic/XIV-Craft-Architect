using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeCompanyWebIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CompanyId CompanyId =
        new(Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a4000"));

    [Fact]
    public async Task LocalOnlyOrderSave_RemainsUsableWithoutCallingCompanyClient()
    {
        var client = new FakeTradeCompanyClient();
        var local = new FakeOrderLocalStore();
        var service = CreateOrderService(client, local, out _);
        var profile = Profile(remoteId: null);
        var order = Order(profile.Id, "Local commission");

        var result = await service.SaveAsync(profile, order);

        Assert.True(result.LocalSaved);
        Assert.Equal(TradeCompanyMutationDisposition.LocalOnly, result.Disposition);
        Assert.Equal(TradeSyncState.LocalOnly, Assert.Single(local.Orders).SyncState);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task ConnectedOrderSave_UsesCanonicalRevisionsAndStoresAuthoritativeResult()
    {
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 4)
        };
        client.MutationHandler = request => Applied(request, companyRevision: 5, recordRevision: 1);
        var local = new FakeOrderLocalStore();
        var service = CreateOrderService(client, local, out var orchestrator);
        var profile = Profile(CompanyId.ToString());
        await orchestrator.RefreshAsync(profile);
        var order = Order(profile.Id, "Canonical commission");

        var result = await service.SaveAsync(profile, order);

        Assert.Equal(TradeCompanyMutationDisposition.Synced, result.Disposition);
        Assert.Equal(TradeSyncState.Synced, result.SavedOrder.SyncState);
        Assert.Equal(order.Id.ToString("D"), result.SavedOrder.RemoteId);
        var mutation = Assert.Single(client.Mutations);
        Assert.Equal(new CompanyRevision(4), mutation.ExpectedCompanyRevision);
        Assert.Equal(CompanyRecordRevision.None, mutation.ExpectedRecordRevision);
        var sentOrder = JsonSerializer.Deserialize<TradeOrder>(mutation.PayloadJson, JsonOptions);
        Assert.Equal(TradeSyncState.Synced, sentOrder?.SyncState);
        Assert.Equal(TradeCompanyConnectionState.Current, orchestrator.Connection.State);
    }

    [Fact]
    public async Task OfflineOrderSave_QueuesStableIdempotentRetry()
    {
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 1),
            ThrowMutations = true
        };
        var local = new FakeOrderLocalStore();
        var service = CreateOrderService(client, local, out var orchestrator);
        var profile = Profile(CompanyId.ToString());
        await orchestrator.RefreshAsync(profile);
        var order = Order(profile.Id, "Pending commission");

        var pending = await service.SaveAsync(profile, order);
        var firstRequest = Assert.Single(client.Mutations);
        Assert.Equal(TradeCompanyMutationDisposition.Pending, pending.Disposition);
        Assert.Equal(TradeSyncState.PendingSync, pending.SavedOrder.SyncState);
        Assert.Equal(1, orchestrator.Connection.PendingCount);

        client.ThrowMutations = false;
        client.MutationHandler = request => Applied(request, companyRevision: 2, recordRevision: 1);
        var retried = await service.RetryPendingAsync();

        Assert.Single(retried);
        Assert.Equal(firstRequest.IdempotencyKey, client.Mutations.Last().IdempotencyKey);
        Assert.Equal(0, orchestrator.Connection.PendingCount);
        Assert.Equal(TradeSyncState.Synced, local.Orders.Single().SyncState);
    }

    [Fact]
    public async Task StaleOrderSave_PreservesLocalWorkAndReturnsCompanyVersion()
    {
        var profile = Profile(CompanyId.ToString());
        var remote = Order(profile.Id, "Remote title");
        var remoteRecord = Envelope(
            TradeCompanyRecordKinds.Order,
            remote.Id.ToString("D"),
            remote,
            companyRevision: 2,
            recordRevision: 2);
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 2),
            Changes = new TradeCompanyChangeSet(CompanyId, new CompanyRevision(2), [remoteRecord])
        };
        client.MutationHandler = _ => new TradeCompanyMutationResult(
            TradeCompanyMutationStatus.Conflict,
            null,
            remoteRecord,
            "stale_record",
            "The order changed in another browser.");
        var local = new FakeOrderLocalStore();
        var service = CreateOrderService(client, local, out _);
        await service.SynchronizeAsync(profile);
        var localEdit = TradeOrderWorkflow.CopyOrder(remote);
        localEdit.Title = "Local title";

        var result = await service.SaveAsync(profile, localEdit);

        Assert.True(result.HasConflict);
        Assert.Equal("Local title", result.SavedOrder.Title);
        Assert.Equal(TradeSyncState.Conflict, result.SavedOrder.SyncState);
        Assert.Equal("Remote title", result.CurrentRemoteOrder?.Title);
        Assert.Equal(TradeSyncState.Synced, result.CurrentRemoteOrder?.SyncState);
    }

    [Fact]
    public async Task CompanyRefresh_AppliesRemoteOrderToTheExistingLocalRepresentation()
    {
        var profile = Profile(CompanyId.ToString());
        var remote = Order(profile.Id, "Edited elsewhere");
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 8),
            Changes = new TradeCompanyChangeSet(
                CompanyId,
                new CompanyRevision(8),
                [
                    Envelope(
                        TradeCompanyRecordKinds.Order,
                        remote.Id.ToString("D"),
                        remote,
                        companyRevision: 8,
                        recordRevision: 3)
                ])
        };
        var local = new FakeOrderLocalStore();
        var service = CreateOrderService(client, local, out _);

        await service.SynchronizeAsync(profile);

        var saved = Assert.Single(local.Orders);
        Assert.Equal("Edited elsewhere", saved.Title);
        Assert.Equal(TradeSyncState.Synced, saved.SyncState);
        Assert.Equal(remote.Id.ToString("D"), saved.RemoteId);
    }

    [Fact]
    public async Task AcceptInterest_UsesOneCanonicalCollaborationMutationAndAppliesReturnedOrder()
    {
        var profile = Profile(CompanyId.ToString());
        var order = Order(profile.Id, "Claimable commission");
        var crafterId = Guid.Parse("d4dc0ff5-4436-4236-94ec-932e9af59c6f");
        var claim = new TradeCommissionInterest(
            "claim-1",
            order.Id,
            "discord-user-1",
            "Helpful Crafter",
            TradeCommissionInterestState.Pending,
            null,
            DateTime.UnixEpoch);
        var orderRecord = Envelope(
            TradeCompanyRecordKinds.Order,
            order.Id.ToString("D"),
            order,
            companyRevision: 1,
            recordRevision: 4);
        var claimRecord = Envelope(
            TradeCompanyRecordKinds.Collaboration,
            claim.ClaimId,
            claim,
            companyRevision: 2,
            recordRevision: 1);
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 2),
            Changes = new TradeCompanyChangeSet(
                CompanyId,
                new CompanyRevision(2),
                [orderRecord, claimRecord])
        };
        client.MutationHandler = request =>
        {
            var command = JsonSerializer.Deserialize<TradeCommissionInterestResolutionCommand>(
                request.PayloadJson,
                JsonOptions)!;
            var assigned = TradeOrderWorkflow.CopyOrder(order);
            assigned.AssignedCrafterId = command.CrafterId;
            assigned.Status = TradeOrderWorkflow.ResolveStatusForAssignment(
                assigned.Status,
                assigned.AssignedCrafterId);
            var accepted = claim with { State = TradeCommissionInterestState.Accepted };
            return Applied(
                request with
                {
                    PayloadJson = JsonSerializer.Serialize(
                        new TradeCommissionInterestResolutionReceipt(accepted, assigned),
                        JsonOptions)
                },
                companyRevision: 3,
                recordRevision: 2);
        };
        var local = new FakeOrderLocalStore();
        var orderService = CreateOrderService(client, local, out var orchestrator);
        await orderService.SynchronizeAsync(profile);
        var collaboration = new TradeCompanyCollaborationService(orchestrator, orderService);

        var result = await collaboration.AcceptInterestAsync(order, claim, crafterId);

        Assert.True(result.Success);
        Assert.Equal(TradeCompanyRecordKinds.Collaboration, Assert.Single(client.Mutations).RecordKind);
        var saved = Assert.Single(local.Orders);
        Assert.Equal(crafterId, saved.AssignedCrafterId);
        Assert.Equal(TradeOrderStatus.Assigned, saved.Status);
        Assert.Equal(TradeSyncState.Synced, saved.SyncState);
    }

    [Fact]
    public async Task DiscordPublication_IsBlockedUntilTheCanonicalOrderIsCurrent()
    {
        var profile = Profile(CompanyId.ToString());
        var order = Order(profile.Id, "Unsynced commission");
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 1)
        };
        var local = new FakeOrderLocalStore();
        var orderService = CreateOrderService(client, local, out var orchestrator);
        await orchestrator.RefreshAsync(profile);
        var collaboration = new TradeCompanyCollaborationService(orchestrator, orderService);

        var result = await collaboration.PublishToDiscordAsync(
            order,
            new CommissionBriefDocument { Title = order.Title });

        Assert.False(result.Success);
        Assert.Equal(TradeCompanyMutationDisposition.Rejected, result.Disposition);
        Assert.Contains("Sync this order", result.Message, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task DiscordPublication_UsesOrderRevisionAndKeepsReturnedDeliveryState()
    {
        var profile = Profile(CompanyId.ToString());
        var order = Order(profile.Id, "Publishable commission");
        var orderRecord = Envelope(
            TradeCompanyRecordKinds.Order,
            order.Id.ToString("D"),
            order,
            companyRevision: 3,
            recordRevision: 7);
        var client = new FakeTradeCompanyClient
        {
            Identity = Identity(revision: 3),
            Changes = new TradeCompanyChangeSet(
                CompanyId,
                new CompanyRevision(3),
                [orderRecord])
        };
        client.MutationHandler = request =>
        {
            var command = JsonSerializer.Deserialize<TradeCommissionPublicationCommand>(
                request.PayloadJson,
                JsonOptions)!;
            Assert.Equal(TradeCommissionDestination.DiscordChannel, command.Destination);
            Assert.Equal(new CompanyRecordRevision(7), command.OrderRevision);
            return Applied(
                request with
                {
                    PayloadJson = JsonSerializer.Serialize(
                        new TradeCommissionPublicationProjection(
                            order.Id,
                            TradeCommissionDestination.DiscordChannel,
                            TradeCommissionDeliveryState.Pending,
                            null,
                            "The Studium commissions",
                            DateTime.UnixEpoch,
                            "Waiting for Discord delivery."),
                        JsonOptions)
                },
                companyRevision: 4,
                recordRevision: 1);
        };
        var local = new FakeOrderLocalStore();
        var orderService = CreateOrderService(client, local, out var orchestrator);
        await orderService.SynchronizeAsync(profile);
        var collaboration = new TradeCompanyCollaborationService(orchestrator, orderService);

        var result = await collaboration.PublishToDiscordAsync(
            order,
            new CommissionBriefDocument { Title = order.Title });

        Assert.True(result.Success);
        Assert.Equal(TradeCommissionDeliveryState.Pending, result.Publication?.State);
        Assert.Equal(
            TradeCommissionDeliveryState.Pending,
            collaboration.GetPublication(order.Id)?.State);

        client.MutationHandler = request => Applied(
            request,
            companyRevision: 5,
            recordRevision: 2);
        var incomplete = await collaboration.PublishToDiscordAsync(
            order,
            new CommissionBriefDocument { Title = order.Title });
        Assert.False(incomplete.Success);
        Assert.Contains("did not return publication delivery state", incomplete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TradeOrders_ComponentKeepsCollaborationAndMutationsInContext()
    {
        var root = LocateRepositoryRoot();
        var pages = Path.Combine(root, "src", "FFXIV Craft Architect.Web", "Pages");
        var razor = File.ReadAllText(Path.Combine(pages, "TradeOrders.razor"));
        var codeBehind = File.ReadAllText(Path.Combine(pages, "TradeOrders.razor.cs"));
        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "Program.cs"));
        var partials = Directory.GetFiles(pages, "TradeOrders*.cs")
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains("<MudTabPanel Text=\"Collaboration\">", razor, StringComparison.Ordinal);
        Assert.Contains("Crafter interest", razor, StringComparison.Ordinal);
        Assert.Contains("Retry Company Sync", razor, StringComparison.Ordinal);
        Assert.Contains("Use Company Version", razor, StringComparison.Ordinal);
        Assert.Contains("TradeOrderMutations.SaveAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AddTradeCompanyWebIntegration", program, StringComparison.Ordinal);
        Assert.DoesNotContain("TradeOrderProfileSyncAdapter", program, StringComparison.Ordinal);
        Assert.DoesNotContain("TradeCompanyProfileSyncAdapter", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            partials,
            source => source.Contains(
                "TradeOperationsPersistence.SaveOrderAsync",
                StringComparison.Ordinal));
    }

    private static TradeOrderMutationService CreateOrderService(
        FakeTradeCompanyClient client,
        FakeOrderLocalStore local,
        out TradeCompanyClientOrchestrator orchestrator)
    {
        orchestrator = new TradeCompanyClientOrchestrator(client);
        return new TradeOrderMutationService(local, orchestrator);
    }

    private static TradeCompanyProfile Profile(string? remoteId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "The Studium",
            RemoteId = remoteId
        };

    private static TradeOrder Order(Guid companyProfileId, string title) =>
        new()
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyProfileId,
            Title = title,
            Status = TradeOrderStatus.ReadyToAssign
        };

    private static TradeCompanyIdentity Identity(long revision) =>
        new(
            CompanyId,
            "The Studium",
            new CompanyRevision(revision),
            DateTime.UnixEpoch,
            DateTime.UnixEpoch);

    private static TradeCompanyRecordEnvelope Envelope<T>(
        string kind,
        string id,
        T payload,
        long companyRevision,
        long recordRevision) =>
        new(
            CompanyId,
            kind,
            id,
            JsonSerializer.Serialize(payload, JsonOptions),
            new CompanyRecordRevision(recordRevision),
            new CompanyRevision(companyRevision),
            DateTime.UnixEpoch);

    private static TradeCompanyMutationResult Applied(
        TradeCompanyMutationRequest request,
        long companyRevision,
        long recordRevision) =>
        new(
            TradeCompanyMutationStatus.Applied,
            new TradeCompanyRecordEnvelope(
                request.CompanyId,
                request.RecordKind,
                request.RecordId,
                request.PayloadJson,
                new CompanyRecordRevision(recordRevision),
                new CompanyRevision(companyRevision),
                DateTime.UtcNow));

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class FakeOrderLocalStore : ITradeOrderLocalStore
    {
        private readonly Dictionary<Guid, TradeOrder> _orders = [];

        public IReadOnlyList<TradeOrder> Orders => _orders.Values.ToArray();

        public Task<IReadOnlyList<TradeOrder>> LoadAsync(Guid companyProfileId) =>
            Task.FromResult<IReadOnlyList<TradeOrder>>(
                _orders.Values
                    .Where(order => order.CompanyProfileId == companyProfileId)
                    .ToArray());

        public Task<bool> SaveAsync(TradeOrder order)
        {
            _orders[order.Id] = TradeOrderWorkflow.CopyOrder(order);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid orderId) =>
            Task.FromResult(_orders.Remove(orderId));
    }

    private sealed class FakeTradeCompanyClient : ITradeCompanyClient
    {
        public TradeCompanyIdentity? Identity { get; set; }

        public TradeCompanyChangeSet? Changes { get; set; }

        public bool ThrowMutations { get; set; }

        public Func<TradeCompanyMutationRequest, TradeCompanyMutationResult>? MutationHandler { get; set; }

        public List<TradeCompanyMutationRequest> Mutations { get; } = [];

        public Task<TradeCompanyIdentity?> GetCompanyAsync(
            CompanyId companyId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Identity);

        public Task<TradeCompanyChangeSet> GetChangesAsync(
            CompanyId companyId,
            CompanyRevision afterRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Changes ?? new TradeCompanyChangeSet(
                    companyId,
                    Identity?.Revision ?? afterRevision,
                    []));

        public Task<TradeCompanyMutationResult> MutateAsync(
            TradeCompanyMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add(request);
            if (ThrowMutations)
            {
                throw new HttpRequestException("Company service offline.");
            }

            return Task.FromResult(
                MutationHandler?.Invoke(request) ??
                Applied(
                    request,
                    request.ExpectedCompanyRevision.Value + 1,
                    request.ExpectedRecordRevision.Value + 1));
        }
    }
}
