using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface ILodestoneCrafterLookupService
{
    Task<LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>> SearchAsync(
        LodestoneCrafterSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>> GetImportPreviewAsync(
        string lodestoneCharacterId,
        CancellationToken cancellationToken = default);
}
