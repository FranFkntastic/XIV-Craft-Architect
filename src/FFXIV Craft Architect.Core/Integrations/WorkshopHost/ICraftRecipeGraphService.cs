namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public interface ICraftRecipeGraphService
{
    Task<CraftRecipeGraphResponseV1> BuildAsync(
        CraftRecipeGraphRequestV1 request,
        CancellationToken cancellationToken = default);
}
