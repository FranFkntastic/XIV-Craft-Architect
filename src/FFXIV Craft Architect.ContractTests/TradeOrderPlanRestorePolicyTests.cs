using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

internal static class TradeOrderPlanRestoreContractScenarios
{
    private static readonly Guid OrderA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task AssertAllAsync()
    {
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.Current, true);
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.SelectionChanged, false);
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.PlanChanged, false);
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.TabChanged, false);
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.Disposed, false);
        AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario.NewerRequest, false);
        WorkerChangeBeforeAdoptionInvalidatesTheRequest();
        ExplicitLoaderRequestRejectsSelectionOrWorkerChange(
            selectionStillMatches: false,
            workerRevision: 12);
        ExplicitLoaderRequestRejectsSelectionOrWorkerChange(
            selectionStillMatches: true,
            workerRevision: 13);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            true, ProfileSyncStage.Inactive, false, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            true, ProfileSyncStage.ApplyingChanges, true, 1, TradeOrderPlanMissingDisposition.WaitForHostedPlan);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            true, ProfileSyncStage.Ready, true, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            true, ProfileSyncStage.Failed, true, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            false, ProfileSyncStage.Inactive, false, 1, TradeOrderPlanMissingDisposition.RetryExactPlanRead);
        MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
            true, ProfileSyncStage.Ready, true, 3, TradeOrderPlanMissingDisposition.ExactPlanUnavailable);
        await ExactReadAdoptsPayloadThatArrivesOnThirdAttempt();
        await TransientReadExceptionsAreBoundedAndCanRecover();
        await TransientReadExceptionsStopAfterThreeAttempts();
        await MissingHostedPlanWaitsForNextSyncStatusWithoutPolling();
        await ExactReadStopsAsSoonAsItsRequestIsSupersededDuringAnAwait();
    }

    private static void AdoptionRequiresTheOriginalSelectionAndPlanIntent(CurrentRequestScenario scenario, bool expected)
    {
        var selectedOrderId = scenario == CurrentRequestScenario.SelectionChanged ? OrderB : OrderA;
        var selectedPlanId = scenario == CurrentRequestScenario.PlanChanged ? "plan-b" : "plan-a";
        var activeTab = scenario == CurrentRequestScenario.TabChanged ? 1 : 0;
        var generation = scenario == CurrentRequestScenario.NewerRequest ? 8 : 7;

        Assert.Equal(expected, CanAdopt(generation, selectedOrderId, selectedPlanId, activeTab,
            disposed: scenario == CurrentRequestScenario.Disposed));
    }

    private static void WorkerChangeBeforeAdoptionInvalidatesTheRequest()
        => Assert.False(CanAdopt(workerRevision: 13));

    private static void ExplicitLoaderRequestRejectsSelectionOrWorkerChange(bool selectionStillMatches, long workerRevision) =>
        Assert.False(CanAdopt(selectedOrderId: selectionStillMatches ? OrderA : OrderB,
            activeTab: 2, planTab: 2, workerRevision: workerRevision));

    private static bool CanAdopt(long generation = 7, Guid? selectedOrderId = null,
        string selectedPlanId = "plan-a", int activeTab = 0, int planTab = 0,
        bool disposed = false, long workerRevision = 12) =>
        TradeOrderPlanRestorePolicy.CanAdoptExactPlan(new(7, OrderA, "plan-a", 12), generation,
            selectedOrderId ?? OrderA, selectedPlanId, activeTab, planTab, disposed, workerRevision);

    private static void MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
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

    private static async Task ExactReadAdoptsPayloadThatArrivesOnThirdAttempt()
    {
        var attempts = 0;
        var payload = new object();

        var result = await ReadExactAsync(_ => Task.FromResult(++attempts == 3 ? payload : null));

        Assert.Equal(TradeOrderPlanReadOutcome.Loaded, result.Outcome);
        Assert.Same(payload, result.Payload);
        Assert.Equal(3, result.Attempts);
    }

    private static async Task TransientReadExceptionsAreBoundedAndCanRecover()
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

    private static async Task TransientReadExceptionsStopAfterThreeAttempts()
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

    private static async Task MissingHostedPlanWaitsForNextSyncStatusWithoutPolling()
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

    private static async Task ExactReadStopsAsSoonAsItsRequestIsSupersededDuringAnAwait()
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

    private enum CurrentRequestScenario
    {
        Current,
        SelectionChanged,
        PlanChanged,
        TabChanged,
        Disposed,
        NewerRequest
    }
}
