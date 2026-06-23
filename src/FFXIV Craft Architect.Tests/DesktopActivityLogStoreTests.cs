using FFXIV_Craft_Architect.Desktop.Services;

namespace FFXIV_Craft_Architect.Tests;

[Collection(DesktopTestCollection.Name)]
public sealed class DesktopActivityLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"craft-architect-activity-log-{Guid.NewGuid():N}");

    [Fact]
    public void Append_AndLoadLatest_PersistRecentEntriesNewestFirst()
    {
        var store = new DesktopActivityLogStore(_root);
        store.Append(new DesktopActivityLogEntry(DateTime.UtcNow.AddMinutes(-2), "Session", "first"));
        store.Append(new DesktopActivityLogEntry(DateTime.UtcNow.AddMinutes(-1), "Job", "second"));

        var entries = store.LoadLatest(2);

        Assert.Equal(Path.Combine(_root, "desktop-activity.jsonl"), store.LogPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Message);
        Assert.Equal("first", entries[1].Message);
    }

    [Fact]
    public void LoadLatest_SkipsCorruptLines()
    {
        var store = new DesktopActivityLogStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllLines(store.LogPath,
        [
            "{not json",
            """{"timestamp":"2026-06-20T00:00:00Z","kind":"Cache","message":"kept"}"""
        ]);

        var entry = Assert.Single(store.LoadLatest(5));

        Assert.Equal("Cache", entry.Kind);
        Assert.Equal("kept", entry.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
