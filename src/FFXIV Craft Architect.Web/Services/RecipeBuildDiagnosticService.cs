using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IRecipeBuildDiagnosticCommandRunner
{
    Task<BuildRecipePlanResult> BuildPlanAsync(
        BuildRecipePlanRequest request,
        CancellationToken cancellationToken);
}

public interface IRecipeBuildDiagnosticRecorder : IRecipePlanBuildDiagnosticRecorder;

public sealed class RecipePlannerDiagnosticCommandRunner : IRecipeBuildDiagnosticCommandRunner
{
    private readonly RecipePlannerCommandService _commandService;

    public RecipePlannerDiagnosticCommandRunner(RecipePlannerCommandService commandService)
    {
        _commandService = commandService;
    }

    public Task<BuildRecipePlanResult> BuildPlanAsync(
        BuildRecipePlanRequest request,
        CancellationToken cancellationToken)
    {
        return _commandService.BuildPlanAsync(request, cancellationToken);
    }
}

public sealed partial class RecipeBuildDiagnosticService
{
    public const int SchemaVersion = 1;
    public const string ToolName = "recipe-build-diagnostic-dump";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppState _appState;
    private readonly IRecipeBuildDiagnosticCommandRunner _commandRunner;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _publicationGraceTimeout;
    private readonly TimeSpan _postBuildTimeout;

    static RecipeBuildDiagnosticService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public RecipeBuildDiagnosticService(
        AppState appState,
        IRecipeBuildDiagnosticCommandRunner commandRunner,
        TimeSpan? defaultTimeout = null,
        TimeSpan? publicationGraceTimeout = null,
        TimeSpan? postBuildTimeout = null)
    {
        _appState = appState;
        _commandRunner = commandRunner;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(300);
        _publicationGraceTimeout = publicationGraceTimeout ?? TimeSpan.FromSeconds(120);
        _postBuildTimeout = postBuildTimeout ?? TimeSpan.FromSeconds(300);
    }

    public async Task<RecipeBuildDiagnosticDump> BuildWithDiagnosticsAsync(
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var context = CreateContext();
        if (context.ProjectItemCount == 0)
        {
            return CreateNoProjectItemsDump(context);
        }

        var phases = new List<RecipeBuildDiagnosticPhase>();
        var exportedAtUtc = DateTime.UtcNow;
        var exportedAtLocal = DateTimeOffset.Now;

        var timeoutBudget = timeout ?? _defaultTimeout;
        using var timeoutCts = new CancellationTokenSource(timeoutBudget);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var recorder = new RecipeBuildDiagnosticRecorder(phases, timeoutCts, _publicationGraceTimeout, _postBuildTimeout);

        try
        {
            var result = await _commandRunner.BuildPlanAsync(
                new BuildRecipePlanRequest(
                    _appState.ProjectItems,
                    _appState.SelectedDataCenter,
                    _appState.SelectedRegion,
                    _appState.DefaultMarketFetchScope,
                    recorder),
                linkedCts.Token);
            if (timeoutCts.IsCancellationRequested)
            {
                var failurePhase = recorder.LastTimedOutPhaseName ?? recorder.LastPhaseName ?? "recipe-build-command";
                var failureType = result.Built
                    ? "CompletedAfterWatchdogTimeout"
                    : "TimedOut";
                var failureMessage = result.Built
                    ? CreateCompletedAfterWatchdogMessage(failurePhase)
                    : CreateTimedOutBeforeBuildFinishedMessage(failurePhase);

                return new RecipeBuildDiagnosticDump(
                    SchemaVersion,
                    ToolName,
                    DateTime.UtcNow,
                    FormatLocalTimestamp(DateTimeOffset.Now),
                    RecipeBuildDiagnosticStatus.TimedOut,
                    context,
                    phases,
                    CreateResult(result),
                    new RecipeBuildDiagnosticFailure(
                        failurePhase,
                        failureType,
                        failureMessage,
                        null,
                        null));
            }

            return new RecipeBuildDiagnosticDump(
                SchemaVersion,
                ToolName,
                exportedAtUtc,
                FormatLocalTimestamp(exportedAtLocal),
                result.Built ? RecipeBuildDiagnosticStatus.Succeeded : RecipeBuildDiagnosticStatus.Failed,
                context,
                phases,
                CreateResult(result),
                result.Built
                    ? null
                    : new RecipeBuildDiagnosticFailure(
                        "recipe-build-command",
                        "BuildReturnedFalse",
                        result.Message,
                        null,
                        null));
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested &&
                                                   !cancellationToken.IsCancellationRequested)
        {
            return CreateFailureDump(
                context,
                phases,
                RecipeBuildDiagnosticStatus.TimedOut,
                RecipeBuildDiagnosticPhaseStatus.TimedOut,
                "TimedOut",
                $"Recipe build diagnostic watchdog timed out after {timeoutBudget.TotalSeconds:0.#} seconds.",
                ex);
        }
        catch (OperationCanceledException ex)
        {
            return CreateFailureDump(
                context,
                phases,
                RecipeBuildDiagnosticStatus.Canceled,
                RecipeBuildDiagnosticPhaseStatus.Canceled,
                "Canceled",
                "Recipe build diagnostic run was canceled.",
                ex);
        }
        catch (Exception ex)
        {
            return CreateFailureDump(
                context,
                phases,
                RecipeBuildDiagnosticStatus.Failed,
                RecipeBuildDiagnosticPhaseStatus.Failed,
                ex.GetType().Name,
                ex.Message,
                ex);
        }
    }

