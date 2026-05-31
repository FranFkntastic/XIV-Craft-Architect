using System.Diagnostics;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class RecipeOperationSnapshotService : IRecipeOperationSnapshotService
{
    private readonly GarlandService _garlandService;
    private readonly IRecipeResolutionService _recipeResolutionService;
    private readonly ILogger<RecipeOperationSnapshotService> _logger;

    public RecipeOperationSnapshotService(
        GarlandService garlandService,
        IRecipeResolutionService recipeResolutionService,
        ILogger<RecipeOperationSnapshotService> logger)
    {
        _garlandService = garlandService;
        _recipeResolutionService = recipeResolutionService;
        _logger = logger;
    }

    public async Task<RecipeOperationSnapshot> BuildAsync(CraftingPlan? plan, CancellationToken ct = default)
    {
        return await BuildAsync(
            plan,
            RecipeOperationSnapshotIdentity.Unspecified,
            RecipeOperationSnapshotBuildOptions.Default,
            ct);
    }

    public async Task<RecipeOperationSnapshot> BuildAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var buildOptions = options ?? RecipeOperationSnapshotBuildOptions.Default;
        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        if (plan == null || plan.RootItems.Count == 0)
        {
            return RecipeOperationSnapshot.Empty with
            {
                Metadata = RecipeOperationSnapshotMetadata.Empty with
                {
                    Identity = identity,
                    BuildOptions = buildOptions,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = DateTime.UtcNow,
                    Duration = stopwatch.Elapsed
                }
            };
        }

        var operations = new List<RecipeOperation>();
        var diagnostics = new List<RecipeOperationDiagnostic>();
        var itemCache = new Dictionary<int, GarlandItem>();
        var buildContext = new RecipeOperationSnapshotBuildContext();

        foreach (var root in plan.RootItems)
        {
            await VisitNodeAsync(
                root,
                parentNodeId: null,
                depth: 0,
                ancestorNodeIds: Array.Empty<string>(),
                suppressingAncestor: null,
                operations,
                diagnostics,
                itemCache,
                buildContext,
                ct);
        }

        AddDuplicateNodeDiagnostics(operations, diagnostics);
        var isNodeIndexComplete = !diagnostics.Any(diagnostic => diagnostic.Code == RecipeOperationDiagnosticCode.DuplicateNodeId);
        var operationsByNodeId = isNodeIndexComplete
            ? operations.ToDictionary(operation => operation.NodeId)
            : new Dictionary<string, RecipeOperation>();
        var operationsByItemId = operations
            .GroupBy(operation => operation.ResultItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RecipeOperation>)group.ToList());

        stopwatch.Stop();
        var completedAtUtc = DateTime.UtcNow;
        var metadata = new RecipeOperationSnapshotMetadata(
            identity,
            buildOptions,
            startedAtUtc,
            completedAtUtc,
            stopwatch.Elapsed,
            operations.Count,
            operations.Select(operation => operation.ResultItemId).Distinct().Count(),
            buildContext.RecipeDataCalls,
            buildContext.RecipeDataCacheHits,
            diagnostics.Count);

        return new RecipeOperationSnapshot(
            operations,
            operationsByNodeId,
            operationsByItemId,
            diagnostics,
            isNodeIndexComplete,
            metadata);
    }

    private async Task VisitNodeAsync(
        PlanNode node,
        string? parentNodeId,
        int depth,
        IReadOnlyList<string> ancestorNodeIds,
        SuppressingAncestor? suppressingAncestor,
        List<RecipeOperation> operations,
        List<RecipeOperationDiagnostic> diagnostics,
        Dictionary<int, GarlandItem> itemCache,
        RecipeOperationSnapshotBuildContext buildContext,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var itemData = await GetItemAsync(node.ItemId, itemCache, buildContext, ct);
        var resolution = _recipeResolutionService.Resolve(node, itemData);
        var state = GetState(node, suppressingAncestor, resolution);

        if (parentNodeId == null || node.CanCraft || node.Children.Count > 0 || resolution.Kind != null)
        {
            var nodeDiagnostics = new List<RecipeOperationDiagnostic>(resolution.Diagnostics);
            AddParentDiagnostics(node, parentNodeId, nodeDiagnostics);
            var ingredients = BuildIngredients(node, resolution, nodeDiagnostics);
            diagnostics.AddRange(nodeDiagnostics);

            operations.Add(new RecipeOperation(
                node.NodeId,
                parentNodeId,
                ancestorNodeIds.ToArray(),
                depth,
                node.ItemId,
                node.Name,
                node.Quantity,
                node.Source,
                node.SourceReason,
                node.MustBeHq,
                node.CanCraft,
                state,
                suppressingAncestor?.NodeId,
                suppressingAncestor?.ItemName,
                resolution.Kind,
                resolution.RecipeId,
                resolution.JobId,
                resolution.JobName,
                resolution.RecipeLevel,
                resolution.Yield,
                resolution.CraftCount,
                ingredients,
                resolution.Confidence,
                resolution.DataSource,
                nodeDiagnostics.Count > 0));
        }

        var childSuppressingAncestor = suppressingAncestor ?? GetDirectSuppressingAncestor(node);
        var childAncestors = ancestorNodeIds.Concat([node.NodeId]).ToArray();
        foreach (var child in node.Children)
        {
            await VisitNodeAsync(
                child,
                parentNodeId: node.NodeId,
                depth + 1,
                childAncestors,
                childSuppressingAncestor,
                operations,
                diagnostics,
                itemCache,
                buildContext,
                ct);
        }
    }

    private async Task<GarlandItem?> GetItemAsync(
        int itemId,
        Dictionary<int, GarlandItem> itemCache,
        RecipeOperationSnapshotBuildContext buildContext,
        CancellationToken ct)
    {
        if (itemCache.TryGetValue(itemId, out var cached))
        {
            buildContext.RecipeDataCacheHits++;
            return cached;
        }

        try
        {
            buildContext.RecipeDataCalls++;
            var item = await _garlandService.GetItemAsync(itemId, ct);
            if (item != null)
            {
                itemCache[itemId] = item;
            }

            return item;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recipe data for item {ItemId}", itemId);
            return null;
        }
    }

    private static IReadOnlyList<RecipeOperationIngredient> BuildIngredients(
        PlanNode node,
        RecipeResolutionResult resolution,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        if (resolution.Kind == RecipeOperationKind.StandardCraft && resolution.StandardRecipe != null)
        {
            return BuildStandardIngredients(node, resolution.StandardRecipe, resolution.CraftCount, resolution.RecipeId, diagnostics);
        }

        if (resolution.Kind == RecipeOperationKind.CompanyCraft && resolution.CompanyCraft != null)
        {
            return BuildCompanyIngredients(node, resolution.CompanyCraft, resolution.RecipeId, diagnostics);
        }

        return Array.Empty<RecipeOperationIngredient>();
    }

    private static IReadOnlyList<RecipeOperationIngredient> BuildStandardIngredients(
        PlanNode node,
        GarlandCraft recipe,
        int craftCount,
        uint? recipeId,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        var childLookup = node.Children
            .GroupBy(child => child.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(child => child.NodeId, StringComparer.Ordinal).ToList());

        return recipe.Ingredients
            .Select(ingredient =>
            {
                var expectedTotalQuantity = ingredient.Amount * craftCount;
                var child = TryTakeBestChild(node, childLookup, ingredient.Id, expectedTotalQuantity, recipeId, diagnostics);
                var linkStatus = GetIngredientLinkStatus(child, expectedTotalQuantity);
                AddIngredientDiagnostic(node, child, ingredient.Id, ingredient.Name, expectedTotalQuantity, linkStatus, diagnostics, recipeId);

                return new RecipeOperationIngredient(
                    ingredient.Id,
                    ingredient.Name ?? child?.Name ?? $"Item_{ingredient.Id}",
                    ingredient.Amount,
                    expectedTotalQuantity,
                    child?.NodeId,
                    child?.Source,
                    child?.CanCraft ?? false,
                    linkStatus,
                    expectedTotalQuantity,
                    child?.Quantity);
            })
            .Concat(CreateExtraChildIngredients(node, childLookup, recipeId, diagnostics))
            .ToList();
    }

    private static IReadOnlyList<RecipeOperationIngredient> BuildCompanyIngredients(
        PlanNode node,
        GarlandCompanyCraft companyCraft,
        uint? recipeId,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        var childLookup = node.Children
            .GroupBy(child => child.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(child => child.NodeId, StringComparer.Ordinal).ToList());

        return companyCraft.Phases
            .SelectMany(phase => phase.Items)
            .GroupBy(item => item.Id)
            .Select(group =>
            {
                var first = group.First();
                var amountPerCraft = group.Sum(item => item.Amount);
                var expectedTotalQuantity = amountPerCraft * node.Quantity;
                var child = TryTakeBestChild(node, childLookup, group.Key, expectedTotalQuantity, recipeId, diagnostics);
                var linkStatus = GetIngredientLinkStatus(child, expectedTotalQuantity);
                AddIngredientDiagnostic(node, child, group.Key, first.Name, expectedTotalQuantity, linkStatus, diagnostics, recipeId);

                return new RecipeOperationIngredient(
                    group.Key,
                    first.Name ?? child?.Name ?? $"Item_{group.Key}",
                    amountPerCraft,
                    expectedTotalQuantity,
                    child?.NodeId,
                    child?.Source,
                    child?.CanCraft ?? false,
                    linkStatus,
                    expectedTotalQuantity,
                    child?.Quantity);
            })
            .Concat(CreateExtraChildIngredients(node, childLookup, recipeId, diagnostics))
            .ToList();
    }

    private static IEnumerable<RecipeOperationIngredient> CreateExtraChildIngredients(
        PlanNode node,
        Dictionary<int, List<PlanNode>> childLookup,
        uint? recipeId,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        foreach (var child in childLookup.Values.SelectMany(queue => queue))
        {
            diagnostics.Add(new RecipeOperationDiagnostic(
                node.NodeId,
                child.ItemId,
                child.Name,
                RecipeOperationDiagnosticSeverity.Warning,
                "Plan child node does not match any ingredient in the resolved recipe.",
                RecipeOperationDiagnosticCode.ExtraChildNotInRecipe,
                recipeId,
                node.NodeId,
                new Dictionary<string, string>
                {
                    ["childNodeId"] = child.NodeId,
                    ["planChildQuantity"] = child.Quantity.ToString()
                }));

            yield return new RecipeOperationIngredient(
                child.ItemId,
                child.Name,
                0,
                0,
                child.NodeId,
                child.Source,
                child.CanCraft,
                RecipeIngredientLinkStatus.ExtraPlanChild,
                0,
                child.Quantity);
        }
    }

    private static PlanNode? TryTakeBestChild(
        PlanNode node,
        Dictionary<int, List<PlanNode>> childLookup,
        int itemId,
        int expectedTotalQuantity,
        uint? recipeId,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        if (!childLookup.TryGetValue(itemId, out var children) || children.Count == 0)
        {
            return null;
        }

        var exactMatches = children
            .Where(child => child.Quantity == expectedTotalQuantity)
            .OrderBy(child => child.NodeId, StringComparer.Ordinal)
            .ToList();
        if (exactMatches.Count > 1)
        {
            diagnostics.Add(new RecipeOperationDiagnostic(
                node.NodeId,
                itemId,
                exactMatches[0].Name,
                RecipeOperationDiagnosticSeverity.Warning,
                "Multiple child nodes matched the same recipe ingredient quantity; using the lowest child node ID.",
                RecipeOperationDiagnosticCode.DuplicateIngredientChildMatch,
                recipeId,
                node.NodeId,
                new Dictionary<string, string>
                {
                    ["expectedTotalQuantity"] = expectedTotalQuantity.ToString(),
                    ["matchingChildNodeIds"] = string.Join(",", exactMatches.Select(child => child.NodeId))
                }));
        }

        var selected = exactMatches.FirstOrDefault()
            ?? children
                .OrderBy(child => Math.Abs(child.Quantity - expectedTotalQuantity))
                .ThenBy(child => child.NodeId, StringComparer.Ordinal)
                .First();
        children.Remove(selected);
        return selected;
    }

    private static void AddDuplicateNodeDiagnostics(
        IReadOnlyList<RecipeOperation> operations,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        foreach (var group in operations.GroupBy(operation => operation.NodeId).Where(group => group.Count() > 1))
        {
            var first = group.First();
            diagnostics.Add(new RecipeOperationDiagnostic(
                first.NodeId,
                first.ResultItemId,
                first.ResultItemName,
                RecipeOperationDiagnosticSeverity.Error,
                "Multiple recipe operations share the same node ID; node-keyed snapshot index is incomplete.",
                RecipeOperationDiagnosticCode.DuplicateNodeId,
                first.RecipeId,
                first.NodeId,
                new Dictionary<string, string>
                {
                    ["duplicateCount"] = group.Count().ToString(),
                    ["itemIds"] = string.Join(",", group.Select(operation => operation.ResultItemId))
                }));
        }
    }

    private static void AddParentDiagnostics(
        PlanNode node,
        string? traversalParentNodeId,
        List<RecipeOperationDiagnostic> diagnostics)
    {
        if (traversalParentNodeId == null)
        {
            if (node.Parent != null)
            {
                diagnostics.Add(new RecipeOperationDiagnostic(
                    node.NodeId,
                    node.ItemId,
                    node.Name,
                    RecipeOperationDiagnosticSeverity.Warning,
                    "Root plan node has a parent pointer.",
                    RecipeOperationDiagnosticCode.ParentLinkMismatch,
                    null,
                    node.NodeId,
                    new Dictionary<string, string>
                    {
                        ["expectedParentNodeId"] = string.Empty,
                        ["actualParentNodeId"] = node.Parent.NodeId
                    }));
            }

            return;
        }

        if (node.Parent == null)
        {
            diagnostics.Add(new RecipeOperationDiagnostic(
                node.NodeId,
                node.ItemId,
                node.Name,
                RecipeOperationDiagnosticSeverity.Warning,
                "Child plan node is missing its parent pointer; traversal parent was used.",
                RecipeOperationDiagnosticCode.MissingParentLink,
                null,
                node.NodeId,
                new Dictionary<string, string>
                {
                    ["expectedParentNodeId"] = traversalParentNodeId
                }));
            return;
        }

        if (!string.Equals(node.Parent.NodeId, traversalParentNodeId, StringComparison.Ordinal))
        {
            diagnostics.Add(new RecipeOperationDiagnostic(
                node.NodeId,
                node.ItemId,
                node.Name,
                RecipeOperationDiagnosticSeverity.Warning,
                "Plan node parent pointer does not match traversal parent; traversal parent was used.",
                RecipeOperationDiagnosticCode.ParentLinkMismatch,
                null,
                node.NodeId,
                new Dictionary<string, string>
                {
                    ["expectedParentNodeId"] = traversalParentNodeId,
                    ["actualParentNodeId"] = node.Parent.NodeId
                }));
        }
    }

    private static RecipeOperationState GetState(
        PlanNode node,
        SuppressingAncestor? suppressingAncestor,
        RecipeResolutionResult resolution)
    {
        if (resolution.Kind == null)
        {
            return RecipeOperationState.Unresolved;
        }

        if (suppressingAncestor != null)
        {
            return RecipeOperationState.SuppressedByAncestor;
        }

        return node.Source == AcquisitionSource.Craft
            ? RecipeOperationState.Active
            : RecipeOperationState.InactiveBySource;
    }

    private static SuppressingAncestor? GetDirectSuppressingAncestor(PlanNode node)
    {
        return node.Source == AcquisitionSource.Craft
            ? null
            : new SuppressingAncestor(node.NodeId, node.Name);
    }

    private static RecipeIngredientLinkStatus GetIngredientLinkStatus(PlanNode? child, int expectedTotalQuantity)
    {
        if (child == null)
        {
            return RecipeIngredientLinkStatus.MissingPlanChild;
        }

        return child.Quantity == expectedTotalQuantity
            ? RecipeIngredientLinkStatus.Matched
            : RecipeIngredientLinkStatus.QuantityMismatch;
    }

    private static void AddIngredientDiagnostic(
        PlanNode node,
        PlanNode? child,
        int ingredientItemId,
        string? ingredientName,
        int expectedTotalQuantity,
        RecipeIngredientLinkStatus linkStatus,
        List<RecipeOperationDiagnostic> diagnostics,
        uint? recipeId = null)
    {
        if (linkStatus == RecipeIngredientLinkStatus.Matched)
        {
            return;
        }

        if (linkStatus == RecipeIngredientLinkStatus.MissingPlanChild)
        {
            diagnostics.Add(new RecipeOperationDiagnostic(
                node.NodeId,
                ingredientItemId,
                ingredientName ?? $"Item_{ingredientItemId}",
                RecipeOperationDiagnosticSeverity.Warning,
                "Recipe ingredient has no matching plan child node.",
                RecipeOperationDiagnosticCode.IngredientChildMissing,
                recipeId,
                node.NodeId,
                new Dictionary<string, string>
                {
                    ["expectedTotalQuantity"] = expectedTotalQuantity.ToString()
                }));
            return;
        }

        diagnostics.Add(new RecipeOperationDiagnostic(
            node.NodeId,
            ingredientItemId,
            ingredientName ?? child?.Name ?? $"Item_{ingredientItemId}",
            RecipeOperationDiagnosticSeverity.Warning,
            "Recipe ingredient quantity does not match the linked plan child quantity.",
            RecipeOperationDiagnosticCode.IngredientChildQuantityMismatch,
            recipeId,
            node.NodeId,
            new Dictionary<string, string>
            {
                ["expectedTotalQuantity"] = expectedTotalQuantity.ToString(),
                ["planChildQuantity"] = child?.Quantity.ToString() ?? string.Empty
            }));
    }

    private sealed record SuppressingAncestor(string NodeId, string ItemName);

    private sealed class RecipeOperationSnapshotBuildContext
    {
        public int RecipeDataCalls { get; set; }

        public int RecipeDataCacheHits { get; set; }
    }
}
