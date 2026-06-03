using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record StoredPlanSnapshotMetrics(
    int PlanNodeCount,
    int ShoppingPlanCount,
    int MarketAnalysisCount,
    int PlanJsonBytes,
    int MarketPlansJsonBytes,
    int MarketItemAnalysesJsonBytes,
    int MarketAnalysisRecipeBasisJsonBytes)
{
    public int TotalJsonBytes => PlanJsonBytes +
                                 MarketPlansJsonBytes +
                                 MarketItemAnalysesJsonBytes +
                                 MarketAnalysisRecipeBasisJsonBytes;

    public static StoredPlanSnapshotMetrics FromStoredPlan(StoredPlan plan)
    {
        return new StoredPlanSnapshotMetrics(
            CountPlanNodes(plan.PlanJson),
            CountList<DetailedShoppingPlan>(plan.MarketPlansJson),
            CountList<MarketItemAnalysis>(plan.MarketItemAnalysesJson),
            GetUtf8ByteCount(plan.PlanJson),
            GetUtf8ByteCount(plan.MarketPlansJson),
            GetUtf8ByteCount(plan.MarketItemAnalysesJson),
            GetUtf8ByteCount(plan.MarketAnalysisRecipeBasisJson));
    }

    private static int CountPlanNodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<CraftingPlan>(json);
            return plan?.RootItems.Sum(CountNodeAndChildren) ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int CountNodeAndChildren(PlanNode node)
    {
        return 1 + (node.Children?.Sum(CountNodeAndChildren) ?? 0);
    }

    private static int CountList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json)?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int GetUtf8ByteCount(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
    }
}
