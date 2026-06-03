using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public class CoreStoredPlanSnapshotTests
{
    [Fact]
    public void Build_CapturesCoreSessionPlanProjectItemsAndMarketEvidence()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = CreatePlan();
        session.ActivatePlan(
            plan,
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1, MustBeHq = true }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "plan loaded",
            new CraftSessionIdentity(Guid.NewGuid(), "Named Plan", "source-id", "Source Plan"));
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 100, Name = "Final Craft", QuantityNeeded = 1 }],
            [CreateShoppingPlan(100)],
            acquisitionDecisionsChanged: false,
            "market analysis",
            [404],
            RecommendationMode.MaximizeValue,
            MarketAcquisitionLens.BulkValue,
            CreateStoredRecipeBasis()));
        var builder = new CoreStoredPlanSnapshotBuilder(session);

        var snapshot = builder.Build(
            "autosave",
            "Autosave",
            DateTime.Parse("2026-06-01T12:00:00Z"),
            includeSourcePlanIdentity: true);

        Assert.Equal("autosave", snapshot.Id);
        Assert.Equal("Autosave", snapshot.Name);
        Assert.Equal(CoreStoredPlanSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal("Aether", snapshot.DataCenter);
        Assert.Single(snapshot.ProjectItems);
        Assert.Contains(404, snapshot.UnavailableMarketItemIds);
        Assert.Equal(RecommendationMode.MaximizeValue, snapshot.SavedRecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, snapshot.SavedMarketAnalysisLens);
        Assert.Equal("source-id", snapshot.SourcePlanId);
        Assert.NotNull(JsonSerializer.Deserialize<CraftingPlan>(snapshot.PlanJson!));
        Assert.Single(JsonSerializer.Deserialize<List<MarketItemAnalysis>>(snapshot.MarketItemAnalysesJson!)!);
        Assert.Single(JsonSerializer.Deserialize<List<DetailedShoppingPlan>>(snapshot.MarketPlansJson!)!);
        Assert.NotNull(snapshot.MarketAnalysisRecipeBasisJson);
        Assert.Single(JsonSerializer.Deserialize<StoredRecipeOperationSnapshot>(
            snapshot.MarketAnalysisRecipeBasisJson!)!.MarketAnalysisDemandItems);
    }

    [Fact]
    public void Prepare_WithNewerSnapshotVersion_ReturnsRecoverableWarningWithoutPayload()
    {
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "future",
            Name = "Future Plan",
            SchemaVersion = CoreStoredPlanSnapshot.CurrentSchemaVersion + 1,
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }
            ],
            PlanJson = JsonSerializer.Serialize(CreatePlan())
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.False(result.CanLoad);
        Assert.Contains("newer", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Plan);
        Assert.Empty(result.ProjectItems);
        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.ShoppingPlans);
    }

    [Fact]
    public void Prepare_WithOlderSnapshotVersion_LoadsWithRecoverableWarning()
    {
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "legacy",
            Name = "Legacy Plan",
            SchemaVersion = 0,
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }
            ],
            PlanJson = JsonSerializer.Serialize(CreatePlan())
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.True(result.CanLoad);
        Assert.Contains("older", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Plan);
        Assert.Single(result.ProjectItems);
    }

    [Fact]
    public void Load_WithNewerSnapshotVersion_DoesNotReplaceActiveSession()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "existing plan");
        var loader = new CorePlanSessionLoadService(session);
        var futureSnapshot = new CoreStoredPlanSnapshot
        {
            Id = "future",
            Name = "Future Plan",
            SchemaVersion = CoreStoredPlanSnapshot.CurrentSchemaVersion + 1,
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 999, Name = "Future Item", Quantity = 1 }
            ],
            PlanJson = JsonSerializer.Serialize(new CraftingPlan
            {
                Name = "Future Plan",
                RootItems =
                [
                    new PlanNode { ItemId = 999, Name = "Future Item", Quantity = 1 }
                ]
            })
        };

        var result = loader.Load(futureSnapshot);

        Assert.False(result.CanLoad);
        Assert.Equal("Plan", session.ActivePlan?.Name);
        Assert.Equal(100, Assert.Single(session.ProjectItems).Id);
    }

    [Fact]
    public void Prepare_DropsStoredMarketEvidenceWhenItDoesNotMatchProjectItems()
    {
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "saved",
            Name = "Saved",
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }
            ],
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 100, Name = "Final Craft", QuantityNeeded = 99 }
            }),
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                CreateShoppingPlan(100, quantityNeeded: 99)
            })
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.Single(result.ProjectItems);
        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.ShoppingPlans);
    }

    [Fact]
    public void Prepare_RecipeBasisQuantityMatch_RestoresAnalysisWhenProjectQuantityDiffers()
    {
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "saved",
            Name = "Saved",
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 99 }
            ],
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 100, Name = "Final Craft", QuantityNeeded = 1 }
            }),
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                CreateShoppingPlan(100)
            }),
            MarketAnalysisRecipeBasisJson = JsonSerializer.Serialize(CreateStoredRecipeBasis())
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.Single(result.MarketItemAnalyses);
        Assert.Single(result.ShoppingPlans);
        Assert.NotNull(result.MarketAnalysisRecipeBasis);
    }

    [Fact]
    public void Prepare_InvalidRecipeBasis_DropsMarketEvidenceEvenIfLegacyWouldMatch()
    {
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "saved",
            Name = "Saved",
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }
            ],
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 100, Name = "Final Craft", QuantityNeeded = 1 }
            }),
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                CreateShoppingPlan(100)
            }),
            MarketAnalysisRecipeBasisJson = "{not json}"
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.ShoppingPlans);
        Assert.Null(result.MarketAnalysisRecipeBasis);
        Assert.Contains("recipe basis", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_DuplicateRecipeBasisDemand_DropsMarketEvidenceWithoutThrowing()
    {
        var recipeBasis = CreateStoredRecipeBasis();
        recipeBasis.MarketAnalysisDemandItems.Add(new StoredMarketAnalysisDemandItem
        {
            ItemId = 100,
            Name = "Duplicate",
            TotalQuantity = 1
        });
        var snapshot = new CoreStoredPlanSnapshot
        {
            Id = "saved",
            Name = "Saved",
            ProjectItems =
            [
                new CoreStoredProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }
            ],
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 100, Name = "Final Craft", QuantityNeeded = 1 }
            }),
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                CreateShoppingPlan(100)
            }),
            MarketAnalysisRecipeBasisJson = JsonSerializer.Serialize(recipeBasis)
        };

        var result = CorePlanSessionLoadService.Prepare(snapshot);

        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.ShoppingPlans);
        Assert.Null(result.MarketAnalysisRecipeBasis);
    }

    [Fact]
    public void Load_ActivatesPlanAndRestoresValidMarketEvidenceIntoCoreSession()
    {
        var sourceSession = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        sourceSession.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "plan loaded");
        Assert.True(sourceSession.TryPublishMarketAnalysis(
            sourceSession.CaptureVersionStamp(),
            sourceSession.ActivePlan!,
            sourceSession.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 100, Name = "Final Craft", QuantityNeeded = 1 }],
            [CreateShoppingPlan(100)],
            acquisitionDecisionsChanged: false,
            "market analysis",
            recommendationMode: RecommendationMode.MaximizeValue,
            lens: MarketAcquisitionLens.BulkValue,
            recipeBasis: CreateStoredRecipeBasis()));
        var snapshot = new CoreStoredPlanSnapshotBuilder(sourceSession)
            .Build("saved-plan", "Saved Plan");
        var targetSession = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var loader = new CorePlanSessionLoadService(targetSession);

        var result = loader.Load(snapshot);

        Assert.Null(result.Warning);
        Assert.Equal("Saved Plan", targetSession.Identity.SourcePlanName);
        Assert.Equal("saved-plan", targetSession.Identity.SourcePlanId);
        Assert.Equal("Final Craft", targetSession.ActivePlan?.RootItems.Single().Name);
        Assert.Single(targetSession.ProjectItems);
        Assert.Equal(100, Assert.Single(targetSession.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(100, Assert.Single(targetSession.MarketEvidence.ShoppingPlans!).ItemId);
        Assert.NotNull(targetSession.MarketEvidence.RecipeBasis);
        Assert.Equal(RecommendationMode.MaximizeValue, targetSession.MarketEvidence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, targetSession.MarketEvidence.Lens);
    }

    [Fact]
    public void AddCraftSessionFoundation_RegistersStoredSessionContracts()
    {
        var provider = new ServiceCollection()
            .AddCraftSessionFoundation()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<CoreStoredPlanSnapshotBuilder>());
        Assert.NotNull(provider.GetRequiredService<CorePlanSessionLoadService>());
    }

    private static CraftingPlan CreatePlan()
    {
        return new CraftingPlan
        {
            Name = "Plan",
            DataCenter = "Aether",
            World = "Siren",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Final Craft",
                    Quantity = 1,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanCraft = false,
                    CanBuyFromMarket = true
                }
            ]
        };
    }

    private static DetailedShoppingPlan CreateShoppingPlan(int itemId, int quantityNeeded = 1)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            QuantityNeeded = quantityNeeded,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 1000,
                TotalQuantityPurchased = quantityNeeded
            }
        };
    }

    private static StoredRecipeOperationSnapshot CreateStoredRecipeBasis()
    {
        return new StoredRecipeOperationSnapshot
        {
            Operations =
            [
                new StoredRecipeOperation
                {
                    NodeId = "root",
                    ResultItemId = 100,
                    ResultItemName = "Final Craft"
                }
            ],
            MarketAnalysisDemandItems =
            [
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 100,
                    Name = "Final Craft",
                    TotalQuantity = 1
                }
            ]
        };
    }
}
