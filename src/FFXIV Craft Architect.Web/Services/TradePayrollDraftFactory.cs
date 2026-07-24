using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradePayrollDraftFactory
{
    private readonly CommissionPayrollService _payrollService;

    public TradePayrollDraftFactory(CommissionPayrollService payrollService)
    {
        _payrollService = payrollService;
    }

    public TradePayrollDraftCreateResult CreateFromCurrentPlan(WorkerTradeProjection source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.HasPlan)
        {
            return TradePayrollDraftCreateResult.Unavailable("Create or load a craft plan before starting payroll.");
        }

        if (source.MaterialLines.Count == 0)
        {
            return TradePayrollDraftCreateResult.Unavailable("The active craft plan does not have material demand to pay against.");
        }

        var snapshot = new TradePayrollImportSnapshot(
            source.PlanSessionVersion,
            source.MarketAnalysisVersion,
            source.PlanName,
            DateTime.UtcNow,
            source.SelectedDataCenter,
            source.SelectedRegion,
            source.MarketFetchScope,
            source.MarketLens,
            source.CraftedItems,
            source.MaterialLines,
            source.Warnings);

        var payroll = _payrollService.Calculate(source.MaterialLines, CommissionPayoutPolicy.Default);
        return TradePayrollDraftCreateResult.Available(new TradePayrollDraft(snapshot, payroll));
    }
}

public sealed record TradePayrollDraftCreateResult(
    bool CanCreate,
    TradePayrollDraft? Draft,
    string? UnavailableReason)
{
    public static TradePayrollDraftCreateResult Available(TradePayrollDraft draft)
    {
        return new TradePayrollDraftCreateResult(true, draft, null);
    }

    public static TradePayrollDraftCreateResult Unavailable(string reason)
    {
        return new TradePayrollDraftCreateResult(false, null, reason);
    }
}
