using System.Reflection;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public class TradeMarginQuickPassTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IdenticalListingsProduceIdenticalCost(bool listed, bool timestamped)
    {
        var fixture = new Fixture(listed, timestamped);
        var quick = await fixture.QuickAsync();
        var full = await fixture.FullAsync();
        Assert.Equal(listed ? 40m : (decimal?)null, full.Materials);
        Assert.Equal(1, full.Synths);
        Assert.Equal(listed && !timestamped, full.UnknownPriceAge);
        Assert.Equal(full.Missing.ToArray(), quick.Missing.ToArray());
        Assert.Equal(full, quick with { Missing = full.Missing });
    }

    [Fact]
    public async Task QuickCalculationOnlyReadsMarketCache()
    {
        var fixture = new Fixture(true, true);
        var quick = await fixture.QuickAsync();
        Assert.Equal(40m, quick.Materials);
        Assert.Equal([nameof(IMarketCacheService.GetManyAsync)], fixture.CacheCalls);
    }

    private sealed class Fixture(bool listed, bool timestamped)
    {
        private readonly TradeCompanyPricingScope scope = new("North America", "Aether", MarketFetchScope.SelectedDataCenter);
        public List<string> CacheCalls { get; } = [];

        private UniversalisResponse Response(bool history) => new()
        {
            Listings = listed ? [new() { Quantity = 2, PricePerUnit = 20, WorldName = "Faerie" }] : [],
            LastUploadTimeUnixMilliseconds = timestamped ? 1700000000000 : null,
            AveragePriceNq = history ? 9999 : 0,
            AveragePriceHq = history ? 11111 : 0
        };

        private IMarketCacheService Cache(bool full) => Stub<IMarketCacheService>((method, _) =>
        {
            CacheCalls.Add(method);
            var entries = new Dictionary<(int, string), CachedMarketData>();
            if (full)
            {
                entries[(2, "Aether")] = UniversalisMarketDataMapper.ToCachedMarketData(2, "Aether", Response(true), null, DateTime.UtcNow);
            }
            return method switch
            {
                nameof(IMarketCacheService.GetManyAsync) => Task.FromResult<IReadOnlyDictionary<(int, string), CachedMarketData>>(entries),
                nameof(IMarketCacheService.EnsurePopulatedAsync) when full => Task.FromResult(0),
                _ => throw new InvalidOperationException(method)
            };
        });

        private static TradeMarginCalculator Calculator(IMarketCacheService cache)
        {
            var garland = Stub<IGarlandService>((_, args) => Task.FromResult((int)args[0]! == 1
                ? new GarlandItem { Name = "Craft", Crafts = [new() { Id = "1", Ingredients = [new() { Id = 2, Amount = 2 }] }] }
                : new GarlandItem { Name = "Leaf" }));
            return new(garland, cache, Stub<IVendorCacheService>((_, _) => Task.FromResult<VendorCacheEntry?>(null)), new RecipeResolutionService());
        }

        public async Task<TradeMarginCost> FullAsync()
        {
            var calculator = Calculator(Cache(true));
            return await calculator.CalculateFullAsync(await calculator.PrepareAsync(1, scope), scope, false, default);
        }

        public async Task<TradeMarginCost> QuickAsync()
        {
            var cache = Cache(false);
            var plan = await Calculator(cache).PrepareAsync(1, scope);
            var api = Stub<IUniversalisService>((name, args) => name switch
            {
                nameof(IUniversalisService.GetWorldDataAsync) => Task.FromResult(new WorldData()),
                nameof(IUniversalisService.GetMarketDataBulkAsync) when Equals(args[4], 0) =>
                    Task.FromResult(new Dictionary<int, UniversalisResponse> { [2] = Response(false) }),
                _ => throw new InvalidOperationException(name)
            });
            TradeMarginCost? result = null;
            await new TradeMarginQuickPass(cache, api, NullLogger<TradeMarginQuickPass>.Instance)
                .RunAsync(new Dictionary<int, TradeMarginCalculator.PreparedRow> { [1] = plan }, scope, false,
                    (_, cost) => result = cost, default);
            return Assert.IsType<TradeMarginCost>(result);
        }
    }

    private static T Stub<T>(Func<string, object?[], object> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Call = call;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<string, object?[], object> Call { private get; set; } = null!;
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!.Name, args!);
    }
}
