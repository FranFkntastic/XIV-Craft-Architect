namespace FFXIV_Craft_Architect.Core.Models;

public sealed class StoredRecipeOperationSnapshot
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public StoredRecipeOperationMetadata Metadata { get; set; } = new();

    public List<StoredRecipeOperation> Operations { get; set; } = new();

    public List<StoredMarketAnalysisDemandItem> MarketAnalysisDemandItems { get; set; } = new();

    public HashSet<int> UnavailableMarketItemIds { get; set; } = new();
}

public sealed class StoredRecipeOperationMetadata
{
    public long PlanSessionVersion { get; set; }

    public long PlanStructureVersion { get; set; }

    public long PlanDecisionVersion { get; set; }

    public long PlanPriceVersion { get; set; }

    public long SettingsVersion { get; set; }

    public string RecipeDataIdentity { get; set; } = string.Empty;

    public DateTime CompletedAtUtc { get; set; }

    public int NodeCount { get; set; }

    public int UniqueItemIdCount { get; set; }

    public int DiagnosticCount { get; set; }
}

public sealed class StoredMarketAnalysisDemandItem
{
    public int ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int IconId { get; set; }

    public int TotalQuantity { get; set; }

    public bool RequiresHq { get; set; }
}

public sealed class StoredRecipeOperation
{
    public string NodeId { get; set; } = string.Empty;

    public string? ParentNodeId { get; set; }

    public List<string> AncestorNodeIds { get; set; } = new();

    public int Depth { get; set; }

    public int ResultItemId { get; set; }

    public string ResultItemName { get; set; } = string.Empty;

    public int RequestedQuantity { get; set; }

    public AcquisitionSource Source { get; set; }

    public AcquisitionSourceReason SourceReason { get; set; }

    public bool MustBeHq { get; set; }

    public bool CanCraft { get; set; }

    public RecipeOperationState State { get; set; }

    public string? SuppressedByNodeId { get; set; }

    public string? SuppressedByItemName { get; set; }

    public RecipeOperationKind? Kind { get; set; }

    public uint? RecipeId { get; set; }

    public int? JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public int RecipeLevel { get; set; }

    public int RecipeDisplayLevel { get; set; }

    public int? RecipeUnlockItemId { get; set; }

    public int Yield { get; set; }

    public int CraftCount { get; set; }

    public List<StoredRecipeOperationIngredient> Ingredients { get; set; } = new();

    public RecipeResolutionConfidence ResolutionConfidence { get; set; }

    public RecipeDataSourceKind RecipeDataSource { get; set; }

    public bool HasStructuralDiagnostics { get; set; }
}

public sealed class StoredRecipeOperationIngredient
{
    public int ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int AmountPerCraft { get; set; }

    public int TotalQuantity { get; set; }

    public string? ChildNodeId { get; set; }

    public AcquisitionSource? ChildSource { get; set; }

    public bool ChildCanCraft { get; set; }

    public RecipeIngredientLinkStatus LinkStatus { get; set; }

    public int ExpectedTotalQuantity { get; set; }

    public int? PlanChildQuantity { get; set; }
}