    public static RecipeBuildDiagnosticDump CreateNoProjectItemsDump(
        RecipeBuildDiagnosticContext context,
        DateTime? exportedAtUtc = null,
        DateTimeOffset? exportedAtLocal = null)
    {
        return new RecipeBuildDiagnosticDump(
            SchemaVersion,
            ToolName,
            exportedAtUtc ?? DateTime.UtcNow,
            FormatLocalTimestamp(exportedAtLocal ?? DateTimeOffset.Now),
            RecipeBuildDiagnosticStatus.NoProjectItems,
            context,
            [],
            null,
            new RecipeBuildDiagnosticFailure(
                "request-validation",
                "NoProjectItems",
                "No project items to build.",
                null,
                null));
    }

    public static string Serialize(RecipeBuildDiagnosticDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    public static string CreateFileName(string? planName, DateTime exportedAtUtc)
    {
        var safeName = string.IsNullOrWhiteSpace(planName) ? "recipe-plan" : planName.Trim();
        safeName = InvalidFileNameCharacterPattern().Replace(safeName, "_");
        if (safeName.Length > 48)
        {
            safeName = safeName[..48];
        }

        return $"recipe-build-{safeName}-{exportedAtUtc:yyyyMMdd-HHmmss}.json";
    }

    public static string FormatLocalTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("yyyy-MM-dd hh:mm:ss tt zzz", CultureInfo.InvariantCulture);
    }

    private RecipeBuildDiagnosticContext CreateContext()
    {
        var projectItems = _appState.ProjectItems
            .Select(item => new RecipeBuildDiagnosticProjectItem(
                item.Id,
                item.Name,
                item.Quantity,
                item.MustBeHq))
            .ToArray();

        return new RecipeBuildDiagnosticContext(
            _appState.CurrentPlanName ?? _appState.CurrentPlan?.Name,
            _appState.SelectedDataCenter,
            _appState.SelectedRegion,
            _appState.DefaultMarketFetchScope,
            _appState.PlanSessionVersion,
            projectItems.Length,
            projectItems);
    }

    private static RecipeBuildDiagnosticResult CreateResult(BuildRecipePlanResult result)
    {
        return new RecipeBuildDiagnosticResult(
            result.Built,
            result.Message,
            result.MessageLevel,
            result.ChangedDefaultDecisions,
            result.SelectedNode != null,
            result.SelectedNode?.ItemId,
            result.SelectedNode?.Name,
            result.SelectedNode == null ? 0 : 1,
            result.SelectedNode == null ? 0 : CountNodes(result.SelectedNode));
    }

