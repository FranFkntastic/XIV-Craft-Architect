using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordCompanyCommissionPostCommitSink(
    IServiceScopeFactory scopeFactory) : ICompanyCommissionPostCommitSink
{
    public async Task OnCommittedAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var publications = scope.ServiceProvider
            .GetRequiredService<DiscordPublicationService>();
        await publications.RefreshCommittedCommissionAsync(
            access,
            committed,
            cancellationToken);
    }
}
