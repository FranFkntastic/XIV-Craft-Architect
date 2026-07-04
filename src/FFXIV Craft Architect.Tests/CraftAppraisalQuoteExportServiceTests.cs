using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class CraftAppraisalQuoteExportServiceTests
{
    [Fact]
    public async Task CreateExportAsync_SerializesSingleRootQuoteAsV1Json()
    {
        var appraisal = new RecordingCraftAppraisalService();
        var service = new CraftAppraisalQuoteExportService(appraisal);
        var plan = CreateSingleRootPlan();

        var result = await service.CreateExportAsync(plan, "North America", "Aether");

        Assert.True(result.IsAvailable);
        Assert.Equal("fire-shard-2-craft-appraisal-quote.json", result.FileName);
        Assert.Equal((uint)2, appraisal.LastRequest?.ItemId);
        Assert.Equal("Fire Shard", appraisal.LastRequest?.ItemName);
        Assert.Equal((uint)10, appraisal.LastRequest?.Quantity);
        Assert.Equal("North America", appraisal.LastRequest?.Scope.Region);
        Assert.Equal("Aether", appraisal.LastRequest?.Scope.DataCenter);
        Assert.Equal("HqOnly", appraisal.LastRequest?.Options.HqPolicy);

        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("itemId").GetInt32());
        Assert.Equal(10, root.GetProperty("requestedQuantity").GetInt32());
        Assert.Equal("CraftArchitectLocal", root.GetProperty("source").GetString());
    }

    [Fact]
    public async Task CreateExportAsync_RefusesMissingPlan()
    {
        var service = new CraftAppraisalQuoteExportService(new RecordingCraftAppraisalService());

        var result = await service.CreateExportAsync(null, "North America", "Aether");

        Assert.False(result.IsAvailable);
        Assert.Contains("Build a recipe plan", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateExportAsync_RefusesMultiRootPlan()
    {
        var service = new CraftAppraisalQuoteExportService(new RecordingCraftAppraisalService());
        var plan = CreateSingleRootPlan();
        plan.RootItems.Add(new PlanNode { ItemId = 3, Name = "Ice Shard", Quantity = 5 });

        var result = await service.CreateExportAsync(plan, "North America", "Aether");

        Assert.False(result.IsAvailable);
        Assert.Contains("single root", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    private static CraftingPlan CreateSingleRootPlan() => new()
    {
        RootItems =
        [
            new PlanNode
            {
                ItemId = 2,
                Name = "Fire Shard",
                Quantity = 10,
                MustBeHq = true,
            },
        ],
    };

    private sealed class RecordingCraftAppraisalService : ICraftAppraisalService
    {
        public CraftAppraisalRequest? LastRequest { get; private set; }

        public Task<CraftAppraisalQuote> AppraiseAsync(
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new CraftAppraisalQuote
            {
                ItemId = request.ItemId,
                ItemName = request.ItemName,
                RequestedQuantity = request.Quantity,
                EstimatedUnitCost = 80m,
                EstimatedTotalCost = 800m,
                Source = "CraftArchitectLocal",
                Confidence = "Medium",
                Warnings =
                [
                    "Quote is advisory evidence. User acquisition threshold remains authoritative.",
                ],
            });
        }
    }
}