    private static RecipeBuildDiagnosticDump CreateFailureDump(
        RecipeBuildDiagnosticContext context,
        List<RecipeBuildDiagnosticPhase> phases,
        RecipeBuildDiagnosticStatus status,
        RecipeBuildDiagnosticPhaseStatus phaseStatus,
        string failureType,
        string message,
        Exception exception)
    {
        var completed = DateTime.UtcNow;
        if (phases.Count == 0)
        {
            var phaseStart = completed;
            phases.Add(new RecipeBuildDiagnosticPhase(
                "recipe-build-command",
                phaseStart,
                completed,
                ElapsedMilliseconds(phaseStart, completed),
                phaseStatus,
                message));
        }
        else
        {
            var lastIndex = phases.Count - 1;
            var phase = phases[lastIndex];
            phases[lastIndex] = phase with
            {
                CompletedAtUtc = phase.CompletedAtUtc ?? completed,
                ElapsedMilliseconds = phase.CompletedAtUtc.HasValue
                    ? phase.ElapsedMilliseconds
                    : ElapsedMilliseconds(phase.StartedAtUtc, completed),
                Status = phaseStatus,
                Message = message
            };
        }

        return new RecipeBuildDiagnosticDump(
            SchemaVersion,
            ToolName,
            DateTime.UtcNow,
            FormatLocalTimestamp(DateTimeOffset.Now),
            status,
            context,
            phases,
            null,
            new RecipeBuildDiagnosticFailure(
                "recipe-build-command",
                failureType,
                message,
                exception.GetType().Name,
                exception.StackTrace));
    }

    private static string CreateCompletedAfterWatchdogMessage(string? phaseName)
    {
        var phase = string.IsNullOrWhiteSpace(phaseName)
            ? "the active phase"
            : $"'{phaseName}'";
        return $"Diagnostic watchdog timed out during {phase}. The plan graph was built, but follow-up work was canceled before the command returned.";
    }

    private static string CreateTimedOutBeforeBuildFinishedMessage(string? phaseName)
    {
        var phase = string.IsNullOrWhiteSpace(phaseName)
            ? "the active phase"
            : $"'{phaseName}'";
        return $"Diagnostic watchdog timed out during {phase} before the plan graph finished building.";
    }

    private static int CountNodes(PlanNode node)
    {
        return 1 + node.Children.Sum(CountNodes);
    }

    private static long ElapsedMilliseconds(DateTime startedAtUtc, DateTime completedAtUtc)
    {
        return Math.Max(0, (long)(completedAtUtc - startedAtUtc).TotalMilliseconds);
    }

    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidFileNameCharacterPattern();

    private sealed class RecipeBuildDiagnosticRecorder : IRecipeBuildDiagnosticRecorder
    {
        private readonly List<RecipeBuildDiagnosticPhase> _phases;
        private readonly CancellationTokenSource _timeoutCts;
        private readonly TimeSpan _publicationGraceTimeout;
        private readonly TimeSpan _postBuildTimeout;

        public RecipeBuildDiagnosticRecorder(
            List<RecipeBuildDiagnosticPhase> phases,
            CancellationTokenSource timeoutCts,
            TimeSpan publicationGraceTimeout,
            TimeSpan postBuildTimeout)
        {
            _phases = phases;
            _timeoutCts = timeoutCts;
            _publicationGraceTimeout = publicationGraceTimeout;
            _postBuildTimeout = postBuildTimeout;
        }

        public string? LastPhaseName { get; private set; }
        public string? LastTimedOutPhaseName { get; private set; }

        public T RunPhase<T>(string name, Func<T> action)
        {
            var index = StartPhase(name);
            try
            {
                var result = action();
                CompletePhase(index, RecipeBuildDiagnosticPhaseStatus.Completed, null);
                return result;
            }
            catch (Exception ex)
            {
                CompletePhase(index, PhaseStatusForException(ex), ex.Message);
                throw;
            }
        }

        public void RunPhase(string name, Action action)
        {
            RunPhase(
                name,
                () =>
                {
                    action();
                    return true;
                });
        }

