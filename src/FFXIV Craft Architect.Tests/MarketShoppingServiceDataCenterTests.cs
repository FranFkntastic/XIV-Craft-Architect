using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketShoppingServiceDataCenterTests
{
    [Fact]
    public async Task CalculateShoppingPlansWithSplitsAsync_SetsDataCenterOnWorldSummariesAndSplitPurchases()
    {
        var cache = new Mock<IMarketCacheService>();
        cache
            .Setup(c => c.GetAsync(123, "Aether", null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 20,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 6, PricePerUnit = 10, RetainerName = "Siren Retainer" }
                        }
                    },
                    new CachedWorldData
                    {
                        WorldName = "Gilgamesh",
                        Listings =
                        {
                            new CachedListing { Quantity = 4, PricePerUnit = 20, RetainerName = "Gilgamesh Retainer" }
                        }
                    }
                }
            });

        var service = new MarketShoppingService(cache.Object);
        var config = new MarketAnalysisConfig { EnableSplitWorld = true };
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Route Item", TotalQuantity = 10 }
        };

        var plans = await service.CalculateShoppingPlansWithSplitsAsync(materials, "Aether", config: config);

        var plan = Assert.Single(plans);
        Assert.All(plan.WorldOptions, world => Assert.Equal("Aether", world.DataCenter));
        var coverage = Assert.IsType<MarketCoverageOption>(
            PurchaseRecommendationCost.GetDefaultCoverageOption(plan));
        Assert.Equal(2, coverage.Worlds.Count);
        Assert.All(coverage.Worlds, world => Assert.Equal("Aether", world.DataCenter));
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_SelectedListingDetailsTrackRemainingNeededQuantity()
    {
        var cache = new Mock<IMarketCacheService>();
        cache
            .Setup(c => c.GetAsync(123, "Aether", null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 110,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 5, PricePerUnit = 100, RetainerName = "Retainer A" },
                            new CachedListing { Quantity = 5, PricePerUnit = 110, RetainerName = "Retainer B" }
                        }
                    }
                }
            });

        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Stack Detail Item", TotalQuantity = 7 }
        };

        var plans = await service.CalculateDetailedShoppingPlansAsync(materials, "Aether");

        var plan = Assert.Single(plans);
        var world = Assert.Single(plan.WorldOptions);
        Assert.Equal(1050, world.TotalCost);
        Assert.Collection(
            world.Listings.Where(listing => !listing.IsAdditionalOption),
            first =>
            {
                Assert.Equal(5, first.Quantity);
                Assert.Equal(5, first.NeededFromStack);
                Assert.Equal(0, first.ExcessQuantity);
            },
            second =>
            {
                Assert.Equal(5, second.Quantity);
                Assert.Equal(2, second.NeededFromStack);
                Assert.Equal(3, second.ExcessQuantity);
            });
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_RequestUsesProvidedEvidenceWithoutCacheReads()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Evidence Item", TotalQuantity = 2 }
        };
        var evidence = new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = new CachedMarketData
                {
                    ItemId = 123,
                    DataCenter = "Aether",
                    DCAveragePrice = 50,
                    Worlds =
                    {
                        new CachedWorldData
                        {
                            WorldName = "Siren",
                            Listings =
                            {
                                new CachedListing { Quantity = 2, PricePerUnit = 50, RetainerName = "Evidence Retainer" }
                            }
                        }
                    }
                }
            },
            [(123, "Aether")],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);

        var plans = await service.CalculateDetailedShoppingPlansAsync(new MarketAnalysisRequest
        {
            Items = materials,
            Evidence = evidence
        });

        var plan = Assert.Single(plans);
        Assert.Equal("Siren", plan.RecommendedWorld?.WorldName);
        Assert.Equal("Aether", plan.RecommendedWorld?.DataCenter);
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_RequestKeepsBlacklistedHomeWorldAvailable()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var settings = new Mock<SettingsService>(Mock.Of<ILogger<SettingsService>>());
        settings
            .Setup(s => s.Get<string>("market.home_world", ""))
            .Returns("Siren");
        settings
            .Setup(s => s.Get<bool>("market.exclude_congested_worlds", true))
            .Returns(true);
        var service = new MarketShoppingService(cache.Object, settingsService: settings.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Home World Item", TotalQuantity = 1 }
        };
        var evidence = CreateEvidence(
            new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 50,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 1, PricePerUnit = 50, RetainerName = "Home Retainer" }
                        }
                    }
                }
            },
            [(123, "Aether")]);

        var plans = await service.CalculateDetailedShoppingPlansAsync(new MarketAnalysisRequest
        {
            Items = materials,
            Evidence = evidence,
            BlacklistedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Siren" }
        });

        var plan = Assert.Single(plans);
        var world = Assert.Single(plan.WorldOptions);
        Assert.Equal("Siren", world.WorldName);
        Assert.True(world.IsHomeWorld);
        Assert.Equal("Siren", plan.RecommendedWorld?.WorldName);
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_RequestMarksPartialEvidence()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Partial Evidence Item", TotalQuantity = 1 }
        };
        var evidence = CreateEvidence(
            new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 50,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 1, PricePerUnit = 50, RetainerName = "Partial Retainer" }
                        }
                    }
                }
            },
            [(123, "Aether"), (123, "Primal")]);

        var plans = await service.CalculateDetailedShoppingPlansAsync(new MarketAnalysisRequest
        {
            Items = materials,
            Evidence = evidence
        });

        var plan = Assert.Single(plans);
        Assert.Equal("Siren", plan.RecommendedWorld?.WorldName);
        Assert.Null(plan.Error);
        Assert.Contains("Market data incomplete for Primal", plan.MarketDataWarning);
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_WithExecutionOptionsMatchesDefaultResults()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Yielded Evidence Item", TotalQuantity = 1 }
        };
        var evidence = CreateEvidence(
            new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 50,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 1, PricePerUnit = 50, RetainerName = "Yielded Retainer" }
                        }
                    }
                }
            },
            [(123, "Aether")]);
        var request = new MarketAnalysisRequest
        {
            Items = materials,
            Evidence = evidence
        };
        var options = new MarketAnalysisExecutionOptions { YieldEveryItems = 1 };

        var defaultPlans = await service.CalculateDetailedShoppingPlansAsync(request);
        var yieldedPlans = await service.CalculateDetailedShoppingPlansAsync(request, executionOptions: options);

        var defaultPlan = Assert.Single(defaultPlans);
        var yieldedPlan = Assert.Single(yieldedPlans);
        Assert.Equal(defaultPlan.RecommendedWorld?.WorldName, yieldedPlan.RecommendedWorld?.WorldName);
        Assert.Equal(defaultPlan.RecommendedWorld?.DataCenter, yieldedPlan.RecommendedWorld?.DataCenter);
        Assert.Equal(defaultPlan.RecommendedWorld?.TotalCost, yieldedPlan.RecommendedWorld?.TotalCost);
    }

    [Fact]
    public void CalculateDetailedShoppingPlansAsync_WithoutExecutionOptions_CompletesSynchronously()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Synchronous Evidence Item", TotalQuantity = 1 }
        };
        var evidence = CreateEvidence(
            new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 50,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 1, PricePerUnit = 50, RetainerName = "Sync Retainer" }
                        }
                    }
                }
            },
            [(123, "Aether")]);

        var task = service.CalculateDetailedShoppingPlansAsync(new MarketAnalysisRequest
        {
            Items = materials,
            Evidence = evidence
        });

        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansMultiDCAsync_StructuredBlacklistExcludesOnlyMatchingDataCenterWorld()
    {
        var cache = new Mock<IMarketCacheService>();
        SetupCachedWorld(cache, "Aether", "Siren", 10);
        SetupCachedWorld(cache, "Primal", "Siren", 20);
        cache
            .Setup(c => c.GetAsync(123, "Crystal", null))
            .ReturnsAsync((CachedMarketData?)null);
        cache
            .Setup(c => c.GetAsync(123, "Dynamis", null))
            .ReturnsAsync((CachedMarketData?)null);

        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Structured Blacklist Item", TotalQuantity = 1 }
        };
        var blacklistedWorlds = new HashSet<MarketWorldKey>
        {
            new("Aether", "Siren")
        };

        var plans = await service.CalculateDetailedShoppingPlansMultiDCAsync(
            materials,
            blacklistedMarketWorlds: blacklistedWorlds);

        var plan = Assert.Single(plans);
        var siren = Assert.Single(plan.WorldOptions, option => option.WorldName == "Siren");
        Assert.Equal("Primal", siren.DataCenter);
    }

    private static void SetupCachedWorld(Mock<IMarketCacheService> cache, string dataCenter, string worldName, long pricePerUnit)
    {
        cache
            .Setup(c => c.GetAsync(123, dataCenter, null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = dataCenter,
                DCAveragePrice = pricePerUnit,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = worldName,
                        Listings =
                        {
                            new CachedListing
                            {
                                Quantity = 1,
                                PricePerUnit = pricePerUnit,
                                RetainerName = $"{dataCenter} Retainer"
                            }
                        }
                    }
                }
            });
    }

    private static MarketEvidenceSet CreateEvidence(
        CachedMarketData entry,
        IReadOnlyList<(int itemId, string dataCenter)> requestedPairs)
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(entry.ItemId, entry.DataCenter)] = entry
            },
            requestedPairs,
            requestedPairs.Count > 1 ? MarketFetchScope.EntireRegion : MarketFetchScope.SelectedDataCenter,
            requestedPairs.Select(pair => pair.dataCenter).Distinct().ToList(),
            entry.DataCenter,
            "North America",
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);
    }
}
