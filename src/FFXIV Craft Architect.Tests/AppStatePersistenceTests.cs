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
                            ComparableQuantity = 10,
                            ComparableAverageUnitPrice = 645m,
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
        Assert.Equal(10, world.ComparableQuantity);
        Assert.Equal(645m, world.ComparableAverageUnitPrice);
        Assert.Equal(0.8m, world.PrimaryUsableCoverageRatio);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
        Assert.Single(restored.ShoppingPlans);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_RoundTripRestoresCurrentProcurementRoute()
    {
        var appState = new AppState();
        var projectItem = new ProjectItem { Id = 123, Name = "Route Item", Quantity = 10 };
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 123,
                    Name = "Route Item",
                    Quantity = 10,
                    Source = AcquisitionSource.MarketBuyNq
                }
            ]
        };
        appState.ActivateRecipePlan(
            plan,
            [projectItem],
            "Aether",
            clearCurrentPlanId: true,
            [new MaterialAggregate { ItemId = 123, Name = "Route Item", TotalQuantity = 10 }]);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }]);
        var decision = new MarketRouteDecision(
            0,
            null,
            1_000,
            1_000,
            0,
            1,
            1,
            0,
            0,
            false,
            null);
        appState.ReplaceProcurementOverlay(
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }],
            decision);

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var restored = new AppState();
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.NotNull(snapshot.ProcurementRouteJson);
        var storedRoute = JsonSerializer.Deserialize<StoredProcurementRoute>(snapshot.ProcurementRouteJson!)!;
        Assert.Equal(storedRoute.PlanHash, StoredPlanSnapshotBuilder.ComputePlanHash(restored.CurrentPlan!));
        Assert.Equal(
            storedRoute.MarketEvidenceHash,
            StoredPlanSnapshotBuilder.ComputeMarketEvidenceHash(snapshot.MarketIntelligenceJson!));
        Assert.Equal(
            storedRoute.PayloadHash,
            StoredPlanSnapshotBuilder.ComputeRoutePayloadHash(storedRoute.ShoppingPlans!, storedRoute.Decision!));
        Assert.Single(AcquisitionPlanningService.GetMarketAnalysisCandidates(restored.CurrentPlan));
        var currentBasis = restored.CreateCurrentProcurementRouteBasis();
        Assert.True((storedRoute.Basis! with
        {
            PlanSessionVersion = currentBasis.PlanSessionVersion,
            PlanDecisionVersion = currentBasis.PlanDecisionVersion
        }).Matches(currentBasis));
        Assert.Equal(ProcurementRoutePublicationValidity.Current, restored.ProcurementRouteValidity);
        Assert.Equal(1_000, restored.ProcurementRouteDecision?.SelectedGilCost);
        Assert.Equal("Route Item", Assert.Single(restored.ProcurementShoppingPlans).Name);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_RoundTripRestoresRouteForFinalActiveProcurementItems()
    {
        var ingredient = new PlanNode
        {
            ItemId = 456,
            Name = "Purchased Ingredient",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq
        };
        var craftedRoot = new PlanNode
        {
            ItemId = 123,
            Name = "Crafted Result",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Children = [ingredient]
        };
        ingredient.Parent = craftedRoot;
        ingredient.ParentNodeId = craftedRoot.NodeId;
        var plan = new CraftingPlan { RootItems = [craftedRoot] };
        var appState = new AppState();
        appState.ActivateRecipePlan(
            plan,
            [new ProjectItem { Id = 123, Name = "Crafted Result", Quantity = 1 }],
            "Aether",
            clearCurrentPlanId: true,
            [new MaterialAggregate { ItemId = 456, Name = "Purchased Ingredient", TotalQuantity = 3 }]);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis { ItemId = 123, Name = "Crafted Result", QuantityNeeded = 1 },
                new MarketItemAnalysis { ItemId = 456, Name = "Purchased Ingredient", QuantityNeeded = 3 }
            ],
            [
                new DetailedShoppingPlan { ItemId = 123, Name = "Crafted Result", QuantityNeeded = 1 },
                new DetailedShoppingPlan { ItemId = 456, Name = "Purchased Ingredient", QuantityNeeded = 3 }
            ]);
        var routePlan = new DetailedShoppingPlan
        {
            ItemId = 456,
            Name = "Purchased Ingredient",
            QuantityNeeded = 3
        };
        appState.ReplaceProcurementOverlay(
            [routePlan],
            new MarketRouteDecision(0, null, 300, 300, 0, 1, 1, 0, 0, false, null));

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var restored = new AppState();
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.Equal(2, AcquisitionPlanningService.GetMarketAnalysisCandidates(restored.CurrentPlan).Count);
        Assert.Single(AcquisitionPlanningService.GetActiveProcurementItems(restored.CurrentPlan));
        Assert.Null(restored.ProcurementRouteRestoreDiagnostic);
        Assert.Equal(ProcurementRoutePublicationValidity.Current, restored.ProcurementRouteValidity);
        Assert.Equal(456, Assert.Single(restored.ProcurementShoppingPlans).ItemId);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_RoundTripRestoresRouteAfterProcurementOptimization()
    {
        var ingredient = new PlanNode
        {
            ItemId = 456,
            Name = "Purchased Ingredient",
            Quantity = 3,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true
        };
        var craftedRoot = new PlanNode
        {
            ItemId = 123,
            Name = "Crafted Result",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            Children = [ingredient]
        };
        ingredient.Parent = craftedRoot;
        ingredient.ParentNodeId = craftedRoot.NodeId;
        var originalPlan = new CraftingPlan { RootItems = [craftedRoot] };
        var appState = new AppState();
        appState.ActivateRecipePlan(
            originalPlan,
            [new ProjectItem { Id = 123, Name = "Crafted Result", Quantity = 1 }],
            "Aether",
            clearCurrentPlanId: true,
            [
                new MaterialAggregate { ItemId = 123, Name = "Crafted Result", TotalQuantity = 1 },
                new MaterialAggregate { ItemId = 456, Name = "Purchased Ingredient", TotalQuantity = 3 }
            ]);
        var optimizedPlan = new CraftingPlan
        {
            RootItems = originalPlan.RootItems.Select(root => root.Clone()).ToList()
        };
        optimizedPlan.RootItems[0].Source = AcquisitionSource.Craft;
        optimizedPlan.RootItems[0].Children[0].Source = AcquisitionSource.MarketBuyNq;
        var analyses = new[]
        {
            new MarketItemAnalysis { ItemId = 123, Name = "Crafted Result", QuantityNeeded = 1 },
            new MarketItemAnalysis { ItemId = 456, Name = "Purchased Ingredient", QuantityNeeded = 3 }
        };
        var evidencePlans = new[]
        {
            new DetailedShoppingPlan { ItemId = 123, Name = "Crafted Result", QuantityNeeded = 1 },
            new DetailedShoppingPlan { ItemId = 456, Name = "Purchased Ingredient", QuantityNeeded = 3 }
        };
        var routePlan = new DetailedShoppingPlan
        {
            ItemId = 456,
            Name = "Purchased Ingredient",
            QuantityNeeded = 3
        };

        var applied = appState.ApplyProcurementOptimization(
            originalPlan,
            optimizedPlan,
            [new MaterialAggregate { ItemId = 456, Name = "Purchased Ingredient", TotalQuantity = 3 }],
            [routePlan],
            new MarketRouteDecision(0, null, 300, 300, 0, 1, 1, 0, 0, false, null),
            analyses,
            evidencePlans);
        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var restored = new AppState();

        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.True(applied);
        Assert.Equal(AcquisitionSource.Craft, restored.CurrentPlan!.RootItems[0].Source);
        Assert.Equal(AcquisitionSource.MarketBuyNq, restored.CurrentPlan.RootItems[0].Children[0].Source);
        Assert.True(restored.ProcurementRouteRestoreDiagnostic is null, restored.ProcurementRouteRestoreDiagnostic);
        Assert.Equal(ProcurementRoutePublicationValidity.Current, restored.ProcurementRouteValidity);
        Assert.Equal(456, Assert.Single(restored.ProcurementShoppingPlans).ItemId);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_RestoresRouteBeforeAutomaticSourceRepair()
    {
        var ingredient = new PlanNode
        {
            ItemId = 456,
            Name = "Vendor Ingredient",
            Quantity = 3,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromMarket = false,
            CanBuyFromVendor = true,
            VendorPrice = 10,
            VendorOptions =
            [
                new VendorInfo { Name = "Supplier", Location = "Limsa Lominsa", Price = 10, Currency = "Gil" }
            ]
        };
        var root = new PlanNode
        {
            ItemId = 123,
            Name = "Route Item",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = [ingredient]
        };
        ingredient.Parent = root;
        var source = new AppState();
        source.ActivateRecipePlan(
            new CraftingPlan { RootItems = [root] },
            [new ProjectItem { Id = 123, Name = "Route Item", Quantity = 1 }],
            "Aether",
            clearCurrentPlanId: true,
            [new MaterialAggregate { ItemId = 123, Name = "Route Item", TotalQuantity = 1 }]);
        source.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Route Item", QuantityNeeded = 1 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 1 }]);
        source.ReplaceProcurementOverlay(
            [new DetailedShoppingPlan
            {
                ItemId = 123,
                Name = "Route Item",
                QuantityNeeded = 1,
                RecommendedWorld = new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalCost = 100
                }
            }],
            new MarketRouteDecision(0, null, 100, 100, 0, 1, 1, 0, 0, false, null));
        var snapshot = source.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var restored = new AppState();

        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.Null(restored.ProcurementRouteRestoreDiagnostic);
        Assert.Equal(ProcurementRoutePublicationValidity.Current, restored.ProcurementRouteValidity);
        Assert.Equal(AcquisitionSource.MarketBuyNq, restored.CurrentPlan!.RootItems[0].Source);
        Assert.Equal(123, Assert.Single(restored.ProcurementShoppingPlans).ItemId);
    }

    [Fact]
    public void LoadStoredPlan_DiscardsMalformedOrTransplantedProcurementRoute()
    {
        var source = new AppState();
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 123,
                    Name = "Route Item",
                    Quantity = 10,
                    Source = AcquisitionSource.MarketBuyNq
                }
            ]
        };
        source.ActivateRecipePlan(
            plan,
            [new ProjectItem { Id = 123, Name = "Route Item", Quantity = 10 }],
            "Aether",
            clearCurrentPlanId: true,
            [new MaterialAggregate { ItemId = 123, Name = "Route Item", TotalQuantity = 10 }]);
        source.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }],
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }]);
        source.ReplaceProcurementOverlay(
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 10 }],
            new MarketRouteDecision(0, null, 1_000, 1_000, 0, 1, 1, 0, 0, false, null));
        var snapshot = source.CreateStoredPlanSnapshot("autosave", "AutoSave");
        var route = JsonSerializer.Deserialize<StoredProcurementRoute>(snapshot.ProcurementRouteJson!)!;
        snapshot.ProcurementRouteJson = JsonSerializer.Serialize(route with
        {
            ShoppingPlans = [new DetailedShoppingPlan { ItemId = 999, Name = "Transplanted", QuantityNeeded = 1 }]
        });
        var restored = new AppState();

        restored.LoadStoredPlan(snapshot, deserializedPlan: null);

        Assert.Equal(ProcurementRoutePublicationValidity.None, restored.ProcurementRouteValidity);
        Assert.Empty(restored.ProcurementShoppingPlans);
        Assert.True(restored.IsPersistedBucketDirty(PersistedStateBucket.ProcurementRoute));
        Assert.Null(restored.CreateStoredPlanSnapshot("autosave", "AutoSave").ProcurementRouteJson);
        restored.MarkPersisted(PersistedStateBucket.ProcurementRoute, restored.CurrentVersions);
        Assert.False(restored.IsPersistedBucketDirty(PersistedStateBucket.ProcurementRoute));

        snapshot.ProcurementRouteJson = JsonSerializer.Serialize(route with { OptimizerVersion = "obsolete" });
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);
        Assert.Equal("route-schema-mismatch", restored.ProcurementRouteRestoreDiagnostic);
        Assert.Equal(ProcurementRoutePublicationValidity.None, restored.ProcurementRouteValidity);

        var changedPlan = JsonSerializer.Deserialize<CraftingPlan>(snapshot.PlanJson!)!;
        changedPlan.RootItems[0].Source = AcquisitionSource.VendorBuy;
        snapshot.PlanJson = JsonSerializer.Serialize(changedPlan);
        snapshot.ProcurementRouteJson = JsonSerializer.Serialize(route);
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);
        Assert.Equal("plan-hash-mismatch", restored.ProcurementRouteRestoreDiagnostic);
        Assert.Equal(ProcurementRoutePublicationValidity.None, restored.ProcurementRouteValidity);

        snapshot.ProcurementRouteJson = "{\"shoppingPlans\":null,\"decision\":null,\"basis\":null}";
        restored.LoadStoredPlan(snapshot, deserializedPlan: null);
        Assert.Equal(ProcurementRoutePublicationValidity.None, restored.ProcurementRouteValidity);
    }

    [Fact]
    public void CreateStoredPlanSnapshot_CurrentRouteWithoutMarketEvidenceIsOmitted()
    {
        var appState = new AppState();
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 123,
                    Name = "Route Item",
                    Quantity = 1,
                    Source = AcquisitionSource.MarketBuyNq
                }
            ]
        };
        appState.ActivateRecipePlan(
            plan,
            [new ProjectItem { Id = 123, Name = "Route Item", Quantity = 1 }],
            "Aether",
            clearCurrentPlanId: true,
            [new MaterialAggregate { ItemId = 123, Name = "Route Item", TotalQuantity = 1 }]);
        appState.ReplaceProcurementOverlay(
            [new DetailedShoppingPlan { ItemId = 123, Name = "Route Item", QuantityNeeded = 1 }],
            new MarketRouteDecision(0, null, 100, 100, 0, 1, 1, 0, 0, false, null));

        var snapshot = appState.CreateStoredPlanSnapshot("autosave", "AutoSave");

        Assert.Null(snapshot.MarketIntelligenceJson);
        Assert.Null(snapshot.ProcurementRouteJson);
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
