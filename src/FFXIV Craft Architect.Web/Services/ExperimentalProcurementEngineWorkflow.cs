using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record ExperimentalProcurementEngineWorkflowRequest(
    CraftingPlan Plan,
    IReadOnlyList<MaterialAggregate> ActiveItems,
    long PlanSessionVersion,
    long PlanDecisionVersion,
    long MarketAnalysisVersion,
    ProcurementRoutePublicationBasis RouteBasis,
    Func<bool>? IsCurrentOperation);

public interface IExperimentalProcurementEngineWorkflow
{
    Task<ProcurementWorkflowResult> RunAsync(
        ExperimentalProcurementEngineWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExperimentalProcurementEngineWorkflow : IExperimentalProcurementEngineWorkflow
{
    private readonly AppState _appState;
    private readonly ExperimentalProcurementEngineFactory _factory;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly ILogger<ExperimentalProcurementEngineWorkflow>? _logger;

    public ExperimentalProcurementEngineWorkflow(
        AppState appState,
        ExperimentalProcurementEngineFactory factory,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        ILogger<ExperimentalProcurementEngineWorkflow>? logger = null)
    {
        _appState = appState;
        _factory = factory;
        _snapshots = snapshots;
        _logger = logger;
    }

    public async Task<ProcurementWorkflowResult> RunAsync(
        ExperimentalProcurementEngineWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        using var memoryPressureLease = await _appState.BeginEngineMemoryPressureLeaseAsync(
            cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        progress?.Report("Preparing procurement engine request...");
        var preparation = Stopwatch.StartNew();
        _logger?.LogInformation("[stage] procurement engine request preparation starting");
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var scope = request.RouteBasis.Scope;
        var activeItemIds = request.ActiveItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var routeRequest = new ProcurementRouteExecutionRequest
        {
            Plan = CreateVendorDecisionSnapshot(request.Plan, activeItemIds),
            ActiveProcurementItems = request.ActiveItems,
            SourceShoppingPlans = _appState.ShoppingPlans
                .Where(plan => activeItemIds.Contains(plan.ItemId))
                .ToArray(),
            SourceMarketAnalyses = _appState.MarketItemAnalyses
                .Where(analysis => activeItemIds.Contains(analysis.ItemId))
                .ToArray(),
            Scope = scope,
            SelectedDataCenter = request.RouteBasis.SelectedDataCenter,
            SelectedRegion = request.RouteBasis.SelectedRegion,
            Lens = request.RouteBasis.Lens,
            ProcurementConfig = new MarketAnalysisConfig
            {
                EnableSplitWorld = request.RouteBasis.IncludeSplitPurchases,
                TravelTolerance = request.RouteBasis.TravelTolerance,
                TravelPriority = request.RouteBasis.TravelPriority,
                StartFromHomeDataCenter = request.RouteBasis.StartFromHomeDataCenter
            },
            IncludeSplitPurchases = request.RouteBasis.IncludeSplitPurchases,
            BlacklistedWorlds = request.RouteBasis.BlacklistedWorlds,
            ExcludedItemWorlds = request.RouteBasis.ExcludedItemWorlds,
            ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(scope),
            IncludeReconciliationEvidenceInResult = false
        };
        LogPreparationStage("route request captured", preparation);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var input = new ReferenceEngineInput(null, routeRequest);
        var wireOptions = EngineJsonSerializerOptions.CreateWire();
        var inputElement = JsonSerializer.SerializeToElement(input, wireOptions);
        LogPreparationStage("input serialized", preparation);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var routeBasisHash = EngineCanonicalHash.Compute(request.RouteBasis, wireOptions);
        var marketAnalysisHash = EngineCanonicalHash.Compute(new
        {
            Domain = "market-analysis-publication-v1",
            request.MarketAnalysisVersion
        });
        LogPreparationStage("publication basis hashed", preparation);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var engineInputHash = await EngineCanonicalHash.ComputeEngineInputAsync(
            inputElement,
            YieldToBrowserForHashAsync,
            cancellationToken);
        LogPreparationStage("engine input hashed", preparation);
        var provisional = new EngineRequestEnvelope(
            "1",
            Guid.NewGuid(),
            EngineInputKind.RootIntent,
            inputElement,
            new EngineBasisSet(
                new EngineBasisIdentity("plan", "1", routeBasisHash),
                new EngineBasisIdentity("session", "1", request.PlanSessionVersion.ToString()),
                new EngineBasisIdentity("publication", "1", request.MarketAnalysisVersion.ToString()),
                new EngineBasisIdentity("route", "1", routeBasisHash)),
            new EngineDeterministicSettings(
                "stable-procurement-worker-v2",
                new Dictionary<string, string>
                {
                    ["travelTolerance"] = request.RouteBasis.TravelTolerance.ToString(),
                    ["travelPriority"] = request.RouteBasis.TravelPriority.ToString()
                }),
            EngineExecutionBudgets.Default,
            string.Empty,
            string.Empty,
            marketAnalysisHash,
            routeBasisHash,
            engineInputHash);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var prepared = _snapshots.PrepareInput(provisional, input);
        LogPreparationStage("semantic snapshots prepared", preparation);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var engineRequest = provisional with
        {
            RootIntentHash = EngineSemanticSnapshotHash.RootIntent(prepared.RootIntent),
            ExpandedGraphHash = EngineSemanticSnapshotHash.ExpandedGraph(prepared.ExpandedGraph)
        };
        LogPreparationStage("semantic snapshots hashed", preparation);
        await YieldToBrowserAsync(cancellationToken);
        if (!IsCurrent(request))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        progress?.Report("Starting procurement engine Worker...");
        LogPreparationStage("Worker starting", preparation);
        var gateHeld = true;
        var gate = OperationGateLease.Create(
            () => gateHeld && (request.IsCurrentOperation?.Invoke() ?? true),
            () => gateHeld && !(gateHeld = false));
        await using var execution = _factory.Create(new WebProcurementSettlementRegistration(
            engineRequest,
            request.Plan,
            request.PlanSessionVersion,
            request.PlanDecisionVersion,
            request.MarketAnalysisVersion,
            request.RouteBasis,
            gate));
        var engineProgress = progress is null
            ? null
            : new Progress<EngineProgress>(value =>
            {
                if (IsCurrent(request))
                {
                    progress.Report(value.Message);
                }
            });

        var result = await execution.ExecuteAsync(engineRequest, engineProgress, cancellationToken);
        if (request.IsCurrentOperation?.Invoke() == false ||
            _appState.PlanSessionVersion != request.PlanSessionVersion)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }
        return result.Status switch
        {
            EngineTerminalStatus.Succeeded when
                _appState.ProcurementRouteValidity == ProcurementRoutePublicationValidity.Current =>
                new ProcurementWorkflowResult(
                    ProcurementWorkflowStatus.Published,
                    _appState.ProcurementShoppingPlans.Count),
            EngineTerminalStatus.Succeeded => ProcurementWorkflowResult.Noop(
                ProcurementWorkflowStatus.StaleDecision),
            EngineTerminalStatus.Cancelled => ProcurementWorkflowResult.Noop(
                request.IsCurrentOperation?.Invoke() == false
                    ? ProcurementWorkflowStatus.Superseded
                    : ProcurementWorkflowStatus.Cancelled),
            EngineTerminalStatus.Failed when
                string.Equals(
                    result.Failure?.Code,
                    "no-complete-procurement-route",
                    StringComparison.Ordinal) =>
                new ProcurementWorkflowResult(
                    ProcurementWorkflowStatus.NoCompleteRoute,
                    0,
                    result.Failure?.Message ??
                    "No complete procurement route is available with the current market evidence."),
            EngineTerminalStatus.Indeterminate => new ProcurementWorkflowResult(
                ProcurementWorkflowStatus.Indeterminate,
                0,
                result.Failure?.Message ??
                "The procurement transaction outcome is uncertain. Reload before retrying."),
            _ => new ProcurementWorkflowResult(
                ProcurementWorkflowStatus.Failed,
                0,
                result.Failure?.Message ?? "The procurement engine transaction did not complete.")
        };
    }

    private static async Task YieldToBrowserAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
    }

    private static async ValueTask YieldToBrowserForHashAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
    }

