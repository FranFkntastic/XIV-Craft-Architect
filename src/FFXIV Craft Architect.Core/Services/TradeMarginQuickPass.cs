using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>Short-lived listings for Margins. Only calculated costs leave this pass.</summary>
public sealed class TradeMarginQuickPass(IMarketCacheService market, IUniversalisService api, ILogger<TradeMarginQuickPass> log)
{
    public async Task RunAsync(IReadOnlyDictionary<int, TradeMarginCalculator.PreparedRow> rows,
        TradeCompanyPricingScope scope, bool refresh, Action<int, TradeMarginCost> publish, CancellationToken ct,
        TradeMarginPricing? pricing = null)
    {
        var centers = MarketFetchScopeResolver.GetDataCenters(scope.MarketFetchScope, scope.EffectiveDataCenter, scope.Region);
        var pairs = rows.Values.SelectMany(row => row.Leaves).Distinct()
            .SelectMany(id => centers.Select(dc => (itemId: id, dataCenter: dc))).ToArray();
        if (pairs.Length == 0)
        {
            return;
        }

        try
        {
            var entries = refresh ? new Dictionary<(int itemId, string dataCenter), CachedMarketData>()
                : new Dictionary<(int itemId, string dataCenter), CachedMarketData>(await market.GetManyAsync(pairs));
            var missing = pairs.Where(pair => !entries.ContainsKey(pair)).ToArray();
            // With a fully warm cache the regular calculation is already the fast path.
            if (missing.Length == 0)
            {
                return;
            }

            // A refresh stores each world's newest reading, whichever fetch it came from,
            // because Universalis can answer from an older copy. Mirror that here or the
            // preview shows older readings than the cache already holds. A normal load
            // discards stale entries before fetching, so it has nothing to merge.
            var retained = refresh ? await market.GetManyAsync(pairs, TimeSpan.FromDays(36500)) : null;
            var worlds = await api.GetWorldDataAsync(ct);
            var batches = missing.GroupBy(pair => pair.dataCenter)
                .SelectMany(group => group.Select(pair => pair.itemId).Chunk(10).Select(ids => (dc: group.Key, ids)));
            using var slots = new SemaphoreSlim(4);
            var fetched = await Task.WhenAll(batches.Select(async batch =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var response = await api.GetMarketDataBulkAsync(batch.dc, batch.ids, false, ct, historyEntriesPerItem: 0);
                    var absent = batch.ids.Except(response.Keys).ToArray();
                    if (absent.Length != 0)
                    {
                        log.LogWarning("Margins listings missing for {Items} on {DataCenter}", string.Join(",", absent), batch.dc);
                    }
                    return response.Select(pair => UniversalisMarketDataMapper.ToCachedMarketData(
                        pair.Key, batch.dc, pair.Value, worlds, DateTime.UtcNow)).ToArray();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Margins listings failed for {Items} on {DataCenter}", string.Join(",", batch.ids), batch.dc);
                    return [];
                }
                finally
                {
                    slots.Release();
                }
            }));
            foreach (var entry in fetched.SelectMany(batch => batch))
            {
                var key = (entry.ItemId, entry.DataCenter);
                entries[key] = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained?.GetValueOrDefault(key), entry);
            }
            var evidence = new MarketEvidenceSet(entries, pairs, scope.MarketFetchScope, centers,
                scope.EffectiveDataCenter, scope.Region, null, fetched.Sum(batch => batch.Length), DateTime.UtcNow);
            var incomplete = evidence.MissingRequests.Select(pair => pair.itemId).ToHashSet();
            foreach (var (id, row) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.Leaves.Length == 0 || row.Leaves.Any(incomplete.Contains))
                {
                    continue;
                }
                try
                {
                    var cost = await row.Price(evidence, pricing ?? TradeMarginPricing.None, ct);
                    ct.ThrowIfCancellationRequested();
                    publish(id, cost);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Margins quick calculation failed for {Item} with leaves {Leaves} on {Centers}",
                        id, string.Join(",", row.Leaves), string.Join(",", centers));
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Margins quick pass failed for {Pairs}", string.Join(",", pairs));
        }
    }
}
