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

    public static IEnumerable<object[]> Scenarios =>
        Enum.GetValues<ProjectionStoreScenario>().Select(scenario => new object[] { scenario });

    [Theory]
    [MemberData(nameof(Scenarios))]
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
            ProjectionStoreScenario.DraftDiscardRefusesPublishedWinner => DraftDiscardRefusesPublishedWinner(),
            ProjectionStoreScenario.StaleMissingOwner => StaleMissingOwnerCannotClearReplacementProjection(),
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
        ProfileSyncLocalStateService LocalState, OwnerMutationHandler Handler, TradeCommissionOperationsService Service)
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
        public static async Task<CenterOperationFixture> CreateAsync(bool draft = false)
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
            Assert.True(store.TryPublishOwner(current));
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
            return new(current, store, runtime, localState, handler, service);
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
        public Func<TradeOrder, Task>? BeforeSaveTradeOrderAsync { get; set; }
        public Action? BeforeSaveTradeCrafter { get; set; }
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
            SaveTradeOrderCount++; DurableOrder = order; return (TValue)(object)true;
        }
    }

    private enum CenterAuthorityOperation { Command, Recovery, Claim, Identity }

    public enum ProjectionStoreScenario
    {
        CanonicalRevisionAndTombstone, CompanyProfileIsImmutable, ProfileResetClearsRevisionHistory, OwnerUpgradeAtSameRevision, SameProfileReconnect,
        ScopeChange, RestoreRevisionCannotRollBack, CompanySnapshotComposition, SameProfileConnectionReplacement, ConnectionScopePathCase,
        SameRevisionOwnerPersistence, LiveTombstonePersistence, OwnerTombstonePersistence, CenterOperationWinner, CenterOperationAuthoritySwitch,
        CenterOperationCommittedFailure, DraftDiscardRefusesPublishedWinner, StaleMissingOwner
    }
}
