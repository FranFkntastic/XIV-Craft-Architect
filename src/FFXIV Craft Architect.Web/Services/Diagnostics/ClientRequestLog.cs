namespace FFXIV_Craft_Architect.Web.Services.Diagnostics;

public sealed record ClientRequestLogEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string Url,
    string Result,
    long DurationMilliseconds);

public sealed class ClientRequestLog
{
    private readonly object _sync = new();
    private readonly Queue<ClientRequestLogEntry> _entries;
    private readonly int _capacity;

    public ClientRequestLog(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new Queue<ClientRequestLogEntry>(capacity);
    }

    public void Add(ClientRequestLogEntry entry)
    {
        lock (_sync)
        {
            if (_entries.Count == _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<ClientRequestLogEntry> GetNewestFirst()
    {
        lock (_sync)
        {
            return _entries.Reverse().ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
