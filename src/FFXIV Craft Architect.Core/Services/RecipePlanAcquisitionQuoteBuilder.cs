using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class RecipePlanAcquisitionQuoteBuilder
{
    public static IReadOnlyDictionary<string, RecipePlanAcquisitionQuote> Build(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        RecipePlanAcquisitionQuoteBasis marketBasis,
        bool isRefreshing,
        DateTime? evidencePublishedAtUtc)
    {
        if (plan == null)
        {
            return new Dictionary<string, RecipePlanAcquisitionQuote>();
        }

        var planByItemId = shoppingPlans
            .GroupBy(candidate => candidate.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var quotes = new Dictionary<string, RecipePlanAcquisitionQuote>(StringComparer.Ordinal);
        foreach (var root in plan.RootItems)
        {
            AddQuote(root, planByItemId, marketBasis, isRefreshing, evidencePublishedAtUtc, quotes);
        }

        return quotes;
    }

    private static RecipePlanAcquisitionQuote AddQuote(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        RecipePlanAcquisitionQuoteBasis marketBasis,
        bool isRefreshing,
        DateTime? evidencePublishedAtUtc,
        IDictionary<string, RecipePlanAcquisitionQuote> quotes)
    {
        var childQuotes = node.Children
            .Select(child => AddQuote(child, planByItemId, marketBasis, isRefreshing, evidencePublishedAtUtc, quotes))
            .ToArray();

        var quote = node.Source switch
        {
            AcquisitionSource.Craft => BuildCraftQuote(node, childQuotes, isRefreshing, evidencePublishedAtUtc),
            AcquisitionSource.VendorBuy => BuildVendorQuote(node),
            AcquisitionSource.MarketBuyNq => BuildMarketQuote(
                node,
                planByItemId,
                marketBasis,
                isRefreshing,
                evidencePublishedAtUtc,
                hqOnly: false),
            AcquisitionSource.MarketBuyHq => BuildMarketQuote(
                node,
                planByItemId,
                marketBasis,
                isRefreshing,
                evidencePublishedAtUtc,
                hqOnly: true),
            _ => Unavailable(node, RecipePlanAcquisitionQuoteBasis.CraftMaterials, "No acquisition method is selected.")
        };

        quotes[node.NodeId] = quote;
        return quote;
    }

    private static RecipePlanAcquisitionQuote BuildCraftQuote(
        PlanNode node,
        IReadOnlyList<RecipePlanAcquisitionQuote> children,
        bool isRefreshing,
        DateTime? evidencePublishedAtUtc)
    {
        if (!node.CanCraft || children.Count == 0)
        {
            return Unavailable(node, RecipePlanAcquisitionQuoteBasis.CraftMaterials, "This recipe has no actionable material path.");
        }

        if (children.Any(child => child.Status == RecipePlanAcquisitionQuoteStatus.Refreshing))
        {
            return Refreshing(node, RecipePlanAcquisitionQuoteBasis.CraftMaterials, "Refreshing material quotes.");
        }

        if (children.Any(child => !child.IsActionable))
        {
            return isRefreshing
                ? Refreshing(node, RecipePlanAcquisitionQuoteBasis.CraftMaterials, "Refreshing material quotes.")
                : Unavailable(node, RecipePlanAcquisitionQuoteBasis.CraftMaterials, "One or more required materials has no actionable quote.");
        }

        var totalCost = children.Sum(child => child.TotalCost);
        return Actionable(
            node,
            RecipePlanAcquisitionQuoteBasis.CraftMaterials,
            totalCost,
            node.Quantity,
            Array.Empty<string>(),
            evidencePublishedAtUtc,
            $"{children.Count:N0}/{children.Count:N0} direct materials priced.");
    }

    private static RecipePlanAcquisitionQuote BuildVendorQuote(PlanNode node)
    {
        var vendor = node.SelectedVendor ?? node.CheapestGilVendor;
        var unitPrice = vendor?.Price ?? node.VendorPrice;
        if (!node.CanBuyFromVendor || unitPrice <= 0 || node.Quantity <= 0)
        {
            return Unavailable(node, RecipePlanAcquisitionQuoteBasis.Vendor, "No fixed gil vendor price is available.");
        }

        var locations = vendor == null
            ? Array.Empty<string>()
            : new[] { FormatVendor(vendor) };
        return Actionable(
            node,
            RecipePlanAcquisitionQuoteBasis.Vendor,
            unitPrice * node.Quantity,
            node.Quantity,
            locations,
            evidencePublishedAtUtc: null,
            "Fixed gil vendor price.");
    }

    private static RecipePlanAcquisitionQuote BuildMarketQuote(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        RecipePlanAcquisitionQuoteBasis marketBasis,
        bool isRefreshing,
        DateTime? evidencePublishedAtUtc,
        bool hqOnly)
    {
        if (!node.CanBuyFromMarket || node.Quantity <= 0)
        {
            return Unavailable(node, marketBasis, "This item cannot be purchased from the market board.");
        }

        if (!planByItemId.TryGetValue(node.ItemId, out var shoppingPlan) ||
            !TryGetSelectedCoverage(shoppingPlan, node.Quantity, hqOnly, out var totalCost, out var locations))
        {
            return isRefreshing
                ? Refreshing(node, marketBasis, "Refreshing quantity-covering market listings.")
                : Unavailable(node, marketBasis, "Current listings cannot cover this purchase.");
        }

        return Actionable(
            node,
            marketBasis,
            totalCost,
            node.Quantity,
            locations,
            evidencePublishedAtUtc,
            $"{node.Quantity:N0}/{node.Quantity:N0} covered by current listings.");
    }

    private static bool TryGetSelectedCoverage(
        DetailedShoppingPlan plan,
        int occurrenceQuantity,
        bool hqOnly,
        out decimal cost,
        out IReadOnlyList<string> locations)
    {
        cost = 0;
        locations = Array.Empty<string>();
        if (plan.QuantityNeeded <= 0 || occurrenceQuantity <= 0 || !string.IsNullOrWhiteSpace(plan.Error))
        {
            return false;
        }

        var qualityPolicy = hqOnly
            ? MarketCoverageQualityPolicy.HqOnly
            : MarketCoverageQualityPolicy.NqOrHq;
        var coverage = PurchaseRecommendationCost.GetDefaultCoverageOption(plan);
        if (coverage != null &&
            coverage.Kind == MarketCoverageKind.SupportedListings &&
            coverage.IsDefaultEligible &&
            coverage.QualityPolicy == qualityPolicy &&
            coverage.QuantityCovered >= plan.QuantityNeeded &&
            coverage.CashOutCost > 0)
        {
            cost = AllocateAggregateCost(coverage.CashOutCost, occurrenceQuantity, plan.QuantityNeeded);
            locations = coverage.Worlds
                .Select(world => FormatLocation(world.WorldName, world.DataCenter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return cost > 0;
        }

        if (plan.RecommendedSplit?.Any() == true &&
            HasSelectedListingCoverage(plan.RecommendedSplit.SelectMany(split => split.Listings), plan.QuantityNeeded, hqOnly))
        {
            var splitCost = plan.RecommendedSplit.Sum(split => split.TotalCost);
            cost = AllocateAggregateCost(splitCost, occurrenceQuantity, plan.QuantityNeeded);
            locations = plan.RecommendedSplit
                .Select(split => FormatLocation(split.WorldName, split.DataCenter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return cost > 0;
        }

        if (plan.RecommendedWorld is { } world &&
            !string.Equals(world.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase) &&
            world.TotalCost > 0 &&
            HasSelectedListingCoverage(world.Listings, plan.QuantityNeeded, hqOnly))
        {
            cost = AllocateAggregateCost(world.TotalCost, occurrenceQuantity, plan.QuantityNeeded);
            locations = new[] { FormatLocation(world.WorldName, world.DataCenter) };
            return cost > 0;
        }

        return false;
    }

    private static bool HasSelectedListingCoverage(
        IEnumerable<ShoppingListingEntry> listings,
        int quantityNeeded,
        bool hqOnly)
    {
        return listings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .Where(listing => !hqOnly || listing.IsHq)
            .Sum(listing => listing.Quantity) >= quantityNeeded;
    }

    private static decimal AllocateAggregateCost(decimal aggregateCost, int occurrenceQuantity, int aggregateQuantity)
    {
        return aggregateQuantity <= 0
            ? 0
            : aggregateCost * occurrenceQuantity / aggregateQuantity;
    }

    private static RecipePlanAcquisitionQuote Actionable(
        PlanNode node,
        RecipePlanAcquisitionQuoteBasis basis,
        decimal totalCost,
        int coveredQuantity,
        IReadOnlyList<string> locations,
        DateTime? evidencePublishedAtUtc,
        string detail)
    {
        return new RecipePlanAcquisitionQuote(
            node.NodeId,
            node.ItemId,
            node.Quantity,
            node.Source,
            RecipePlanAcquisitionQuoteStatus.Actionable,
            basis,
            totalCost,
            node.Quantity > 0 ? totalCost / node.Quantity : 0,
            coveredQuantity,
            locations,
            evidencePublishedAtUtc,
            detail);
    }

    private static RecipePlanAcquisitionQuote Refreshing(
        PlanNode node,
        RecipePlanAcquisitionQuoteBasis basis,
        string detail)
    {
        return new RecipePlanAcquisitionQuote(
            node.NodeId,
            node.ItemId,
            node.Quantity,
            node.Source,
            RecipePlanAcquisitionQuoteStatus.Refreshing,
            basis,
            0,
            0,
            0,
            Array.Empty<string>(),
            null,
            detail);
    }

    private static RecipePlanAcquisitionQuote Unavailable(
        PlanNode node,
        RecipePlanAcquisitionQuoteBasis basis,
        string detail)
    {
        return new RecipePlanAcquisitionQuote(
            node.NodeId,
            node.ItemId,
            node.Quantity,
            node.Source,
            RecipePlanAcquisitionQuoteStatus.Unavailable,
            basis,
            0,
            0,
            0,
            Array.Empty<string>(),
            null,
            detail);
    }

    private static string FormatLocation(string world, string dataCenter)
    {
        return string.IsNullOrWhiteSpace(dataCenter) ? world : $"{world} / {dataCenter}";
    }

    private static string FormatVendor(VendorInfo vendor)
    {
        return string.IsNullOrWhiteSpace(vendor.Location)
            ? vendor.Name
            : $"{vendor.Name} / {vendor.Location}";
    }
}
