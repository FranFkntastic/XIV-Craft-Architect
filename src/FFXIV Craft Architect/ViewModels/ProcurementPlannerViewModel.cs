using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ViewModels;

public sealed partial class ProcurementPlannerViewModel : ViewModelBase
{
    private readonly CraftSessionState _session;
    private readonly CoreProcurementWorkflowService? _coreProcurementWorkflow;
    private string _statusMessage = string.Empty;

    public ProcurementPlannerViewModel(
        CraftSessionState session,
        CoreProcurementWorkflowService? coreProcurementWorkflow = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coreProcurementWorkflow = coreProcurementWorkflow;
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public IReadOnlyList<DetailedShoppingPlan> CurrentOverlayShoppingPlans =>
        _session.ProcurementOverlay?.ShoppingPlans?.ToList() ?? [];

    public IReadOnlyList<WorldProcurementCardModel> CurrentOverlayRouteCards =>
        _session.ProcurementOverlay?.RouteCards?.ToList() ?? [];

    public void BlacklistMarketWorldTemporarily(MarketWorldKey world, string reason) =>
        _session.BlacklistMarketWorldTemporarily(world, reason: reason);

    public void ExcludeItemWorldTemporarily(int itemId, MarketWorldKey world, string reason) =>
        _session.ExcludeItemWorldTemporarily(itemId, world, reason);

    public void ClearTemporaryProcurementExclusions(string reason) =>
        _session.ClearTemporaryProcurementExclusions(reason);

    public async Task<CoreProcurementWorkflowResult> RunCoreProcurementAnalysisAsync(
        CoreProcurementWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentMarketEvidence(out var shoppingPlans, out var statusMessage, out var status))
        {
            StatusMessage = statusMessage;
            return CoreProcurementWorkflowResult.Noop(status);
        }

        if (_coreProcurementWorkflow == null)
        {
            var legacyResult = BuildFromCurrentMarketEvidence();
            return legacyResult.Published
                ? new CoreProcurementWorkflowResult(CoreProcurementWorkflowStatus.Published, legacyResult.ShoppingPlanCount)
                : CoreProcurementWorkflowResult.Noop(status);
        }

        var result = await _coreProcurementWorkflow.RunAnalysisAsync(
            request with { SourceShoppingPlans = shoppingPlans },
            progress,
            ct);
        StatusMessage = result.Status switch
        {
            CoreProcurementWorkflowStatus.Published => "Procurement route published from Core workflow",
            CoreProcurementWorkflowStatus.NoPlan => "No plan - build a plan first",
            CoreProcurementWorkflowStatus.NoActiveProcurementItems => "No active procurement items found.",
            CoreProcurementWorkflowStatus.MissingDataCenter => "Select a data center before building procurement.",
            CoreProcurementWorkflowStatus.StaleDecision => "Procurement plan needs fresh market analysis after acquisition changes.",
            CoreProcurementWorkflowStatus.StaleConfiguration => "Procurement plan needs fresh market analysis after market settings changes.",
            CoreProcurementWorkflowStatus.StalePlan => "Procurement plan needs a current recipe plan.",
            CoreProcurementWorkflowStatus.Superseded => "Procurement analysis was superseded.",
            _ => "Procurement analysis did not publish."
        };

        return result;
    }

    public ProcurementPlannerBuildResult BuildFromCurrentMarketEvidence()
    {
        if (!TryGetCurrentMarketEvidence(out var shoppingPlans, out var statusMessage, out _))
        {
            StatusMessage = statusMessage;
            return new ProcurementPlannerBuildResult(false, 0);
        }

        _session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(
                DateTime.UtcNow,
                shoppingPlans.Select(plan => plan.ItemId).ToArray(),
                "wpf procurement plan built from market evidence",
                shoppingPlans,
                ProcurementWorldCardBuilder.BuildWorldCards(
                    shoppingPlans,
                    _session.ActiveContext.DataCenter ?? _session.ActivePlan?.DataCenter ?? string.Empty)),
            "wpf procurement plan built");

        StatusMessage = "Procurement plan built from current market evidence";
        return new ProcurementPlannerBuildResult(true, shoppingPlans.Count);
    }

    private bool TryGetCurrentMarketEvidence(
        out IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        out string statusMessage,
        out CoreProcurementWorkflowStatus status)
    {
        shoppingPlans = [];
        if (_session.ActivePlan == null)
        {
            statusMessage = "No plan - build a plan first";
            status = CoreProcurementWorkflowStatus.NoPlan;
            return false;
        }

        var evidence = _session.MarketEvidence;
        shoppingPlans = evidence.ShoppingPlans?.ToList() ?? [];
        if (shoppingPlans.Count == 0)
        {
            statusMessage = "No market analysis data found. Run Conduct Analysis in Market Analysis first.";
            status = CoreProcurementWorkflowStatus.NoActiveProcurementItems;
            return false;
        }

        var currentStamp = _session.CaptureVersionStamp();
        var evidenceStamp = evidence.PublishedAgainstVersion;
        if (evidenceStamp == null ||
            evidenceStamp.Value.PlanSession != currentStamp.PlanSession ||
            evidenceStamp.Value.PlanDecision != currentStamp.PlanDecision)
        {
            statusMessage = "Procurement plan needs fresh market analysis after acquisition changes.";
            status = CoreProcurementWorkflowStatus.StaleDecision;
            return false;
        }

        if (evidenceStamp.Value.SettingsContext != currentStamp.SettingsContext)
        {
            statusMessage = "Procurement plan needs fresh market analysis after market settings changes.";
            status = CoreProcurementWorkflowStatus.StaleConfiguration;
            return false;
        }

        statusMessage = string.Empty;
        status = CoreProcurementWorkflowStatus.Published;
        return true;
    }
}

public sealed record ProcurementPlannerBuildResult(
    bool Published,
    int ShoppingPlanCount);
