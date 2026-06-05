using System.Text.Json;
using System.Text.Json.Serialization;

using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketAnalysisDiagnosticDumpService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static MarketAnalysisDiagnosticDumpService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly IMarketAnalysisDetailHydrationService? _detailHydrationService;

    public MarketAnalysisDiagnosticDumpService(
        IMarketAnalysisDetailHydrationService? detailHydrationService = null)
    {
        _detailHydrationService = detailHydrationService;
    }

    public MarketAnalysisDiagnosticDump? BuildDump(
        IReadOnlyList<MarketItemAnalysis> analyses,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        MarketAnalysisDiagnosticDumpContext context)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        if (analyses.Count == 0)
        {
            return null;
        }

        var selectedAnalysis = context.SelectedItemId.HasValue
            ? analyses.FirstOrDefault(analysis => analysis.ItemId == context.SelectedItemId.Value)
            : null;
        var selection = selectedAnalysis == null
            ? analyses[0]
            : selectedAnalysis;
        var shoppingPlan = shoppingPlans.FirstOrDefault(plan => plan.ItemId == selection.ItemId);

        return new MarketAnalysisDiagnosticDump(
            SchemaVersion: 1,
            Tool: "market-analysis-diagnostic-dump",
            ExportedAtUtc: DateTime.UtcNow,
            Selection: new MarketAnalysisDiagnosticSelection(
                RequestedItemId: context.SelectedItemId,
                ExportedItemId: selection.ItemId,
                ExportedItemName: selection.Name,
                UsedFallbackSelection: selectedAnalysis == null),
            Context: context,
            Analysis: selection,
            ShoppingPlan: shoppingPlan,
            DetailAvailability: []);
    }

    public async Task<MarketAnalysisDiagnosticDump?> BuildDumpAsync(
        IReadOnlyList<MarketItemAnalysis> analyses,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        MarketAnalysisDiagnosticDumpContext context,
        CancellationToken cancellationToken = default)
    {
        var dump = BuildDump(analyses, shoppingPlans, context);
        if (dump == null || _detailHydrationService == null)
        {
            return dump;
        }

        var hydratedWorlds = new List<WorldMarketAnalysis>();
        var availability = new List<MarketAnalysisDiagnosticDetailAvailability>();
        foreach (var world in dump.Analysis.Worlds)
        {
            var detail = await _detailHydrationService.LoadWorldDetailAsync(
                context.MarketIntelligencePublicationId,
                dump.Analysis.ItemId,
                world,
                cancellationToken);
            var hydratedWorld = detail.HasListings
                ? CloneWorldWithDetail(world, detail)
                : world;
            hydratedWorlds.Add(hydratedWorld);
            availability.Add(new MarketAnalysisDiagnosticDetailAvailability(
                world.DataCenter,
                world.WorldName,
                detail.Status,
                detail.Message,
                detail.Listings.Count,
                detail.FromEmbeddedHotState));
        }

        var hydratedAnalysis = CloneAnalysisWithWorlds(dump.Analysis, hydratedWorlds);
        return dump with
        {
            Analysis = hydratedAnalysis,
            DetailAvailability = availability
        };
    }

    public string Serialize(MarketAnalysisDiagnosticDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    private static MarketItemAnalysis CloneAnalysisWithWorlds(
        MarketItemAnalysis analysis,
        IReadOnlyList<WorldMarketAnalysis> worlds)
    {
        return new MarketItemAnalysis
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = analysis.Scope,
            LoadedAtUtc = analysis.LoadedAtUtc,
            AnalysisScopeBaselineUnitPrice = analysis.AnalysisScopeBaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = analysis.AnalysisScopeAverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = analysis.AnalysisScopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = analysis.AnalysisScopeMedianUnitPrice,
            CompetitiveThresholdUnitPrice = analysis.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = analysis.SaneThresholdUnitPrice,
            RequestedDataCenters = analysis.RequestedDataCenters.ToList(),
            PresentDataCenters = analysis.PresentDataCenters.ToList(),
            MissingDataCenters = analysis.MissingDataCenters.ToList(),
            WorstDataQualityBucket = analysis.WorstDataQualityBucket,
            Worlds = worlds.ToList(),
            Warning = analysis.Warning
        };
    }

    private static WorldMarketAnalysis CloneWorldWithDetail(
        WorldMarketAnalysis world,
        MarketAnalysisWorldDetailHydrationResult detail)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = world.DataCenter,
            WorldName = world.WorldName,
            QuantityNeeded = world.QuantityNeeded,
            CompetitiveQuantity = world.CompetitiveQuantity,
            LocalCompetitiveQuantity = world.LocalCompetitiveQuantity,
            ScopeCompetitiveQuantity = world.ScopeCompetitiveQuantity,
            ScopeSaneQuantity = world.ScopeSaneQuantity,
            ScopeUncompetitiveQuantity = world.ScopeUncompetitiveQuantity,
            ScopeInsaneQuantity = world.ScopeInsaneQuantity,
            TotalSaneQuantity = world.TotalSaneQuantity,
            TotalListingQuantity = world.TotalListingQuantity,
            CompetitiveCoverageRatio = world.CompetitiveCoverageRatio,
            ScopeCompetitiveCoverageRatio = world.ScopeCompetitiveCoverageRatio,
            ScopeSaneCoverageRatio = world.ScopeSaneCoverageRatio,
            SaneCoverageRatio = world.SaneCoverageRatio,
            AnalysisScopeBaselineUnitPrice = world.AnalysisScopeBaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = world.AnalysisScopeAverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = world.AnalysisScopeCompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = world.ScopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = world.AnalysisScopeMedianUnitPrice,
            CompetitiveThresholdUnitPrice = world.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = world.SaneThresholdUnitPrice,
            CoverageBucket = world.CoverageBucket,
            FetchedAtUtc = world.FetchedAtUtc,
            MarketUploadedAtUtc = world.MarketUploadedAtUtc,
            DataAgeSource = world.DataAgeSource,
            DataAge = world.DataAge,
            DataQualityScore = world.DataQualityScore,
            DataQualityBucket = world.DataQualityBucket,
            PriceBands = detail.PriceBands.ToList(),
            Listings = detail.Listings.ToList(),
            Scores = world.Scores.ToList()
        };
    }
}

public sealed record MarketAnalysisDiagnosticDumpContext(
    string? PlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    bool SearchEntireRegion,
    MarketAcquisitionLens Lens,
    MarketSortOption SortPreference,
    MarketAnalysisGridSortColumn? GridSortColumn,
    bool GridSortDescending,
    MarketAnalysisWorldGridSortColumn? WorldSortColumn,
    bool WorldSortDescending,
    int? SelectedItemId,
    Guid? MarketIntelligencePublicationId = null);

public sealed record MarketAnalysisDiagnosticSelection(
    int? RequestedItemId,
    int ExportedItemId,
    string ExportedItemName,
    bool UsedFallbackSelection);

public sealed record MarketAnalysisDiagnosticDump(
    int SchemaVersion,
    string Tool,
    DateTime ExportedAtUtc,
    MarketAnalysisDiagnosticSelection Selection,
    MarketAnalysisDiagnosticDumpContext Context,
    MarketItemAnalysis Analysis,
    DetailedShoppingPlan? ShoppingPlan,
    IReadOnlyList<MarketAnalysisDiagnosticDetailAvailability> DetailAvailability);

public sealed record MarketAnalysisDiagnosticDetailAvailability(
    string DataCenter,
    string WorldName,
    MarketAnalysisWorldDetailHydrationStatus Status,
    string? Message,
    int ListingCount,
    bool FromEmbeddedHotState);
