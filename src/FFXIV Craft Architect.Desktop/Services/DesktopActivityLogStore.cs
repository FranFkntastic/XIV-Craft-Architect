using System.Text.Json;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopActivityLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public DesktopActivityLogStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIV_Craft_Architect",
                "Logs"))
    {
    }

    public DesktopActivityLogStore(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Activity log root is required.", nameof(rootDirectory))
            : rootDirectory;
        LogPath = Path.Combine(RootDirectory, "desktop-activity.jsonl");
    }

    public string RootDirectory { get; }

    public string LogPath { get; }

    public void Append(DesktopActivityLogEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(RootDirectory);
            File.AppendAllText(LogPath, $"{JsonSerializer.Serialize(entry, JsonOptions)}{Environment.NewLine}");
        }
    }

    public IReadOnlyList<DesktopActivityLogEntry> LoadLatest(int count)
    {
        if (count <= 0 || !File.Exists(LogPath))
        {
            return Array.Empty<DesktopActivityLogEntry>();
        }

        lock (_gate)
        {
            return File.ReadLines(LogPath)
                .Reverse()
                .Select(TryDeserialize)
                .Where(entry => entry != null)
                .Take(count)
                .Select(entry => entry!)
                .ToArray();
        }
    }

    private static DesktopActivityLogEntry? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopActivityLogEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DesktopActivityLogEntry(DateTime Timestamp, string Kind, string Message);
