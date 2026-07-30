using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationService(
    TradeCompanyCollaborationClient client,
    TradeOperationsPersistenceService tradeOperations,
    ProfileSyncLocalStateService localState,
    ProfileSyncService profileSync)
{
    private readonly Dictionary<Guid, IReadOnlyList<TradeCommissionInterest>> _interests = [];
    private readonly Dictionary<Guid, TradeCommissionPublicationProjection> _publications = [];

    public IReadOnlyList<TradeCommissionInterest> GetPendingInterests(Guid orderId) =>
        _interests.GetValueOrDefault(orderId, [])
            .Where(claim => claim.State == TradeCommissionInterestState.Pending)
            .OrderBy(claim => claim.CreatedAtUtc)
            .ToArray();

    public TradeCommissionPublicationProjection? GetPublication(Guid orderId) =>
        _publications.GetValueOrDefault(orderId);

    public bool CanPerformExternalAction(TradeOrder order, out string reason)
    {
        if (!profileSync.CurrentStatus.IsConnected)
        {
            reason = "Connect Profile Hosting in Options first.";
            return false;
        }

        if (!profileSync.CurrentStatus.HostReachable)
        {
            reason = profileSync.CurrentStatus.Message ?? "Profile Hosting is unavailable.";
            return false;
        }

        var objectId = order.Id.ToString("D");
        if (profileSync.PendingSaves.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)) ||
            profileSync.Conflicts.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Resolve the pending hosted order update before continuing.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public async Task<TradeCompanyPublicationOwnership?> GetPublicationOwnershipAsync(
        TradeOrder order)
    {
        if (!CanPerformExternalAction(order, out _))
        {
            return null;
        }

        var revision = await localState.LoadObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        return revision > 0
            ? new TradeCompanyPublicationOwnership(
                new CompanyId(order.CompanyProfileId),
                order.Id,
                new CompanyRecordRevision(revision))
            : null;
    }

    public async Task RefreshAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var interests = await client.LoadPendingInterestsAsync(
            companyProfileId,
            orderId,
            cancellationToken);
        var publication = await client.LoadPublicationAsync(
            companyProfileId,
            orderId,
            cancellationToken);
        _interests[orderId] = interests;
        if (publication == null)
        {
            _publications.Remove(orderId);
        }
        else
        {
            _publications[orderId] = publication;
        }
    }

    public async Task<TradeCommissionWorkflowResult> PublishToDiscordAsync(
        TradeOrder order,
        CommissionBriefDocument brief,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            return Rejected(reason);
        }

        var revision = await localState.LoadObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        if (revision <= 0)
        {
            return Rejected("Sync this order through Profile Hosting before publishing it.");
        }

        try
        {
            var publication = await client.PublishAsync(
                order.CompanyProfileId,
                order.Id,
                revision,
                brief,
                $"discord-publish:{order.Id:D}:{revision}",
                cancellationToken);
            _publications[order.Id] = publication;
            return new TradeCommissionWorkflowResult(
                publication.State is
                    TradeCommissionDeliveryState.Pending or
                    TradeCommissionDeliveryState.Published,
                TradeCompanyMutationDisposition.Synced,
                publication,
                Message: publication.Message);
        }
        catch (Exception exception)
        {
            return Rejected(exception.Message);
        }
    }

    public Task<TradeCommissionWorkflowResult> AcceptInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid crafterId,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(true, order, claim, crafterId, cancellationToken);

    public Task<TradeCommissionWorkflowResult> DeclineInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(false, order, claim, null, cancellationToken);

    public async Task RevokePublicationAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await client.RevokeAsync(
            ownership.CompanyId.Value,
            publicId,
            cancellationToken);
        _publications.Remove(ownership.OrderId);
    }

    private async Task<TradeCommissionWorkflowResult> ResolveInterestAsync(
        bool accept,
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid? crafterId,
        CancellationToken cancellationToken)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            return Rejected(reason);
        }

        try
        {
            var receipt = accept
                ? await client.AcceptAsync(
                    order.CompanyProfileId,
                    claim.ClaimId,
                    crafterId ?? throw new InvalidOperationException(
                        "Choose a hosted company crafter before accepting interest."),
                    $"discord-claim:{claim.ClaimId}:{crafterId:D}",
                    cancellationToken)
                : await client.DeclineAsync(
                    order.CompanyProfileId,
                    claim.ClaimId,
                    cancellationToken);

            if (receipt.UpdatedOrder != null)
            {
                if (!await tradeOperations.SaveOrderAsync(receipt.UpdatedOrder))
                {
                    return Rejected(
                        "The hosted assignment was accepted, but the order could not be saved locally.");
                }

                if (receipt.UpdatedOrderRevision is > 0)
                {
                    await localState.SaveObjectRevisionAsync(
                        ProfileSyncCollections.TradeOrders,
                        receipt.UpdatedOrder.Id.ToString("D"),
                        receipt.UpdatedOrderRevision.Value);
                }
            }

            _interests[order.Id] = GetPendingInterests(order.Id)
                .Where(candidate => candidate.ClaimId != claim.ClaimId)
                .ToArray();
            return new TradeCommissionWorkflowResult(
                !accept || receipt.UpdatedOrder != null,
                TradeCompanyMutationDisposition.Synced,
                Resolution: receipt,
                Message: receipt.Message);
        }
        catch (Exception exception)
        {
            return Rejected(exception.Message);
        }
    }

    private static TradeCommissionWorkflowResult Rejected(string message) =>
        new(false, TradeCompanyMutationDisposition.Rejected, Message: message);
}
