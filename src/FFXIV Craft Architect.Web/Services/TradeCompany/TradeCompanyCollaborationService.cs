using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationService
{
    private readonly TradeCompanyClientOrchestrator _company;
    private readonly TradeOrderMutationService _orders;
    private readonly ITradeCompanyCollaborationClient _collaboration;
    private readonly Dictionary<Guid, IReadOnlyList<TradeCommissionInterest>> _interests = [];
    private readonly Dictionary<Guid, TradeCommissionPublicationProjection> _publications = [];

    public TradeCompanyCollaborationService(
        TradeCompanyClientOrchestrator company,
        TradeOrderMutationService orders,
        ITradeCompanyCollaborationClient collaboration)
    {
        _company = company;
        _orders = orders;
        _collaboration = collaboration;
    }

    public IReadOnlyList<TradeCommissionInterest> GetPendingInterests(Guid orderId) =>
        _interests.GetValueOrDefault(orderId, [])
            .Where(claim => claim.State == TradeCommissionInterestState.Pending)
            .OrderBy(claim => claim.CreatedAtUtc)
            .ToArray();

    public TradeCommissionPublicationProjection? GetPublication(Guid orderId) =>
        _publications.GetValueOrDefault(orderId);

    public async Task RefreshAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var interests = await _collaboration.LoadPendingInterestsAsync(
            companyId,
            orderId,
            cancellationToken);
        var publication = await _collaboration.LoadPublicationAsync(
            companyId,
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
        if (!_company.CanPerformExternalAction(order.Id, out var reason))
        {
            return Rejected(reason);
        }

        var ownership = _company.GetPublicationOwnership(order.Id);
        if (ownership == null)
        {
            return Rejected("Sync this order before publishing it to Discord.");
        }

        try
        {
            var publication = await _collaboration.PublishAsync(
                ownership.CompanyId,
                order.Id,
                ownership.OrderRevision,
                brief,
                $"discord-publish:{order.Id:D}:{ownership.OrderRevision.Value}",
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
        ResolveInterestAsync(
            accept: true,
            order,
            claim,
            crafterId,
            cancellationToken);

    public Task<TradeCommissionWorkflowResult> DeclineInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(
            accept: false,
            order,
            claim,
            crafterId: null,
            cancellationToken);

    public async Task RevokePublicationAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await _collaboration.RevokeAsync(
            ownership.CompanyId,
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
        if (!_company.CanPerformExternalAction(order.Id, out var reason))
        {
            return Rejected(reason);
        }

        var ownership = _company.GetPublicationOwnership(order.Id);
        if (ownership == null)
        {
            return Rejected("Refresh this order before resolving crafter interest.");
        }

        try
        {
            var receipt = accept
                ? await _collaboration.AcceptAsync(
                    ownership.CompanyId,
                    claim.ClaimId,
                    crafterId ?? throw new InvalidOperationException(
                        "Choose a canonical company crafter before accepting interest."),
                    $"discord-claim:{claim.ClaimId}:{crafterId:D}",
                    cancellationToken)
                : await _collaboration.DeclineAsync(
                    ownership.CompanyId,
                    claim.ClaimId,
                    cancellationToken);

            if (receipt.UpdatedOrder != null)
            {
                var local = await _orders.ApplyCanonicalOrderAsync(
                    receipt.UpdatedOrder,
                    order.CompanyProfileId,
                    cancellationToken);
                if (!local.LocalSaved)
                {
                    return Rejected(local.Message ??
                        "The canonical assignment could not be saved locally.");
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
        new(
            false,
            TradeCompanyMutationDisposition.Rejected,
            Message: message);
}
