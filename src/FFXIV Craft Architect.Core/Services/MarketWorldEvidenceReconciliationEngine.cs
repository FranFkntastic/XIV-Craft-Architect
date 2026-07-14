using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

internal sealed class MarketWorldEvidenceReconciliationEngine
{
    private readonly IMarketCacheService _marketCache;
    private readonly IUniversalisService _universalis;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysis;

    public MarketWorldEvidenceReconciliationEngine(
        IMarketCacheService marketCache,
        IUniversalisService universalis,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis)
    {
        _marketCache = marketCache ?? throw new ArgumentNullException(nameof(marketCache));
        _universalis = universalis ?? throw new ArgumentNullException(nameof(universalis));
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis ??
            throw new ArgumentNullException(nameof(marketPriceLadderAnalysis));
    }

    public async Task<MarketWorldEvidenceReconciliationResult> ReconcileAsync(
        MarketWorldEvidenceReconciliationRequest request,
        IProgress<string>? progress,
        CancellationToken ct,
        MarketAnalysisExecutionOptions? executionOptions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Item);
        ValidateRequest(request);

        var snapshot = request.ObservedEvidence == null
            ? await FetchWorldEvidenceAsync(request, progress, ct)
            : NormalizeSnapshot(request.ObservedEvidence);
        ValidateSnapshot(request, snapshot);

