using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
namespace FFXIV_Craft_Architect.ContractTests;

public sealed class WorkshopHostContractTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    [Fact]
    public void AppraisalRequestJson_IsVersionedAndCarriesItemName()
    {
        var request = new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
            Scope = new CraftAppraisalScope
            {
                Region = "North America",
                DataCenter = "Aether",
                World = "Siren",
            },
        };
        var json = JsonSerializer.Serialize(request, WireJson);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"itemName\":\"Varnish\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"pricingMode\":\"CurrentMarketEvidence\"", json, StringComparison.Ordinal);
    }
    [Fact]
    public async Task MissingMaterialPriceEvidence_ProducesIncompleteAdvisoryQuote()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 7017,
                    Name = "Varnish",
                    Quantity = 4,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true,
                    MarketPrice = 0,
                },
            ],
        };
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(plan),
            new NoEvidenceService(),
            () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
        });
        Assert.False(quote.IsComplete);
        Assert.Equal("IncompletePriceEvidence", quote.AppraisalStatus);
        Assert.Equal("Low", quote.Confidence);
        Assert.Equal(0m, quote.EstimatedTotalCost);
        Assert.Equal("MissingEvidence", Assert.Single(quote.Materials).CostSource);
        Assert.Contains(quote.Warnings, warning => warning.Contains("missing price evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Same(plan, quote.Plan);
    }
    [Fact]
    public async Task FreshMarketEvidence_IsReusedWithoutAnUpstreamRefresh()
    {
        var material = new PlanNode
        {
            ItemId = 2,
            Name = "Fire Shard",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
        };
        var cache = new FixedMarketCache(new CachedMarketData
        {
            ItemId = 2,
            DataCenter = "Aether",
            DCAveragePrice = 80,
            FetchedAt = DateTime.UtcNow,
        });
        var service = new CraftAppraisalPriceEvidenceService(
            cache,
            new FixedPlanBuilder(new CraftingPlan { RootItems = [material] }));
        var result = await service.ApplyAsync(
            new CraftingPlan { RootItems = [material] },
            new CraftAppraisalRequest
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                Quantity = 10,
                Scope = new CraftAppraisalScope { DataCenter = "Aether" },
            });
        Assert.Equal(80m, material.MarketPrice);
        Assert.Equal(0, cache.EnsureCalls);
        Assert.Equal(0, cache.RefreshCalls);
        Assert.Equal(1, result.MarketItemsPriced);
    }
    [Fact]
    public async Task RegionEvidenceFetchesScopesConcurrentlyAndRetainsPartialResults()
    {
        var material = new PlanNode
        {
            ItemId = 2,
            Name = "Fire Shard",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
        };
        var cache = new PartialRegionMarketCache();
        var service = new CraftAppraisalPriceEvidenceService(
            cache,
            new FixedPlanBuilder(new CraftingPlan { RootItems = [material] }),
            TimeSpan.FromSeconds(1));
        var result = await service.ApplyAsync(
            new CraftingPlan { RootItems = [material] },
            new CraftAppraisalRequest
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                Quantity = 10,
                Scope = new CraftAppraisalScope { Region = "North America" },
            });
        Assert.Equal(70m, material.MarketPrice);
        Assert.Equal(1, result.MarketItemsPriced);
        Assert.Equal(4, cache.EnsureCalls);
        Assert.True(cache.MaximumConcurrentCalls > 1);
        Assert.Contains(
            result.Issues,
            issue => issue.Message.Contains("Dynamis", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Issues,
            issue => issue.Message.Contains("Aether", StringComparison.OrdinalIgnoreCase));
    }
    [Theory]
    [InlineData(AcquisitionSource.MarketBuyNq, 25, 100, "MarketEvidence")]
    [InlineData(AcquisitionSource.VendorBuy, 15, 60, "VendorPrice")]
    public async Task SuccessfulMarketAndVendorQuotes_UseSelectedSourceEconomics(
        AcquisitionSource source,
        int unitCost,
        int totalCost,
        string costSource)
    {
        var material = new PlanNode
        {
            ItemId = 7017,
            Name = "Varnish",
            Quantity = 4,
            Source = source,
            CanBuyFromMarket = true,
            CanBuyFromVendor = source == AcquisitionSource.VendorBuy,
            MarketPrice = unitCost,
            VendorPrice = unitCost,
            VendorOptions = source == AcquisitionSource.VendorBuy
                ? [new VendorInfo { Name = "Material Supplier", Location = "Siren", Price = unitCost, Currency = "gil" }]
                : [],
        };
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [material] }),
            new NoEvidenceService(),
            () => new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
        });
        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Equal("Medium", quote.Confidence);
        Assert.Equal((decimal)unitCost, quote.EstimatedUnitCost);
        Assert.Equal((decimal)totalCost, quote.EstimatedTotalCost);
        var quotedMaterial = Assert.Single(quote.Materials);
        Assert.Equal((4m, 1m, (decimal)unitCost, (decimal)totalCost),
            (quotedMaterial.TotalQuantity, quotedMaterial.QuantityPerCraft, quotedMaterial.UnitCost, quotedMaterial.TotalCost));
        Assert.Equal(source.ToString(), quotedMaterial.AcquisitionSource);
        Assert.Equal(costSource, quotedMaterial.CostSource);
        Assert.Empty(quotedMaterial.Warnings);
    }
    [Fact]
    public async Task MixedSourceQuote_AggregatesMatchingDemandWithoutCollapsingSourceEconomics()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Contract Craft",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
        };
        var firstMarket = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            MarketPrice = 30,
            Parent = root,
        };
        var secondMarket = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            MarketPrice = 30,
            Parent = root,
        };
        var vendor = new PlanNode
        {
            ItemId = 500,
            Name = "Shared Material",
            Quantity = 4,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromVendor = true,
            Parent = root,
            VendorOptions =
            [
                new VendorInfo { Name = "Material Supplier", Location = "Limsa Lominsa", Price = 10, Currency = "gil" },
            ],
        };
        root.Children = [firstMarket, secondMarket, vendor];
        var service = new CraftAppraisalService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            new NoEvidenceService(),
            () => DateTimeOffset.UnixEpoch);
        var quote = await service.AppraiseAsync(new CraftAppraisalRequest
        {
            ItemId = 100,
            ItemName = "Contract Craft",
            Quantity = 2,
        });
        Assert.True(quote.IsComplete);
        Assert.Equal(190m, quote.EstimatedTotalCost);
        Assert.Equal(95m, quote.EstimatedUnitCost);
        Assert.Equal(2, quote.Materials.Count);
        var marketQuote = Assert.Single(quote.Materials, material => material.AcquisitionSource == "MarketBuyNq");
        Assert.Equal((5m, 2.5m, 30m, 150m),
            (marketQuote.TotalQuantity, marketQuote.QuantityPerCraft, marketQuote.UnitCost, marketQuote.TotalCost));
        var vendorQuote = Assert.Single(quote.Materials, material => material.AcquisitionSource == "VendorBuy");
        Assert.Equal((4m, 2m, 10m, 40m),
            (vendorQuote.TotalQuantity, vendorQuote.QuantityPerCraft, vendorQuote.UnitCost, vendorQuote.TotalCost));
    }
    [Fact]
    public void PartialObservationJson_RemainsIncompleteMarketEvidence()
    {
        const string json = """
            {
              "observationId": "observation-1",
              "requestId": "batch-1",
              "attemptId": "attempt-1",
              "sequence": 3,
              "lineId": "line-1",
              "itemId": 5064,
              "itemName": "Silver Ingot",
              "dataCenter": "Aether",
              "worldName": "Siren",
              "readState": "Partial",
              "reportedListingCount": 5,
              "readableListingCount": 1,
              "listingCapacity": 2,
              "isTruncated": true,
              "observedAtUtc": "2026-07-20T12:00:00Z",
              "listings": [{
                "listingId": "listing-1",
                "retainerId": "retainer-1",
                "retainerName": "Contract Seller",
                "quantity": 10,
                "unitPrice": 50,
                "isHq": false
              }]
            }
            """;
        var observation = JsonSerializer.Deserialize<WorkshopHostMarketObservation>(json, WireJson)!;
        var evidence = observation.ToMarketEvidenceSnapshot();
        Assert.Equal(MarketEvidenceCompleteness.Partial, evidence.Completeness);
        Assert.True(evidence.IsTruncated);
        Assert.Equal(5, evidence.ReportedListingCount);
        Assert.Equal(2, evidence.ListingCapacity);
        Assert.Equal("listing-1", Assert.Single(evidence.Listings).ListingId);
    }
    [Fact]
    public void CapabilityWire_RequiresAvailableStatusAndMatchingSchema()
    {
        const string json = """
            {
              "service": "Workshop Host",
              "schemaVersion": 1,
              "capabilities": [{
                "id": "acquisition-batches",
                "status": "available",
                "supportedSchemaVersions": [1],
                "requiredScopes": ["acquisition.write"]
              }, {
                "id": "recipe-graphs",
                "status": "unavailable",
                "supportedSchemaVersions": [1],
                "requiredScopes": []
              }]
            }
            """;
        var capabilities = JsonSerializer.Deserialize<WorkshopHostCapabilityResponse>(json, WireJson)!;
        Assert.True(capabilities.Supports("acquisition-batches", 1));
        Assert.False(capabilities.Supports("acquisition-batches", 2));
        Assert.False(capabilities.Supports("recipe-graphs", 1));
        Assert.False(capabilities.Supports("unknown-capability", 1));
    }
    private sealed class FixedPlanBuilder(CraftingPlan plan) : ICoreRecipePlanBuilder
    {
        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default) => Task.FromResult(plan);
        public Task FetchVendorPricesAsync(CraftingPlan value, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class NoEvidenceService : ICraftAppraisalPriceEvidenceService
    {
        public Task<CraftAppraisalPriceEvidenceResult> ApplyAsync(
            CraftingPlan plan,
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CraftAppraisalPriceEvidenceResult.Empty);
    }
    private sealed class FixedMarketCache(CachedMarketData data) : IMarketCacheService
    {
        public int EnsureCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) =>
            Task.FromResult<CachedMarketData?>(data);
        public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(
            int itemId,
            string dataCenter,
            TimeSpan? maxAge = null) =>
            Task.FromResult<(CachedMarketData?, bool)>((data, false));
        public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
            IReadOnlyCollection<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null) =>
            Task.FromResult<IReadOnlyDictionary<(int, string), CachedMarketData>>(
                requests.ToDictionary(request => request, _ => data));
        public Task SetAsync(int itemId, string dataCenter, CachedMarketData value) => Task.CompletedTask;
        public Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) =>
            Task.FromResult(true);
        public Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
            List<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null) =>
            Task.FromResult(new List<(int, string)>());
        public Task<int> CleanupStaleAsync(TimeSpan maxAge) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync() => Task.FromResult(new CacheStats());
        public Task<int> EnsurePopulatedAsync(
            List<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            EnsureCalls++;
            return Task.FromResult(0);
        }
        public Task<int> RefreshRequestedAsync(
            List<(int itemId, string dataCenter)> requests,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            RefreshCalls++;
            return Task.FromResult(0);
        }
    }
    private sealed class PartialRegionMarketCache : IMarketCacheService
    {
        private readonly object gate = new();
        private readonly Dictionary<(int itemId, string dataCenter), CachedMarketData> entries = [];
        private int activeCalls;
        public int EnsureCalls { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }
        public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
        {
            lock (gate)
            {
                return Task.FromResult(entries.GetValueOrDefault((itemId, dataCenter)));
            }
        }
        public async Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(
            int itemId,
            string dataCenter,
            TimeSpan? maxAge = null) =>
            (await GetAsync(itemId, dataCenter, maxAge), false);
        public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
            IReadOnlyCollection<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyDictionary<(int, string), CachedMarketData>>(
                    requests
                        .Where(entries.ContainsKey)
                        .ToDictionary(request => request, request => entries[request]));
            }
        }
        public Task SetAsync(int itemId, string dataCenter, CachedMarketData value)
        {
            lock (gate)
            {
                entries[(itemId, dataCenter)] = value;
            }
            return Task.CompletedTask;
        }
        public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) =>
            await GetAsync(itemId, dataCenter, maxAge) != null;
        public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
            List<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null)
        {
            var present = await GetManyAsync(requests, maxAge);
            return requests.Where(request => !present.ContainsKey(request)).ToList();
        }
        public Task<int> CleanupStaleAsync(TimeSpan maxAge) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync() => Task.FromResult(new CacheStats());
        public async Task<int> EnsurePopulatedAsync(
            List<(int itemId, string dataCenter)> requests,
            TimeSpan? maxAge = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            lock (gate)
            {
                EnsureCalls++;
                activeCalls++;
                MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, activeCalls);
            }
            try
            {
                var scope = Assert.Single(requests).dataCenter;
                if (scope == "Dynamis")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                await Task.Delay(20, ct);
                var price = scope switch
                {
                    "Aether" => 90,
                    "Primal" => 80,
                    "Crystal" => 70,
                    _ => 0,
                };
                foreach (var request in requests)
                {
                    await SetAsync(request.itemId, request.dataCenter, new CachedMarketData
                    {
                        ItemId = request.itemId,
                        DataCenter = request.dataCenter,
                        DCAveragePrice = price,
                        FetchedAt = DateTime.UtcNow,
                    });
                }
                return requests.Count;
            }
            finally
            {
                lock (gate)
                {
                    activeCalls--;
                }
            }
        }
        public Task<int> RefreshRequestedAsync(
            List<(int itemId, string dataCenter)> requests,
            IProgress<string>? progress = null,
            CancellationToken ct = default) =>
            EnsurePopulatedAsync(requests, null, progress, ct);
    }
}
