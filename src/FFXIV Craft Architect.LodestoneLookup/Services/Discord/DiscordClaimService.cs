using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public enum DiscordOperatorClaimStatus
{
    Applied,
    Replayed,
    Conflict,
    Missing,
    Rejected
}

public sealed record DiscordOperatorClaimResult(
    DiscordOperatorClaimStatus Status,
    DiscordInterestClaim? Claim,
    TradeCompanyMutationResult? OrderMutation = null,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordOperatorClaimStatus.Applied or DiscordOperatorClaimStatus.Replayed;
}

public sealed class DiscordClaimService(
    SqliteDiscordCollaborationStore collaboration,
    DiscordCompanyOrderAdapter orders,
    TimeProvider timeProvider) : IDiscordVolunteerInteractionService
{
    public Task<DiscordVolunteerInteractionResult> RecordInterestAsync(
        DiscordVolunteerInteraction interaction,
        CancellationToken cancellationToken = default) =>
        collaboration.RecordInterestAsync(interaction, cancellationToken);

    public async Task<IReadOnlyList<DiscordInterestClaim>> LoadPendingAsync(
        TradeCompanyAccessContext access,
        Guid? orderId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order IDs cannot be empty.", nameof(orderId));
        }

        return await collaboration.LoadPendingClaimsAsync(
            access.CompanyId,
            orderId,
            cancellationToken);
    }

    public async Task<DiscordOperatorClaimResult> AcceptAsync(
        TradeCompanyAccessContext access,
        Guid claimId,
        Guid selectedCrafterId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(access);
        if (claimId == Guid.Empty || selectedCrafterId == Guid.Empty)
        {
            throw new ArgumentException("Claim and crafter IDs cannot be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var begun = await collaboration.BeginClaimAcceptanceAsync(
            access.CompanyId,
            claimId,
            idempotencyKey,
            cancellationToken);
        if (begun.Status == DiscordClaimTransitionStatus.Missing || begun.Claim == null)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Missing,
                null,
                Error: "The Discord interest claim was not found.");
        }

        if (begun.Claim.CompanyId != access.CompanyId)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Rejected,
                null,
                Error: "The Discord interest claim belongs to another company.");
        }

        if (begun.Claim.State == DiscordInterestClaimState.Accepted)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Replayed,
                begun.Claim);
        }

        if (!begun.Success)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Conflict,
                begun.Claim,
                Error: begun.Error ?? "The Discord interest claim is no longer pending.");
        }

        var publication = await collaboration.LoadPublicationAsync(
            begun.Claim.PublicationId,
            cancellationToken);
        if (publication == null ||
            publication.CompanyId != access.CompanyId ||
            publication.OrderId != begun.Claim.OrderId ||
            publication.State != DiscordPublicationState.Open)
        {
            await ResetAsync(access, claimId, idempotencyKey, cancellationToken);
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Conflict,
                begun.Claim,
                Error: "The Discord publication is no longer open for assignment.");
        }

        var order = await orders.LoadOrderAsync(
            access,
            begun.Claim.OrderId,
            cancellationToken);
        var crafter = await orders.LoadCrafterAsync(
            access,
            selectedCrafterId,
            cancellationToken);
        if (order == null || crafter == null)
        {
            await ResetAsync(access, claimId, idempotencyKey, cancellationToken);
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Conflict,
                begun.Claim,
                Error: "The canonical Trade order or selected crafter is missing.");
        }

        if (order.Order.AssignedCrafterId == selectedCrafterId)
        {
            var repaired = await CompleteAsync(
                access,
                begun.Claim,
                idempotencyKey,
                selectedCrafterId,
                order.Envelope.RecordRevision,
                cancellationToken);
            if (!repaired.Success)
            {
                return new DiscordOperatorClaimResult(
                    DiscordOperatorClaimStatus.Rejected,
                    repaired.Claim ?? begun.Claim,
                    Error: repaired.Error ??
                        "The canonical assignment is present, but claim reconciliation failed.");
            }

            return new DiscordOperatorClaimResult(
                repaired.Status == DiscordClaimTransitionStatus.Replayed
                    ? DiscordOperatorClaimStatus.Replayed
                    : DiscordOperatorClaimStatus.Applied,
                repaired.Claim);
        }

        DiscordOrderAssignmentMutation assignment;
        try
        {
            assignment = await orders.AssignAsync(
                access,
                order,
                crafter,
                idempotencyKey,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await ResetAsync(access, claimId, idempotencyKey, cancellationToken);
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Conflict,
                begun.Claim,
                Error: exception.Message);
        }

        if (!assignment.Success)
        {
            await ResetAsync(access, claimId, idempotencyKey, cancellationToken);
            return new DiscordOperatorClaimResult(
                assignment.Conflict
                    ? DiscordOperatorClaimStatus.Conflict
                    : DiscordOperatorClaimStatus.Rejected,
                begun.Claim,
                assignment.Mutation,
                assignment.Mutation.ErrorMessage ?? "The canonical order assignment was rejected.");
        }

        var acceptedRevision = assignment.Mutation.Record?.RecordRevision;
        if (!acceptedRevision.HasValue)
        {
            var current = await orders.LoadOrderAsync(
                access,
                begun.Claim.OrderId,
                cancellationToken);
            if (current?.Order.AssignedCrafterId != selectedCrafterId)
            {
                return new DiscordOperatorClaimResult(
                    DiscordOperatorClaimStatus.Rejected,
                    begun.Claim,
                    assignment.Mutation,
                    "The canonical assignment succeeded without a verifiable order revision.");
            }

            acceptedRevision = current.Envelope.RecordRevision;
        }

        var completed = await CompleteAsync(
            access,
            begun.Claim,
            idempotencyKey,
            selectedCrafterId,
            acceptedRevision.Value,
            cancellationToken);
        if (!completed.Success)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Rejected,
                completed.Claim ?? begun.Claim,
                assignment.Mutation,
                completed.Error ??
                    "The canonical assignment succeeded, but claim reconciliation remains pending.");
        }

        return new DiscordOperatorClaimResult(
            completed.Status == DiscordClaimTransitionStatus.Replayed ||
            assignment.Mutation.Status == TradeCompanyMutationStatus.Replayed
                ? DiscordOperatorClaimStatus.Replayed
                : DiscordOperatorClaimStatus.Applied,
            completed.Claim,
            assignment.Mutation,
            completed.Error);
    }

    public async Task<DiscordOperatorClaimResult> DeclineAsync(
        TradeCompanyAccessContext access,
        Guid claimId,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(access);
        if (claimId == Guid.Empty)
        {
            throw new ArgumentException("Claim IDs cannot be empty.", nameof(claimId));
        }

        var pending = await collaboration.LoadPendingClaimsAsync(
            access.CompanyId,
            orderId: null,
            cancellationToken);
        var claim = pending.FirstOrDefault(candidate => candidate.ClaimId == claimId);
        if (claim == null)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Missing,
                null,
                Error: "The pending Discord interest claim was not found.");
        }

        if (claim.State != DiscordInterestClaimState.Pending)
        {
            return new DiscordOperatorClaimResult(
                DiscordOperatorClaimStatus.Conflict,
                claim,
                Error: "An assignment operation is already resolving this claim.");
        }

        await collaboration.DeclineClaimAsync(
            access.CompanyId,
            claimId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return new DiscordOperatorClaimResult(
            DiscordOperatorClaimStatus.Applied,
            claim with
            {
                State = DiscordInterestClaimState.Declined,
                ResolvedAt = timeProvider.GetUtcNow()
            });
    }

    private Task<DiscordClaimTransitionResult> CompleteAsync(
        TradeCompanyAccessContext access,
        DiscordInterestClaim claim,
        string idempotencyKey,
        Guid selectedCrafterId,
        CompanyRecordRevision acceptedRevision,
        CancellationToken cancellationToken) =>
        collaboration.CompleteClaimAcceptanceAsync(
            access.CompanyId,
            claim.ClaimId,
            idempotencyKey,
            selectedCrafterId,
            acceptedRevision,
            timeProvider.GetUtcNow(),
            cancellationToken);

    private Task ResetAsync(
        TradeCompanyAccessContext access,
        Guid claimId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        collaboration.ResetClaimAcceptanceAsync(
            access.CompanyId,
            claimId,
            idempotencyKey,
            cancellationToken);

    private static void ValidateAccess(TradeCompanyAccessContext access)
    {
        if (access.GrantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Canonical company access is required.");
        }
    }

    private static void RequireOperator(TradeCompanyAccessContext access)
    {
        ValidateAccess(access);
        if (access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
        {
            throw new UnauthorizedAccessException(
                "Resolving Discord interest requires a company operator.");
        }
    }
}
