using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class StoredRecipeBasisMapperTests
{
    [Fact]
    public void ToStoredAndHydrate_RoundTripsOperationsIngredientsAndDemandItems()
    {
        var snapshot = CreateSnapshot();
        var demandItems = new List<MaterialAggregate>
        {
            new()
            {
                ItemId = 300,
                Name = "Demand Item",
                IconId = 123,
                TotalQuantity = 9,
                RequiresHq = true
            }
        };

        var stored = StoredRecipeBasisMapper.ToStored(snapshot, demandItems, new HashSet<int> { 400 });
        var hydrated = StoredRecipeBasisMapper.Hydrate(stored);

        Assert.Equal(StoredRecipeOperationSnapshot.CurrentSchemaVersion, stored.SchemaVersion);
        Assert.Equal(7, stored.Metadata.PlanSessionVersion);
        Assert.Equal(8, stored.Metadata.PlanStructureVersion);
        Assert.Equal(9, stored.Metadata.PlanDecisionVersion);
        Assert.Equal(10, stored.Metadata.PlanPriceVersion);
        Assert.Equal(11, stored.Metadata.SettingsVersion);
        Assert.Equal("recipe-data-v1", stored.Metadata.RecipeDataIdentity);
        Assert.Equal(snapshot.Metadata.CompletedAtUtc, stored.Metadata.CompletedAtUtc);
        Assert.Equal(1, stored.Metadata.NodeCount);
        Assert.Equal(1, stored.Metadata.UniqueItemIdCount);
        Assert.Equal(2, stored.Metadata.DiagnosticCount);

        var storedDemand = Assert.Single(stored.MarketAnalysisDemandItems);
        Assert.Equal(300, storedDemand.ItemId);
        Assert.Equal("Demand Item", storedDemand.Name);
        Assert.Equal(123, storedDemand.IconId);
        Assert.Equal(9, storedDemand.TotalQuantity);
        Assert.True(storedDemand.RequiresHq);
        Assert.Contains(400, stored.UnavailableMarketItemIds);

        var operation = Assert.Single(hydrated.Operations);
        Assert.Equal("root", operation.NodeId);
        Assert.Equal(100, operation.ResultItemId);
        Assert.Equal("Root Item", operation.ResultItemName);
        Assert.Equal(3, operation.RequestedQuantity);
        Assert.True(operation.MustBeHq);
        Assert.Equal(RecipeOperationKind.StandardCraft, operation.Kind);
        Assert.Equal((uint)1234, operation.RecipeId);
        Assert.Equal(5, operation.JobId);
        Assert.Equal("Carpenter", operation.JobName);
        Assert.Equal(90, operation.RecipeLevel);
        Assert.Equal(91, operation.RecipeDisplayLevel);
        Assert.Equal(789, operation.RecipeUnlockItemId);
        Assert.Equal(2, operation.Yield);
        Assert.Equal(2, operation.CraftCount);
        Assert.Equal(RecipeResolutionConfidence.Exact, operation.ResolutionConfidence);
        Assert.Equal(RecipeDataSourceKind.GarlandStandardCraft, operation.RecipeDataSource);
        Assert.True(operation.HasStructuralDiagnostics);

        var ingredient = Assert.Single(operation.Ingredients);
        Assert.Equal(200, ingredient.ItemId);
        Assert.Equal("Ingredient Item", ingredient.Name);
        Assert.Equal(2, ingredient.AmountPerCraft);
        Assert.Equal(4, ingredient.TotalQuantity);
        Assert.Equal("child", ingredient.ChildNodeId);
        Assert.Equal(AcquisitionSource.MarketBuyNq, ingredient.ChildSource);
        Assert.True(ingredient.ChildCanCraft);
        Assert.Equal(RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus);
        Assert.Equal(4, ingredient.ExpectedTotalQuantity);
        Assert.Equal(4, ingredient.PlanChildQuantity);

        Assert.True(hydrated.OperationsByNodeId.ContainsKey("root"));
        Assert.Same(operation, hydrated.OperationsByNodeId["root"]);
        Assert.True(hydrated.OperationsByItemId.ContainsKey(100));
        Assert.Same(operation, Assert.Single(hydrated.OperationsByItemId[100]));
    }

    [Fact]
    public void TryDeserialize_InvalidJson_ReturnsNullAndWarning()
    {
        var result = StoredRecipeBasisMapper.TryDeserialize("{not-json", out var warning);

        Assert.Null(result);
        Assert.Contains("recipe basis", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDeserialize_NewerSchema_ReturnsNullAndWarning()
    {
        var json = JsonSerializer.Serialize(new StoredRecipeOperationSnapshot
        {
            SchemaVersion = StoredRecipeOperationSnapshot.CurrentSchemaVersion + 1
        });

        var result = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);

        Assert.Null(result);
        Assert.Contains("newer", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDeserialize_ExplicitNullCollections_NormalizesToEmptyCollections()
    {
        const string json = """
            {
              "SchemaVersion": 1,
              "Metadata": null,
              "Operations": null,
              "MarketAnalysisDemandItems": null,
              "UnavailableMarketItemIds": null
            }
            """;

        var stored = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);
        var hydrated = StoredRecipeBasisMapper.Hydrate(stored!);

        Assert.NotNull(stored);
        Assert.Null(warning);
        Assert.NotNull(stored.Metadata);
        Assert.Equal(0, stored.Metadata.PlanSessionVersion);
        Assert.Empty(stored.Operations);
        Assert.Empty(stored.MarketAnalysisDemandItems);
        Assert.Empty(stored.UnavailableMarketItemIds);
        Assert.Empty(hydrated.Operations);
        Assert.Empty(hydrated.OperationsByNodeId);
        Assert.Empty(hydrated.OperationsByItemId);
    }



    [Fact]
    public void TryDeserialize_DuplicateNodeIds_ReturnsNullAndWarning()
    {
        const string json = """
            {
              "SchemaVersion": 1,
              "Operations": [
                { "NodeId": "duplicate", "ResultItemId": 100, "ResultItemName": "First" },
                { "NodeId": "duplicate", "ResultItemId": 200, "ResultItemName": "Second" }
              ]
            }
            """;

        var result = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);

        Assert.Null(result);
        Assert.Contains("duplicate", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("node id", warning, StringComparison.OrdinalIgnoreCase);
    }





    [Fact]
    public void TryDeserialize_NullOperationElement_ReturnsNullAndWarning()
    {
        const string json = """
            {
              "SchemaVersion": 1,
              "Operations": [null]
            }
            """;

        var result = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);

        Assert.Null(result);
        Assert.Contains("recipe basis", warning, StringComparison.OrdinalIgnoreCase);
    }





    [Fact]
    public void TryDeserialize_DuplicateDemandItemIds_ReturnsNullAndWarning()
    {
        const string json = """
            {
              "SchemaVersion": 1,
              "MarketAnalysisDemandItems": [
                { "ItemId": 100, "Name": "First", "TotalQuantity": 1 },
                { "ItemId": 100, "Name": "Second", "TotalQuantity": 2 }
              ]
            }
            """;

        var result = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);

        Assert.Null(result);
        Assert.Contains("duplicate", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("demand item", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hydrate_DuplicateNodeIds_ThrowsClearInvalidOperationException()
    {
        var stored = new StoredRecipeOperationSnapshot
        {
            Operations =
            [
                new StoredRecipeOperation { NodeId = "duplicate", ResultItemId = 100, ResultItemName = "First" },
                new StoredRecipeOperation { NodeId = "duplicate", ResultItemId = 200, ResultItemName = "Second" }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => StoredRecipeBasisMapper.Hydrate(stored));

        Assert.Contains("duplicate node id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hydrate_EmptyNodeId_ThrowsClearInvalidOperationException()
    {
        var stored = new StoredRecipeOperationSnapshot
        {
            Operations =
            [
                new StoredRecipeOperation { NodeId = "", ResultItemId = 100, ResultItemName = "Root Item" }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => StoredRecipeBasisMapper.Hydrate(stored));

        Assert.Contains("node id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }



    private static RecipeOperationSnapshot CreateSnapshot()
    {
        var operation = new RecipeOperation(
            "root",
            null,
            Array.Empty<string>(),
            0,
            100,
            "Root Item",
            3,
            AcquisitionSource.Craft,
            AcquisitionSourceReason.UserSelected,
            true,
            true,
            RecipeOperationState.Active,
            null,
            null,
            RecipeOperationKind.StandardCraft,
            1234,
            5,
            "Carpenter",
            90,
            2,
            2,
            [
                new RecipeOperationIngredient(
                    200,
                    "Ingredient Item",
                    2,
                    4,
                    "child",
                    AcquisitionSource.MarketBuyNq,
                    true,
                    RecipeIngredientLinkStatus.Matched,
                    4,
                    4)
            ],
            RecipeResolutionConfidence.Exact,
            RecipeDataSourceKind.GarlandStandardCraft,
            true,
            91,
            789);

        var metadata = new RecipeOperationSnapshotMetadata(
            new RecipeOperationSnapshotIdentity(
                7,
                8,
                9,
                10,
                11,
                "recipe-data-v1"),
            RecipeOperationSnapshotBuildOptions.Default,
            new DateTime(2026, 6, 3, 1, 2, 3, DateTimeKind.Utc),
            new DateTime(2026, 6, 3, 1, 2, 4, DateTimeKind.Utc),
            TimeSpan.FromSeconds(1),
            1,
            1,
            0,
            0,
            2);

        return new RecipeOperationSnapshot(
            [operation],
            new Dictionary<string, RecipeOperation> { ["root"] = operation },
            new Dictionary<int, IReadOnlyList<RecipeOperation>> { [100] = [operation] },
            [
                new RecipeOperationDiagnostic("root", 100, "Root Item", RecipeOperationDiagnosticSeverity.Info, "One"),
                new RecipeOperationDiagnostic("root", 100, "Root Item", RecipeOperationDiagnosticSeverity.Warning, "Two")
            ],
            metadata: metadata);
    }
}
