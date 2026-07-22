using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

internal sealed record ReusableStoredMarketEvidence(
    long MarketAnalysisVersion,
    long SettingsVersion,
    string? MarketIntelligenceJson,
    string? MarketAnalysisRecipeBasisJson,
    string? MarketAnalysisScopeSnapshotJson,
    string? MarketEvidenceHash);

public sealed class StoredPlanSnapshotBuilder
{
    public const int ProcurementRouteSchemaVersion = 3;
    public const string ProcurementOptimizerVersion = "bounded-joint-v2";
    private readonly AppState _appState;

    public StoredPlanSnapshotBuilder(AppState appState)
    {
        _appState = appState;
    }

    public StoredPlan Build(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        return Build(
            _appState,
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields);
    }

    public StoredPlan? BuildForCurrentPlan(
        string planId,
        string planName,
        CraftingPlan expectedPlan,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        ArgumentNullException.ThrowIfNull(expectedPlan);

        if (!ReferenceEquals(_appState.CurrentPlan, expectedPlan))
        {
            return null;
        }

        return Build(
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields);
    }

    public static StoredPlan Build(
        AppState appState,
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        return BuildCore(
            appState,
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields,
            reusableMarketEvidence: null,
            out _);
    }

    internal static StoredPlan BuildForAutoSave(
        AppState appState,
        string planId,
        string planName,
        DateTime? savedAt,
        bool includeSourcePlanIdentity,
        bool includeLegacyMarketAnalysisFields,
        ReusableStoredMarketEvidence? reusableMarketEvidence,
        out ReusableStoredMarketEvidence capturedMarketEvidence) =>
        BuildCore(
            appState,
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields,
            reusableMarketEvidence,
            out capturedMarketEvidence);

    private static StoredPlan BuildCore(
        AppState appState,
        string planId,
        string planName,
        DateTime? savedAt,
        bool includeSourcePlanIdentity,
        bool includeLegacyMarketAnalysisFields,
        ReusableStoredMarketEvidence? reusableMarketEvidence,
        out ReusableStoredMarketEvidence capturedMarketEvidence)
    {
        var versions = appState.CurrentVersions;
        var canReuseMarketEvidence = reusableMarketEvidence is not null &&
                                     reusableMarketEvidence.MarketAnalysisVersion == versions.MarketAnalysisVersion &&
                                     reusableMarketEvidence.SettingsVersion == versions.SettingsVersion;
        string? marketIntelligenceJson;
        string? marketAnalysisRecipeBasisJson;
        string? marketAnalysisScopeSnapshotJson;
        string? marketEvidenceHash;
        if (canReuseMarketEvidence)
        {
            marketIntelligenceJson = reusableMarketEvidence!.MarketIntelligenceJson;
            marketAnalysisRecipeBasisJson = reusableMarketEvidence.MarketAnalysisRecipeBasisJson;
            marketAnalysisScopeSnapshotJson = reusableMarketEvidence.MarketAnalysisScopeSnapshotJson;
            marketEvidenceHash = reusableMarketEvidence.MarketEvidenceHash;
        }
        else
        {
            var marketIntelligence = appState.MarketIntelligence;
            var hasMarketIntelligence = marketIntelligence.HasPublishedMarketAnalysis ||
                                        marketIntelligence.HasRecommendations ||
                                        marketIntelligence.HasUnavailableMarketItems;
            marketIntelligenceJson = hasMarketIntelligence
                ? JsonSerializer.Serialize(StoredMarketIntelligence.FromMarketIntelligence(marketIntelligence))
                : null;
            marketAnalysisRecipeBasisJson = appState.MarketAnalysisRecipeBasis is { } recipeBasis
                ? JsonSerializer.Serialize(recipeBasis)
                : null;
            marketAnalysisScopeSnapshotJson = appState.PublishedMarketAnalysisScope is { } scope
                ? JsonSerializer.Serialize(scope)
                : null;
            marketEvidenceHash = marketIntelligenceJson is null
                ? null
                : ComputeMarketEvidenceHash(marketIntelligenceJson);
        }

        capturedMarketEvidence = canReuseMarketEvidence
            ? reusableMarketEvidence!
            : new ReusableStoredMarketEvidence(
                versions.MarketAnalysisVersion,
                versions.SettingsVersion,
                marketIntelligenceJson,
                marketAnalysisRecipeBasisJson,
                marketAnalysisScopeSnapshotJson,
                marketEvidenceHash);

        return new StoredPlan
        {
            Id = planId,
            Name = planName,
            DataCenter = appState.SelectedDataCenter,
            ModifiedAt = DateTime.UtcNow,
            SavedAt = savedAt ?? DateTime.UtcNow,
            ProjectItems = appState.ProjectItems.Select(p => new StoredProjectItem
            {
                Id = p.Id,
                Name = p.Name,
                IconId = p.IconId,
                Quantity = p.Quantity,
                MustBeHq = p.MustBeHq
            }).ToList(),
            PlanJson = appState.CurrentPlan != null
                ? JsonSerializer.Serialize(appState.CurrentPlan)
                : null,
            MarketIntelligenceJson = marketIntelligenceJson,
            ProcurementRouteJson = BuildProcurementRouteJson(appState, marketEvidenceHash),
            MarketPlansJson = includeLegacyMarketAnalysisFields && appState.ShoppingPlans.Any()
                ? JsonSerializer.Serialize(appState.ShoppingPlans)
                : null,
            MarketItemAnalysesJson = includeLegacyMarketAnalysisFields && appState.MarketItemAnalyses.Any()
                ? JsonSerializer.Serialize(appState.MarketItemAnalyses)
                : null,
            MarketAnalysisRecipeBasisJson = marketAnalysisRecipeBasisJson,
            MarketAnalysisScopeSnapshotJson = marketAnalysisScopeSnapshotJson,
            SavedRecommendationMode = appState.RecommendationMode,
            SavedMarketAnalysisLens = appState.MarketAnalysisLens,
            SourcePlanId = includeSourcePlanIdentity ? appState.CurrentPlanId : null,
            SourcePlanName = includeSourcePlanIdentity ? appState.CurrentPlanName : null
        };
    }

