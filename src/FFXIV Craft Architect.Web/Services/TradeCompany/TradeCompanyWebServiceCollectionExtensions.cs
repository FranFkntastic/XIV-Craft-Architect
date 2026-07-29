using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public static class TradeCompanyWebServiceCollectionExtensions
{
    public static IServiceCollection AddTradeCompanyWebIntegration(this IServiceCollection services)
    {
        services.TryAddScoped<ITradeCompanyClient, UnavailableTradeCompanyClient>();
        services.AddScoped<TradeCompanyClientOrchestrator>();
        services.AddScoped<ITradeOrderLocalStore, TradeOperationsOrderLocalStore>();
        services.AddScoped<TradeOrderMutationService>();
        services.AddScoped<TradeCompanyCollaborationService>();
        return services;
    }
}

internal sealed class UnavailableTradeCompanyClient : ITradeCompanyClient
{
    public Task<TradeCompanyIdentity?> GetCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TradeCompanyIdentity?>(null);

    public Task<TradeCompanyChangeSet> GetChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TradeCompanyChangeSet(companyId, afterRevision, []));

    public Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TradeCompanyMutationResult>(
            new InvalidOperationException(
                "The Trade Company client has not been connected by the host."));
}
