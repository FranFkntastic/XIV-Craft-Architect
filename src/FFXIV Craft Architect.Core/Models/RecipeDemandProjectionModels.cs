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

public sealed record RecipeDemandRow
{
    public RecipeDemandRow(
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
        string? SuppressedByItemName)
        : this(
            viewKind: ViewKind,
            nodeId: NodeId,
            itemId: ItemId,
            itemName: ItemName,
            iconId: IconId,
            quantity: Quantity,
            quantityBasis: QuantityBasis,
            mustBeHq: MustBeHq,
            source: Source,
            sourceReason: SourceReason,
            hasChildren: HasChildren,
            canBuyFromMarket: CanBuyFromMarket,
            canBuyFromVendor: CanBuyFromVendor,
            unitPrice: UnitPrice,
            parentNodeId: ParentNodeId,
            parentItemName: ParentItemName,
            parentOperationNodeId: ParentOperationNodeId,
            parentRecipeId: ParentRecipeId,
            operationNodeId: OperationNodeId,
            recipeId: RecipeId,
            suppressedByNodeId: SuppressedByNodeId,
            suppressedByItemId: SuppressedByItemId,
            suppressedByItemName: SuppressedByItemName)
    {
    }

    public RecipeDemandRow(
        RecipeDemandViewKind viewKind,
        string nodeId,
        int itemId,
        string itemName,
        int iconId,
        int quantity,
        RecipeDemandQuantityBasis quantityBasis,
        bool mustBeHq,
        AcquisitionSource source,
        AcquisitionSourceReason sourceReason,
        bool hasChildren,
        bool canBuyFromMarket,
        bool canBuyFromVendor,
        decimal unitPrice,
        string? parentNodeId,
        string? parentItemName,
        string? parentOperationNodeId,
        uint? parentRecipeId,
        string? operationNodeId,
        uint? recipeId,
        string? suppressedByNodeId,
        int? suppressedByItemId,
        string? suppressedByItemName,
        bool canCraft = false,
        bool canBeHq = false,
        int yield = 1,
        decimal hqUnitPrice = 0,
        decimal vendorUnitPrice = 0,
        int selectedVendorIndex = -1,
        IReadOnlyList<RecipeDemandVendorOption>? vendorOptions = null,
        int parentOutputQuantity = 0)
    {
        ViewKind = viewKind;
        NodeId = nodeId;
        ItemId = itemId;
        ItemName = itemName;
        IconId = iconId;
        Quantity = quantity;
        QuantityBasis = quantityBasis;
        MustBeHq = mustBeHq;
        Source = source;
        SourceReason = sourceReason;
        HasChildren = hasChildren;
        CanBuyFromMarket = canBuyFromMarket;
        CanBuyFromVendor = canBuyFromVendor;
        UnitPrice = unitPrice;
        ParentNodeId = parentNodeId;
        ParentItemName = parentItemName;
        ParentOperationNodeId = parentOperationNodeId;
        ParentRecipeId = parentRecipeId;
        OperationNodeId = operationNodeId;
        RecipeId = recipeId;
        SuppressedByNodeId = suppressedByNodeId;
        SuppressedByItemId = suppressedByItemId;
        SuppressedByItemName = suppressedByItemName;
        CanCraft = canCraft;
        CanBeHq = canBeHq;
        Yield = yield;
        HqUnitPrice = hqUnitPrice;
        VendorUnitPrice = vendorUnitPrice;
        SelectedVendorIndex = selectedVendorIndex;
        VendorOptions = vendorOptions ?? Array.Empty<RecipeDemandVendorOption>();
        ParentOutputQuantity = parentOutputQuantity;
    }

