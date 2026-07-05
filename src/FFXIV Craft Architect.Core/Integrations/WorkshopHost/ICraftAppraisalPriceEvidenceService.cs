using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public interface ICraftAppraisalPriceEvidenceService
{
    Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
        CraftingPlan plan,
        CraftAppraisalRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CraftAppraisalPriceEvidenceResult(
    int MarketItemScopePairsRequested,
    int MarketItemsPriced,
    int VendorItemsPriced,
    IReadOnlyList<CraftAppraisalPriceEvidenceIssue> Issues)
{
    public static CraftAppraisalPriceEvidenceResult Empty { get; } = new(0, 0, 0, []);
}

public sealed record CraftAppraisalPriceEvidenceIssue(
    int ItemId,
    string ItemName,
    string Source,
    string Message);
