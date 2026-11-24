using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Margin figures for one production run of <see cref="BatchSize"/>, which makes
/// <see cref="Units"/> units (whole crafts of the sold item). Equality compares the figures
/// only; the <see cref="Basis"/> evidence snapshot used to reprice the row is not part of a
/// cost's value.
/// </summary>
public sealed record TradeMarginCost(
    string Name, int IconId, decimal? Materials, int? Synths,
    DateTime? OldestPriceUtc, IReadOnlyList<string> Missing, string? Issue = null, int Units = 1,
    bool UnknownPriceAge = false)
{
    /// <summary>Evidence this cost was priced from; reprices the row without fetching.</summary>
    public TradeMarginBasis? Basis { get; init; }

    /// <summary>The batch size these figures were priced for.</summary>
    public int BatchSize { get; init; } = 1;

    public bool Equals(TradeMarginCost? other) =>
        other is not null && Name == other.Name && IconId == other.IconId && Materials == other.Materials &&
        Synths == other.Synths && OldestPriceUtc == other.OldestPriceUtc && Missing.SequenceEqual(other.Missing) &&
        Issue == other.Issue && Units == other.Units && UnknownPriceAge == other.UnknownPriceAge;

    public override int GetHashCode() => HashCode.Combine(Name, IconId, Materials, Synths, OldestPriceUtc, Issue, Units, UnknownPriceAge);
}

/// <summary>Company inputs to a Margins price: labor rate and explicit material sources.</summary>
public sealed class TradeMarginPricing
{
    public static TradeMarginPricing None { get; } = new(0, []);

    public TradeMarginPricing(decimal laborGilPerSynth, IEnumerable<TradeMaterialSource> sources)
    {
        LaborGilPerSynth = Math.Max(0, laborGilPerSynth);
        Sources = sources.Where(source => source != null).GroupBy(source => source.ItemId)
            .ToDictionary(group => group.Key, group => group.Last());
    }

    public decimal LaborGilPerSynth { get; }
    public IReadOnlyDictionary<int, TradeMaterialSource> Sources { get; }
}

/// <summary>One route for a material line, priced for the line's whole quantity.</summary>
public sealed record TradeMarginRouteOption(
    TradeMaterialRoute Route, decimal? Total, decimal? Materials = null, int Synths = 0,
    int ListingCount = 0, int ListedQuantity = 0, int RequiredQuantity = 0, DateTime? ObservedAtUtc = null,
    decimal? UnitPrice = null, IReadOnlyList<string>? Missing = null)
{
    public bool IsShortOfListings => Route == TradeMaterialRoute.Market && ListedQuantity < RequiredQuantity;
}

/// <summary>
/// One distinct item of a Margins row with demand above zero. Quantity and Cost are for the
/// whole production run; Cost is labor for a crafted line and the purchase for a bought line.
/// </summary>
public sealed record TradeMarginLine(
    int ItemId, string Name, int IconId, bool IsSoldItem, bool IsCraftable, int Quantity,
    TradeMaterialRoute Route, bool IsExplicit, decimal? UnitCost, decimal? Cost, int Synths,
    TradeMarginRouteOption? Chosen, IReadOnlyList<TradeMarginRouteOption> Options, decimal? DefaultDelta);

public sealed record TradeMarginBreakdown(int Units, IReadOnlyList<TradeMarginLine> Lines);

