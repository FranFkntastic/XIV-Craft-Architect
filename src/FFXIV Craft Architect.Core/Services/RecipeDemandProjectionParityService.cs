using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class RecipeDemandProjectionParityService : IRecipeDemandProjectionParityService
{
    private readonly IRecipeDemandProjectionService _projectionService;

    public RecipeDemandProjectionParityService(IRecipeDemandProjectionService projectionService)
    {
        _projectionService = projectionService;
    }

    public RecipeDemandParityReport Compare(CraftingPlan? plan, RecipeOperationSnapshot? snapshot)
    {
        var projection = _projectionService.Build(plan, snapshot);
        var mismatches = new List<RecipeDemandParityMismatch>();

        CompareAggregateView(
            RecipeDemandParityView.MarketAnalysisCandidates,
            AcquisitionPlanningService.GetMarketAnalysisCandidates(plan),
            projection.ToMarketAnalysisMaterialAggregates(),
            mismatches);
        CompareAggregateView(
            RecipeDemandParityView.ActiveProcurement,
            AcquisitionPlanningService.GetActiveProcurementItems(plan),
            projection.ToActiveProcurementMaterialAggregates(),
            mismatches);

        return new RecipeDemandParityReport(mismatches);
    }

    private static void CompareAggregateView(
        RecipeDemandParityView view,
        IReadOnlyList<MaterialAggregate> expected,
        IReadOnlyList<MaterialAggregate> actual,
        List<RecipeDemandParityMismatch> mismatches)
    {
        var expectedByItemId = expected.ToDictionary(item => item.ItemId);
        var actualByItemId = actual.ToDictionary(item => item.ItemId);

        foreach (var missing in expected.Where(item => !actualByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, RecipeDemandParityField.MissingItem, missing, "present", "missing"));
        }

        foreach (var extra in actual.Where(item => !expectedByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, RecipeDemandParityField.ExtraItem, extra, "missing", "present"));
        }

        foreach (var expectedItem in expected.Where(item => actualByItemId.ContainsKey(item.ItemId)))
        {
            var actualItem = actualByItemId[expectedItem.ItemId];
            CompareValue(view, RecipeDemandParityField.TotalQuantity, expectedItem, expectedItem.TotalQuantity, actualItem.TotalQuantity, mismatches);
            CompareValue(view, RecipeDemandParityField.RequiresHq, expectedItem, expectedItem.RequiresHq, actualItem.RequiresHq, mismatches);
            CompareValue(view, RecipeDemandParityField.SourceCount, expectedItem, expectedItem.Sources.Count, actualItem.Sources.Count, mismatches);

            var sourceCount = Math.Min(expectedItem.Sources.Count, actualItem.Sources.Count);
            for (var index = 0; index < sourceCount; index++)
            {
                var expectedSource = expectedItem.Sources[index];
                var actualSource = actualItem.Sources[index];
                CompareValue(view, RecipeDemandParityField.SourceParent, expectedItem, expectedSource.ParentItemName, actualSource.ParentItemName, mismatches);
                CompareValue(view, RecipeDemandParityField.SourceQuantity, expectedItem, expectedSource.Quantity, actualSource.Quantity, mismatches);
                CompareValue(view, RecipeDemandParityField.SourceCraftedFlag, expectedItem, expectedSource.IsCrafted, actualSource.IsCrafted, mismatches);
            }
        }
    }

    private static void CompareValue<T>(
        RecipeDemandParityView view,
        RecipeDemandParityField field,
        MaterialAggregate item,
        T expected,
        T actual,
        List<RecipeDemandParityMismatch> mismatches)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }

        mismatches.Add(CreateMismatch(
            view,
            field,
            item,
            expected?.ToString() ?? string.Empty,
            actual?.ToString() ?? string.Empty));
    }

    private static RecipeDemandParityMismatch CreateMismatch(
        RecipeDemandParityView view,
        RecipeDemandParityField field,
        MaterialAggregate item,
        string expected,
        string actual)
    {
        return new RecipeDemandParityMismatch(
            view,
            field,
            item.ItemId,
            item.Name,
            expected,
            actual);
    }
}
