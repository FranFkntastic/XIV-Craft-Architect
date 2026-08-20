using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class CompanyOwnershipTransferService(
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships)
{
    public async Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        var completed = 0;
        foreach (var receipt in await profiles.LoadPendingOwnershipTransfersAsync(cancellationToken))
        {
            var result = await CompleteProjectionAsync(
                receipt,
                CompanyOwnershipTransferStatus.Replayed,
                cancellationToken);
            if (result.Receipt?.MembershipProjectedAtUtc.HasValue == true)
            {
                completed++;
            }
        }
        return completed;
    }

    public async Task<CompanyOwnershipTransferResult> PreviewAsync(
        CompanyId companyId,
        Guid actorProfileId,
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        HostedProfileObject? host;
        try
        {
            host = await profiles.FindObjectAsync(
                ProfileSyncCollections.TradeCompanyProfiles,
                companyId.ToString(),
                cancellationToken);
        }
        catch (DuplicateHostedObjectIdentityException)
        {
            return new(
                CompanyOwnershipTransferStatus.Conflict,
                Error: "This company has conflicting hosted owners and cannot be transferred until canonical ownership is repaired.");
        }
        if (host is not { Object.Deleted: false } ||
            !Guid.TryParse(host.ProfileId, out var sourceProfileId))
        {
            return new(CompanyOwnershipTransferStatus.NotFound);
        }
        if (sourceProfileId != actorProfileId)
        {
            return new(
                CompanyOwnershipTransferStatus.Forbidden,
                Error: "Only the account that currently hosts this company can transfer ownership.");
        }
        var targetMembership = await memberships.LoadAsync(companyId, targetProfileId, cancellationToken);
        if (targetMembership is not { State: MembershipState.Active })
        {
            return new(
                CompanyOwnershipTransferStatus.InvalidTarget,
                Error: "Choose an active company member.");
        }
        var preview = await profiles.PreviewCompanyOwnershipTransferAsync(
            companyId,
            sourceProfileId,
            targetProfileId,
            cancellationToken);
        return preview == null
            ? new(CompanyOwnershipTransferStatus.InvalidTarget, Error: "The selected member no longer has an active account.")
            : new(CompanyOwnershipTransferStatus.Applied, Preview: preview);
    }

    public async Task<CompanyOwnershipTransferResult> TransferAsync(
        CompanyId companyId,
        Guid actorProfileId,
        Guid targetProfileId,
        PreviousOwnerDisposition disposition,
        Guid idempotencyKey,
        string expectedScopeFingerprint,
        CancellationToken cancellationToken = default)
    {
        var replay = await profiles.LoadCompanyOwnershipTransferAsync(idempotencyKey, cancellationToken);
        if (replay != null)
        {
            if (replay.CompanyId != companyId || replay.SourceProfileId != actorProfileId ||
                replay.TargetProfileId != targetProfileId || replay.PreviousOwnerDisposition != disposition)
            {
                return new(CompanyOwnershipTransferStatus.Conflict, Error: "That transfer key already belongs to a different request.");
            }
            return await CompleteProjectionAsync(replay, CompanyOwnershipTransferStatus.Replayed, cancellationToken);
        }

        var preview = await PreviewAsync(companyId, actorProfileId, targetProfileId, cancellationToken);
        if (preview.Status != CompanyOwnershipTransferStatus.Applied)
        {
            return preview;
        }
        var committed = await profiles.CommitCompanyOwnershipTransferAsync(
            companyId,
            actorProfileId,
            targetProfileId,
            disposition,
            idempotencyKey,
            expectedScopeFingerprint,
            cancellationToken);
        return committed.Receipt == null
            ? committed
            : await CompleteProjectionAsync(committed.Receipt, committed.Status, cancellationToken);
    }

    private async Task<CompanyOwnershipTransferResult> CompleteProjectionAsync(
        CompanyOwnershipTransferReceipt receipt,
        CompanyOwnershipTransferStatus status,
        CancellationToken cancellationToken)
    {
        if (!receipt.MembershipProjectedAtUtc.HasValue)
        {
            var projected = await memberships.ProjectOwnershipTransferAsync(
                receipt.TransferId,
                receipt.CompanyId,
                receipt.SourceProfileId,
                receipt.TargetProfileId,
                receipt.PreviousOwnerDisposition,
                cancellationToken);
            if (!projected)
            {
                return new(
                    CompanyOwnershipTransferStatus.Conflict,
                    receipt,
                    Error: "The hosted company moved, but its membership projection could not be completed. Retry the same transfer to finish automatically.");
            }
            receipt = (await profiles.MarkOwnershipMembershipProjectedAsync(
                receipt.IdempotencyKey,
                cancellationToken))!;
        }
        return new(status, receipt);
    }
}
