using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed record CraftRecipeGraphRequestV1
{
    public const string CurrentSchemaVersion = "craft-architect-exact-recipe-graph-request/v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
}

public sealed record CraftRecipeGraphResponseV1
{
    public const string CurrentSchemaVersion = "craft-architect-exact-recipe-graph/v1";
    public const string ExactProviderId = "CraftArchitect";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProviderId { get; init; } = ExactProviderId;
    public string ProviderVersion { get; init; } = string.Empty;
    public string RecipeDataIdentity { get; init; } = string.Empty;
    public bool IsComplete { get; init; }
    public uint RootItemId { get; init; }
    public string RootItemName { get; init; } = string.Empty;
    public CraftRecipeGraphLimitsV1 Limits { get; init; } = CraftRecipeGraphLimitsV1.Default;
    public IReadOnlyList<CraftRecipeDefinitionV1> Recipes { get; init; } = [];
    public IReadOnlyList<uint> TerminalMaterialItemIds { get; init; } = [];
    public IReadOnlyList<CraftRecipeGraphDiagnosticV1> Diagnostics { get; init; } = [];
}

public sealed record CraftRecipeGraphLimitsV1
{
    public static CraftRecipeGraphLimitsV1 Default { get; } = new();

    public int MaximumDepth { get; init; } = 16;
    public int MaximumExpandedNodeCount { get; init; } = 1_024;
    public int MaximumRecipeDefinitionCount { get; init; } = 4_096;
    public int MaximumTerminalMaterialCount { get; init; } = 16_384;
    public int MaximumIngredientsPerRecipe { get; init; } = 32;
    public int MaximumTotalIngredientCount { get; init; } = 16_384;
    public int MaximumDiagnosticCount { get; init; } = 256;
}

public sealed record CraftRecipeDefinitionV1
{
    public uint RecipeId { get; init; }
    public uint OutputItemId { get; init; }
    public string OutputItemName { get; init; } = string.Empty;
    public uint OutputQuantity { get; init; }
    public uint RequiredClassJobId { get; init; }
    public string RequiredClassJobName { get; init; } = string.Empty;
    public int RequiredLevel { get; init; }
    public uint RecipeUnlockItemId { get; init; }
    public CraftRecipeUnlockEvidenceV1 UnlockEvidence { get; init; }
    public CraftRecipeResolutionConfidenceV1 ResolutionConfidence { get; init; }
    public CraftRecipeDataSourceV1 DataSource { get; init; }
    public IReadOnlyList<CraftRecipeIngredientV1> Ingredients { get; init; } = [];
    public IReadOnlyList<CraftRecipeGraphDiagnosticV1> StructuralDiagnostics { get; init; } = [];
    public bool HasStructuralDiagnostics => StructuralDiagnostics.Count > 0;
}

public sealed record CraftRecipeIngredientV1
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint QuantityPerCraft { get; init; }
}

public sealed record CraftRecipeGraphDiagnosticV1
{
    public string Code { get; init; } = string.Empty;
    public CraftRecipeGraphDiagnosticSeverityV1 Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public uint? ItemId { get; init; }
    public uint? RecipeId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CraftRecipeUnlockEvidenceV1
{
    Unknown,
    NoUnlockRequired,
    UnlockItemRequired,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CraftRecipeResolutionConfidenceV1
{
    Exact,
    Ambiguous,
    Fallback,
    Missing,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CraftRecipeDataSourceV1
{
    GarlandStandardCraft,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CraftRecipeGraphDiagnosticSeverityV1
{
    Info,
    Warning,
    Error,
}
