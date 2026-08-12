using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly Guid ClaimId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CrafterId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OutputLineId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void ResolutionStatusPreservesPersistedCompletedAndCanceledValues()
    {
        Assert.Equal(5, (int)TradeOrderStatus.Completed);
        Assert.Equal(6, (int)TradeOrderStatus.Canceled);
        Assert.Equal(7, (int)TradeOrderStatus.ResolutionRequired);
    }

    [Fact]
    public void AssignedPublicationOmitsWorkspaceWhenDeploymentCapabilityIsDisabled()
    {
        var payload = CompanyCommissionDiscordMessage.CreatePublication(
            CreateProjection(
                TradeOrderStatus.Assigned,
                projectionRevision: 2,
                eventKind: CompanyCommissionActivityKind.ClaimAccepted,
                claimed: true),
            "ca:v1:workspace-capability-disabled");

        AssertPublicationPayload(
            JsonSerializer.Serialize(payload),
            0xD18B18,
            expectsClaimButton: false,
            expectsWorkspaceButton: false);
    }

    [Fact]
    public async Task DiscordClaimContactStoreAddsClaimIdentityToExistingDatabase()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-contact-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE discord_claim_contacts (
                        interaction_id TEXT PRIMARY KEY,
                        company_id TEXT NOT NULL,
                        commission_id TEXT NOT NULL,
                        claim_event_id TEXT NOT NULL,
                        commission_revision INTEGER NOT NULL,
                        discord_user_id TEXT NOT NULL,
                        display_name_snapshot TEXT NOT NULL,
                        committed_at_utc TEXT NOT NULL,
                        UNIQUE(company_id, commission_id, discord_user_id)
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteDiscordNotificationStore(
                CreateDiscordOptions(databasePath));
            await store.InitializeAsync();
            await store.CaptureCommittedClaimContactAsync(
                new CommittedDiscordClaimContact(
                    CompanyId,
                    CommissionId,
                    ClaimId,
                    Guid.NewGuid(),
                    2,
                    CompanyCommissionActivityKind.ClaimAccepted,
                    CapturedAt,
                    "100000000000000004",
                    new DiscordOriginContact(
                        "100000000000000005",
                        "Migration Crafter")));

            Assert.True(await store.HasCommittedClaimContactAsync(
                CompanyId,
                CommissionId,
                ClaimId,
                "100000000000000005"));
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
    public void PreWorkReleaseReturnsCommissionToOpen()
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

    [Theory]
    [InlineData("partial-payment")]
    [InlineData("materials-ready")]
    [InlineData("materials-satisfied")]
    [InlineData("output-progress")]
    [InlineData("work-status")]
    public void PostWorkReleaseRequiresPrivateResolution(string boundary)
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

    [Fact]
    public void OperatorReopenCreatesAClaimableCommissionWithoutDiscardingProgressEvidence()
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

    [Fact]
    public void OperatorCanReopenACanceledPublishedCommission()
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

    [Fact]
    public async Task DiscordDeletesCanceledAndHeldMessagesThenCreatesFreshMessagesOnReopen()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-lifecycle-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(
                databasePath,
                crafterWorkspaceEnabled: true);
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
            AssertPublicationPayload(
                create.PayloadJson,
                0x2E6EA6,
                expectsClaimButton: true,
                expectsWorkspaceButton: false);
            await store.CompleteAsync(create.WorkItemId, create.LeaseId, "900000000000000001", DateTimeOffset.UtcNow);

            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.Assigned,
                projectionRevision: 2,
                eventKind: CompanyCommissionActivityKind.ClaimAccepted,
                claimed: true));
            var edit = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.EditMessage, edit.Operation);
            AssertPublicationPayload(
                edit.PayloadJson,
                0xD18B18,
                expectsClaimButton: false,
                expectsWorkspaceButton: true);
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

    [Fact]
    public async Task CancellationOvertakesAnInFlightCreateBeforeReopenPostsFresh()
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

    [Fact]
    public async Task StartupReconciliationMigratesAnOldProjectionExactlyOnce()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            var create = Assert.Single(await LeaseAsync(store));
            await store.CompleteAsync(
                create.WorkItemId,
                create.LeaseId,
                "900000000000000020",
                DateTimeOffset.UtcNow);
            await SetProjectionFormatVersionAsync(databasePath, 0);

            var refresher = new RecordingPublicationRefresher(store);
            await using var services = new ServiceCollection()
                .AddScoped<IDiscordPublicationRefresher>(_ => refresher)
                .BuildServiceProvider();
            var reconciliation = new DiscordPublicationReconciliationService(
                services.GetRequiredService<IServiceScopeFactory>(),
                store,
                options,
                TimeProvider.System,
                NullLogger<DiscordPublicationReconciliationService>.Instance);
            var hosted = CreateHostedPublishedOrder();

            var first = await reconciliation.ReconcileStaleAsync([hosted]);
            Assert.Equal(1, first.Examined);
            Assert.Equal(1, first.Reconciled);
            Assert.Equal(1, refresher.CallCount);
            var edit = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.EditMessage, edit.Operation);
            var migrated = await store.LoadPublicationByOrderAsync(CompanyId, CommissionId);
            Assert.Equal(
                DiscordPublicationProjectionFormat.CurrentVersion,
                migrated!.ProjectionFormatVersion);

            var second = await reconciliation.ReconcileStaleAsync([hosted]);
            Assert.Equal(0, second.Examined);
            Assert.Equal(1, refresher.CallCount);
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
    public async Task StartupReconciliationDeletesADeletedOrderProjectionExactlyOnce()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-deleted-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            var create = Assert.Single(await LeaseAsync(store));
            await store.CompleteAsync(
                create.WorkItemId,
                create.LeaseId,
                "900000000000000021",
                DateTimeOffset.UtcNow);
            await SetProjectionFormatVersionAsync(databasePath, 0);

            var refresher = new RecordingPublicationRefresher(store);
            await using var services = new ServiceCollection()
                .AddScoped<IDiscordPublicationRefresher>(_ => refresher)
                .BuildServiceProvider();
            var reconciliation = new DiscordPublicationReconciliationService(
                services.GetRequiredService<IServiceScopeFactory>(),
                store,
                options,
                TimeProvider.System,
                NullLogger<DiscordPublicationReconciliationService>.Instance);
            var active = CreateHostedPublishedOrder();
            var deleted = new HostedProfileObject(
                active.ProfileId,
                new ProfileSyncObjectEnvelope
                {
                    Collection = active.Object.Collection,
                    ObjectId = active.Object.ObjectId,
                    PayloadJson = "{}",
                    Revision = active.Object.Revision + 1,
                    UpdatedAtUtc = CapturedAt.AddMinutes(1),
                    Deleted = true,
                    DeletedAtUtc = CapturedAt.AddMinutes(1)
                });

            var first = await reconciliation.ReconcileStaleAsync([deleted]);
            Assert.Equal(1, first.Examined);
            Assert.Equal(1, first.Reconciled);
            Assert.Equal(0, refresher.CallCount);
            var removal = Assert.Single(await LeaseAsync(store));
            Assert.Equal(DiscordOutboxOperation.DeleteMessage, removal.Operation);
            Assert.Equal("900000000000000021", removal.MessageId);
            var migrated = await store.LoadPublicationByOrderAsync(CompanyId, CommissionId);
            Assert.Equal(DiscordPublicationState.Suppressed, migrated!.State);
            Assert.Equal(
                DiscordPublicationProjectionFormat.CurrentVersion,
                migrated.ProjectionFormatVersion);

            var second = await reconciliation.ReconcileStaleAsync([deleted]);
            Assert.Equal(0, second.Examined);
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

    [Fact]
    public async Task RetryingAnOldFailedPublicationAdoptsTheCurrentFormat()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-retry-format-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            var created = await delivery.ProjectAsync(CreateProjection(
                TradeOrderStatus.ReadyToAssign,
                projectionRevision: 1,
                eventKind: CompanyCommissionActivityKind.CommissionOpened,
                claimed: false));
            var failedCreate = Assert.Single(await LeaseAsync(store));
            await store.ExhaustAsync(
                failedCreate.WorkItemId,
                failedCreate.LeaseId,
                "fixture failure",
                DateTimeOffset.UtcNow);
            await SetProjectionFormatVersionAsync(databasePath, 0);

            var retried = await store.RetryFailedPublicationAsync(
                CompanyId,
                created.Publication!.PublicationId,
                created.Publication.PublicId,
                DiscordPublicationState.Open,
                "{\"content\":\"current format\"}",
                DateTimeOffset.UtcNow);

            Assert.True(retried.Success, retried.Error);
            Assert.Equal(
                DiscordPublicationProjectionFormat.CurrentVersion,
                retried.Publication!.ProjectionFormatVersion);
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
    public async Task ExistingCollaborationSchemaGainsAProjectionFormatRevision()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-schema-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE discord_publications (
                        publication_id TEXT PRIMARY KEY,
                        company_id TEXT NOT NULL,
                        order_id TEXT NOT NULL,
                        source_order_revision INTEGER NOT NULL,
                        public_id TEXT NOT NULL,
                        brief_version INTEGER NOT NULL,
                        channel_id TEXT NOT NULL,
                        message_id TEXT NULL,
                        action_token TEXT NOT NULL UNIQUE,
                        state INTEGER NOT NULL,
                        desired_projection_revision INTEGER NOT NULL,
                        idempotency_key TEXT NOT NULL UNIQUE,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var store = new SqliteDiscordCollaborationStore(
                CreateDiscordOptions(databasePath));
            await store.InitializeAsync();

            await using var verify = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verify.OpenAsync();
            await using var columns = verify.CreateCommand();
            columns.CommandText = "PRAGMA table_info(discord_publications);";
            await using var reader = await columns.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync())
            {
                found |= string.Equals(
                    reader.GetString(1),
                    "projection_format_version",
                    StringComparison.Ordinal);
            }

            Assert.True(found);
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
    public async Task SuppressionOvertakesMissingEditRecovery()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-missing-edit-race-{Guid.NewGuid():N}.db");
        try
        {
            var options = CreateDiscordOptions(databasePath);
            var store = new SqliteDiscordCollaborationStore(options);
            var delivery = CreateDelivery(store, options);
            var discord = new MissingEditDiscordClient(async () =>
            {
                await delivery.ProjectAsync(CreateProjection(
                    TradeOrderStatus.Canceled,
                    projectionRevision: 3,
                    eventKind: CompanyCommissionActivityKind.CommissionCanceled,
                    claimed: true));
            });
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
            Assert.Equal(DiscordPublicationState.Suppressed, publication!.State);
            Assert.Null(publication.MessageId);
            Assert.Equal(1, discord.CreateCount);
            Assert.Equal(1, discord.EditCount);
            Assert.Equal(1, discord.DeleteCount);
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
    public void ReconciliationRequiresAnOperator()
    {
        Assert.False(DiscordCollaborationEndpoints.CanManagePublications(
            new TradeCompanyAccessContext(
                CompanyId,
                Guid.NewGuid(),
                TradeCompanyRole.ReadOnly)));
        Assert.True(DiscordCollaborationEndpoints.CanManagePublications(
            new TradeCompanyAccessContext(
                CompanyId,
                Guid.NewGuid(),
                TradeCompanyRole.Operator)));
        Assert.True(DiscordCollaborationEndpoints.CanManagePublications(
            new TradeCompanyAccessContext(
                CompanyId,
                Guid.NewGuid(),
                TradeCompanyRole.Owner)));
    }

    [Fact]
    public void ProjectionReconciliationUsesCanonicalOrderState()
    {
        Assert.Equal(
            DiscordPublicationState.Open,
            DiscordPublicationService.ResolvePublicationState(
                CreateOrderForProjectionState(TradeOrderStatus.ReadyToAssign)));
        Assert.Equal(
            DiscordPublicationState.Assigned,
            DiscordPublicationService.ResolvePublicationState(
                CreateOrderForProjectionState(TradeOrderStatus.Assigned, assigned: true)));
        Assert.Equal(
            DiscordPublicationState.Closed,
            DiscordPublicationService.ResolvePublicationState(
                CreateOrderForProjectionState(TradeOrderStatus.Completed, assigned: true)));
        Assert.Equal(
            DiscordPublicationState.Suppressed,
            DiscordPublicationService.ResolvePublicationState(
                CreateOrderForProjectionState(TradeOrderStatus.Canceled)));
        Assert.Equal(
            DiscordPublicationState.Suppressed,
            DiscordPublicationService.ResolvePublicationState(
                CreateOrderForProjectionState(TradeOrderStatus.ResolutionRequired, assigned: true)));
        var revoked = CreateOrderForProjectionState(TradeOrderStatus.ReadyToAssign);
        revoked.CommissionPublication!.RevokedAtUtc = CapturedAt;
        Assert.Equal(
            DiscordPublicationState.Revoked,
            DiscordPublicationService.ResolvePublicationState(revoked));
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

    private static HostedProfileObject CreateHostedPublishedOrder()
    {
        var order = CreateAssignedOrder();
        order.Status = TradeOrderStatus.ReadyToAssign;
        order.AssignedCrafterId = null;
        order.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = "discord-lifecycle",
            PublicUrl = "https://example.test/commission/discord-lifecycle",
            Version = 1,
            PublishedAtUtc = CapturedAt
        };
        return new HostedProfileObject(
            "77777777-7777-7777-7777-777777777777",
            new ProfileSyncObjectEnvelope
            {
                Collection = ProfileSyncCollections.TradeOrders,
                ObjectId = order.Id.ToString("D"),
                PayloadJson = JsonSerializer.Serialize(order),
                Revision = 1,
                UpdatedAtUtc = CapturedAt
            });
    }

    private static TradeOrder CreateOrderForProjectionState(
        TradeOrderStatus status,
        bool assigned = false)
    {
        var order = CreateAssignedOrder();
        order.Status = status;
        order.AssignedCrafterId = assigned ? CrafterId : null;
        order.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = "discord-lifecycle",
            PublicUrl = "https://example.test/commission/discord-lifecycle",
            Version = 1,
            PublishedAtUtc = CapturedAt
        };
        return order;
    }

    private static async Task SetProjectionFormatVersionAsync(
        string databasePath,
        int version)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE discord_publications SET projection_format_version = $version;";
        command.Parameters.AddWithValue("$version", version);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

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
        bool expectsClaimButton,
        bool expectsWorkspaceButton)
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
        Assert.Equal(expectsClaimButton, buttons.Contains("Claim with Discord"));
        Assert.Equal(
            expectsWorkspaceButton,
            buttons.Contains("Open my workspace"));
        var customIds = payload.RootElement
            .GetProperty("components")[0]
            .GetProperty("components")
            .EnumerateArray()
            .Where(button => button.TryGetProperty("custom_id", out _))
            .Select(button => button.GetProperty("custom_id").GetString())
            .ToArray();
        Assert.Equal(
            expectsWorkspaceButton,
            customIds.Any(value =>
                value!.StartsWith("open-workspace:ca:v1:", StringComparison.Ordinal)));
        Assert.Equal(
            expectsClaimButton,
            customIds.Any(value =>
                value!.StartsWith("claim-discord:ca:v1:", StringComparison.Ordinal)));
    }

    private sealed class RecordingPublicationRefresher(
        SqliteDiscordCollaborationStore store) : IDiscordPublicationRefresher
    {
        public int CallCount { get; private set; }

        public async Task RefreshOrderAsync(
            TradeCompanyAccessContext access,
            Guid orderId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var publication = await store.LoadPublicationByOrderAsync(
                access.CompanyId,
                orderId,
                cancellationToken) ?? throw new InvalidOperationException(
                "The migration fixture publication is missing.");
            await store.EnqueueProjectionAsync(
                publication.PublicationId,
                DiscordPublicationState.Open,
                checked(publication.DesiredProjectionRevision + 1),
                "{\"content\":\"migrated\"}",
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
    }

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
