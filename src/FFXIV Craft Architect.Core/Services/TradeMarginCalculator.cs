using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record TradeMarginCost(
    string Name, int IconId, decimal? Materials, int? Synths,
    DateTime? OldestPriceUtc, IReadOnlyList<string> Missing, string? Issue = null, int Units = 1,
    bool UnknownPriceAge = false);

/// <summary>Read-only full-tree commissioning projection, independent of planner sourcing choices.</summary>
public sealed class TradeMarginCalculator(
    IGarlandService garland, IMarketCacheService market, IVendorCacheService vendors,
    IRecipeResolutionService resolution)
{
    public sealed class PreparedRow
    {
        internal int[] Leaves { get; } = [];
        internal TradeMarginCost? Issue { get; }
        internal Func<MarketEvidenceSet, CancellationToken, Task<TradeMarginCost>> Price { get; }

        internal PreparedRow(TradeMarginCost issue)
        {
            Issue = issue;
            Price = (_, _) => Task.FromResult(issue);
        }

        internal PreparedRow(int[] leaves, Func<MarketEvidenceSet, CancellationToken, Task<TradeMarginCost>> price)
        {
            Leaves = leaves;
            Price = price;
        }
    }

    public async Task<TradeMarginCost> CalculateAsync(
        int itemId, TradeCompanyPricingScope? scope, bool refresh = false, CancellationToken ct = default) =>
        await CalculateFullAsync(await PrepareAsync(itemId, scope, ct), scope, refresh, ct);

    public async Task<PreparedRow> PrepareAsync(
        int itemId, TradeCompanyPricingScope? scope, CancellationToken ct = default)
    {
        // Invocation-local recipe and demand state never clears the planner's mutable cache.
        var data = new Dictionary<int, GarlandItem>();
        var edges = new Dictionary<int, (int Yield, List<(int Id, int Amount)> Inputs)>();
        var active = new HashSet<int>();
        var order = new List<int>();
        var root = await garland.GetItemAsync(itemId, ct);
        if (root == null)
        {
            return new(new TradeMarginCost("Item unavailable", 0, null, null, null, [], "Item data unavailable"));
        }

        if (root.Crafts?.Count is not > 0 && root.CompanyCrafts?.Count is not > 0)
        {
            return new(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Not craftable"));
        }

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
            var item = (id == itemId ? root : await garland.GetItemAsync(id, ct))
                ?? throw new ItemDataUnavailableException();
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
        try { await Discover(itemId); }
        catch (ItemDataUnavailableException)
        {
            return new(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Item data unavailable"));
        }
        catch (InvalidOperationException)
        {
            return new(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Recipe mismatch"));
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
            return new(new TradeMarginCost(root.Name, root.IconId, null, synths, null, [], "Pricing scope missing", units));
        }

        var leaves = order.Where(id => !edges.ContainsKey(id)).ToArray();
        var items = leaves.Select(id => new MaterialAggregate { ItemId = id, Name = data[id].Name, TotalQuantity = demand[id] }).ToArray();
        return new(leaves, (evidence, token) => PriceAsync(root, units, synths, items, evidence, token));
    }

    public async Task<TradeMarginCost> CalculateFullAsync(PreparedRow row, TradeCompanyPricingScope? scope, bool refresh, CancellationToken ct)
    {
        if (row.Issue != null)
        {
            return row.Issue;
        }
        ArgumentNullException.ThrowIfNull(scope);
        var evidence = await MarketEvidenceLoader.LoadAsync(market, row.Leaves, scope.MarketFetchScope,
            scope.EffectiveDataCenter, scope.Region, forceRefreshData: refresh, ct: ct);
        return await row.Price(evidence, ct);
    }

    private async Task<TradeMarginCost> PriceAsync(GarlandItem root, int units, int synths,
        MaterialAggregate[] items, MarketEvidenceSet evidence, CancellationToken ct)
    {
        var analyses = await new MarketPriceLadderAnalysisService().AnalyzeAsync(new MarketAnalysisRequest
        {
            Evidence = evidence,
            Items = items
        }, ct: ct);
        decimal materials = 0;
        DateTime? oldest = null;
        var unknownAge = false;
        var missing = new List<string>();
        foreach (var item in items)
        {
            var id = item.ItemId;
            var analysis = analyses.Single(entry => entry.ItemId == id);
            var marketPrice = CommissionCostBasisResolver.GetReferenceUnitCost(analysis);
            var vendor = (await vendors.GetOrFetchAsync(id, ct))?.CheapestGilPrice ?? 0;
            var price = vendor > 0 && (marketPrice <= 0 || vendor <= marketPrice) ? vendor : marketPrice;
            if (price <= 0) { missing.Add(item.Name); continue; }
            materials += price * item.TotalQuantity;
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
            var contributingWorlds = worlds.Where(pair => pair.world.Listings.Count > 0).ToArray();
            if (contributingWorlds.Length == 0)
            {
                unknownAge = true;
            }

            foreach (var (entry, world) in contributingWorlds)
            {
                var stamp = world.LastUploadTimeUnixMilliseconds ?? entry.LastUploadTimeUnixMilliseconds;
                if (stamp is not > 0) { unknownAge = true; continue; }
                var time = DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime;
                oldest = oldest == null || time < oldest ? time : oldest;
            }
        }
        return new(root.Name, root.IconId, missing.Count == 0 ? materials : null, synths,
            unknownAge ? null : oldest, missing, Units: units, UnknownPriceAge: unknownAge);
    }

    private sealed class ItemDataUnavailableException : Exception;
}
