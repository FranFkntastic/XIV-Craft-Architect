using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class PackagedWorldDirectoryService
{
    private const string ResourceName = "FFXIV_Craft_Architect.Core.Data.world-directory.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PackagedWorldDirectorySnapshot LoadSnapshot()
    {
        var assembly = typeof(PackagedWorldDirectoryService).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The packaged world directory resource '{ResourceName}' is missing.");
        var snapshot = JsonSerializer.Deserialize<PackagedWorldDirectorySnapshot>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The packaged world directory is empty.");

        Validate(snapshot);
        return snapshot;
    }

    public WorldData LoadWorldData()
    {
        var snapshot = LoadSnapshot();
        return new WorldData
        {
            WorldIdToName = snapshot.WorldIdToName.ToDictionary(pair => pair.Key, pair => pair.Value),
            DataCenterToWorlds = snapshot.DataCenterToWorlds.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void Validate(PackagedWorldDirectorySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported packaged world directory schema {snapshot.SchemaVersion}.");
        }

        if (snapshot.WorldIdToName.Count < 80 || snapshot.DataCenterToWorlds.Count < 10)
        {
            throw new InvalidOperationException("The packaged world directory is incomplete.");
        }

        var knownWorldNames = snapshot.WorldIdToName.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownWorld = snapshot.DataCenterToWorlds.Values
            .SelectMany(worlds => worlds)
            .FirstOrDefault(world => !knownWorldNames.Contains(world));
        if (unknownWorld != null)
        {
            throw new InvalidOperationException(
                $"The packaged data-center map references unknown world '{unknownWorld}'.");
        }
    }
}

public sealed record PackagedWorldDirectorySnapshot(
    int SchemaVersion,
    DateTime GeneratedAtUtc,
    string Source,
    IReadOnlyDictionary<int, string> WorldIdToName,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DataCenterToWorlds);
