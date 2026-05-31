using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class RecipeDemandProjectionService : IRecipeDemandProjectionService
{
    public RecipeDemandProjection Build(CraftingPlan? plan, RecipeOperationSnapshot? snapshot)
    {
        if (plan == null)
        {
            return new RecipeDemandProjection(
                Array.Empty<RecipeDemandRow>(),
                Array.Empty<RecipeDemandRow>(),
                Array.Empty<RecipeDemandRow>(),
                Array.Empty<RecipeDemandRow>());
        }

        var ingredientDemandByChildNodeId = BuildIngredientDemandLookup(snapshot);
        var allPlanDemand = new List<RecipeDemandRow>();
        var marketCandidates = new List<RecipeDemandRow>();
        var activeProcurement = new List<RecipeDemandRow>();
        var suppressed = new List<RecipeDemandRow>();

        foreach (var root in plan.RootItems)
        {
            CollectPlanDemand(root, snapshot, ingredientDemandByChildNodeId, allPlanDemand, suppressingAncestor: null);
            CollectMarketCandidates(root, snapshot, ingredientDemandByChildNodeId, marketCandidates);
            CollectActiveProcurement(root, snapshot, ingredientDemandByChildNodeId, activeProcurement, suppressed, suppressingAncestor: null);
        }

        return new RecipeDemandProjection(
            allPlanDemand,
            marketCandidates,
            activeProcurement,
            suppressed);
    }

    private static void CollectPlanDemand(
        PlanNode node,
        RecipeOperationSnapshot? snapshot,
        IReadOnlyDictionary<string, RecipeOperationIngredient> ingredientDemandByChildNodeId,
        List<RecipeDemandRow> rows,
        SuppressingDemandAncestor? suppressingAncestor)
    {
        rows.Add(CreateRow(
            RecipeDemandViewKind.PlanOccurrence,
            node,
            snapshot,
            ingredientDemandByChildNodeId,
            suppressingAncestor));

        var childSuppressingAncestor = suppressingAncestor;
        if (suppressingAncestor == null && IsLedgerDirectSource(node))
        {
            childSuppressingAncestor = new SuppressingDemandAncestor(node.NodeId, node.ItemId, node.Name);
        }

        foreach (var child in node.Children)
        {
            CollectPlanDemand(child, snapshot, ingredientDemandByChildNodeId, rows, childSuppressingAncestor);
        }
    }

    private static void CollectMarketCandidates(
        PlanNode node,
        RecipeOperationSnapshot? snapshot,
        IReadOnlyDictionary<string, RecipeOperationIngredient> ingredientDemandByChildNodeId,
        List<RecipeDemandRow> rows)
    {
        if (node.Quantity > 0 && node.CanBuyFromMarket)
        {
            rows.Add(CreateRow(
                RecipeDemandViewKind.MarketAnalysisCandidate,
                node,
                snapshot,
                ingredientDemandByChildNodeId,
                suppressingAncestor: null));
        }

        foreach (var child in node.Children)
        {
            CollectMarketCandidates(child, snapshot, ingredientDemandByChildNodeId, rows);
        }
    }

    private static void CollectActiveProcurement(
        PlanNode node,
        RecipeOperationSnapshot? snapshot,
        IReadOnlyDictionary<string, RecipeOperationIngredient> ingredientDemandByChildNodeId,
        List<RecipeDemandRow> activeRows,
        List<RecipeDemandRow> suppressedRows,
        SuppressingDemandAncestor? suppressingAncestor)
    {
        if (suppressingAncestor != null)
        {
            suppressedRows.Add(CreateRow(
                RecipeDemandViewKind.Suppressed,
                node,
                snapshot,
                ingredientDemandByChildNodeId,
                suppressingAncestor));

            foreach (var suppressedChild in node.Children)
            {
                CollectActiveProcurement(suppressedChild, snapshot, ingredientDemandByChildNodeId, activeRows, suppressedRows, suppressingAncestor);
            }

            return;
        }

        if (node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy)
        {
            activeRows.Add(CreateRow(
                RecipeDemandViewKind.ActiveProcurement,
                node,
                snapshot,
                ingredientDemandByChildNodeId,
                suppressingAncestor: null));

            var newSuppressingAncestor = new SuppressingDemandAncestor(node.NodeId, node.ItemId, node.Name);
            foreach (var child in node.Children)
            {
                CollectActiveProcurement(child, snapshot, ingredientDemandByChildNodeId, activeRows, suppressedRows, newSuppressingAncestor);
            }

            return;
        }

        if (node.Children.Count == 0)
        {
            activeRows.Add(CreateRow(
                RecipeDemandViewKind.ActiveProcurement,
                node,
                snapshot,
                ingredientDemandByChildNodeId,
                suppressingAncestor: null));
            return;
        }

        foreach (var child in node.Children)
        {
            CollectActiveProcurement(child, snapshot, ingredientDemandByChildNodeId, activeRows, suppressedRows, suppressingAncestor: null);
        }
    }

    private static RecipeDemandRow CreateRow(
        RecipeDemandViewKind viewKind,
        PlanNode node,
        RecipeOperationSnapshot? snapshot,
        IReadOnlyDictionary<string, RecipeOperationIngredient> ingredientDemandByChildNodeId,
        SuppressingDemandAncestor? suppressingAncestor)
    {
        var operation = snapshot?.GetOperationsForNode(node.NodeId).FirstOrDefault();
        var parentOperation = node.Parent == null
            ? null
            : snapshot?.GetOperationsForNode(node.Parent.NodeId).FirstOrDefault();
        var quantity = node.Quantity;
        var quantityBasis = RecipeDemandQuantityBasis.PlanNodeQuantity;
        if (ingredientDemandByChildNodeId.TryGetValue(node.NodeId, out var ingredient))
        {
            quantity = ingredient.ExpectedTotalQuantity > 0
                ? ingredient.ExpectedTotalQuantity
                : ingredient.TotalQuantity;
            quantityBasis = RecipeDemandQuantityBasis.RecipeExpectedQuantity;
        }

        return new RecipeDemandRow(
            viewKind,
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            quantity,
            quantityBasis,
            node.MustBeHq,
            node.Source,
            node.SourceReason,
            node.Children.Count > 0,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.Parent?.NodeId,
            node.Parent?.Name,
            parentOperation?.NodeId,
            parentOperation?.RecipeId,
            operation?.NodeId,
            operation?.RecipeId,
            suppressingAncestor?.NodeId,
            suppressingAncestor?.ItemId,
            suppressingAncestor?.ItemName);
    }

    private static IReadOnlyDictionary<string, RecipeOperationIngredient> BuildIngredientDemandLookup(RecipeOperationSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return new Dictionary<string, RecipeOperationIngredient>();
        }

        return snapshot.GetIngredientEdges()
            .Select(edge => edge.Ingredient)
            .Where(ingredient => !string.IsNullOrWhiteSpace(ingredient.ChildNodeId))
            .GroupBy(ingredient => ingredient.ChildNodeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static bool IsLedgerDirectSource(PlanNode node)
    {
        return node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy or AcquisitionSource.UnknownSource;
    }

    private sealed record SuppressingDemandAncestor(string NodeId, int ItemId, string ItemName);
}
