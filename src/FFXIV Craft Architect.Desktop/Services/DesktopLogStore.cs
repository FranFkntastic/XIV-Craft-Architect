using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DiagnosticLogSearchPattern = "desktop-*.jsonl";
    private const string ActivityLogFileName = "desktop-activity.jsonl";
    private readonly object _gate = new();

    public DesktopLogStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIV_Craft_Architect",
                "Logs"))
    {
    }

    public DesktopLogStore(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Log root is required.", nameof(rootDirectory))
            : rootDirectory;
        LogPath = Path.Combine(RootDirectory, $"desktop-{DateTime.Now:yyyy-MM-dd}.jsonl");
    }

    public string RootDirectory { get; }

    public string LogPath { get; }

    public IReadOnlyList<string> ListLogFiles()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return File.Exists(LogPath) ? [LogPath] : [];
        }

        var files = Directory
            .EnumerateFiles(RootDirectory, DiagnosticLogSearchPattern, SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), ActivityLogFileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (!files.Contains(LogPath, StringComparer.OrdinalIgnoreCase))
        {
            files.Insert(0, LogPath);
        }

        return files;
    }

    public void Append(DesktopLogEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(RootDirectory);
            File.AppendAllText(LogPath, $"{JsonSerializer.Serialize(entry, JsonOptions)}{Environment.NewLine}");
        }
    }

    public IReadOnlyList<DesktopLogEntry> LoadLatest(int count)
    {
        return LoadLatest(LogPath, count);
    }

    public IReadOnlyList<DesktopLogEntry> LoadLatest(string path, int count)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<DesktopLogEntry>();
        }

        lock (_gate)
        {
            return File.ReadLines(path)
                .Reverse()
                .Select(TryDeserialize)
                .Where(entry => entry != null)
                .Take(count)
                .Select(entry => entry!)
                .ToArray();
        }
    }

    public IReadOnlyList<DesktopLogEntry> LoadAll(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<DesktopLogEntry>();
        }

        lock (_gate)
        {
            return File.ReadLines(path)
                .Reverse()
                .Select(TryDeserialize)
                .Where(entry => entry != null)
                .Select(entry => entry!)
                .ToArray();
        }
    }

    public static DesktopLogEntry? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopLogEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DesktopLogEntry(
    DateTime Timestamp,
    string Level,
    string Category,
    int EventId,
    string Message,
    string? Exception,
    string? StackTrace);

public sealed class DesktopLogProvider : ILoggerProvider
{
    private readonly DesktopLogStore _store;

    public DesktopLogProvider(DesktopLogStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ILogger CreateLogger(string categoryName) =>
        new DesktopLogger(_store, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class DesktopLogger : ILogger
{
    private readonly DesktopLogStore _store;
    private readonly string _categoryName;

    public DesktopLogger(DesktopLogStore store, string categoryName)
    {
        _store = store;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception == null)
        {
            return;
        }

        _store.Append(new DesktopLogEntry(
            DateTime.UtcNow,
            logLevel.ToString(),
            _categoryName,
            eventId.Id,
            message,
            exception?.ToString(),
            exception?.StackTrace));
    }
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}
