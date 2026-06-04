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
            (result.MarketItemAnalyses.Count > 0 || result.UnavailableMarketItemIds.Count > 0))
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
                result.MarketIntelligence?.RecommendationMode ?? storedPlan.SavedRecommendationMode,
                result.MarketIntelligence?.Lens ?? storedPlan.SavedMarketAnalysisLens,
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

        var marketRestore = StoredMarketIntelligenceRestorer.Restore(
            new StoredMarketIntelligenceRestoreInput(
                storedPlan.MarketIntelligenceJson,
                storedPlan.MarketItemAnalysesJson,
                storedPlan.MarketPlansJson,
                storedPlan.MarketAnalysisRecipeBasisJson,
                storedPlan.UnavailableMarketItemIds,
                storedPlan.SavedRecommendationMode,
                storedPlan.SavedMarketAnalysisLens,
                plan,
                projectItems,
                BuildMarketAnalysisCandidates));
        warning = AppendWarning(warning, marketRestore.Warning);

        return new CorePlanSessionLoadResult(
            storedPlan,
            plan,
            projectItems,
            marketRestore.MarketItemAnalyses,
            marketRestore.Recommendations,
            marketRestore.UnavailableMarketItemIds,
            marketRestore.MarketIntelligence,
            marketRestore.RecipeBasis,
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

    private static IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToMarketAnalysisMaterialAggregates();
    }
}
