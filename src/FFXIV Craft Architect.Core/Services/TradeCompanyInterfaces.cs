using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public interface ITradeCompanyClient
{
    Task<TradeCompanyIdentity?> GetCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyChangeSet> GetChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITradeCompanyService
{
    Task<TradeCompanyIdentity?> GetCompanyAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyChangeSet> GetChangesAsync(
        TradeCompanyAccessContext access,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyAccessContext access,
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyPublicationOwnership?> ResolvePublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default);
}

public interface ITradeCompanyStore
{
    Task<TradeCompanyIdentity?> LoadCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyChangeSet> LoadChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyMutationResult> ApplyMutationAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<TradeCompanyPublicationOwnership?> LoadPublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default);
}
