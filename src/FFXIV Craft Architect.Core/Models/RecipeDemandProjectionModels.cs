namespace FFXIV_Craft_Architect.Core.Models;

public enum RecipeDemandViewKind
{
    PlanOccurrence,
    MarketAnalysisCandidate,
    ActiveProcurement,
    Suppressed
}

public enum RecipeDemandQuantityBasis
{
    PlanNodeQuantity,
    RecipeExpectedQuantity
}

public sealed record RecipeDemandProjection(
    IReadOnlyList<RecipeDemandRow> AllPlanDemand,
    IReadOnlyList<RecipeDemandRow> MarketAnalysisCandidates,
    IReadOnlyList<RecipeDemandRow> ActiveProcurementDemand,
    IReadOnlyList<RecipeDemandRow> SuppressedDemand)
{
    public IReadOnlyList<MaterialAggregate> ToMarketAnalysisMaterialAggregates()
    {
        return ToMaterialAggregates(MarketAnalysisCandidates, useCraftedSourceFlag: true);
    }

    public IReadOnlyList<MaterialAggregate> ToActiveProcurementMaterialAggregates()
    {
        return ToMaterialAggregates(ActiveProcurementDemand, useCraftedSourceFlag: false);
    }

    private static IReadOnlyList<MaterialAggregate> ToMaterialAggregates(
        IReadOnlyList<RecipeDemandRow> rows,
        bool useCraftedSourceFlag)
    {
        var aggregates = new Dictionary<int, MaterialAggregate>();
        foreach (var row in rows.Where(row => row.Quantity > 0))
        {
            if (!aggregates.TryGetValue(row.ItemId, out var aggregate))
            {
                aggregate = new MaterialAggregate
                {
                    ItemId = row.ItemId,
                    Name = row.ItemName,
                    IconId = row.IconId,
                    UnitPrice = row.UnitPrice,
                    RequiresHq = row.MustBeHq
                };
                aggregates[row.ItemId] = aggregate;
            }

            aggregate.TotalQuantity += row.Quantity;
            aggregate.UnitPrice = row.UnitPrice;
            aggregate.RequiresHq = aggregate.RequiresHq || row.MustBeHq;
            aggregate.Sources.Add(new MaterialSource
            {
                ParentItemName = row.ParentItemName ?? "Direct",
                Quantity = row.Quantity,
                IsCrafted = useCraftedSourceFlag && row.HasChildren
            });
        }

        return aggregates.Values.OrderBy(item => item.Name).ToList();
    }
}

public sealed record RecipeDemandRow(
    RecipeDemandViewKind ViewKind,
    string NodeId,
    int ItemId,
    string ItemName,
    int IconId,
    int Quantity,
    RecipeDemandQuantityBasis QuantityBasis,
    bool MustBeHq,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool HasChildren,
    bool CanBuyFromMarket,
    bool CanBuyFromVendor,
    decimal UnitPrice,
    string? ParentNodeId,
    string? ParentItemName,
    string? ParentOperationNodeId,
    uint? ParentRecipeId,
    string? OperationNodeId,
    uint? RecipeId,
    string? SuppressedByNodeId,
    int? SuppressedByItemId,
    string? SuppressedByItemName);