    public RecipeDemandViewKind ViewKind { get; init; }
    public string NodeId { get; init; }
    public int ItemId { get; init; }
    public string ItemName { get; init; }
    public int IconId { get; init; }
    public int Quantity { get; init; }
    public RecipeDemandQuantityBasis QuantityBasis { get; init; }
    public bool MustBeHq { get; init; }
    public AcquisitionSource Source { get; init; }
    public AcquisitionSourceReason SourceReason { get; init; }
    public bool HasChildren { get; init; }
    public bool CanBuyFromMarket { get; init; }
    public bool CanBuyFromVendor { get; init; }
    public decimal UnitPrice { get; init; }
    public string? ParentNodeId { get; init; }
    public string? ParentItemName { get; init; }
    public string? ParentOperationNodeId { get; init; }
    public uint? ParentRecipeId { get; init; }
    public string? OperationNodeId { get; init; }
    public uint? RecipeId { get; init; }
    public string? SuppressedByNodeId { get; init; }
    public int? SuppressedByItemId { get; init; }
    public string? SuppressedByItemName { get; init; }
    public bool CanCraft { get; init; }
    public bool CanBeHq { get; init; }
    public int Yield { get; init; }
    public decimal HqUnitPrice { get; init; }
    public decimal VendorUnitPrice { get; init; }
    public int SelectedVendorIndex { get; init; }
    public IReadOnlyList<RecipeDemandVendorOption> VendorOptions { get; init; }
    public int ParentOutputQuantity { get; init; }

    public bool IsMarketBoardPurchase => Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq;
    public bool IsVendorPurchase => Source == AcquisitionSource.VendorBuy;
    public bool IsDirectPurchase => IsMarketBoardPurchase || IsVendorPurchase || Source == AcquisitionSource.UnknownSource;

    public void Deconstruct(
        out RecipeDemandViewKind viewKind,
        out string nodeId,
        out int itemId,
        out string itemName,
        out int iconId,
        out int quantity,
        out RecipeDemandQuantityBasis quantityBasis,
        out bool mustBeHq,
        out AcquisitionSource source,
        out AcquisitionSourceReason sourceReason,
        out bool hasChildren,
        out bool canBuyFromMarket,
        out bool canBuyFromVendor,
        out decimal unitPrice,
        out string? parentNodeId,
        out string? parentItemName,
        out string? parentOperationNodeId,
        out uint? parentRecipeId,
        out string? operationNodeId,
        out uint? recipeId,
        out string? suppressedByNodeId,
        out int? suppressedByItemId,
        out string? suppressedByItemName)
    {
        viewKind = ViewKind;
        nodeId = NodeId;
        itemId = ItemId;
        itemName = ItemName;
        iconId = IconId;
        quantity = Quantity;
        quantityBasis = QuantityBasis;
        mustBeHq = MustBeHq;
        source = Source;
        sourceReason = SourceReason;
        hasChildren = HasChildren;
        canBuyFromMarket = CanBuyFromMarket;
        canBuyFromVendor = CanBuyFromVendor;
        unitPrice = UnitPrice;
        parentNodeId = ParentNodeId;
        parentItemName = ParentItemName;
        parentOperationNodeId = ParentOperationNodeId;
        parentRecipeId = ParentRecipeId;
        operationNodeId = OperationNodeId;
        recipeId = RecipeId;
        suppressedByNodeId = SuppressedByNodeId;
        suppressedByItemId = SuppressedByItemId;
        suppressedByItemName = SuppressedByItemName;
    }

    public RecipeDemandVendorOption? SelectedVendor
    {
        get
        {
            if (SelectedVendorIndex >= 0 && SelectedVendorIndex < VendorOptions.Count)
            {
                return VendorOptions[SelectedVendorIndex];
            }

            return VendorOptions
                .Where(vendor => vendor.IsGilVendor)
                .OrderBy(vendor => vendor.Price)
                .FirstOrDefault();
        }
    }
}

public sealed record RecipeDemandVendorOption(
    string Name,
    string Location,
    decimal Price,
    string Currency)
{
    public bool IsGilVendor => string.Equals(Currency, "gil", StringComparison.OrdinalIgnoreCase);
}
