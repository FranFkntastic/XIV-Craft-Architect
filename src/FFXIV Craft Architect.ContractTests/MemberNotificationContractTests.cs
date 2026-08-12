using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class MemberNotificationContractTests
{
    private static readonly CompanyId CompanyId = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid CommissionId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid CrafterId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");
    private const string CommissionerDiscordId = "100000000000000001";
    private const string CrafterDiscordId = "100000000000000002";

    [Theory]
    [InlineData(CompanyCommissionActivityKind.ClaimAccepted)]
    [InlineData(CompanyCommissionActivityKind.TermsAmended)]
    [InlineData(CompanyCommissionActivityKind.ProgressReported)]
    [InlineData(CompanyCommissionActivityKind.DeliveryAccepted)]
    [InlineData(CompanyCommissionActivityKind.CommissionCanceled)]
    public async Task CommissionActivityEnqueuesMemberDmAndPreservesCommissionerRoute(
        CompanyCommissionActivityKind eventKind)
    {
        await using var fixture = await NotificationFixture.CreateAsync(linkDiscord: true);
        var notification = fixture.Notification(eventKind);
        if (eventKind == CompanyCommissionActivityKind.ProgressReported)
        {
            notification = notification with
            {
                Summary = "The crafter reported production progress: " +
                    "Iron Nails: 1 of 1 completed, 1 ready. " +
                    "Comment: First batch is staged. Work remains in progress.",
                ActionLabel = "View progress"
            };
        }

        var result = await fixture.Delivery.NotifyMembersAsync(
            notification,
            fixture.Commission,
            fixture.PublicUrl);
        Assert.True(result.Success);

        var commissionerResult = await fixture.Delivery.NotifyAsync(notification);
        Assert.True(commissionerResult.Success, commissionerResult.Error);
        Assert.True(
            commissionerResult.WorkItemIds.Count > 0,
            $"{commissionerResult.Status}: {commissionerResult.Error}");
        var items = await fixture.LoadOutboxAsync();
        Assert.True(items.Count >= 2, string.Join(" | ", items));

        var member = Assert.Single(items, item => item.DestinationKind == 2);
        Assert.Equal(CrafterDiscordId, member.DestinationKey);
        Assert.Contains(
            items,
            item => item.DestinationKind == 0 &&
                item.DestinationKey == $"dm:{CommissionerDiscordId}");
        Assert.DoesNotContain("capability", member.PayloadJson, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(member.PayloadJson);
        Assert.Equal(
            $"https://example.test/companies/{CompanyId.Value:D}" +
            $"?commissionId={CommissionId:D}" +
            "&activityId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            payload.RootElement
            .GetProperty("embeds")[0]
            .GetProperty("url")
            .GetString());
        Assert.DoesNotContain("Contract Crafter", member.PayloadJson, StringComparison.Ordinal);
        if (eventKind == CompanyCommissionActivityKind.ProgressReported)
        {
            Assert.Contains("Iron Nails: 1 of 1 completed, 1 ready", member.PayloadJson);
            Assert.Contains("First batch is staged", member.PayloadJson);
        }

        var commissioner = Assert.Single(
            items,
            item => item.DestinationKind == 0 &&
                item.DestinationKey == $"dm:{CommissionerDiscordId}");
        using var commissionerPayload = JsonDocument.Parse(commissioner.PayloadJson);
        var commissionerEmbed = commissionerPayload.RootElement
            .GetProperty("embeds")[0];
        Assert.Contains("Contract commission", commissionerEmbed.GetProperty("title").GetString());
        Assert.Contains("MEMBER-1", commissionerEmbed.GetProperty("title").GetString());
        Assert.Equal("Contract Crafter", commissionerEmbed
            .GetProperty("fields")[0]
            .GetProperty("value")
            .GetString());
        Assert.Equal(
            eventKind == CompanyCommissionActivityKind.ProgressReported
                ? "View progress"
                : "Review identity",
            commissionerPayload.RootElement
                .GetProperty("components")[0]
                .GetProperty("components")[0]
                .GetProperty("label")
                .GetString());
        if (eventKind == CompanyCommissionActivityKind.ProgressReported)
        {
            Assert.Contains("Iron Nails: 1 of 1 completed, 1 ready", commissioner.PayloadJson);
            Assert.Contains("First batch is staged", commissioner.PayloadJson);
        }
        Assert.Equal(
            $"https://example.test/trade/orders?orderId={CommissionId:D}" +
            "&activityId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            commissionerEmbed.GetProperty("url").GetString());
    }

    [Fact]
    public async Task OptedOutMemberReceivesNoMemberDm()
    {
        await using var fixture = await NotificationFixture.CreateAsync(linkDiscord: true);
        var membership = await fixture.Memberships.SetNotificationsOptedOutAsync(
            CompanyId,
            CrafterId,
            optedOut: true);

        Assert.True(membership!.NotificationsOptedOut);
        var result = await fixture.Delivery.NotifyMembersAsync(
            fixture.Notification(CompanyCommissionActivityKind.ProgressReported),
            fixture.Commission,
            fixture.PublicUrl);

        Assert.Equal(DiscordNotificationEnqueueStatus.Suppressed, result.Status);
        Assert.DoesNotContain(
            await fixture.LoadOutboxAsync(),
            item => item.DestinationKind == 2);
    }

    [Fact]
    public async Task ActiveParticipantWithoutLinkedDiscordReceivesNoMemberDm()
    {
        await using var fixture = await NotificationFixture.CreateAsync(linkDiscord: false);

        var result = await fixture.Delivery.NotifyMembersAsync(
            fixture.Notification(CompanyCommissionActivityKind.ClaimAccepted),
            fixture.Commission,
            fixture.PublicUrl);

        Assert.Equal(DiscordNotificationEnqueueStatus.Suppressed, result.Status);
        Assert.Empty(await fixture.LoadOutboxAsync());
    }

    [Fact]
    public async Task MemberDmDispatchResolvesRecipientBeforeCreatingMessage()
    {
        await using var fixture = await NotificationFixture.CreateAsync(linkDiscord: true);
        var enqueued = await fixture.Delivery.NotifyMembersAsync(
            fixture.Notification(CompanyCommissionActivityKind.CommentAdded),
            fixture.Commission,
            fixture.PublicUrl);
        Assert.True(enqueued.Success, enqueued.Error);

        var discord = new RecordingMemberDmClient();
        await fixture.CreateDispatcher(discord).DispatchDueAsync(default);

        Assert.Equal(CrafterDiscordId, discord.ResolvedRecipientUserId);
        Assert.Equal(RecordingMemberDmClient.DirectMessageChannelId, discord.CreatedChannelId);
        var delivery = await fixture.LoadMemberDeliveryAsync();
        Assert.Equal((int)DiscordOutboxState.Succeeded, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(RecordingMemberDmClient.DirectMessageChannelId, delivery.ChannelId);
        Assert.Equal(RecordingMemberDmClient.MessageId, delivery.MessageId);
        Assert.Null(delivery.LastError);
        Assert.Null(delivery.FailureCode);
    }

    [Fact]
    public async Task NotificationRejectsNonCanonicalOperatorDestination()
    {
        await using var fixture = await NotificationFixture.CreateAsync(linkDiscord: true);
        var notification = fixture.Notification(
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted) with
        {
            ActivityUrl = new Uri(
                $"https://example.test/trade/orders?orderId={CommissionId:D}" +
                "&activityId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" +
                "&capability=must-not-travel")
        };

        var result = await fixture.Delivery.NotifyAsync(notification);

        Assert.Equal(DiscordNotificationEnqueueStatus.Invalid, result.Status);
        Assert.Empty(await fixture.LoadOutboxAsync());
    }

    private sealed class NotificationFixture : IAsyncDisposable
    {
        private readonly string databasePath;
        private readonly SqliteDiscordNotificationStore notifications;
        private readonly DiscordCommissionOptions options;

        private NotificationFixture(
            string databasePath,
            DiscordCommissionOptions options,
            SqliteDiscordNotificationStore notifications,
            SqliteMembershipStore memberships,
            SqliteDiscordIdentityStore identities,
            CompanyCommissionDiscordDeliveryService delivery,
            TradeCompanyCommission commission)
        {
            this.databasePath = databasePath;
            this.notifications = notifications;
            this.options = options;
            Memberships = memberships;
            Commission = commission;
            Delivery = delivery;
        }

        public CompanyCommissionDiscordDeliveryService Delivery { get; }
        public SqliteMembershipStore Memberships { get; }
        public TradeCompanyCommission Commission { get; }
        public Uri PublicUrl { get; } = new("https://example.test/commission/brief");

        public DiscordNotificationOutboxDispatcher CreateDispatcher(IDiscordApiClient discord) =>
            new(
                notifications,
                discord,
                options,
                TimeProvider.System,
                NullLogger<DiscordNotificationOutboxDispatcher>.Instance);

        public static async Task<NotificationFixture> CreateAsync(bool linkDiscord)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"craft-architect-member-notifications-{Guid.NewGuid():N}.db");
            var options = new DiscordCommissionOptions
            {
                Enabled = true,
                CompanyId = CompanyId.Value.ToString("D"),
                ApplicationId = CommissionerDiscordId,
                PublicKey = new string('a', 64),
                BotToken = "test-token",
                AllowedGuildId = "100000000000000003",
                AllowedChannelId = "100000000000000004",
                CommissionBaseUrl = "https://example.test/commission/",
                DatabasePath = path
            };
            var notifications = new SqliteDiscordNotificationStore(options);
            await notifications.InitializeAsync();
            var route = await notifications.PutRouteAsync(
                CompanyId,
                new DiscordNotificationRouteUpdate(
                    CommissionerDiscordId,
                    DiscordNotificationDestinationMode.CommissionerDirectMessage,
                    null,
                    DiscordDirectMessageFallback.None,
                    DiscordNotificationMentionBehavior.NoPing,
                    DiscordNotificationMentionBehavior.NoPing,
                    DiscordNotificationMentionBehavior.NoPing,
                    0,
                    "member-notification-contract"),
                DateTimeOffset.UtcNow);
            Assert.True(route.Success, route.Error);

            var memberships = new SqliteMembershipStore(
                new TradeMembershipOptions { DatabasePath = path },
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            await memberships.RequestAsync(CompanyId, CrafterId, null);
            var approval = await memberships.ApproveAsync(CompanyId, CrafterId, Guid.NewGuid());
            Assert.Equal(MembershipMutationStatus.Applied, approval.Status);

            var identities = new SqliteDiscordIdentityStore(new DiscordIdentityOptions
            {
                DatabasePath = path
            });
            if (linkDiscord)
            {
                var linked = await identities.LinkAsync(
                    CrafterId,
                    CrafterDiscordId,
                    "Contract Crafter",
                    DateTimeOffset.UtcNow);
                Assert.Equal(DiscordIdentityLinkResultStatus.Linked, linked.Status);
            }

            var commission = CreateCommission();
            return new NotificationFixture(
                path,
                options,
                notifications,
                memberships,
                identities,
                new CompanyCommissionDiscordDeliveryService(
                    new SqliteDiscordCollaborationStore(options),
                    notifications,
                    memberships,
                    identities,
                    options,
                    TimeProvider.System),
                commission);
        }

        public CommittedCompanyCommissionNotification Notification(
            CompanyCommissionActivityKind eventKind)
        {
            var eventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            return new(
                CompanyId,
                CommissionBrief(),
                eventId,
                1,
                eventKind,
                DateTime.UtcNow,
                "Commission activity changed.",
                "Contract Crafter",
                "Review identity",
                CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
                    PublicUrl,
                    CommissionId,
                    eventId));
        }

        public async Task<IReadOnlyList<OutboxItem>> LoadOutboxAsync()
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT destination_kind, destination_key, payload_json
                FROM discord_notification_outbox
                ORDER BY destination_kind;
                """;
            var items = new List<OutboxItem>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            return items;
        }

        public async Task<MemberDelivery> LoadMemberDeliveryAsync()
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT state, attempt_count, channel_id, message_id, last_error, failure_code
                FROM discord_notification_outbox
                WHERE destination_kind = $destinationKind;
                """;
            command.Parameters.AddWithValue(
                "$destinationKind",
                (int)DiscordNotificationDestinationKind.MemberDirectMessage);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                File.Delete(databasePath);
                File.Delete(databasePath + "-shm");
                File.Delete(databasePath + "-wal");
            }
            catch (IOException)
            {
            }
            return ValueTask.CompletedTask;
        }

        private CompanyCommissionPublicBrief CommissionBrief() => new()
        {
            PublicBriefId = "brief",
            CommissionId = CommissionId,
            Title = "Contract commission",
            CompanyDisplayName = "Contract company",
            Reference = "MEMBER-1",
            ViewState = CompanyCommissionPublicViewState.Published,
            Terms = new()
            {
                Version = 1,
                Outputs = [],
                Payment = new(
                    CompanyCommissionPaymentSchedule.Advance,
                    "Contract payment",
                    0,
                    0,
                    1_000,
                    1_000),
                PricingEvidence = new("Contract evidence", "Aether", "Siren", DateTime.UtcNow)
            },
            Status = TradeOrderStatus.Assigned,
            Gates = new(
                CompanyCommissionClearanceState.Satisfied,
                CompanyCommissionClearanceState.Pending,
                CompanyCommissionClearanceState.NotRequired),
            ClearedToWork = false,
            IsClaimed = true,
            DeliveryReadiness = new(false, null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Closed = false,
            ProjectionRevision = 1
        };

        private static TradeCompanyCommission CreateCommission() => new()
        {
            CommissionId = CommissionId,
            CompanyId = CompanyId,
            CommissionerActorId = "commissioner",
            Reference = "MEMBER-1",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CurrentTermsVersion = 1,
            TermsVersions = [],
            PublicMetadata = new()
            {
                PublicBriefId = "brief",
                ViewState = CompanyCommissionPublicViewState.Published,
                PublicUrl = "https://example.test/commission/brief"
            },
            ActiveClaimCapabilityRevision = 1,
            ActiveClaim = new(Guid.NewGuid(), 1, DateTime.UtcNow, CrafterId, null),
            Gates = new(
                new(CompanyCommissionClearanceState.Satisfied),
                new(CompanyCommissionClearanceState.Pending),
                new(CompanyCommissionClearanceState.NotRequired, [], null)),
            DeliveryReadiness = new(false, null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue
        };

        public sealed record OutboxItem(int DestinationKind, string DestinationKey, string PayloadJson);
        public sealed record MemberDelivery(
            int State,
            int AttemptCount,
            string? ChannelId,
            string? MessageId,
            string? LastError,
            string? FailureCode);
    }

    private sealed class RecordingMemberDmClient : IDiscordApiClient
    {
        public const string DirectMessageChannelId = "100000000000000099";
        public const string MessageId = "100000000000000100";

        public string? ResolvedRecipientUserId { get; private set; }
        public string? CreatedChannelId { get; private set; }

        public Task<DiscordApiResult> ResolveDirectMessageChannelAsync(
            string recipientUserId,
            CancellationToken cancellationToken = default)
        {
            ResolvedRecipientUserId = recipientUserId;
            return Task.FromResult(new DiscordApiResult(
                DiscordApiOutcome.Succeeded,
                DirectMessageChannelId));
        }

        public Task<DiscordApiResult> CreateNotificationMessageAsync(
            string channelId,
            object payload,
            string? allowedMentionUserId,
            CancellationToken cancellationToken = default)
        {
            CreatedChannelId = channelId;
            return Task.FromResult(new DiscordApiResult(
                DiscordApiOutcome.Succeeded,
                MessageId));
        }

        public Task<DiscordApiResult> CreateMessageAsync(
            string channelId,
            object payload,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> EditMessageAsync(
            string channelId,
            string messageId,
            object payload,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> DeleteMessageAsync(
            string channelId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> GetMessageAsync(
            string channelId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
