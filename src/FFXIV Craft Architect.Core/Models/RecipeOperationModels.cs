namespace FFXIV_Craft_Architect.Core.Models;

public enum RecipeOperationKind
{
    StandardCraft,
    CompanyCraft
}

public enum RecipeOperationState
{
    Active,
    InactiveBySource,
    SuppressedByAncestor,
    Unresolved
}

public enum RecipeOperationDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum RecipeResolutionConfidence
{
    None,
    Exact,
    AmbiguousExact,
    FallbackByJob,
    FallbackByLevelYield,
    FallbackFirstAvailable,
    Missing,
    NonNumericRecipeId
}

public enum RecipeDataSourceKind
{
    None,
    GarlandStandardCraft,
    GarlandCompanyCraft
}

public enum RecipeOperationDiagnosticCode
{
    RecipeDataUnavailable,
    NoRecipeData,
    MissingRecipe,
    NonNumericRecipeId,
    AmbiguousRecipe,
    LowConfidenceRecipeResolution,
    DuplicateNodeId,
    MissingParentLink,
    ParentLinkMismatch,
    IngredientChildMissing,
    IngredientChildQuantityMismatch,
    ExtraChildNotInRecipe,
    DuplicateIngredientChildMatch
}

public enum RecipeIngredientLinkStatus
{
    NotLinked,
    Matched,
    MissingPlanChild,
    QuantityMismatch,
    ExtraPlanChild
}

public sealed record RecipeOperationSnapshotIdentity(
    long PlanSessionVersion,
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long SettingsVersion,
    string RecipeDataIdentity)
{
    public static RecipeOperationSnapshotIdentity Unspecified { get; } = new(0, 0, 0, 0, 0, "unspecified");
}

public sealed record RecipeOperationSnapshotBuildOptions(
    string Mode = "default")
{
    public static RecipeOperationSnapshotBuildOptions Default { get; } = new();
}

public sealed record RecipeOperationSnapshotMetadata(
    RecipeOperationSnapshotIdentity Identity,
    RecipeOperationSnapshotBuildOptions BuildOptions,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    TimeSpan Duration,
    int NodeCount,
    int UniqueItemIdCount,
    int RecipeDataCalls,
    int RecipeDataCacheHits,
    int DiagnosticCount)
{
    public static RecipeOperationSnapshotMetadata Empty { get; } = new(
        RecipeOperationSnapshotIdentity.Unspecified,
        RecipeOperationSnapshotBuildOptions.Default,
        DateTime.UnixEpoch,
        DateTime.UnixEpoch,
        TimeSpan.Zero,
        0,
        0,
        0,
        0,
        0);
}

public sealed record RecipeOperationSnapshot
{
    public RecipeOperationSnapshot(
        IReadOnlyList<RecipeOperation> operations,
        IReadOnlyDictionary<string, RecipeOperation> operationsByNodeId,
        IReadOnlyDictionary<int, IReadOnlyList<RecipeOperation>> operationsByItemId,
        IReadOnlyList<RecipeOperationDiagnostic> diagnostics,
        bool isNodeIndexComplete = true,
        RecipeOperationSnapshotMetadata? metadata = null)
    {
        Operations = operations;
        OperationsByNodeId = operationsByNodeId;
        OperationsByItemId = operationsByItemId;
        Diagnostics = diagnostics;
        IsNodeIndexComplete = isNodeIndexComplete;
        Metadata = metadata ?? RecipeOperationSnapshotMetadata.Empty;
    }

    public IReadOnlyList<RecipeOperation> Operations { get; init; }

    public IReadOnlyDictionary<string, RecipeOperation> OperationsByNodeId { get; init; }

    public IReadOnlyDictionary<int, IReadOnlyList<RecipeOperation>> OperationsByItemId { get; init; }

    public IReadOnlyList<RecipeOperationDiagnostic> Diagnostics { get; init; }

    public bool IsNodeIndexComplete { get; init; }

    public RecipeOperationSnapshotMetadata Metadata { get; init; }

    public static RecipeOperationSnapshot Empty { get; } = new(
        Array.Empty<RecipeOperation>(),
        new Dictionary<string, RecipeOperation>(),
        new Dictionary<int, IReadOnlyList<RecipeOperation>>(),
        Array.Empty<RecipeOperationDiagnostic>(),
        true,
        RecipeOperationSnapshotMetadata.Empty);

