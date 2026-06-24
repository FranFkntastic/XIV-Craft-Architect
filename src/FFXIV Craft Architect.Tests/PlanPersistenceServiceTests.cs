using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.Tests;

public class PlanPersistenceServiceTests
{
    [Fact]
    public async Task SaveAndLoadPlanAsync_IgnoresLegacyCompanionMarketEvidenceFile()
    {
        var service = new PlanPersistenceService(NullLogger<PlanPersistenceService>.Instance);
        var planName = $"csv-removed-{Guid.NewGuid():N}";
        var plansDirectory = service.GetPlansDirectory();
        var jsonPath = Path.Combine(plansDirectory, $"{planName}.json");
        var csvPath = Path.Combine(plansDirectory, $"{planName}.recommendations" + ".csv");

        try
        {
            File.Delete(jsonPath);
            File.Delete(csvPath);

            var plan = new CraftingPlan
            {
                Name = planName,
                SavedMarketPlans =
                [
                    new DetailedShoppingPlan
                    {
                        ItemId = 1,
                        Name = "Legacy Market Evidence",
                        QuantityNeeded = 1
                    }
                ]
            };

            var saved = await service.SavePlanAsync(plan);
            Assert.True(saved);
            Assert.True(File.Exists(jsonPath));
            Assert.False(File.Exists(csvPath));

            await File.WriteAllTextAsync(csvPath, "stale legacy csv");
            var loaded = await service.LoadPlanAsync(jsonPath);

            Assert.NotNull(loaded);
            Assert.Empty(loaded.SavedMarketPlans);
        }
        finally
        {
            File.Delete(jsonPath);
            File.Delete(csvPath);
        }
    }
}
