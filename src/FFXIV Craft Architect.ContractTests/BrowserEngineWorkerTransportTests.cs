using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public sealed class BrowserEngineWorkerTransportTests
{
    [Fact]
    public async Task Transport_HandshakesForwardsMessagesAndTerminatesBeforeDisposal()
    {
        var controller = new RecordingController();
        var module = new RecordingModule(controller);
        var runtime = new RecordingRuntime(module);
        var transport = new BrowserEngineWorkerTransport(
            runtime,
            "engine-worker.js?acceptance=true",
            "Company.Alpha");
        EngineWorkerMessage? received = null;
        transport.MessageReceived += (_, message) => received = message;

        var capability = await transport.StartAsync(3, CancellationToken.None);
        var progress = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "progress",
            3,
            Guid.NewGuid(),
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { message = "bounded" }));
        await controller.EmitAsync(progress);
        await transport.SendAsync(progress, CancellationToken.None);
        await transport.TerminateAsync(CancellationToken.None);
        await transport.DisposeAsync();

        Assert.True(capability.ExecutionSupported);
        Assert.Equal("11111111-1111-1111-1111-111111111111", capability.WorkerInstanceId);
        Assert.Equal(progress.Kind, received?.Kind);
        Assert.Equal(progress.Generation, received?.Generation);
        Assert.Equal(progress.ExecutionId, received?.ExecutionId);
        Assert.Equal(progress.TransactionId, received?.TransactionId);
        Assert.Equal(progress.Payload?.GetRawText(), received?.Payload?.GetRawText());
        Assert.Equal(progress.Kind, controller.Sent?.Kind);
        Assert.Equal(progress.Generation, controller.Sent?.Generation);
        Assert.Equal(progress.ExecutionId, controller.Sent?.ExecutionId);
        Assert.Equal(progress.TransactionId, controller.Sent?.TransactionId);
        Assert.Equal(progress.Payload?.GetRawText(), controller.Sent?.Payload?.GetRawText());
        Assert.Equal("engine-worker.js?acceptance=true", module.WorkerUrl);
        Assert.Equal("company.alpha", module.WorkspaceId);
        Assert.False(module.RequestFreshAuthority);
        Assert.True(controller.Terminated);
        Assert.True(controller.Disposed);
        Assert.True(module.Disposed);
    }

    [Fact]
    public async Task Transport_FailedTerminationRetainsControllerForCleanupRetry()
    {
        var controller = new RecordingController { FailTerminationOnce = true };
        var transport = new BrowserEngineWorkerTransport(
            new RecordingRuntime(new RecordingModule(controller)));
        await transport.StartAsync(1, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.TerminateAsync(CancellationToken.None));
        await transport.TerminateAsync(CancellationToken.None);

        Assert.Equal(2, controller.TerminationAttempts);
        Assert.True(controller.Terminated);
        Assert.True(controller.Disposed);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Transport_RestartRequestsFreshWorkspaceAuthority()
    {
        var controller = new RecordingController();
        var module = new RecordingModule(controller);
        var transport = new BrowserEngineWorkerTransport(new RecordingRuntime(module));

        await transport.StartAsync(1, CancellationToken.None);
        Assert.False(module.RequestFreshAuthority);
        await transport.TerminateAsync(CancellationToken.None);
        await transport.StartAsync(2, CancellationToken.None);

        Assert.True(module.RequestFreshAuthority);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Transport_RunningWorkerErrorIsCorrelatedToActiveExecution()
    {
        var controller = new RecordingController();
        var transport = new BrowserEngineWorkerTransport(
            new RecordingRuntime(new RecordingModule(controller)));
        EngineWorkerMessage? received = null;
        transport.MessageReceived += (_, message) => received = message;
        await transport.StartAsync(1, CancellationToken.None);
        var execute = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "execute",
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { request = "fixture" }));
        await transport.SendAsync(execute, CancellationToken.None);

        await transport.ReceiveError("error", "runtime crashed");

        Assert.Equal("protocol-error", received?.Kind);
        Assert.Equal(execute.ExecutionId, received?.ExecutionId);
        Assert.Equal(execute.TransactionId, received?.TransactionId);
        Assert.Contains("runtime crashed", received?.Payload?.GetProperty("message").GetString(), StringComparison.Ordinal);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Transport_MalformedRunningMessageFailsActiveExecutionImmediately()
    {
        var controller = new RecordingController();
        var transport = new BrowserEngineWorkerTransport(
            new RecordingRuntime(new RecordingModule(controller)));
        EngineWorkerMessage? received = null;
        transport.MessageReceived += (_, message) => received = message;
        await transport.StartAsync(1, CancellationToken.None);
        var execute = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "execute",
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { request = "fixture" }));
        await transport.SendAsync(execute, CancellationToken.None);

        await transport.ReceiveMessageJson("{");

        Assert.Equal("protocol-error", received?.Kind);
        Assert.Equal(execute.ExecutionId, received?.ExecutionId);
        Assert.Equal(execute.TransactionId, received?.TransactionId);
        Assert.Equal("worker-message-invalid", received?.Payload?.GetProperty("code").GetString());
        await transport.DisposeAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("/absolute")]
    [InlineData("workspace:colon")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Transport_RejectsUnsafeWorkspaceIds(string workspaceId)
    {
        var runtime = new RecordingRuntime(new RecordingModule(new RecordingController()));

        Assert.Throws<ArgumentException>(() =>
            new BrowserEngineWorkerTransport(runtime, workspaceId: workspaceId));
    }

    [Fact]
    public void BrowserBootstrap_EnforcesSingleWriterAndBoundedFollowerCoordination()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "engine-worker-bootstrap.js"));

        Assert.Contains("navigator.locks.request(", bootstrap, StringComparison.Ordinal);
        Assert.Contains("new BroadcastChannel(channelName)", bootstrap, StringComparison.Ordinal);
        Assert.Contains("ifAvailable: true", bootstrap, StringComparison.Ordinal);
        Assert.Contains("type: \"command-accepted\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("type: \"authority-restart-request\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("type: \"session-projection\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("kind: \"cross-tab-session-projection\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("result?.commandKind?.startsWith(\"mutate-\")", bootstrap, StringComparison.Ordinal);
        Assert.Contains("result?.commandKind?.startsWith(\"operation-\")", bootstrap, StringComparison.Ordinal);
        Assert.Contains("typeof shell.hasSession !== \"boolean\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("requestLease();", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("new SharedWorker", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserWorker_NamespacesDurableStateWithoutOrphaningLegacyActiveSession()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "engine-worker.js"));

        Assert.Contains("workspaceId === \"active\"", worker, StringComparison.Ordinal);
        Assert.Contains("`workspace:${workspaceId}:active`", worker, StringComparison.Ordinal);
        Assert.Contains("`${activeSessionManifestId}:${revision}`", worker, StringComparison.Ordinal);
        Assert.Contains("`${activeSessionManifestId}:${revision}:${field}`", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossTabShell_AdvancesAuthorityAndInvalidatesStaleViewProjections()
    {
        var store = new WorkerProjectionStore();
        var recipe = new WorkerRecipePlannerProjection(
            Revision: 0,
            PlanId: null,
            PlanName: null,
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            ProjectItems: [],
            Roots: [],
            HasMarketEvidence: false,
            HasProcurementRoute: false);
        var recipeResult = new WorkerSessionResultEnvelope(
            WorkerSessionProtocol.ContractVersion,
            WorkerSessionCommandKinds.RecipeProjection,
            Revision: 0,
            Accepted: true,
            RejectionCode: null,
            Message: null,
            JsonSerializer.SerializeToElement(
                recipe,
                EngineJsonSerializerOptions.CreateWire()));
        Assert.True(store.TryPublishRecipe(recipeResult));

        var successor = store.Shell with
        {
            Revision = 1,
            HasSession = true,
            PlanId = "plan-1",
            PlanName = "Plan 1"
        };

        Assert.True(store.TryPublishCrossTabShell(successor));
        Assert.Equal(1, store.Shell.Revision);
        Assert.Null(store.Recipe);
        Assert.Null(store.Acquisition);
        Assert.Null(store.Market);
        Assert.Null(store.Procurement);
        Assert.False(store.TryPublishCrossTabShell(successor));

        var operationId = Guid.NewGuid();
        var operationShell = successor with
        {
            Operation = new WorkerSessionOperationProjection(
                operationId,
                WorkerSessionOperationKind.PlanDerivation,
                "plan-derivation:1",
                BaseRevision: 1,
                WorkerSessionOperationDisposition.Acquired,
                IsActive: true,
                "Updating plan prices and route...")
        };
        Assert.True(store.TryPublishCrossTabShell(operationShell));
        Assert.Equal(operationId, store.Operation?.OperationId);
    }

    [Fact]
    public void ProfileSyncSession_UsesOneAuthenticatedRevisionOnlyLeaderStream()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "profile-sync-session.js"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("navigator.locks.request(", session, StringComparison.Ordinal);
        Assert.Contains("new BroadcastChannel(channelName)", session, StringComparison.Ordinal);
        Assert.Contains("\"X-Profile-Key\": accessKey", session, StringComparison.Ordinal);
        Assert.Contains("credentials: \"omit\"", session, StringComparison.Ordinal);
        Assert.Contains("redirect: \"error\"", session, StringComparison.Ordinal);
        Assert.Contains("applicationLockName", session, StringComparison.Ordinal);
        Assert.Contains("\"RecoverProfileRevision\"", session, StringComparison.Ordinal);
        Assert.Contains("normalizedProfileId,\n                    state.cursor", session, StringComparison.Ordinal);
        Assert.Contains("const replayAfterRevision = state.cursor", session, StringComparison.Ordinal);
        Assert.Contains("source,\n                replayAfterRevision", session, StringComparison.Ordinal);
        Assert.Contains("kind: \"profile-revision\"", session, StringComparison.Ordinal);
        Assert.Contains("serverRevision: revision", session, StringComparison.Ordinal);
        Assert.Contains("state.fetchController?.abort()", session, StringComparison.Ordinal);
        Assert.DoesNotContain("new EventSource", session, StringComparison.Ordinal);

        var syncService = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "Services",
            "ProfileHosting",
            "ProfileSyncService.cs"));
        Assert.Contains("Math.Min(persistedRevision, Math.Max(0, replayAfterRevision.Value))", syncService, StringComparison.Ordinal);
        Assert.Contains("candidateRevision > persistedRevision", syncService, StringComparison.Ordinal);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RecordingRuntime(RecordingModule module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("import", identifier);
            Assert.Equal("./engine-worker-bootstrap.js?v=2", Assert.Single(args!));
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class RecordingModule(RecordingController controller) : IJSObjectReference
    {
        public string? WorkerUrl { get; private set; }

        public string? WorkspaceId { get; private set; }

        public bool RequestFreshAuthority { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("createEngineWorkerController", identifier);
            controller.Callback = Assert.IsType<DotNetObjectReference<BrowserEngineWorkerTransport>>(args![0]);
            WorkerUrl = Assert.IsType<string>(args[1]);
            WorkspaceId = Assert.IsType<string>(args[2]);
            RequestFreshAuthority = Assert.IsType<bool>(args[3]);
            return ValueTask.FromResult((TValue)(object)controller);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingController : IJSObjectReference
    {
        public DotNetObjectReference<BrowserEngineWorkerTransport>? Callback { get; set; }

        public EngineWorkerMessage? Sent { get; private set; }

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public bool FailTerminationOnce { get; init; }

        public int TerminationAttempts { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            switch (identifier)
            {
                case "ping":
                    var generation = Assert.IsType<long>(args![0]);
                    var capability = new EngineWorkerCapability(
                        EngineWorkerClient.ProtocolVersion,
                        generation,
                        DedicatedWorker: true,
                        CrossOriginIsolated: false,
                        SharedArrayBufferAvailable: false,
                        ThreadsAvailable: false,
                        ExecutionSupported: true,
                        ManagedRuntimeReady: true,
                        ManagedRuntimeAssembly: EngineWorkerClient.ManagedRuntimeAssembly,
                        ManagedRuntimeProofHash: new string('a', 64),
                        WorkerInstanceId: "11111111-1111-1111-1111-111111111111");
                    var message = new EngineWorkerMessage(
                        EngineWorkerClient.ProtocolVersion,
                        "capability",
                        generation,
                        null,
                        null,
                        JsonSerializer.SerializeToElement(capability, EngineJsonSerializerOptions.CreateWire()));
                    Callback!.Value.ReceiveMessage(
                        JsonSerializer.SerializeToElement(message, EngineJsonSerializerOptions.CreateWire())).GetAwaiter().GetResult();
                    break;
                case "sendJson":
                    Sent = JsonSerializer.Deserialize<EngineWorkerMessage>(
                        Assert.IsType<string>(args![0]),
                        EngineJsonSerializerOptions.CreateWire());
                    Assert.Equal(Sent!.Generation, Assert.IsType<long>(args[1]));
                    Assert.Equal(Sent.Kind, Assert.IsType<string>(args[2]));
                    break;
                case "terminate":
                    TerminationAttempts++;
                    if (FailTerminationOnce && TerminationAttempts == 1)
                    {
                        throw new InvalidOperationException("termination failed");
                    }
                    Terminated = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected controller invocation '{identifier}'.");
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        public async Task EmitAsync(EngineWorkerMessage message) =>
            await Callback!.Value.ReceiveMessage(
                JsonSerializer.SerializeToElement(message, EngineJsonSerializerOptions.CreateWire()));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
