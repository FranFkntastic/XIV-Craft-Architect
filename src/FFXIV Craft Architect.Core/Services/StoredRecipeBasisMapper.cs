using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class StoredRecipeBasisMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static StoredRecipeOperationSnapshot ToStored(
        RecipeOperationSnapshot snapshot,
        IReadOnlyList<MaterialAggregate> marketAnalysisDemandItems,
        IReadOnlySet<int>? unavailableMarketItemIds = null)
    {
        return new StoredRecipeOperationSnapshot
        {
            SchemaVersion = StoredRecipeOperationSnapshot.CurrentSchemaVersion,
            Metadata = ToStoredMetadata(snapshot.Metadata),
            Operations = snapshot.Operations.Select(ToStoredOperation).ToList(),
            MarketAnalysisDemandItems = marketAnalysisDemandItems.Select(ToStoredDemandItem).ToList(),
            UnavailableMarketItemIds = unavailableMarketItemIds?.ToHashSet() ?? new HashSet<int>()
        };
    }

    public static RecipeOperationSnapshot Hydrate(StoredRecipeOperationSnapshot stored)
    {
        if (!ValidateAndNormalize(stored, out var warning))
        {
            throw new InvalidOperationException(warning);
        }

        var operations = stored.Operations.Select(HydrateOperation).ToList();
        var operationsByNodeId = operations.ToDictionary(operation => operation.NodeId, StringComparer.Ordinal);
        var operationsByItemId = operations
            .GroupBy(operation => operation.ResultItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RecipeOperation>)group.ToList());

        return new RecipeOperationSnapshot(
            operations,
            operationsByNodeId,
            operationsByItemId,
            Array.Empty<RecipeOperationDiagnostic>(),
            metadata: HydrateMetadata(stored.Metadata));
    }

    public static StoredRecipeOperationSnapshot? TryDeserialize(string? json, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredRecipeOperationSnapshot>(json, JsonOptions);

            if (stored == null)
            {
                warning = "Stored recipe basis payload was empty.";
                return null;
            }

            if (stored.SchemaVersion > StoredRecipeOperationSnapshot.CurrentSchemaVersion)
            {
                warning = "Stored recipe basis was saved with a newer schema version.";
                return null;
            }

            Normalize(stored);

            if (!ValidateAndNormalize(stored, out warning))
            {
                return null;
            }

            return stored;
        }
        catch (JsonException ex)
        {
            warning = $"Stored recipe basis could not be deserialized: {ex.Message}";
            return null;
        }
        catch (NotSupportedException ex)
        {
            warning = $"Stored recipe basis could not be deserialized: {ex.Message}";
            return null;
        }
    }

    private static void Normalize(StoredRecipeOperationSnapshot stored)
    {
        stored.Metadata ??= new StoredRecipeOperationMetadata();
        stored.Operations ??= new List<StoredRecipeOperation>();
        stored.MarketAnalysisDemandItems ??= new List<StoredMarketAnalysisDemandItem>();
        stored.UnavailableMarketItemIds ??= new HashSet<int>();
    }

    private static bool ValidateAndNormalize(StoredRecipeOperationSnapshot stored, out string? warning)
    {
        warning = null;
        Normalize(stored);

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        if (stored.MarketAnalysisDemandItems.Any(item => item == null))
        {
            warning = "Stored recipe basis contains a null market analysis demand item.";
            return false;
        }

        foreach (var operation in stored.Operations)
        {
            if (operation == null)
            {
                warning = "Stored recipe basis contains a null operation.";
                return false;
            }

            operation.AncestorNodeIds ??= new List<string>();
            operation.Ingredients ??= new List<StoredRecipeOperationIngredient>();

            if (operation.Ingredients.Any(ingredient => ingredient == null))
            {
                warning = "Stored recipe basis contains a null operation ingredient.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(operation.NodeId))
            {
                warning = "Stored recipe basis contains an empty node id.";
                return false;
            }

            if (!nodeIds.Add(operation.NodeId))
            {
                warning = $"Stored recipe basis contains a duplicate node id: {operation.NodeId}.";
                return false;
            }
        }

        return true;
    }

    private static StoredRecipeOperationMetadata ToStoredMetadata(RecipeOperationSnapshotMetadata metadata)
    {
        return new StoredRecipeOperationMetadata
        {
            PlanSessionVersion = metadata.Identity.PlanSessionVersion,
            PlanStructureVersion = metadata.Identity.PlanStructureVersion,
            PlanDecisionVersion = metadata.Identity.PlanDecisionVersion,
            PlanPriceVersion = metadata.Identity.PlanPriceVersion,
            SettingsVersion = metadata.Identity.SettingsVersion,
            RecipeDataIdentity = metadata.Identity.RecipeDataIdentity,
            CompletedAtUtc = metadata.CompletedAtUtc,
            NodeCount = metadata.NodeCount,
            UniqueItemIdCount = metadata.UniqueItemIdCount,
            DiagnosticCount = metadata.DiagnosticCount
        };
    }

    private static RecipeOperationSnapshotMetadata HydrateMetadata(StoredRecipeOperationMetadata metadata)
    {
        return RecipeOperationSnapshotMetadata.Empty with
        {
            Identity = new RecipeOperationSnapshotIdentity(
                metadata.PlanSessionVersion,
                metadata.PlanStructureVersion,
                metadata.PlanDecisionVersion,
                metadata.PlanPriceVersion,
                metadata.SettingsVersion,
                metadata.RecipeDataIdentity),
            CompletedAtUtc = metadata.CompletedAtUtc,
            NodeCount = metadata.NodeCount,
            UniqueItemIdCount = metadata.UniqueItemIdCount,
            DiagnosticCount = metadata.DiagnosticCount
        };
    }

    private static StoredMarketAnalysisDemandItem ToStoredDemandItem(MaterialAggregate material)
    {
        return new StoredMarketAnalysisDemandItem
        {
            ItemId = material.ItemId,
            Name = material.Name,
            IconId = material.IconId,
            TotalQuantity = material.TotalQuantity,
            RequiresHq = material.RequiresHq
        };
    }

    private static StoredRecipeOperation ToStoredOperation(RecipeOperation operation)
    {
        return new StoredRecipeOperation
        {
            NodeId = operation.NodeId,
            ParentNodeId = operation.ParentNodeId,
            AncestorNodeIds = operation.AncestorNodeIds.ToList(),
            Depth = operation.Depth,
            ResultItemId = operation.ResultItemId,
            ResultItemName = operation.ResultItemName,
            RequestedQuantity = operation.RequestedQuantity,
            Source = operation.Source,
            SourceReason = operation.SourceReason,
            MustBeHq = operation.MustBeHq,
            CanCraft = operation.CanCraft,
            State = operation.State,
            SuppressedByNodeId = operation.SuppressedByNodeId,
            SuppressedByItemName = operation.SuppressedByItemName,
            Kind = operation.Kind,
            RecipeId = operation.RecipeId,
            JobId = operation.JobId,
            JobName = operation.JobName,
            RecipeLevel = operation.RecipeLevel,
            Yield = operation.Yield,
            CraftCount = operation.CraftCount,
            Ingredients = operation.Ingredients.Select(ToStoredIngredient).ToList(),
            ResolutionConfidence = operation.ResolutionConfidence,
            RecipeDataSource = operation.RecipeDataSource,
            HasStructuralDiagnostics = operation.HasStructuralDiagnostics
        };
    }

    private static RecipeOperation HydrateOperation(StoredRecipeOperation operation)
    {
        return new RecipeOperation(
            operation.NodeId,
            operation.ParentNodeId,
            operation.AncestorNodeIds,
            operation.Depth,
            operation.ResultItemId,
            operation.ResultItemName,
            operation.RequestedQuantity,
            operation.Source,
            operation.SourceReason,
            operation.MustBeHq,
            operation.CanCraft,
            operation.State,
            operation.SuppressedByNodeId,
            operation.SuppressedByItemName,
            operation.Kind,
            operation.RecipeId,
            operation.JobId,
            operation.JobName,
            operation.RecipeLevel,
            operation.Yield,
            operation.CraftCount,
            operation.Ingredients.Select(HydrateIngredient).ToList(),
            operation.ResolutionConfidence,
            operation.RecipeDataSource,
            operation.HasStructuralDiagnostics);
    }

    private static StoredRecipeOperationIngredient ToStoredIngredient(RecipeOperationIngredient ingredient)
    {
        return new StoredRecipeOperationIngredient
        {
            ItemId = ingredient.ItemId,
            Name = ingredient.Name,
            AmountPerCraft = ingredient.AmountPerCraft,
            TotalQuantity = ingredient.TotalQuantity,
            ChildNodeId = ingredient.ChildNodeId,
            ChildSource = ingredient.ChildSource,
            ChildCanCraft = ingredient.ChildCanCraft,
            LinkStatus = ingredient.LinkStatus,
            ExpectedTotalQuantity = ingredient.ExpectedTotalQuantity,
            PlanChildQuantity = ingredient.PlanChildQuantity
        };
    }

    private static RecipeOperationIngredient HydrateIngredient(StoredRecipeOperationIngredient ingredient)
    {
        return new RecipeOperationIngredient(
            ingredient.ItemId,
            ingredient.Name,
            ingredient.AmountPerCraft,
            ingredient.TotalQuantity,
            ingredient.ChildNodeId,
            ingredient.ChildSource,
            ingredient.ChildCanCraft,
            ingredient.LinkStatus,
            ingredient.ExpectedTotalQuantity,
            ingredient.PlanChildQuantity);
    }
}