    public IEnumerable<RecipeOperation> GetActiveOperations()
    {
        return Operations.Where(operation => operation.State == RecipeOperationState.Active);
    }

    public IEnumerable<RecipeOperation> GetRootOperations()
    {
        return Operations.Where(operation => operation.IsRoot);
    }

    public IEnumerable<RecipeOperation> GetRequiredCrafts()
    {
        return Operations.Where(operation =>
            operation.State == RecipeOperationState.Active &&
            operation.Kind != null);
    }

    public IEnumerable<RecipeOperation> GetCraftableReferences()
    {
        return Operations.Where(operation => operation.IsCraftableReference);
    }

    public IEnumerable<RecipeOperation> GetSuppressedCraftReferences()
    {
        return Operations.Where(operation => operation.State == RecipeOperationState.SuppressedByAncestor);
    }

    public IEnumerable<RecipeOperation> GetUnresolvedRequiredCrafts()
    {
        return Operations.Where(operation =>
            operation.State == RecipeOperationState.Unresolved &&
            operation.Source == AcquisitionSource.Craft &&
            operation.SuppressedByNodeId == null);
    }

    public IEnumerable<RecipeOperation> GetOperationsByRecipe(uint recipeId)
    {
        return Operations.Where(operation => operation.RecipeId == recipeId);
    }

    public IEnumerable<RecipeOperationIngredientEdge> GetIngredientEdges()
    {
        return Operations.SelectMany(operation =>
            operation.Ingredients.Select(ingredient => new RecipeOperationIngredientEdge(operation, ingredient)));
    }

    public IEnumerable<RecipeOperation> GetOperationsForNode(string nodeId)
    {
        if (IsNodeIndexComplete && OperationsByNodeId.TryGetValue(nodeId, out var operation))
        {
            return [operation];
        }

        return Operations.Where(operation => string.Equals(operation.NodeId, nodeId, StringComparison.Ordinal));
    }

    public IEnumerable<RecipeOperation> GetArtisanExportOperations(bool includePrecrafts)
    {
        return includePrecrafts
            ? GetRootOperations().Concat(GetRequiredCrafts().Where(operation => !operation.IsRoot)).Distinct()
            : GetRootOperations();
    }
}

public sealed record RecipeOperationIngredientEdge(
    RecipeOperation Operation,
    RecipeOperationIngredient Ingredient);

public sealed record RecipeOperation(
    string NodeId,
    string? ParentNodeId,
    IReadOnlyList<string> AncestorNodeIds,
    int Depth,
    int ResultItemId,
    string ResultItemName,
    int RequestedQuantity,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool CanCraft,
    RecipeOperationState State,
    string? SuppressedByNodeId,
    string? SuppressedByItemName,
    RecipeOperationKind? Kind,
    uint? RecipeId,
    int? JobId,
    string JobName,
    int RecipeLevel,
    int Yield,
    int CraftCount,
    IReadOnlyList<RecipeOperationIngredient> Ingredients,
    RecipeResolutionConfidence ResolutionConfidence = RecipeResolutionConfidence.None,
    RecipeDataSourceKind RecipeDataSource = RecipeDataSourceKind.None,
    bool HasStructuralDiagnostics = false)
{
    public bool IsRoot => ParentNodeId == null;

    public bool IsCraftableReference => State is RecipeOperationState.InactiveBySource or RecipeOperationState.SuppressedByAncestor;
}

public sealed record RecipeOperationIngredient(
    int ItemId,
    string Name,
    int AmountPerCraft,
    int TotalQuantity,
    string? ChildNodeId,
    AcquisitionSource? ChildSource,
    bool ChildCanCraft,
    RecipeIngredientLinkStatus LinkStatus = RecipeIngredientLinkStatus.NotLinked,
    int ExpectedTotalQuantity = 0,
    int? PlanChildQuantity = null);

public sealed record RecipeOperationDiagnostic(
    string NodeId,
    int ItemId,
    string ItemName,
    RecipeOperationDiagnosticSeverity Severity,
    string Message,
    RecipeOperationDiagnosticCode Code = RecipeOperationDiagnosticCode.MissingRecipe,
    uint? RecipeId = null,
    string? OperationNodeId = null,
    IReadOnlyDictionary<string, string>? Details = null);
