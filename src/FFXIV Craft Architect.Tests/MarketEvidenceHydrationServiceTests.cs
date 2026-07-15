using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public sealed class MarketEvidenceHydrationServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeedsHydration_MarketPlanWithMissingEvidence_ReturnsTrue()
    {
        var session = CreateSession([], [], publishedAtUtc: null);

        Assert.True(MarketEvidenceHydrationService.NeedsHydration(session, NowUtc));
    }

    [Fact]
    public void NeedsHydration_FreshActionableEvidence_ReturnsFalse()
    {
        var session = CreateSession(
            [new MarketItemAnalysis { ItemId = 5061, Name = "Darksteel Nugget", QuantityNeeded = 1 }],
            [new DetailedShoppingPlan { ItemId = 5061, Name = "Darksteel Nugget", QuantityNeeded = 1 }],
            NowUtc - TimeSpan.FromMinutes(30));

        Assert.False(MarketEvidenceHydrationService.NeedsHydration(session, NowUtc));
    }

    [Fact]
    public void NeedsHydration_StaleActionableEvidence_ReturnsTrue()
    {
        var session = CreateSession(
            [new MarketItemAnalysis { ItemId = 5061, Name = "Darksteel Nugget", QuantityNeeded = 1 }],
            [new DetailedShoppingPlan { ItemId = 5061, Name = "Darksteel Nugget", QuantityNeeded = 1 }],
            NowUtc - TimeSpan.FromHours(2));

        Assert.True(MarketEvidenceHydrationService.NeedsHydration(session, NowUtc));
    }

    [Fact]
    public void NeedsHydration_PlanWithoutMarketCandidates_ReturnsFalse()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 5061,
                    Name = "Darksteel Nugget",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    CanBuyFromMarket = false
                }
            ]
        };
        var session = new PlanSessionLoadResult(
            new StoredPlan(),
            plan,
            [],
            [],
            [],
            null,
            null,
            null,
            null);

        Assert.False(MarketEvidenceHydrationService.NeedsHydration(session, NowUtc));
    }

    private static PlanSessionLoadResult CreateSession(
        IReadOnlyList<MarketItemAnalysis> analyses,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        DateTime? publishedAtUtc)
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 5061,
                    Name = "Darksteel Nugget",
                    Quantity = 1,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true,
                    MarketPrice = 2_000
                }
            ]
        };
        var scope = publishedAtUtc.HasValue
            ? new PublishedMarketAnalysisScopeSnapshot(
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                ["Aether"],
                MarketAcquisitionLens.MinimumUpfrontCost,
                1,
                publishedAtUtc.Value)
            : null;

        return new PlanSessionLoadResult(
            new StoredPlan(),
            plan,
            [],
            analyses,
            shoppingPlans,
            null,
            null,
            scope,
            null);
    }
}
