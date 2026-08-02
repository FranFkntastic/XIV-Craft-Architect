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

    private static void MissingGeneratedPlanHasNoAutomaticRebuildPath()
    {
        var pricing = ReadWebSource("Services", "TradeOrderPricingWorkflowService.cs").ReplaceLineEndings("\n");
        var craftPlan = ReadWebSource("Pages", "TradeOrders.CraftPlan.cs");
        var procurement = ReadWebSource("Pages", "TradeOrders.Procurement.cs");
        var termsConflict = ReadWebSource("Pages", "TradeOrders.TermsRevisionConflict.cs");

        Assert.DoesNotContain("RebuildPlanCacheAsync", pricing, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadOrRebuildOrderPlanAsync", craftPlan, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "return await RebuildAndPriceAsync(order, options, ct)",
            pricing,
            StringComparison.Ordinal);
        AssertContainsAll(termsConflict, "ReadExactPlanAsync", "PlanPersistence.LoadPlanPayloadAsync(planId)",
            "canContinue: CanContinue", "read.Payload.Id", "read.Payload.SavedAt", "CraftPlanSavedAtUtc");
        AssertDoesNotContainAny(termsConflict, "RebuildAndPriceAsync", "RepriceAsync");
        Assert.True(procurement.IndexOf("RepriceActivePlanAsync", StringComparison.Ordinal) <
            procurement.IndexOf("ExportStoredPlanAsync", procurement.IndexOf("RepriceActivePlanAsync", StringComparison.Ordinal), StringComparison.Ordinal));
        AssertContainsAll(procurement, "orderToSave.CraftPlanSavedAtUtc = stored.SavedAt", "SaveSnapshotAsync(stored)");
        var exactSave = procurement.IndexOf("SaveSnapshotAsync(stored)", StringComparison.Ordinal);
        Assert.True(exactSave < procurement.IndexOf("UpdateCanonicalDraftAsync", exactSave, StringComparison.Ordinal));
        var rebase = termsConflict[..termsConflict.IndexOf(
            "private Task DiscardConflictedCommissionTermsRevisionAsync()",
            StringComparison.Ordinal)];
        AssertContainsAll(rebase, "ReadLatestCanonicalPlanAsync", "_commissionTermsRevisionRollbackPlan = latestBaseline");
        AssertDoesNotContainAny(rebase, "WorkerSession", "RestoreStagedProcurementPlanAsync");
    }

    private static void PlanPanePreservesAnUnnamedActiveWorkerPlanBeforeAnyAdoption()
    {
        var procurement = ReadWebSource("Pages", "TradeOrders.Procurement.cs");

        AssertInOrder(procurement, "string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)",
            "ReadExactPlanAsync", "ReplaceStoredPlanAsync");
    }

    private static void ExplicitPlanOpenUsesFencedAdoptionAndPreservesUnnamedPlan()
    {
        var craftPlan = ReadWebSource("Pages", "TradeOrders.CraftPlan.cs");
        var procurement = ReadWebSource("Pages", "TradeOrders.Procurement.cs");
        var loader = Slice(craftPlan, "private async Task<bool> LoadExactOrderPlanAsync", "private string GetOrderDataCenter");
        AssertContainsAll(loader, "TradeOrderPlanRestoreRequest", "CanAdoptCurrentPlanRequest", "canContinue:", "cancellationToken:");
        AssertInOrder(loader, "string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)",
            "ConfirmActiveCraftPlanCanBeReplacedAsync", "ReadExactPlanAsync", "ReplaceStoredPlanAsync");
        Assert.Contains("tabIndex != _activeOpsTab", procurement, StringComparison.Ordinal);

        var confirmation = Slice(craftPlan, "private async Task<bool> ConfirmActiveCraftPlanCanBeReplacedAsync",
            "private async Task<bool> SaveActiveCraftPlanBeforeTradeActionAsync");
        Assert.DoesNotContain("string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId)", confirmation, StringComparison.Ordinal);
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

    private static string ReadWebSource(params string[] path) =>
        File.ReadAllText(Path.Combine([LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web", .. path]));

    private static void AssertContainsAll(string source, params string[] values)
    {
        foreach (var value in values)
        {
            Assert.Contains(value, source, StringComparison.Ordinal);
        }
    }

    private static void AssertDoesNotContainAny(string source, params string[] values)
    {
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, source, StringComparison.Ordinal);
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after index {previous}.");
            previous = current;
        }
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
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