    internal static string? BuildProcurementRouteJson(
        AppState appState,
        string? marketEvidenceHash)
    {
        if (appState.CurrentPlan is not { } currentPlan ||
            appState.ProcurementRouteValidity != ProcurementRoutePublicationValidity.Current ||
            appState.ProcurementRouteDecision is not { } routeDecision ||
            appState.ProcurementRoutePublicationBasis is not { } routeBasis)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(marketEvidenceHash))
        {
            throw new InvalidOperationException("Current market intelligence has no persistence hash.");
        }

        return JsonSerializer.Serialize(new StoredProcurementRoute(
            ProcurementRouteSchemaVersion,
            ProcurementOptimizerVersion,
            appState.ProcurementShoppingPlans,
            routeDecision,
            routeBasis,
            ComputePlanHash(currentPlan),
            marketEvidenceHash,
            ComputeRoutePayloadHash(appState.ProcurementShoppingPlans, routeDecision)));
    }

    public static string ComputePlanHash(CraftingPlan plan)
    {
        var options = EngineJsonSerializerOptions.CreateWire();
        var canonicalPlan = JsonSerializer.SerializeToNode(plan, options)
            ?? throw new InvalidOperationException("The procurement route plan could not be serialized.");
        NormalizeParentNodeIds(canonicalPlan);
        return EngineCanonicalHash.Compute(
            new
            {
                Domain = "stored-route-plan-v3",
                Plan = canonicalPlan
            },
            options);
    }

    private static void NormalizeParentNodeIds(JsonNode plan)
    {
        if (plan["rootItems"] is not JsonArray roots)
        {
            return;
        }
        foreach (var root in roots.OfType<JsonObject>())
        {
            NormalizeParentNodeIds(root, parentNodeId: null);
        }
    }

    private static void NormalizeParentNodeIds(JsonObject node, string? parentNodeId)
    {
        node["parentNodeId"] = parentNodeId;
        var nodeId = node["nodeId"]?.GetValue<string>();
        if (node["children"] is not JsonArray children)
        {
            return;
        }
        foreach (var child in children.OfType<JsonObject>())
        {
            NormalizeParentNodeIds(child, nodeId);
        }
    }

    public static string ComputeMarketEvidenceHash(string marketIntelligenceJson)
    {
        using var document = JsonDocument.Parse(marketIntelligenceJson);
        return EngineCanonicalHash.Compute(new
        {
            Domain = "stored-route-market-evidence-v1",
            IntelligenceHash = EngineCanonicalHash.Compute(document.RootElement)
        });
    }

    public static string ComputeRoutePayloadHash(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        MarketRouteDecision decision) =>
        EngineCanonicalHash.Compute(
            new { Domain = "stored-route-payload-v1", ShoppingPlans = shoppingPlans, Decision = decision },
            EngineJsonSerializerOptions.CreateWire());
}
