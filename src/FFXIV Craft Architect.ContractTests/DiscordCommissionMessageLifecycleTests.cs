using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordCommissionMessageLifecycleTests
{
    private static readonly DateTime CapturedAt =
        new(2026, 8, 2, 16, 0, 0, DateTimeKind.Utc);
    private static readonly CompanyId CompanyId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid CommissionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClaimId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CrafterId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OutputLineId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static async Task AssertAllAsync()
    {
        ResolutionStatusPreservesPersistedCompletedAndCanceledValues();
        PreWorkReleaseReturnsCommissionToOpen();
        foreach (var boundary in new[]
                 {
                     "partial-payment",
                     "materials-ready",
                     "materials-satisfied",
                     "output-progress",
                     "work-status"
                 })
        {
            PostWorkReleaseRequiresPrivateResolution(boundary);
        }
        OperatorReopenCreatesAClaimableCommissionWithoutDiscardingProgressEvidence();
        OperatorCanReopenACanceledPublishedCommission();
        await DiscordDeletesCanceledAndHeldMessagesThenCreatesFreshMessagesOnReopen();
        await CancellationOvertakesAnInFlightCreateBeforeReopenPostsFresh();
    }

    private static void ResolutionStatusPreservesPersistedCompletedAndCanceledValues()
    {
        Assert.Equal(5, (int)TradeOrderStatus.Completed);
        Assert.Equal(6, (int)TradeOrderStatus.Canceled);
        Assert.Equal(7, (int)TradeOrderStatus.ResolutionRequired);
    }

    private static void PreWorkReleaseReturnsCommissionToOpen()
    {
        var source = CreateAssignedOrder();

        var transition = CompanyCommissionCommandWorkflow.Apply(
            source,
            new ReleaseCompanyCommissionClaimCommand(Context(), "Schedule changed."),
            new CompanyCommissionActor("crafter", CompanyCommissionActorKind.Crafter),
            CapturedAt.AddMinutes(5));

        Assert.Equal(TradeOrderStatus.ReadyToAssign, transition.UpdatedOrder.Status);
        Assert.Null(transition.UpdatedOrder.AssignedCrafterId);
        Assert.Null(transition.UpdatedOrder.CompanyCommission!.ActiveClaim);
        Assert.Null(transition.UpdatedOrder.CompanyCommission.ManualResolution);
        Assert.Equal(CompanyCommissionActivityKind.ClaimReleased, transition.ActivityKind);
    }

    private static void PostWorkReleaseRequiresPrivateResolution(string boundary)
    {
        var source = CreateAssignedOrder(boundary);

        var transition = CompanyCommissionCommandWorkflow.Apply(
            source,
            new ReleaseCompanyCommissionClaimCommand(Context(), "I cannot finish this commission."),
            new CompanyCommissionActor("crafter", CompanyCommissionActorKind.Crafter),
            CapturedAt.AddMinutes(5));

        var commission = transition.UpdatedOrder.CompanyCommission!;
        Assert.Equal(TradeOrderStatus.ResolutionRequired, transition.UpdatedOrder.Status);
        Assert.NotNull(commission.ManualResolution);
        Assert.Equal(ClaimId, commission.ManualResolution!.ClaimId);
        Assert.NotNull(commission.ParticipantGrant!.RevokedAtUtc);
        Assert.Equal(CompanyCommissionActivityKind.ClaimResolutionRequired, transition.ActivityKind);
        Assert.Equal(CompanyCommissionActivityVisibility.CompanyOnly, transition.Visibility);
        Assert.Equal(
            source.CompanyCommission!.OutputProgress,
            commission.OutputProgress);
    }

    private static void OperatorReopenCreatesAClaimableCommissionWithoutDiscardingProgressEvidence()
    {
        var held = CompanyCommissionCommandWorkflow.Apply(
            CreateAssignedOrder("output-progress"),
            new ReleaseCompanyCommissionClaimCommand(Context(), "I cannot finish this commission."),
            new CompanyCommissionActor("crafter", CompanyCommissionActorKind.Crafter),
            CapturedAt.AddMinutes(5)).UpdatedOrder;

        var transition = CompanyCommissionCommandWorkflow.Apply(
            held,
            new ReopenCompanyCommissionCommand(Context(), "Company recovered the materials and approved reassignment."),
            new CompanyCommissionActor("operator", CompanyCommissionActorKind.Commissioner),
            CapturedAt.AddMinutes(10));

        var commission = transition.UpdatedOrder.CompanyCommission!;
        Assert.Equal(TradeOrderStatus.ReadyToAssign, transition.UpdatedOrder.Status);
        Assert.Null(transition.UpdatedOrder.AssignedCrafterId);
        Assert.Null(commission.ActiveClaim);
        Assert.Null(commission.ManualResolution);
        Assert.Null(commission.ParticipantAcknowledgedTermsVersion);
        Assert.Equal(CompanyCommissionSettlementState.NotDue, commission.SettlementState);
        Assert.Single(commission.OutputProgress);
        Assert.Equal(1, commission.OutputProgress[0].CompletedQuantity);
        Assert.Equal(CompanyCommissionActivityKind.CommissionReopened, transition.ActivityKind);
        Assert.Equal(CompanyCommissionActivityVisibility.CompanyOnly, transition.Visibility);
    }

    private static void OperatorCanReopenACanceledPublishedCommission()
    {
        var canceled = CreateAssignedOrder();
        canceled.Status = TradeOrderStatus.Canceled;

        var transition = CompanyCommissionCommandWorkflow.Apply(
            canceled,
            new ReopenCompanyCommissionCommand(Context(), "The request is active again."),
            new CompanyCommissionActor("operator", CompanyCommissionActorKind.Commissioner),
            CapturedAt.AddMinutes(10));

        Assert.Equal(TradeOrderStatus.ReadyToAssign, transition.UpdatedOrder.Status);
        Assert.Null(transition.UpdatedOrder.CompanyCommission!.ActiveClaim);
        Assert.Equal(CompanyCommissionActivityKind.CommissionReopened, transition.ActivityKind);
    }

    private static async Task DiscordDeletesCanceledAndHeldMessagesThenCreatesFreshMessagesOnReopen()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-lifecycle-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            await store.InitializeAsync();
            var delivery = new CompanyCommissionDiscordDeliveryService(
                store,
                new SqliteDiscordNotificationStore(options),
                options,
                TimeProvider.System);

            var open = await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            Assert.True(open.Success, open.Error);
            var create = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.CreateMessage, create.Operation);
            AssertPublicationPayload(create.PayloadJson, 0x2E6EA6, expectsClaimButton: true);
            await store.CompleteAsync(create.WorkItemId, create.LeaseId, "900000000000000001", DateTimeOffset.UtcNow);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.Assigned,
                projectionRevision: 2,
                eventKind: CompanyCommissionActivityKind.ClaimAccepted,
                claimed: true));
            var edit = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.EditMessage, edit.Operation);
            AssertPublicationPayload(edit.PayloadJson, 0xD18B18, expectsClaimButton: false);
            await store.CompleteAsync(edit.WorkItemId, edit.LeaseId, edit.MessageId, DateTimeOffset.UtcNow);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.Canceled,
                projectionRevision: 3,
                eventKind: CompanyCommissionActivityKind.CommissionCanceled,
                claimed: true));
            var deleteCanceled = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.DeleteMessage, deleteCanceled.Operation);
            await store.CompleteAsync(
                deleteCanceled.WorkItemId,
                deleteCanceled.LeaseId,
                deleteCanceled.MessageId,
                DateTimeOffset.UtcNow);
            var canceled = await store.LoadPublicationByOrderAsync(CompanyId, CommissionId);
            Assert.Equal(DiscordPublicationState.Suppressed, canceled!.State);
            Assert.Null(canceled.MessageId);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 4,
                eventKind: CompanyCommissionActivityKind.CommissionReopened,
                claimed: false));
            var recreate = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.CreateMessage, recreate.Operation);
            Assert.Null(recreate.MessageId);
            await store.CompleteAsync(recreate.WorkItemId, recreate.LeaseId, "900000000000000002", DateTimeOffset.UtcNow);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ResolutionRequired,
                projectionRevision: 5,
                eventKind: CompanyCommissionActivityKind.ClaimResolutionRequired,
                claimed: true,
                requiresManualResolution: true));
            var deleteHeld = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.DeleteMessage, deleteHeld.Operation);
            var held = await store.LoadPublicationByOrderAsync(CompanyId, CommissionId);
            Assert.Equal(DiscordPublicationState.Suppressed, held!.State);
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

    private static async Task CancellationOvertakesAnInFlightCreateBeforeReopenPostsFresh()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-ordering-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            await store.InitializeAsync();
            var delivery = new CompanyCommissionDiscordDeliveryService(
                store,
                new SqliteDiscordNotificationStore(options),
                options,
                TimeProvider.System);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            var inFlightCreate = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.CreateMessage, inFlightCreate.Operation);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.Canceled,
                projectionRevision: 2,
                eventKind: CompanyCommissionActivityKind.CommissionCanceled,
                claimed: false));
            Assert.Empty(await LeaseAsync(store));

            await store.CompleteAsync(
                inFlightCreate.WorkItemId,
                inFlightCreate.LeaseId,
                "900000000000000010",
                DateTimeOffset.UtcNow);
            var compensatingDelete = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.DeleteMessage, compensatingDelete.Operation);
            Assert.Equal("900000000000000010", compensatingDelete.MessageId);
            await store.CompleteAsync(
                compensatingDelete.WorkItemId,
                compensatingDelete.LeaseId,
                compensatingDelete.MessageId,
                DateTimeOffset.UtcNow);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 3,
                eventKind: CompanyCommissionActivityKind.CommissionReopened,
                claimed: false));
            var freshCreate = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.CreateMessage, freshCreate.Operation);
            Assert.Null(freshCreate.MessageId);
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

    private static TradeOrder CreateAssignedOrder(string? boundary = null)
    {
        var actor = new CompanyCommissionActor("commissioner", CompanyCommissionActorKind.Commissioner);
        var paymentSent = boundary == "partial-payment"
            ? new CompanyCommissionPaymentAttestation(1, CapturedAt, actor.ActorId, "Sent")
            : null;
        var outputProgress = boundary == "output-progress"
            ? new[]
            {
                new CompanyCommissionOutputProgress(
                    OutputLineId, 100, 5, 1, 0, 0, CapturedAt, actor)
            }
            : [];
        var companyMaterials = new CompanyCommissionMaterialClearance(
            boundary == "materials-satisfied"
                ? CompanyCommissionClearanceState.Satisfied
                : CompanyCommissionClearanceState.Pending,
            [],
            ReadyAtUtc: boundary == "materials-ready" ? CapturedAt : null);
        var commission = new TradeCompanyCommission
        {
            CommissionId = CommissionId,
            CompanyId = CompanyId,
            CommissionerActorId = actor.ActorId,
            Reference = "CA-LIFECYCLE",
            CreatedAtUtc = CapturedAt,
            UpdatedAtUtc = CapturedAt,
            CurrentTermsVersion = 1,
            TermsVersions = [CreateTerms(actor)],
            PublicMetadata = new()
            {
                PublicBriefId = "discord-lifecycle",
                ViewState = CompanyCommissionPublicViewState.Published
            },
            ActiveClaimCapabilityRevision = 1,
            ActiveClaim = new(ClaimId, 1, CapturedAt, CrafterId, null),
            ParticipantGrant = new(Guid.NewGuid(), ClaimId, 1, 1, CapturedAt),
            ParticipantAcknowledgedTermsVersion = 1,
            Gates = new(
                new(CompanyCommissionClearanceState.Satisfied),
                new(
                    CompanyCommissionClearanceState.Pending,
                    TermsVersion: 1,
                    CommissionerSent: paymentSent),
                companyMaterials),
            OutputProgress = outputProgress,
            DeliveryReadiness = new(false, null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue
        };
        return new TradeOrder
        {
            Id = CommissionId,
            CompanyProfileId = CompanyId.Value,
            Title = "Cobalt Joint Plate",
            Status = boundary == "work-status"
                ? TradeOrderStatus.InProgress
                : TradeOrderStatus.Assigned,
            AssignedCrafterId = CrafterId,
            CompanyCommission = commission,
            CreatedAtUtc = CapturedAt,
            UpdatedAtUtc = CapturedAt,
            CommissionedAtUtc = CapturedAt
        };
    }

    private static CompanyCommissionTermsVersion CreateTerms(CompanyCommissionActor actor) =>
        new()
        {
            Version = 1,
            CreatedAtUtc = CapturedAt,
            CreatedBy = actor,
            Outputs = [new(OutputLineId, 100, "Cobalt Joint Plate", 5, false)],
            Payment = new(
                CompanyCommissionPaymentSchedule.Advance,
                "Labor standard",
                0,
                0,
                1_000,
                1_000),
            PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
        };

    private static CompanyCommissionCommandContext Context() =>
        new(CompanyId, CommissionId, new(1), new(1), Guid.NewGuid(), 1);

    private static DiscordCommissionOptions CreateDiscordOptions(string databasePath) =>
        new()
        {
            Enabled = true,
            CompanyId = CompanyId.Value.ToString("D"),
            ApplicationId = "100000000000000001",
            PublicKey = new string('a', 64),
            BotToken = "test-token",
            AllowedGuildId = "100000000000000002",
            AllowedChannelId = "100000000000000003",
            CommissionBaseUrl = "https://example.test/commission/",
            DatabasePath = databasePath
        };

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

    private static void AssertPublicationPayload(
        string payloadJson,
        int expectedColor,
        bool expectsClaimButton)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var embed = payload.RootElement.GetProperty("embeds")[0];
        Assert.Equal(expectedColor, embed.GetProperty("color").GetInt32());
        Assert.Equal(
            "Craft Architect | Commission",
            embed.GetProperty("author").GetProperty("name").GetString());
        var buttons = payload.RootElement
            .GetProperty("components")[0]
            .GetProperty("components")
            .EnumerateArray()
            .Select(button => button.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("View commission", buttons);
        Assert.Equal(expectsClaimButton, buttons.Contains("Claim commission"));
    }
}
