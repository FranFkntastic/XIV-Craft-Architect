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
        MissingGeneratedPlanHasNoAutomaticRebuildPath();
        PlanPanePreservesAnUnnamedActiveWorkerPlanBeforeAnyAdoption();
        ExplicitPlanOpenUsesFencedAdoptionAndPreservesUnnamedPlan();
    }

    private static void AdoptionRequiresTheOriginalSelectionAndPlanIntent(
        CurrentRequestScenario scenario,
        bool expected)
    {
        var request = new TradeOrderPlanRestoreRequest(7, OrderA, "plan-a", 12);
        var selectedOrderId = scenario == CurrentRequestScenario.SelectionChanged
            ? OrderB
            : OrderA;
        var selectedPlanId = scenario == CurrentRequestScenario.PlanChanged
            ? "plan-b"
            : "plan-a";
        var activeTab = scenario == CurrentRequestScenario.TabChanged ? 1 : 0;
        var generation = scenario == CurrentRequestScenario.NewerRequest ? 8 : 7;

        Assert.Equal(
            expected,
            TradeOrderPlanRestorePolicy.CanAdoptExactPlan(
                request,
                generation,
                selectedOrderId,
                selectedPlanId,
                activeTab,
                planTab: 0,
                disposed: scenario == CurrentRequestScenario.Disposed,
                currentWorkerRevision: 12));
    }

    private static void WorkerChangeBeforeAdoptionInvalidatesTheRequest()
    {
        var request = new TradeOrderPlanRestoreRequest(7, OrderA, "plan-a", 12);

        Assert.False(TradeOrderPlanRestorePolicy.CanAdoptExactPlan(
            request,
            currentGeneration: 7,
            selectedOrderId: OrderA,
            selectedPlanId: "plan-a",
            activeTab: 0,
            planTab: 0,
            disposed: false,
            currentWorkerRevision: 13));
    }

    private static void ExplicitLoaderRequestRejectsSelectionOrWorkerChange(
        bool selectionStillMatches,
        long workerRevision)
    {
        var request = new TradeOrderPlanRestoreRequest(7, OrderA, "plan-a", 12);

        Assert.False(TradeOrderPlanRestorePolicy.CanAdoptExactPlan(
            request,
            currentGeneration: 7,
            selectedOrderId: selectionStillMatches ? OrderA : OrderB,
            selectedPlanId: "plan-a",
            activeTab: 2,
            planTab: 2,
            disposed: false,
            currentWorkerRevision: workerRevision));
    }

    private static void MissingPlanOnlyWaitsForAuthorityOrRetriesTheExactSavedObject(
        bool waitsForProfilePlanAuthority,
        ProfileSyncStage stage,
        bool isConnected,
        int attempt,
        TradeOrderPlanMissingDisposition expected)
    {
        var status = new ProfileSyncStatus(
            isConnected,
            HostReachable: isConnected,
            LastSyncRevision: 10,
            PendingCount: 0,
            ConflictCount: 0,
            LastSyncedAtUtc: DateTime.UtcNow,
            Message: "fixture")
        {
            Stage = stage
        };

        Assert.Equal(
            expected,
            TradeOrderPlanRestorePolicy.ResolveMissingExactPlan(
                waitsForProfilePlanAuthority,
                status,
                attempt));
    }

    private static async Task ExactReadAdoptsPayloadThatArrivesOnThirdAttempt()
    {
        var attempts = 0;
        var payload = new object();

        var result = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync(
            _ => Task.FromResult(++attempts == 3 ? payload : null),
            ReadyStatus,
            waitsForProfilePlanAuthority: true,
            delay: NoDelay);

        Assert.Equal(TradeOrderPlanReadOutcome.Loaded, result.Outcome);
        Assert.Same(payload, result.Payload);
        Assert.Equal(3, result.Attempts);
    }

    private static async Task TransientReadExceptionsAreBoundedAndCanRecover()
    {
        var attempts = 0;
        var payload = new object();

        var result = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync<object>(
            _ => ++attempts < 3
                ? Task.FromException<object?>(new InvalidOperationException("IndexedDB opening"))
                : Task.FromResult<object?>(payload),
            ReadyStatus,
            waitsForProfilePlanAuthority: true,
            delay: NoDelay);

        Assert.Equal(TradeOrderPlanReadOutcome.Loaded, result.Outcome);
        Assert.Same(payload, result.Payload);
        Assert.Equal(3, attempts);
    }

    private static async Task TransientReadExceptionsStopAfterThreeAttempts()
    {
        var attempts = 0;

        var result = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync<object>(
            _ =>
            {
                attempts++;
                return Task.FromException<object?>(
                    new InvalidOperationException($"IndexedDB attempt {attempts}"));
            },
            ReadyStatus,
            waitsForProfilePlanAuthority: true,
            delay: NoDelay);

        Assert.Equal(TradeOrderPlanReadOutcome.ExactPlanUnavailable, result.Outcome);
        Assert.Null(result.Payload);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, attempts);
        Assert.Equal("IndexedDB attempt 3", result.LastException?.Message);
    }

    private static async Task MissingHostedPlanWaitsForNextSyncStatusWithoutPolling()
    {
        var attempts = 0;

        var result = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync<object>(
            _ =>
            {
                attempts++;
                return Task.FromResult<object?>(null);
            },
            () => Status(ProfileSyncStage.ApplyingChanges, isConnected: true),
            waitsForProfilePlanAuthority: true,
            delay: NoDelay);

        Assert.Equal(TradeOrderPlanReadOutcome.WaitForHostedPlan, result.Outcome);
        Assert.Equal(1, attempts);
    }

    private static async Task ExactReadStopsAsSoonAsItsRequestIsSupersededDuringAnAwait()
    {
        var attempts = 0;
        var requestIsCurrent = true;

        var result = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync<object>(
            _ =>
            {
                attempts++;
                requestIsCurrent = false;
                return Task.FromResult<object?>(null);
            },
            ReadyStatus,
            waitsForProfilePlanAuthority: true,
            delay: NoDelay,
            canContinue: () => requestIsCurrent);

        Assert.Equal(TradeOrderPlanReadOutcome.RequestSuperseded, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, attempts);
    }

    private static void MissingGeneratedPlanHasNoAutomaticRebuildPath()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var webRoot = Path.Combine(repositoryRoot, "src", "FFXIV Craft Architect.Web");
        var pricing = File.ReadAllText(Path.Combine(
            webRoot,
            "Services",
            "TradeOrderPricingWorkflowService.cs"))
            .ReplaceLineEndings("\n");
        var craftPlan = File.ReadAllText(Path.Combine(
            webRoot,
            "Pages",
            "TradeOrders.CraftPlan.cs"));

        Assert.DoesNotContain("RebuildPlanCacheAsync", pricing, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadOrRebuildOrderPlanAsync", craftPlan, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "return await RebuildAndPriceAsync(order, options, ct)",
            pricing,
            StringComparison.Ordinal);
    }

    private static void PlanPanePreservesAnUnnamedActiveWorkerPlanBeforeAnyAdoption()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var procurement = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.Procurement.cs"));

        var preservation = procurement.IndexOf(
            "string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)",
            StringComparison.Ordinal);
        var exactRead = procurement.IndexOf(
            "ReadExactPlanAsync",
            StringComparison.Ordinal);
        var adoption = procurement.IndexOf(
            "ReplaceStoredPlanAsync",
            StringComparison.Ordinal);

        Assert.True(preservation >= 0);
        Assert.True(exactRead > preservation);
        Assert.True(adoption > exactRead);
    }

    private static void ExplicitPlanOpenUsesFencedAdoptionAndPreservesUnnamedPlan()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var craftPlan = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.CraftPlan.cs"));
        var procurement = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Pages",
            "TradeOrders.Procurement.cs"));
        var start = craftPlan.IndexOf(
            "private async Task<bool> LoadExactOrderPlanAsync",
            StringComparison.Ordinal);
        var end = craftPlan.IndexOf(
            "private string GetOrderDataCenter",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var loader = craftPlan[start..end];

        var preserveUnnamed = loader.IndexOf(
            "string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)",
            StringComparison.Ordinal);
        var confirm = loader.IndexOf(
            "ConfirmActiveCraftPlanCanBeReplacedAsync",
            StringComparison.Ordinal);
        var exactRead = loader.IndexOf("ReadExactPlanAsync", StringComparison.Ordinal);
        var replace = loader.IndexOf("ReplaceStoredPlanAsync", StringComparison.Ordinal);

        Assert.Contains("TradeOrderPlanRestoreRequest", loader, StringComparison.Ordinal);
        Assert.Contains("CanAdoptCurrentPlanRequest", loader, StringComparison.Ordinal);
        Assert.Contains("canContinue:", loader, StringComparison.Ordinal);
        Assert.Contains("cancellationToken:", loader, StringComparison.Ordinal);
        Assert.True(preserveUnnamed >= 0);
        Assert.True(confirm > preserveUnnamed);
        Assert.True(exactRead > confirm);
        Assert.True(replace > exactRead);
        Assert.Contains("tabIndex != _activeOpsTab", procurement, StringComparison.Ordinal);

        var confirmStart = craftPlan.IndexOf(
            "private async Task<bool> ConfirmActiveCraftPlanCanBeReplacedAsync",
            StringComparison.Ordinal);
        var confirmEnd = craftPlan.IndexOf(
            "private async Task<bool> SaveActiveCraftPlanBeforeTradeActionAsync",
            confirmStart,
            StringComparison.Ordinal);
        Assert.True(confirmStart >= 0 && confirmEnd > confirmStart);
        Assert.DoesNotContain(
            "string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)",
            craftPlan[confirmStart..confirmEnd],
            StringComparison.Ordinal);
    }

    private static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static ProfileSyncStatus ReadyStatus() =>
        Status(ProfileSyncStage.Ready, isConnected: true);

    private static ProfileSyncStatus Status(ProfileSyncStage stage, bool isConnected) =>
        new(
            isConnected,
            HostReachable: isConnected,
            LastSyncRevision: 10,
            PendingCount: 0,
            ConflictCount: 0,
            LastSyncedAtUtc: DateTime.UtcNow,
            Message: "fixture")
        {
            Stage = stage
        };

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test output path.");
    }

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
