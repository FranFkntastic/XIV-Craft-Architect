using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed class CraftRecipeGraphService : ICraftRecipeGraphService
{
    private static readonly CraftRecipeGraphLimitsV1 Limits = CraftRecipeGraphLimitsV1.Default;
    private static readonly HashSet<RecipeOperationDiagnosticCode> StructuralDiagnosticCodes =
    [
        RecipeOperationDiagnosticCode.DuplicateNodeId,
        RecipeOperationDiagnosticCode.MissingParentLink,
        RecipeOperationDiagnosticCode.ParentLinkMismatch,
        RecipeOperationDiagnosticCode.IngredientChildMissing,
        RecipeOperationDiagnosticCode.IngredientChildQuantityMismatch,
        RecipeOperationDiagnosticCode.ExtraChildNotInRecipe,
        RecipeOperationDiagnosticCode.DuplicateIngredientChildMatch,
    ];
    private readonly ICoreRecipePlanBuilder planBuilder;
    private readonly IRecipeOperationSnapshotService snapshotService;
    private readonly string providerVersion;

    public CraftRecipeGraphService(
        ICoreRecipePlanBuilder planBuilder,
        IRecipeOperationSnapshotService snapshotService,
        string? providerVersion = null)
    {
        this.planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        this.providerVersion = providerVersion ?? ResolveProviderVersion();
        if (string.IsNullOrWhiteSpace(this.providerVersion))
        {
            throw new ArgumentException("Provider version must be non-empty.", nameof(providerVersion));
        }
    }

    public async Task<CraftRecipeGraphResponseV1> BuildAsync(
        CraftRecipeGraphRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SchemaVersion, CraftRecipeGraphRequestV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exact recipe graph request schema version is unsupported.", nameof(request));
        }

        if (request.ItemId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Item ID must be greater than zero.");
        }

        if (request.ItemId > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Item ID exceeds the Craft Architect planner range.");
        }

        var plan = await planBuilder.BuildPlanAsync(
            [(checked((int)request.ItemId), request.ItemName, 1, false)],
            string.Empty,
            string.Empty,
            cancellationToken);
        var nodes = EnumerateNodes(plan).Take(Limits.MaximumExpandedNodeCount + 1).ToList();
        var root = plan.RootItems.Count == 1 ? plan.RootItems[0] : null;
        var diagnostics = new List<CraftRecipeGraphDiagnosticV1>();

        if (root == null || root.ItemId != request.ItemId)
        {
            AddDiagnostic(diagnostics, "InvalidRoot", "The planner did not return exactly one matching root item.", request.ItemId);
        }

        var nodeLimitExceeded = nodes.Count > Limits.MaximumExpandedNodeCount;
        if (nodeLimitExceeded)
        {
            AddDiagnostic(diagnostics, "ExpandedNodeLimitExceeded", $"The plan exceeds the {Limits.MaximumExpandedNodeCount} node limit.", request.ItemId);
            nodes = nodes.Take(Limits.MaximumExpandedNodeCount).ToList();
        }

        var depthLimitExceeded = nodes.Any(node => node.Depth > Limits.MaximumDepth);
        if (depthLimitExceeded)
        {
            AddDiagnostic(diagnostics, "DepthLimitExceeded", $"The plan exceeds the {Limits.MaximumDepth} level depth limit.", request.ItemId);
        }

        var hasCircularReference = nodes.Any(node => node.Node.IsCircularReference);
        if (hasCircularReference)
        {
            AddDiagnostic(diagnostics, "CircularRecipeReference", "The expanded plan contains a circular recipe reference.", request.ItemId);
        }

        if (root == null || nodeLimitExceeded || depthLimitExceeded || hasCircularReference)
        {
            return CreateResponse(request, root, [], [], diagnostics);
        }

        var snapshot = await snapshotService.BuildAsync(
            plan,
            new RecipeOperationSnapshotIdentity(0, 0, 0, 0, 0, "garland-current"),
            ct: cancellationToken);
        var nodeById = nodes
            .GroupBy(entry => entry.Node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Node, StringComparer.Ordinal);

        foreach (var diagnostic in snapshot.Diagnostics)
        {
            diagnostics.Add(MapDiagnostic(diagnostic));
        }
        var structuralDiagnosticsByNodeId = snapshot.Diagnostics
            .Where(diagnostic => StructuralDiagnosticCodes.Contains(diagnostic.Code))
            .GroupBy(diagnostic => diagnostic.OperationNodeId ?? diagnostic.NodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CraftRecipeGraphDiagnosticV1>)group.Select(MapDiagnostic).ToList(),
                StringComparer.Ordinal);

        foreach (var operation in snapshot.Operations.Where(operation => operation.Kind == null || operation.RecipeId == null))
        {
            AddDiagnostic(
                diagnostics,
                "UnresolvedRecipe",
                $"No exact standard recipe was resolved for {operation.ResultItemName}.",
                checked((uint)operation.ResultItemId));
        }

        var recipeGroups = snapshot.Operations
            .Where(operation => operation.Kind != null && operation.RecipeId != null)
            .GroupBy(operation => operation.RecipeId!.Value)
            .Take(Limits.MaximumRecipeDefinitionCount + 1)
            .ToList();
        var recipes = new List<CraftRecipeDefinitionV1>();
        foreach (var group in recipeGroups)
        {
            try
            {
                var recipe = MapRecipe(group, nodeById, structuralDiagnosticsByNodeId, diagnostics);
                recipes.Add(recipe);
            }
            catch (OverflowException)
            {
                AddDiagnostic(diagnostics, "RecipeValueOutOfRange", $"Recipe {group.Key} contains a value outside the public graph contract range.", recipeId: group.Key);
            }
        }

        recipes = recipes
            .OrderBy(recipe => recipe.OutputItemId)
            .ThenBy(recipe => recipe.RecipeId)
            .ToList();

        if (recipes.Count > Limits.MaximumRecipeDefinitionCount)
        {
            AddDiagnostic(diagnostics, "RecipeDefinitionLimitExceeded", $"The graph exceeds the {Limits.MaximumRecipeDefinitionCount} recipe limit.", request.ItemId);
            recipes = recipes.Take(Limits.MaximumRecipeDefinitionCount).ToList();
        }

        var totalIngredientCount = recipes.Sum(recipe => recipe.Ingredients.Count);
        if (totalIngredientCount > Limits.MaximumTotalIngredientCount)
        {
            AddDiagnostic(diagnostics, "TotalIngredientLimitExceeded", $"The graph exceeds the {Limits.MaximumTotalIngredientCount} total ingredient limit.", request.ItemId);
            recipes = [];
        }

        var outputItemIds = recipes.Select(recipe => recipe.OutputItemId).ToHashSet();
        var terminalMaterialItemIds = recipes
            .SelectMany(recipe => recipe.Ingredients)
            .Select(ingredient => ingredient.ItemId)
            .Where(itemId => !outputItemIds.Contains(itemId))
            .Distinct()
            .Order()
            .Take(Limits.MaximumTerminalMaterialCount + 1)
            .ToList();
        if (terminalMaterialItemIds.Count > Limits.MaximumTerminalMaterialCount)
        {
            AddDiagnostic(diagnostics, "TerminalMaterialLimitExceeded", $"The graph exceeds the {Limits.MaximumTerminalMaterialCount} terminal material limit.", request.ItemId);
            terminalMaterialItemIds = terminalMaterialItemIds.Take(Limits.MaximumTerminalMaterialCount).ToList();
        }

        if (!recipes.Any(recipe => recipe.OutputItemId == request.ItemId))
        {
            AddDiagnostic(diagnostics, "MissingRootRecipe", "The response has no resolved standard recipe for the requested root item.", request.ItemId);
        }

        var uncappedDiagnosticCount = diagnostics.Count;
        if (uncappedDiagnosticCount > Limits.MaximumDiagnosticCount)
        {
            diagnostics = diagnostics.Take(Limits.MaximumDiagnosticCount - 1).ToList();
            AddDiagnostic(diagnostics, "DiagnosticLimitExceeded", $"Diagnostics were truncated at {Limits.MaximumDiagnosticCount} entries.", request.ItemId);
        }

        var isComplete = uncappedDiagnosticCount == 0 &&
            recipes.Count > 0 &&
            recipes.All(IsExactRecipe) &&
            totalIngredientCount <= Limits.MaximumTotalIngredientCount &&
            nodes.Count <= Limits.MaximumExpandedNodeCount;
        return CreateResponse(request, root, recipes, terminalMaterialItemIds, diagnostics, isComplete);
    }

    private static CraftRecipeDefinitionV1 MapRecipe(
        IGrouping<uint, RecipeOperation> operations,
        IReadOnlyDictionary<string, PlanNode> nodeById,
        IReadOnlyDictionary<string, IReadOnlyList<CraftRecipeGraphDiagnosticV1>> structuralDiagnosticsByNodeId,
        List<CraftRecipeGraphDiagnosticV1> responseDiagnostics)
    {
        var operation = operations.OrderBy(value => value.NodeId, StringComparer.Ordinal).First();
        var structuralDiagnostics = operations
            .SelectMany(value => structuralDiagnosticsByNodeId.GetValueOrDefault(
                value.NodeId,
                Array.Empty<CraftRecipeGraphDiagnosticV1>()))
            .Take(Limits.MaximumDiagnosticCount)
            .ToList();
        var sourceIngredients = operation.Ingredients.ToList();
        var hasInvalidSourceIngredient = sourceIngredients.Any(ingredient =>
            ingredient.ItemId <= 0 ||
            ingredient.AmountPerCraft <= 0 ||
            string.IsNullOrWhiteSpace(ingredient.Name));
        var ingredients = sourceIngredients
            .Where(ingredient => ingredient.ItemId > 0 &&
                ingredient.AmountPerCraft > 0 &&
                !string.IsNullOrWhiteSpace(ingredient.Name))
            .Take(Limits.MaximumIngredientsPerRecipe)
            .Select(ingredient => new CraftRecipeIngredientV1
            {
                ItemId = checked((uint)ingredient.ItemId),
                ItemName = ingredient.Name,
                QuantityPerCraft = checked((uint)ingredient.AmountPerCraft),
            })
            .ToList();

        if (operations.Any(value => !Equivalent(operation, value)))
        {
            AddDiagnostic(responseDiagnostics, "InconsistentRecipeDefinition", $"Recipe {operation.RecipeId} resolved to inconsistent definitions.", checked((uint)operation.ResultItemId), operation.RecipeId);
        }

        if (sourceIngredients.Count is 0 ||
            hasInvalidSourceIngredient ||
            sourceIngredients.Count > Limits.MaximumIngredientsPerRecipe ||
            ingredients.Any(ingredient => ingredient.ItemId == 0 ||
                ingredient.QuantityPerCraft == 0 ||
                string.IsNullOrWhiteSpace(ingredient.ItemName)) ||
            ingredients.Select(ingredient => ingredient.ItemId).Distinct().Count() != ingredients.Count)
        {
            AddDiagnostic(responseDiagnostics, "InvalidIngredientCollection", $"Recipe {operation.RecipeId} has an invalid bounded ingredient collection.", checked((uint)operation.ResultItemId), operation.RecipeId);
        }

        if (!nodeById.ContainsKey(operation.NodeId))
        {
            AddDiagnostic(responseDiagnostics, "MissingPlanNode", $"Recipe {operation.RecipeId} has no matching expanded plan node.", checked((uint)operation.ResultItemId), operation.RecipeId);
        }

        var unlockEvidence = operation.RecipeUnlockItemId switch
        {
            null => CraftRecipeUnlockEvidenceV1.Unknown,
            0 => CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
            > 0 => CraftRecipeUnlockEvidenceV1.UnlockItemRequired,
            _ => CraftRecipeUnlockEvidenceV1.Unknown,
        };
        if (unlockEvidence == CraftRecipeUnlockEvidenceV1.Unknown)
        {
            AddDiagnostic(responseDiagnostics, "UnknownRecipeUnlockEvidence", $"Recipe {operation.RecipeId} has no numeric master/unlock evidence.", checked((uint)operation.ResultItemId), operation.RecipeId);
        }

        var recipe = new CraftRecipeDefinitionV1
        {
            RecipeId = operation.RecipeId!.Value,
            OutputItemId = checked((uint)operation.ResultItemId),
            OutputItemName = operation.ResultItemName,
            OutputQuantity = checked((uint)operation.Yield),
            RequiredClassJobId = operation.JobId is > 0 ? checked((uint)operation.JobId.Value) : 0,
            RequiredClassJobName = operation.JobName,
            RequiredLevel = operation.RecipeDisplayLevel,
            RecipeUnlockItemId = operation.RecipeUnlockItemId is > 0 ? checked((uint)operation.RecipeUnlockItemId.Value) : 0,
            UnlockEvidence = unlockEvidence,
            ResolutionConfidence = MapConfidence(operation.ResolutionConfidence),
            DataSource = operation.RecipeDataSource == RecipeDataSourceKind.GarlandStandardCraft
                ? CraftRecipeDataSourceV1.GarlandStandardCraft
                : CraftRecipeDataSourceV1.Other,
            Ingredients = ingredients,
            StructuralDiagnostics = structuralDiagnostics,
        };

        if (recipe.RecipeId == 0 || recipe.OutputItemId == 0 || string.IsNullOrWhiteSpace(recipe.OutputItemName) ||
            recipe.OutputQuantity == 0 || recipe.RequiredClassJobId == 0 || string.IsNullOrWhiteSpace(recipe.RequiredClassJobName) ||
            recipe.RequiredLevel is < 1 or > 100)
        {
            AddDiagnostic(responseDiagnostics, "IncompleteRecipeFacts", $"Recipe {operation.RecipeId} is missing output or crafter eligibility facts.", recipe.OutputItemId, recipe.RecipeId);
        }

        return recipe;
    }

    private static bool Equivalent(RecipeOperation left, RecipeOperation right)
    {
        return left.ResultItemId == right.ResultItemId &&
            left.JobId == right.JobId &&
            left.RecipeDisplayLevel == right.RecipeDisplayLevel &&
            left.RecipeUnlockItemId == right.RecipeUnlockItemId &&
            left.Yield == right.Yield &&
            left.ResolutionConfidence == right.ResolutionConfidence &&
            left.RecipeDataSource == right.RecipeDataSource &&
            left.Ingredients.Select(IngredientIdentity).SequenceEqual(right.Ingredients.Select(IngredientIdentity));
    }

    private static string IngredientIdentity(RecipeOperationIngredient ingredient) =>
        string.Create(CultureInfo.InvariantCulture, $"{ingredient.ItemId}:{ingredient.AmountPerCraft}:{ingredient.Name}");

    private static bool IsExactRecipe(CraftRecipeDefinitionV1 recipe) =>
        recipe.ResolutionConfidence == CraftRecipeResolutionConfidenceV1.Exact &&
        recipe.DataSource == CraftRecipeDataSourceV1.GarlandStandardCraft &&
        recipe.UnlockEvidence != CraftRecipeUnlockEvidenceV1.Unknown &&
        recipe.StructuralDiagnostics.Count == 0;

    private static CraftRecipeResolutionConfidenceV1 MapConfidence(RecipeResolutionConfidence confidence) => confidence switch
    {
        RecipeResolutionConfidence.Exact => CraftRecipeResolutionConfidenceV1.Exact,
        RecipeResolutionConfidence.AmbiguousExact => CraftRecipeResolutionConfidenceV1.Ambiguous,
        RecipeResolutionConfidence.FallbackByJob or
        RecipeResolutionConfidence.FallbackByLevelYield or
        RecipeResolutionConfidence.FallbackFirstAvailable => CraftRecipeResolutionConfidenceV1.Fallback,
        _ => CraftRecipeResolutionConfidenceV1.Missing,
    };

    private static CraftRecipeGraphDiagnosticV1 MapDiagnostic(RecipeOperationDiagnostic diagnostic) => new()
    {
        Code = diagnostic.Code.ToString(),
        Severity = diagnostic.Severity switch
        {
            RecipeOperationDiagnosticSeverity.Info => CraftRecipeGraphDiagnosticSeverityV1.Info,
            RecipeOperationDiagnosticSeverity.Warning => CraftRecipeGraphDiagnosticSeverityV1.Warning,
            _ => CraftRecipeGraphDiagnosticSeverityV1.Error,
        },
        Message = diagnostic.Message,
        ItemId = diagnostic.ItemId > 0 ? checked((uint)diagnostic.ItemId) : null,
        RecipeId = diagnostic.RecipeId,
    };

    private static IEnumerable<(PlanNode Node, int Depth)> EnumerateNodes(CraftingPlan plan)
    {
        var pending = new Stack<(PlanNode Node, int Depth)>(
            plan.RootItems.AsEnumerable().Reverse().Select(node => (node, 0)));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            for (var index = current.Node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push((current.Node.Children[index], current.Depth + 1));
            }
        }
    }

    private static void AddDiagnostic(
        List<CraftRecipeGraphDiagnosticV1> diagnostics,
        string code,
        string message,
        uint? itemId = null,
        uint? recipeId = null)
    {
        diagnostics.Add(new CraftRecipeGraphDiagnosticV1
        {
            Code = code,
            Severity = CraftRecipeGraphDiagnosticSeverityV1.Error,
            Message = message,
            ItemId = itemId,
            RecipeId = recipeId,
        });
    }

    private CraftRecipeGraphResponseV1 CreateResponse(
        CraftRecipeGraphRequestV1 request,
        PlanNode? root,
        IReadOnlyList<CraftRecipeDefinitionV1> recipes,
        IReadOnlyList<uint> terminalMaterialItemIds,
        IReadOnlyList<CraftRecipeGraphDiagnosticV1> diagnostics,
        bool isComplete = false) => new()
    {
        ProviderVersion = providerVersion,
        RecipeDataIdentity = ComputeRecipeDataIdentity(recipes, terminalMaterialItemIds),
        IsComplete = isComplete,
        RootItemId = request.ItemId,
        RootItemName = root?.Name ?? request.ItemName,
        Limits = Limits,
        Recipes = recipes,
        TerminalMaterialItemIds = terminalMaterialItemIds,
        Diagnostics = diagnostics.Take(Limits.MaximumDiagnosticCount).ToList(),
    };

    private static string ComputeRecipeDataIdentity(
        IReadOnlyList<CraftRecipeDefinitionV1> recipes,
        IReadOnlyList<uint> terminalMaterialItemIds)
    {
        var canonical = new StringBuilder("craft-architect-recipe-data/v1");
        foreach (var recipe in recipes.OrderBy(value => value.RecipeId))
        {
            canonical.Append('|').Append(recipe.RecipeId)
                .Append('|').Append(recipe.OutputItemId)
                .Append('|').Append(recipe.OutputQuantity)
                .Append('|').Append(recipe.RequiredClassJobId)
                .Append('|').Append(recipe.RequiredLevel)
                .Append('|').Append(recipe.RecipeUnlockItemId)
                .Append('|').Append(recipe.UnlockEvidence)
                .Append('|').Append(recipe.ResolutionConfidence)
                .Append('|').Append(recipe.DataSource);
            AppendString(canonical, recipe.OutputItemName);
            AppendString(canonical, recipe.RequiredClassJobName);
            foreach (var ingredient in recipe.Ingredients.OrderBy(value => value.ItemId))
            {
                canonical.Append('|').Append(ingredient.ItemId).Append('|').Append(ingredient.QuantityPerCraft);
                AppendString(canonical, ingredient.ItemName);
            }
        }

        foreach (var terminalItemId in terminalMaterialItemIds.Order())
        {
            canonical.Append("|terminal|").Append(terminalItemId);
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('|').Append(value.Length).Append(':').Append(value);
    }

    private static string ResolveProviderVersion()
    {
        var assembly = typeof(CraftRecipeGraphService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
