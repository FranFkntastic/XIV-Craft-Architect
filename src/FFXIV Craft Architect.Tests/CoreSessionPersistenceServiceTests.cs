using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public class CoreSessionPersistenceServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ffxiv-ca-core-persistence-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveCurrentPlanAndLoadIntoSession_RoundTripsThroughCoreStore()
    {
        var source = CreateHost();
        ActivatePlan(source.Session, "Original Plan");

        Assert.True(await source.Persistence.SaveCurrentPlanAsync("plan-1", "Saved Plan"));
        var summaries = await source.Persistence.LoadPlanSummariesAsync();

        var target = CreateHost();
        target.Store = source.Store;
        target.Persistence = new CoreSessionPersistenceService(
            target.Session,
            source.Store,
            new CoreStoredPlanSnapshotBuilder(target.Session),
            new CorePlanSessionLoadService(target.Session));
        var result = await target.Persistence.LoadPlanIntoSessionAsync("plan-1");

        Assert.Single(summaries);
        Assert.NotNull(result);
        Assert.Equal("Saved Plan", target.Session.Identity.SourcePlanName);
        Assert.Equal("plan-1", target.Session.Identity.SourcePlanId);
        Assert.Equal("Original Plan", target.Session.ActivePlan?.Name);
        Assert.Single(target.Session.ProjectItems);
        Assert.False(target.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.False(target.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        Assert.False(await target.Persistence.SaveCurrentAutoSaveAsync());
    }

    [Fact]
    public async Task SaveCurrentAutoSave_SkipsEmptySessionAndRestoresSourceIdentity()
    {
        var source = CreateHost();

        Assert.False(await source.Persistence.SaveCurrentAutoSaveAsync());

        ActivatePlan(
            source.Session,
            "Working Plan",
            new CraftSessionIdentity(Guid.NewGuid(), "Working Plan", "named-id", "Named Plan"));
        Assert.True(await source.Persistence.SaveCurrentAutoSaveAsync());

        var target = CreateHost();
        target.Store = source.Store;
        target.Persistence = new CoreSessionPersistenceService(
            target.Session,
            source.Store,
            new CoreStoredPlanSnapshotBuilder(target.Session),
            new CorePlanSessionLoadService(target.Session));
        var result = await target.Persistence.LoadAutoSaveIntoSessionAsync();

        Assert.NotNull(result);
        Assert.Equal("named-id", target.Session.Identity.SourcePlanId);
        Assert.Equal("Named Plan", target.Session.Identity.SourcePlanName);
        Assert.Equal("Working Plan", target.Session.ActivePlan?.Name);
        Assert.False(target.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.False(target.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        Assert.False(await target.Persistence.SaveCurrentAutoSaveAsync());
    }

    [Fact]
    public async Task SaveCurrentAutoSave_SkipsCleanSessionAndMarksPersistedBuckets()
    {
        var host = CreateHost();
        ActivatePlan(host.Session, "Working Plan");
        Assert.True(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));

        Assert.True(await host.Persistence.SaveCurrentAutoSaveAsync());
        Assert.False(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.False(host.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));

        Assert.False(await host.Persistence.SaveCurrentAutoSaveAsync());
    }

    [Fact]
    public async Task SaveCurrentAutoSave_WhenSessionChangesDuringWrite_DoesNotMarkNewStatePersisted()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        ActivatePlan(session, "Working Plan");
        var innerStore = new FileCoreStoredPlanStore(new CoreStoredPlanStoreOptions(_rootDirectory));
        var store = new MutatingAutoSaveStore(innerStore, () => session.MarkPlanDecisionChanged("changed during autosave"));
        var persistence = new CoreSessionPersistenceService(
            session,
            store,
            new CoreStoredPlanSnapshotBuilder(session),
            new CorePlanSessionLoadService(session));

        Assert.True(await persistence.SaveCurrentAutoSaveAsync());

        Assert.True(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public async Task LoadPlanIntoSession_WithUnsupportedSnapshot_DoesNotMarkExistingSessionPersisted()
    {
        var host = CreateHost();
        ActivatePlan(host.Session, "Working Plan");
        Assert.True(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        await host.Store.SavePlanSnapshotAsync(new CoreStoredPlanSnapshot
        {
            Id = "future",
            Name = "Future Plan",
            SchemaVersion = CoreStoredPlanSnapshot.CurrentSchemaVersion + 1,
            PlanJson = "{}"
        });

        var result = await host.Persistence.LoadPlanIntoSessionAsync("future");

        Assert.NotNull(result);
        Assert.False(result.CanLoad);
        Assert.Equal("Working Plan", host.Session.ActivePlan?.Name);
        Assert.True(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public async Task RenamePlan_UpdatesStoredNameWithoutLoadingSession()
    {
        var host = CreateHost();
        ActivatePlan(host.Session, "Original Plan");
        await host.Persistence.SaveCurrentPlanAsync("plan-1", "Old Name");

        var result = await host.Persistence.RenamePlanAsync("plan-1", "New Name");
        var loaded = await host.Persistence.LoadPlanPayloadAsync("plan-1");

        Assert.True(result.Success);
        Assert.Equal("Old Name", result.OldName);
        Assert.Equal("New Name", result.NewName);
        Assert.Equal("New Name", loaded?.Name);
        Assert.Null(host.Session.Identity.SourcePlanId);
    }

    [Fact]
    public async Task RenamePlan_WhenRenamingActiveStoredSession_UpdatesSessionIdentity()
    {
        var host = CreateHost();
        ActivatePlan(host.Session, "Original Plan");
        await host.Persistence.SaveCurrentPlanAsync("plan-1", "Old Name");
        await host.Persistence.LoadPlanIntoSessionAsync("plan-1");

        var result = await host.Persistence.RenamePlanAsync("plan-1", "New Name");

        Assert.True(result.Success);
        Assert.Equal("plan-1", host.Session.Identity.SourcePlanId);
        Assert.Equal("New Name", host.Session.Identity.SourcePlanName);
        Assert.Equal("New Name", host.Session.Identity.Name);
        Assert.False(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public async Task DeletePlan_WhenDeletingActiveStoredSession_ClearsSessionSourceIdentity()
    {
        var host = CreateHost();
        ActivatePlan(host.Session, "Original Plan");
        await host.Persistence.SaveCurrentPlanAsync("plan-1", "Saved Plan");
        await host.Persistence.LoadPlanIntoSessionAsync("plan-1");

        var deleted = await host.Persistence.DeletePlanAsync("plan-1");

        Assert.True(deleted);
        Assert.Null(host.Session.Identity.SourcePlanId);
        Assert.Null(host.Session.Identity.SourcePlanName);
        Assert.Equal("Original Plan", host.Session.Identity.Name);
        Assert.Equal("Original Plan", host.Session.ActivePlan?.Name);
        Assert.False(host.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public void AddCraftSessionFoundation_RegistersPersistenceWorkflow()
    {
        var provider = new ServiceCollection()
            .AddCraftSessionFoundation()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<CoreSessionPersistenceService>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private TestHost CreateHost()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var store = new FileCoreStoredPlanStore(new CoreStoredPlanStoreOptions(_rootDirectory));
        var persistence = new CoreSessionPersistenceService(
            session,
            store,
            new CoreStoredPlanSnapshotBuilder(session),
            new CorePlanSessionLoadService(session));
        return new TestHost(session, store, persistence);
    }

    private static void ActivatePlan(
        CraftSessionState session,
        string planName,
        CraftSessionIdentity? identity = null)
    {
        session.ActivatePlan(
            new CraftingPlan
            {
                Name = planName,
                DataCenter = "Aether",
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Final Craft",
                        Quantity = 1,
                        Source = AcquisitionSource.MarketBuyNq,
                        CanBuyFromMarket = true
                    }
                ]
            },
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "plan loaded",
            identity);
    }

    private sealed class TestHost(
        CraftSessionState session,
        FileCoreStoredPlanStore store,
        CoreSessionPersistenceService persistence)
    {
        public CraftSessionState Session { get; } = session;
        public FileCoreStoredPlanStore Store { get; set; } = store;
        public CoreSessionPersistenceService Persistence { get; set; } = persistence;
    }

    private sealed class MutatingAutoSaveStore(
        ICoreStoredPlanStore inner,
        Action beforeSaveCompletes) : ICoreStoredPlanStore
    {
        public Task<IReadOnlyList<CoreStoredPlanSummary>> LoadPlanSummariesAsync(CancellationToken ct = default) =>
            inner.LoadPlanSummariesAsync(ct);

        public Task<CoreStoredPlanSnapshot?> LoadPlanSnapshotAsync(string planId, CancellationToken ct = default) =>
            inner.LoadPlanSnapshotAsync(planId, ct);

        public Task<bool> SavePlanSnapshotAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default) =>
            inner.SavePlanSnapshotAsync(snapshot, ct);

        public Task<bool> DeletePlanSnapshotAsync(string planId, CancellationToken ct = default) =>
            inner.DeletePlanSnapshotAsync(planId, ct);

        public Task<CoreStoredPlanSnapshot?> LoadAutoSaveAsync(CancellationToken ct = default) =>
            inner.LoadAutoSaveAsync(ct);

        public async Task<bool> SaveAutoSaveAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default)
        {
            var saved = await inner.SaveAutoSaveAsync(snapshot, ct);
            beforeSaveCompletes();
            return saved;
        }

        public Task<bool> DeleteAutoSaveAsync(CancellationToken ct = default) =>
            inner.DeleteAutoSaveAsync(ct);
    }
}
