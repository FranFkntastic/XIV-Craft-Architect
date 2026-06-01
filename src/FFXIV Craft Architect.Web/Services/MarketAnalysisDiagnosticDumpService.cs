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
            ShoppingPlan: shoppingPlan);
    }

    public string Serialize(MarketAnalysisDiagnosticDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return JsonSerializer.Serialize(dump, JsonOptions);
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
    int? SelectedItemId);

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
    DetailedShoppingPlan? ShoppingPlan);
