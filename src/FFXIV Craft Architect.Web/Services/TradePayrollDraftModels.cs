using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradePayrollImportSnapshot(
    long PlanSessionVersion,
    long MarketAnalysisVersion,
    string SourcePlanName,
    DateTime ImportedAtUtc,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope MarketFetchScope,
    MarketAcquisitionLens MarketLens,
    IReadOnlyList<CommissionPayrollInputLine> Lines,
    IReadOnlyList<string> Warnings);

public sealed record TradePayrollDraft(
    TradePayrollImportSnapshot Source,
    CommissionPayrollRun Payroll);
