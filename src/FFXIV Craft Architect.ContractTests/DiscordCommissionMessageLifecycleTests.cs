using System.Net;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordCommissionMessageLifecycleTests
{
    private static readonly DateTime CapturedAt =
        new(2026, 8, 2, 16, 0, 0, DateTimeKind.Utc);
    private static readonly CompanyId CompanyId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid CommissionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OutputLineId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void ResolutionStatusPreservesPersistedCompletedAndCanceledValues()
    {
        Assert.Equal(5, (int)TradeOrderStatus.Completed);
        Assert.Equal(6, (int)TradeOrderStatus.Canceled);
        Assert.Equal(7, (int)TradeOrderStatus.ResolutionRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("900000000000000123")]
    public async Task AmbiguousCreateReconciliationAdoptsOrEnablesOneRetry(string? messageId)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"discord-reconcile-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            var created = await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign, projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened, claimed: false));
            var work = Assert.Single(await LeaseAsync(store));
            // A projection change while the create is in flight defers an edit without a message id.
            await store.EnqueueProjectionAsync(created.Publication!.PublicationId, DiscordPublicationState.Open,
                created.Publication.DesiredProjectionRevision + 1, "{}", DateTimeOffset.UtcNow);
            await store.RequireReconciliationAsync(work.WorkItemId, work.LeaseId,
                "fixture ambiguous send", DateTimeOffset.UtcNow);
            var ambiguous = (await store.LoadPublicationAsync(created.Publication!.PublicationId))!;
            Assert.True(await store.ResolveAmbiguousCreateAsync(ambiguous, messageId, DateTimeOffset.UtcNow));
            Assert.False(await store.ResolveAmbiguousCreateAsync(ambiguous, messageId, DateTimeOffset.UtcNow));
            var resolved = (await store.LoadPublicationAsync(ambiguous.PublicationId))!;
            Assert.Equal(messageId, resolved.MessageId);
            Assert.Equal(messageId == null ? DiscordPublicationState.Failed : DiscordPublicationState.Open, resolved.State);
            if (messageId == null)
            {
                var retry = await store.RetryFailedPublicationAsync(CompanyId, resolved.PublicationId,
                    resolved.PublicId, DiscordPublicationState.Open, "{}", DateTimeOffset.UtcNow);
                Assert.True(retry.Success, retry.Error);
            }
            else
            {
                await store.EnqueueProjectionAsync(resolved.PublicationId, DiscordPublicationState.Open,
                    resolved.DesiredProjectionRevision + 1, "{}", DateTimeOffset.UtcNow);
            }
            var next = Assert.Single(await LeaseAsync(store));
            Assert.Equal(messageId == null ? DiscordOutboxOperation.CreateMessage : DiscordOutboxOperation.EditMessage,
                next.Operation);
            Assert.Equal(messageId, next.MessageId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task AmbiguousCreateSearchFindsNonOpenMessagesByBriefLinkOnly()
    {
        var createdAt = new DateTimeOffset(2026, 11, 16, 1, 41, 30, TimeSpan.Zero);
        static string Message(string id, DateTimeOffset sentAt, string url) =>
            $$"""
            {"id":"{{id}}","timestamp":"{{sentAt:O}}","author":{"id":"100000000000000001","bot":true},
             "components":[{"type":1,"components":[{"type":2,"style":5,"label":"View commission","url":"{{url}}"}]}]}
            """;
        var olderSamePublicId = Message("900000000000000001", createdAt.AddHours(-2),
            "https://example.test/commission.html?id=PUB1");
        var prefixCollision = Message("900000000000000002", createdAt.AddSeconds(5),
            "https://example.test/commission.html?id=PUB10");
        var assignedCreate = Message("900000000000000003", createdAt.AddSeconds(1),
            "https://example.test/commission.html?id=PUB1#participant");

        async Task<DiscordApiResult> SearchAsync(params string[] messages)
        {
            var handler = new StubHistoryHandler($"[{string.Join(',', messages)}]");
            var client = new DiscordApiClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://discord.test/api/v10/") },
                CreateDiscordOptions(Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.db")),
                NullLogger<DiscordApiClient>.Instance);
            return await client.FindPublicationMessageAsync(
                "100000000000000003", "ca:v1:token", "PUB1", createdAt);
        }

        var pathStyleCreate = Message("900000000000000004", createdAt.AddSeconds(1),
            "https://example.test/commission/PUB1");
        var pathFound = await SearchAsync(prefixCollision, pathStyleCreate, olderSamePublicId);
        Assert.True(pathFound.Succeeded, pathFound.Error);
        Assert.Equal("900000000000000004", pathFound.MessageId);

        var found = await SearchAsync(prefixCollision, assignedCreate, olderSamePublicId);
        Assert.True(found.Succeeded, found.Error);
        Assert.Equal("900000000000000003", found.MessageId);

        var notFound = await SearchAsync(prefixCollision, olderSamePublicId);
        Assert.True(notFound.Succeeded, notFound.Error);
        Assert.Null(notFound.MessageId);
    }

    private sealed class StubHistoryHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task MissingVisibleEditRecreatesExactlyOneMessage()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-missing-edit-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            var discord = new MissingEditDiscordClient();
            var dispatcher = CreateDispatcher(store, discord, options);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            await dispatcher.DispatchDueAsync(default);
            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.Assigned,
                projectionRevision: 2,
                eventKind: CompanyCommissionActivityKind.ClaimAccepted,
                claimed: true));
            await dispatcher.DispatchDueAsync(default);
            await dispatcher.DispatchDueAsync(default);

            var publication = await store.LoadPublicationByOrderAsync(CompanyId, CommissionId);
            Assert.Equal("900000000000000102", publication!.MessageId);
            Assert.Equal(2, discord.CreateCount);
            Assert.Equal(1, discord.EditCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static DiscordCommissionOptions CreateDiscordOptions(
        string databasePath,
        bool crafterWorkspaceEnabled = false) =>
        new()
        {
            Enabled = true,
            CrafterWorkspaceEnabled = crafterWorkspaceEnabled,
            CompanyId = CompanyId.Value.ToString("D"),
            ApplicationId = "100000000000000001",
            PublicKey = new string('a', 64),
            BotToken = "test-token",
            AllowedGuildId = "100000000000000002",
            AllowedChannelId = "100000000000000003",
            CommissionBaseUrl = "https://example.test/commission/",
            DatabasePath = databasePath
        };

    private static CompanyCommissionDiscordDeliveryService CreateDelivery(
        SqliteDiscordCollaborationStore store,
        DiscordCommissionOptions options) =>
        new(
            store,
            new SqliteDiscordNotificationStore(options),
            options,
            TimeProvider.System);

    private static DiscordOutboxDispatcher CreateDispatcher(
        SqliteDiscordCollaborationStore store,
        IDiscordApiClient discord,
        DiscordCommissionOptions options) =>
        new(
            store,
            discord,
            options,
            TimeProvider.System,
            NullLogger<DiscordOutboxDispatcher>.Instance);

    private static CommittedCompanyCommissionDiscordProjection CreateProjection(
        TradeOrderStatus status,
        long projectionRevision,
        CompanyCommissionActivityKind eventKind,
        bool claimed,
        bool requiresManualResolution = false)
    {
        var brief = new CompanyCommissionPublicBrief
        {
            PublicBriefId = "discord-lifecycle",
            CommissionId = CommissionId,
            Title = "Cobalt Joint Plate",
            CompanyDisplayName = "Test Company",
            Reference = "CA-LIFECYCLE",
            ViewState = CompanyCommissionPublicViewState.Published,
            Terms = new()
            {
                Version = 1,
                Outputs = [new(OutputLineId, 100, "Cobalt Joint Plate", 5, false)],
                Payment = new(
                    CompanyCommissionPaymentSchedule.Advance,
                    "Labor standard",
                    0,
                    0,
                    1_000,
                    1_000),
                PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
            },
            Status = status,
            Gates = new(
                CompanyCommissionClearanceState.Satisfied,
                CompanyCommissionClearanceState.Pending,
                CompanyCommissionClearanceState.NotRequired),
            ClearedToWork = false,
            IsClaimed = claimed,
            OutputProgress = [],
            DeliveryReadiness = new(false, null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Closed = false,
            RequiresManualResolution = requiresManualResolution,
            ProjectionRevision = projectionRevision
        };
        return new(
            CompanyId,
            brief,
            new CompanyRecordRevision(projectionRevision),
            Guid.NewGuid(),
            projectionRevision,
            eventKind,
            CapturedAt.AddMinutes(projectionRevision),
            "Lifecycle projection",
            new Uri("https://example.test/commission/discord-lifecycle"),
            claimed || status is TradeOrderStatus.Canceled or TradeOrderStatus.ResolutionRequired
                ? null
                : new Uri("https://example.test/commission/discord-lifecycle#claim=testclaimcapability"));
    }

    private static Task<IReadOnlyList<DiscordOutboxWorkItem>> LeaseAsync(
        SqliteDiscordCollaborationStore store) =>
        store.LeaseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(1), 10);

    private sealed class MissingEditDiscordClient(
        Func<Task>? beforeMissingEdit = null) : IDiscordApiClient
    {
        public int CreateCount { get; private set; }
        public int EditCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<DiscordApiResult> CreateMessageAsync(
            string channelId,
            object payload,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return Task.FromResult(new DiscordApiResult(
                DiscordApiOutcome.Succeeded,
                $"9000000000000001{CreateCount:00}",
                HttpStatusCode.OK));
        }

        public async Task<DiscordApiResult> EditMessageAsync(
            string channelId,
            string messageId,
            object payload,
            CancellationToken cancellationToken = default)
        {
            EditCount++;
            if (beforeMissingEdit != null)
            {
                await beforeMissingEdit();
            }

            return new DiscordApiResult(
                DiscordApiOutcome.TerminalFailure,
                StatusCode: HttpStatusCode.NotFound,
                Error: "Unknown Message");
        }

        public Task<DiscordApiResult> DeleteMessageAsync(
            string channelId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(new DiscordApiResult(
                DiscordApiOutcome.Succeeded,
                messageId,
                HttpStatusCode.NoContent));
        }

        public Task<DiscordApiResult> GetMessageAsync(
            string channelId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> CreateNotificationMessageAsync(
            string channelId,
            object payload,
            string? allowedMentionUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> ResolveDirectMessageChannelAsync(
            string recipientUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
