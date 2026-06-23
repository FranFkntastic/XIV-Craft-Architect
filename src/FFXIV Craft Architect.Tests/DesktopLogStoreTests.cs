using FFXIV_Craft_Architect.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Tests;

[Collection(DesktopTestCollection.Name)]
[Trait(TestTraits.Surface, TestTraits.Desktop)]
public sealed class DesktopLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"craft-architect-diagnostic-log-{Guid.NewGuid():N}");

    [Fact]
    public void Append_AndLoadLatest_PersistRecentEntriesNewestFirst()
    {
        var store = new DesktopLogStore(_root);
        store.Append(new DesktopLogEntry(DateTime.UtcNow.AddMinutes(-2), "Information", "First.Category", 1, "first", null, null));
        store.Append(new DesktopLogEntry(DateTime.UtcNow.AddMinutes(-1), "Error", "Second.Category", 2, "second", "boom", "stack"));

        var entries = store.LoadLatest(2);

        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Message);
        Assert.Equal("first", entries[1].Message);
        Assert.Contains("desktop-", Path.GetFileName(store.LogPath));
    }

    [Fact]
    public void ListLogFiles_ReturnsDiagnosticLogsWithoutActivityLog()
    {
        var store = new DesktopLogStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.LogPath, string.Empty);
        File.WriteAllText(Path.Combine(_root, "desktop-activity.jsonl"), string.Empty);
        var olderLog = Path.Combine(_root, "desktop-2026-06-20.jsonl");
        File.WriteAllText(olderLog, string.Empty);

        var files = store.ListLogFiles();

        Assert.Contains(store.LogPath, files);
        Assert.Contains(olderLog, files);
        Assert.DoesNotContain(files, path => path.EndsWith("desktop-activity.jsonl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadAll_ReadsSelectedLogFile()
    {
        var store = new DesktopLogStore(_root);
        var selectedPath = Path.Combine(_root, "desktop-2026-06-20.jsonl");
        Directory.CreateDirectory(_root);
        File.WriteAllLines(selectedPath,
        [
            """{"timestamp":"2026-06-20T00:00:00Z","level":"Debug","category":"Desktop.First","eventId":1,"message":"first","exception":null,"stackTrace":null}""",
            """{"timestamp":"2026-06-20T00:01:00Z","level":"Trace","category":"Desktop.Second","eventId":2,"message":"second","exception":null,"stackTrace":null}"""
        ]);

        var entries = store.LoadAll(selectedPath);

        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Message);
        Assert.Equal("first", entries[1].Message);
    }

    [Fact]
    public void LoadLatest_SkipsCorruptLines()
    {
        var store = new DesktopLogStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllLines(store.LogPath,
        [
            "{not json",
            """{"timestamp":"2026-06-20T00:00:00Z","level":"Error","category":"Desktop","eventId":12,"message":"kept","exception":null,"stackTrace":null}"""
        ]);

        var entry = Assert.Single(store.LoadLatest(5));

        Assert.Equal("Error", entry.Level);
        Assert.Equal("kept", entry.Message);
    }

    [Fact]
    public void DesktopLogProvider_WritesLoggerEvents()
    {
        var store = new DesktopLogStore(_root);
        using var provider = new DesktopLogProvider(store);
        var logger = provider.CreateLogger("Desktop.Tests");

        logger.LogTrace("Trace item {ItemId}", 372608);
        logger.LogError(new InvalidOperationException("broken"), "Failed item {ItemId}", 372609);

        var entries = store.LoadLatest(5);
        Assert.Equal(2, entries.Count);
        var entry = entries[0];
        Assert.Equal("Error", entry.Level);
        Assert.Equal("Desktop.Tests", entry.Category);
        Assert.Contains("Failed item 372609", entry.Message);
        Assert.Contains("broken", entry.Exception);
        Assert.Contains(entries, logged => logged is { Level: "Trace" } && logged.Message.Contains("Trace item 372608", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