/// <summary>Read-only acquisition projection for Margins, independent of planner sourcing choices.</summary>
public sealed class TradeMarginCalculator(
    IGarlandService garland, IMarketCacheService market, IVendorCacheService vendors,
    IRecipeResolutionService resolution)
{
    public sealed class PreparedRow
    {
        /// <summary>Every item below the sold item; each needs market and vendor evidence.</summary>
        internal int[] Leaves { get; } = [];
        internal TradeMarginCost? Issue { get; }
        internal Func<MarketEvidenceSet, TradeMarginPricing, CancellationToken, Task<TradeMarginCost>> Price { get; }

        internal PreparedRow(TradeMarginCost issue)
        {
            Issue = issue;
            Price = (_, _, _) => Task.FromResult(issue);
        }

        internal PreparedRow(int[] items, Func<MarketEvidenceSet, TradeMarginPricing, CancellationToken, Task<TradeMarginCost>> price)
        {
            Leaves = items;
            Price = price;
        }
    }

    public async Task<TradeMarginCost> CalculateAsync(
        int itemId, TradeCompanyPricingScope? scope, bool refresh = false, CancellationToken ct = default,
        TradeMarginPricing? pricing = null, int batchSize = 1) =>
        await CalculateFullAsync(await PrepareAsync(itemId, scope, ct, batchSize), scope, refresh, ct, pricing);

    /// <param name="batchSize">Units one production run makes; quantities and listings are evaluated for the whole run.</param>
    public async Task<PreparedRow> PrepareAsync(
        int itemId, TradeCompanyPricingScope? scope, CancellationToken ct = default, int batchSize = 1)
    {
        var batch = MarginTree.ClampBatch(batchSize);
        PreparedRow Issue(TradeMarginCost issue) => new(issue with { BatchSize = batch });
        // Invocation-local recipe and demand state never clears the planner's mutable cache.
        var data = new Dictionary<int, GarlandItem>();
        var edges = new Dictionary<int, MarginRecipe>();
        var active = new HashSet<int>();
        var order = new List<int>();
        var root = await garland.GetItemAsync(itemId, ct);
        if (root == null)
        {
            return Issue(new TradeMarginCost("Item unavailable", 0, null, null, null, [], "Item data unavailable"));
        }

        if (root.Crafts?.Count is not > 0 && root.CompanyCrafts?.Count is not > 0)
        {
            return Issue(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Not craftable"));
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

                edges[id] = new(yield, inputs);
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
            return Issue(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Item data unavailable"));
        }
        catch (InvalidOperationException)
        {
            return Issue(new TradeMarginCost(root.Name, root.IconId, null, null, null, [], "Recipe mismatch"));
        }
        order.Reverse();
        // Cost the whole production run; Units lets callers report per-unit figures.
        var rootYield = edges.TryGetValue(itemId, out var rootRecipe) ? rootRecipe.Yield : 1;
        var tree = new MarginTree(itemId, root, rootYield, data, edges, order);
        if (scope is not { IsValid: true })
        {
            var units = tree.UnitsFor(batch);
            var synths = tree.Propagate(itemId, units, _ => TradeMaterialRoute.Craft).Synths;
            return Issue(new TradeMarginCost(root.Name, root.IconId, null, synths, null, [], "Pricing scope missing", units));
        }

        var items = order.Where(id => id != itemId).ToArray();
        return new(items, async (evidence, pricing, token) =>
        {
            var vendorPrices = new Dictionary<int, decimal>();
            foreach (var id in items)
            {
                var vendor = (await vendors.GetOrFetchAsync(id, token))?.CheapestGilPrice ?? 0;
                if (vendor > 0)
                {
                    vendorPrices[id] = vendor;
                }
            }
            return await new TradeMarginBasis(tree, evidence, vendorPrices, batch).PriceAsync(pricing, token);
        });
    }

    public async Task<TradeMarginCost> CalculateFullAsync(PreparedRow row, TradeCompanyPricingScope? scope, bool refresh,
        CancellationToken ct, TradeMarginPricing? pricing = null)
    {
        if (row.Issue != null)
        {
            return row.Issue;
        }
        ArgumentNullException.ThrowIfNull(scope);
        var evidence = await MarketEvidenceLoader.LoadAsync(market, row.Leaves, scope.MarketFetchScope,
            scope.EffectiveDataCenter, scope.Region, forceRefreshData: refresh, ct: ct);
        return await row.Price(evidence, pricing ?? TradeMarginPricing.None, ct);
    }

    /// <summary>Reprices a row for new company inputs from the evidence it was priced with.</summary>
    public static Task<TradeMarginCost> RepriceAsync(TradeMarginCost cost, TradeMarginPricing pricing, CancellationToken ct = default) =>
        cost.Basis is { } basis ? basis.PriceAsync(pricing, ct) : Task.FromResult(cost);

    /// <summary>Reprices a row for another batch size from the evidence it was priced with.</summary>
    public static Task<TradeMarginCost> RepriceAsync(TradeMarginCost cost, TradeMarginPricing pricing, int batchSize,
        CancellationToken ct = default) =>
        cost.Basis is { } basis ? basis.ForBatch(batchSize).PriceAsync(pricing, ct) : Task.FromResult(cost);

    /// <summary>Per-material lines, route options and override deltas for an expanded row.</summary>
    public static async Task<TradeMarginBreakdown?> DescribeAsync(TradeMarginCost cost, TradeMarginPricing pricing, CancellationToken ct = default) =>
        cost.Basis is { } basis ? await basis.DescribeAsync(pricing, ct) : null;

    private sealed class ItemDataUnavailableException : Exception;
}

internal sealed record MarginRecipe(int Yield, List<(int Id, int Amount)> Inputs);

internal sealed record MarginPropagation(
    decimal? Materials, int Synths, IReadOnlyList<string> Missing,
    IReadOnlyDictionary<int, int> Demand, IReadOnlyDictionary<int, decimal?> BoughtCost);

internal sealed class MarginTree(
    int rootId, GarlandItem root, int rootYield, IReadOnlyDictionary<int, GarlandItem> data,
    IReadOnlyDictionary<int, MarginRecipe> edges, IReadOnlyList<int> order)
{
    public int RootId { get; } = rootId;
    public GarlandItem Root { get; } = root;
    public int RootYield { get; } = rootYield;
    public IReadOnlyDictionary<int, GarlandItem> Data { get; } = data;
    public IReadOnlyDictionary<int, MarginRecipe> Edges { get; } = edges;
    /// <summary>Topological order: every item precedes its inputs.</summary>
    public IReadOnlyList<int> Order { get; } = order;

    public static int ClampBatch(int batchSize) => Math.Clamp(batchSize, 1, TradeMarginItem.MaxBatchSize);

    /// <summary>
    /// Units a run of <paramref name="batchSize"/> makes: whole crafts of the sold item, so a
    /// batch of 1 is one craft, exactly as a row was priced before batch sizes existed.
    /// </summary>
    public int UnitsFor(int batchSize)
    {
        var batch = ClampBatch(batchSize);
        return checked((batch + RootYield - 1) / RootYield * RootYield);
    }

    /// <summary>Demand below <paramref name="start"/> when every craftable item is crafted.</summary>
    public Dictionary<int, int> FullDemand(int start, int quantity) =>
        new(Propagate(start, quantity, _ => TradeMaterialRoute.Craft).Demand);

    /// <summary>
    /// Pushes <paramref name="quantity"/> of <paramref name="start"/> down through crafted
    /// items only; a bought item stops the expansion beneath it.
    /// </summary>
    public MarginPropagation Propagate(int start, int quantity, Func<int, TradeMaterialRoute> route,
        Func<int, TradeMaterialRoute, int, decimal?>? buy = null)
    {
        var demand = new Dictionary<int, int> { [start] = quantity };
        var bought = new Dictionary<int, decimal?>();
        var missing = new List<string>();
        decimal materials = 0;
        var synths = 0;
        foreach (var id in Order)
        {
            var qty = demand.GetValueOrDefault(id);
            if (qty <= 0)
            {
                continue;
            }

            var chosen = id == start ? TradeMaterialRoute.Craft : route(id);
            if (chosen == TradeMaterialRoute.Craft && Edges.TryGetValue(id, out var recipe))
            {
                var crafts = checked((int)(((long)qty + recipe.Yield - 1) / recipe.Yield));
                synths = checked(synths + crafts);
                foreach (var input in recipe.Inputs)
                {
                    demand[input.Id] = checked(demand.GetValueOrDefault(input.Id) + input.Amount * crafts);
                }
                continue;
            }

            var cost = buy?.Invoke(id, chosen, qty);
            bought[id] = cost;
            if (cost is { } value)
            {
                materials += value;
            }
            else
            {
                missing.Add(Data[id].Name);
            }
        }
        return new(missing.Count == 0 ? materials : null, synths, missing, demand, bought);
    }
}

/// <summary>
/// Market listings, vendor prices and the recipe tree one Margins row was priced from.
/// Pricing for new company inputs only re-runs the route evaluation against this snapshot.
/// </summary>
public sealed class TradeMarginBasis
{
    private readonly MarginTree _tree;
    private readonly MarketEvidenceSet _evidence;
    private readonly IReadOnlyDictionary<int, decimal> _vendors;
    private readonly Dictionary<(int ItemId, int Quantity), MarketQuote> _quotes;
    private readonly int _units;

    internal TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors, int batchSize)
        : this(tree, evidence, vendors, batchSize, new())
    {
    }

    private TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors,
        int batchSize, Dictionary<(int ItemId, int Quantity), MarketQuote> quotes)
    {
        _tree = tree;
        _evidence = evidence;
        _vendors = vendors;
        _quotes = quotes;
        BatchSize = MarginTree.ClampBatch(batchSize);
        _units = tree.UnitsFor(BatchSize);
    }

    /// <summary>Units sold from one production run; every quantity is evaluated for the whole run.</summary>
    public int BatchSize { get; }

    /// <summary>The same evidence priced for another batch size; quotes already walked are shared.</summary>
    internal TradeMarginBasis ForBatch(int batchSize) =>
        MarginTree.ClampBatch(batchSize) == BatchSize ? this : new(_tree, _evidence, _vendors, batchSize, _quotes);

    private int RootId => _tree.RootId;

    internal async Task<TradeMarginCost> PriceAsync(TradeMarginPricing pricing, CancellationToken ct)
    {
        var solved = await SolveAsync(pricing.Sources, pricing.LaborGilPerSynth, ct);
        DateTime? oldest = null;
        var unknownAge = false;
        foreach (var (id, cost) in solved.Result.BoughtCost)
        {
            if (cost == null || solved.Routes[id] != TradeMaterialRoute.Market)
            {
                continue;
            }

            var quote = _quotes[(id, solved.Result.Demand[id])];
            unknownAge |= quote.UnknownAge;
            if (quote.Oldest is { } time)
            {
                oldest = oldest == null || time < oldest ? time : oldest;
            }
        }
        return new TradeMarginCost(_tree.Root.Name, _tree.Root.IconId, solved.Result.Materials, solved.Result.Synths,
            unknownAge ? null : oldest, solved.Result.Missing, Units: _units, UnknownPriceAge: unknownAge)
        {
            Basis = this,
            BatchSize = BatchSize
        };
    }

    internal async Task<TradeMarginBreakdown> DescribeAsync(TradeMarginPricing pricing, CancellationToken ct)
    {
        var rate = pricing.LaborGilPerSynth;
        var solved = await SolveAsync(pricing.Sources, rate, ct);
        var total = Total(solved.Result, rate);
        var lines = new List<TradeMarginLine>();
        foreach (var id in DisplayOrder())
        {
            var qty = solved.Result.Demand.GetValueOrDefault(id);
            if (qty <= 0)
            {
                continue;
            }

            var item = _tree.Data[id];
            var craftable = _tree.Edges.TryGetValue(id, out var recipe);
            var route = solved.Routes[id];
            if (id == RootId)
            {
                var rootSynths = Crafts(qty, recipe!);
                lines.Add(new(id, item.Name, item.IconId, true, true, qty, TradeMaterialRoute.Craft, false,
                    null, rootSynths * rate, rootSynths, null, [], null));
                continue;
            }

            var options = await OptionsAsync(id, qty, solved.Routes, pricing.Sources, rate, ct);
            var chosen = options.FirstOrDefault(option => option.Route == route);
            var isExplicit = solved.Explicit.Contains(id);
            decimal? delta = null;
            if (isExplicit && total is { } withChoice)
            {
                var without = pricing.Sources.Where(pair => pair.Key != id).ToDictionary();
                if (Total((await SolveAsync(without, rate, ct)).Result, rate) is { } baseline && baseline != withChoice)
                {
                    delta = withChoice - baseline;
                }
            }

            if (route == TradeMaterialRoute.Craft && craftable)
            {
                var synths = Crafts(qty, recipe!);
                lines.Add(new(id, item.Name, item.IconId, false, true, qty, route, isExplicit, null,
                    synths * rate, synths, chosen, options, delta));
            }
            else
            {
                var cost = solved.Result.BoughtCost.GetValueOrDefault(id);
                lines.Add(new(id, item.Name, item.IconId, false, craftable, qty, route, isExplicit,
                    cost / qty, cost, 0, chosen, options, delta));
            }
        }
        return new(_units, lines);
    }

    private static decimal? Total(MarginPropagation result, decimal rate) =>
        result.Materials is { } materials ? materials + result.Synths * rate : null;

    private static int Crafts(int quantity, MarginRecipe recipe) =>
        checked((int)(((long)quantity + recipe.Yield - 1) / recipe.Yield));

    private sealed record Solved(
        MarginPropagation Result, IReadOnlyDictionary<int, TradeMaterialRoute> Routes, IReadOnlySet<int> Explicit);

    /// <summary>
    /// Evaluation order, chosen so the result is deterministic and stable:
    /// 1. Decision quantities are the fully crafted tree's demand for each material. Unit cost
    ///    depends on quantity (price ladder) while quantity depends on parent routes; fixing the
    ///    decision quantity breaks that cycle the same way the commission planner does, whose
    ///    nodes keep their full-tree quantities while it chooses sources.
    /// 2. Routes are decided bottom-up (inputs before the items made from them) at those
    ///    quantities through <see cref="AcquisitionDefaultRule"/>, so a craft candidate prices its
    ///    inputs on their already decided routes. Explicit company choices always win.
    /// 3. Actual demand is then propagated top-down through crafted items only, and each
    ///    bought line is priced at its actual quantity.
    /// </summary>
    private async Task<Solved> SolveAsync(IReadOnlyDictionary<int, TradeMaterialSource> sources, decimal rate, CancellationToken ct)
    {
        var full = _tree.FullDemand(RootId, _units);
        var routes = new Dictionary<int, TradeMaterialRoute> { [RootId] = TradeMaterialRoute.Craft };
        var explicitChoices = new HashSet<int>();
        var unit = new Dictionary<int, decimal?>();
        for (var index = _tree.Order.Count - 1; index >= 0; index--)
        {
            var id = _tree.Order[index];
            if (id == RootId || !full.TryGetValue(id, out var qty) || qty <= 0)
            {
                continue;
            }

            var craftable = _tree.Edges.TryGetValue(id, out var recipe);
            decimal? craft = null;
            if (craftable)
            {
                var crafts = Crafts(qty, recipe!);
                decimal? inputs = 0;
                foreach (var input in recipe!.Inputs)
                {
                    inputs = unit.GetValueOrDefault(input.Id) is { } inputUnit && inputs is { } sum
                        ? sum + inputUnit * input.Amount * crafts : null;
                }
                craft = AcquisitionDefaultRule.CraftCost(inputs, crafts, rate);
            }

            TradeMaterialRoute route;
            if (sources.TryGetValue(id, out var choice) && IsUsable(choice, craftable))
            {
                route = choice.Route;
                explicitChoices.Add(id);
            }
            else
            {
                var quote = await QuoteAsync(id, qty, ct);
                var best = AcquisitionDefaultRule.Cheapest(
                [
                    (AcquisitionSource.Craft, craft),
                    (AcquisitionSource.MarketBuyNq, quote.Listed >= qty ? quote.Unit * qty : null),
                    (AcquisitionSource.VendorBuy, _vendors.TryGetValue(id, out var vendor) ? vendor * qty : (decimal?)null)
                ]);
                route = best switch
                {
                    AcquisitionSource.VendorBuy => TradeMaterialRoute.Vendor,
                    AcquisitionSource.MarketBuyNq => TradeMaterialRoute.Market,
                    AcquisitionSource.Craft => TradeMaterialRoute.Craft,
                    _ => craftable ? TradeMaterialRoute.Craft : TradeMaterialRoute.Market
                };
            }

            routes[id] = route;
            unit[id] = route == TradeMaterialRoute.Craft ? craft / qty : await BuyAsync(id, route, qty, sources, ct) / qty;
        }

        // Every bought line is priced before propagation so Propagate stays synchronous.
        var actual = _tree.Propagate(RootId, _units, id => routes.GetValueOrDefault(id, TradeMaterialRoute.Market));
        var prices = new Dictionary<int, decimal?>();
        foreach (var (id, qty) in actual.Demand)
        {
            if (id != RootId && qty > 0 && !(routes[id] == TradeMaterialRoute.Craft && _tree.Edges.ContainsKey(id)))
            {
                prices[id] = await BuyAsync(id, routes[id], qty, sources, ct);
            }
        }
        var result = _tree.Propagate(RootId, _units, id => routes.GetValueOrDefault(id, TradeMaterialRoute.Market),
            (id, _, _) => prices.GetValueOrDefault(id));
        return new(result, routes, explicitChoices);
    }

    private static bool IsUsable(TradeMaterialSource choice, bool craftable) => choice.Route switch
    {
        TradeMaterialRoute.Craft => craftable,
        TradeMaterialRoute.SetPrice => choice.UnitPrice is >= 0,
        TradeMaterialRoute.Market or TradeMaterialRoute.Vendor => true,
        _ => false
    };

    private async Task<decimal?> BuyAsync(int id, TradeMaterialRoute route, int qty,
        IReadOnlyDictionary<int, TradeMaterialSource> sources, CancellationToken ct) => route switch
        {
            TradeMaterialRoute.Market => (await QuoteAsync(id, qty, ct)).Unit * qty,
            TradeMaterialRoute.Vendor => _vendors.TryGetValue(id, out var vendor) ? vendor * qty : null,
            TradeMaterialRoute.SetPrice => sources.GetValueOrDefault(id)?.UnitPrice * (decimal)qty,
            _ => null
        };

    private async Task<IReadOnlyList<TradeMarginRouteOption>> OptionsAsync(int id, int qty,
        IReadOnlyDictionary<int, TradeMaterialRoute> routes, IReadOnlyDictionary<int, TradeMaterialSource> sources,
        decimal rate, CancellationToken ct)
    {
        var options = new List<TradeMarginRouteOption>();
        if (_tree.Edges.ContainsKey(id))
        {
            // Craft this item, everything beneath it on its current route.
            var below = _tree.Propagate(id, qty, child => routes.GetValueOrDefault(child, TradeMaterialRoute.Market));
            var prices = new Dictionary<int, decimal?>();
            foreach (var (child, childQty) in below.Demand)
            {
                if (child != id && childQty > 0 && !(routes[child] == TradeMaterialRoute.Craft && _tree.Edges.ContainsKey(child)))
                {
                    prices[child] = await BuyAsync(child, routes[child], childQty, sources, ct);
                }
            }
            var craft = _tree.Propagate(id, qty, child => routes.GetValueOrDefault(child, TradeMaterialRoute.Market),
                (child, _, _) => prices.GetValueOrDefault(child));
            options.Add(new(TradeMaterialRoute.Craft, Total(craft, rate), craft.Materials, craft.Synths, Missing: craft.Missing));
        }

        var quote = await QuoteAsync(id, qty, ct);
        options.Add(new(TradeMaterialRoute.Market, quote.Unit * qty, ListingCount: quote.ListingCount,
            ListedQuantity: quote.Listed, RequiredQuantity: qty, ObservedAtUtc: quote.Oldest, UnitPrice: quote.Unit));
        if (_vendors.TryGetValue(id, out var vendor))
        {
            options.Add(new(TradeMaterialRoute.Vendor, vendor * qty, UnitPrice: vendor));
        }

        var set = sources.GetValueOrDefault(id) is { Route: TradeMaterialRoute.SetPrice, UnitPrice: { } price } ? (decimal?)price : null;
        options.Add(new(TradeMaterialRoute.SetPrice, set * qty, UnitPrice: set));
        return options;
    }

    /// <summary>Breadth-first from the sold item, so lines read in recipe order.</summary>
    private IEnumerable<int> DisplayOrder()
    {
        var seen = new HashSet<int> { RootId };
        var queue = new Queue<int>([RootId]);
        while (queue.TryDequeue(out var id))
        {
            yield return id;
            if (!_tree.Edges.TryGetValue(id, out var recipe))
            {
                continue;
            }

            foreach (var input in recipe.Inputs.Where(input => seen.Add(input.Id)))
            {
                queue.Enqueue(input.Id);
            }
        }
    }

    private sealed record MarketQuote(decimal? Unit, int Listed, int ListingCount, DateTime? Oldest, bool UnknownAge);

    /// <summary>
    /// The market route for <paramref name="quantity"/> units. Listings that cannot cover the
    /// whole quantity still give a directional unit price, so a material with no other route
    /// keeps a figure; such a route is never chosen as a default over another priced route.
    /// </summary>
    private async Task<MarketQuote> QuoteAsync(int id, int quantity, CancellationToken ct)
    {
        if (_quotes.TryGetValue((id, quantity), out var cached))
        {
            return cached;
        }

        var entries = _evidence.GetEntriesForItem(id);
        var listings = entries.SelectMany(entry => entry.Worlds).SelectMany(world => world.Listings)
            .Where(listing => listing.Quantity > 0).ToArray();
        var listed = (int)Math.Min(int.MaxValue, listings.Sum(listing => (long)listing.Quantity));
        decimal? unit = null;
        DateTime? oldest = null;
        var unknownAge = false;
        if (listings.Length > 0)
        {
            var analysis = (await new MarketPriceLadderAnalysisService().AnalyzeAsync(new MarketAnalysisRequest
            {
                Evidence = _evidence,
                Items = [new MaterialAggregate { ItemId = id, Name = _tree.Data[id].Name, TotalQuantity = quantity }]
            }, ct: ct)).Single(entry => entry.ItemId == id);
            var price = CommissionCostBasisResolver.GetReferenceUnitCost(analysis);
            if (price > 0)
            {
                unit = price;
                var worlds = entries.SelectMany(entry => entry.Worlds.Select(world => (entry, world)));
                if (analysis.CostToCoverUnitPrice > 0)
                {
                    // Margins is per unit sold across repeated production, so a purchase costs what
                    // each unit in the bought listings costs. Charging the whole listing to the
                    // units used makes a 999-stack of shards the price of five.
                    var selected = analysis.Worlds.Where(world => world.ActionableCostToCoverTotalGil > 0 && world.ActionableAverageUnitPrice > 0)
                        .OrderBy(world => world.ActionableAverageUnitPrice).ThenBy(world => world.ActionableCostToCoverMaxUnitPrice)
                        .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase).ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (selected == null)
                    {
                        selected = analysis.Worlds.Where(world => world.ActionableCostToCoverTotalGil > 0 && world.ActionableCostToCoverUnitPrice > 0)
                            .OrderBy(world => world.ActionableCostToCoverTotalGil).ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase).First();
                    }
                    else
                    {
                        unit = selected.ActionableAverageUnitPrice;
                    }
                    worlds = worlds.Where(pair => pair.entry.DataCenter == selected.DataCenter && pair.world.WorldName == selected.WorldName);
                }
                var contributing = worlds.Where(pair => pair.world.Listings.Count > 0).ToArray();
                unknownAge = contributing.Length == 0;
                foreach (var (entry, world) in contributing)
                {
                    var stamp = world.LastUploadTimeUnixMilliseconds ?? entry.LastUploadTimeUnixMilliseconds;
                    if (stamp is not > 0) { unknownAge = true; continue; }
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime;
                    oldest = oldest == null || time < oldest ? time : oldest;
                }
            }
        }

        var quote = new MarketQuote(unit, listed, listings.Length, oldest, unknownAge);
        _quotes[(id, quantity)] = quote;
        return quote;
    }
}
