using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CompanyCommissionProjectionService
{
    public static CompanyCommissionPublicBrief CreatePublicBrief(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var commission = order.CompanyCommission ??
            throw new InvalidOperationException("The Trade order does not have a canonical company commission.");

        return new CompanyCommissionPublicBrief
        {
            PublicBriefId = commission.PublicMetadata.PublicBriefId,
            CommissionId = commission.CommissionId,
            Reference = commission.Reference,
            ViewState = commission.PublicMetadata.ViewState,
            Terms = commission.CurrentTerms,
            Status = order.Status,
            Gates = commission.Gates,
            ClearedToWork = commission.ClearedToWork,
            AssignedCrafterId = order.AssignedCrafterId,
            ProvisionalCrafter = commission.ProvisionalCrafter,
            OutputProgress = commission.OutputProgress,
            DeliveryReadiness = commission.DeliveryReadiness,
            SettlementState = commission.SettlementState,
            Closed = commission.IsClosed(order.Status),
            Activity = commission.Activity,
            ProjectionRevision = commission.Activity.LastOrDefault()?.CommissionRevision ?? 0
        };
    }
}
