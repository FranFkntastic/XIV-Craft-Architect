using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public class FileCoreStoredPlanStoreTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ffxiv-ca-session-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveLoadListAndDeleteNamedSnapshots()
    {
        var store = CreateStore();
        var oldSnapshot = CreateSnapshot(
            "older/plan",
            "Older Plan",
            modifiedAt: DateTime.Parse("2026-06-01T10:00:00Z"));
        var newSnapshot = CreateSnapshot(
            "newer:plan",
            "Newer Plan",
            modifiedAt: DateTime.Parse("2026-06-01T11:00:00Z"));

        Assert.True(await store.SavePlanSnapshotAsync(oldSnapshot));
        Assert.True(await store.SavePlanSnapshotAsync(newSnapshot));

        var loaded = await store.LoadPlanSnapshotAsync("newer:plan");
        var summaries = await store.LoadPlanSummariesAsync();

        Assert.NotNull(loaded);
        Assert.Equal("Newer Plan", loaded.Name);
        Assert.Equal(["newer:plan", "older/plan"], summaries.Select(summary => summary.Id));
        Assert.Equal(1, summaries[0].ProjectItemCount);

        Assert.True(await store.DeletePlanSnapshotAsync("older/plan"));
        Assert.False(await store.DeletePlanSnapshotAsync("older/plan"));
        Assert.Null(await store.LoadPlanSnapshotAsync("older/plan"));
    }

    [Fact]
    public async Task AutoSaveSlot_IsIndependentFromNamedPlanSnapshots()
    {
        var store = CreateStore();
        await store.SavePlanSnapshotAsync(CreateSnapshot("named", "Named Plan"));
        await store.SaveAutoSaveAsync(CreateSnapshot("autosave", "Autosave"));

        var summaries = await store.LoadPlanSummariesAsync();
        var autosave = await store.LoadAutoSaveAsync();

        Assert.Single(summaries);
        Assert.Equal("named", summaries[0].Id);
        Assert.NotNull(autosave);
        Assert.Equal("autosave", autosave.Id);
        Assert.True(await store.DeleteAutoSaveAsync());
        Assert.Null(await store.LoadAutoSaveAsync());
        Assert.Single(await store.LoadPlanSummariesAsync());
    }

    [Fact]
    public async Task CorruptNamedSnapshot_IsSkippedInsteadOfBreakingSummaries()
    {
        var store = CreateStore();
        await store.SavePlanSnapshotAsync(CreateSnapshot("valid", "Valid Plan"));
        var corruptDirectory = Path.Combine(_rootDirectory, "plans");
        Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "corrupt.json"), "{ not json");

        var summaries = await store.LoadPlanSummariesAsync();
        var corrupt = await store.LoadPlanSnapshotAsync("corrupt");

        Assert.Single(summaries);
        Assert.Equal("valid", summaries[0].Id);
        Assert.Null(corrupt);
    }

    [Fact]
    public void AddCraftSessionFoundation_RegistersFileBackedStore()
    {
        var provider = new ServiceCollection()
            .AddCraftSessionFoundation()
            .BuildServiceProvider();

        Assert.IsType<FileCoreStoredPlanStore>(provider.GetRequiredService<ICoreStoredPlanStore>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private FileCoreStoredPlanStore CreateStore() =>
        new(new CoreStoredPlanStoreOptions(_rootDirectory));

    private static CoreStoredPlanSnapshot CreateSnapshot(
        string id,
        string name,
        DateTime? modifiedAt = null)
    {
        var timestamp = modifiedAt ?? DateTime.Parse("2026-06-01T12:00:00Z");
        return new CoreStoredPlanSnapshot
        {
            Id = id,
            Name = name,
            DataCenter = "Aether",
            CreatedAt = timestamp,
            ModifiedAt = timestamp,
            SavedAt = timestamp,
            ProjectItems =
            [
                new CoreStoredProjectItem
                {
                    Id = 100,
                    Name = "Final Craft",
                    Quantity = 1,
                    MustBeHq = true
                }
            ]
        };
    }
}
