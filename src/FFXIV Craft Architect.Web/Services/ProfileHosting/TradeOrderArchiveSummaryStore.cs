using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeOrderArchiveSummaryRecord
{
    public string Id { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
    public Guid CompanyProfileId { get; set; }
    public string ConnectionScopeId { get; set; } = string.Empty;
    public long HostedRevision { get; set; }
    public TradeOrderArchiveSummary Summary { get; set; } = new();
}

public sealed class TradeOrderArchiveSummaryStore
{
    private readonly IndexedDbService _indexedDb;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private Dictionary<ArchiveSummaryIdentity, TradeOrderArchiveSummaryRecord> _records = [];
    private bool _loaded;

    public TradeOrderArchiveSummaryStore(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<TradeOrderArchiveSummaryRecord>> LoadAsync()
    {
        await EnsureLoadedAsync();
        lock (_stateGate)
        {
            return _records.Values.ToArray();
        }
    }

    public IReadOnlyList<TradeOrderArchiveSummaryRecord> GetAll(string? connectionScopeId)
    {
        if (string.IsNullOrWhiteSpace(connectionScopeId))
        {
            return [];
        }

        lock (_stateGate)
        {
            return _records.Values
                .Where(record => string.Equals(record.ConnectionScopeId, connectionScopeId, StringComparison.Ordinal))
                .ToArray();
        }
    }

    public async Task<bool> UpsertAsync(
        TradeOrderArchiveSummary summary,
        long hostedRevision,
        string connectionScopeId)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionScopeId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostedRevision);

        await _gate.WaitAsync();
        var changed = false;
        try
        {
            await EnsureLoadedUnderGateAsync();
            var identity = new ArchiveSummaryIdentity(connectionScopeId, summary.OrderId);
            TradeOrderArchiveSummaryRecord? existing;
            lock (_stateGate)
            {
                existing = _records.GetValueOrDefault(identity);
            }
            if (existing != null && existing.HostedRevision >= hostedRevision)
            {
                return false;
            }

            var record = new TradeOrderArchiveSummaryRecord
            {
                Id = CreateStorageId(identity),
                OrderId = summary.OrderId,
                CompanyProfileId = summary.CompanyProfileId,
                ConnectionScopeId = connectionScopeId,
                HostedRevision = hostedRevision,
                Summary = summary
            };
            if (!await _indexedDb.SaveTradeOrderArchiveSummaryAsync(record))
            {
                throw new InvalidOperationException(
                    $"Browser storage could not persist archived Trade order summary '{summary.OrderId:D}'.");
            }

            lock (_stateGate)
            {
                _records[identity] = record;
            }
            changed = true;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
        return changed;
    }

    public Task<bool> RemoveAsync(string connectionScopeId, Guid orderId) =>
        RemoveAsync(connectionScopeId, orderId, null);

    public Task<bool> RemoveIfSupersededAsync(
        string connectionScopeId,
        Guid orderId,
        long hostedRevision) =>
        RemoveAsync(connectionScopeId, orderId, hostedRevision);

    private async Task<bool> RemoveAsync(
        string connectionScopeId,
        Guid orderId,
        long? maximumHostedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionScopeId);
        var identity = new ArchiveSummaryIdentity(connectionScopeId, orderId);
        await _gate.WaitAsync();
        var changed = false;
        try
        {
            await EnsureLoadedUnderGateAsync();
            TradeOrderArchiveSummaryRecord? existing;
            lock (_stateGate)
            {
                existing = _records.GetValueOrDefault(identity);
            }
            if (existing == null ||
                maximumHostedRevision.HasValue && existing.HostedRevision > maximumHostedRevision.Value)
            {
                return false;
            }

            if (!await _indexedDb.DeleteTradeOrderArchiveSummaryAsync(CreateStorageId(identity)))
            {
                throw new InvalidOperationException(
                    $"Browser storage could not delete archived Trade order summary '{orderId:D}'.");
            }

            lock (_stateGate)
            {
                changed = _records.Remove(identity);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
        return changed;
    }

    private async Task EnsureLoadedAsync()
    {
        lock (_stateGate)
        {
            if (_loaded)
            {
                return;
            }
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedUnderGateAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedUnderGateAsync()
    {
        lock (_stateGate)
        {
            if (_loaded)
            {
                return;
            }
        }

        var records = await _indexedDb.LoadTradeOrderArchiveSummariesAsync();
        lock (_stateGate)
        {
            _records = records
                .Where(record =>
                    record.OrderId != Guid.Empty &&
                    !string.IsNullOrWhiteSpace(record.ConnectionScopeId))
                .GroupBy(record => new ArchiveSummaryIdentity(
                    record.ConnectionScopeId,
                    record.OrderId))
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(record => record.HostedRevision).First());
            _loaded = true;
        }
    }

    private static string CreateStorageId(ArchiveSummaryIdentity identity) =>
        $"{identity.ConnectionScopeId}\u001f{identity.OrderId:D}";

    private readonly record struct ArchiveSummaryIdentity(
        string ConnectionScopeId,
        Guid OrderId);
}
