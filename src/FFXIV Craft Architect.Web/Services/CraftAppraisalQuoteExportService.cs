using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class CraftAppraisalQuoteExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ICraftAppraisalService appraisalService;

    public CraftAppraisalQuoteExportService(ICraftAppraisalService appraisalService)
    {
        this.appraisalService = appraisalService ?? throw new ArgumentNullException(nameof(appraisalService));
    }

    public async Task<CraftAppraisalQuoteExportResult> CreateExportAsync(
        CraftingPlan? plan,
        string region,
        string dataCenter,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            return CraftAppraisalQuoteExportResult.Unavailable(
                "Build a recipe plan before exporting a Craft Architect quote.");
        }

        if (plan.RootItems.Count != 1)
        {
            return CraftAppraisalQuoteExportResult.Unavailable(
                "Craft quote export currently requires a single root item plan.");
        }

        var root = plan.RootItems[0];
        var request = new CraftAppraisalRequest
        {
            ItemId = checked((uint)root.ItemId),
            ItemName = root.Name,
            Quantity = checked((uint)root.Quantity),
            Scope = new CraftAppraisalScope
            {
                Region = string.IsNullOrWhiteSpace(region) ? "North America" : region,
                DataCenter = string.IsNullOrWhiteSpace(dataCenter) ? null : dataCenter,
            },
            Options = new CraftAppraisalOptions
            {
                HqPolicy = root.MustBeHq ? "HqOnly" : "Either",
                PricingMode = "CurrentMarketEvidence",
            },
        };

        var quote = await appraisalService.AppraiseAsync(request, cancellationToken);
        var json = JsonSerializer.Serialize(quote, JsonOptions);
        return CraftAppraisalQuoteExportResult.Available(
            BuildFileName(root.Name, root.ItemId),
            json,
            quote);
    }

    private static string BuildFileName(string itemName, int itemId)
    {
        var slug = new string(itemName
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "craft";
        }

        return $"{slug}-{itemId}-craft-appraisal-quote.json";
    }
}

public sealed record CraftAppraisalQuoteExportResult(
    bool IsAvailable,
    string? UnavailableReason,
    string FileName,
    string Json,
    CraftAppraisalQuote? Quote)
{
    public static CraftAppraisalQuoteExportResult Available(
        string fileName,
        string json,
        CraftAppraisalQuote quote) =>
        new(true, null, fileName, json, quote);

    public static CraftAppraisalQuoteExportResult Unavailable(string reason) =>
        new(false, reason, string.Empty, string.Empty, null);
}
