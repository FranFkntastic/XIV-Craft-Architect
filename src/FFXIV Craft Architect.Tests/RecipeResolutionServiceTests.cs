using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeResolutionServiceTests
{
    [Fact]
    public void Resolve_ExactJobLevelYieldMatch_ReturnsExactConfidence()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Blacksmith", recipeLevel: 43, yield: 1, quantity: 8);
        var item = ItemWithCrafts(
            Craft("153", recipeLevel: 43, jobId: 9, yield: 1),
            Craft("273", recipeLevel: 43, jobId: 10, yield: 1));

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.Exact, result.Confidence);
        Assert.Equal(RecipeDataSourceKind.GarlandStandardCraft, result.DataSource);
        Assert.Equal(153u, result.RecipeId);
        Assert.Equal(9, result.JobId);
        Assert.Equal("Blacksmith", result.JobName);
        Assert.Equal(8, result.CraftCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Resolve_MultipleExactMatches_ReturnsAmbiguousExactDiagnostic()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Blacksmith", recipeLevel: 43, yield: 1, quantity: 3);
        var item = ItemWithCrafts(
            Craft("200", recipeLevel: 43, jobId: 9, yield: 1),
            Craft("100", recipeLevel: 43, jobId: 9, yield: 1));

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.AmbiguousExact, result.Confidence);
        Assert.Equal(100u, result.RecipeId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RecipeOperationDiagnosticCode.AmbiguousRecipe, diagnostic.Code);
        Assert.Equal(RecipeOperationDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Resolve_JobOnlyMatch_ReturnsFallbackByJobConfidence()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Blacksmith", recipeLevel: 99, yield: 9, quantity: 4);
        var item = ItemWithCrafts(
            Craft("153", recipeLevel: 43, jobId: 9, yield: 1),
            Craft("273", recipeLevel: 43, jobId: 10, yield: 1));

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.FallbackByJob, result.Confidence);
        Assert.Equal(153u, result.RecipeId);
    }

    [Fact]
    public void Resolve_LevelYieldOnlyMatch_ReturnsFallbackByLevelYieldConfidence()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Weaver", recipeLevel: 43, yield: 1, quantity: 4);
        var item = ItemWithCrafts(
            Craft("153", recipeLevel: 43, jobId: 9, yield: 1),
            Craft("999", recipeLevel: 50, jobId: 14, yield: 1));

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.FallbackByLevelYield, result.Confidence);
        Assert.Equal(153u, result.RecipeId);
    }

    [Fact]
    public void Resolve_NoUsefulMatch_ReturnsFallbackFirstAvailableConfidence()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Weaver", recipeLevel: 99, yield: 9, quantity: 4);
        var item = ItemWithCrafts(
            Craft("300", recipeLevel: 50, jobId: 10, yield: 1),
            Craft("100", recipeLevel: 40, jobId: 9, yield: 2));

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.FallbackFirstAvailable, result.Confidence);
        Assert.Equal(100u, result.RecipeId);
    }

    [Fact]
    public void Resolve_NonNumericRecipeId_ReturnsUnresolvedWithDiagnostic()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Blacksmith", recipeLevel: 43, yield: 1, quantity: 4);
        var item = ItemWithCrafts(Craft("not_numeric", recipeLevel: 43, jobId: 9, yield: 1));

        var result = service.Resolve(node, item);

        Assert.False(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.NonNumericRecipeId, result.Confidence);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RecipeOperationDiagnosticCode.NonNumericRecipeId, diagnostic.Code);
        Assert.Equal(RecipeOperationDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Resolve_CompanyCraft_ReturnsCompanyCraftResolution()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Company Workshop", recipeLevel: 1, yield: 1, quantity: 6);
        var item = new GarlandItem
        {
            Id = 400,
            Name = "Workshop Part",
            CompanyCrafts =
            [
                new GarlandCompanyCraft
                {
                    Id = 4000,
                    PhaseCount = 1,
                    Phases =
                    [
                        new GarlandCompanyPhase
                        {
                            PhaseNumber = 0,
                            Items =
                            [
                                new GarlandCompanyIngredient
                                {
                                    Id = 500,
                                    Name = "Workshop Ingredient",
                                    Amount = 2
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.Exact, result.Confidence);
        Assert.Equal(RecipeDataSourceKind.GarlandCompanyCraft, result.DataSource);
        Assert.Equal(4000u, result.RecipeId);
        Assert.Equal("Company Workshop", result.JobName);
        Assert.Equal(6, result.CraftCount);
    }

    [Fact]
    public void Resolve_ItemWithStandardAndCompanyCrafts_PrefersCompanyCraftForCompanyNode()
    {
        var service = new RecipeResolutionService();
        var node = CraftNode(job: "Company Workshop", recipeLevel: 1, yield: 1, quantity: 6);
        var item = new GarlandItem
        {
            Id = 400,
            Name = "Workshop Part",
            Crafts = [Craft("1234", recipeLevel: 50, jobId: 9, yield: 1)],
            CompanyCrafts =
            [
                new GarlandCompanyCraft
                {
                    Id = 4000,
                    PhaseCount = 1,
                    Phases = []
                }
            ]
        };

        var result = service.Resolve(node, item);

        Assert.True(result.IsResolved);
        Assert.Equal(RecipeOperationKind.CompanyCraft, result.Kind);
        Assert.Equal(RecipeDataSourceKind.GarlandCompanyCraft, result.DataSource);
        Assert.Equal(4000u, result.RecipeId);
    }

    private static PlanNode CraftNode(string job, int recipeLevel, int yield, int quantity)
    {
        return new PlanNode
        {
            ItemId = 5059,
            Name = "Test Craft",
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Job = job,
            RecipeLevel = recipeLevel,
            Yield = yield,
            Quantity = quantity
        };
    }

    private static GarlandItem ItemWithCrafts(params GarlandCraft[] crafts)
    {
        return new GarlandItem
        {
            Id = 5059,
            Name = "Test Craft",
            Crafts = crafts.ToList()
        };
    }

    private static GarlandCraft Craft(string id, int recipeLevel, int jobId, int yield)
    {
        return new GarlandCraft
        {
            Id = id,
            RecipeLevel = recipeLevel,
            JobId = jobId,
            Yield = yield,
            Ingredients = []
        };
    }
}
