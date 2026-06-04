using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CommissionCostBasisResolver
{
    public IReadOnlyList<CommissionPayrollInputLine> BuildMarketRecommendationLines(
        IReadOnlyList<MaterialAggregate> demand,
        IReadOnlyList<MarketItemAnalysis> marketAnalyses,
        IReadOnlyList<DetailedShoppingPlan>? shoppingPlans = null)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(marketAnalyses);

        var analysesByItemId = marketAnalyses
            .GroupBy(analysis => analysis.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var plansByItemId = (shoppingPlans ?? Array.Empty<DetailedShoppingPlan>())
            .GroupBy(plan => plan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return demand
            .Where(item => item.TotalQuantity > 0)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .Select(item => BuildLine(item, analysesByItemId, plansByItemId))
            .ToArray();
    }

    private static CommissionPayrollInputLine BuildLine(
        MaterialAggregate item,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysesByItemId,
        IReadOnlyDictionary<int, DetailedShoppingPlan> plansByItemId)
    {
        var warnings = new List<string>();
        var evidenceSource = "Plan price";
        var unitCostExplanation = $"No market-analysis evidence found for {item.Name}; using plan price: {item.UnitPrice:N0}g.";
        DateTime? evidenceTimestampUtc = null;
        var unitCost = item.UnitPrice;

        if (analysesByItemId.TryGetValue(item.ItemId, out var analysis))
        {
            var selectedCost = SelectUnitCost(analysis);
            unitCost = selectedCost.UnitCost;
            evidenceTimestampUtc = analysis.LoadedAtUtc;
            evidenceSource = selectedCost.SourceLabel;
            unitCostExplanation = $"{item.Name} unit cost uses {selectedCost.SourceDescription}: {unitCost:N0}g.";

            if (unitCost <= 0)
            {
                warnings.Add($"Market evidence for {item.Name} did not include a usable unit cost.");
            }

            if (!analysis.HasCompleteScopeData)
            {
                warnings.Add($"Market evidence for {item.Name} is missing at least one requested data center.");
            }

            if (plansByItemId.TryGetValue(item.ItemId, out var shoppingPlan))
            {
                var contexts = GetRecommendationDataAgeContexts(shoppingPlan).ToArray();
                var staleContexts = contexts
                    .Where(context => context.Bucket is MarketDataQualityBucket.VeryOld or MarketDataQualityBucket.Ancient)
                    .ToArray();
                warnings.AddRange(staleContexts.Select(context => FormatRecommendationWarning(item.Name, context)));

                var tooltipContext = staleContexts.FirstOrDefault();
                if (tooltipContext != null)
                {
                    unitCostExplanation += $" {FormatRecommendationTooltip(tooltipContext)}";
                }
            }

            if (!string.IsNullOrWhiteSpace(analysis.Warning))
            {
                warnings.Add($"{item.Name}: {analysis.Warning}");
            }
        }
        else
        {
            warnings.Add($"No market-analysis evidence was available for {item.Name}.");
        }

        if (unitCost <= 0)
        {
            warnings.Add($"Unit cost for {item.Name} is zero; payroll estimate may be incomplete.");
        }

        return new CommissionPayrollInputLine(
            item.ItemId,
            item.Name,
            item.TotalQuantity,
            unitCost,
            item.RequiresHq,
            CommissionMaterialResponsibility.Crafter,
            evidenceSource,
            unitCostExplanation,
            evidenceTimestampUtc,
            warnings);
    }

    private static IEnumerable<RecommendationDataAgeContext> GetRecommendationDataAgeContexts(DetailedShoppingPlan shoppingPlan)
    {
        if (shoppingPlan.RecommendedSplit is { Count: > 1 })
        {
            foreach (var split in shoppingPlan.RecommendedSplit)
            {
                var world = shoppingPlan.WorldOptions.FirstOrDefault(option =>
                    IsSameWorld(option.DataCenter, split.DataCenter) &&
                    IsSameWorld(option.WorldName, split.WorldName));
                if (world == null)
                {
                    continue;
                }

                yield return new RecommendationDataAgeContext(
                    world.DataCenter,
                    world.WorldName,
                    IsSplit: true,
                    world.MarketDataQualityBucket,
                    world.MarketDataAge,
                    world.MarketUploadedAtUtc);
            }

            yield break;
        }

        var recommendedWorld = shoppingPlan.RecommendedWorld;
        if (recommendedWorld == null)
        {
            yield break;
        }

        yield return new RecommendationDataAgeContext(
            recommendedWorld.DataCenter,
            recommendedWorld.WorldName,
            IsSplit: false,
            recommendedWorld.MarketDataQualityBucket,
            recommendedWorld.MarketDataAge,
            recommendedWorld.MarketUploadedAtUtc);
    }

    private static string FormatRecommendationWarning(string itemName, RecommendationDataAgeContext context)
    {
        var bucket = FormatBucket(context.Bucket);
        var age = FormatAge(context.DataAge);
        return context.IsSplit
            ? $"{itemName} uses {bucket} market data in a recommended split: {context.WorldName} uploaded {age} ago."
            : $"{itemName} uses {bucket} market data from {context.WorldName} uploaded {age} ago.";
    }

    private static string FormatRecommendationTooltip(RecommendationDataAgeContext context)
    {
        return context.IsSplit
            ? $"Recommended split includes {context.WorldName}; upload age {FormatAge(context.DataAge)}."
            : $"Recommended world {context.WorldName}; upload age {FormatAge(context.DataAge)}.";
    }

    private static string FormatBucket(MarketDataQualityBucket bucket)
    {
        return bucket == MarketDataQualityBucket.VeryOld
            ? "very old"
            : bucket.ToString().ToLowerInvariant();
    }

    private static string FormatAge(TimeSpan? age)
    {
        if (age == null)
        {
            return "unknown";
        }

        if (age.Value.TotalHours >= 1)
        {
            return $"{Math.Round(age.Value.TotalHours, 0, MidpointRounding.AwayFromZero):N0}h";
        }

        return $"{Math.Max(1, (int)Math.Round(age.Value.TotalMinutes, 0, MidpointRounding.AwayFromZero)):N0}m";
    }

    private static bool IsSameWorld(string left, string right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static SelectedUnitCost SelectUnitCost(MarketItemAnalysis analysis)
    {
        if (analysis.AnalysisScopeCompetitiveAverageUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.AnalysisScopeCompetitiveAverageUnitPrice,
                "Market competitive average",
                "the market-analysis competitive average");
        }

        if (analysis.AnalysisScopeAverageUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.AnalysisScopeAverageUnitPrice,
                "Market average",
                "the market-analysis average fallback");
        }

        if (analysis.AnalysisScopeMedianUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.AnalysisScopeMedianUnitPrice,
                "Market median",
                "the market-analysis median fallback");
        }

        return new SelectedUnitCost(
            analysis.AnalysisScopeBaselineUnitPrice,
            "Market baseline",
            "the market-analysis baseline fallback");
    }

    private sealed record SelectedUnitCost(
        decimal UnitCost,
        string SourceLabel,
        string SourceDescription);

    private sealed record RecommendationDataAgeContext(
        string DataCenter,
        string WorldName,
        bool IsSplit,
        MarketDataQualityBucket Bucket,
        TimeSpan? DataAge,
        DateTime? UploadedAtUtc);
}
