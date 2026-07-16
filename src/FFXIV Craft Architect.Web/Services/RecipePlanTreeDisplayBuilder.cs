using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record RecipeNodeDisplayState(
    string NodeId,
    string SourceColor,
    string PriceText,
    string QuoteMethodText,
    string QuoteStatusClass,
    string QuoteBasisText,
    string QuoteUnitText,
    string QuoteCoverageText,
    string QuoteLocationText,
    string QuoteEvidenceText,
    string HqPrefix,
    string? RecipeInfo);

public static class RecipePlanTreeDisplayBuilder
{
    public static IReadOnlyDictionary<string, RecipeNodeDisplayState> Build(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        RecipePlanAcquisitionQuoteBasis marketBasis,
        bool isRefreshing,
        DateTime? evidencePublishedAtUtc)
    {
        if (plan == null)
        {
            return new Dictionary<string, RecipeNodeDisplayState>();
        }

        var quotes = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            shoppingPlans,
            marketBasis,
            isRefreshing,
            evidencePublishedAtUtc);
        var states = new Dictionary<string, RecipeNodeDisplayState>(StringComparer.Ordinal);
        foreach (var root in plan.RootItems)
        {
            AddNode(root, quotes, states);
        }

        return states;
    }

    public static RecipeNodeDisplayState BuildWithoutCost(PlanNode node)
    {
        return CreateState(node, quote: null);
    }

    private static void AddNode(
        PlanNode node,
        IReadOnlyDictionary<string, RecipePlanAcquisitionQuote> quotes,
        IDictionary<string, RecipeNodeDisplayState> states)
    {
        quotes.TryGetValue(node.NodeId, out var quote);
        states[node.NodeId] = CreateState(node, quote);
        foreach (var child in node.Children)
        {
            AddNode(child, quotes, states);
        }
    }

    private static RecipeNodeDisplayState CreateState(PlanNode node, RecipePlanAcquisitionQuote? quote)
    {
        var recipeInfo = !string.IsNullOrEmpty(node.Job) && node.Job != "Company Workshop"
            ? FormatRecipeInfo(node)
            : null;

        return new RecipeNodeDisplayState(
            node.NodeId,
            RecipePlanDisplayHelpers.GetSourceHexColor(node.Source),
            GetPriceText(quote),
            GetMethodText(node.Source),
            GetStatusClass(quote),
            GetBasisText(quote?.Basis),
            quote?.IsActionable == true ? $"{quote.EffectiveUnitCost:N0}g effective" : string.Empty,
            quote?.Detail ?? "No acquisition quote is available.",
            quote?.Locations.Count > 0 ? string.Join(" + ", quote.Locations) : string.Empty,
            FormatEvidenceTime(quote?.EvidencePublishedAtUtc),
            node.MustBeHq ? "\u2605 " : string.Empty,
            recipeInfo);
    }

    private static string FormatRecipeInfo(PlanNode node)
    {
        var displayLevel = node.RecipeDisplayLevel > 0 ? node.RecipeDisplayLevel : node.RecipeLevel;
        var stars = node.RecipeStars > 0 ? new string('\u2605', node.RecipeStars) : string.Empty;
        var master = node.RecipeUnlockItemId > 0 ? " (Master)" : string.Empty;
        return $"Lv.{displayLevel}{stars} {node.Job}{master}";
    }

    private static string GetPriceText(RecipePlanAcquisitionQuote? quote)
    {
        return quote?.Status switch
        {
            RecipePlanAcquisitionQuoteStatus.Actionable => $"{quote.TotalCost:N0}g",
            RecipePlanAcquisitionQuoteStatus.Refreshing => "Pricing…",
            _ => "Unavailable"
        };
    }

    private static string GetMethodText(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => "CRAFT",
            AcquisitionSource.MarketBuyNq => "BUY",
            AcquisitionSource.MarketBuyHq => "BUY HQ",
            AcquisitionSource.VendorBuy => "VENDOR",
            _ => "—"
        };
    }

    private static string GetStatusClass(RecipePlanAcquisitionQuote? quote)
    {
        return quote?.Status switch
        {
            RecipePlanAcquisitionQuoteStatus.Actionable => "actionable",
            RecipePlanAcquisitionQuoteStatus.Refreshing => "refreshing",
            _ => "unavailable"
        };
    }

    private static string GetBasisText(RecipePlanAcquisitionQuoteBasis? basis)
    {
        return basis switch
        {
            RecipePlanAcquisitionQuoteBasis.CraftMaterials => "Current material path",
            RecipePlanAcquisitionQuoteBasis.Vendor => "Fixed gil vendor",
            RecipePlanAcquisitionQuoteBasis.ProcurementRoute => "Current procurement route",
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis => "Current market analysis",
            _ => "Acquisition quote"
        };
    }

    private static string FormatEvidenceTime(DateTime? publishedAtUtc)
    {
        return publishedAtUtc.HasValue
            ? $"Evidence published {publishedAtUtc.Value.ToUniversalTime():MMM d, HH:mm} UTC"
            : string.Empty;
    }
}
