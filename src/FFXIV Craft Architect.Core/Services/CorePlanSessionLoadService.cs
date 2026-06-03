using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CorePlanSessionLoadService
{
    private readonly CraftSessionState _session;

    public CorePlanSessionLoadService(CraftSessionState session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public CorePlanSessionLoadResult Load(
        CoreStoredPlanSnapshot storedPlan,
        bool trackStoredPlanIdentity = true)
    {
        var result = Prepare(storedPlan);
        if (!result.CanLoad)
        {
            return result;
        }

        var identity = trackStoredPlanIdentity
            ? new CraftSessionIdentity(
                Guid.NewGuid(),
                storedPlan.Name,
                storedPlan.Id,
                storedPlan.Name)
            : new CraftSessionIdentity(
                Guid.NewGuid(),
                storedPlan.SourcePlanName ?? storedPlan.Name,
                storedPlan.SourcePlanId,
                storedPlan.SourcePlanName);

        _session.ActivatePlan(
            result.Plan,
            result.ProjectItems,
            new CraftSessionActiveContext(
                MarketFetchScopeResolver.ResolveRegionForDataCenter(storedPlan.DataCenter, "North America"),
                storedPlan.DataCenter,
                result.Plan?.World,
                MarketFetchScope.SelectedDataCenter),
            "stored session loaded",
            identity);

        if (result.Plan != null &&
            result.MarketItemAnalyses.Count > 0 &&
            result.ShoppingPlans.Count > 0)
        {
            _session.TryPublishMarketAnalysis(
                _session.CaptureVersionStamp(),
                _session.ActivePlan!,
                _session.PlanSessionVersion,
                result.MarketItemAnalyses,
                result.ShoppingPlans,
                acquisitionDecisionsChanged: false,
                "stored market analysis restored",
                result.UnavailableMarketItemIds,
                storedPlan.SavedRecommendationMode,
                storedPlan.SavedMarketAnalysisLens,
                result.MarketAnalysisRecipeBasis);
        }

        return result;
    }

    public static CorePlanSessionLoadResult Prepare(CoreStoredPlanSnapshot storedPlan)
    {
        ArgumentNullException.ThrowIfNull(storedPlan);

        if (storedPlan.SchemaVersion > CoreStoredPlanSnapshot.CurrentSchemaVersion)
        {
            return new CorePlanSessionLoadResult(
                storedPlan,
                null,
                Array.Empty<ProjectItem>(),
                Array.Empty<MarketItemAnalysis>(),
                Array.Empty<DetailedShoppingPlan>(),
                new HashSet<int>(),
                null,
                CanLoad: false,
                $"Stored plan '{storedPlan.Name}' uses newer session schema version {storedPlan.SchemaVersion}; this app supports version {CoreStoredPlanSnapshot.CurrentSchemaVersion}.");
        }

        CraftingPlan? plan = null;
        string? warning = storedPlan.SchemaVersion < CoreStoredPlanSnapshot.CurrentSchemaVersion
            ? $"Stored plan '{storedPlan.Name}' uses older session schema version {storedPlan.SchemaVersion}; loaded with compatibility defaults for version {CoreStoredPlanSnapshot.CurrentSchemaVersion}."
            : null;
        if (!string.IsNullOrWhiteSpace(storedPlan.PlanJson))
        {
            try
            {
                plan = JsonSerializer.Deserialize<CraftingPlan>(storedPlan.PlanJson);
                RestoreParentLinks(plan);
            }
            catch (Exception ex)
            {
                warning = AppendWarning(warning, $"Could not load full plan data: {ex.Message}");
            }
        }

        var projectItems = storedPlan.ProjectItems.Select(item => new ProjectItem
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
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
        else if (!RestoredMarketAnalysisMatchesPlan(plan, projectItems, marketAnalyses))
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

        return new CorePlanSessionLoadResult(
            storedPlan,
            plan,
            projectItems,
            marketAnalyses,
            shoppingPlans,
            storedPlan.UnavailableMarketItemIds.ToHashSet(),
            marketAnalysisRecipeBasis,
            CanLoad: true,
            warning);
    }

    private static string? AppendWarning(string? existingWarning, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return existingWarning;
        }

        return string.IsNullOrWhiteSpace(existingWarning)
            ? warning
            : $"{existingWarning} {warning}";
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

    private static List<T> DeserializeOrEmpty<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch
        {
            return [];
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

    private static bool RestoredMarketAnalysisMatchesPlan(
        CraftingPlan? plan,
        IReadOnlyList<ProjectItem> projectItems,
        IReadOnlyList<MarketItemAnalysis> analyses)
    {
        if (analyses.Count == 0)
        {
            return false;
        }

        var candidates = plan != null
            ? new RecipeDemandProjectionService()
                .Build(plan, snapshot: null)
                .ToMarketAnalysisMaterialAggregates()
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
