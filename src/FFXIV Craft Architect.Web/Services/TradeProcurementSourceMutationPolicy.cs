using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeProcurementSourceMutationPolicy
{
    public static bool CanReadLivePlan(
        string? orderPlanId,
        string? activePlanId,
        bool snapshotHasPlan,
        string? snapshotPlanId) =>
        snapshotHasPlan &&
        !string.IsNullOrWhiteSpace(orderPlanId) &&
        string.Equals(orderPlanId, activePlanId, StringComparison.Ordinal) &&
        string.Equals(orderPlanId, snapshotPlanId, StringComparison.Ordinal);

    public static bool CanMutateLivePlan(
        bool hasCanonicalCommission,
        bool canEditCanonicalWorkPackage) =>
        !hasCanonicalCommission || canEditCanonicalWorkPackage;

    public static bool CanChangeSource(TradeOrderProcurementRow row)
    {
        return !row.IsLiveAcquisitionRow ||
            row.HasEditableOccurrences ||
            row is
            {
                IsFullySuppressed: true,
                HasChildren: true
            };
    }
}
