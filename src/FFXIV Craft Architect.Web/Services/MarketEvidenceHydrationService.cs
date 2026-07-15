using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketEvidenceHydrationService : IDisposable
{
    private readonly AppState _appState;
    private readonly MarketAnalysisWorkflowService _workflow;
    private readonly ILogger<MarketEvidenceHydrationService> _logger;
    private CancellationTokenSource? _cancellation;
    private long _generation;

    public MarketEvidenceHydrationService(
        AppState appState,
        MarketAnalysisWorkflowService workflow,
        ILogger<MarketEvidenceHydrationService> logger)
    {
        _appState = appState;
        _workflow = workflow;
        _logger = logger;
    }

    public void ScheduleAfterPlanLoad(PlanSessionLoadResult session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        var generation = ++_generation;

        if (!NeedsHydration(session, DateTime.UtcNow))
        {
            _appState.SetMarketEvidenceHydrating(false);
            return;
        }

        var plan = session.Plan!;
        var planSessionVersion = _appState.PlanSessionVersion;
        _cancellation = new CancellationTokenSource();
        _appState.SetMarketEvidenceHydrating(true);
        _ = HydrateAsync(plan, planSessionVersion, generation, _cancellation.Token);
    }

    public static bool NeedsHydration(PlanSessionLoadResult session, DateTime nowUtc)
    {
        if (session.Plan == null || !HasMarketCandidate(session.Plan))
        {
            return false;
        }

        var hasActionableEvidence = session.ShoppingPlans.Count > 0 &&
                                    session.MarketItemAnalyses.Count > 0;
        if (!hasActionableEvidence)
        {
            return true;
        }

        var publishedAtUtc = session.PublishedMarketAnalysisScope?.PublishedAtUtc;
        return publishedAtUtc == null ||
               nowUtc - publishedAtUtc.Value > MarketEvidencePolicyDefaults.ReusableCacheMaxAge;
    }

    private static bool HasMarketCandidate(CraftingPlan plan)
    {
        return plan.RootItems.Any(HasMarketCandidate);
    }

    private static bool HasMarketCandidate(PlanNode node)
    {
        return node.CanBuyFromMarket || node.Children.Any(HasMarketCandidate);
    }

    private async Task HydrateAsync(
        CraftingPlan plan,
        long planSessionVersion,
        long generation,
        CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
            {
                return;
            }

            var result = await _workflow.RunAnalysisAsync(
                new MarketAnalysisWorkflowRequest(
                    ForceRefreshData: false,
                    PreserveExistingEvidence: true),
                ct: ct);
            if (!result.Published && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Background market evidence hydration did not publish a result");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background market evidence hydration failed");
        }
        finally
        {
            if (generation == _generation)
            {
                _appState.SetMarketEvidenceHydrating(false);
            }
        }
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}
