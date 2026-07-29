using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TradeCompanyClientOrchestrator _company;
    private readonly TradeOrderMutationService _orders;

    public TradeCompanyCollaborationService(
        TradeCompanyClientOrchestrator company,
        TradeOrderMutationService orders)
    {
        _company = company;
        _orders = orders;
    }

    public IReadOnlyList<TradeCommissionInterest> GetPendingInterests(Guid orderId) =>
        _company.GetRecords(TradeCompanyRecordKinds.Collaboration)
            .Select(record => Deserialize<TradeCommissionInterest>(record.PayloadJson))
            .Where(claim =>
                claim != null &&
                string.Equals(
                    claim.DocumentKind,
                    TradeCompanyWebDocumentKinds.InterestProjection,
                    StringComparison.Ordinal) &&
                claim.OrderId == orderId &&
                claim.State == TradeCommissionInterestState.Pending)
            .Cast<TradeCommissionInterest>()
            .OrderBy(claim => claim.CreatedAtUtc)
            .ToArray();

    public TradeCommissionPublicationProjection? GetPublication(Guid orderId) =>
        _company.GetRecords(TradeCompanyRecordKinds.Publication)
            .Select(record => Deserialize<TradeCommissionPublicationProjection>(record.PayloadJson))
            .Where(publication =>
                publication?.OrderId == orderId &&
                string.Equals(
                    publication.DocumentKind,
                    TradeCompanyWebDocumentKinds.PublicationProjection,
                    StringComparison.Ordinal))
            .OrderByDescending(publication => publication!.UpdatedAtUtc)
            .FirstOrDefault();

    public async Task<TradeCommissionWorkflowResult> PublishToDiscordAsync(
        TradeOrder order,
        CommissionBriefDocument brief,
        CancellationToken cancellationToken = default)
    {
        if (!_company.CanPerformExternalAction(order.Id, out var reason))
        {
            return new TradeCommissionWorkflowResult(
                false,
                TradeCompanyMutationDisposition.Rejected,
                Message: reason);
        }

        var ownership = _company.GetPublicationOwnership(order.Id);
        if (ownership == null)
        {
            return new TradeCommissionWorkflowResult(
                false,
                TradeCompanyMutationDisposition.Rejected,
                Message: "Sync this order before publishing it to Discord.");
        }

        var command = new TradeCommissionPublicationCommand(
            "publish",
            order.Id,
            TradeCommissionDestination.DiscordChannel,
            brief,
            ownership.OrderRevision,
            DateTime.UtcNow);
        var result = await _company.MutateAsync(
            TradeCompanyRecordKinds.Publication,
            order.Id.ToString("D"),
            command,
            requiresCurrentCompany: true,
            cancellationToken: cancellationToken);
        var publication = Deserialize<TradeCommissionPublicationProjection>(result.Record?.PayloadJson);
        if (!string.Equals(
                publication?.DocumentKind,
                TradeCompanyWebDocumentKinds.PublicationProjection,
                StringComparison.Ordinal))
        {
            publication = null;
        }

        var succeeded = result.IsRemoteCurrent &&
            publication != null &&
            publication.State != TradeCommissionDeliveryState.Failed;
        return new TradeCommissionWorkflowResult(
            succeeded,
            result.Disposition,
            publication,
            Message: publication?.Message ??
                (publication == null
                    ? "The company did not return publication delivery state."
                    : result.Message));
    }

    public Task<TradeCommissionWorkflowResult> AcceptInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid crafterId,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(
            "accept",
            order,
            claim,
            crafterId,
            cancellationToken);

    public Task<TradeCommissionWorkflowResult> DeclineInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(
            "decline",
            order,
            claim,
            null,
            cancellationToken);

    private async Task<TradeCommissionWorkflowResult> ResolveInterestAsync(
        string action,
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid? crafterId,
        CancellationToken cancellationToken)
    {
        if (!_company.CanPerformExternalAction(order.Id, out var reason))
        {
            return new TradeCommissionWorkflowResult(
                false,
                TradeCompanyMutationDisposition.Rejected,
                Message: reason);
        }

        var ownership = _company.GetPublicationOwnership(order.Id);
        if (ownership == null)
        {
            return new TradeCommissionWorkflowResult(
                false,
                TradeCompanyMutationDisposition.Rejected,
                Message: "Refresh this order before resolving crafter interest.");
        }

        var command = new TradeCommissionInterestResolutionCommand(
            action,
            claim.ClaimId,
            order.Id,
            crafterId,
            ownership.OrderRevision,
            DateTime.UtcNow);
        var result = await _company.MutateAsync(
            TradeCompanyRecordKinds.Collaboration,
            claim.ClaimId,
            command,
            requiresCurrentCompany: true,
            cancellationToken: cancellationToken);
        var receipt = Deserialize<TradeCommissionInterestResolutionReceipt>(result.Record?.PayloadJson);
        if (!string.Equals(
                receipt?.DocumentKind,
                TradeCompanyWebDocumentKinds.InterestResolutionReceipt,
                StringComparison.Ordinal))
        {
            receipt = null;
        }

        var receiptIsComplete = receipt != null &&
            (!string.Equals(action, "accept", StringComparison.Ordinal) ||
             receipt.UpdatedOrder != null);
        if (result.IsRemoteCurrent && receipt?.UpdatedOrder != null)
        {
            var local = await _orders.ApplyCanonicalOrderAsync(receipt.UpdatedOrder, cancellationToken);
            if (!local.LocalSaved)
            {
                return new TradeCommissionWorkflowResult(
                    false,
                    TradeCompanyMutationDisposition.Rejected,
                    Resolution: receipt,
                    Message: local.Message);
            }
        }

        return new TradeCommissionWorkflowResult(
            result.IsRemoteCurrent && receiptIsComplete,
            result.Disposition,
            Resolution: receipt,
            Message: receipt?.Message ??
                (receiptIsComplete
                    ? result.Message
                    : "The company did not return a complete claim resolution."));
    }

    private static T? Deserialize<T>(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
