using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AppStatePersistenceTests
{
    [Fact]
    public void LoadStoredPlan_RestoresMarketAnalysisSourceAndLens()
    {
        var appState = new AppState
        {
            MarketAnalysisLens = MarketAcquisitionLens.MinimumUpfrontCost,
            RecommendationMode = RecommendationMode.MinimizeTotalCost,
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan
                {
                    ItemId = 999,
                    Name = "Stale Procurement Item"
                }
            ]
        };
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 123,
                Name = "Test Item",
                QuantityNeeded = 10
            }
        };
        var analyses = new List<MarketItemAnalysis>
        {
            new()
            {
                ItemId = 123,
                Name = "Test Item",
                QuantityNeeded = 10,
                Worlds =
                [
                    new WorldMarketAnalysis
                    {
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        QuantityNeeded = 10,
                        Scores =
                        [
                            new WorldLensScore
                            {
                                Lens = MarketAcquisitionLens.BulkValue,
                                Score = 100,
                                Rank = 1,
                                ScoreBucket = MarketScoreBucket.Optimal
                            }
                        ]
                    }
                ]
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "autosave",
            Name = "AutoSave",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Test Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = JsonSerializer.Serialize(analyses),
            SavedRecommendationMode = RecommendationMode.MaximizeValue,
            SavedMarketAnalysisLens = MarketAcquisitionLens.BulkValue
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Equal(MarketAcquisitionLens.BulkValue, appState.MarketAnalysisLens);
        Assert.Equal(RecommendationMode.MaximizeValue, appState.RecommendationMode);
        Assert.Single(appState.ShoppingPlans);
        var restoredAnalysis = Assert.Single(appState.MarketItemAnalyses);
        Assert.Equal(123, restoredAnalysis.ItemId);
        Assert.Equal("Siren", Assert.Single(restoredAnalysis.Worlds).WorldName);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public void LoadStoredPlan_LegacyMarketPlansWithoutAnalysisSource_ClearsProjectionAndDefaultLens()
    {
        var appState = new AppState
        {
            MarketAnalysisLens = MarketAcquisitionLens.BulkValue
        };
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 123,
                Name = "Legacy Item",
                QuantityNeeded = 10
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "legacy",
            Name = "Legacy",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Legacy Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = null
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Equal(MarketAcquisitionLens.MinimumUpfrontCost, appState.MarketAnalysisLens);
    }

    [Fact]
    public void LoadStoredPlan_InvalidAnalysisJson_ClearsShoppingPlanProjection()
    {
        var appState = new AppState();
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 123,
                Name = "Restored Item",
                QuantityNeeded = 10
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "autosave",
            Name = "AutoSave",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Restored Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = "{not valid json}"
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
    }

    [Fact]
    public void LoadStoredPlan_AnalysisForDifferentPlan_ClearsAnalysisAndProjection()
    {
        var appState = new AppState();
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 999,
                Name = "Wrong Item",
                QuantityNeeded = 10
            }
        };
        var analyses = new List<MarketItemAnalysis>
        {
            new()
            {
                ItemId = 999,
                Name = "Wrong Item",
                QuantityNeeded = 10
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "plan",
            Name = "Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = JsonSerializer.Serialize(analyses)
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
    }

    [Fact]
    public void LoadStoredPlan_AnalysisForSameItemDifferentQuantity_ClearsAnalysisAndProjection()
    {
        var appState = new AppState();
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 123,
                Name = "Current Item",
                QuantityNeeded = 5
            }
        };
        var analyses = new List<MarketItemAnalysis>
        {
            new()
            {
                ItemId = 123,
                Name = "Current Item",
                QuantityNeeded = 5
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "plan",
            Name = "Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = JsonSerializer.Serialize(analyses)
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
    }

    [Fact]
    public void LoadStoredPlan_ProjectionForDifferentAnalysis_ClearsProjectionButKeepsAnalysis()
    {
        var appState = new AppState();
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 999,
                Name = "Wrong Item",
                QuantityNeeded = 10
            }
        };
        var analyses = new List<MarketItemAnalysis>
        {
            new()
            {
                ItemId = 123,
                Name = "Current Item",
                QuantityNeeded = 10
            }
        };
        var storedPlan = new StoredPlan
        {
            Id = "plan",
            Name = "Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = JsonSerializer.Serialize(analyses)
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.ShoppingPlans);
        Assert.Single(appState.MarketItemAnalyses);
    }

    [Fact]
    public void LoadStoredPlan_RebuildsShoppingItemsFromLoadedPlan()
    {
        var appState = new AppState
        {
            ShoppingItems =
            [
                new MarketShoppingItem
                {
                    Id = 999,
                    Name = "Previous Item",
                    Quantity = 99
                }
            ]
        };
        var storedPlan = new StoredPlan
        {
            Id = "plan",
            Name = "Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ]
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        var shoppingItem = Assert.Single(appState.ShoppingItems);
        Assert.Equal(123, shoppingItem.Id);
        Assert.Equal("Current Item", shoppingItem.Name);
        Assert.Equal(10, shoppingItem.Quantity);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_PreservesMarketAnalysisAndLens()
    {
        var appState = new AppState
        {
            CurrentPlan = new CraftingPlan
            {
                Name = "Snapshot Plan",
                DataCenter = "Aether"
            },
            CurrentPlanId = "named-plan",
            CurrentPlanName = "Named Plan",
            SelectedDataCenter = "Aether",
            RecommendationMode = RecommendationMode.MaximizeValue,
            MarketAnalysisLens = MarketAcquisitionLens.BulkValue,
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 123,
                    Name = "Snapshot Item",
                    Quantity = 10
                }
            ],
            ShoppingPlans =
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ],
            MarketItemAnalyses =
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]
        };

        var snapshot = appState.CreateStoredPlanSnapshot(
            "autosave",
            "AutoSave",
            includeSourcePlanIdentity: true);

        Assert.NotNull(snapshot.PlanJson);
        Assert.NotNull(snapshot.MarketPlansJson);
        Assert.NotNull(snapshot.MarketItemAnalysesJson);
        Assert.Equal(RecommendationMode.MaximizeValue, snapshot.SavedRecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, snapshot.SavedMarketAnalysisLens);
        Assert.Equal("named-plan", snapshot.SourcePlanId);
        Assert.Equal("Named Plan", snapshot.SourcePlanName);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_AfterAnalysisCleared_WritesNullMarketAnalysisFields()
    {
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 123,
                    Name = "Snapshot Item",
                    Quantity = 10
                }
            ],
            ShoppingPlans =
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ],
            MarketItemAnalyses =
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]
        };

        appState.ClearMarketAnalysisState();
        var snapshot = appState.CreateStoredPlanSnapshot("plan", "Plan");

        Assert.Null(snapshot.MarketPlansJson);
        Assert.Null(snapshot.MarketItemAnalysesJson);
    }

    [Fact]
    public void LoadStoredPlan_AutosaveRestoreUsesSourcePlanIdentity()
    {
        var appState = new AppState();
        var storedPlan = new StoredPlan
        {
            Id = "autosave",
            Name = "AutoSave",
            SourcePlanId = "named-plan",
            SourcePlanName = "Named Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Restored Item",
                    Quantity = 10
                }
            ]
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null, trackStoredPlanIdentity: false);

        Assert.Equal("named-plan", appState.CurrentPlanId);
        Assert.Equal("Named Plan", appState.CurrentPlanName);
    }

    [Fact]
    public void ClearMarketAnalysisState_RemovesAnalysisProjectionAndProcurementOverlay()
    {
        var appState = new AppState
        {
            ShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 1, Name = "Market" }
            ],
            MarketItemAnalyses =
            [
                new MarketItemAnalysis { ItemId = 1, Name = "Market" }
            ],
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 1, Name = "Procurement" }
            ]
        };

        appState.ClearMarketAnalysisState();

        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }
}
