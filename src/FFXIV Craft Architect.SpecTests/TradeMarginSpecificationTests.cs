using System.Reflection;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class TradeMarginSpecificationTests
{
    [Fact]
    public async Task SharedYieldIsRoundedOnceAndMissingLeafNeverBecomesZero()
    {
        static GarlandItem Craft(int id, int yield, params (int Id, int Amount)[] inputs) => new()
        {
            Id = id,
            Name = $"Item {id}",
            Crafts = [new() { Id = id.ToString(), Yield = yield, Ingredients = inputs.Select(i => new GarlandIngredient { Id = i.Id, Amount = i.Amount }).ToList() }]
        };
        // Root -> two branches -> 1 + 1 shared units, yield 3 -> one synth -> 2 vendor leaves.
        // Root also needs one missing leaf. Total synths: root + branches + shared = 4, not 5.
        var items = new Dictionary<int, GarlandItem>
        {
            [1] = Craft(1, 1, (2, 1), (3, 1), (6, 1)),
            [2] = Craft(2, 1, (4, 1)),
            [3] = Craft(3, 1, (4, 1)),
            [4] = Craft(4, 3, (5, 2)),
            [5] = new() { Id = 5, Name = "Vendor leaf" },
            [6] = new() { Id = 6, Name = "Missing leaf" }
        };
        var garland = Proxy<IGarlandService>.Create((method, args) =>
            method == "GetItemAsync" ? Task.FromResult<GarlandItem?>(items[(int)args[0]!]) : throw new NotSupportedException(method));
        var vendor = Proxy<IVendorCacheService>.Create((method, args) =>
            method == "GetOrFetchAsync" ? Task.FromResult<VendorCacheEntry?>((int)args[0]! == 5 ? new(5, [], 7, DateTime.UtcNow) : null) : throw new NotSupportedException(method));
        var market = Proxy<IMarketCacheService>.Create((method, args) => method switch
        {
            "EnsurePopulatedAsync" => Task.FromResult(0),
            "GetManyAsync" => Task.FromResult<IReadOnlyDictionary<(int, string), CachedMarketData>>(new Dictionary<(int, string), CachedMarketData>()),
            _ => throw new NotSupportedException(method)
        });
        var calculator = new TradeMarginCalculator(garland, market, vendor, new RecipeResolutionService());
        var result = await calculator.CalculateAsync(1, new("North America", "Aether"));
        Assert.Equal(4, result.Synths);
        Assert.Null(result.Materials);
        Assert.Equal(["Missing leaf"], result.Missing);
        // Remove only the unpriced root input; the remaining two raw units cost 14 gil.
        items[1].Crafts![0].Ingredients.RemoveAll(input => input.Id == 6);
        result = await calculator.CalculateAsync(1, new("North America", "Aether"));
        Assert.Equal(4, result.Synths);
        Assert.Equal(14m, result.Materials);
        Assert.Empty(result.Missing);
    }

    public class Proxy<T> : DispatchProxy where T : class
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        public static T Create(Func<string, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Proxy<T>>();
            ((Proxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args!);
    }
}
