using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeProcurementSourceMutationPolicy
{
    public static bool CanUseLivePlan(
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