        progress?.Report($"Updating {request.Item.Name} evidence for {request.WorldName}...");
        var existing = (await _marketCache.GetWithStaleAsync(request.Item.ItemId, request.DataCenter)).Data;
        var patch = PatchWorldEvidence(existing, request, snapshot);
        var patched = patch.Data;
        if (patch.Applied)
        {
            await _marketCache.SetAsync(request.Item.ItemId, request.DataCenter, patched);
        }

        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion);
        var requestedPairs = dataCenters
            .Select(dataCenter => (request.Item.ItemId, dataCenter))
            .ToList();
        var entries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        foreach (var pair in requestedPairs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(pair.dataCenter, request.DataCenter, StringComparison.OrdinalIgnoreCase))
            {
                entries[pair] = patched;
                continue;
            }

            var cached = (await _marketCache.GetWithStaleAsync(pair.Item1, pair.dataCenter)).Data;
            if (cached != null)
            {
                entries[pair] = cached;
            }
        }

        var evidence = new MarketEvidenceSet(
            entries,
            requestedPairs,
            request.Scope,
            dataCenters,
            request.SelectedDataCenter,
            request.SelectedRegion,
            maxAge: null,
            fetchedCount: request.ObservedEvidence == null ? 1 : 0,
            loadedAtUtc: DateTime.UtcNow);
        var analyses = await _marketPriceLadderAnalysis.AnalyzeAsync(
            new MarketAnalysisRequest
            {
                Items = [request.Item],
                Evidence = evidence,
                RecommendationMode = request.RecommendationMode,
                AnalysisConfig = request.AnalysisConfig,
                ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
            },
            progress,
            ct,
            executionOptions);
        var analysis = analyses.SingleOrDefault() ??
            throw new InvalidOperationException("The reconciled world evidence did not produce an item analysis.");
        var shoppingPlan = _marketPriceLadderAnalysis.ProjectToShoppingPlan(
            analysis,
            request.Lens,
            request.AnalysisConfig);

        return new MarketWorldEvidenceReconciliationResult(
            analysis,
            shoppingPlan,
            snapshot,
            patch.Applied,
            patch.Message);
    }

    private async Task<MarketWorldEvidenceSnapshot> FetchWorldEvidenceAsync(
        MarketWorldEvidenceReconciliationRequest request,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"Checking {request.Item.Name} on {request.WorldName}...");
        var observedAtUtc = DateTime.UtcNow;
        var responses = await _universalis.GetMarketDataBulkAsync(
            request.WorldName,
            [request.Item.ItemId],
            useParallel: false,
            ct);
        if (!responses.TryGetValue(request.Item.ItemId, out var response))
        {
            throw new InvalidOperationException(
                $"Universalis returned no response for {request.Item.Name} on {request.WorldName}.");
        }

        DateTime? marketUpdatedAtUtc = null;
        if (response.WorldId is int worldId &&
            response.WorldUploadTimes.TryGetValue(worldId, out var worldUploadTime) &&
            worldUploadTime > 0)
        {
            marketUpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(worldUploadTime).UtcDateTime;
        }
        else if (response.LastUploadTimeUnixMilliseconds is > 0)
        {
            marketUpdatedAtUtc = DateTimeOffset
                .FromUnixTimeMilliseconds(response.LastUploadTimeUnixMilliseconds.Value)
                .UtcDateTime;
        }

        return new MarketWorldEvidenceSnapshot(
            request.Item.ItemId,
            request.DataCenter.Trim(),
            request.WorldName.Trim(),
            MarketEvidenceOrigin.Universalis,
            observedAtUtc,
            marketUpdatedAtUtc,
            response.Listings
                .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                .Select(listing => new MarketWorldEvidenceListing(
                    listing.Quantity,
                    listing.PricePerUnit,
                    listing.RetainerName,
                    listing.IsHq,
                    listing.LastReviewTimeUnix))
                .ToList());
    }

    private static MarketWorldEvidencePatchResult PatchWorldEvidence(
        CachedMarketData? existing,
        MarketWorldEvidenceReconciliationRequest request,
        MarketWorldEvidenceSnapshot snapshot)
    {
        var existingWorld = existing?.Worlds.FirstOrDefault(world =>
            string.Equals(world.WorldName, request.WorldName, StringComparison.OrdinalIgnoreCase));
        if (snapshot.Completeness == MarketEvidenceCompleteness.Unavailable)
        {
            if (existing == null || existingWorld == null)
            {
                throw new InvalidOperationException(
                    $"No retained evidence exists for unavailable observation {request.Item.Name} on {request.WorldName}.");
            }

            return new MarketWorldEvidencePatchResult(
                existing,
                false,
                "The market-board observation was unavailable, so retained evidence was left unchanged.");
        }

        if (existingWorld != null && IsOlderThanRetainedEvidence(snapshot, existingWorld))
        {
            return new MarketWorldEvidencePatchResult(
                existing!,
                false,
                "The observation was older than the retained authoritative evidence and was ignored.");
        }

        var worlds = existing?.Worlds
            .Where(world => !string.Equals(world.WorldName, request.WorldName, StringComparison.OrdinalIgnoreCase))
            .Select(CloneWorld)
            .ToList() ?? [];
        var incomingListings = snapshot.Listings.Select(ToCachedListing).ToList();
        var reconciledListings = snapshot.Completeness == MarketEvidenceCompleteness.Partial && existingWorld != null
            ? MergePartialListings(existingWorld.Listings, incomingListings)
            : incomingListings;
        worlds.Add(new CachedWorldData
        {
            WorldId = existingWorld?.WorldId,
            WorldName = request.WorldName.Trim(),
            LastUploadTimeUnixMilliseconds = snapshot.MarketUpdatedAtUtc.HasValue
                ? new DateTimeOffset(CacheTimeHelper.NormalizeToUtc(snapshot.MarketUpdatedAtUtc.Value)).ToUnixTimeMilliseconds()
                : existingWorld?.LastUploadTimeUnixMilliseconds,
            EvidenceOrigin = snapshot.Origin,
            ObservedAtUnixMilliseconds = new DateTimeOffset(
                CacheTimeHelper.NormalizeToUtc(snapshot.ObservedAtUtc)).ToUnixTimeMilliseconds(),
            EvidenceCompleteness = snapshot.Completeness,
            ReportedListingCount = snapshot.ReportedListingCount ?? snapshot.Listings.Count,
            ListingCapacity = snapshot.ListingCapacity,
            IsTruncated = snapshot.IsTruncated || snapshot.Completeness == MarketEvidenceCompleteness.Partial,
            IsCongested = existingWorld?.IsCongested ?? false,
            Listings = reconciledListings
        });

        var nqListings = worlds.SelectMany(world => world.Listings).Where(listing => !listing.IsHq).ToList();
        var hqListings = worlds.SelectMany(world => world.Listings).Where(listing => listing.IsHq).ToList();
        var patched = new CachedMarketData
        {
            ItemId = request.Item.ItemId,
            DataCenter = request.DataCenter.Trim(),
            FetchedAt = existing?.FetchedAt ?? CacheTimeHelper.NormalizeToUtc(snapshot.ObservedAtUtc),
            LastUploadTimeUnixMilliseconds = existing?.LastUploadTimeUnixMilliseconds,
            DCAveragePrice = existing?.DCAveragePrice > 0
                ? existing.DCAveragePrice
                : CalculateAverage(nqListings.Count > 0 ? nqListings : worlds.SelectMany(world => world.Listings)),
            HQAveragePrice = existing?.HQAveragePrice ?? (hqListings.Count > 0 ? CalculateAverage(hqListings) : null),
            Worlds = worlds
        };
        return new MarketWorldEvidencePatchResult(
            patched,
            true,
            snapshot.Completeness == MarketEvidenceCompleteness.Partial
                ? "Partial live evidence was merged without removing unseen retained listings."
                : "Complete world evidence replaced the retained world snapshot.");
    }

    private static bool IsOlderThanRetainedEvidence(
        MarketWorldEvidenceSnapshot snapshot,
        CachedWorldData retained)
    {
        var incomingTimestamp = GetAuthoritativeTimestamp(snapshot);
        var retainedTimestamp = GetAuthoritativeTimestamp(retained);
        return incomingTimestamp.HasValue && retainedTimestamp.HasValue && incomingTimestamp < retainedTimestamp;
    }

    private static DateTime? GetAuthoritativeTimestamp(MarketWorldEvidenceSnapshot snapshot) =>
        snapshot.Origin == MarketEvidenceOrigin.Universalis && snapshot.MarketUpdatedAtUtc.HasValue
            ? CacheTimeHelper.NormalizeToUtc(snapshot.MarketUpdatedAtUtc.Value)
            : CacheTimeHelper.NormalizeToUtc(snapshot.ObservedAtUtc);

    private static DateTime? GetAuthoritativeTimestamp(CachedWorldData world)
    {
        var unix = world.EvidenceOrigin == MarketEvidenceOrigin.Universalis
            ? world.LastUploadTimeUnixMilliseconds ?? world.ObservedAtUnixMilliseconds
            : world.ObservedAtUnixMilliseconds;
        return unix is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(unix.Value).UtcDateTime
            : null;
    }

    private static List<CachedListing> MergePartialListings(
        IEnumerable<CachedListing> retained,
        IEnumerable<CachedListing> observed)
    {
        var merged = retained.Select(CloneListing).ToList();
        var indexByKey = merged
            .Select((listing, index) => (Key: GetListingKey(listing), Index: index))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        foreach (var listing in observed)
        {
            var key = GetListingKey(listing);
            if (indexByKey.TryGetValue(key, out var index))
            {
                merged[index] = listing;
            }
            else
            {
                indexByKey[key] = merged.Count;
                merged.Add(listing);
            }
        }

        return merged;
    }

    private static string GetListingKey(CachedListing listing) =>
        !string.IsNullOrWhiteSpace(listing.ListingId)
            ? $"listing:{listing.ListingId}"
            : !string.IsNullOrWhiteSpace(listing.RetainerId)
                ? $"retainer:{listing.RetainerId}:{listing.PricePerUnit}:{listing.Quantity}:{listing.IsHq}"
                : $"visible:{listing.RetainerName}:{listing.PricePerUnit}:{listing.Quantity}:{listing.IsHq}";

    private static CachedListing ToCachedListing(MarketWorldEvidenceListing listing) =>
        new()
        {
            Quantity = listing.Quantity,
            PricePerUnit = listing.PricePerUnit,
            RetainerName = string.IsNullOrWhiteSpace(listing.RetainerName) ? "Unknown" : listing.RetainerName,
            ListingId = listing.ListingId,
            RetainerId = listing.RetainerId,
            IsHq = listing.IsHq,
            LastReviewTimeUnix = listing.LastReviewTimeUnix
        };

    private static CachedListing CloneListing(CachedListing listing) =>
        new()
        {
            Quantity = listing.Quantity,
            PricePerUnit = listing.PricePerUnit,
            RetainerName = listing.RetainerName,
            ListingId = listing.ListingId,
            RetainerId = listing.RetainerId,
            IsHq = listing.IsHq,
            LastReviewTimeUnix = listing.LastReviewTimeUnix
        };

    private static CachedWorldData CloneWorld(CachedWorldData world) =>
        new()
        {
            WorldId = world.WorldId,
            WorldName = world.WorldName,
            LastUploadTimeUnixMilliseconds = world.LastUploadTimeUnixMilliseconds,
            EvidenceOrigin = world.EvidenceOrigin,
            ObservedAtUnixMilliseconds = world.ObservedAtUnixMilliseconds,
            EvidenceCompleteness = world.EvidenceCompleteness,
            ReportedListingCount = world.ReportedListingCount,
            ListingCapacity = world.ListingCapacity,
            IsTruncated = world.IsTruncated,
            IsCongested = world.IsCongested,
            Listings = world.Listings.Select(CloneListing).ToList()
        };

    private static decimal CalculateAverage(IEnumerable<CachedListing> listings)
    {
        var priced = listings.Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0).ToList();
        var quantity = priced.Sum(listing => (long)listing.Quantity);
        return quantity == 0
            ? 0
            : priced.Sum(listing => (decimal)listing.PricePerUnit * listing.Quantity) / quantity;
    }

    private static MarketWorldEvidenceSnapshot NormalizeSnapshot(MarketWorldEvidenceSnapshot snapshot) =>
        snapshot.Listings == null
            ? throw new ArgumentException("Observed market evidence must include a listing collection.", nameof(snapshot))
            : snapshot with
            {
                DataCenter = snapshot.DataCenter.Trim(),
                WorldName = snapshot.WorldName.Trim(),
                ObservedAtUtc = CacheTimeHelper.NormalizeToUtc(snapshot.ObservedAtUtc),
                MarketUpdatedAtUtc = snapshot.MarketUpdatedAtUtc.HasValue
                    ? CacheTimeHelper.NormalizeToUtc(snapshot.MarketUpdatedAtUtc.Value)
                    : null,
                Listings = snapshot.Listings
                    .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                    .ToList(),
                ReportedListingCount = Math.Max(
                    snapshot.Listings.Count,
                    snapshot.ReportedListingCount ?? snapshot.Listings.Count),
                ListingCapacity = snapshot.ListingCapacity is > 0 ? snapshot.ListingCapacity : null,
                IsTruncated = snapshot.IsTruncated || snapshot.Completeness == MarketEvidenceCompleteness.Partial
            };

    private static void ValidateRequest(MarketWorldEvidenceReconciliationRequest request)
    {
        if (request.Item.ItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Item.ItemId));
        }

        if (string.IsNullOrWhiteSpace(request.DataCenter))
        {
            throw new ArgumentException("A target data center is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorldName))
        {
            throw new ArgumentException("A target world is required.", nameof(request));
        }

        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion);
        if (!dataCenters.Contains(request.DataCenter, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The target world is outside the active market scope.", nameof(request));
        }
    }

    private static void ValidateSnapshot(
        MarketWorldEvidenceReconciliationRequest request,
        MarketWorldEvidenceSnapshot snapshot)
    {
        if (snapshot.ItemId != request.Item.ItemId ||
            !string.Equals(snapshot.DataCenter, request.DataCenter, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.WorldName, request.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Observed market evidence must match the requested item and world.",
                nameof(request));
        }
    }

    private sealed record MarketWorldEvidencePatchResult(
        CachedMarketData Data,
        bool Applied,
        string Message);
}
