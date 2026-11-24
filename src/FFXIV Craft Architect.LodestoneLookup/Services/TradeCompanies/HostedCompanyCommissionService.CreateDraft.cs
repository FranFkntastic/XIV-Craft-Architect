using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed partial class HostedCompanyCommissionService
{
    /// <summary>
    /// Commits the canonical unpublished commission for a new Trade order, or converts a plain
    /// company draft at the exact revision the browser converted from. The host rebuilds the
    /// draft with the shared Core builder, so a second browser converting the same order either
    /// replays the identical draft or is refused with already_canonical / revision_conflict;
    /// nothing is written twice.
    /// </summary>
    public async Task<CompanyCommissionMutationResult> CreateDraftAsync(
        TradeCompanyAccessContext access,
        Guid commissionId,
        CreateCompanyCommissionDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireCompanyOperator(access);
        if (request?.WorkPackage is not { } workPackage ||
            request.Brief == null ||
            workPackage.Id != commissionId ||
            workPackage.CompanyProfileId != access.CompanyId.Value)
        {
            return RefuseDraft(
                access,
                commissionId,
                "invalid_draft",
                "The draft work package does not belong to this company commission.");
        }

        TradeOrder draft;
        try
        {
            draft = TradeCompanyCommissionMigrationService.CreateCanonicalDraft(
                workPackage,
                request.Brief);
        }
        catch (InvalidOperationException exception)
        {
            return RefuseDraft(access, commissionId, "invalid_draft", exception.Message);
        }

        var payload = JsonSerializer.Serialize(draft, JsonOptions);
        var canonicalAccess = ToCanonicalAccess(access);
        var recordId = commissionId.ToString("D");
        var current = await companies.LoadRecordAsync(
            canonicalAccess,
            TradeCompanyRecordKinds.Order,
            recordId,
            cancellationToken);
        var existing = await ResolveExistingDraftAsync(
            access,
            canonicalAccess,
            commissionId,
            current,
            payload,
            cancellationToken);
        if (existing != null)
        {
            return existing;
        }
        if ((current?.RecordRevision.Value ?? 0) != request.ExpectedObjectRevision.Value)
        {
            return RefuseDraft(
                access,
                commissionId,
                "revision_conflict",
                "The hosted Trade order changed before it became a commission draft.",
                CompanyCommissionMutationStatus.Conflict);
        }

        var plan = await companies.EnsureLinkedPlanDependencyAsync(
            access,
            workPackage,
            cancellationToken);
        if (plan != null)
        {
            return RefuseDraft(
                access,
                commissionId,
                plan.ErrorCode ?? "linked_plan_invalid",
                plan.ErrorMessage ?? "The draft's generated plan could not be adopted.");
        }

        var mutation = await companies.PutRecordAsync(
            canonicalAccess,
            TradeCompanyRecordKinds.Order,
            recordId,
            payload,
            request.ExpectedObjectRevision,
            $"commission-draft:{commissionId:D}",
            cancellationToken);
        if (mutation.Success && mutation.Record != null)
        {
            return new CompanyCommissionMutationResult(
                mutation.Status == TradeCompanyMutationStatus.Replayed
                    ? CompanyCommissionMutationStatus.Replayed
                    : CompanyCommissionMutationStatus.Applied,
                draft,
                draft.CompanyCommission!.Activity[^1],
                ObjectRevision: mutation.Record.RecordRevision,
                CompanyRevision: mutation.CompanyRevision ??
                    await companies.LoadCompanyRevisionAsync(canonicalAccess, cancellationToken));
        }

        var afterConflict = await companies.LoadRecordAsync(
            canonicalAccess,
            TradeCompanyRecordKinds.Order,
            recordId,
            cancellationToken);
        return await ResolveExistingDraftAsync(
                   access,
                   canonicalAccess,
                   commissionId,
                   afterConflict,
                   payload,
                   cancellationToken) ??
               RefuseDraft(
                   access,
                   commissionId,
                   mutation.ErrorCode == "company_scope_mismatch"
                       ? "company_scope_mismatch"
                       : "revision_conflict",
                   mutation.ErrorMessage ??
                   "The hosted Trade order changed before it became a commission draft.",
                   CompanyCommissionMutationStatus.Conflict);
    }

    /// <summary>
    /// An already-canonical record answers the request by itself: the identical draft replays,
    /// anything else is already_canonical. A plain or missing record returns null.
    /// </summary>
    private async Task<CompanyCommissionMutationResult?> ResolveExistingDraftAsync(
        TradeCompanyAccessContext access,
        TradeCompanyAccessContext canonicalAccess,
        Guid commissionId,
        TradeCompanyRecordEnvelope? current,
        string payload,
        CancellationToken cancellationToken)
    {
        if (current == null)
        {
            return null;
        }

        TradeOrder? stored;
        try
        {
            stored = JsonSerializer.Deserialize<TradeOrder>(current.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            stored = null;
        }
        if (stored?.CompanyCommission == null)
        {
            return null;
        }

        if (string.Equals(current.PayloadJson, payload, StringComparison.Ordinal) ||
            string.Equals(
                JsonSerializer.Serialize(stored, JsonOptions),
                payload,
                StringComparison.Ordinal))
        {
            return new CompanyCommissionMutationResult(
                CompanyCommissionMutationStatus.Replayed,
                stored,
                stored.CompanyCommission.Activity.LastOrDefault(),
                ObjectRevision: current.RecordRevision,
                CompanyRevision: await companies.LoadCompanyRevisionAsync(
                    canonicalAccess,
                    cancellationToken));
        }

        return RefuseDraft(
            access,
            commissionId,
            "already_canonical",
            "This Trade order is already a company commission. Its current hosted version is used instead.",
            CompanyCommissionMutationStatus.Conflict);
    }

    private CompanyCommissionMutationResult RefuseDraft(
        TradeCompanyAccessContext access,
        Guid commissionId,
        string code,
        string message,
        CompanyCommissionMutationStatus status = CompanyCommissionMutationStatus.Rejected)
    {
        logger.LogWarning(
            "Commission draft creation refused with {Code} for company {CompanyId}, commission {CommissionId}: {Reason}",
            code,
            access.CompanyId,
            commissionId,
            message);
        return new CompanyCommissionMutationResult(
            status,
            ErrorCode: code,
            ErrorMessage: message);
    }
}
