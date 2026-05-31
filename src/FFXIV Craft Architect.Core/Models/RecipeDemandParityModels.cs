namespace FFXIV_Craft_Architect.Core.Models;

public enum RecipeDemandParityView
{
    MarketAnalysisCandidates,
    ActiveProcurement
}

public enum RecipeDemandParityField
{
    MissingItem,
    ExtraItem,
    TotalQuantity,
    RequiresHq,
    SourceCount,
    SourceQuantity,
    SourceParent,
    SourceCraftedFlag
}

public sealed record RecipeDemandParityReport(
    IReadOnlyList<RecipeDemandParityMismatch> Mismatches)
{
    public bool Matches => Mismatches.Count == 0;
}

public sealed record RecipeDemandParityMismatch(
    RecipeDemandParityView View,
    RecipeDemandParityField Field,
    int ItemId,
    string ItemName,
    string Expected,
    string Actual);