    private static CraftingPlan CreateVendorDecisionSnapshot(
        CraftingPlan plan,
        IReadOnlySet<int> activeItemIds)
    {
        var vendorNodes = new List<PlanNode>();
        AddVendorDecisionNodes(plan.RootItems, activeItemIds, vendorNodes);
        return new CraftingPlan
        {
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            ModifiedAt = plan.ModifiedAt,
            DataCenter = plan.DataCenter,
            World = plan.World,
            RootItems = vendorNodes
        };
    }

    private static void AddVendorDecisionNodes(
        IEnumerable<PlanNode> nodes,
        IReadOnlySet<int> activeItemIds,
        ICollection<PlanNode> vendorNodes)
    {
        foreach (var node in nodes)
        {
            if (activeItemIds.Contains(node.ItemId) &&
                node.Source == AcquisitionSource.VendorBuy)
            {
                vendorNodes.Add(new PlanNode
                {
                    ItemId = node.ItemId,
                    Name = node.Name,
                    Quantity = node.Quantity,
                    Source = AcquisitionSource.VendorBuy,
                    SourceReason = node.SourceReason,
                    CanBuyFromVendor = node.CanBuyFromVendor,
                    VendorPrice = node.VendorPrice,
                    VendorOptions = node.VendorOptions.ToList(),
                    SelectedVendorIndex = node.SelectedVendorIndex,
                    NodeId = node.NodeId
                });
            }

            AddVendorDecisionNodes(node.Children, activeItemIds, vendorNodes);
        }
    }

    private void LogPreparationStage(string stage, Stopwatch stopwatch)
    {
        _logger?.LogInformation(
            "[stage] procurement engine {Stage} ({ElapsedMilliseconds}ms total)",
            stage,
            stopwatch.ElapsedMilliseconds);
    }

    private bool IsCurrent(ExperimentalProcurementEngineWorkflowRequest request) =>
        _appState.IsCurrentPlanSession(request.Plan, request.PlanSessionVersion) &&
        _appState.CurrentVersions.PlanDecisionVersion == request.PlanDecisionVersion &&
        _appState.CurrentVersions.MarketAnalysisVersion == request.MarketAnalysisVersion &&
        request.RouteBasis.Matches(_appState.CreateCurrentProcurementRouteBasis()) &&
        (request.IsCurrentOperation?.Invoke() ?? true);
}
