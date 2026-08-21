using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CompanyCommissionProjectionService
{
    public static CompanyCommissionPublicBrief CreatePublicBrief(
        TradeOrder order,
        string companyDisplayName)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyDisplayName);
        var commission = order.CompanyCommission ??
            throw new InvalidOperationException("The Trade order does not have a canonical company commission.");

        return new CompanyCommissionPublicBrief
        {
            PublicBriefId = commission.PublicMetadata.PublicBriefId,
            CommissionId = commission.CommissionId,
            CompanyId = commission.CompanyId,
            Title = order.Title,
            CompanyDisplayName = companyDisplayName,
            Reference = commission.Reference,
            ViewState = commission.PublicMetadata.ViewState,
            IsTestFixture = commission.PublicMetadata.IsTestFixture,
            Terms = CreatePublicTerms(commission.CurrentTerms),
            Status = order.Status,
            Gates = new CompanyCommissionPublicGateState(
                commission.Gates.Identity.State,
                commission.Gates.Payment.State,
                commission.Gates.CompanyMaterials.State),
            ClearedToWork = commission.ClearedToWork,
            IsClaimed = commission.ActiveClaim != null,
            RequiresManualResolution = commission.ManualResolution != null,
            OutputProgress = commission.OutputProgress
                .Select(progress => new CompanyCommissionPublicOutputProgress(
                    progress.LineId,
                    progress.ItemId,
                    progress.RequiredQuantity,
                    progress.CompletedQuantity,
                    progress.ReadyQuantity,
                    progress.AcceptedQuantity,
                    progress.UpdatedAtUtc))
                .ToArray(),
            DeliveryReadiness = new CompanyCommissionPublicDeliveryReadiness(
                commission.DeliveryReadiness.IsReady,
                commission.DeliveryReadiness.DeclaredAtUtc,
                commission.DeliveryReadiness.WithdrawnAtUtc),
            SettlementState = commission.SettlementState,
            Closed = commission.IsClosed(order.Status),
            ProjectionRevision = commission.Activity.LastOrDefault()?.CommissionRevision ?? 0
        };
    }

    public static CompanyCommissionParticipantBrief CreateParticipantBrief(
        TradeOrder order,
        string companyDisplayName)
    {
        ArgumentNullException.ThrowIfNull(order);
        var commission = order.CompanyCommission ??
            throw new InvalidOperationException("The Trade order does not have a canonical company commission.");

        return new CompanyCommissionParticipantBrief
        {
            Public = CreatePublicBrief(order, companyDisplayName),
            ProvisionalCrafter = commission.ProvisionalCrafter,
            ClaimAccountEvidence = commission.ActiveClaim?.AccountEvidence,
            ParticipantCapabilityRevision = commission.ParticipantGrant?.CapabilityRevision ?? 0,
            Payment = commission.Gates.Payment,
            CompanyMaterialsReadyForHandoff =
                commission.Gates.CompanyMaterials.State == CompanyCommissionClearanceState.Pending &&
                commission.Gates.CompanyMaterials.ReadyAtUtc.HasValue,
            SettlementPayment = commission.SettlementPayment,
            Activity = commission.Activity
                .Where(item =>
                    item.Visibility == CompanyCommissionActivityVisibility.Shared)
                .Select(item => new CompanyCommissionParticipantActivity(
                item.EventId,
                item.CommissionRevision,
                item.Actor.Kind,
                item.Actor.DisplayName,
                item.SourceSurface,
                item.CreatedAtUtc,
                item.Kind,
                item.TermsVersion,
                item.Comment))
                .ToArray()
        };
    }

    private static CompanyCommissionPublicTerms CreatePublicTerms(
        CompanyCommissionTermsVersion terms) =>
        new()
        {
            Version = terms.Version,
            Outputs = terms.Outputs,
            Materials = terms.Materials,
            Payment = terms.Payment,
            DeliveryInstructions = terms.DeliveryInstructions,
            PricingEvidence = terms.PricingEvidence,
            ContactInstructions = terms.ContactInstructions
        };
}
