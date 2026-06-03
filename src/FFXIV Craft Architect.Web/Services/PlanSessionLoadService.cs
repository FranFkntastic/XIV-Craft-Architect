using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class PlanSessionLoadService
{
    private readonly AppState _appState;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public PlanSessionLoadService(
        AppState appState,
        IRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        _appState = appState;
        _recipeLayerWorkflow = recipeLayerWorkflow ?? new LightweightRecipeLayerWorkflowService();
    }

    public PlanSessionLoadResult Load(StoredPlan storedPlan, bool trackStoredPlanIdentity = true)
    {
        var result = PrepareSession(storedPlan);
        _appState.ApplyLoadedPlanSession(result, trackStoredPlanIdentity);
        return result;
    }

    public PlanSessionLoadResult PrepareSession(StoredPlan storedPlan)
    {
        return Prepare(
            storedPlan,
            deserializedPlan: null,
            buildMarketAnalysisCandidates: _recipeLayerWorkflow.BuildMarketAnalysisCandidates);
    }

    public static PlanSessionLoadResult Prepare(StoredPlan storedPlan)
    {
        return Prepare(storedPlan, deserializedPlan: null);
    }

    public static PlanSessionLoadResult Prepare(StoredPlan storedPlan, CraftingPlan? deserializedPlan)
    {
        return Prepare(
            storedPlan,
            deserializedPlan,
            buildMarketAnalysisCandidates: BuildLightweightMarketAnalysisCandidates);
    }

    private static PlanSessionLoadResult Prepare(
        StoredPlan storedPlan,
        CraftingPlan? deserializedPlan,
        Func<CraftingPlan?, IReadOnlyList<MaterialAggregate>> buildMarketAnalysisCandidates)
    {
        CraftingPlan? plan = null;
        string? warning = null;

        if (deserializedPlan != null)
        {
            plan = deserializedPlan;
        }
        else if (!string.IsNullOrWhiteSpace(storedPlan.PlanJson))
        {
            try
            {
                plan = JsonSerializer.Deserialize<CraftingPlan>(storedPlan.PlanJson);
                RestoreParentLinks(plan);
            }
            catch (Exception ex)
            {
                warning = $"Could not load full plan data: {ex.Message}";
            }
        }

        var projectItems = storedPlan.ProjectItems.Select(p => new ProjectItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity,
            MustBeHq = p.MustBeHq
        }).ToList();

        var marketAnalyses = DeserializeOrEmpty<MarketItemAnalysis>(storedPlan.MarketItemAnalysesJson);
        var hasRecipeBasisPayload = !string.IsNullOrWhiteSpace(storedPlan.MarketAnalysisRecipeBasisJson);
        var marketAnalysisRecipeBasis = StoredRecipeBasisMapper.TryDeserialize(
            storedPlan.MarketAnalysisRecipeBasisJson,
            out var recipeBasisWarning);
        warning = AppendWarning(warning, recipeBasisWarning);
        if (ContainsLegacyListingOutlierField(storedPlan.MarketItemAnalysesJson))
        {
            marketAnalyses.Clear();
            marketAnalysisRecipeBasis = null;
        }
        else if (hasRecipeBasisPayload)
        {
            if (marketAnalysisRecipeBasis == null ||
                !RestoredMarketAnalysisMatchesRecipeBasis(marketAnalysisRecipeBasis, marketAnalyses))
            {
                marketAnalyses.Clear();
                marketAnalysisRecipeBasis = null;
            }
        }
        else if (!RestoredMarketAnalysisMatchesPlan(plan, projectItems, marketAnalyses, buildMarketAnalysisCandidates))
        {
            marketAnalyses.Clear();
        }

        var restoredShoppingPlans = DeserializeOrEmpty<DetailedShoppingPlan>(storedPlan.MarketPlansJson);
        var shoppingPlans = marketAnalyses.Count > 0 &&
                            RestoredShoppingPlansMatchMarketAnalysis(restoredShoppingPlans, marketAnalyses)
            ? restoredShoppingPlans
            : new List<DetailedShoppingPlan>();
        if (marketAnalyses.Count == 0)
        {
            marketAnalysisRecipeBasis = null;
        }

        return new PlanSessionLoadResult(
            storedPlan,
            plan,
            projectItems,
            marketAnalyses,
            shoppingPlans,
            marketAnalysisRecipeBasis,
            warning);
    }

    private static string? AppendWarning(string? existingWarning, string? newWarning)
    {
        if (string.IsNullOrWhiteSpace(newWarning))
        {
            return existingWarning;
        }

        if (string.IsNullOrWhiteSpace(existingWarning))
        {
            return newWarning;
        }

        return $"{existingWarning} {newWarning}";
    }

    private static void RestoreParentLinks(CraftingPlan? plan)
    {
        if (plan == null)
        {
            return;
        }

        foreach (var root in plan.RootItems)
        {
            RestoreParentLinks(root, parent: null);
        }
    }

    private static void RestoreParentLinks(PlanNode node, PlanNode? parent)
    {
        node.Parent = parent;
        node.ParentNodeId = parent?.NodeId;
        foreach (var child in node.Children)
        {
            RestoreParentLinks(child, node);
        }
    }

    private static bool ContainsLegacyListingOutlierField(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsLegacyListingOutlierField(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsLegacyListingOutlierField(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                string.Equals(property.Name, "IsOutlier", StringComparison.OrdinalIgnoreCase) ||
                ContainsLegacyListingOutlierField(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsLegacyListingOutlierField),
            _ => false
        };
    }

    private static List<T> DeserializeOrEmpty<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static bool RestoredMarketAnalysisMatchesPlan(
        CraftingPlan? plan,
        IReadOnlyList<ProjectItem> projectItems,
        IReadOnlyList<MarketItemAnalysis> analyses,
        Func<CraftingPlan?, IReadOnlyList<MaterialAggregate>> buildMarketAnalysisCandidates)
    {
        if (analyses.Count == 0)
        {
            return false;
        }

        var candidates = plan != null
            ? buildMarketAnalysisCandidates(plan)
            : projectItems
                .Where(item => item.Quantity > 0)
                .Select(item => new MaterialAggregate
                {
                    ItemId = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    TotalQuantity = item.Quantity
                })
                .ToList();
        var expected = candidates.ToDictionary(candidate => candidate.ItemId, candidate => candidate.TotalQuantity);

        return expected.Count == analyses.Count &&
               analyses.All(analysis =>
                   expected.TryGetValue(analysis.ItemId, out var quantityNeeded) &&
                   quantityNeeded == analysis.QuantityNeeded);
    }

    private static bool RestoredMarketAnalysisMatchesRecipeBasis(
        StoredRecipeOperationSnapshot recipeBasis,
        IReadOnlyList<MarketItemAnalysis> analyses)
    {
        if (analyses.Count == 0)
        {
            return false;
        }

        var expected = recipeBasis.MarketAnalysisDemandItems
            .Where(item => !recipeBasis.UnavailableMarketItemIds.Contains(item.ItemId))
            .ToDictionary(item => item.ItemId, item => item.TotalQuantity);

        return expected.Count == analyses.Count &&
               analyses.All(analysis =>
                   expected.TryGetValue(analysis.ItemId, out var quantityNeeded) &&
                   quantityNeeded == analysis.QuantityNeeded);
    }

    private static IReadOnlyList<MaterialAggregate> BuildLightweightMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToMarketAnalysisMaterialAggregates();
    }

    private static bool RestoredShoppingPlansMatchMarketAnalysis(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> analyses)
    {
        if (shoppingPlans.Count == 0)
        {
            return false;
        }

        var expected = analyses.ToDictionary(analysis => analysis.ItemId, analysis => analysis.QuantityNeeded);
        return expected.Count == shoppingPlans.Count &&
               shoppingPlans.All(plan =>
                   expected.TryGetValue(plan.ItemId, out var quantityNeeded) &&
                   quantityNeeded == plan.QuantityNeeded);
    }
}

public sealed record PlanSessionLoadResult(
    StoredPlan StoredPlan,
    CraftingPlan? Plan,
    IReadOnlyList<ProjectItem> ProjectItems,
    IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    StoredRecipeOperationSnapshot? MarketAnalysisRecipeBasis,
    string? Warning);
