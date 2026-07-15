using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreAcquisitionDecisionService
{
    private readonly CraftSessionState _session;
    private readonly ICraftOperationCoordinator _operationCoordinator;

    public CoreAcquisitionDecisionService(
        CraftSessionState session,
        ICraftOperationCoordinator operationCoordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
    }

    public CoreAcquisitionDecisionResult ChangeSource(int itemId, AcquisitionSource source)
    {
        var plan = _session.ActivePlan;
        var planSessionVersion = _session.PlanSessionVersion;
        if (plan == null)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        var nodesUpdated = AcquisitionDecisionMutation.ChangeSource(plan, itemId, source);
        if (nodesUpdated == 0)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        return PublishDecisionChange(plan, planSessionVersion, nodesUpdated);
    }

    public CoreAcquisitionDecisionResult ChangeMarketHq(int itemId, bool isHq)
    {
        var plan = _session.ActivePlan;
        var planSessionVersion = _session.PlanSessionVersion;
        if (plan == null)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        var nodesUpdated = AcquisitionDecisionMutation.ChangeMarketHq(plan, itemId, isHq);

        if (nodesUpdated == 0)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        return PublishDecisionChange(plan, planSessionVersion, nodesUpdated);
    }

    private CoreAcquisitionDecisionResult PublishDecisionChange(
        CraftingPlan plan,
        long planSessionVersion,
        int nodesUpdated)
    {
        var published = _session.TryReplaceActivePlanDecisions(
            _session.CaptureVersionStamp(),
            plan,
            planSessionVersion,
            "acquisition decision changed");
        if (!published)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        _operationCoordinator.Cancel(CraftOperationWorkflow.ProcurementAnalysis);
        return new CoreAcquisitionDecisionResult(true, nodesUpdated);
    }
}

public sealed record CoreAcquisitionDecisionResult(bool Changed, int NodesUpdated);
