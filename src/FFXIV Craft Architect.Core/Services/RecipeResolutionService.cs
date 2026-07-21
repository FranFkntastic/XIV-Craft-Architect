using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record RecipeResolutionResult(
    RecipeOperationKind? Kind,
    RecipeResolutionConfidence Confidence,
    RecipeDataSourceKind DataSource,
    uint? RecipeId,
    int? JobId,
    string JobName,
    int RecipeLevel,
    int RecipeDisplayLevel,
    int RecipeStars,
    int? RecipeUnlockItemId,
    int Yield,
    int CraftCount,
    GarlandCraft? StandardRecipe,
    GarlandCompanyCraft? CompanyCraft,
    IReadOnlyList<RecipeOperationDiagnostic> Diagnostics)
{
    public bool IsResolved => Kind != null && RecipeId != null;

    public static RecipeResolutionResult Unresolved(
        PlanNode node,
        RecipeResolutionConfidence confidence,
        RecipeOperationDiagnostic diagnostic)
    {
        return new RecipeResolutionResult(
            null,
            confidence,
            RecipeDataSourceKind.None,
            null,
            null,
            node.Job,
            node.RecipeLevel,
            node.RecipeDisplayLevel,
            node.RecipeStars,
            node.RecipeUnlockItemId,
            Math.Max(1, node.Yield),
            node.CraftCount,
            null,
            null,
            [diagnostic]);
    }
}

public sealed class RecipeResolutionService : IRecipeResolutionService
{
    public RecipeResolutionResult Resolve(PlanNode node, GarlandItem? itemData)
    {
        if (IsCompanyCraftNode(node) && itemData?.CompanyCrafts?.Any() == true)
        {
            return ResolveCompanyCraft(node, itemData.CompanyCrafts.First());
        }

        if (itemData?.Crafts?.Any() == true)
        {
            return ResolveStandardCraft(node, itemData.Crafts);
        }

        if (itemData?.CompanyCrafts?.Any() == true)
        {
            return ResolveCompanyCraft(node, itemData.CompanyCrafts.First());
        }

        return RecipeResolutionResult.Unresolved(
            node,
            RecipeResolutionConfidence.Missing,
            CreateDiagnostic(
                node,
                RecipeOperationDiagnosticCode.NoRecipeData,
                RecipeOperationDiagnosticSeverity.Error,
                "No standard or company craft recipe data was available."));
    }

