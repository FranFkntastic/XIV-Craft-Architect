using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class BrowserEngineWorkerTransport : IEngineWorkerTransport
{
    private const string ModulePath = "./engine-worker-bootstrap.js";
    private readonly IJSRuntime _jsRuntime;
    private readonly string _workerUrl;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private IJSObjectReference? _module;
    private IJSObjectReference? _controller;
    private DotNetObjectReference<BrowserEngineWorkerTransport>? _callback;
    private TaskCompletionSource<EngineWorkerCapability>? _startup;
    private EngineWorkerMessage? _activeExecution;
    private bool _disposed;

    public BrowserEngineWorkerTransport(IJSRuntime jsRuntime, string workerUrl = "engine-worker.js")
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _workerUrl = string.IsNullOrWhiteSpace(workerUrl)
            ? throw new ArgumentException("A Worker URL is required.", nameof(workerUrl))
            : workerUrl;
    }

    public event EventHandler<EngineWorkerMessage>? MessageReceived;

    public async Task<EngineWorkerCapability> StartAsync(long generation, CancellationToken cancellationToken)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        Task<EngineWorkerCapability> startupTask;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_controller is not null)
            {
                throw new InvalidOperationException("The browser engine Worker is already started.");
            }

            _startup = new TaskCompletionSource<EngineWorkerCapability>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _activeExecution = null;
            }
            _callback ??= DotNetObjectReference.Create(this);
            _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
            _controller = await _module.InvokeAsync<IJSObjectReference>(
                "createEngineWorkerController",
                cancellationToken,
                _callback,
                _workerUrl);
            startupTask = _startup.Task;
            await _controller.InvokeVoidAsync("ping", cancellationToken, generation);
        }
        catch
        {
            _startup = null;
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }

        return await startupTask.WaitAsync(cancellationToken);
    }

    public async Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var controller = _controller
            ?? throw new InvalidOperationException("The browser engine Worker has not started.");
        if (string.Equals(message.Kind, "execute", StringComparison.Ordinal))
        {
            lock (_sync)
            {
                _activeExecution = message;
            }
        }
        try
        {
            await controller.InvokeVoidAsync("send", cancellationToken, message);
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeExecution, message))
                {
                    _activeExecution = null;
                }
            }
            throw;
        }
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var controller = _controller;
            _startup?.TrySetCanceled(cancellationToken);
            _startup = null;
            if (controller is null)
            {
                return;
            }
            await controller.InvokeVoidAsync("terminate", cancellationToken);
            await controller.DisposeAsync();
            _controller = null;
            lock (_sync)
            {
                _activeExecution = null;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    [JSInvokable]
    public Task ReceiveMessage(JsonElement messageJson)
    {
        try
        {
            var message = messageJson.Deserialize<EngineWorkerMessage>(EngineJsonSerializerOptions.CreateWire())
                ?? throw new InvalidOperationException("The browser Worker message is empty.");
            if (string.Equals(message.Kind, "capability", StringComparison.Ordinal))
            {
                var capability = message.Payload?.Deserialize<EngineWorkerCapability>(EngineJsonSerializerOptions.CreateWire())
                    ?? throw new InvalidOperationException("The browser Worker capability is missing.");
                _startup?.TrySetResult(capability);
            }
            else
            {
                if (message.Kind is "computation-result" or "protocol-error")
                {
                    lock (_sync)
                    {
                        if (_activeExecution is { } active &&
                            active.Generation == message.Generation &&
                            active.ExecutionId == message.ExecutionId &&
                            active.TransactionId == message.TransactionId)
                        {
                            _activeExecution = null;
                        }
                    }
                }
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception ex)
        {
            _startup?.TrySetException(ex);
        }
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task ReceiveError(string kind, string message)
    {
        var failure = new InvalidOperationException($"Browser engine Worker {kind}: {message}");
        if (_startup is { Task.IsCompleted: false } startup)
        {
            startup.TrySetException(failure);
            return Task.CompletedTask;
        }

        EngineWorkerMessage? active;
        lock (_sync)
        {
            active = _activeExecution;
        }
        if (active is not null)
        {
            MessageReceived?.Invoke(
                this,
                new EngineWorkerMessage(
                    EngineWorkerClient.ProtocolVersion,
                    "protocol-error",
                    active.Generation,
                    active.ExecutionId,
                    active.TransactionId,
                    JsonSerializer.SerializeToElement(
                        new { code = $"worker-{kind}", message = failure.Message },
                        EngineJsonSerializerOptions.CreateWire())));
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await TerminateAsync(CancellationToken.None);
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_module is not null)
            {
                await _module.DisposeAsync();
                _module = null;
            }
            _callback?.Dispose();
            _callback = null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
