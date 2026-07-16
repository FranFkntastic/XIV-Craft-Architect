using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisGridViewServiceTests
{
    [Fact]
    public void GetOrderedPlans_DefaultRecommendedSort_UsesBestWorldRankThenName()
    {
        var plans = new[]
        {
            Plan(300, "Zinc Ore", 300),
            Plan(100, "Silver Ore", 100),
            Plan(200, "Copper Ore", 200)
        };
        var analyses = new[]
        {
            Analysis(100, "Silver Ore", rank: 2),
            Analysis(200, "Copper Ore", rank: 1),
            Analysis(300, "Zinc Ore", rank: 1)
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedPlans(
            plans,
            analyses,
            MarketAcquisitionLens.BulkValue,
            MarketSortOption.ByRecommended,
            sortColumn: null,
            sortDescending: false);

        Assert.Equal([200, 300, 100], ordered.Select(plan => plan.ItemId));
    }
    [Fact]
    public void GetOrderedPlans_QuantitySort_UsesNeededQuantityNotAvailableStock()
    {
        var lowNeedHighStock = Plan(100, "Low Need", 100);
        lowNeedHighStock.QuantityNeeded = 2;
        lowNeedHighStock.WorldOptions[0].TotalQuantityPurchased = 500;

        var highNeedThinStock = Plan(200, "High Need", 100);
        highNeedThinStock.QuantityNeeded = 50;
        highNeedThinStock.WorldOptions[0].TotalQuantityPurchased = 1;

        var ascending = MarketAnalysisGridViewService.GetOrderedPlans(
            [highNeedThinStock, lowNeedHighStock],
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Quantity,
            sortDescending: false);
        var descending = MarketAnalysisGridViewService.GetOrderedPlans(
            [highNeedThinStock, lowNeedHighStock],
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Quantity,
            sortDescending: true);

        Assert.Equal([100, 200], ascending.Select(plan => plan.ItemId));
        Assert.Equal([200, 100], descending.Select(plan => plan.ItemId));
    }
    [Fact]
    public void FormatCoverage_UsesAllProcurementEvidenceWhenScopeContextExists()
    {
        var world = ScopeWorld("Uncompetitive Full", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 25);

        Assert.Equal("100/100", MarketAnalysisGridViewService.FormatCoverage(world));
        Assert.Equal(MarketCoverageBucket.Full, MarketAnalysisGridViewService.GetDisplayCoverageBucket(world));
    }
    [Fact]
    public void GetOrderedWorlds_UsesScopeAwareDisplayScoreBeforeStoredLensRank()
    {
        var optimal = ScopeWorld("Later Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 50);
        var partial = ScopeWorld("Early Rank", scopeSaneQuantity: 50, scopePrimaryUsableQuantity: 10, rank: 1);
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [partial, optimal]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Equal(["Later Rank", "Early Rank"], ordered.Select(world => world.WorldName));
    }
    [Fact]
    public void GetOrderedWorlds_UnitPriceSort_UsesPriceSignalAverageForCompetitivenessOverlay()
    {
        var cheaperLaterRank = ScopeWorld("Cheaper Later Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 50, worldCompetitiveAverage: 103);
        cheaperLaterRank.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 40,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Usable,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var pricierEarlyRank = ScopeWorld("Pricier Early Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 1, worldCompetitiveAverage: 153);
        pricierEarlyRank.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 150,
            MaxUnitPrice = 155,
            WeightedAverageUnitPrice = 153,
            Quantity = 80,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [pricierEarlyRank, cheaperLaterRank]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false,
            MarketAnalysisEvidenceOverlay.CompetitivenessOverlay);

        Assert.Equal(["Cheaper Later Rank", "Pricier Early Rank"], ordered.Select(world => world.WorldName));
    }
    [Fact]
    public void FormatWorldUnitPrice_UsesPriceSignalInsteadOfLowOutlierBand()
    {
        var world = ScopeWorld("Diabolos", scopeSaneQuantity: 4_402, scopePrimaryUsableQuantity: 4_402, worldCompetitiveAverage: 890);
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 1,
            MaxUnitPrice = 1,
            WeightedAverageUnitPrice = 1,
            Quantity = 4,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.LowOutlier,
            Depth = PriceBandDepth.Thin
        });
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 2,
            LastListingIndex = 3,
            MinUnitPrice = 880,
            MaxUnitPrice = 900,
            WeightedAverageUnitPrice = 890,
            Quantity = 4_402,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        Assert.Equal("~890g / unit", MarketAnalysisGridViewService.FormatWorldUnitPrice(world));
        Assert.Equal("4,402", MarketAnalysisGridViewService.FormatWorldMarketDepthQuantity(world));
        Assert.Equal("strong", MarketAnalysisGridViewService.FormatWorldMarketDepthDescriptor(world));
        Assert.Equal("is-optimal", MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
    }

    [Fact]
    public void ThinCredibleListings_DisplayAsProcurementEvidenceWithoutPrimaryShelf()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Primal",
            WorldName = "Excalibur",
            QuantityNeeded = 1_000,
            CompetitiveThresholdUnitPrice = 150,
            SaneThresholdUnitPrice = 300,
            ScopeSaneQuantity = 200,
            DataQualityBucket = MarketDataQualityBucket.Current,
            AnalysisCompetitiveAverageUnitPrice = 110,
            Listings =
            [
                Listing(0, 10, 1, MarketListingPriceSanity.LowOutlier, MarketListingCompetitiveness.Deal),
                Listing(1, 200, 100, MarketListingPriceSanity.Sane, MarketListingCompetitiveness.Competitive),
                Listing(2, 2, 10_000, MarketListingPriceSanity.Insane, MarketListingCompetitiveness.Excluded)
            ],
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.BulkValue,
                    Rank = 10,
                    ScoreBucket = MarketScoreBucket.Unavailable
                }
            ]
        };

        Assert.Equal("200", MarketAnalysisGridViewService.FormatWorldMarketDepthQuantity(world));
        Assert.Equal("limited", MarketAnalysisGridViewService.FormatWorldMarketDepthDescriptor(world));
        Assert.Equal("~100g / unit", MarketAnalysisGridViewService.FormatWorldUnitPrice(world));
        Assert.Equal("200/1,000", MarketAnalysisGridViewService.FormatCoverage(world));
        Assert.Equal(MarketCoverageBucket.PartialThin, MarketAnalysisGridViewService.GetDisplayCoverageBucket(world));
        Assert.Equal(MarketScoreBucket.PoorFit, MarketAnalysisGridViewService.GetDisplayScoreBucket(world, MarketAcquisitionLens.BulkValue));
        Assert.Equal("-9%", MarketAnalysisGridViewService.FormatCompetitiveValue(world));
        Assert.Contains("best usable listing price", MarketAnalysisGridViewService.FormatCompetitiveValueTooltip(world));
    }

    [Fact]
    public void GetOrderedWorlds_ValueSort_UsesProcurementSignalDiscountAgainstMarketFallback()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds =
            [
                ScopeWorld("Small Discount", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 95),
                ScopeWorld("Large Discount", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 80),
                ScopeWorld("Above Average", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 110)
            ]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.Value,
            sortDescending: false);

        Assert.Equal(["Large Discount", "Small Discount", "Above Average"], ordered.Select(world => world.WorldName));
    }
    [Fact]
    public void GetListingDividersBefore_AddsAverageThresholdDividersWithoutDuplicates()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            QuantityNeeded = 100,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = 100,
            PrimaryUsableQuantity = 100,
            AnalysisCompetitiveAverageUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 150,
            DataQualityBucket = MarketDataQualityBucket.Current,
            PriceBands =
            [
                new MarketPriceBand
                {
                    FirstListingIndex = 0,
                    LastListingIndex = 0,
                    WeightedAverageUnitPrice = 80,
                    NextBreakPercent = 100,
                    IsPrimaryUsableBand = true
                }
            ],
            Listings =
            [
                new AnalyzedMarketListing { SortIndex = 0, Quantity = 10, PricePerUnit = 80 },
                new AnalyzedMarketListing { SortIndex = 1, Quantity = 10, PricePerUnit = 120 },
                new AnalyzedMarketListing { SortIndex = 2, Quantity = 10, PricePerUnit = 170 }
            ],
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Rank = 1,
                    ScoreBucket = MarketScoreBucket.Unavailable
                }
            ]
        };

        var first = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[0]);
        var second = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[1]);
        var third = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[2]);

        Assert.Empty(first);
        Assert.Equal(["Price band +100%", "Above best available average"], second.Select(divider => divider.Label));
        Assert.Equal(["Above market average"], third.Select(divider => divider.Label));
    }

    [Fact]
    public void IsUnsupportedProjectedCost_FullSplitRecommendation_ReturnsFalseWithCalculatedTotalTooltip()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Split Ore",
            QuantityNeeded = 10,
            RecommendedSplit =
            [
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 6, TotalCost = 600 },
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Faerie", QuantityToBuy = 4, TotalCost = 440 }
            ]
        };

        Assert.False(MarketAnalysisGridViewService.IsUnsupportedProjectedCost(plan));
        Assert.Equal("ma-total-value", MarketAnalysisGridViewService.GetTotalCostClass(plan));
        var tooltip = MarketAnalysisGridViewService.GetTotalCostTooltip(plan);
        Assert.Contains("Cash Out", tooltip);
        Assert.Contains("selected split", tooltip);
    }


    [Fact]
    public void ResolveSelectedPlan_UsesItemIdAcrossNewPlanInstancesAndFallsBackWhenMissing()
    {
        var oldSelection = Plan(100, "Old Instance", 100);
        var plans = new[]
        {
            Plan(100, "New Instance", 100),
            Plan(200, "Other", 200)
        };

        var selected = MarketAnalysisGridViewService.ResolveSelectedPlan(plans, oldSelection.ItemId);
        var fallback = MarketAnalysisGridViewService.ResolveSelectedPlan(plans, selectedItemId: 999);

        Assert.Same(plans[0], selected);
        Assert.Same(plans[0], fallback);
    }


    public static IEnumerable<object[]> PriceBandListingClassCases()
    {
        var lowOutlier = Listing(sortIndex: 0, quantity: 5, pricePerUnit: 1, priceSanity: MarketListingPriceSanity.LowOutlier);
        yield return
        [
            WorldWithListingBand(lowOutlier, quantityNeeded: 100, bandQuantity: 5, isPrimaryUsableBand: false),
            lowOutlier,
            "ma-band-tone-low",
            "ma-band-edge-low-outlier"
        ];

        var thin = Listing(
            sortIndex: 0,
            quantity: 2,
            pricePerUnit: 120,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(thin, quantityNeeded: 100, bandQuantity: 2, isPrimaryUsableBand: false),
            thin,
            "ma-band-tone-mid",
            "ma-band-edge-thin"
        ];

        var competitive = Listing(
            sortIndex: 0,
            quantity: 100,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(competitive, quantityNeeded: 100, bandQuantity: 100, isPrimaryUsableBand: true),
            competitive,
            "ma-band-tone-mid",
            "ma-band-edge-competitive"
        ];

        var credibleCompetitive = Listing(
            sortIndex: 0,
            quantity: 30,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(credibleCompetitive, quantityNeeded: 100, bandQuantity: 30, isPrimaryUsableBand: true, listingCount: 2),
            credibleCompetitive,
            "ma-band-tone-mid",
            "ma-band-edge-competitive"
        ];

        var insane = Listing(sortIndex: 0, quantity: 10, pricePerUnit: 1_000, priceSanity: MarketListingPriceSanity.Insane);
        yield return
        [
            WorldWithListingBand(insane, quantityNeeded: 100, bandQuantity: 10, isPrimaryUsableBand: false),
            insane,
            "ma-band-tone-high",
            "ma-band-edge-insane"
        ];
    }

    private static DetailedShoppingPlan Plan(int itemId, string name, long totalCost)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 1,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = totalCost,
                TotalQuantityPurchased = 1
            },
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalCost = totalCost,
                    TotalQuantityPurchased = 1
                }
            ]
        };
    }

    private static MarketItemAnalysis Analysis(int itemId, string name, int rank)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Scores =
                    [
                        new WorldLensScore
                        {
                            Lens = MarketAcquisitionLens.BulkValue,
                            Rank = rank,
                            ScoreBucket = MarketScoreBucket.Competitive
                        }
                    ]
                }
            ]
        };
    }

    private static WorldMarketAnalysis WorldWithListingBand(
        AnalyzedMarketListing listing,
        int quantityNeeded,
        int bandQuantity,
        bool isPrimaryUsableBand,
        int listingCount = 1)
    {
        var depth = bandQuantity >= Math.Max(quantityNeeded / 2, 1)
            ? PriceBandDepth.Deep
            : bandQuantity >= Math.Max(quantityNeeded / 4, 1)
                ? PriceBandDepth.Usable
                : PriceBandDepth.Thin;
        var competitiveness = listing.PriceSanity switch
        {
            MarketListingPriceSanity.LowOutlier => PriceBandCompetitiveness.LowOutlier,
            MarketListingPriceSanity.Insane or MarketListingPriceSanity.Outlier => PriceBandCompetitiveness.Insane,
            _ when listing.Competitiveness is MarketListingCompetitiveness.Fair or MarketListingCompetitiveness.Uncompetitive => PriceBandCompetitiveness.Uncompetitive,
            _ => PriceBandCompetitiveness.Competitive
        };

        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            QuantityNeeded = quantityNeeded,
            PrimaryUsableAverageUnitPrice = 100,
            Listings = [listing],
            PriceBands =
            [
                new MarketPriceBand
                {
                    FirstListingIndex = listing.SortIndex,
                    LastListingIndex = listing.SortIndex,
                    MinUnitPrice = listing.PricePerUnit,
                    MaxUnitPrice = listing.PricePerUnit,
                    WeightedAverageUnitPrice = listing.PricePerUnit,
                    ListingCount = listingCount,
                    Quantity = bandQuantity,
                    Competitiveness = competitiveness,
                    Depth = depth,
                    IsPrimaryUsableBand = isPrimaryUsableBand &&
                        competitiveness == PriceBandCompetitiveness.Competitive &&
                        depth is PriceBandDepth.Usable or PriceBandDepth.Deep
                }
            ]
        };
    }

    private static AnalyzedMarketListing Listing(
        int sortIndex,
        int quantity,
        long pricePerUnit,
        MarketListingPriceSanity priceSanity = MarketListingPriceSanity.Sane,
        MarketListingCompetitiveness competitiveness = MarketListingCompetitiveness.Unknown)
    {
        return new AnalyzedMarketListing
        {
            SortIndex = sortIndex,
            Quantity = quantity,
            PricePerUnit = pricePerUnit,
            PriceSanity = priceSanity,
            Competitiveness = competitiveness
        };
    }

    private static MarketItemAnalysis ScopeAnalysis(
        int itemId,
        string name,
        int scopeSaneQuantity,
        int scopePrimaryUsableQuantity)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    QuantityNeeded = 100,
                    SaneThresholdUnitPrice = 200,
                    ScopeSaneQuantity = scopeSaneQuantity,
                    PrimaryUsableQuantity = scopePrimaryUsableQuantity,
                    CoverageBucket = MarketCoverageBucket.None,
                    Scores =
                    [
                        new WorldLensScore
                        {
                            Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                            Rank = 1,
                            ScoreBucket = MarketScoreBucket.Unavailable
                        }
                    ]
                }
            ]
        };
    }

    private static WorldMarketAnalysis ScopeWorld(
        string worldName,
        int scopeSaneQuantity,
        int scopePrimaryUsableQuantity,
        int rank = 1,
        decimal goodAverage = 0,
        decimal worldCompetitiveAverage = 0)
    {
        var priceSignalQuantity = worldCompetitiveAverage > 0
            ? scopeSaneQuantity
            : 0;

        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = worldName,
            QuantityNeeded = 100,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = scopeSaneQuantity,
            PrimaryUsableQuantity = scopePrimaryUsableQuantity,
            PriceSignalQuantity = priceSignalQuantity,
            AnalysisCompetitiveAverageUnitPrice = goodAverage,
            PrimaryUsableAverageUnitPrice = worldCompetitiveAverage,
            PriceSignalAverageUnitPrice = worldCompetitiveAverage,
            PriceSignalDepth = GetPriceSignalDepth(priceSignalQuantity, 100),
            DataQualityBucket = MarketDataQualityBucket.Current,
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Rank = rank,
                    ScoreBucket = MarketScoreBucket.Unavailable
                }
            ]
        };
    }

    private static PriceBandDepth GetPriceSignalDepth(int quantity, int quantityNeeded)
    {
        if (quantity <= 0 || quantityNeeded <= 0)
        {
            return PriceBandDepth.None;
        }

        if (quantity >= Math.Max(quantityNeeded / 2, 1))
        {
            return PriceBandDepth.Deep;
        }

        return quantity >= Math.Max(quantityNeeded / 4, 1)
            ? PriceBandDepth.Usable
            : PriceBandDepth.Thin;
    }

    private static DetailedShoppingPlan CreatePlanWithCoverage(decimal exactNeededCost, decimal cashOutCost)
    {
        var coverage = CreateCoverageOption(
            MarketCoverageTier.SingleWorld,
            exactNeededCost,
            cashOutCost,
            isDefaultEligible: true);
        return new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Coverage Item",
            QuantityNeeded = 10,
            CoverageSet = new MarketCoverageSet(
                100,
                "Coverage Item",
                10,
                SingleWorld: coverage,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: null,
                AllCandidates: [coverage])
        };
    }

    private static DetailedShoppingPlan CreatePlanWithCheapestObservedOnly(decimal exactNeededCost)
    {
        var coverage = CreateCoverageOption(
            MarketCoverageTier.CheapestObserved,
            exactNeededCost,
            exactNeededCost,
            isDefaultEligible: false);
        return new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Diagnostic Item",
            QuantityNeeded = 10,
            CoverageSet = new MarketCoverageSet(
                100,
                "Diagnostic Item",
                10,
                SingleWorld: null,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: coverage,
                AllCandidates: [coverage])
        };
    }

    private static DetailedShoppingPlan CreatePlanWithSupportedSingleWorldAndCheapestObservedDiagnostic()
    {
        var singleWorld = CreateCoverageOption(
            MarketCoverageTier.SingleWorld,
            exactNeededCost: 3_529_250,
            cashOutCost: 3_551_750,
            isDefaultEligible: true);
        var cheapestObserved = CreateCoverageOption(
            MarketCoverageTier.CheapestObserved,
            exactNeededCost: 3_004_308,
            cashOutCost: 3_009_102,
            isDefaultEligible: false);

        return new DetailedShoppingPlan
        {
            ItemId = 5059,
            Name = "Cobalt Ingot",
            QuantityNeeded = 3_996,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Crystal",
                WorldName = "Coeurl",
                TotalCost = 3_551_750,
                TotalQuantityPurchased = 4_021,
                HasSufficientStock = true
            },
            CoverageSet = new MarketCoverageSet(
                5059,
                "Cobalt Ingot",
                3_996,
                SingleWorld: singleWorld,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: cheapestObserved,
                AllCandidates: [singleWorld, cheapestObserved])
        };
    }

    private static MarketCoverageOption CreateCoverageOption(
        MarketCoverageTier tier,
        decimal exactNeededCost,
        decimal cashOutCost,
        bool isDefaultEligible)
    {
        return new MarketCoverageOption(
            CandidateId: $"100-10-{tier.ToString().ToLowerInvariant()}-nqorhq-siren",
            Tier: tier,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: MarketCoverageQualityPolicy.NqOrHq,
            QuantityCovered: 10,
            QuantityToPurchase: 12,
            ExcessQuantity: 2,
            ExactNeededCost: exactNeededCost,
            CashOutCost: cashOutCost,
            AverageUnitCost: exactNeededCost / 10,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityCovered: 10,
                    QuantityToPurchase: 12,
                    ExactNeededCost: exactNeededCost,
                    CashOutCost: cashOutCost)
            ],
            Listings: [],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: 10,
                LargestContribution: 10,
                ExcessQuantity: 2),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: isDefaultEligible,
            DegradedReason: null);
    }
}