    private static RecipeResolutionResult ResolveStandardCraft(PlanNode node, IReadOnlyList<GarlandCraft> recipes)
    {
        var numericRecipes = recipes
            .Select(recipe => new ParsedRecipe(recipe, TryParseRecipeId(recipe, out var id) ? id : null))
            .Where(parsed => parsed.RecipeId.HasValue)
            .ToList();

        if (numericRecipes.Count == 0)
        {
            return RecipeResolutionResult.Unresolved(
                node,
                RecipeResolutionConfidence.NonNumericRecipeId,
                CreateDiagnostic(
                    node,
                    RecipeOperationDiagnosticCode.NonNumericRecipeId,
                    RecipeOperationDiagnosticSeverity.Error,
                    "Unable to resolve a numeric standard recipe ID for this craftable item."));
        }

        var exactMatches = numericRecipes
            .Where(parsed => parsed.Recipe.RecipeLevel == node.RecipeLevel)
            .Where(parsed => Math.Max(1, parsed.Recipe.Yield) == Math.Max(1, node.Yield))
            .Where(parsed => string.Equals(JobHelper.GetJobName(parsed.Recipe.JobId), node.Job, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            return CreateStandardResult(node, exactMatches[0], RecipeResolutionConfidence.Exact, []);
        }

        if (exactMatches.Count > 1)
        {
            var selected = exactMatches.OrderBy(parsed => parsed.RecipeId!.Value).First();
            return CreateStandardResult(
                node,
                selected,
                RecipeResolutionConfidence.AmbiguousExact,
                [
                    CreateDiagnostic(
                        node,
                        RecipeOperationDiagnosticCode.AmbiguousRecipe,
                        RecipeOperationDiagnosticSeverity.Warning,
                        "Multiple recipes matched this node by job, recipe level, and yield; using the lowest recipe ID.",
                        selected.RecipeId)
                ]);
        }

        var jobMatches = numericRecipes
            .Where(parsed => string.Equals(JobHelper.GetJobName(parsed.Recipe.JobId), node.Job, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (jobMatches.Count == 1)
        {
            return CreateStandardResult(
                node,
                jobMatches[0],
                RecipeResolutionConfidence.FallbackByJob,
                [
                    CreateLowConfidenceDiagnostic(
                        node,
                        "Recipe matched by job only; recipe level and yield did not match the plan node.",
                        jobMatches[0].RecipeId)
                ]);
        }

        var levelYieldMatches = numericRecipes
            .Where(parsed => parsed.Recipe.RecipeLevel == node.RecipeLevel)
            .Where(parsed => Math.Max(1, parsed.Recipe.Yield) == Math.Max(1, node.Yield))
            .ToList();
        if (levelYieldMatches.Count == 1)
        {
            return CreateStandardResult(
                node,
                levelYieldMatches[0],
                RecipeResolutionConfidence.FallbackByLevelYield,
                [
                    CreateLowConfidenceDiagnostic(
                        node,
                        "Recipe matched by recipe level and yield only; job did not match the plan node.",
                        levelYieldMatches[0].RecipeId)
                ]);
        }

        if (levelYieldMatches.Count > 1)
        {
            var selected = levelYieldMatches.OrderBy(parsed => parsed.RecipeId!.Value).First();
            return CreateStandardResult(
                node,
                selected,
                RecipeResolutionConfidence.FallbackByLevelYield,
                [
                    CreateDiagnostic(
                        node,
                        RecipeOperationDiagnosticCode.AmbiguousRecipe,
                        RecipeOperationDiagnosticSeverity.Warning,
                        "Multiple recipes matched this node by recipe level and yield, but the job did not disambiguate them.",
                        selected.RecipeId)
                ]);
        }

        var firstAvailable = numericRecipes
            .OrderBy(parsed => parsed.Recipe.RecipeLevel)
            .ThenBy(parsed => parsed.RecipeId!.Value)
            .First();
        return CreateStandardResult(
            node,
            firstAvailable,
            RecipeResolutionConfidence.FallbackFirstAvailable,
            [
                CreateLowConfidenceDiagnostic(
                    node,
                    "Recipe did not match the plan node by job, recipe level, or yield; using the lowest-level available recipe.",
                    firstAvailable.RecipeId)
            ]);
    }

    private static bool IsCompanyCraftNode(PlanNode node)
    {
        return string.Equals(node.Job, "Company Workshop", StringComparison.OrdinalIgnoreCase);
    }

    private static RecipeResolutionResult ResolveCompanyCraft(PlanNode node, GarlandCompanyCraft companyCraft)
    {
        return new RecipeResolutionResult(
            RecipeOperationKind.CompanyCraft,
            RecipeResolutionConfidence.Exact,
            RecipeDataSourceKind.GarlandCompanyCraft,
            (uint)companyCraft.Id,
            null,
            "Company Workshop",
            node.RecipeLevel,
            node.RecipeDisplayLevel,
            node.RecipeStars,
            node.RecipeUnlockItemId,
            Math.Max(1, node.Yield),
            node.Quantity,
            null,
            companyCraft,
            []);
    }

    private static RecipeResolutionResult CreateStandardResult(
        PlanNode node,
        ParsedRecipe parsedRecipe,
        RecipeResolutionConfidence confidence,
        IReadOnlyList<RecipeOperationDiagnostic> diagnostics)
    {
        var recipe = parsedRecipe.Recipe;
        var yield = Math.Max(1, recipe.Yield);
        var craftCount = (int)Math.Ceiling((double)node.Quantity / yield);
        return new RecipeResolutionResult(
            RecipeOperationKind.StandardCraft,
            confidence,
            RecipeDataSourceKind.GarlandStandardCraft,
            parsedRecipe.RecipeId!.Value,
            recipe.JobId,
            JobHelper.GetJobName(recipe.JobId),
            recipe.RecipeLevel,
            GetRecipeDisplayLevel(recipe),
            Math.Max(0, recipe.Stars),
            recipe.UnlockItemId,
            yield,
            craftCount,
            recipe,
            null,
            diagnostics);
    }

    private static bool TryParseRecipeId(GarlandCraft recipe, out uint recipeId)
    {
        return uint.TryParse(recipe.Id, out recipeId);
    }

    private static int GetRecipeDisplayLevel(GarlandCraft recipe)
    {
        return recipe.DisplayLevel > 0 ? recipe.DisplayLevel : recipe.RecipeLevel;
    }

    private static RecipeOperationDiagnostic CreateDiagnostic(
        PlanNode node,
        RecipeOperationDiagnosticCode code,
        RecipeOperationDiagnosticSeverity severity,
        string message,
        uint? recipeId = null)
    {
        return new RecipeOperationDiagnostic(
            node.NodeId,
            node.ItemId,
            node.Name,
            severity,
            message,
            code,
            recipeId,
            node.NodeId);
    }

    private static RecipeOperationDiagnostic CreateLowConfidenceDiagnostic(
        PlanNode node,
        string message,
        uint? recipeId)
    {
        return CreateDiagnostic(
            node,
            RecipeOperationDiagnosticCode.LowConfidenceRecipeResolution,
            RecipeOperationDiagnosticSeverity.Warning,
            message,
            recipeId);
    }

    private sealed record ParsedRecipe(GarlandCraft Recipe, uint? RecipeId);
}
