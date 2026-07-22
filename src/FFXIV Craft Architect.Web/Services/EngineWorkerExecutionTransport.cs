using FFXIV_Craft_Architect.Core.Engine;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class EngineWorkerExecutionTransport : IEngineExecutionTransport, IAsyncDisposable
{
    private readonly EngineWorkerClient _client;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private bool _disposed;

    public EngineWorkerExecutionTransport(EngineWorkerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public EngineExecutionTransportCapability Capability { get; } =
        new(EngineExecutionTransportKind.BrowserWorker, true);

    public async Task<EngineComputationResult> ExecuteAsync(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _execution.WaitAsync(cancellationToken);
        EventHandler<EngineProgress>? progressHandler = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client.State is EngineWorkerLifecycleState.Stopped or EngineWorkerLifecycleState.Faulted)
            {
                await _client.StartAsync(cancellationToken);
            }
            if (_client.State != EngineWorkerLifecycleState.Ready || _client.Capability?.ExecutionSupported != true)
            {
                throw new NotSupportedException("The managed browser Worker is not ready for engine computation.");
            }

            if (progress is not null)
            {
                progressHandler = (_, value) =>
                {
                    if (value.Generation == generation && value.ExecutionId == executionId)
                    {
                        progress.Report(value);
                    }
                };
                _client.ProgressChanged += progressHandler;
            }
            return await _client.ExecuteForHostAsync(generation, executionId, request, cancellationToken);
        }
        finally
        {
            if (progressHandler is not null)
            {
                _client.ProgressChanged -= progressHandler;
            }
            _execution.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _execution.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await _client.DisposeAsync();
        }
        finally
        {
            _execution.Release();
        }
    }
}
