using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class RecipeBuildDiagnosticServiceTests
{
    [Fact]
    public void Constructor_WhenDefaultTimeoutNotProvided_UsesFiveMinuteWatchdog()
    {
        var service = new RecipeBuildDiagnosticService(
            new AppState(),
            new FakeDiagnosticCommandRunner());

        var timeoutField = typeof(RecipeBuildDiagnosticService)
            .GetField("_defaultTimeout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(timeoutField);
        Assert.Equal(TimeSpan.FromSeconds(300), timeoutField.GetValue(service));
    }

    [Fact]
    public void Serialize_UsesCamelCaseIndentedJson()
    {
        var dump = RecipeBuildDiagnosticService.CreateNoProjectItemsDump(
            new RecipeBuildDiagnosticContext(
                "Plan/Test",
                "Aether",
                "North America",
                MarketFetchScope.SelectedDataCenter,
                7,
                0,
                []),
            exportedAtUtc: new DateTime(2026, 6, 8, 18, 30, 12, DateTimeKind.Utc),
            exportedAtLocal: new DateTimeOffset(2026, 6, 8, 14, 30, 12, TimeSpan.FromHours(-4)));

        var json = RecipeBuildDiagnosticService.Serialize(dump);

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"tool\": \"recipe-build-diagnostic-dump\"", json);
        Assert.Contains("\"exportedAtLocal\":", json);
        Assert.Contains("02:30:12", json);
        Assert.Contains("PM", json);
        Assert.Contains("-04", json);
        Assert.Contains("\"status\": \"NoProjectItems\"", json);
    }

    [Fact]
    public void CreateFileName_RemovesInvalidCharactersAndIncludesTimestamp()
    {
        var exportedAt = new DateTime(2026, 6, 8, 12, 34, 56, DateTimeKind.Utc);
        var fileName = RecipeBuildDiagnosticService.CreateFileName("Plan: Bad/Name", exportedAt);

        Assert.Equal("recipe-build-Plan_ Bad_Name-20260608-123456.json", fileName);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenBuildSucceeds_ReturnsSucceededDump()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Result = new BuildRecipePlanResult(
                true,
                new PlanNode { ItemId = 5338, Name = "Test Item", Quantity = 1 },
                CoreMarketPriceAvailability.Empty,
                3,
                "Plan built!",
                RecipePlannerCommandMessageLevel.Success)
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromSeconds(5));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.Succeeded, dump.Status);
        Assert.Contains(dump.Phases, phase => phase.Name == "recipe-build-command" &&
                                              phase.Status == RecipeBuildDiagnosticPhaseStatus.Completed);
        Assert.NotNull(dump.Result);
        Assert.True(dump.Result.Built);
        Assert.Equal(3, dump.Result.ChangedDefaultDecisions);
        Assert.Null(dump.Failure);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_UsesExistingBuildPlanCommandAndDoesNotRequireCompactStorage()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Result = new BuildRecipePlanResult(
                true,
                new PlanNode { ItemId = 5338, Name = "Test Item", Quantity = 1 },
                CoreMarketPriceAvailability.Empty,
                0,
                "Plan built!",
                RecipePlannerCommandMessageLevel.Success)
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromSeconds(5));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.Succeeded, dump.Status);
        Assert.Equal(1, runner.BuildCallCount);
        Assert.DoesNotContain(
            dump.Phases,
            phase => phase.Name.Contains("market-intelligence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenBuildThrows_ReturnsFailedDump()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Exception = new ArgumentNullException("s", "ArgumentNull_Generic")
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromSeconds(5));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.Failed, dump.Status);
        Assert.Equal("ArgumentNullException", dump.Failure?.ExceptionType);
        Assert.Contains("s", dump.Failure?.Message);
        Assert.Contains(dump.Phases, phase => phase.Name == "recipe-build-command" &&
                                              phase.Status == RecipeBuildDiagnosticPhaseStatus.Failed);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenBuildDoesNotFinishBeforeTimeout_ReturnsTimedOutDump()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Delay = TimeSpan.FromSeconds(10)
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromMilliseconds(25));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.TimedOut, dump.Status);
        Assert.Equal("recipe-build-command", dump.Failure?.Phase);
        Assert.Contains(dump.Phases, phase => phase.Name == "recipe-build-command" &&
                                              phase.Status == RecipeBuildDiagnosticPhaseStatus.TimedOut);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenCommandSwallowsTimeoutCancellation_ReturnsTimedOutDumpWithResult()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Delay = TimeSpan.FromSeconds(10),
            SwallowCancellationAfterDelay = true,
            Result = new BuildRecipePlanResult(
                true,
                new PlanNode { ItemId = 5338, Name = "Test Item", Quantity = 1 },
                CoreMarketPriceAvailability.Empty,
                0,
                "Plan built; follow-up work canceled before all refresh/analysis steps completed.",
                RecipePlannerCommandMessageLevel.Info)
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromMilliseconds(25));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.TimedOut, dump.Status);
        Assert.Equal("recipe-build-command", dump.Failure?.Phase);
        Assert.Equal("CompletedAfterWatchdogTimeout", dump.Failure?.FailureType);
        Assert.Equal(
            "Diagnostic watchdog timed out during 'recipe-build-command'. The plan graph was built, but follow-up work was canceled before the command returned.",
            dump.Failure?.Message);
        Assert.NotNull(dump.Result);
        Assert.True(dump.Result.Built);
        Assert.Contains("follow-up work canceled", dump.Result.Message);
        Assert.Contains(dump.Phases, phase => phase.Name == "recipe-build-command" &&
                                              phase.Status == RecipeBuildDiagnosticPhaseStatus.TimedOut);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenCommandReturnsCanceledAfterWatchdog_DoesNotSayPlanGraphWasBuilt()
    {
        var appState = CreateAppStateWithProjectItem();
        var runner = new FakeDiagnosticCommandRunner
        {
            Delay = TimeSpan.FromSeconds(10),
            SwallowCancellationAfterDelay = true,
            Result = new BuildRecipePlanResult(
                false,
                null,
                CoreMarketPriceAvailability.Empty,
                0,
                "Plan build canceled.",
                RecipePlannerCommandMessageLevel.Info)
        };
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromMilliseconds(25));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.TimedOut, dump.Status);
        Assert.False(dump.Result?.Built);
        Assert.Equal("TimedOut", dump.Failure?.FailureType);
        Assert.DoesNotContain("plan graph was built", dump.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before the plan graph finished building", dump.Failure?.Message);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_WhenNoProjectItems_ReturnsNoProjectItemsDump()
    {
        var appState = new AppState();
        var runner = new FakeDiagnosticCommandRunner();
        var service = new RecipeBuildDiagnosticService(appState, runner, TimeSpan.FromSeconds(5));

        var dump = await service.BuildWithDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(RecipeBuildDiagnosticStatus.NoProjectItems, dump.Status);
        Assert.Equal(0, runner.BuildCallCount);
        Assert.Equal("request-validation", dump.Failure?.Phase);
    }

    private static AppState CreateAppStateWithProjectItem()
    {
        var appState = new AppState();
        appState.ApplyImportedProjectItems(
        [
            new ProjectItem
            {
                Id = 5338,
                Name = "Test Item",
                Quantity = 2,
                MustBeHq = true
            }
        ]);
        return appState;
    }

    private sealed class FakeDiagnosticCommandRunner : IRecipeBuildDiagnosticCommandRunner
    {
        public BuildRecipePlanResult Result { get; init; } = new(
            true,
            new PlanNode { ItemId = 5338, Name = "Test Item", Quantity = 1 },
            CoreMarketPriceAvailability.Empty,
            0,
            "Plan built!",
            RecipePlannerCommandMessageLevel.Success);

        public Exception? Exception { get; init; }
        public TimeSpan Delay { get; init; }
        public bool SwallowCancellationAfterDelay { get; init; }
        public int BuildCallCount { get; private set; }

        public async Task<BuildRecipePlanResult> BuildPlanAsync(
            BuildRecipePlanRequest request,
            CancellationToken cancellationToken)
        {
            BuildCallCount++;
            if (request.Diagnostics != null)
            {
                try
                {
                    return await request.Diagnostics.RunPhaseAsync(
                        "recipe-build-command",
                        async ct =>
                        {
                            await DelayIfRequestedAsync(ct);
                            ThrowIfRequested();
                            return Result;
                        },
                        cancellationToken);
                }
                catch (OperationCanceledException) when (SwallowCancellationAfterDelay)
                {
                    return Result;
                }
            }

            await DelayIfRequestedAsync(cancellationToken);
            ThrowIfRequested();
            return Result;

            async Task DelayIfRequestedAsync(CancellationToken ct)
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }
            }

            void ThrowIfRequested()
            {
                if (Exception != null)
                {
                    throw Exception;
                }
            }
        }
    }
}
