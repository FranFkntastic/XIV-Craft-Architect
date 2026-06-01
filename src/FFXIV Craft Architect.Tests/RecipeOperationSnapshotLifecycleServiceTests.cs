using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeOperationSnapshotLifecycleServiceTests
{
    [Fact]
    public async Task GetOrBuildAsync_IdenticalConcurrentRequests_SharesSingleBuild()
    {
        var inner = new BlockingSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();

        var first = service.GetOrBuildAsync(plan, identity);
        var second = service.GetOrBuildAsync(plan, identity);
        await inner.WaitForBuildStartAsync();

        Assert.Equal(1, inner.BuildCount);

        inner.Release();
        var snapshots = await Task.WhenAll(first, second);

        Assert.Same(snapshots[0], snapshots[1]);
    }

    [Fact]
    public async Task GetOrBuildAsync_CancelledWaiter_DoesNotCancelSharedBuild()
    {
        var inner = new BlockingSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();
        using var cancelledWaiter = new CancellationTokenSource();

        var cancelledTask = service.GetOrBuildAsync(plan, identity, cancellationToken: cancelledWaiter.Token);
        var successfulTask = service.GetOrBuildAsync(plan, identity);
        await inner.WaitForBuildStartAsync();
        await cancelledWaiter.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);

        inner.Release();
        var snapshot = await successfulTask;

        Assert.NotNull(snapshot);
        Assert.Equal(1, inner.BuildCount);
    }

    [Fact]
    public async Task GetCurrentOrNullAsync_StaleAfterBuild_ReturnsNull()
    {
        var inner = new BlockingSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();
        var isCurrent = true;

        var task = service.GetCurrentOrNullAsync(plan, identity, _ => isCurrent);
        await inner.WaitForBuildStartAsync();
        isCurrent = false;
        inner.Release();

        var snapshot = await task;

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GetCurrentOrNullAsync_StaleBuild_IsNotRetained()
    {
        var inner = new BlockingSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();
        var isCurrent = true;

        var staleTask = service.GetCurrentOrNullAsync(plan, identity, _ => isCurrent);
        await inner.WaitForBuildStartAsync();
        isCurrent = false;
        inner.Release();
        Assert.Null(await staleTask);

        inner.ResetGate();
        isCurrent = true;
        var currentTask = service.GetCurrentOrNullAsync(plan, identity, _ => isCurrent);
        await inner.WaitForBuildStartAsync();
        inner.Release();
        var snapshot = await currentTask;

        Assert.NotNull(snapshot);
        Assert.Equal(2, inner.BuildCount);
    }

    [Fact]
    public async Task GetOrBuildAsync_CompletedBuild_IsNotRetainedAsPermanentCache()
    {
        var inner = new BlockingSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();

        var first = service.GetOrBuildAsync(plan, identity);
        await inner.WaitForBuildStartAsync();
        inner.Release();
        await first;

        inner.ResetGate();
        var second = service.GetOrBuildAsync(plan, identity);
        await inner.WaitForBuildStartAsync();
        inner.Release();
        await second;

        Assert.Equal(2, inner.BuildCount);
    }

    [Fact]
    public async Task GetOrBuildAsync_FailedBuild_IsNotCached()
    {
        var inner = new FailingOnceSnapshotService();
        var service = new RecipeOperationSnapshotLifecycleService(inner);
        var plan = CreatePlan();
        var identity = CreateIdentity();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetOrBuildAsync(plan, identity));
        var snapshot = await service.GetOrBuildAsync(plan, identity);

        Assert.NotNull(snapshot);
        Assert.Equal(2, inner.BuildCount);
    }

    private static CraftingPlan CreatePlan()
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Root",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };
    }

    private static RecipeOperationSnapshotIdentity CreateIdentity()
    {
        return new RecipeOperationSnapshotIdentity(
            PlanSessionVersion: 1,
            PlanStructureVersion: 2,
            PlanDecisionVersion: 3,
            PlanPriceVersion: 4,
            SettingsVersion: 5,
            RecipeDataIdentity: "test");
    }

    private sealed class BlockingSnapshotService : IRecipeOperationSnapshotService
    {
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BuildCount { get; private set; }

        public async Task<RecipeOperationSnapshot> BuildAsync(CraftingPlan? plan, CancellationToken ct = default)
        {
            return await BuildAsync(plan, CreateIdentity(), RecipeOperationSnapshotBuildOptions.Default, ct);
        }

        public async Task<RecipeOperationSnapshot> BuildAsync(
            CraftingPlan? plan,
            RecipeOperationSnapshotIdentity identity,
            RecipeOperationSnapshotBuildOptions? options = null,
            CancellationToken ct = default)
        {
            BuildCount++;
            _started.SetResult();
            await _release.Task;
            return new RecipeOperationSnapshot(
                Array.Empty<RecipeOperation>(),
                new Dictionary<string, RecipeOperation>(),
                new Dictionary<int, IReadOnlyList<RecipeOperation>>(),
                Array.Empty<RecipeOperationDiagnostic>(),
                true,
                RecipeOperationSnapshotMetadata.Empty with { Identity = identity });
        }

        public Task WaitForBuildStartAsync()
        {
            return _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Release()
        {
            _release.SetResult();
        }

        public void ResetGate()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FailingOnceSnapshotService : IRecipeOperationSnapshotService
    {
        public int BuildCount { get; private set; }

        public Task<RecipeOperationSnapshot> BuildAsync(CraftingPlan? plan, CancellationToken ct = default)
        {
            return BuildAsync(plan, CreateIdentity(), RecipeOperationSnapshotBuildOptions.Default, ct);
        }

        public Task<RecipeOperationSnapshot> BuildAsync(
            CraftingPlan? plan,
            RecipeOperationSnapshotIdentity identity,
            RecipeOperationSnapshotBuildOptions? options = null,
            CancellationToken ct = default)
        {
            BuildCount++;
            if (BuildCount == 1)
            {
                throw new InvalidOperationException("first build fails");
            }

            return Task.FromResult(new RecipeOperationSnapshot(
                Array.Empty<RecipeOperation>(),
                new Dictionary<string, RecipeOperation>(),
                new Dictionary<int, IReadOnlyList<RecipeOperation>>(),
                Array.Empty<RecipeOperationDiagnostic>(),
                true,
                RecipeOperationSnapshotMetadata.Empty with { Identity = identity }));
        }
    }
}
