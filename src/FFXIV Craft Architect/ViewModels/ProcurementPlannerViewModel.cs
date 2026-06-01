using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ViewModels;

public sealed partial class ProcurementPlannerViewModel : ViewModelBase
{
    private readonly CraftSessionState _session;
    private string _statusMessage = string.Empty;

    public ProcurementPlannerViewModel(CraftSessionState session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ProcurementPlannerBuildResult BuildFromCurrentMarketEvidence()
    {
        if (_session.ActivePlan == null)
        {
            StatusMessage = "No plan - build a plan first";
            return new ProcurementPlannerBuildResult(false, 0);
        }

        var evidence = _session.MarketEvidence;
        var shoppingPlans = evidence.ShoppingPlans?.ToList() ?? [];
        if (shoppingPlans.Count == 0)
        {
            StatusMessage = "No market analysis data found. Run Conduct Analysis in Market Analysis first.";
            return new ProcurementPlannerBuildResult(false, 0);
        }

        var currentStamp = _session.CaptureVersionStamp();
        var evidenceStamp = evidence.PublishedAgainstVersion;
        if (evidenceStamp == null ||
            evidenceStamp.Value.PlanSession != currentStamp.PlanSession ||
            evidenceStamp.Value.PlanDecision != currentStamp.PlanDecision)
        {
            StatusMessage = "Procurement plan needs fresh market analysis after acquisition changes.";
            return new ProcurementPlannerBuildResult(false, 0);
        }

        if (evidenceStamp.Value.SettingsContext != currentStamp.SettingsContext)
        {
            StatusMessage = "Procurement plan needs fresh market analysis after market settings changes.";
            return new ProcurementPlannerBuildResult(false, 0);
        }

        _session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(
                DateTime.UtcNow,
                shoppingPlans.Select(plan => plan.ItemId).ToArray(),
                "wpf procurement plan built from market evidence",
                shoppingPlans),
            "wpf procurement plan built");

        StatusMessage = "Procurement plan built from current market evidence";
        return new ProcurementPlannerBuildResult(true, shoppingPlans.Count);
    }
}

public sealed record ProcurementPlannerBuildResult(
    bool Published,
    int ShoppingPlanCount);
