using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeOrderPlanRestorePolicyTests
{
    private static readonly Guid OrderA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(CurrentRequestScenario.Current, true)]
    [InlineData(CurrentRequestScenario.SelectionChanged, false)]
    [InlineData(CurrentRequestScenario.PlanChanged, false)]
    [InlineData(CurrentRequestScenario.TabChanged, false)]
    [InlineData(CurrentRequestScenario.Disposed, false)]
    [InlineData(CurrentRequestScenario.NewerRequest, false)]
    public void AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario scenario, bool expected)
    {
        var selectedOrderId = scenario == CurrentRequestScenario.SelectionChanged ? OrderB : OrderA;
        var selectedPlanId = scenario == CurrentRequestScenario.PlanChanged ? "plan-b" : "plan-a";
        var activeTab = scenario == CurrentRequestScenario.TabChanged ? 1 : 0;
        var generation = scenario == CurrentRequestScenario.NewerRequest ? 8 : 7;

        Assert.Equal(expected, CanAdopt(generation, selectedOrderId, selectedPlanId, activeTab,
            disposed: scenario == CurrentRequestScenario.Disposed));
    }

    [Fact]
    public void WorkerChangeBeforeAdoptionInvalidatesTheRequest()
        => Assert.False(CanAdopt(workerRevision: 13));

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 13)]
    public void ExplicitLoaderRequestRejectsSelectionOrWorkerChange(bool selectionStillMatches, long workerRevision) =>
        Assert.False(CanAdopt(selectedOrderId: selectionStillMatches ? OrderA : OrderB,
            activeTab: 2, planTab: 2, workerRevision: workerRevision));

    private static bool CanAdopt(long generation = 7, Guid? selectedOrderId = null,
        string selectedPlanId = "plan-a", int activeTab = 0, int planTab = 0,
        bool disposed = false, long workerRevision = 12) =>
        TradeOrderPlanRestorePolicy.CanAdoptExactPlan(new(7, OrderA, "plan-a", 12), generation,
            selectedOrderId ?? OrderA, selectedPlanId, activeTab, planTab, disposed, workerRevision);

    [Theory]
    [InlineData(true, ProfileSyncStage.Inactive, false, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead)]
    [InlineData(true, ProfileSyncStage.ApplyingChanges, true, 1, TradeOrderPlanMissingDisposition.WaitForHostedPlan)]
    [InlineData(true, ProfileSyncStage.Ready, true, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead)]
    [InlineData(true, ProfileSyncStage.Failed, true, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead)]
    [InlineData(false, ProfileSyncStage.Inactive, false, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead)]
    [InlineData(true, ProfileSyncStage.Ready, true, 3, TradeOrderPlanMissingDisposition.ExactPlanUnavailable)]
    public void MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
        bool waitsForProfilePlanAuthority,
        ProfileSyncStage stage,
        bool isConnected,
        int attempt,
        TradeOrderPlanMissingDisposition expected)
    {
        Assert.Equal(
            expected,
            TradeOrderPlanRestorePolicy.ResolveMissingExactPlan(
                waitsForProfilePlanAuthority,
                Status(stage, isConnected),
                attempt));
    }

    [Fact]
    public async Task ExactReadAdoptsPayloadThatArrivesOnThirdAttempt()
    {
        var attempts = 0;
        var payload = new object();

        var result = await ReadExactAsync(_ => Task.FromResult(++attempts == 3 ? payload : null));

        Assert.Equal(TradeOrderPlanReadOutcome.Loaded, result.Outcome);
        Assert.Same(payload, result.Payload);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task TransientReadExceptionsAreBoundedAndCanRecover()
    {
        var attempts = 0;
        var payload = new object();

        var result = await ReadExactAsync<object>(
            _ => ++attempts < 3
                ? Task.FromException<object?>(new InvalidOperationException("IndexedDB opening"))
                : Task.FromResult<object?>(payload));

        Assert.Equal(TradeOrderPlanReadOutcome.Loaded, result.Outcome);
        Assert.Same(payload, result.Payload);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TransientReadExceptionsStopAfterThreeAttempts()
    {
        var attempts = 0;

        var result = await ReadExactAsync<object>(
            _ =>
            {
                attempts++;
                return Task.FromException<object?>(
                    new InvalidOperationException($"IndexedDB attempt {attempts}"));
            });

        Assert.Equal(TradeOrderPlanReadOutcome.ExactPlanUnavailable, result.Outcome);
        Assert.Null(result.Payload);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, attempts);
        Assert.Equal("IndexedDB attempt 3", result.LastException?.Message);
    }

    [Fact]
    public async Task MissingHostedPlanWaitsForNextSyncStatusWithoutPolling()
    {
        var attempts = 0;

        var result = await ReadExactAsync<object>(
            _ =>
            {
                attempts++;
                return Task.FromResult<object?>(null);
            },
            () => Status(ProfileSyncStage.ApplyingChanges, isConnected: true));

        Assert.Equal(TradeOrderPlanReadOutcome.WaitForHostedPlan, result.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExactReadStopsAsSoonAsItsRequestIsSupersededDuringAnAwait()
    {
        var attempts = 0;
        var requestIsCurrent = true;

        var result = await ReadExactAsync<object>(
            _ =>
            {
                attempts++;
                requestIsCurrent = false;
                return Task.FromResult<object?>(null);
            },
            canContinue: () => requestIsCurrent);

        Assert.Equal(TradeOrderPlanReadOutcome.RequestSuperseded, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, attempts);
    }

    private static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private static Task<TradeOrderPlanReadResult<T>> ReadExactAsync<T>(Func<CancellationToken, Task<T?>> load,
        Func<ProfileSyncStatus>? status = null, Func<bool>? canContinue = null) where T : class =>
        TradeOrderPlanRestorePolicy.ReadExactPlanAsync(load, status ?? ReadyStatus, true,
            delay: NoDelay, canContinue: canContinue);

    private static ProfileSyncStatus ReadyStatus() =>
        Status(ProfileSyncStage.Ready, isConnected: true);

    private static ProfileSyncStatus Status(ProfileSyncStage stage, bool isConnected) =>
        new(isConnected, isConnected, 10, 0, 0, DateTime.UtcNow, "fixture") { Stage = stage };

    public enum CurrentRequestScenario
    {
        Current,
        SelectionChanged,
        PlanChanged,
        TabChanged,
        Disposed,
        NewerRequest
    }
}
