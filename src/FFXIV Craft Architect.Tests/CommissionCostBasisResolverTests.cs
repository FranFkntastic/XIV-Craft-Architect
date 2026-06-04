using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CommissionCostBasisResolverTests
{
    [Fact]
    public void BuildMarketRecommendationLines_UsesCompetitiveAverageBeforeOtherPrices()
    {
        var resolver = new CommissionCostBasisResolver();
        var loadedAt = DateTime.UtcNow;

        var lines = resolver.BuildMarketRecommendationLines(
            [
                new MaterialAggregate
                {
                    ItemId = 10,
                    Name = "Thread",
                    TotalQuantity = 3,
                    UnitPrice = 999m,
                    RequiresHq = true
                }
            ],
            [
                new MarketItemAnalysis
                {
                    ItemId = 10,
                    Name = "Thread",
                    QuantityNeeded = 3,
                    LoadedAtUtc = loadedAt,
                    AnalysisScopeCompetitiveAverageUnitPrice = 120m,
                    AnalysisScopeAverageUnitPrice = 180m,
                    AnalysisScopeMedianUnitPrice = 150m
                }
            ]);

        var line = Assert.Single(lines);
        Assert.Equal(120m, line.UnitCost);
        Assert.Equal("Market competitive average", line.EvidenceSource);
        Assert.Contains("competitive average", line.UnitCostExplanation);
        Assert.Contains("120g", line.UnitCostExplanation);
        Assert.Equal(loadedAt, line.EvidenceTimestampUtc);
        Assert.True(line.RequiresHq);
    }

    [Fact]
    public void BuildMarketRecommendationLines_MissingEvidenceUsesPlanPriceWithWarning()
    {
        var resolver = new CommissionCostBasisResolver();

        var line = Assert.Single(resolver.BuildMarketRecommendationLines(
            [
                new MaterialAggregate
                {
                    ItemId = 20,
                    Name = "Ore",
                    TotalQuantity = 2,
                    UnitPrice = 50m
                }
            ],
            []));

        Assert.Equal(50m, line.UnitCost);
        Assert.Equal("Plan price", line.EvidenceSource);
        Assert.Contains("No market-analysis evidence was available for Ore.", line.Warnings);
        Assert.Contains("using plan price", line.UnitCostExplanation);
        Assert.Contains("Ore", line.UnitCostExplanation);
    }

    [Fact]
    public void BuildMarketRecommendationLines_DoesNotWarnWhenOnlyNonRecommendedWorldIsStale()
    {
        var resolver = new CommissionCostBasisResolver();
        var loadedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        var line = Assert.Single(resolver.BuildMarketRecommendationLines(
            [Material(30, "Ingot")],
            [
                new MarketItemAnalysis
                {
                    ItemId = 30,
                    Name = "Ingot",
                    LoadedAtUtc = loadedAt,
                    AnalysisScopeCompetitiveAverageUnitPrice = 100m,
                    WorstDataQualityBucket = MarketDataQualityBucket.Ancient
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 30,
                    Name = "Ingot",
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        MarketDataQualityBucket = MarketDataQualityBucket.Current,
                        MarketDataAge = TimeSpan.FromMinutes(30),
                        MarketUploadedAtUtc = loadedAt - TimeSpan.FromMinutes(30)
                    }
                }
            ]));

        Assert.DoesNotContain(line.Warnings, warning => warning.Contains("ancient", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(line.Warnings, warning => warning.Contains("very old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildMarketRecommendationLines_WarnsWhenRecommendedWorldIsVeryOld()
    {
        var resolver = new CommissionCostBasisResolver();
        var loadedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        var line = Assert.Single(resolver.BuildMarketRecommendationLines(
            [Material(40, "Silk")],
            [
                new MarketItemAnalysis
                {
                    ItemId = 40,
                    Name = "Silk",
                    LoadedAtUtc = loadedAt,
                    AnalysisScopeCompetitiveAverageUnitPrice = 100m,
                    WorstDataQualityBucket = MarketDataQualityBucket.VeryOld
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 40,
                    Name = "Silk",
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        MarketDataQualityBucket = MarketDataQualityBucket.VeryOld,
                        MarketDataAge = TimeSpan.FromHours(18),
                        MarketUploadedAtUtc = loadedAt - TimeSpan.FromHours(18)
                    }
                }
            ]));

        var warning = Assert.Single(line.Warnings, warning => warning.Contains("very old", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Silk", warning);
        Assert.Contains("Siren", warning);
        Assert.Contains("18h", warning);
        Assert.Contains("Siren", line.UnitCostExplanation);
        Assert.Contains("18h", line.UnitCostExplanation);
    }

    [Fact]
    public void BuildMarketRecommendationLines_WarnsWhenRecommendedSplitContainsAncientWorld()
    {
        var resolver = new CommissionCostBasisResolver();
        var loadedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        var line = Assert.Single(resolver.BuildMarketRecommendationLines(
            [Material(50, "Leather")],
            [
                new MarketItemAnalysis
                {
                    ItemId = 50,
                    Name = "Leather",
                    LoadedAtUtc = loadedAt,
                    AnalysisScopeCompetitiveAverageUnitPrice = 100m,
                    WorstDataQualityBucket = MarketDataQualityBucket.Ancient
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 50,
                    Name = "Leather",
                    WorldOptions =
                    [
                        new WorldShoppingSummary
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            MarketDataQualityBucket = MarketDataQualityBucket.Current,
                            MarketDataAge = TimeSpan.FromMinutes(20),
                            MarketUploadedAtUtc = loadedAt - TimeSpan.FromMinutes(20)
                        },
                        new WorldShoppingSummary
                        {
                            DataCenter = "Aether",
                            WorldName = "Faerie",
                            MarketDataQualityBucket = MarketDataQualityBucket.Ancient,
                            MarketDataAge = TimeSpan.FromHours(27),
                            MarketUploadedAtUtc = loadedAt - TimeSpan.FromHours(27)
                        }
                    ],
                    RecommendedSplit =
                    [
                        new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 1 },
                        new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Faerie", QuantityToBuy = 1 }
                    ]
                }
            ]));

        var warning = Assert.Single(line.Warnings, warning => warning.Contains("ancient", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Leather", warning);
        Assert.Contains("Faerie", warning);
        Assert.Contains("27h", warning);
    }

    private static MaterialAggregate Material(int itemId, string name)
    {
        return new MaterialAggregate
        {
            ItemId = itemId,
            Name = name,
            TotalQuantity = 2,
            UnitPrice = 50m
        };
    }
}
