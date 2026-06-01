using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AppStatePersistenceTests
{
    [Fact]
    public void LoadStoredPlan_RestoresMarketAnalysisSourceAndLens()
    {
        var appState = new AppState();
        appState.ReplaceProcurementOverlay(
        [
            new DetailedShoppingPlan
            {
                ItemId = 999,
                Name = "Stale Procurement Item"
            }
        ]);
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
    public void LoadStoredPlan_RestoresScopeAwareMarketAnalysisFields()
    {
        var appState = new AppState();
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
                    Name = "Scope Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new()
                {
                    ItemId = 123,
                    Name = "Scope Item",
                    QuantityNeeded = 10
                }
            }),
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new()
                {
                    ItemId = 123,
                    Name = "Scope Item",
                    QuantityNeeded = 10,
                    AnalysisScopeBaselineUnitPrice = 640,
                    AnalysisScopeAverageUnitPrice = 650,
                    AnalysisScopeMedianUnitPrice = 630,
                    SaneThresholdUnitPrice = 1280,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            QuantityNeeded = 10,
                            AnalysisScopeBaselineUnitPrice = 640,
                            AnalysisScopeAverageUnitPrice = 650,
                            AnalysisScopeMedianUnitPrice = 630,
                            SaneThresholdUnitPrice = 1280,
                            LocalCompetitiveQuantity = 2,
                            ScopeCompetitiveQuantity = 8,
                            ScopeSaneQuantity = 10,
                            ScopeInsaneQuantity = 3,
                            ScopeCompetitiveCoverageRatio = 0.8m,
                            ScopeSaneCoverageRatio = 1.0m
                        }
                    ]
                }
            })
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        var analysis = Assert.Single(appState.MarketItemAnalyses);
        Assert.Equal(640, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(650, analysis.AnalysisScopeAverageUnitPrice);
        Assert.Equal(630, analysis.AnalysisScopeMedianUnitPrice);
        Assert.Equal(1280, analysis.SaneThresholdUnitPrice);

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(640, world.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(650, world.AnalysisScopeAverageUnitPrice);
        Assert.Equal(630, world.AnalysisScopeMedianUnitPrice);
        Assert.Equal(1280, world.SaneThresholdUnitPrice);
        Assert.Equal(2, world.LocalCompetitiveQuantity);
        Assert.Equal(8, world.ScopeCompetitiveQuantity);
        Assert.Equal(10, world.ScopeSaneQuantity);
        Assert.Equal(3, world.ScopeInsaneQuantity);
        Assert.Equal(0.8m, world.ScopeCompetitiveCoverageRatio);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
    }

    [Fact]
    public void LoadStoredPlan_LegacyAnalysisMissingScopeAwareFields_LoadsDefaults()
    {
        var appState = new AppState();
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
            MarketPlansJson = """
                [
                  {
                    "ItemId": 123,
                    "Name": "Legacy Item",
                    "QuantityNeeded": 10
                  }
                ]
                """,
            MarketItemAnalysesJson = """
                [
                  {
                    "ItemId": 123,
                    "Name": "Legacy Item",
                    "QuantityNeeded": 10,
                    "Worlds": [
                      {
                        "DataCenter": "Aether",
                        "WorldName": "Siren",
                        "QuantityNeeded": 10
                      }
                    ]
                  }
                ]
                """
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        var analysis = Assert.Single(appState.MarketItemAnalyses);
        Assert.Equal(0, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(0, analysis.AnalysisScopeAverageUnitPrice);
        Assert.Equal(0, analysis.AnalysisScopeMedianUnitPrice);
        Assert.Equal(0, analysis.SaneThresholdUnitPrice);

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(0, world.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(0, world.AnalysisScopeAverageUnitPrice);
        Assert.Equal(0, world.AnalysisScopeMedianUnitPrice);
        Assert.Equal(0, world.SaneThresholdUnitPrice);
        Assert.Equal(0, world.ScopeCompetitiveQuantity);
        Assert.Equal(0, world.ScopeSaneQuantity);
        Assert.Equal(0, world.ScopeInsaneQuantity);
    }

    [Fact]
    public void LoadStoredPlan_ClearsMarketAnalysisViewState()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Existing Item",
                    QuantityNeeded = 10,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren"
                        }
                    ]
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Existing Item",
                    QuantityNeeded = 10
                }
            ]);
        appState.SelectMarketAnalysisItem(123);
        appState.ToggleMarketAnalysisWorld(123, "Aether", "Siren");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
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
                    Name = "Loaded Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new()
                {
                    ItemId = 123,
                    Name = "Loaded Item",
                    QuantityNeeded = 10
                }
            }),
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new()
                {
                    ItemId = 123,
                    Name = "Loaded Item",
                    QuantityNeeded = 10,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren"
                        }
                    ]
                }
            })
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Null(appState.SelectedMarketAnalysisItemId);
        Assert.Empty(appState.ExpandedMarketAnalysisWorlds);
        Assert.Null(appState.MarketAnalysisGridSortColumn);
        Assert.False(appState.MarketAnalysisGridSortDescending);
    }

    [Fact]
    public void LoadStoredPlan_LegacyMarketPlansWithoutAnalysisSource_ClearsProjectionAndDefaultLens()
    {
        var appState = new AppState();
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
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
        var appState = new AppState();
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
        var appState = new AppState();
        appState.SetRecommendationMode(RecommendationMode.MaximizeValue);
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan
        {
            Name = "Snapshot Plan",
            DataCenter = "Aether"
        });
        appState.TrackCurrentPlanIdentity("named-plan", "Named Plan");
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]);

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
    public void CreateStoredPlanSnapshot_RoundTripPreservesScopeAwareMarketAnalysisFields()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10,
                    AnalysisScopeBaselineUnitPrice = 640,
                    AnalysisScopeAverageUnitPrice = 650,
                    AnalysisScopeMedianUnitPrice = 630,
                    SaneThresholdUnitPrice = 1280,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            QuantityNeeded = 10,
                            AnalysisScopeBaselineUnitPrice = 640,
                            AnalysisScopeAverageUnitPrice = 650,
                            AnalysisScopeMedianUnitPrice = 630,
                            SaneThresholdUnitPrice = 1280,
                            LocalCompetitiveQuantity = 2,
                            ScopeCompetitiveQuantity = 8,
                            ScopeSaneQuantity = 10,
                            ScopeInsaneQuantity = 3,
                            ScopeCompetitiveCoverageRatio = 0.8m,
                            ScopeSaneCoverageRatio = 1.0m
                        }
                    ]
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]);
        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var restored = new AppState();

        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        var analysis = Assert.Single(restored.MarketItemAnalyses);
        Assert.Equal(640, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(650, analysis.AnalysisScopeAverageUnitPrice);
        Assert.Equal(630, analysis.AnalysisScopeMedianUnitPrice);
        Assert.Equal(1280, analysis.SaneThresholdUnitPrice);

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(640, world.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(650, world.AnalysisScopeAverageUnitPrice);
        Assert.Equal(630, world.AnalysisScopeMedianUnitPrice);
        Assert.Equal(1280, world.SaneThresholdUnitPrice);
        Assert.Equal(2, world.LocalCompetitiveQuantity);
        Assert.Equal(8, world.ScopeCompetitiveQuantity);
        Assert.Equal(10, world.ScopeSaneQuantity);
        Assert.Equal(3, world.ScopeInsaneQuantity);
        Assert.Equal(0.8m, world.ScopeCompetitiveCoverageRatio);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
        Assert.Single(restored.ShoppingPlans);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_DoesNotPersistMarketAnalysisViewState()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren"
                        }
                    ]
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]);
        appState.SelectMarketAnalysisItem(123);
        appState.ToggleMarketAnalysisWorld(123, "Aether", "Siren");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var storedPlanJson = JsonSerializer.Serialize(snapshot);
        var marketPlansJson = snapshot.MarketPlansJson ?? string.Empty;
        var marketItemAnalysesJson = snapshot.MarketItemAnalysesJson ?? string.Empty;

        Assert.DoesNotContain(nameof(AppState.SelectedMarketAnalysisItemId), storedPlanJson);
        Assert.DoesNotContain(nameof(AppState.ExpandedMarketAnalysisWorlds), storedPlanJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortColumn), storedPlanJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortDescending), storedPlanJson);
        Assert.DoesNotContain(nameof(AppState.SelectedMarketAnalysisItemId), marketPlansJson);
        Assert.DoesNotContain(nameof(AppState.ExpandedMarketAnalysisWorlds), marketPlansJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortColumn), marketPlansJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortDescending), marketPlansJson);
        Assert.DoesNotContain(nameof(AppState.SelectedMarketAnalysisItemId), marketItemAnalysesJson);
        Assert.DoesNotContain(nameof(AppState.ExpandedMarketAnalysisWorlds), marketItemAnalysesJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortColumn), marketItemAnalysesJson);
        Assert.DoesNotContain(nameof(AppState.MarketAnalysisGridSortDescending), marketItemAnalysesJson);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_AfterAnalysisCleared_WritesNullMarketAnalysisFields()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10
                }
            ]);

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

}
