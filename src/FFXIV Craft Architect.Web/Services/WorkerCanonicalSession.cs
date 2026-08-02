using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
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
    private static readonly JsonSerializerOptions JsonOptions =
        EngineJsonSerializerOptions.CreateWire();
    private CraftSessionState _session = CreateSession();
    private string? _legacyMarketAnalysisScopeSnapshotJson;
    private string? _legacyProcurementRouteJson;
    private CraftSessionVersionStamp? _activeProcurementItemsVersion;
    private IReadOnlyList<MaterialAggregate>? _activeProcurementItems;
    private LinkedPlanRevisionIdentity? _linkedPlanRevision;

    public CraftSessionState Session => _session;

    public WorkerCanonicalSessionRestoreResult Restore(
        StoredPlan? storedPlan,
        bool trackStoredPlanIdentity)
    {
        var timing = Stopwatch.StartNew();
        _session = CreateSession();
        _activeProcurementItemsVersion = null;
        _activeProcurementItems = null;
        _linkedPlanRevision = storedPlan?.LinkedOrderId.HasValue == true
            ? new LinkedPlanRevisionIdentity(
                storedPlan.Id,
                storedPlan.CreatedAt,
                storedPlan.ModifiedAt,
                storedPlan.SavedAt,
                storedPlan.LinkedOrderId.Value)
            : null;
        _legacyMarketAnalysisScopeSnapshotJson = storedPlan?.MarketAnalysisScopeSnapshotJson;
        _legacyProcurementRouteJson = storedPlan?.ProcurementRouteJson;
        if (storedPlan is null)
        {
            return new WorkerCanonicalSessionRestoreResult(null, null);
        }

        var loader = new CorePlanSessionLoadService(_session);
        var result = loader.Load(ToCoreSnapshot(storedPlan), trackStoredPlanIdentity);
        var coreRestoreMilliseconds = timing.ElapsedMilliseconds;
        if (!result.CanLoad)
        {
            throw new InvalidOperationException(
                result.Warning ?? $"Stored plan '{storedPlan.Name}' could not be restored.");
        }

        var repairedAcquisitionDecisions =
            result.ReconciledAcquisitionDecisionCount > 0;
        if (repairedAcquisitionDecisions)
        {
            _legacyProcurementRouteJson = null;
        }
        else
        {
            RestoreLegacyProcurementOverlay(storedPlan.ProcurementRouteJson);
        }
        if (!repairedAcquisitionDecisions &&
            storedPlan.ProcurementTravelTolerance is { } tolerance)
        {
            _session.TrySelectProcurementTravelTolerance(tolerance);
        }
        var routeRestoreMilliseconds = timing.ElapsedMilliseconds - coreRestoreMilliseconds;
        _session.MarkCurrentPersisted(
            CraftSessionDirtyBucket.PlanCore,
            CraftSessionDirtyBucket.MarketAnalysis,
            CraftSessionDirtyBucket.Procurement,
            CraftSessionDirtyBucket.SettingsContext);
        Console.WriteLine(
            $"[EngineSession] restore core={coreRestoreMilliseconds}ms " +
            $"route={routeRestoreMilliseconds}ms total={timing.ElapsedMilliseconds}ms");
        var repairPatch = repairedAcquisitionDecisions
            ? CreateDurablePatch(
                replacePlanStateJson: true,
                replaceProcurementRoute: true)
            : null;
        return new WorkerCanonicalSessionRestoreResult(
            result.Warning,
            repairPatch);
    }

    public StoredPlan? Export(
        string planId,
        string planName,
        bool includeSourcePlanIdentity)
    {
        if (_session.BorrowActivePlan() is null && _session.ProjectItems.Count == 0)
        {
            return null;
        }

        var snapshot = new CoreStoredPlanSnapshotBuilder(_session).Build(
            planId,
            planName,
            DateTime.UtcNow,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields: false,
            borrowCanonicalState: true,
            compressMarketIntelligence: true);
        var linkedRevision = _linkedPlanRevision is { } retained &&
            string.Equals(retained.PlanId, planId, StringComparison.Ordinal)
                ? retained
                : null;
        return new StoredPlan
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            CreatedAt = linkedRevision?.CreatedAt ?? snapshot.CreatedAt,
            ModifiedAt = linkedRevision?.ModifiedAt ?? snapshot.ModifiedAt,
            SavedAt = linkedRevision?.SavedAt ?? snapshot.SavedAt,
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
            PlanStateJson = snapshot.PlanStateJson,
            MarketIntelligenceJson = snapshot.MarketIntelligenceJson,
            MarketAnalysisRecipeBasisJson = snapshot.MarketAnalysisRecipeBasisJson,
            MarketAnalysisScopeSnapshotJson = _legacyMarketAnalysisScopeSnapshotJson,
            ProcurementRouteJson = BuildProcurementRouteJson() ?? _legacyProcurementRouteJson,
            ProcurementTravelTolerance = _session.BorrowProcurementOverlay()?
                .RouteDecision?
                .TravelTolerance,
            SavedRecommendationMode = snapshot.SavedRecommendationMode,
            SavedMarketAnalysisLens = snapshot.SavedMarketAnalysisLens,
            SourcePlanId = snapshot.SourcePlanId,
            SourcePlanName = snapshot.SourcePlanName,
            LinkedOrderId = linkedRevision?.LinkedOrderId
        };
    }

    public void InvalidateLegacyProcurementRoute()
    {
        _legacyProcurementRouteJson = null;
    }

    public string ExportProcurementRoute() =>
        BuildProcurementRouteJson()
        ?? throw new InvalidOperationException("The current procurement route is unavailable.");

    public WorkerSessionDurablePatch CreateDurablePatch(
        bool replacePlanJson = false,
        bool replacePlanStateJson = false,
        bool replaceProjectItems = false,
        bool replaceMarketEvidence = false,
        bool replaceProcurementRoute = false,
        int? procurementTravelTolerance = null,
        bool replaceContext = false,
        bool replaceSourceIdentity = false)
    {
        var plan = _session.BorrowActivePlan();
        var evidence = _session.BorrowMarketEvidence();
        var identity = _session.Identity;
        var intelligence = replaceMarketEvidence
            ? MarketIntelligence.FromCraftSessionMarketEvidence(evidence)
            : null;
        var marketIntelligenceJson = intelligence is
        {
            HasPublishedMarketAnalysis: true
        } or
        {
            HasRecommendations: true
        } or
        {
            HasUnavailableMarketItems: true
        }
                ? MarketIntelligencePayloadCodec.Serialize(
                    StoredMarketIntelligence.FromMarketIntelligence(intelligence),
                    compress: true)
                : null;

        return new WorkerSessionDurablePatch(
            ReplacePlanJson: replacePlanJson,
            PlanJson: replacePlanJson && plan is not null
                ? JsonSerializer.Serialize(plan)
                : null,
            ReplacePlanStateJson: replacePlanStateJson,
            PlanStateJson: replacePlanStateJson && plan is not null
                ? StoredPlanRuntimeState.Capture(plan)
                : null,
            ReplaceProjectItems: replaceProjectItems,
            ProjectItems: replaceProjectItems
                ? _session.ProjectItems.Select(item => new StoredProjectItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    Quantity = item.Quantity,
                    MustBeHq = item.MustBeHq
                }).ToArray()
                : null,
            ReplaceMarketEvidence: replaceMarketEvidence,
            MarketIntelligenceJson: marketIntelligenceJson,
            MarketAnalysisRecipeBasisJson: replaceMarketEvidence && evidence.RecipeBasis is not null
                ? JsonSerializer.Serialize(evidence.RecipeBasis)
                : null,
            SavedRecommendationMode: replaceMarketEvidence
                ? evidence.RecommendationMode
                : null,
            SavedMarketAnalysisLens: replaceMarketEvidence
                ? evidence.Lens
                : null,
            ReplaceProcurementRoute: replaceProcurementRoute,
            ProcurementRouteJson: replaceProcurementRoute
                ? BuildProcurementRouteJson() ?? _legacyProcurementRouteJson
                : null,
            ProcurementTravelTolerance: procurementTravelTolerance,
            ReplaceContext: replaceContext,
            DataCenter: replaceContext
                ? _session.ActiveContext.DataCenter ?? plan?.DataCenter ?? "Aether"
                : null,
            ReplaceSourceIdentity: replaceSourceIdentity,
            SourcePlanId: replaceSourceIdentity ? identity.SourcePlanId : null,
            SourcePlanName: replaceSourceIdentity ? identity.SourcePlanName : null);
    }

    public IReadOnlyList<MaterialAggregate> GetActiveProcurementItems(
        Func<IReadOnlyList<MaterialAggregate>> build)
    {
        var version = _session.CaptureVersionStamp();
        if (_activeProcurementItems is not null &&
            _activeProcurementItemsVersion is { } cached &&
            cached.PlanSession == version.PlanSession &&
            cached.PlanCore == version.PlanCore &&
            cached.PlanDecision == version.PlanDecision)
        {
            return _activeProcurementItems;
        }

        _activeProcurementItems = build();
        _activeProcurementItemsVersion = version;
        return _activeProcurementItems;
    }

    private string? BuildProcurementRouteJson()
    {
        var overlay = _session.BorrowProcurementOverlay();
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
            PayloadHash: null),
            JsonOptions);
    }

    private void RestoreLegacyProcurementOverlay(string? routeJson)
    {
        if (string.IsNullOrWhiteSpace(routeJson))
        {
            return;
        }

        try
        {
            var route = JsonSerializer.Deserialize<StoredProcurementRoute>(routeJson, JsonOptions);
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
            PlanStateJson = storedPlan.PlanStateJson,
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

    private sealed record LinkedPlanRevisionIdentity(
        string PlanId,
        DateTime CreatedAt,
        DateTime ModifiedAt,
        DateTime SavedAt,
        Guid LinkedOrderId);
}

internal sealed record WorkerCanonicalSessionRestoreResult(
    string? Warning,
    WorkerSessionDurablePatch? DurableRepairPatch);
