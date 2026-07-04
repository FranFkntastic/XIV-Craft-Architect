using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed class CraftAppraisalService : ICraftAppraisalService
{
    private readonly ICoreRecipePlanBuilder planBuilder;
    private readonly Func<DateTimeOffset> getUtcNow;

    public CraftAppraisalService(
        ICoreRecipePlanBuilder planBuilder,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CraftAppraisalQuote> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemId == 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Item ID must be greater than zero.");
        if (request.Quantity == 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Quantity must be greater than zero.");

        var plan = await planBuilder.BuildPlanAsync(
            [(checked((int)request.ItemId), request.ItemName, checked((int)request.Quantity), IsHqOnly(request))],
            ResolveDataCenter(request),
            request.Scope.World ?? string.Empty,
            cancellationToken);

        var activeMaterials = AcquisitionPlanningService.GetActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .OrderBy(item => item.Name)
            .ThenBy(item => item.ItemId)
            .ToList();
        var materialQuotes = activeMaterials
            .Select(material => MapMaterial(material, request.Quantity))
            .ToList();
        var estimatedTotalCost = materialQuotes.Sum(material => material.TotalCost);
        var warningList = BuildWarnings(materialQuotes);
        var outputQuantity = ResolveOutputQuantity(plan);
        var missingEvidence = materialQuotes.Any(material => material.UnitCost <= 0);

        return new CraftAppraisalQuote
        {
            SchemaVersion = 1,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            RequestedQuantity = request.Quantity,
            OutputQuantity = outputQuantity,
            EstimatedUnitCost = estimatedTotalCost / request.Quantity,
            EstimatedTotalCost = estimatedTotalCost,
            Currency = "gil",
            QuotedAtUtc = getUtcNow(),
            Source = "CraftArchitectLocal",
            Confidence = missingEvidence ? "Low" : "Medium",
            Materials = materialQuotes,
            Warnings = warningList,
        };
    }

    private static CraftAppraisalMaterialQuote MapMaterial(MaterialAggregate material, uint requestedQuantity)
    {
        var warnings = new List<string>();
        if (material.UnitPrice <= 0)
        {
            warnings.Add($"{material.Name} is missing price evidence.");
        }

        return new CraftAppraisalMaterialQuote
        {
            ItemId = checked((uint)material.ItemId),
            ItemName = material.Name,
            QuantityPerCraft = requestedQuantity == 0 ? 0 : (decimal)material.TotalQuantity / requestedQuantity,
            TotalQuantity = material.TotalQuantity,
            UnitCost = material.UnitPrice,
            TotalCost = material.TotalCost,
            CostSource = material.UnitPrice > 0 ? "PlanEvidence" : "MissingEvidence",
            Warnings = warnings,
        };
    }

    private static List<string> BuildWarnings(IReadOnlyList<CraftAppraisalMaterialQuote> materials)
    {
        var warnings = new List<string>
        {
            "Quote is advisory evidence. User acquisition threshold remains authoritative.",
        };

        var missingEvidenceCount = materials.Count(material => material.UnitCost <= 0);
        if (missingEvidenceCount > 0)
        {
            warnings.Add($"{missingEvidenceCount} active material(s) are missing price evidence.");
        }

        if (materials.Count == 0)
        {
            warnings.Add("No active procurement materials were found for this appraisal.");
        }

        return warnings;
    }

    private static string ResolveDataCenter(CraftAppraisalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Scope.DataCenter))
            return request.Scope.DataCenter;

        return request.Scope.Region;
    }

    private static bool IsHqOnly(CraftAppraisalRequest request)
    {
        return string.Equals(request.Options.HqPolicy, "HqOnly", StringComparison.OrdinalIgnoreCase);
    }

    private static uint ResolveOutputQuantity(CraftingPlan plan)
    {
        var root = plan.RootItems.FirstOrDefault();
        return root?.Yield > 0
            ? checked((uint)root.Yield)
            : 1u;
    }
}
