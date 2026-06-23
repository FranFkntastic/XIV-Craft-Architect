using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CommissionCostBasisResolver
{
    public IReadOnlyList<CommissionPayrollInputLine> BuildSelectedSourceLines(
        IReadOnlyList<RecipeDemandRow> demand,
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
            .Where(row => row.Quantity > 0)
            .GroupBy(row => row.ItemId)
            .Select(ToSelectedSourceDemand)
            .Where(item => item.TotalQuantity > 0)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .Select(item => BuildSelectedSourceLine(item, analysesByItemId, plansByItemId))
            .ToArray();
    }

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

    private static SelectedSourceDemand ToSelectedSourceDemand(IGrouping<int, RecipeDemandRow> group)
    {
        var rows = group.ToArray();
        var primary = rows
            .OrderByDescending(row => row.ViewKind == RecipeDemandViewKind.ActiveProcurement)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .First();
        return new SelectedSourceDemand(
            primary.ItemId,
            primary.ItemName,
            rows.Sum(row => row.Quantity),
            rows.Any(row => row.MustBeHq),
            primary.Source,
            primary.CanBuyFromMarket,
            primary.CanBuyFromVendor,
            primary.CanBeHq,
            primary.UnitPrice,
            primary.HqUnitPrice,
            primary.SelectedVendor?.Price ?? primary.VendorUnitPrice);
    }

    private static CommissionPayrollInputLine BuildSelectedSourceLine(
        SelectedSourceDemand item,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysesByItemId,
        IReadOnlyDictionary<int, DetailedShoppingPlan> plansByItemId)
    {
        var warnings = new List<string>();
        var evidenceSource = "Plan price";
        var unitCostExplanation = $"No selected-source evidence found for {item.Name}; using plan price: {item.UnitPrice:N0}g.";
        DateTime? evidenceTimestampUtc = null;
        var unitCost = item.UnitPrice;

        analysesByItemId.TryGetValue(item.ItemId, out var analysis);
        plansByItemId.TryGetValue(item.ItemId, out var shoppingPlan);

        var selectedCost = SelectSelectedSourceUnitCost(item, shoppingPlan);
        if (selectedCost != null)
        {
            unitCost = selectedCost.UnitCost;
            evidenceTimestampUtc = selectedCost.EvidenceTimestampUtc ?? analysis?.LoadedAtUtc;
            evidenceSource = selectedCost.SourceLabel;
            unitCostExplanation = $"{item.Name} unit cost uses {selectedCost.SourceDescription}: {unitCost:N0}g.";
        }
        else if (shoppingPlan == null)
        {
            unitCost = item.Source == AcquisitionSource.MarketBuyHq && item.HqUnitPrice > 0
                ? item.HqUnitPrice
                : item.Source == AcquisitionSource.VendorBuy && item.VendorUnitPrice > 0
                    ? item.VendorUnitPrice
                    : item.UnitPrice;
            evidenceSource = item.Source == AcquisitionSource.VendorBuy ? "Vendor price" : "Plan price";
            unitCostExplanation = $"{item.Name} unit cost uses the selected source plan price: {unitCost:N0}g.";
        }

        if (analysis != null)
        {
            if (!analysis.HasCompleteScopeData)
            {
                warnings.Add($"Market evidence for {item.Name} is missing at least one requested data center.");
            }

            if (!string.IsNullOrWhiteSpace(analysis.Warning))
            {
                warnings.Add($"{item.Name}: {analysis.Warning}");
            }
        }
        else if (selectedCost == null)
        {
            warnings.Add($"No market-analysis evidence was available for {item.Name}.");
        }

        if (shoppingPlan != null)
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

        if (unitCost <= 0)
        {
            warnings.Add($"Unit cost for {item.Name} is zero; payroll estimate may be incomplete.");
        }

        return new CommissionPayrollInputLine(
            item.ItemId,
            item.Name,
            item.TotalQuantity,
            unitCost,
            item.RequiresHq || item.Source == AcquisitionSource.MarketBuyHq,
            CommissionMaterialResponsibility.Crafter,
            evidenceSource,
            unitCostExplanation,
            evidenceTimestampUtc,
            warnings);
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

        analysesByItemId.TryGetValue(item.ItemId, out var analysis);
        plansByItemId.TryGetValue(item.ItemId, out var shoppingPlan);

        var selectedCost = shoppingPlan != null
            ? SelectAcquisitionUnitCost(item, shoppingPlan)
            : null;

        if (selectedCost == null && analysis != null)
        {
            selectedCost = SelectUnitCost(analysis);
        }

        if (selectedCost != null)
        {
            unitCost = selectedCost.UnitCost;
            evidenceTimestampUtc = selectedCost.EvidenceTimestampUtc ?? analysis?.LoadedAtUtc;
            evidenceSource = selectedCost.SourceLabel;
            unitCostExplanation = $"{item.Name} unit cost uses {selectedCost.SourceDescription}: {unitCost:N0}g.";
        }

        if (analysis != null)
        {

            if (unitCost <= 0)
            {
                warnings.Add($"Market evidence for {item.Name} did not include a usable unit cost.");
            }

            if (!analysis.HasCompleteScopeData)
            {
                warnings.Add($"Market evidence for {item.Name} is missing at least one requested data center.");
            }

            if (!string.IsNullOrWhiteSpace(analysis.Warning))
            {
                warnings.Add($"{item.Name}: {analysis.Warning}");
            }
        }
        else if (selectedCost == null)
        {
            warnings.Add($"No market-analysis evidence was available for {item.Name}.");
        }

        if (shoppingPlan != null)
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

    private static SelectedUnitCost? SelectAcquisitionUnitCost(
        MaterialAggregate item,
        DetailedShoppingPlan shoppingPlan)
    {
        var acquisition = MarketPurchaseCostProjectionService.Estimate(
            shoppingPlan,
            item.TotalQuantity,
            item.RequiresHq,
            includeVendor: true);
        if (!acquisition.IsDefaultEligible || !acquisition.HasCost || item.TotalQuantity <= 0)
        {
            return null;
        }

        var unitCost = Math.Ceiling(acquisition.Cost / item.TotalQuantity);
        var source = FormatAcquisitionSource(shoppingPlan, acquisition);
        return new SelectedUnitCost(
            unitCost,
            source.Label,
            source.Description,
            acquisition.World?.MarketUploadedAtUtc);
    }

    private static SelectedUnitCost? SelectSelectedSourceUnitCost(
        SelectedSourceDemand item,
        DetailedShoppingPlan? shoppingPlan)
    {
        if (item.TotalQuantity <= 0)
        {
            return null;
        }

        if (item.Source == AcquisitionSource.VendorBuy && item.VendorUnitPrice > 0)
        {
            return new SelectedUnitCost(
                item.VendorUnitPrice,
                "Vendor price",
                "the selected vendor price");
        }

        if (shoppingPlan == null)
        {
            return null;
        }

        bool? hqOnly = item.Source switch
        {
            AcquisitionSource.MarketBuyNq when item.CanBuyFromMarket && !item.RequiresHq => false,
            AcquisitionSource.MarketBuyHq when item.CanBuyFromMarket && item.CanBeHq => true,
            _ => null
        };
        if (hqOnly == null)
        {
            return null;
        }

        var acquisition = MarketPurchaseCostProjectionService.Estimate(
            shoppingPlan,
            item.TotalQuantity,
            hqOnly.Value,
            includeVendor: false);
        if (!acquisition.HasCost)
        {
            return null;
        }

        var unitCost = acquisition.Cost / item.TotalQuantity;
        var source = FormatAcquisitionSource(shoppingPlan, acquisition);
        return new SelectedUnitCost(
            unitCost,
            source.Label,
            source.Description,
            acquisition.World?.MarketUploadedAtUtc);
    }

    private static (string Label, string Description) FormatAcquisitionSource(
        DetailedShoppingPlan shoppingPlan,
        MarketPurchaseCostEstimate acquisition)
    {
        if (string.Equals(
                shoppingPlan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return ("Vendor price", "loaded gil vendor pricing");
        }

        if (shoppingPlan.RecommendedSplit is { Count: > 0 } && acquisition.World == null)
        {
            return ("Split procurement route", "the recommended split procurement route");
        }

        if (acquisition.World != null)
        {
            return ("Procurement route", $"the recommended procurement route from {acquisition.World.WorldName}");
        }

        return ("Procurement route", "the recommended procurement route");
    }

    private static SelectedUnitCost SelectUnitCost(MarketItemAnalysis analysis)
    {
        if (analysis.CostToCoverUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.CostToCoverUnitPrice,
                "Primary procurement shelf",
                "the primary procurement shelf cost to cover the requested quantity");
        }

        if (analysis.PrimaryProcurementShelfAverageUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.PrimaryProcurementShelfAverageUnitPrice,
                "Primary procurement shelf",
                "the primary procurement shelf average");
        }

        if (analysis.AnalysisCompetitiveAverageUnitPrice > 0)
        {
            return new SelectedUnitCost(
                analysis.AnalysisCompetitiveAverageUnitPrice,
                "Market evidence fallback",
                "the broad market evidence fallback");
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
        string SourceDescription,
        DateTime? EvidenceTimestampUtc = null);

    private sealed record SelectedSourceDemand(
        int ItemId,
        string Name,
        int TotalQuantity,
        bool RequiresHq,
        AcquisitionSource Source,
        bool CanBuyFromMarket,
        bool CanBuyFromVendor,
        bool CanBeHq,
        decimal UnitPrice,
        decimal HqUnitPrice,
        decimal VendorUnitPrice);

    private sealed record RecommendationDataAgeContext(
        string DataCenter,
        string WorldName,
        bool IsSplit,
        MarketDataQualityBucket Bucket,
        TimeSpan? DataAge,
        DateTime? UploadedAtUtc);
}
