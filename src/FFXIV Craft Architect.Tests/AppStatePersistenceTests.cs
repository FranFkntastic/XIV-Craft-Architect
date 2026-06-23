using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    public void LoadStoredPlan_NoRestoredMarketIntelligenceUsesSavedModeAndLens()
    {
        var appState = new AppState();
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.SetRecommendationMode(RecommendationMode.MinimizeTotalCost);
        var storedPlan = new StoredPlan
        {
            SavedRecommendationMode = RecommendationMode.MaximizeValue,
            SavedMarketAnalysisLens = MarketAcquisitionLens.MinimumUpfrontCost,
            MarketItemAnalysesJson = "{not valid json}",
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new() { ItemId = 123, Name = "Stale Projection", QuantityNeeded = 1 }
            })
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Equal(RecommendationMode.MaximizeValue, appState.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.MinimumUpfrontCost, appState.MarketAnalysisLens);
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
                            PrimaryUsableQuantity = 8,
                            ScopeSaneQuantity = 10,
                            ScopeInsaneQuantity = 3,
                            PrimaryUsableCoverageRatio = 0.8m,
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
        Assert.Equal(8, world.PrimaryUsableQuantity);
        Assert.Equal(10, world.ScopeSaneQuantity);
        Assert.Equal(3, world.ScopeInsaneQuantity);
        Assert.Equal(0.8m, world.PrimaryUsableCoverageRatio);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
    }

    [Fact]
    public void PlanSessionLoadService_Prepare_LegacyScopeSnapshotProjectsIntoMarketIntelligence()
    {
        var publishedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var storedPlan = new StoredPlan
        {
            SavedMarketAnalysisLens = MarketAcquisitionLens.BulkValue,
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
                new() { ItemId = 123, Name = "Scope Item", QuantityNeeded = 10 }
            }),
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 123, Name = "Scope Item", QuantityNeeded = 10 }
            }),
            MarketAnalysisScopeSnapshotJson = JsonSerializer.Serialize(new PublishedMarketAnalysisScopeSnapshot(
                MarketFetchScope.EntireRegion,
                "Aether",
                "North America",
                ["Aether", "Primal"],
                MarketAcquisitionLens.BulkValue,
                7,
                publishedAt))
        };

        var result = PlanSessionLoadService.Prepare(storedPlan);

        Assert.NotNull(result.MarketIntelligence);
        Assert.Equal(MarketIntelligencePublicationContextKind.Known, result.MarketIntelligence!.PublicationContext.Kind);
        Assert.Equal(MarketFetchScope.EntireRegion, result.MarketIntelligence.PublicationContext.Scope);
        Assert.Equal(["Aether", "Primal"], result.MarketIntelligence.PublicationContext.RequestedDataCenters);
        Assert.Equal(publishedAt, result.MarketIntelligence.PublicationContext.PublishedAtUtc);
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
        Assert.Equal(0, world.PrimaryUsableQuantity);
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
        var recipeBasis = CreateStoredRecipeBasis();
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
            ],
            recipeBasis);

        var snapshot = appState.CreateStoredPlanSnapshot(
            "autosave",
            "AutoSave",
            includeSourcePlanIdentity: true);

        Assert.NotNull(snapshot.PlanJson);
        Assert.NotNull(snapshot.MarketPlansJson);
        Assert.NotNull(snapshot.MarketItemAnalysesJson);
        Assert.NotNull(snapshot.MarketAnalysisRecipeBasisJson);
        Assert.Equal(RecommendationMode.MaximizeValue, snapshot.SavedRecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, snapshot.SavedMarketAnalysisLens);
        Assert.Equal("named-plan", snapshot.SourcePlanId);
        Assert.Equal("Named Plan", snapshot.SourcePlanName);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_WritesMarketIntelligenceJsonAlongsideLegacyFields()
    {
        var appState = new AppState();
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            publishedScope: appState.CreateCurrentMarketAnalysisScopeSnapshot(
                new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc)));

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");

        Assert.NotNull(snapshot.MarketIntelligenceJson);
        Assert.NotNull(snapshot.MarketPlansJson);
        Assert.NotNull(snapshot.MarketItemAnalysesJson);

        var storedIntelligence = JsonSerializer.Deserialize<StoredMarketIntelligence>(snapshot.MarketIntelligenceJson!);
        Assert.NotNull(storedIntelligence);
        Assert.NotEqual(Guid.Empty, storedIntelligence!.MarketIntelligenceId);
        Assert.Equal(MarketIntelligencePublicationContextKind.Known, storedIntelligence.PublicationContext.Kind);
        Assert.Equal(MarketAcquisitionLens.BulkValue, storedIntelligence.PublicationContext.Lens);
        Assert.Equal(123, Assert.Single(storedIntelligence.ItemAnalyses).ItemId);
        Assert.Equal(123, Assert.Single(storedIntelligence.Recommendations).ItemId);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_CanOmitLegacyFieldsWhenMarketIntelligenceJsonIsAvailable()
    {
        var appState = new AppState();
        appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        appState.ReplaceProjectItems(
            [new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            publishedScope: appState.CreateCurrentMarketAnalysisScopeSnapshot(
                new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc)));

        var snapshot = appState.CreateStoredPlanSnapshot(
            "autosave",
            "AutoSave",
            includeLegacyMarketAnalysisFields: false);

        Assert.NotNull(snapshot.MarketIntelligenceJson);
        Assert.Null(snapshot.MarketPlansJson);
        Assert.Null(snapshot.MarketItemAnalysesJson);

        var restored = PlanSessionLoadService.Prepare(snapshot);
        Assert.Equal(123, Assert.Single(restored.MarketItemAnalyses).ItemId);
        Assert.Equal(123, Assert.Single(restored.ShoppingPlans).ItemId);
    }

    [Fact]
    public void LoadStoredPlan_RestoresMarketIntelligenceJsonWithoutLegacyMarketFields()
    {
        var appState = new AppState();
        var publishedAtUtc = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);
        var intelligence = new MarketIntelligence(
            Guid.NewGuid(),
            [new MarketItemAnalysis { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [new CoreMarketDataUnavailableItem(456, "Missing Item")],
            new MarketIntelligencePublicationContext(
                MarketIntelligencePublicationContextKind.Known,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                ["Aether"],
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                null,
                false,
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.BulkValue,
                null,
                7,
                2,
                publishedAtUtc),
            CreateStoredRecipeBasis());
        var storedPlan = new StoredPlan
        {
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Snapshot Item",
                    Quantity = 10
                }
            ],
            MarketIntelligenceJson = JsonSerializer.Serialize(StoredMarketIntelligence.FromMarketIntelligence(intelligence)),
            MarketPlansJson = null,
            MarketItemAnalysesJson = null,
            MarketAnalysisRecipeBasisJson = null,
            MarketAnalysisScopeSnapshotJson = null
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Equal(123, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(123, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Equal(456, Assert.Single(appState.UnavailableMarketItems).ItemId);
        Assert.Equal(MarketAcquisitionLens.BulkValue, appState.MarketAnalysisLens);
        Assert.Equal(MarketIntelligencePublicationContextKind.Known, appState.MarketIntelligence.PublicationContext.Kind);
        Assert.Equal(publishedAtUtc, appState.PublishedMarketAnalysisScope?.PublishedAtUtc);
        Assert.NotNull(appState.MarketAnalysisRecipeBasis);
    }

    [Fact]
    public void LoadStoredPlan_MarketIntelligenceWithAnalysisButNoRecommendationsKeepsKnownScope()
    {
        var appState = new AppState();
        var publishedAtUtc = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);
        var intelligence = new MarketIntelligence(
            Guid.NewGuid(),
            [new MarketItemAnalysis { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [],
            [],
            new MarketIntelligencePublicationContext(
                MarketIntelligencePublicationContextKind.Known,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                ["Aether"],
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                null,
                false,
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.MinimumUpfrontCost,
                null,
                7,
                2,
                publishedAtUtc),
            null);
        var storedPlan = new StoredPlan
        {
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Snapshot Item",
                    Quantity = 10
                }
            ],
            MarketIntelligenceJson = JsonSerializer.Serialize(StoredMarketIntelligence.FromMarketIntelligence(intelligence))
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.Equal(123, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Equal(publishedAtUtc, appState.PublishedMarketAnalysisScope?.PublishedAtUtc);
        Assert.Equal(MarketIntelligencePublicationContextKind.Known, appState.MarketIntelligence.PublicationContext.Kind);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_WritesUnavailableOnlyMarketIntelligence()
    {
        var appState = new AppState();
        appState.SetUnavailableMarketItems([new CoreMarketDataUnavailableItem(456, "Missing Item")]);

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");

        Assert.NotNull(snapshot.MarketIntelligenceJson);
        var storedIntelligence = JsonSerializer.Deserialize<StoredMarketIntelligence>(snapshot.MarketIntelligenceJson!);
        Assert.Equal(456, Assert.Single(storedIntelligence!.UnavailableMarketItems).ItemId);

        var restored = new AppState();
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.Equal(456, Assert.Single(restored.UnavailableMarketItems).ItemId);
        Assert.Equal(456, Assert.Single(restored.MarketIntelligence.UnavailableMarketItems).ItemId);
    }

    [Fact]
    public void LoadStoredPlan_RestoresMarketAnalysisRecipeBasis()
    {
        var appState = new AppState();
        var recipeBasis = CreateStoredRecipeBasis();
        var storedPlan = new StoredPlan
        {
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Snapshot Item",
                    Quantity = 10
                }
            ],
            MarketPlansJson = JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new() { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }
            }),
            MarketItemAnalysesJson = JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }
            }),
            MarketAnalysisRecipeBasisJson = JsonSerializer.Serialize(recipeBasis)
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        Assert.NotNull(appState.MarketAnalysisRecipeBasis);
        Assert.Equal("root", Assert.Single(appState.MarketAnalysisRecipeBasis.Operations).NodeId);
        Assert.Equal(123, Assert.Single(appState.MarketAnalysisRecipeBasis.MarketAnalysisDemandItems).ItemId);
    }

    [Fact]
    public void ReplaceMarketAnalysis_ClonesRecipeBasisForPersistenceIsolation()
    {
        var appState = new AppState();
        var recipeBasis = CreateStoredRecipeBasis();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Snapshot Item", QuantityNeeded = 10 }],
            recipeBasis);

        recipeBasis.MarketAnalysisDemandItems[0].TotalQuantity = 99;
        appState.MarketAnalysisRecipeBasis!.MarketAnalysisDemandItems[0].TotalQuantity = 77;
        var snapshot = appState.CreateStoredPlanSnapshot("plan", "Plan");
        var persistedBasis = StoredRecipeBasisMapper.TryDeserialize(
            snapshot.MarketAnalysisRecipeBasisJson,
            out var warning);

        Assert.Null(warning);
        Assert.Equal(10, Assert.Single(persistedBasis!.MarketAnalysisDemandItems).TotalQuantity);
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
                            PrimaryUsableQuantity = 8,
                            ScopeSaneQuantity = 10,
                            ScopeInsaneQuantity = 3,
                            PrimaryUsableCoverageRatio = 0.8m,
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
        Assert.Equal(8, world.PrimaryUsableQuantity);
        Assert.Equal(10, world.ScopeSaneQuantity);
        Assert.Equal(3, world.ScopeInsaneQuantity);
        Assert.Equal(0.8m, world.PrimaryUsableCoverageRatio);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
        Assert.Single(restored.ShoppingPlans);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_RoundTripPreservesMarketPriceEvaluation()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Snapshot Item", Quantity = 10 }]);
        var evaluatedAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    QuantityNeeded = 10,
                    Scope = MarketFetchScope.SelectedDataCenter,
                    PriceEvaluation = new MarketPriceEvaluation
                    {
                        ItemId = 123,
                        Scope = MarketFetchScope.SelectedDataCenter,
                        QualityPolicy = MarketPriceQualityPolicy.DualChannel,
                        EvaluatedAtUtc = evaluatedAtUtc,
                        CentralRegion = new MarketCentralPriceRegion
                        {
                            MinUnitPrice = 100,
                            MaxUnitPrice = 120,
                            MedianUnitPrice = 110,
                            WeightedAverageUnitPrice = 112,
                            ListingCount = 3,
                            TotalQuantity = 99,
                            DistinctRetainerCount = 3,
                            DistinctWorldCount = 2,
                            DataQualityBucket = MarketDataQualityBucket.Current,
                            Credibility = MarketPriceRegionCredibility.Credible
                        },
                        Thresholds = new MarketPriceThresholds
                        {
                            DealCeilingUnitPrice = 95,
                            CompetitiveCeilingUnitPrice = 150,
                            SaneCeilingUnitPrice = 220,
                            InsaneFloorUnitPrice = 400
                        },
                        ListingClassCounts = new MarketListingClassCounts
                        {
                            DealCount = 1,
                            CompetitiveCount = 2,
                            FairCount = 3,
                            UncompetitiveCount = 4,
                            ExcludedCount = 5,
                            LowOutlierCount = 6,
                            SaneCount = 7,
                            OutlierCount = 8,
                            InsaneCount = 9
                        },
                        Confidence = MarketPriceEvaluationConfidence.High,
                        Diagnostics = new MarketPriceEvaluationDiagnostics
                        {
                            CompactReasonCodes =
                            [
                                MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity
                            ],
                            CompactRegionSummaries =
                            [
                                new MarketPriceRegionSummary
                                {
                                    MinUnitPrice = 100,
                                    MaxUnitPrice = 120,
                                    ListingCount = 3,
                                    TotalQuantity = 99,
                                    Credibility = MarketPriceRegionCredibility.Credible,
                                    ReasonCode = MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity
                                }
                            ],
                            DetectedPriceGapSummaries =
                            [
                                new MarketPriceGapSummary
                                {
                                    BeforeUnitPrice = 120,
                                    AfterUnitPrice = 300,
                                    BreakPercent = 150
                                }
                            ],
                            DebugDetailAvailable = true
                        }
                    },
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            Listings =
                            [
                                new AnalyzedMarketListing
                                {
                                    Quantity = 99,
                                    PricePerUnit = 110,
                                    RetainerName = "Seller",
                                    Competitiveness = MarketListingCompetitiveness.Competitive
                                }
                            ]
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
        Assert.NotNull(analysis.PriceEvaluation);
        var evaluation = analysis.PriceEvaluation!;
        Assert.Equal(123, evaluation.ItemId);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, evaluation.Scope);
        Assert.Equal(MarketPriceQualityPolicy.DualChannel, evaluation.QualityPolicy);
        Assert.Equal(evaluatedAtUtc, evaluation.EvaluatedAtUtc);
        Assert.Equal(100, evaluation.CentralRegion.MinUnitPrice);
        Assert.Equal(120, evaluation.CentralRegion.MaxUnitPrice);
        Assert.Equal(110, evaluation.CentralRegion.MedianUnitPrice);
        Assert.Equal(MarketPriceRegionCredibility.Credible, evaluation.CentralRegion.Credibility);
        Assert.Equal(112, evaluation.CentralRegion.WeightedAverageUnitPrice);
        Assert.Equal(3, evaluation.CentralRegion.ListingCount);
        Assert.Equal(99, evaluation.CentralRegion.TotalQuantity);
        Assert.Equal(3, evaluation.CentralRegion.DistinctRetainerCount);
        Assert.Equal(2, evaluation.CentralRegion.DistinctWorldCount);
        Assert.Equal(MarketDataQualityBucket.Current, evaluation.CentralRegion.DataQualityBucket);
        Assert.Equal(95, evaluation.Thresholds.DealCeilingUnitPrice);
        Assert.Equal(150, evaluation.Thresholds.CompetitiveCeilingUnitPrice);
        Assert.Equal(220, evaluation.Thresholds.SaneCeilingUnitPrice);
        Assert.Equal(400, evaluation.Thresholds.InsaneFloorUnitPrice);
        Assert.Equal(1, evaluation.ListingClassCounts.DealCount);
        Assert.Equal(2, evaluation.ListingClassCounts.CompetitiveCount);
        Assert.Equal(3, evaluation.ListingClassCounts.FairCount);
        Assert.Equal(4, evaluation.ListingClassCounts.UncompetitiveCount);
        Assert.Equal(5, evaluation.ListingClassCounts.ExcludedCount);
        Assert.Equal(6, evaluation.ListingClassCounts.LowOutlierCount);
        Assert.Equal(7, evaluation.ListingClassCounts.SaneCount);
        Assert.Equal(8, evaluation.ListingClassCounts.OutlierCount);
        Assert.Equal(9, evaluation.ListingClassCounts.InsaneCount);
        Assert.Equal(MarketPriceEvaluationConfidence.High, evaluation.Confidence);
        Assert.Equal(
            MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity,
            Assert.Single(evaluation.Diagnostics.CompactReasonCodes));
        var regionSummary = Assert.Single(evaluation.Diagnostics.CompactRegionSummaries);
        Assert.Equal(100, regionSummary.MinUnitPrice);
        Assert.Equal(120, regionSummary.MaxUnitPrice);
        Assert.Equal(3, regionSummary.ListingCount);
        Assert.Equal(99, regionSummary.TotalQuantity);
        Assert.Equal(MarketPriceRegionCredibility.Credible, regionSummary.Credibility);
        Assert.Equal(MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity, regionSummary.ReasonCode);
        var gapSummary = Assert.Single(evaluation.Diagnostics.DetectedPriceGapSummaries);
        Assert.Equal(120, gapSummary.BeforeUnitPrice);
        Assert.Equal(300, gapSummary.AfterUnitPrice);
        Assert.Equal(150, gapSummary.BreakPercent);
        Assert.True(evaluation.Diagnostics.DebugDetailAvailable);
        Assert.Equal(
            MarketListingCompetitiveness.Competitive,
            Assert.Single(Assert.Single(analysis.Worlds).Listings).Competitiveness);
    }

    [Fact]
    public void LoadStoredPlan_LegacyAnalysisMissingPriceEvaluationAndCompetitiveness_UsesUnknownDefaults()
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
                        "QuantityNeeded": 10,
                        "Listings": [
                          {
                            "Quantity": 1,
                            "PricePerUnit": 100,
                            "RetainerName": "Legacy Seller"
                          }
                        ]
                      }
                    ]
                  }
                ]
                """
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        var analysis = Assert.Single(appState.MarketItemAnalyses);
        Assert.Null(analysis.PriceEvaluation);
        var listing = Assert.Single(Assert.Single(analysis.Worlds).Listings);
        Assert.Equal(MarketListingCompetitiveness.Unknown, listing.Competitiveness);
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
        var recipeBasis = CreateStoredRecipeBasis();
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
            ],
            recipeBasis);

        appState.ClearMarketAnalysisState();
        var snapshot = appState.CreateStoredPlanSnapshot("plan", "Plan");

        Assert.Null(snapshot.MarketPlansJson);
        Assert.Null(snapshot.MarketItemAnalysesJson);
        Assert.Null(snapshot.MarketAnalysisRecipeBasisJson);
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
    public void LoadStoredPlan_TrackedStoredPlanStartsCleanForPersistenceGuard()
    {
        var appState = new AppState();
        var storedPlan = new StoredPlan
        {
            Id = "order-plan-id",
            Name = "Order - Cobalt Ingot Commission",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Cobalt Ingot",
                    Quantity = 999
                }
            ],
            MarketItemAnalysesJson = System.Text.Json.JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 456, Name = "Cobalt Ore", QuantityNeeded = 1_998 }
            }),
            MarketPlansJson = System.Text.Json.JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new() { ItemId = 456, Name = "Cobalt Ore", QuantityNeeded = 1_998 }
            })
        };

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null, trackStoredPlanIdentity: true);

        Assert.Equal("order-plan-id", appState.CurrentPlanId);
        Assert.Equal("Order - Cobalt Ingot Commission", appState.CurrentPlanName);
        Assert.Equal(PersistedStateBucket.None, appState.GetDirtyPersistedBuckets());
    }

    private static StoredRecipeOperationSnapshot CreateStoredRecipeBasis()
    {
        var stored = new StoredRecipeOperationSnapshot
        {
            Metadata = new StoredRecipeOperationMetadata
            {
                PlanSessionVersion = 1,
                PlanStructureVersion = 1,
                PlanDecisionVersion = 1,
                PlanPriceVersion = 1,
                SettingsVersion = 1,
                RecipeDataIdentity = "test"
            },
            Operations =
            [
                new StoredRecipeOperation
                {
                    NodeId = "root",
                    ResultItemId = 123,
                    ResultItemName = "Snapshot Item",
                    RequestedQuantity = 10,
                    Yield = 10,
                    State = RecipeOperationState.Active,
                    Source = AcquisitionSource.MarketBuyNq,
                    Kind = RecipeOperationKind.StandardCraft,
                    Ingredients = []
                }
            ],
            MarketAnalysisDemandItems =
            [
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 123,
                    Name = "Snapshot Item",
                    TotalQuantity = 10
                }
            ]
        };

        return stored;
    }

}
