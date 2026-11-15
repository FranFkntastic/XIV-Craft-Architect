using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record TradeMarginCost(
    string Name, int IconId, decimal? Materials, int? Synths,
    DateTime? OldestPriceUtc, IReadOnlyList<string> Missing, string? Issue = null, int Units = 1);

/// <summary>Read-only full-tree commissioning projection, independent of planner sourcing choices.</summary>
public sealed class TradeMarginCalculator(
    IGarlandService garland, IMarketCacheService market, IVendorCacheService vendors,
    IRecipeResolutionService resolution)
{
    public async Task<TradeMarginCost> CalculateAsync(
        int itemId, TradeCompanyPricingScope? scope, bool refresh = false, CancellationToken ct = default)
    {
        // Invocation-local recipe and demand state never clears the planner's mutable cache.
        var data = new Dictionary<int, GarlandItem>();
        var edges = new Dictionary<int, (int Yield, List<(int Id, int Amount)> Inputs)>();
        var active = new HashSet<int>();
        var order = new List<int>();
        async Task Discover(int id)
        {
            if (active.Contains(id))
            {
                throw new InvalidOperationException("Recipe cycle.");
            }

            if (data.ContainsKey(id))
            {
                return;
            }

            active.Add(id);
            var item = await garland.GetItemAsync(id, ct)
                ?? throw new InvalidOperationException("Recipe item data is unavailable.");
            data[id] = item;
            if (item.Crafts?.Count > 0 || item.CompanyCrafts?.Count > 0)
            {
                var recipe = resolution.Resolve(new PlanNode { ItemId = id, Name = item.Name, Quantity = 1 }, item);
                var workshop = item.Crafts?.FirstOrDefault(craft => craft.Id?.StartsWith("fc", StringComparison.Ordinal) == true);
                var inputs = workshop != null ? workshop.Ingredients.Select(input => (input.Id, input.Amount)).ToList()
                    : recipe.CompanyCraft is { } company
                    ? company.Phases.SelectMany(phase => phase.Items).Select(input => (input.Id, input.Amount)).ToList()
                    : recipe.StandardRecipe?.Ingredients.Select(input => (input.Id, input.Amount)).ToList();
                var yield = workshop == null ? recipe.Yield : 1;
                if (inputs is not { Count: > 0 } || yield <= 0 || inputs.Any(input => input.Amount <= 0))
                {
                    throw new InvalidOperationException("Recipe structure is incomplete.");
                }

                edges[id] = (yield, inputs);
                foreach (var input in inputs)
                {
                    await Discover(input.Id);
                }
            }
            active.Remove(id);
            order.Add(id);
        }
        var root = await garland.GetItemAsync(itemId, ct)
            ?? throw new InvalidOperationException("Item data is unavailable.");
        try { await Discover(itemId); }
        catch (InvalidOperationException)
        {
            return new(root.Name, root.IconId, null, null, null, [], "Recipe mismatch");
        }
        order.Reverse();
        // Cost one full root batch; Units lets callers report per-unit figures.
        var units = edges.TryGetValue(itemId, out var rootRecipe) ? rootRecipe.Yield : 1;
        var demand = new Dictionary<int, int> { [itemId] = units };
        var synths = 0;
        foreach (var id in order)
        {
            if (!edges.TryGetValue(id, out var recipe))
            {
                continue;
            }

            var crafts = checked((int)(((long)demand[id] + recipe.Yield - 1) / recipe.Yield));
            synths = checked(synths + crafts);
            foreach (var input in recipe.Inputs)
            {
                demand[input.Id] = checked(demand.GetValueOrDefault(input.Id) + input.Amount * crafts);
            }
        }
        if (scope is not { IsValid: true })
        {
            return new(root.Name, root.IconId, null, synths, null, [], "Pricing scope missing", units);
        }

        var leaves = order.Where(id => !edges.ContainsKey(id)).ToArray();
        var evidence = await MarketEvidenceLoader.LoadAsync(market, leaves, scope.MarketFetchScope,
            scope.EffectiveDataCenter, scope.Region, forceRefreshData: refresh, ct: ct);
        var analyses = await new MarketPriceLadderAnalysisService().AnalyzeAsync(new MarketAnalysisRequest
        {
            Evidence = evidence,
            Items = leaves.Select(id => new MaterialAggregate { ItemId = id, Name = data[id].Name, TotalQuantity = demand[id] }).ToArray()
        }, ct: ct);
        decimal materials = 0;
        DateTime? oldest = null;
        var unknownAge = false;
        var missing = new List<string>();
        foreach (var id in leaves)
        {
            var analysis = analyses.Single(entry => entry.ItemId == id);
            var marketPrice = CommissionCostBasisResolver.GetReferenceUnitCost(analysis);
            var vendor = (await vendors.GetOrFetchAsync(id, ct))?.CheapestGilPrice ?? 0;
            var price = vendor > 0 && (marketPrice <= 0 || vendor <= marketPrice) ? vendor : marketPrice;
            if (price <= 0) { missing.Add(data[id].Name); continue; }
            materials += price * demand[id];
            if (price == vendor && vendor > 0)
            {
                continue;
            }

            var worlds = evidence.GetEntriesForItem(id).SelectMany(entry => entry.Worlds.Select(world => (entry, world)));
            if (analysis.CostToCoverUnitPrice > 0)
            {
                var selected = analysis.Worlds.Where(world => world.ActionableCostToCoverTotalGil > 0 && world.ActionableCostToCoverUnitPrice > 0)
                    .OrderBy(world => world.ActionableCostToCoverTotalGil).ThenBy(world => world.ActionableCostToCoverMaxUnitPrice)
                    .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase).ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase).First();
                worlds = worlds.Where(pair => pair.entry.DataCenter == selected.DataCenter && pair.world.WorldName == selected.WorldName);
            }
            foreach (var (entry, world) in worlds.Where(pair => pair.world.Listings.Count > 0))
            {
                var stamp = world.LastUploadTimeUnixMilliseconds ?? entry.LastUploadTimeUnixMilliseconds;
                if (stamp is not > 0) { unknownAge = true; continue; }
                var time = DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime;
                oldest = oldest == null || time < oldest ? time : oldest;
            }
        }
        return new(root.Name, root.IconId, missing.Count == 0 ? materials : null, synths,
            unknownAge ? null : oldest, missing, Units: units);
    }
}
