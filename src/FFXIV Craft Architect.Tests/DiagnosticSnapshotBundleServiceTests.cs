using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class DiagnosticSnapshotBundleServiceTests
{
    [Fact]
    public void BuildBundle_CapturesPlanMarketAcquisitionProcurementAndContextState()
    {
        var appState = new AppState();
        var plan = CreatePlan();
        appState.SetMarketEvidenceSettings(
            "Crystal",
            "North America",
            MarketFetchScope.SelectedDataCenter,
            searchEntireRegion: false);
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        appState.ReplaceProjectItems([new ProjectItem { Id = 100, Name = "Final Part", Quantity = 2 }]);
        appState.TrackCurrentPlanIdentity("plan-123", "Stored Diagnostic Plan");
        appState.SetRecommendationMode(RecommendationMode.MaximizeValue);
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 200,
                    Name = "Market Material",
                    QuantityNeeded = 10
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 200,
                    Name = "Market Material",
                    QuantityNeeded = 10,
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        DataCenter = "Crystal",
                        WorldName = "Coeurl",
                        TotalCost = 1_230,
                        TotalQuantityPurchased = 10
                    }
                }
            ]);
        appState.ReplaceProcurementOverlay(
            [
                new DetailedShoppingPlan
                {
                    ItemId = 200,
                    Name = "Market Material",
                    QuantityNeeded = 10
                }
            ]);
        appState.SetStatus("Ready for diagnostic export");
        var service = CreateService(appState);
        var exportedAt = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

        var bundle = service.BuildBundle(exportedAt);

        Assert.Equal(1, bundle.SchemaVersion);
        Assert.Equal("craft-architect-diagnostic-snapshot", bundle.Tool);
        Assert.Equal(exportedAt, bundle.ExportedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(bundle.Build.BuildVersion));
        Assert.Equal("plan-123", bundle.Context.CurrentPlanId);
        Assert.Equal("Stored Diagnostic Plan", bundle.Context.CurrentPlanName);
        Assert.Equal("Crystal", bundle.Context.SelectedDataCenter);
        Assert.False(bundle.Context.SearchEntireRegion);
        Assert.Equal(RecommendationMode.MaximizeValue, bundle.Context.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, bundle.Context.MarketAnalysisLens);
        Assert.Equal("Ready for diagnostic export", bundle.Context.StatusMessage);
        Assert.NotNull(bundle.StoredPlan.PlanJson);
        Assert.NotNull(bundle.StoredPlan.MarketIntelligenceJson);
        Assert.Single(bundle.ShoppingPlans);
        Assert.Single(bundle.ProcurementShoppingPlans);
        Assert.Single(bundle.MarketItemAnalyses);
        Assert.Contains(bundle.AcquisitionRows, row => row.ItemId == 200);
        Assert.Contains(bundle.MarketAnalysisCandidates, item => item.ItemId == 200);
        Assert.Contains(bundle.ActiveProcurementItems, item => item.ItemId == 200);
    }

    [Fact]
    public void Serialize_UsesIndentedCamelCaseJsonAndStringEnums()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan
        {
            Name = "Serialization Plan",
            DataCenter = "Aether",
            RootItems = []
        });
        var service = CreateService(appState);
        var bundle = service.BuildBundle(new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc));

        var json = service.Serialize(bundle);

        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"tool\": \"craft-architect-diagnostic-snapshot\"", json);
        Assert.Contains("\"marketAnalysisLens\": \"MinimumUpfrontCost\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("craft-architect-diagnostic-snapshot", document.RootElement.GetProperty("tool").GetString());
    }

    private static DiagnosticSnapshotBundleService CreateService(AppState appState)
    {
        return new DiagnosticSnapshotBundleService(
            appState,
            new StoredPlanSnapshotBuilder(appState),
            new StubRecipeLayerWorkflowService());
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Part",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var material = new PlanNode
        {
            ItemId = 200,
            Name = "Market Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 123,
            Parent = root
        };
        root.Children.Add(material);

        return new CraftingPlan
        {
            Name = "Diagnostic Plan",
            DataCenter = "Crystal",
            RootItems = [root]
        };
    }

    private sealed class StubRecipeLayerWorkflowService : IRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjectionService _projectionService = new();

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            return RecipeOperationSnapshotIdentity.Unspecified;
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
        {
            return _projectionService.Build(plan, snapshot: null);
        }

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
        {
            return BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();
        }

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan)
        {
            return BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();
        }

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
        }
    }
}
