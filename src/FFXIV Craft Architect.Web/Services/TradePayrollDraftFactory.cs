using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradePayrollDraftFactory
{
    private readonly CommissionCostBasisResolver _costBasisResolver;
    private readonly CommissionPayrollService _payrollService;

    public TradePayrollDraftFactory(
        CommissionCostBasisResolver costBasisResolver,
        CommissionPayrollService payrollService)
    {
        _costBasisResolver = costBasisResolver;
        _payrollService = payrollService;
    }

    public TradePayrollDraftCreateResult CreateFromCurrentPlan(AppState appState)
    {
        ArgumentNullException.ThrowIfNull(appState);

        if (appState.CurrentPlan == null)
        {
            return TradePayrollDraftCreateResult.Unavailable("Create or load a craft plan before starting payroll.");
        }

        var demand = appState.CurrentPlan.AggregatedMaterials
            .Select(CloneMaterial)
            .ToArray();
        if (demand.Length == 0)
        {
            return TradePayrollDraftCreateResult.Unavailable("The active craft plan does not have material demand to pay against.");
        }

        var warnings = new List<string>();
        if (appState.MarketItemAnalyses.Count == 0)
        {
            warnings.Add("No market-analysis evidence is loaded. Payroll uses plan prices where available and may be incomplete.");
        }
        else if (!string.IsNullOrWhiteSpace(appState.MarketAnalysisScopeWarning))
        {
            warnings.Add(appState.MarketAnalysisScopeWarning);
        }

        var lines = _costBasisResolver.BuildMarketRecommendationLines(
            demand,
            appState.MarketItemAnalyses.ToArray(),
            appState.ShoppingPlans.ToArray());
        warnings.AddRange(lines.SelectMany(line => line.Warnings));

        var versions = appState.CurrentVersions;
        var snapshot = new TradePayrollImportSnapshot(
            appState.PlanSessionVersion,
            versions.MarketAnalysisVersion,
            string.IsNullOrWhiteSpace(appState.CurrentPlanName) ? "Active craft plan" : appState.CurrentPlanName,
            DateTime.UtcNow,
            appState.SelectedDataCenter,
            appState.SelectedRegion,
            appState.DefaultMarketFetchScope,
            appState.MarketAnalysisLens,
            CraftPlanStateMapper.GetRootProjectItems(appState.CurrentPlan),
            lines,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase).ToArray());

        var payroll = _payrollService.Calculate(lines, CommissionPayoutPolicy.Default);
        return TradePayrollDraftCreateResult.Available(new TradePayrollDraft(snapshot, payroll));
    }

    private static MaterialAggregate CloneMaterial(MaterialAggregate material)
    {
        return new MaterialAggregate
        {
            ItemId = material.ItemId,
            Name = material.Name,
            IconId = material.IconId,
            TotalQuantity = material.TotalQuantity,
            UnitPrice = material.UnitPrice,
            RequiresHq = material.RequiresHq
        };
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
