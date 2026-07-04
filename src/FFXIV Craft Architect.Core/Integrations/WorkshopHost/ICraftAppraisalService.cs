namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public interface ICraftAppraisalService
{
    Task<CraftAppraisalQuote> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken = default);
}
