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
        var transport = new BrowserEngineWorkerTransport(runtime, "engine-worker.js?acceptance=true");
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
            Assert.Equal("./engine-worker-bootstrap.js", Assert.Single(args!));
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class RecordingModule(RecordingController controller) : IJSObjectReference
    {
        public string? WorkerUrl { get; private set; }

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
