namespace FFXIV_Craft_Architect.Core.Models;

public enum RecipePlanAcquisitionQuoteStatus
{
    Actionable,
    Refreshing,
    Unavailable
}

public enum RecipePlanAcquisitionQuoteBasis
{
    CraftMaterials,
    Vendor,
    MarketAnalysis,
    ProcurementRoute
}

/// <summary>
/// An immutable, quantity-aware acquisition quote for one recipe-tree occurrence.
/// Market quotes are published only when selected listing evidence covers the demand.
/// </summary>
public sealed record RecipePlanAcquisitionQuote(
    string NodeId,
    int ItemId,
    int Quantity,
    AcquisitionSource Source,
    RecipePlanAcquisitionQuoteStatus Status,
    RecipePlanAcquisitionQuoteBasis Basis,
    decimal TotalCost,
    decimal EffectiveUnitCost,
    int CoveredQuantity,
    IReadOnlyList<string> Locations,
    DateTime? EvidencePublishedAtUtc,
    string Detail)
{
    public bool IsActionable => Status == RecipePlanAcquisitionQuoteStatus.Actionable && TotalCost > 0;
}
