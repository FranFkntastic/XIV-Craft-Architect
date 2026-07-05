using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed class CraftAppraisalService : ICraftAppraisalService
{
    private readonly ICoreRecipePlanBuilder planBuilder;
    private readonly ICraftAppraisalPriceEvidenceService priceEvidenceService;
    private readonly Func<DateTimeOffset> getUtcNow;

    public CraftAppraisalService(
        ICoreRecipePlanBuilder planBuilder,
        ICraftAppraisalPriceEvidenceService priceEvidenceService,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        this.priceEvidenceService = priceEvidenceService ?? throw new ArgumentNullException(nameof(priceEvidenceService));
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public CraftAppraisalService(
        ICoreRecipePlanBuilder planBuilder,
        Func<DateTimeOffset>? getUtcNow = null)
        : this(planBuilder, NullCraftAppraisalPriceEvidenceService.Instance, getUtcNow)
    {
    }

    public async Task<CraftAppraisalQuote> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Item ID must be greater than zero.");
        }

        if (request.Quantity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Quantity must be greater than zero.");
        }

        var plan = await planBuilder.BuildPlanAsync(
            [(checked((int)request.ItemId), request.ItemName, checked((int)request.Quantity), IsHqOnly(request))],
            ResolveDataCenter(request),
            request.Scope.World ?? string.Empty,
            cancellationToken);

        var evidenceResult = await priceEvidenceService.ApplyAsync(plan, request, cancellationToken);
        var activeRows = new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ActiveProcurementDemand
            .Where(row => row.Quantity > 0)
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ThenBy(row => row.Source)
            .ToList();
        var materialQuotes = activeRows
            .GroupBy(CreateQuoteMaterialKey)
            .Select(group => MapMaterial(group, request.Quantity))
            .ToList();
        var estimatedTotalCost = materialQuotes.Sum(material => material.TotalCost);
        var warningList = BuildWarnings(materialQuotes, evidenceResult);
        var outputQuantity = ResolveOutputQuantity(plan);
        var isComplete = materialQuotes.Count > 0 && materialQuotes.All(material => material.UnitCost > 0);
        var appraisalStatus = materialQuotes.Count == 0
            ? "NoActiveProcurement"
            : isComplete
                ? "Complete"
                : "IncompletePriceEvidence";

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
            Confidence = isComplete ? "Medium" : "Low",
            IsComplete = isComplete,
            AppraisalStatus = appraisalStatus,
            Materials = materialQuotes,
            Warnings = warningList,
        };
    }

    private static QuoteMaterialKey CreateQuoteMaterialKey(RecipeDemandRow row)
    {
        var vendor = row.Source == AcquisitionSource.VendorBuy ? row.SelectedVendor : null;
        return new QuoteMaterialKey(
            row.ItemId,
            row.Source,
            row.MustBeHq,
            vendor?.Name ?? string.Empty,
            vendor?.Location ?? string.Empty,
            vendor?.Price ?? 0,
            vendor?.Currency ?? string.Empty);
    }

    private static CraftAppraisalMaterialQuote MapMaterial(
        IGrouping<QuoteMaterialKey, RecipeDemandRow> materialRows,
        uint requestedQuantity)
    {
        var firstRow = materialRows.First();
        var unitCost = ResolveSelectedSourceUnitCost(firstRow);
        var totalQuantity = materialRows.Sum(row => row.Quantity);
        var totalCost = totalQuantity * unitCost;
        var warnings = new List<string>();
        if (unitCost <= 0)
        {
            warnings.Add(CreateMissingEvidenceWarning(firstRow));
        }

        return new CraftAppraisalMaterialQuote
        {
            ItemId = checked((uint)firstRow.ItemId),
            ItemName = firstRow.ItemName,
            QuantityPerCraft = requestedQuantity == 0 ? 0 : (decimal)totalQuantity / requestedQuantity,
            TotalQuantity = totalQuantity,
            UnitCost = unitCost,
            TotalCost = totalCost,
            AcquisitionSource = firstRow.Source.ToString(),
            CostSource = ResolveCostSource(firstRow, unitCost),
            CostSourceDetails = ResolveCostSourceDetails(firstRow, unitCost),
            Warnings = warnings,
        };
    }

    private static decimal ResolveSelectedSourceUnitCost(RecipeDemandRow row)
    {
        return row.Source switch
        {
            AcquisitionSource.VendorBuy => ResolveSelectedGilVendorPrice(row),
            AcquisitionSource.MarketBuyHq => row.HqUnitPrice,
            AcquisitionSource.MarketBuyNq => row.UnitPrice,
            AcquisitionSource.UnknownSource => 0,
            _ => row.MustBeHq && row.HqUnitPrice > 0 ? row.HqUnitPrice : row.UnitPrice
        };
    }

    private static decimal ResolveSelectedGilVendorPrice(RecipeDemandRow row)
    {
        if (row.SelectedVendor?.IsGilVendor == true)
        {
            return row.SelectedVendor.Price;
        }

        var cheapestGilVendor = row.VendorOptions
            .Where(vendor => vendor.IsGilVendor)
            .OrderBy(vendor => vendor.Price)
            .FirstOrDefault();

        if (cheapestGilVendor != null)
        {
            return cheapestGilVendor.Price;
        }

        return row.VendorUnitPrice;
    }

    private static string ResolveCostSource(RecipeDemandRow row, decimal unitCost)
    {
        if (unitCost <= 0)
        {
            return "MissingEvidence";
        }

        return row.Source switch
        {
            AcquisitionSource.VendorBuy => "VendorPrice",
            AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq => "MarketEvidence",
            _ => "PlanEvidence",
        };
    }

    private static string ResolveCostSourceDetails(RecipeDemandRow row, decimal unitCost)
    {
        if (unitCost <= 0)
        {
            return string.Empty;
        }

        if (row.Source == AcquisitionSource.VendorBuy)
        {
            var vendor = row.SelectedVendor;
            return vendor == null
                ? "Gil vendor evidence"
                : $"{vendor.Name} @ {vendor.Location}";
        }

        return row.Source == AcquisitionSource.MarketBuyHq
            ? "HQ market evidence"
            : row.Source == AcquisitionSource.MarketBuyNq
                ? "NQ market evidence"
                : string.Empty;
    }

    private static string CreateMissingEvidenceWarning(RecipeDemandRow row)
    {
        return row.Source switch
        {
            AcquisitionSource.VendorBuy => $"{row.ItemName} is missing vendor price evidence.",
            AcquisitionSource.MarketBuyHq => $"{row.ItemName} is missing HQ market price evidence.",
            AcquisitionSource.MarketBuyNq => $"{row.ItemName} is missing market price evidence.",
            _ => $"{row.ItemName} is missing price evidence.",
        };
    }

    private static List<string> BuildWarnings(
        IReadOnlyList<CraftAppraisalMaterialQuote> materials,
        CraftAppraisalPriceEvidenceResult evidenceResult)
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

        warnings.AddRange(evidenceResult.Issues.Select(issue => issue.Message));

        return warnings;
    }

    private static string ResolveDataCenter(CraftAppraisalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Scope.DataCenter))
        {
            return request.Scope.DataCenter;
        }

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

    private sealed record QuoteMaterialKey(
        int ItemId,
        AcquisitionSource Source,
        bool MustBeHq,
        string VendorName,
        string VendorLocation,
        decimal VendorPrice,
        string VendorCurrency);

    private sealed class NullCraftAppraisalPriceEvidenceService : ICraftAppraisalPriceEvidenceService
    {
        public static NullCraftAppraisalPriceEvidenceService Instance { get; } = new();

        public Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
            CraftingPlan plan,
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CraftAppraisalPriceEvidenceResult.Empty);
        }
    }
}
