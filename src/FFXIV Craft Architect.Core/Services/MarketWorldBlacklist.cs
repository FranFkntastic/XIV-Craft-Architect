using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketWorldBlacklist
{
    private readonly List<MarketWorldBlacklistEntry> _entries = new();

    public IReadOnlyList<MarketWorldBlacklistEntry> Entries => _entries;

    public void Add(MarketWorldKey world, TimeSpan duration, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var expiresAt = currentTime.Add(duration);
        AddUntil(world, expiresAt);
    }

    public void AddUntil(MarketWorldKey world, DateTimeOffset expiresAt)
    {
        _entries.RemoveAll(entry => entry.World.Equals(world));
        _entries.Add(new MarketWorldBlacklistEntry(world, expiresAt));
    }

    public HashSet<MarketWorldKey> GetActiveWorlds(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        PruneExpired(currentTime);
        return _entries
            .Where(entry => entry.ExpiresAt > currentTime)
            .Select(entry => entry.World)
            .ToHashSet();
    }

    public void Clear()
    {
        _entries.Clear();
    }

    public void PruneExpired(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        _entries.RemoveAll(entry => entry.ExpiresAt <= currentTime);
    }

    public MarketWorldBlacklist Clone()
    {
        var clone = new MarketWorldBlacklist();
        foreach (var entry in _entries)
        {
            clone.AddUntil(entry.World, entry.ExpiresAt);
        }

        return clone;
    }
}

public sealed record MarketWorldBlacklistEntry(MarketWorldKey World, DateTimeOffset ExpiresAt);