        public async Task<T> RunPhaseAsync<T>(
            string name,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            var index = StartPhase(name);
            try
            {
                var result = await action(cancellationToken);
                CompletePhase(index, RecipeBuildDiagnosticPhaseStatus.Completed, null);
                return result;
            }
            catch (Exception ex)
            {
                CompletePhase(index, PhaseStatusForException(ex), ex.Message);
                throw;
            }
        }

        public async Task RunPhaseAsync(
            string name,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            await RunPhaseAsync(
                name,
                async ct =>
                {
                    await action(ct);
                    return true;
                },
                cancellationToken);
        }

        private int StartPhase(string name)
        {
            LastPhaseName = name;
            if (string.Equals(name, "auto-market-analysis.publish", StringComparison.Ordinal) &&
                _publicationGraceTimeout > TimeSpan.Zero)
            {
                _timeoutCts.CancelAfter(_publicationGraceTimeout);
            }

            _phases.Add(new RecipeBuildDiagnosticPhase(
                name,
                DateTime.UtcNow,
                null,
                0,
                RecipeBuildDiagnosticPhaseStatus.Started,
                null));
            return _phases.Count - 1;
        }

        private void CompletePhase(int index, RecipeBuildDiagnosticPhaseStatus status, string? message)
        {
            var completed = DateTime.UtcNow;
            var phase = _phases[index];
            if (status == RecipeBuildDiagnosticPhaseStatus.Completed &&
                string.Equals(phase.Name, "build-plan", StringComparison.Ordinal) &&
                !_timeoutCts.IsCancellationRequested &&
                _postBuildTimeout > TimeSpan.Zero)
            {
                _timeoutCts.CancelAfter(_postBuildTimeout);
            }

            if (status == RecipeBuildDiagnosticPhaseStatus.TimedOut &&
                string.IsNullOrWhiteSpace(LastTimedOutPhaseName))
            {
                LastTimedOutPhaseName = phase.Name;
            }

            _phases[index] = phase with
            {
                CompletedAtUtc = completed,
                ElapsedMilliseconds = ElapsedMilliseconds(phase.StartedAtUtc, completed),
                Status = status,
                Message = message
            };
        }

        private RecipeBuildDiagnosticPhaseStatus PhaseStatusForException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return _timeoutCts.IsCancellationRequested
                    ? RecipeBuildDiagnosticPhaseStatus.TimedOut
                    : RecipeBuildDiagnosticPhaseStatus.Canceled;
            }

            return RecipeBuildDiagnosticPhaseStatus.Failed;
        }
    }
}

public sealed record RecipeBuildDiagnosticDump(
    int SchemaVersion,
    string Tool,
    DateTime ExportedAtUtc,
    string ExportedAtLocal,
    RecipeBuildDiagnosticStatus Status,
    RecipeBuildDiagnosticContext Context,
    IReadOnlyList<RecipeBuildDiagnosticPhase> Phases,
    RecipeBuildDiagnosticResult? Result,
    RecipeBuildDiagnosticFailure? Failure);

public enum RecipeBuildDiagnosticStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Canceled,
    NoProjectItems
}

public sealed record RecipeBuildDiagnosticContext(
    string? CurrentPlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope,
    long PlanSessionVersion,
    int ProjectItemCount,
    IReadOnlyList<RecipeBuildDiagnosticProjectItem> ProjectItems);

public sealed record RecipeBuildDiagnosticProjectItem(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq);

public sealed record RecipeBuildDiagnosticPhase(
    string Name,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    long ElapsedMilliseconds,
    RecipeBuildDiagnosticPhaseStatus Status,
    string? Message);

public enum RecipeBuildDiagnosticPhaseStatus
{
    Started,
    Completed,
    Failed,
    TimedOut,
    Canceled
}

public sealed record RecipeBuildDiagnosticResult(
    bool Built,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel,
    int ChangedDefaultDecisions,
    bool HasSelectedNode,
    int? SelectedItemId,
    string? SelectedItemName,
    int RootItemCount,
    int TotalNodeCount);

public sealed record RecipeBuildDiagnosticFailure(
    string Phase,
    string FailureType,
    string Message,
    string? ExceptionType,
    string? StackTrace);
