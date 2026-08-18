using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class TradeMaterialQuoteSpecificationTests
{
    private static readonly DateTime QuotedAt = new(2026, 8, 18, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void QuoteUsesWholeListingRouteCashAndControlledAllowance()
    {
        var result = BuildQuote(cash: 7_477_139, worldStops: 6, transfers: 1, evidenceAgeMinutes: 30);

        var quote = Assert.IsType<TradeMaterialQuote>(result.Quote);
        Assert.Equal(7_477_139m, quote.RouteCashRequired);
        Assert.Equal(250_000m, quote.SafetyAllowance);
        Assert.Equal(7_727_139m, quote.MaterialReimbursement);
        Assert.Equal(7_477_139m, Assert.Single(quote.Lines).CashRequired);
        Assert.Equal(quote.RouteCashRequired, result.MaterialLines.Sum(line => line.UnitCost * line.Quantity));
    }

    [Theory]
    [InlineData(9, 1, 30)]
    [InlineData(6, 3, 30)]
    [InlineData(6, 1, 121)]
    public void QuoteFailsClosedOutsideCompanyRouteOrFreshnessEnvelope(
        int worldStops,
        int transfers,
        int evidenceAgeMinutes)
    {
        var result = BuildQuote(1_000_000, worldStops, transfers, evidenceAgeMinutes);

        Assert.False(result.IsComplete);
        Assert.Null(result.Quote);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Contains("company policy", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuoteRejectsASelectionAboveTheCompanyConsolidationPremium()
    {
        var result = BuildQuote(
            cash: 1_060_000,
            worldStops: 2,
            transfers: 0,
            evidenceAgeMinutes: 30,
            cheapestCash: 1_000_000);

        Assert.False(result.IsComplete);
        Assert.Null(result.Quote);
    }

    [Fact]
    public void IncompleteOptimizationNamesTheAppliedPolicyEnvelope()
    {
        var policy = TradeMaterialPricingPolicy.Default with
        {
            MaximumWorldStops = 7,
            MaximumDataCenterTransfers = 3,
            MaximumConsolidationPremiumPercent = 15,
            MaximumEvidenceAgeMinutes = 90
        };

        var result = new TradeMaterialQuoteService().Build(
            new ProcurementRouteOptimizationResult([], Decision: null, IsComplete: false),
            [new MaterialAggregate { ItemId = 5111, Name = "Gold Ore", TotalQuantity = 14_985 }],
            policy,
            QuotedAt);

        Assert.False(result.IsComplete);
        Assert.Null(result.Quote);
        Assert.Contains("7 worlds", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("3 data-center transfers", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("15% consolidation premium", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("90 minutes old", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanyPolicyControlsAllowanceLifetimeAndReasonableRouteBounds()
    {
        var policy = TradeMaterialPricingPolicy.Default with
        {
            MaximumWorldStops = 6,
            MaximumDataCenterTransfers = 1,
            SafetyAllowancePercent = 20,
            MaximumSafetyAllowanceGil = 500_000,
            QuoteLifetimeMinutes = 45
        };

        var result = BuildQuote(1_000_000, 6, 1, 30, policy: policy);

        var quote = Assert.IsType<TradeMaterialQuote>(result.Quote);
        Assert.Equal(200_000m, quote.SafetyAllowance);
        Assert.Equal(QuotedAt.AddMinutes(45), quote.ExpiresAtUtc);
        Assert.Equal(TradeMaterialPricingPolicyNormalizer.Fingerprint(policy), quote.PolicyFingerprint);
        Assert.Equal(policy, quote.AppliedPolicy);
    }

    [Fact]
    public void QuoteSkipsAStalePreferredFrontierRouteWhenAFreshReasonableRouteExists()
    {
        var stalePlan = Plan("Stale", 1_000_000, evidenceAgeMinutes: 121);
        var freshPlan = Plan("Fresh", 1_020_000, evidenceAgeMinutes: 30);
        var stale = Selection("stale", stalePlan, 1_000_000, worldStops: 1);
        var fresh = Selection("fresh", freshPlan, 1_020_000, worldStops: 2);
        var decision = new MarketRouteDecision(
            9,
            0.05m,
            1_000_000,
            1_000_000,
            0,
            1,
            1,
            0,
            0,
            true,
            "Aether")
        {
            ToleranceSelections = [stale, fresh],
            IncludeSplitPurchases = true
        };

        var result = new TradeMaterialQuoteService().Build(
            new ProcurementRouteOptimizationResult([stalePlan], decision),
            [new MaterialAggregate { ItemId = 5111, Name = "Gold Ore", TotalQuantity = 14_985 }],
            TradeMaterialPricingPolicy.Default,
            QuotedAt);

        var quote = Assert.IsType<TradeMaterialQuote>(result.Quote);
        Assert.Equal("fresh", quote.RouteSelectionKey);
        Assert.Equal(1_020_000m, quote.RouteCashRequired);
    }

    [Fact]
    public async Task CompanyFreshnessPolicyRemovesStaleWorldsBeforeRouteOptimization()
    {
        var stale = SpecificationFixtures.World("Aether", "Stale", 14_985, 100);
        stale.MarketUploadedAtUtc = QuotedAt.AddMinutes(-121);
        var fresh = SpecificationFixtures.World("Dynamis", "Fresh", 14_985, 102);
        fresh.MarketUploadedAtUtc = QuotedAt.AddMinutes(-30);
        var evidence = SpecificationFixtures.Evidence(5111, "Gold Ore", 14_985, stale, fresh);
        evidence.RecommendedWorld = stale;

        var quoteService = new TradeMaterialQuoteService();
        var input = quoteService.PrepareOptimizationInput(
            [evidence],
            TradeMaterialPricingPolicy.Default,
            QuotedAt);
        var filtered = Assert.Single(input);

        Assert.Equal("Fresh", Assert.Single(filtered.WorldOptions).WorldName);
        Assert.Null(filtered.RecommendedWorld);

        var optimization = await new MarketShoppingService(null!)
            .OptimizeProcurementRouteWithDecisionAsync(
                input,
                SpecificationFixtures.Config(tolerance: 11, enableSplitWorld: true));
        var result = quoteService.Build(
            optimization,
            [new MaterialAggregate { ItemId = 5111, Name = "Gold Ore", TotalQuantity = 14_985 }],
            TradeMaterialPricingPolicy.Default,
            QuotedAt);

        var quote = Assert.IsType<TradeMaterialQuote>(result.Quote);
        Assert.Equal(1_528_470m, quote.RouteCashRequired);
        Assert.Contains("Fresh", Assert.Single(quote.Lines).Worlds.Single());
    }

    private static TradeMaterialQuoteResult BuildQuote(
        long cash,
        int worldStops,
        int transfers,
        int evidenceAgeMinutes,
        long? cheapestCash = null,
        TradeMaterialPricingPolicy? policy = null)
    {
        var world = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            TotalCost = cash,
            TotalQuantityPurchased = 15_000,
            HasSufficientStock = true,
            MarketUploadedAtUtc = QuotedAt.AddMinutes(-evidenceAgeMinutes)
        };
        var plan = new DetailedShoppingPlan
        {
            ItemId = 5111,
            Name = "Gold Ore",
            QuantityNeeded = 14_985,
            WorldOptions = [world],
            RecommendedWorld = world
        };
        var selection = new MarketRouteToleranceSelection(
            9,
            9,
            "route",
            cash,
            0,
            worldStops,
            transfers,
            0,
            [plan],
            []);
        var decision = new MarketRouteDecision(
            9,
            0.05m,
            cheapestCash ?? cash,
            cash,
            0,
            worldStops,
            worldStops,
            transfers,
            transfers,
            true,
            "Aether")
        {
            ToleranceSelections = [selection],
            IncludeSplitPurchases = true
        };
        return new TradeMaterialQuoteService().Build(
            new ProcurementRouteOptimizationResult([plan], decision),
            [new MaterialAggregate
            {
                ItemId = 5111,
                Name = "Gold Ore",
                TotalQuantity = 14_985
            }],
            policy ?? TradeMaterialPricingPolicy.Default,
            QuotedAt);
    }

    private static DetailedShoppingPlan Plan(string worldName, long cash, int evidenceAgeMinutes)
    {
        var world = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = worldName,
            TotalCost = cash,
            TotalQuantityPurchased = 15_000,
            HasSufficientStock = true,
            MarketUploadedAtUtc = QuotedAt.AddMinutes(-evidenceAgeMinutes)
        };
        return new DetailedShoppingPlan
        {
            ItemId = 5111,
            Name = "Gold Ore",
            QuantityNeeded = 14_985,
            WorldOptions = [world],
            RecommendedWorld = world
        };
    }

    private static MarketRouteToleranceSelection Selection(
        string key,
        DetailedShoppingPlan plan,
        long cash,
        int worldStops) => new(
            9,
            9,
            key,
            cash,
            0,
            worldStops,
            0,
            0,
            [plan],
            []);
}
