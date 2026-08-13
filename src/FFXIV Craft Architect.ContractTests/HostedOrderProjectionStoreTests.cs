using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderProjectionStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OwnerProjectionUsesProfileRevisionForRefreshAndCanonicalRevisionForCommands()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var companyId = Guid.NewGuid();
        var draft = CreateOrder(Guid.NewGuid(), companyId, "Operator draft");
        var canonical = CreateOrder(draft.Id, companyId, "Canonical company order");
        var store = RestoringStore(
            profileId,
            7,
            $"https://profiles.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(draft, 7));
        var owner = new CompanyCommissionOwnerProjection
        {
            Order = canonical,
            ObjectRevision = new CompanyRecordRevision(2),
            CompanyRevision = new CompanyRecordRevision(12),
            ProfileObjectRevision = new CompanyRecordRevision(8)
        };

        Assert.True(store.TryPublishOwner(owner));
        var projected = store.Get(draft.Id);
        Assert.Equal(8, projected?.ObjectRevision);
        Assert.Equal(2, projected?.OwnerProjection?.ObjectRevision.Value);
        Assert.Equal("Canonical company order", projected?.Order?.Title);
    }

    [Theory]
    [InlineData(ProjectionStoreScenario.CanonicalRevisionAndTombstone)]
    [InlineData(ProjectionStoreScenario.CompanyProfileIsImmutable)]
    [InlineData(ProjectionStoreScenario.ProfileResetClearsRevisionHistory)]
    [InlineData(ProjectionStoreScenario.OwnerUpgradeAtSameRevision)]
    [InlineData(ProjectionStoreScenario.SameProfileReconnect)]
    [InlineData(ProjectionStoreScenario.ScopeChange)]
    [InlineData(ProjectionStoreScenario.RestoreRevisionCannotRollBack)]
    [InlineData(ProjectionStoreScenario.CompanySnapshotComposition)]
    [InlineData(ProjectionStoreScenario.SameProfileConnectionReplacement)]
    [InlineData(ProjectionStoreScenario.ConnectionScopePathCase)]
    [InlineData(ProjectionStoreScenario.SameRevisionOwnerPersistence)]
    [InlineData(ProjectionStoreScenario.LiveTombstonePersistence)]
    [InlineData(ProjectionStoreScenario.OwnerTombstonePersistence)]
    [InlineData(ProjectionStoreScenario.CenterOperationWinner)]
    [InlineData(ProjectionStoreScenario.CenterOperationAuthoritySwitch)]
    [InlineData(ProjectionStoreScenario.CenterOperationCommittedFailure)]
    [InlineData(ProjectionStoreScenario.StaleMissingOwner)]
    [InlineData(ProjectionStoreScenario.BatchHydration)]
    [InlineData(ProjectionStoreScenario.DirectNotificationHydration)]
    public async Task ProjectionStorePreservesCanonicalIdentityAndRestoreTruth(ProjectionStoreScenario scenario)
    {
        await (scenario switch
        {
            ProjectionStoreScenario.CanonicalRevisionAndTombstone => Run(NewerCanonicalOrderWinsAndTombstoneCannotRollBack),
            ProjectionStoreScenario.CompanyProfileIsImmutable => Run(SameOrderCannotMoveBetweenCompanyProfiles),
            ProjectionStoreScenario.ProfileResetClearsRevisionHistory => Run(TombstoneWinsSameRevisionAndProfileResetClearsHistory),
            ProjectionStoreScenario.OwnerUpgradeAtSameRevision => Run(SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified),
            ProjectionStoreScenario.SameProfileReconnect => Run(SameProfileReconnectRetainsReadyProjection),
            ProjectionStoreScenario.ScopeChange => Run(ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor),
            ProjectionStoreScenario.RestoreRevisionCannotRollBack => Run(RestoreStateCannotRollBackAppliedRevision),
            ProjectionStoreScenario.CompanySnapshotComposition => Run(SharedSnapshotCanBeComposedWithoutAPageCache),
            ProjectionStoreScenario.SameProfileConnectionReplacement => Run(() => ConnectionReplacementInvalidatesAuthority(pathCase: false)),
            ProjectionStoreScenario.ConnectionScopePathCase => Run(() => ConnectionReplacementInvalidatesAuthority(pathCase: true)),
            ProjectionStoreScenario.SameRevisionOwnerPersistence => PersistenceReconcilesSameRevisionOwnerUpgrade(),
            ProjectionStoreScenario.LiveTombstonePersistence => PersistenceReconcilesTombstoneAfterBlockedWrite(ownerCandidate: false),
            ProjectionStoreScenario.OwnerTombstonePersistence => PersistenceReconcilesTombstoneAfterBlockedWrite(ownerCandidate: true),
            ProjectionStoreScenario.CenterOperationWinner => CenterOperationReconcilesNewerOwnerDuringDurableWrite(),
            ProjectionStoreScenario.CenterOperationAuthoritySwitch => CenterOperationRejectsHostAndProfileReplacement(),
            ProjectionStoreScenario.CenterOperationCommittedFailure => CenterOperationRetainsCommittedProjectionOnAdoptionFailure(),
            ProjectionStoreScenario.StaleMissingOwner => StaleMissingOwnerCannotClearReplacementProjection(),
            ProjectionStoreScenario.BatchHydration => Run(BatchHydrationPublishesOneNotification),
            ProjectionStoreScenario.DirectNotificationHydration => DirectNotificationHydratesMissingOrder(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        });
    }

    private static Task Run(Action scenario) { scenario(); return Task.CompletedTask; }

    private static void ConnectionReplacementInvalidatesAuthority(bool pathCase)
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), pathCase ? "Upper path authority" : "Old authority");
        var oldRevision = pathCase ? 8 : 20;
        var oldScope = pathCase ? $"https://profiles.example/api/A|{profileId}" : $"https://first.example/|{profileId}";
        var replacementScope = pathCase ? $"https://profiles.example/api/a|{profileId}" : $"https://replacement.example/|{profileId}";
        var store = RestoringStore(profileId, oldRevision, oldScope);
        var resets = 0;
        store.Reset += () => resets++;
        Assert.True(store.TryPublishRemoteOrder(order, oldRevision));
        var oldAuthority = store.CaptureAuthorityScope();

        var replacementRevision = pathCase ? 1 : 2;
        Restore(store, profileId, replacementRevision, replacementScope, seconds: 1);

        Assert.Null(store.Get(order.Id));
        Assert.Equal(1, resets);
        Assert.Equal(replacementRevision, store.RestoreState.LastAppliedRevision);
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(HostedOrderCommittedProjectionResult.ScopeChanged,
            store.TryAdoptCommittedOrder(oldAuthority, order, oldRevision + 1));

        if (pathCase)
        {
            return;
        }

        var replacement = CreateOrder(order.Id, order.CompanyProfileId, "New authority");
        Assert.True(store.TryPublishRemoteOrder(replacement, 3));
        Restore(store, profileId, 1, replacementScope, seconds: 2);

        Assert.Same(replacement, store.Get(order.Id)?.Order);
        Assert.Equal(3, store.RestoreState.LastAppliedRevision);
        Assert.Equal(1, resets);
    }

    private static async Task PersistenceReconcilesSameRevisionOwnerUpgrade()
    {
        var race = ProjectionPersistenceRace.Create("Canonical", publishOrder: true);
        var persistence = race.Store.AdoptAndPersistCommittedOrderAsync(
            race.Authority, race.Order, 4, race.PersistAsync);
        await race.FirstWriteEntered.Task;
        var owner = CreateOwner(race.Order, 4, 9);
        Assert.True(race.Store.TryPublishOwner(owner));
        race.ReleaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.AlreadyCurrent, await persistence);
        Assert.Equal(2, race.Persisted.Count);
        Assert.Null(race.Persisted[0].OwnerProjection);
        Assert.Same(owner, race.Persisted[1].OwnerProjection);
        Assert.Equal(4, race.Store.RestoreState.LastAppliedRevision);
    }

    private static async Task PersistenceReconcilesTombstoneAfterBlockedWrite(bool ownerCandidate)
    {
        var race = ProjectionPersistenceRace.Create(ownerCandidate ? "Owner candidate" : "Live revision",
            publishOrder: ownerCandidate);
        var persistence = ownerCandidate
            ? race.Store.AdoptAndPersistCommittedOwnerAsync(race.Authority, CreateOwner(race.Order, 4, 9), race.PersistAsync)
            : race.Store.AdoptAndPersistCommittedOrderAsync(race.Authority, race.Order, 5, race.PersistAsync);
        await race.FirstWriteEntered.Task;
        var tombstoneRevision = ownerCandidate ? 5 : 6;
        Assert.True(race.Store.TryPublishTombstone(race.Order.Id, tombstoneRevision, race.Order.CompanyProfileId));
        race.ReleaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.Adopted, await persistence);
        Assert.Equal([false, true], race.Persisted.Select(candidate => candidate.Deleted));
        Assert.True(race.Store.Get(race.Order.Id)?.Deleted);
        Assert.Equal(tombstoneRevision, race.Store.RestoreState.LastAppliedRevision);
    }

    private static async Task CenterOperationReconcilesNewerOwnerDuringDurableWrite()
    {
        var fixture = await CenterOperationFixture.CreateAsync();
        fixture.Handler.Projection = fixture.Owner("Revision five", 5, 9);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeSaveTradeOrderAsync = async order => { if (order.Title != "Revision five") { return; } writeEntered.TrySetResult(); await releaseWrite.Task; };
        var operation = fixture.Service.AcceptDeliveryAsync(fixture.Current);
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Store.TryPublishOwner(fixture.Owner("Revision six", 6, 10)));
        releaseWrite.SetResult();
        Assert.True((await operation).Success);
        Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(6, fixture.Store.Get(fixture.Current.Order.Id)?.ObjectRevision);
        Assert.Equal(6, await fixture.LocalState.LoadObjectRevisionAsync(ProfileSyncCollections.TradeOrders, fixture.Current.Order.Id.ToString("D")));
        await VerifyDurableAuthorityRepair(replacementHasWinner: false);
        await VerifyDurableAuthorityRepair(replacementHasWinner: true);
    }

    private static async Task DirectNotificationHydratesMissingOrder()
    {
        var fixture = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(fixture);

        var projection = await fixture.Service.ResolveNotificationNavigationAsync(
            fixture.Current.Order.Id);

        Assert.NotNull(projection);
        Assert.Equal(fixture.Current.Order.Id, projection.Order.Id);
        Assert.Equal(fixture.Current.ObjectRevision, projection.ObjectRevision);
        Assert.Equal(fixture.Current.CompanyRevision, projection.CompanyRevision);
        Assert.Same(
            projection,
            fixture.Store.Get(fixture.Current.Order.Id)?.OwnerProjection);
        Assert.Equal(fixture.Current.Order.Id, fixture.Runtime.DurableOrder?.Id);

        var raced = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(raced);
        raced.Handler.OwnerMissing = true;
        await raced.Service.RefreshAsync(raced.Current.Order);
        Assert.True(raced.Service.IsCanonicalOwnerMissing(raced.Current.Order.Id));
        raced.Handler.OwnerMissing = false;
        var newer = raced.Owner("Newer background winner", 5, 9);
        raced.Handler.BeforeResponse = _ => Assert.True(raced.Store.TryPublishOwner(newer));
        var resolvedRace = await raced.Service.ResolveNotificationNavigationAsync(
            raced.Current.Order.Id);
        Assert.Same(newer, resolvedRace);
        Assert.Same(newer, raced.Store.Get(raced.Current.Order.Id)?.OwnerProjection);
        Assert.False(raced.Service.IsCanonicalOwnerMissing(raced.Current.Order.Id));

        var failedPersistence = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(failedPersistence);
        var inMemoryWinner = failedPersistence.Owner("Unpersisted winner", 5, 9);
        failedPersistence.Runtime.SaveTradeOrderResult = false;
        failedPersistence.Runtime.BeforeSaveTradeOrderAsync = _ =>
        {
            Assert.True(failedPersistence.Store.TryPublishOwner(inMemoryWinner));
            return Task.CompletedTask;
        };
        var rejectedPersistence = await failedPersistence.Service.ResolveNotificationNavigationAsync(
            failedPersistence.Current.Order.Id);
        Assert.Null(rejectedPersistence);
        Assert.Null(failedPersistence.Runtime.DurableOrder);

        var deleted = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(deleted);
        deleted.Handler.BeforeResponse = _ => Assert.True(deleted.Store.TryPublishTombstone(
            deleted.Current.Order.Id,
            5,
            deleted.Current.Order.CompanyProfileId));
        var rejectedDeletion = await deleted.Service.ResolveNotificationNavigationAsync(
            deleted.Current.Order.Id);
        Assert.Null(rejectedDeletion);
        Assert.True(deleted.Store.Get(deleted.Current.Order.Id)?.Deleted);

        var replacedAuthority = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(replacedAuthority);
        replacedAuthority.Handler.BeforeResponse = _ =>
            replacedAuthority.ReplaceAuthority(replaceProfile: false);
        var rejectedAuthority = await replacedAuthority.Service.ResolveNotificationNavigationAsync(
            replacedAuthority.Current.Order.Id);
        Assert.Null(rejectedAuthority);
        Assert.Null(replacedAuthority.Runtime.DurableOrder);

        var protectedLocal = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(protectedLocal);
        var localDraft = TradeOrderWorkflow.CopyOrder(protectedLocal.Current.Order);
        localDraft.Title = "Unsynced local draft";
        localDraft.CompanyCommission = null;
        protectedLocal.Runtime.SeedDurableOrder(localDraft);
        Assert.IsType<List<ProfileSyncPendingSave>>(protectedLocal.ProfileSync.PendingSaves)
            .Add(new(ProfileSyncCollections.TradeOrders, protectedLocal.Current.Order.Id.ToString("D")));
        var resolvedPending = await protectedLocal.Service.ResolveNotificationNavigationAsync(
            protectedLocal.Current.Order.Id);

        Assert.NotNull(resolvedPending);
        Assert.NotNull(protectedLocal.Handler.LastRequestUri);
        Assert.Equal(
            $"/api/trade/v1/commissions/{protectedLocal.Current.Order.Id:D}/owner",
            protectedLocal.Handler.LastRequestUri!.AbsolutePath);
        Assert.Null(protectedLocal.Store.Get(protectedLocal.Current.Order.Id));
        Assert.Equal("Unsynced local draft", protectedLocal.Runtime.DurableOrder?.Title);
        Assert.Null(protectedLocal.Runtime.DurableOrder?.CompanyCommission);
        Assert.Same(resolvedPending, protectedLocal.Service.GetForOrder(protectedLocal.Current.Order.Id));

        var committed = protectedLocal.Owner("Canonical after command", 5, 9);
        protectedLocal.Handler.Projection = committed;
        var command = await protectedLocal.Service.AcceptDeliveryAsync(resolvedPending);
        Assert.True(command.Success);
        Assert.Equal(5, protectedLocal.Service.GetForOrder(protectedLocal.Current.Order.Id)?.ObjectRevision.Value);
        Assert.Equal("Canonical after command", protectedLocal.Service.GetForOrder(protectedLocal.Current.Order.Id)?.Order.Title);
        Assert.Null(protectedLocal.Store.Get(protectedLocal.Current.Order.Id));
        Assert.Equal("Unsynced local draft", protectedLocal.Runtime.DurableOrder?.Title);
        Assert.Null(protectedLocal.Runtime.DurableOrder?.CompanyCommission);

        Assert.True(Assert.IsType<List<ProfileSyncPendingSave>>(protectedLocal.ProfileSync.PendingSaves)
            .RemoveAll(item => item.ObjectId == protectedLocal.Current.Order.Id.ToString("D")) > 0);
        var afterProtection = protectedLocal.Owner("Canonical after protection", 6, 10);
        protectedLocal.Handler.Projection = afterProtection;
        var resolvedAfterProtection = await protectedLocal.Service.ResolveNotificationNavigationAsync(
            protectedLocal.Current.Order.Id);
        Assert.Equal(6, resolvedAfterProtection?.ObjectRevision.Value);
        Assert.Null(protectedLocal.Store.Get(protectedLocal.Current.Order.Id));
        Assert.Equal("Unsynced local draft", protectedLocal.Runtime.DurableOrder?.Title);

        protectedLocal.Runtime.SeedDurableOrder(null);
        var afterCollision = protectedLocal.Owner("Canonical after collision", 7, 11);
        protectedLocal.Handler.Projection = afterCollision;
        var resolvedAfterCollision = await protectedLocal.Service.ResolveNotificationNavigationAsync(
            protectedLocal.Current.Order.Id);
        Assert.Equal(7, resolvedAfterCollision?.ObjectRevision.Value);
        Assert.Same(
            resolvedAfterCollision,
            protectedLocal.Store.Get(protectedLocal.Current.Order.Id)?.OwnerProjection);
        Assert.Equal("Canonical after collision", protectedLocal.Runtime.DurableOrder?.Title);

        protectedLocal.ReplaceAuthority(replaceProfile: true);
        Assert.Null(protectedLocal.Service.GetForOrder(protectedLocal.Current.Order.Id));
        Assert.Equal("Canonical after collision", protectedLocal.Runtime.DurableOrder?.Title);

        var newerHosted = await CreateProtectedLinkedFixtureAsync();
        var newerHostedProjection = newerHosted.Owner("Newer hosted owner", 6, 10);
        Assert.True(newerHosted.Store.TryPublishOwner(newerHostedProjection));
        Assert.Same(
            newerHostedProjection,
            newerHosted.Service.GetForOrder(newerHosted.Current.Order.Id));

        var missingLinked = await CreateProtectedLinkedFixtureAsync();
        missingLinked.Handler.OwnerMissing = true;
        await missingLinked.Service.RefreshAsync(
            missingLinked.Service.GetForOrder(missingLinked.Current.Order.Id)!.Order);
        Assert.Null(missingLinked.Service.GetForOrder(missingLinked.Current.Order.Id));
        Assert.True(missingLinked.Service.IsCanonicalOwnerMissing(missingLinked.Current.Order.Id));
        Assert.Equal("Unsynced local draft", missingLinked.Runtime.DurableOrder?.Title);

        var dismissedLinked = await CreateProtectedLinkedFixtureAsync();
        dismissedLinked.Service.DismissLinkedProjectionForLocalOrder(
            dismissedLinked.Current.Order.Id);
        Assert.Null(dismissedLinked.Service.GetForOrder(dismissedLinked.Current.Order.Id));
        Assert.Equal("Unsynced local draft", dismissedLinked.Runtime.DurableOrder?.Title);

        var dismissedInFlight = await CreateProtectedLinkedFixtureAsync();
        var inFlightOwner = dismissedInFlight.Service.GetForOrder(
            dismissedInFlight.Current.Order.Id)!;
        dismissedInFlight.Handler.Projection = dismissedInFlight.Owner(
            "Committed after local selection",
            5,
            9);
        dismissedInFlight.Handler.BeforeResponse = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                dismissedInFlight.Service.DismissLinkedProjectionForLocalOrder(
                    dismissedInFlight.Current.Order.Id);
            }
        };
        var dismissedResult = await dismissedInFlight.Service.AcceptDeliveryAsync(inFlightOwner);
        Assert.True(dismissedResult.Success);
        Assert.True(dismissedResult.HostCommitted);
        Assert.Null(dismissedInFlight.Service.GetForOrder(dismissedInFlight.Current.Order.Id));
        Assert.Equal("Unsynced local draft", dismissedInFlight.Runtime.DurableOrder?.Title);
    }

    private static async Task<CenterOperationFixture> CreateProtectedLinkedFixtureAsync()
    {
        var fixture = await CenterOperationFixture.CreateAsync(publishOwner: false);
        AlignCommissionIdentity(fixture);
        var localDraft = TradeOrderWorkflow.CopyOrder(fixture.Current.Order);
        localDraft.Title = "Unsynced local draft";
        localDraft.CompanyCommission = null;
        fixture.Runtime.SeedDurableOrder(localDraft);
        Assert.IsType<List<ProfileSyncPendingSave>>(fixture.ProfileSync.PendingSaves)
            .Add(new(ProfileSyncCollections.TradeOrders, fixture.Current.Order.Id.ToString("D")));
        Assert.NotNull(await fixture.Service.ResolveNotificationNavigationAsync(
            fixture.Current.Order.Id));
        return fixture;
    }

    private static void AlignCommissionIdentity(CenterOperationFixture fixture) =>
        fixture.Current.Order.CompanyCommission = fixture.Current.Order.CompanyCommission! with
        {
            CommissionId = fixture.Current.Order.Id
        };

    private static async Task CenterOperationRejectsHostAndProfileReplacement()
    {
        foreach (var operation in Enum.GetValues<CenterAuthorityOperation>())
        {
            foreach (var replaceProfile in new[] { false, true })
            {
                var fixture = await CenterOperationFixture.CreateAsync();
                fixture.Handler.Projection = fixture.Owner("Revision five", 5, 9);
                Action switchAuthority = () => fixture.ReplaceAuthority(replaceProfile);
                fixture.Runtime.BeforeSaveTradeCrafter = operation == CenterAuthorityOperation.Identity ? switchAuthority : null;
                fixture.Handler.BeforeResponse = operation == CenterAuthorityOperation.Identity ? null : _ => switchAuthority();

                var result = await fixture.InvokeAsync(operation);
                Assert.False(result.Success);
                Assert.Contains("authority", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
                if (operation != CenterAuthorityOperation.Identity)
                {
                    Assert.Equal("profiles.example", fixture.Handler.LastRequestUri?.Host);
                }
            }
        }
    }

    private static async Task StaleMissingOwnerCannotClearReplacementProjection()
    {
        var sameAuthority = await CenterOperationFixture.CreateAsync();
        sameAuthority.Handler.OwnerMissing = true;
        sameAuthority.Handler.BeforeResponse = request => { if (request.Method == HttpMethod.Get) { Assert.True(sameAuthority.Store.TryPublishOwner(sameAuthority.Owner("Same-authority revision five", 5, 9))); } };

        await sameAuthority.Service.RefreshAsync(sameAuthority.Current.Order);

        Assert.Equal("Same-authority revision five",
            sameAuthority.Store.GetOwnerProjection(sameAuthority.Current.Order.Id)?.Order.Title);
        Assert.False(sameAuthority.Service.IsCanonicalOwnerMissing(sameAuthority.Current.Order.Id));
        Assert.Null(sameAuthority.Service.GetErrorForOrder(sameAuthority.Current.Order.Id));

        foreach (var replaceProfile in new[] { false, true })
        {
            var fixture = await CenterOperationFixture.CreateAsync();
            fixture.Handler.OwnerMissing = true;
            fixture.Handler.BeforeResponse = request => { if (request.Method == HttpMethod.Get) { fixture.ReplaceAuthority(replaceProfile, "Replacement winner"); } };

            await fixture.Service.RefreshAsync(fixture.Current.Order);
            Assert.Equal("Replacement winner", fixture.Store.GetOwnerProjection(fixture.Current.Order.Id)?.Order.Title);
            Assert.False(fixture.Service.IsCanonicalOwnerMissing(fixture.Current.Order.Id));
            Assert.Null(fixture.Service.GetErrorForOrder(fixture.Current.Order.Id));
        }
    }

    private static async Task CenterOperationRetainsCommittedProjectionOnAdoptionFailure()
    {
        var fixture = await CenterOperationFixture.CreateAsync();
        fixture.Handler.Projection = fixture.Owner("Host-committed revision five", 5, 9);
        fixture.Handler.BeforeResponse = request => { if (request.Method == HttpMethod.Post) { fixture.ReplaceAuthority(replaceProfile: true); } };

        var result = await fixture.Service.AcceptDeliveryAsync(fixture.Current);

        Assert.False(result.Success);
        Assert.True(result.HostCommitted);
        Assert.Equal("Host-committed revision five", result.Projection?.Order.Title);
        Assert.Contains("authority", result.Message!, StringComparison.OrdinalIgnoreCase);
        await DraftDiscardRefusesPublishedWinner();
    }

    private static async Task DraftDiscardRefusesPublishedWinner()
    {
        var fixture = await CenterOperationFixture.CreateAsync(draft: true);
        var published = fixture.Owner("Published while confirmation was open", 5, 9);
        published.Order.CompanyCommission = published.Order.CompanyCommission! with { PublicMetadata = published.Order.CompanyCommission.PublicMetadata with { ViewState = CompanyCommissionPublicViewState.Published } };
        fixture.Handler.Projection = published;
        var result = await fixture.Service.CancelDraftAsync(fixture.Current.Order, "Draft discarded before publication.");
        Assert.False(result.Success);
        Assert.Contains("no longer an unpublished draft", result.Message!, StringComparison.OrdinalIgnoreCase); Assert.Equal(0, fixture.Handler.PostCount); Assert.Equal((published.Order.Title, published.ObjectRevision), (fixture.Store.GetOwnerProjection(published.Order.Id)?.Order.Title, fixture.Store.GetOwnerProjection(published.Order.Id)?.ObjectRevision));
    }

    private static async Task VerifyDurableAuthorityRepair(bool replacementHasWinner)
    {
        var fixture = await CenterOperationFixture.CreateAsync();
        fixture.Handler.Projection = fixture.Owner("Revision five", 5, 9);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeSaveTradeOrderAsync = async order => { if (order.Title == "Revision five") { entered.SetResult(); await release.Task; } };
        var operation = fixture.Service.AcceptDeliveryAsync(fixture.Current);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ReplaceAuthority(replaceProfile: true, replacementHasWinner ? "Replacement winner" : null);
        release.SetResult();

        var result = await operation;
        Assert.False(result.Success);
        Assert.Contains("authority", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replacementHasWinner ? "Replacement winner" : null, fixture.Runtime.DurableOrder?.Title);
    }

    private static void NewerCanonicalOrderWinsAndTombstoneCannotRollBack()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var revisions = new List<long>();
        store.Changed += projection => revisions.Add(projection.ObjectRevision);

        Assert.True(Publish(store, orderId, companyProfileId, "Revision two", 2));
        Assert.False(Publish(store, orderId, companyProfileId, "Stale revision one", 1));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(Publish(store, orderId, companyProfileId, "Stale after delete", 2));

        var current = Assert.IsType<HostedOrderProjectionSnapshot>(store.Get(orderId));
        Assert.True(current.Deleted);
        Assert.Equal(3, current.ObjectRevision);
        Assert.Equal([2L, 3L], revisions);
    }

    private static void BatchHydrationPublishesOneNotification()
    {
        var store = new HostedOrderProjectionStore();
        var companyProfileId = Guid.NewGuid();
        var first = CreateOrder(Guid.NewGuid(), companyProfileId, "First cached order");
        var second = CreateOrder(Guid.NewGuid(), companyProfileId, "Second cached order");
        var singleNotifications = 0;
        var batchNotifications = new List<IReadOnlyList<HostedOrderProjectionSnapshot>>();
        store.Changed += _ => singleNotifications++;
        store.BatchChanged += snapshots => batchNotifications.Add(snapshots);

        var restored = store.PublishRemoteOrders([(first, 2), (second, 3)]);

        Assert.Equal(2, restored);
        Assert.Equal(0, singleNotifications);
        Assert.Single(batchNotifications);
        Assert.Equal(2, batchNotifications[0].Count);
        Assert.Same(first, store.Get(first.Id)?.Order);
        Assert.Same(second, store.Get(second.Id)?.Order);
    }

    private static void SameOrderCannotMoveBetweenCompanyProfiles()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        Assert.True(Publish(store, orderId, Guid.NewGuid(), "Original company", 1));

        Assert.Throws<InvalidOperationException>(() =>
            Publish(store, orderId, Guid.NewGuid(), "Different company", 2));
    }

    private static void TombstoneWinsSameRevisionAndProfileResetClearsHistory()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        store.ResetForProfile("profile-one");

        Assert.True(Publish(store, orderId, companyProfileId, "Live", 3));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(Publish(store, orderId, companyProfileId, "Resurrected", 3));

        store.ResetForProfile("profile-two");
        Assert.Null(store.Get(orderId));
        Assert.True(Publish(store, orderId, Guid.NewGuid(), "New profile", 1));
    }

    private static void SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified()
    {
        var store = new HostedOrderProjectionStore();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Canonical");
        var notifications = 0;
        store.Changed += _ => notifications++;

        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishOwner(CreateOwner(order, 4, 9)));

        Assert.NotNull(store.GetOwnerProjection(order.Id));
        Assert.Equal(2, notifications);
    }

    private static void SameProfileReconnectRetainsReadyProjection()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var connectionScopeId = $"https://profiles.example/|{profileId}";
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Trusted order");
        var store = RestoringStore(profileId, 0, connectionScopeId);
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishRestoreState(store.RestoreState.Apply(
            ReadyStatus(profileId, revision: 4),
            Now.AddSeconds(1))));

        store.BeginProfileRestore(profileId, false, 4, Now.AddSeconds(2), connectionScopeId);

        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(HostedOrderRestoreStage.Reconnecting, store.RestoreState.Stage);
        Assert.True(store.RestoreState.ShowsCompleteProjection);
    }

    private static void ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor()
    {
        var firstProfile = Guid.NewGuid().ToString("D");
        var secondProfile = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old scope");
        var store = RestoringStore(firstProfile, 20, $"https://profiles.example/|{firstProfile}");
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 20));

        Restore(store, secondProfile, 2, $"https://profiles.example/|{secondProfile}", seconds: 1);

        Assert.Null(store.Get(order.Id));
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.CanShowAuthoritativeEmpty);
    }

    private static void RestoreStateCannotRollBackAppliedRevision()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var store = RestoringStore(profileId, 7, $"https://profiles.example/|{profileId}");

        Assert.False(store.TryPublishRestoreState(store.RestoreState with
        {
            LastAppliedRevision = 6,
            Stage = HostedOrderRestoreStage.Ready
        }));
        Assert.Equal(7, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.IsAuthoritative);
    }

    private static void SharedSnapshotCanBeComposedWithoutAPageCache()
    {
        var store = new HostedOrderProjectionStore();
        var companyProfileId = Guid.NewGuid();
        var included = CreateOrder(Guid.NewGuid(), companyProfileId, "Included");
        var other = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Other company");
        Assert.True(store.TryPublishRemoteOrder(included, 2));
        Assert.True(store.TryPublishRemoteOrder(other, 3));

        var snapshot = Assert.Single(store.GetAll(companyProfileId));

        Assert.Same(included, snapshot.Order);
        Assert.Equal(2, store.GetAll().Count);
    }

    private static ProfileSyncStatus ReadyStatus(string profileId, long revision) =>
        new(true, true, revision, 0, 0, Now, "Synced") { ProfileId = profileId, Stage = ProfileSyncStage.Ready };

    private static TradeOrder CreateOrder(Guid orderId, Guid companyProfileId, string title) =>
        new() { Id = orderId, CompanyProfileId = companyProfileId, Title = title };

    private static bool Publish(HostedOrderProjectionStore store, Guid orderId, Guid companyProfileId, string title, long revision) =>
        store.TryPublishRemoteOrder(CreateOrder(orderId, companyProfileId, title), revision);

    private static CompanyCommissionOwnerProjection CreateOwner(TradeOrder order, long objectRevision, long companyRevision) =>
        new() { Order = order, ObjectRevision = new(objectRevision), CompanyRevision = new(companyRevision) };

    private static HostedOrderProjectionStore RestoringStore(string profileId, long revision, string connectionScope)
    {
        var store = new HostedOrderProjectionStore();
        Restore(store, profileId, revision, connectionScope);
        return store;
    }

    private static void Restore(HostedOrderProjectionStore store, string profileId, long revision, string connectionScope, int seconds = 0) =>
        store.BeginProfileRestore(profileId, false, revision, Now.AddSeconds(seconds), connectionScope);

    private sealed class ProjectionPersistenceRace(HostedOrderProjectionStore store, TradeOrder order)
    {
        public HostedOrderProjectionStore Store { get; } = store;
        public TradeOrder Order { get; } = order;
        public HostedOrderAuthorityScope Authority { get; } = store.CaptureAuthorityScope();
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<HostedOrderProjectionSnapshot> Persisted { get; } = [];

        public static ProjectionPersistenceRace Create(string title, bool publishOrder = false)
        {
            var profileId = Guid.NewGuid().ToString("D");
            var store = RestoringStore(profileId, 4, $"https://profiles.example/|{profileId}");
            var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), title);
            if (publishOrder)
            {
                Assert.True(store.TryPublishRemoteOrder(order, 4));
            }
            return new(store, order);
        }

        public async Task PersistAsync(HostedOrderProjectionSnapshot candidate)
        {
            Persisted.Add(candidate);
            if (Persisted.Count == 1)
            {
                FirstWriteEntered.SetResult();
                await ReleaseFirstWrite.Task;
            }
        }
    }

    private sealed record CenterOperationFixture(CompanyCommissionOwnerProjection Current, HostedOrderProjectionStore Store, CenterOperationRuntime Runtime,
        ProfileSyncLocalStateService LocalState, ProfileSyncService ProfileSync, OwnerMutationHandler Handler, TradeCommissionOperationsService Service)
    {
        public const string Host = "https://profiles.example/api/";
        public CompanyCommissionOwnerProjection Owner(string title, long orderRevision, long companyRevision)
        {
            var order = TradeOrderWorkflow.CopyOrder(Current.Order);
            order.Title = title;
            return CreateOwner(order, orderRevision, companyRevision);
        }
        public void ReplaceAuthority(bool replaceProfile, string? winnerTitle = null)
        {
            const string replacementHost = "https://replacement.example/api/";
            var profileId = replaceProfile ? Guid.NewGuid().ToString("D") : Store.RestoreState.ProfileId!;
            Runtime.SaveRawSetting(
                replaceProfile ? ProfileSyncSettingsKeys.ConnectedProfileId : ProfileSyncSettingsKeys.HostUrl,
                JsonSerializer.Serialize(replaceProfile ? profileId : replacementHost));
            if (!replaceProfile && winnerTitle == null)
            {
                return;
            }

            var host = replaceProfile ? Host : replacementHost;
            Store.BeginProfileRestore(profileId, false, 0, Now, $"{ProfileHostClient.NormalizeHostUrl(host)}|{profileId}");
            if (winnerTitle != null)
            {
                Assert.True(Store.TryPublishOwner(Owner(winnerTitle, 1, 1)));
            }
        }
        public Task<TradeCommissionOperatorResult> InvokeAsync(CenterAuthorityOperation operation) => operation switch
        {
            CenterAuthorityOperation.Command => Service.AcceptDeliveryAsync(Current),
            CenterAuthorityOperation.Recovery => Service.RecoverParticipantAsync(Current),
            CenterAuthorityOperation.Claim => Service.IssueClaimLinkAsync(Current),
            CenterAuthorityOperation.Identity => Service.ConfirmIdentityAsync(Current, new TradeCrafterProfile
            { CompanyProfileId = Current.Order.CompanyProfileId, DisplayName = "Test", LodestoneCharacterId = "123" }, "123"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
        public static async Task<CenterOperationFixture> CreateAsync(
            bool draft = false,
            bool publishOwner = true)
        {
            var profileId = Guid.NewGuid().ToString("D");
            var current = new CompanyCommissionOwnerProjection { Order = CreateCommissionOrder(Guid.NewGuid()), ObjectRevision = new(4), CompanyRevision = new(8) };
            if (draft)
            {
                current.Order.CompanyCommission = current.Order.CompanyCommission! with { PublicMetadata = current.Order.CompanyCommission.PublicMetadata with { ViewState = CompanyCommissionPublicViewState.Draft } };
            }
            var runtime = new CenterOperationRuntime(profileId);
            var indexedDb = new IndexedDbService(runtime);
            var localState = new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(Host));
            await localState.LoadConnectionSettingsAsync();
            var store = RestoringStore(profileId, 4, $"{ProfileHostClient.NormalizeHostUrl(Host)}|{profileId}");
            if (publishOwner)
            {
                Assert.True(store.TryPublishOwner(current));
            }
            var recovery = CreateOwner(current.Order, 5, 9);
            recovery.Order.CompanyCommission = recovery.Order.CompanyCommission! with { RecoveryGrant = new(Guid.NewGuid(), Guid.NewGuid(), 1, Now) };
            var handler = new OwnerMutationHandler { Projection = current, RecoveryProjection = recovery };
            var http = new HttpClient(handler) { BaseAddress = new Uri(Host) };
            var profileSync = new ProfileSyncService(new ProfileHostClient(http, new ProfileHostClientOptions(Host)), localState, new WebSettingsService(indexedDb), store, []);
            typeof(ProfileSyncService).GetProperty(nameof(ProfileSyncService.CurrentStatus), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(profileSync, ReadyStatus(profileId, 4));
            var service = new TradeCommissionOperationsService(new TradeCommissionOperationsClient(new HttpClient(handler) { BaseAddress = new Uri(Host) }, localState),
                new TradeCompanyCollaborationClient(http, localState), new TradeOperationsPersistenceService(indexedDb, new TradeCompanyProfilePackageService()),
                localState, profileSync, store, new WebPlanPersistenceService(indexedDb), new AppState());
            return new(current, store, runtime, localState, profileSync, handler, service);
        }
        private static TradeOrder CreateCommissionOrder(Guid companyProfileId) => new()
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyProfileId,
            Title = "Revision four",
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = Guid.NewGuid(),
                CompanyId = new(companyProfileId),
                CommissionerActorId = "commissioner",
                Reference = "TEST-001",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                CurrentTermsVersion = 1,
                TermsVersions = [new CompanyCommissionTermsVersion { Version = 1, CreatedAtUtc = Now, CreatedBy = new("commissioner", CompanyCommissionActorKind.Commissioner),
                    Payment = new(CompanyCommissionPaymentSchedule.OnDelivery, "Test", 0, 0, 0, 0), PricingEvidence = new("test", "test", "test", Now) }],
                PublicMetadata = new() { PublicBriefId = "test-001", PublicUrl = "https://public.example/commission", ViewState = CompanyCommissionPublicViewState.Published },
                ActiveClaimCapabilityRevision = 1,
                Gates = new(new(CompanyCommissionClearanceState.NotRequired), new(CompanyCommissionClearanceState.NotRequired), new(CompanyCommissionClearanceState.NotRequired, [])),
                DeliveryReadiness = new(true),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }
    private sealed class OwnerMutationHandler : HttpMessageHandler
    {
        public required CompanyCommissionOwnerProjection Projection { get; set; }
        public required CompanyCommissionOwnerProjection RecoveryProjection { get; set; }
        public Action<HttpRequestMessage>? BeforeResponse { get; set; }
        public bool OwnerMissing { get; set; }
        public int PostCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            PostCount += request.Method == HttpMethod.Post ? 1 : 0;
            BeforeResponse?.Invoke(request);
            if (OwnerMissing && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new
                    { Error = "commission_missing", Message = "The commission is missing." })
                });
            }
            object body = request.Method == HttpMethod.Get ? Projection : request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("/reset-participant-recovery") => new TradeCommissionRecoveryResetResponse(
                    new CompanyCommissionMutationResult(CompanyCommissionMutationStatus.Applied),
                    RecoveryProjection, "https://public.example/commission#recover=test"),
                var path when path.EndsWith("/issue-claim-link") => new TradeCommissionClaimLinkResponse(
                    "https://public.example/commission#claim=test"),
                _ => new { Status = CompanyCommissionMutationStatus.Applied, Order = Projection.Order, Projection }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
    private sealed class CenterOperationRuntime(string profileId) : IJSRuntime
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal)
        {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(CenterOperationFixture.Host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId)
        };
        public int SaveTradeOrderCount { get; private set; }
        public TradeOrder? DurableOrder { get; private set; }
        public bool SaveTradeOrderResult { get; set; } = true;
        public Func<TradeOrder, Task>? BeforeSaveTradeOrderAsync { get; set; }
        public Action? BeforeSaveTradeCrafter { get; set; }
        public void SeedDurableOrder(TradeOrder? order) => DurableOrder = order;
        public void SaveRawSetting(string key, string value) => _settings[key] = value;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "IndexedDB.saveTradeOrder")
            {
                return SaveOrderAsync<TValue>((TradeOrder)args![0]!);
            }

            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(_settings),
                "IndexedDB.loadSetting" => _settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.loadTradeOrder" => DurableOrder,
                "IndexedDB.deleteTradeOrder" => DeleteOrder(),
                "IndexedDB.saveTradeCrafter" => SaveCrafter(),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }
        private bool SaveBatch(Dictionary<string, string> values) { foreach (var (key, value) in values) { _settings[key] = value; } return true; }
        private bool SaveSetting(string key, string value) { _settings[key] = value; return true; }
        private bool DeleteOrder() { DurableOrder = null; return true; }
        private bool SaveCrafter() { BeforeSaveTradeCrafter?.Invoke(); return true; }
        private async ValueTask<TValue> SaveOrderAsync<TValue>(TradeOrder order)
        {
            await (BeforeSaveTradeOrderAsync?.Invoke(order) ?? Task.CompletedTask);
            SaveTradeOrderCount++;
            if (SaveTradeOrderResult)
            {
                DurableOrder = order;
            }
            return (TValue)(object)SaveTradeOrderResult;
        }
    }

    private enum CenterAuthorityOperation { Command, Recovery, Claim, Identity }

    public enum ProjectionStoreScenario
    {
        CanonicalRevisionAndTombstone, CompanyProfileIsImmutable, ProfileResetClearsRevisionHistory, OwnerUpgradeAtSameRevision, SameProfileReconnect,
        ScopeChange, RestoreRevisionCannotRollBack, CompanySnapshotComposition, SameProfileConnectionReplacement, ConnectionScopePathCase,
        SameRevisionOwnerPersistence, LiveTombstonePersistence, OwnerTombstonePersistence, CenterOperationWinner, CenterOperationAuthoritySwitch,
        CenterOperationCommittedFailure, StaleMissingOwner, BatchHydration, DirectNotificationHydration
    }
}
