using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CoreMarketAnalysisWorkflowRequest(
    bool ForceRefreshData,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter,
    MarketAnalysisExecutionOptions? ExecutionOptions = null,
    RecommendationMode RecommendationMode = RecommendationMode.MinimizeTotalCost);

public sealed record CoreApplyMarketAnalysisLensRequest(MarketAcquisitionLens Lens);

public sealed record CoreMarketAnalysisWorkflowResult(
    bool Published,
    int AnalyzedCount,
    int ChangedDecisionCount,
    int FetchedCount);

public sealed class CoreMarketAnalysisWorkflowService
{
    private readonly CraftSessionState _session;
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecution;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysis;
    private readonly ICoreRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly ICraftOperationCoordinator _operationCoordinator;

    public CoreMarketAnalysisWorkflowService(
        CraftSessionState session,
        IMarketAnalysisExecutionService marketAnalysisExecution,
        MarketShoppingService marketShoppingService,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis,
        ICoreRecipeLayerWorkflowService recipeLayerWorkflow,
        ICraftOperationCoordinator operationCoordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _marketAnalysisExecution = marketAnalysisExecution ?? throw new ArgumentNullException(nameof(marketAnalysisExecution));
        _marketShoppingService = marketShoppingService ?? throw new ArgumentNullException(nameof(marketShoppingService));
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis ?? throw new ArgumentNullException(nameof(marketPriceLadderAnalysis));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
    }

    public async Task<CoreMarketAnalysisWorkflowResult> RunAnalysisAsync(
        CoreMarketAnalysisWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.MarketAnalysis,
            "Running Market Analysis",
            "Analyzing market evidence...");

        try
        {
            var plan = _session.ActivePlan;
            var planSessionVersion = _session.PlanSessionVersion;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(
                    plan,
                    linkedCancellation.Token);
            var materials = candidateResult?.Candidates.ToList() ?? [];
            var recipeBasis = candidateResult?.RecipeBasis;
            if (_session.PlanSessionVersion != planSessionVersion)
            {
                operation.Cancel();
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
            }

            if (plan == null || materials.Count == 0 || string.IsNullOrWhiteSpace(request.SelectedDataCenter))
            {
                operation.Cancel();
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
            }

            _session.ClearMarketAnalysis("market analysis run started");
            operation.RefreshSessionStamp();
            plan = _session.ActivePlan;
            if (plan == null || _session.PlanSessionVersion != planSessionVersion)
            {
                operation.Cancel();
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
            }

            var capturedVersions = _session.CaptureVersionStamp();
            var executionResult = await _marketAnalysisExecution.ExecuteAsync(
                new MarketAnalysisExecutionRequest
                {
                    Items = materials,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    MaxAge = request.ForceRefreshData ? TimeSpan.Zero : null,
                    RecommendationMode = request.RecommendationMode,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
                },
                progress,
                linkedCancellation.Token,
                executionOptions: request.ExecutionOptions);
            linkedCancellation.Token.ThrowIfCancellationRequested();

            if (_session.CaptureVersionStamp().PlanSession != capturedVersions.PlanSession)
            {
                operation.Cancel();
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
            }

            var published = PublishMarketAnalysis(
                operation,
                plan,
                planSessionVersion,
                capturedVersions,
                executionResult.Analyses,
                executionResult.ShoppingPlans,
                request.RecommendationMode,
                request.Lens,
                recipeBasis);
            if (published == null)
            {
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
            }

            return new CoreMarketAnalysisWorkflowResult(
                true,
                executionResult.ShoppingPlans.Count,
                published.ChangedDecisionCount,
                executionResult.Evidence.FetchedCount);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            throw;
        }
    }

    public async Task<CoreMarketAnalysisWorkflowResult> ApplyLensAsync(
        CoreApplyMarketAnalysisLensRequest request,
        CancellationToken ct = default)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.MarketAnalysis,
            "Applying Market Lens",
            "Reprojecting market analysis...");

        try
        {
            var plan = _session.ActivePlan;
            var planSessionVersion = _session.PlanSessionVersion;
            var evidence = _session.MarketEvidence;
            if (plan == null || evidence.ItemAnalyses.Count == 0)
            {
                operation.Cancel();
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
            }

            ct.ThrowIfCancellationRequested();
            var capturedVersions = _session.CaptureVersionStamp();
            var shoppingPlans = evidence.ItemAnalyses
                .Select(analysis => _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, request.Lens))
                .ToList();
            ct.ThrowIfCancellationRequested();
            var published = PublishMarketAnalysis(
                operation,
                plan,
                planSessionVersion,
                capturedVersions,
                evidence.ItemAnalyses,
                shoppingPlans,
                evidence.RecommendationMode,
                request.Lens,
                evidence.RecipeBasis);
            if (published == null)
            {
                return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
            }

            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            return new CoreMarketAnalysisWorkflowResult(
                true,
                shoppingPlans.Count,
                published.ChangedDecisionCount,
                0);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            throw;
        }
    }

    private PublishMarketAnalysisResult? PublishMarketAnalysis(
        CraftOperationLease operation,
        CraftingPlan plan,
        long planSessionVersion,
        CraftSessionVersionStamp capturedVersions,
        IEnumerable<MarketItemAnalysis> analyses,
        List<DetailedShoppingPlan> shoppingPlans,
        RecommendationMode recommendationMode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis)
    {
        var currentVersions = _session.CaptureVersionStamp();
        if (currentVersions.PlanSession != capturedVersions.PlanSession)
        {
            operation.Cancel();
            return null;
        }

        if (currentVersions.SettingsContext != capturedVersions.SettingsContext)
        {
            operation.Cancel();
            return null;
        }

        var analysisList = analyses.ToList();
        var changedDecisions = AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, shoppingPlans);
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);
        if (_session.CaptureVersionStamp().PlanSession != capturedVersions.PlanSession)
        {
            operation.Cancel();
            return null;
        }

        var published = false;
        var completed = operation.CompleteIfCurrent(
            () =>
            {
                var stamp = _session.CaptureVersionStamp();
                published = _session.TryPublishMarketAnalysis(
                    stamp,
                    plan,
                    planSessionVersion,
                    analysisList,
                    shoppingPlans,
                    changedDecisions > 0,
                    "market analysis published",
                    recommendationMode: recommendationMode,
                    lens: lens,
                    recipeBasis: recipeBasis);
            },
            "Market analysis published.");
        if (!completed || !published)
        {
            operation.Cancel();
            return null;
        }

        return new PublishMarketAnalysisResult(changedDecisions);
    }

    private sealed record PublishMarketAnalysisResult(int ChangedDecisionCount);
}
