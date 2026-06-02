using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreStoredPlanStoreOptions
{
    public CoreStoredPlanStoreOptions(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Storage root directory is required.", nameof(rootDirectory))
            : rootDirectory;
    }

    public string RootDirectory { get; }

    public static CoreStoredPlanStoreOptions CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new CoreStoredPlanStoreOptions(
            Path.Combine(appData, "FFXIV_Craft_Architect", "Sessions"));
    }
}

public sealed class FileCoreStoredPlanStore : ICoreStoredPlanStore
{
    private const string AutoSaveFileName = "autosave.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _plansDirectory;
    private readonly string _autoSavePath;

    public FileCoreStoredPlanStore(CoreStoredPlanStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _plansDirectory = Path.Combine(options.RootDirectory, "plans");
        _autoSavePath = Path.Combine(options.RootDirectory, AutoSaveFileName);
    }

    public async Task<IReadOnlyList<CoreStoredPlanSummary>> LoadPlanSummariesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_plansDirectory))
        {
            return [];
        }

        var summaries = new List<CoreStoredPlanSummary>();
        foreach (var file in Directory.EnumerateFiles(_plansDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(file, ct);
            if (snapshot == null)
            {
                continue;
            }

            summaries.Add(ToSummary(snapshot));
        }

        return summaries
            .OrderByDescending(summary => summary.ModifiedAt)
            .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<CoreStoredPlanSnapshot?> LoadPlanSnapshotAsync(string planId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return Task.FromResult<CoreStoredPlanSnapshot?>(null);
        }

        return ReadSnapshotAsync(GetPlanPath(planId), ct);
    }

    public Task<bool> SavePlanSnapshotAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Id))
        {
            throw new ArgumentException("Stored plan snapshots require an id.", nameof(snapshot));
        }

        return WriteSnapshotAsync(GetPlanPath(snapshot.Id), snapshot, ct);
    }

    public Task<bool> DeletePlanSnapshotAsync(string planId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return Task.FromResult(false);
        }

        return DeleteFileAsync(GetPlanPath(planId), ct);
    }

    public Task<CoreStoredPlanSnapshot?> LoadAutoSaveAsync(CancellationToken ct = default) =>
        ReadSnapshotAsync(_autoSavePath, ct);

    public Task<bool> SaveAutoSaveAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WriteSnapshotAsync(_autoSavePath, snapshot, ct);
    }

    public Task<bool> DeleteAutoSaveAsync(CancellationToken ct = default) =>
        DeleteFileAsync(_autoSavePath, ct);

    private string GetPlanPath(string planId) =>
        Path.Combine(_plansDirectory, $"{EncodePathSegment(planId)}.json");

    private static CoreStoredPlanSummary ToSummary(CoreStoredPlanSnapshot snapshot) =>
        new()
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            ModifiedAt = snapshot.ModifiedAt,
            SavedAt = snapshot.SavedAt,
            DataCenter = snapshot.DataCenter,
            ProjectItemCount = snapshot.ProjectItems.Count
        };

    private static async Task<CoreStoredPlanSnapshot?> ReadSnapshotAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CoreStoredPlanSnapshot>(stream, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<bool> WriteSnapshotAsync(
        string path,
        CoreStoredPlanSnapshot snapshot,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Task<bool> DeleteFileAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private static string EncodePathSegment(string value)
    {
        return string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character.ToString()
                : $"_{(int)character:X4}"));
    }
}
