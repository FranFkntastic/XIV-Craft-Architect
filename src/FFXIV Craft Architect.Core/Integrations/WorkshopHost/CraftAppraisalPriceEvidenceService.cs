using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed class CraftAppraisalPriceEvidenceService : ICraftAppraisalPriceEvidenceService
{
    private static readonly TimeSpan MarketEvidenceMaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultMarketEvidenceScopeTimeout = TimeSpan.FromSeconds(10);

    private readonly IMarketCacheService marketCache;
    private readonly ICoreRecipePlanBuilder planBuilder;
    private readonly TimeSpan marketEvidenceScopeTimeout;

    public CraftAppraisalPriceEvidenceService(
        IMarketCacheService marketCache,
        ICoreRecipePlanBuilder planBuilder,
        TimeSpan? marketEvidenceScopeTimeout = null)
    {
        this.marketCache = marketCache ?? throw new ArgumentNullException(nameof(marketCache));
        this.planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        this.marketEvidenceScopeTimeout = marketEvidenceScopeTimeout ?? DefaultMarketEvidenceScopeTimeout;
        if (this.marketEvidenceScopeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(marketEvidenceScopeTimeout),
                this.marketEvidenceScopeTimeout,
                "Market evidence scope timeout must be positive.");
        }
    }

    public async Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
        CraftingPlan plan,
        CraftAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        await planBuilder.FetchVendorPricesAsync(plan, cancellationToken);

        var activeRows = new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ActiveProcurementDemand
            .Where(row => row.Quantity > 0)
            .ToList();
        var issues = new List<CraftAppraisalPriceEvidenceIssue>();
        var marketRows = activeRows
            .Where(row => row.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq)
            .GroupBy(row => (row.ItemId, row.ItemName, row.Source))
            .Select(group => group.First())
            .ToList();
        var vendorItemsPriced = activeRows
            .Count(row => row.Source == AcquisitionSource.VendorBuy && ResolveSelectedGilVendorPrice(row) > 0);

        if (marketRows.Count == 0)
        {
            return new CraftAppraisalPriceEvidenceResult(0, 0, vendorItemsPriced, issues);
        }

        var resolvedScope = CraftAppraisalScopeResolver.Resolve(request);
        if (!resolvedScope.IsSupported)
        {
            issues.AddRange(marketRows.Select(row => new CraftAppraisalPriceEvidenceIssue(
                row.ItemId,
                row.ItemName,
                row.Source.ToString(),
                $"{row.ItemName}: {resolvedScope.UnsupportedReason}")));
            return new CraftAppraisalPriceEvidenceResult(0, 0, vendorItemsPriced, issues);
        }

        var requests = marketRows
            .SelectMany(row => resolvedScope.MarketScopes.Select(scope => (row.ItemId, dataCenter: scope)))
            .Distinct()
            .ToList();
        if (requests.Count == 0)
        {
            return new CraftAppraisalPriceEvidenceResult(0, 0, vendorItemsPriced, issues);
        }

        var entries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>(
            await marketCache.GetManyAsync(requests, MarketEvidenceMaxAge));
        var missing = new List<(int itemId, string dataCenter)>();
        foreach (var cacheRequest in requests.Where(requestKey => !entries.ContainsKey(requestKey)))
        {
            var (retained, _) = await marketCache.GetWithStaleAsync(
                cacheRequest.Item1,
                cacheRequest.dataCenter,
                MarketEvidenceMaxAge);
            if (retained != null)
            {
                entries[cacheRequest] = retained;
                var row = marketRows.First(candidate => candidate.ItemId == cacheRequest.Item1);
                issues.Add(new CraftAppraisalPriceEvidenceIssue(
                    row.ItemId,
                    row.ItemName,
                    row.Source.ToString(),
                    $"{row.ItemName}: using retained market evidence from {retained.FetchedAt:u} because the current observation is older than 30 minutes."));
            }
            else
            {
                missing.Add(cacheRequest);
            }
        }

        if (missing.Count > 0)
        {
            await Task.WhenAll(missing
                .GroupBy(requestKey => requestKey.dataCenter, StringComparer.OrdinalIgnoreCase)
                .Select(async scopeRequests =>
                {
                    using var scopeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    scopeTimeout.CancelAfter(marketEvidenceScopeTimeout);
                    try
                    {
                        await marketCache.EnsurePopulatedAsync(
                            scopeRequests.ToList(),
                            MarketEvidenceMaxAge,
                            progress: null,
                            scopeTimeout.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (
                        ex is HttpRequestException or
                        TimeoutException or
                        OperationCanceledException)
                    {
                        // A regional appraisal remains useful with partial data-center evidence.
                        // Missing pairs are identified explicitly after all scopes settle.
                    }
                }));

            foreach (var entry in await marketCache.GetManyAsync(missing, MarketEvidenceMaxAge))
            {
                entries[entry.Key] = entry.Value;
            }

            foreach (var cacheRequest in missing.Where(requestKey => !entries.ContainsKey(requestKey)))
            {
                var row = marketRows.First(candidate => candidate.ItemId == cacheRequest.Item1);
                issues.Add(new CraftAppraisalPriceEvidenceIssue(
                    row.ItemId,
                    row.ItemName,
                    row.Source.ToString(),
                    $"{row.ItemName}: current market evidence for {cacheRequest.dataCenter} could not be retrieved and no retained observation is available."));
            }
        }

        ApplyMarketEvidenceToPlan(plan, entries);

        var pricedMarketItems = 0;
        foreach (var row in marketRows)
        {
            var price = ResolveMarketPrice(row, resolvedScope.MarketScopes, entries);
            if (price > 0)
            {
                pricedMarketItems++;
                continue;
            }

            issues.Add(new CraftAppraisalPriceEvidenceIssue(
                row.ItemId,
                row.ItemName,
                row.Source.ToString(),
                row.Source == AcquisitionSource.MarketBuyHq
                    ? $"{row.ItemName} is missing HQ market price evidence for the requested scope."
                    : $"{row.ItemName} is missing market price evidence for the requested scope."));
        }

        return new CraftAppraisalPriceEvidenceResult(
            requests.Count,
            pricedMarketItems,
            vendorItemsPriced,
            issues);
    }

    private static void ApplyMarketEvidenceToPlan(
        CraftingPlan plan,
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries)
    {
        foreach (var root in plan.RootItems)
        {
            ApplyMarketEvidenceToNode(root, entries);
        }
    }

    private static void ApplyMarketEvidenceToNode(
        PlanNode node,
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries)
    {
        var itemEntries = entries
            .Where(entry => entry.Key.itemId == node.ItemId)
            .Select(entry => entry.Value)
            .ToList();
        if (itemEntries.Count > 0)
        {
            var nqPrices = itemEntries
                .Select(entry => entry.DCAveragePrice)
                .Where(price => price > 0)
                .ToList();
            if (nqPrices.Count > 0)
            {
                node.MarketPrice = nqPrices.Min();
            }

            var hqPrices = itemEntries
                .Select(entry => entry.HQAveragePrice ?? 0)
                .Where(price => price > 0)
                .ToList();
            if (hqPrices.Count > 0)
            {
                node.HqMarketPrice = hqPrices.Min();
            }
        }

        foreach (var child in node.Children)
        {
            ApplyMarketEvidenceToNode(child, entries);
        }
    }

    private static decimal ResolveMarketPrice(
        RecipeDemandRow row,
        IReadOnlyList<string> scopes,
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries)
    {
        var matching = scopes
            .Select(scope => entries.GetValueOrDefault((row.ItemId, scope)))
            .Where(entry => entry != null)
            .Select(entry => entry!)
            .ToList();
        if (matching.Count == 0)
        {
            return 0;
        }

        var prices = row.Source == AcquisitionSource.MarketBuyHq
            ? matching.Select(entry => entry.HQAveragePrice ?? 0)
            : matching.Select(entry => entry.DCAveragePrice);
        return prices
            .Where(price => price > 0)
            .DefaultIfEmpty(0)
            .Min();
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
}
