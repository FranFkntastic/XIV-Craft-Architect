using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Worker-local owner of the canonical Core session. Web <see cref="StoredPlan"/>
/// remains the migration wire format while the active mutable model is the
/// product-neutral Core session used by recipe, acquisition, market, and
/// procurement command services.
/// </summary>
internal sealed class WorkerCanonicalSession
{
    private CraftSessionState _session = CreateSession();
    private string? _legacyMarketAnalysisScopeSnapshotJson;
    private string? _legacyProcurementRouteJson;

    public CraftSessionState Session => _session;

    public string? Restore(StoredPlan? storedPlan, bool trackStoredPlanIdentity)
    {
        _session = CreateSession();
        _legacyMarketAnalysisScopeSnapshotJson = storedPlan?.MarketAnalysisScopeSnapshotJson;
        _legacyProcurementRouteJson = storedPlan?.ProcurementRouteJson;
        if (storedPlan is null)
        {
            return null;
        }

        var loader = new CorePlanSessionLoadService(_session);
        var result = loader.Load(ToCoreSnapshot(storedPlan), trackStoredPlanIdentity);
        if (!result.CanLoad)
        {
            throw new InvalidOperationException(
                result.Warning ?? $"Stored plan '{storedPlan.Name}' could not be restored.");
        }

        RestoreLegacyProcurementOverlay(storedPlan.ProcurementRouteJson);
        _session.MarkCurrentPersisted(
            CraftSessionDirtyBucket.PlanCore,
            CraftSessionDirtyBucket.MarketAnalysis,
            CraftSessionDirtyBucket.Procurement,
            CraftSessionDirtyBucket.SettingsContext);
        return result.Warning;
    }

    public StoredPlan? Export(
        string planId,
        string planName,
        bool includeSourcePlanIdentity,
        bool includeLegacyMarketAnalysisFields)
    {
        if (_session.ActivePlan is null && _session.ProjectItems.Count == 0)
        {
            return null;
        }

        var snapshot = new CoreStoredPlanSnapshotBuilder(_session).Build(
            planId,
            planName,
            DateTime.UtcNow,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields,
            borrowCanonicalState: true,
            compressMarketIntelligence: true);
        return new StoredPlan
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            CreatedAt = snapshot.CreatedAt,
            ModifiedAt = snapshot.ModifiedAt,
            SavedAt = snapshot.SavedAt,
            DataCenter = snapshot.DataCenter,
            ProjectItems = snapshot.ProjectItems.Select(item => new StoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToList(),
            PlanJson = snapshot.PlanJson,
            MarketPlansJson = snapshot.MarketPlansJson,
            MarketIntelligenceJson = snapshot.MarketIntelligenceJson,
            MarketItemAnalysesJson = includeLegacyMarketAnalysisFields
                ? snapshot.MarketItemAnalysesJson
                : null,
            MarketAnalysisRecipeBasisJson = snapshot.MarketAnalysisRecipeBasisJson,
            MarketAnalysisScopeSnapshotJson = _legacyMarketAnalysisScopeSnapshotJson,
            ProcurementRouteJson = BuildProcurementRouteJson() ?? _legacyProcurementRouteJson,
            SavedRecommendationMode = snapshot.SavedRecommendationMode,
            SavedMarketAnalysisLens = snapshot.SavedMarketAnalysisLens,
            SourcePlanId = snapshot.SourcePlanId,
            SourcePlanName = snapshot.SourcePlanName
        };
    }

    public void InvalidateLegacyProcurementRoute()
    {
        _legacyProcurementRouteJson = null;
    }

    private string? BuildProcurementRouteJson()
    {
        var overlay = _session.ProcurementOverlay;
        if (overlay?.ShoppingPlans is not { Count: > 0 } shoppingPlans ||
            overlay.RouteDecision is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(new StoredProcurementRoute(
            SchemaVersion: 4,
            OptimizerVersion: "worker-owned-v1",
            ShoppingPlans: shoppingPlans,
            Decision: overlay.RouteDecision,
            Basis: null,
            PlanHash: string.Empty,
            MarketEvidenceHash: null,
            PayloadHash: null));
    }

    private void RestoreLegacyProcurementOverlay(string? routeJson)
    {
        if (string.IsNullOrWhiteSpace(routeJson))
        {
            return;
        }

        try
        {
            var route = JsonSerializer.Deserialize<StoredProcurementRoute>(routeJson);
            if (route?.ShoppingPlans?.Count > 0 && route.Decision is not null)
            {
                _session.PublishProcurementOverlay(
                    new CraftSessionProcurementOverlay(
                        DateTime.UtcNow,
                        route.ShoppingPlans.Select(plan => plan.ItemId).Distinct().ToArray(),
                        "restored procurement route",
                        route.ShoppingPlans,
                        ProcurementWorldCardBuilder.BuildWorldCards(
                            route.ShoppingPlans,
                            _session.ActiveContext.DataCenter ?? "Aether"),
                        route.Decision),
                    "stored procurement route restored");
            }
        }
        catch (JsonException)
        {
            _legacyProcurementRouteJson = null;
        }
    }

    private static CoreStoredPlanSnapshot ToCoreSnapshot(StoredPlan storedPlan) =>
        new()
        {
            Id = storedPlan.Id,
            Name = storedPlan.Name,
            CreatedAt = storedPlan.CreatedAt,
            ModifiedAt = storedPlan.ModifiedAt,
            SavedAt = storedPlan.SavedAt,
            DataCenter = storedPlan.DataCenter,
            ProjectItems = storedPlan.ProjectItems.Select(item => new CoreStoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToList(),
            PlanJson = storedPlan.PlanJson,
            MarketPlansJson = storedPlan.MarketPlansJson,
            MarketIntelligenceJson = storedPlan.MarketIntelligenceJson,
            MarketItemAnalysesJson = storedPlan.MarketItemAnalysesJson,
            MarketAnalysisRecipeBasisJson = storedPlan.MarketAnalysisRecipeBasisJson,
            SavedRecommendationMode = storedPlan.SavedRecommendationMode,
            SavedMarketAnalysisLens = storedPlan.SavedMarketAnalysisLens,
            SourcePlanId = storedPlan.SourcePlanId,
            SourcePlanName = storedPlan.SourcePlanName
        };

    private static CraftSessionState CreateSession() =>
        new(new ImmediateCraftSessionDispatcher());
}
