using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class NativePlanExportServiceTests
{
    [Fact]
    public void GenerateNativeJson_ExportsStoredPlanSnapshotWithMarketIntelligence()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems(
        [
            new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 2, MustBeHq = true }
        ]);
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan
        {
            Name = "Exported Plan",
            DataCenter = "Aether",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Final Craft",
                    Quantity = 2,
                    MustBeHq = true,
                    Source = AcquisitionSource.Craft
                }
            ]
        });
        appState.SetRecommendationMode(RecommendationMode.MaximizeValue);
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Final Craft", QuantityNeeded = 2 }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Final Craft", QuantityNeeded = 2 }],
            publishedScope: appState.CreateCurrentMarketAnalysisScopeSnapshot(DateTime.UtcNow));
        var service = new NativePlanExportService(new StoredPlanSnapshotBuilder(appState));

        var json = service.GenerateNativeJson("export-id", "Exported Plan");
        var exported = JsonSerializer.Deserialize<StoredPlan>(json);

        Assert.NotNull(exported);
        Assert.Equal("export-id", exported.Id);
        Assert.Equal("Exported Plan", exported.Name);
        Assert.NotNull(exported.PlanJson);
        Assert.Single(exported.ProjectItems);
        Assert.NotNull(exported.MarketIntelligenceJson);
        Assert.NotNull(exported.MarketAnalysisScopeSnapshotJson);
        Assert.DoesNotContain("\"Nodes\"", json);
    }

    [Fact]
    public void Import_StoredPlanNativeJson_ReturnsStoredPlanPayload()
    {
        var storedPlan = new StoredPlan
        {
            Id = "stored",
            Name = "Stored Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 2, MustBeHq = true }
            ],
            PlanJson = JsonSerializer.Serialize(new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode { ItemId = 100, Name = "Final Craft", Quantity = 2 }
                ]
            })
        };
        var service = new NativePlanImportService(new RecipeCalculationService(
            new GarlandService(new HttpClient()),
            Mock.Of<IVendorCacheService>()));

        var result = service.Import(JsonSerializer.Serialize(storedPlan));

        Assert.NotNull(result.StoredPlan);
        Assert.Null(result.Plan);
        Assert.Equal("stored", result.StoredPlan.Id);
        var item = Assert.Single(result.Items);
        Assert.Equal(100, item.Id);
        Assert.True(item.MustBeHq);
    }
}
