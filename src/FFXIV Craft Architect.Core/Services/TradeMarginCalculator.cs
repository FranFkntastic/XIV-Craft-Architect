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

    /// <summary>
    /// Buying the whole run of the item itself on the market (whole-listing executable cost for
    /// <see cref="Units"/>); only for rows prepared with the root market included.
    /// </summary>
    public TradeMarginRouteOption? MarketBuy { get; init; }

    /// <summary>The sold item's own cheapest NPC vendor gil price per unit; null when no vendor sells it.</summary>
    public decimal? RootVendorPrice { get; init; }

    public bool Equals(TradeMarginCost? other) =>
        other is not null && Name == other.Name && IconId == other.IconId && Materials == other.Materials &&
        Synths == other.Synths && OldestPriceUtc == other.OldestPriceUtc && Missing.SequenceEqual(other.Missing) &&
        Issue == other.Issue && Units == other.Units && UnknownPriceAge == other.UnknownPriceAge &&
        Equals(MarketBuy, other.MarketBuy) && RootVendorPrice == other.RootVendorPrice;

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(Name, IconId, Materials, Synths, OldestPriceUtc, Issue, Units, UnknownPriceAge),
            MarketBuy, RootVendorPrice);
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

/// <summary>
/// One route for a material line, priced for the line's whole quantity. A
/// <see cref="TradeMaterialRoute.MarketVendor"/> route buys <see cref="VendorQuantity"/> of it
/// from the NPC vendor and the rest from listings below the vendor price.
/// </summary>
public sealed record TradeMarginRouteOption(
    TradeMaterialRoute Route, decimal? Total, decimal? Materials = null, int Synths = 0,
    int ListingCount = 0, int ListedQuantity = 0, int RequiredQuantity = 0, DateTime? ObservedAtUtc = null,
    decimal? UnitPrice = null, IReadOnlyList<string>? Missing = null, string? WorldName = null, string? DataCenter = null,
    int VendorQuantity = 0)
{
    public bool IsShortOfListings => Route == TradeMaterialRoute.Market && ListedQuantity < RequiredQuantity;

    /// <summary>Units bought from listings; the rest of <see cref="RequiredQuantity"/> comes from the vendor.</summary>
    public int MarketQuantity => RequiredQuantity - VendorQuantity;

    /// <summary>The one world a market price was taken from, as <c>World (Data center)</c>; null when none was selected.</summary>
    public string? BuyAt => string.IsNullOrWhiteSpace(WorldName) ? null
        : string.IsNullOrWhiteSpace(DataCenter) ? WorldName : $"{WorldName} ({DataCenter})";
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
        /// <summary>
        /// Every item below the sold item, each needing market and vendor evidence, plus the sold
        /// item itself when its own market price is part of the row.
        /// </summary>
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

    /// <param name="batchSize">Units one production run makes; quantities and listings are evaluated for the whole run.</param>
    /// <param name="includeRootMarket">
    /// Also price buying the whole run of the item itself on the market
    /// (<see cref="TradeMarginCost.MarketBuy"/>); its market data is fetched with the materials'.
    /// </param>
    public async Task<PreparedRow> PrepareAsync(
        int itemId, TradeMarginMarketScope? scope, CancellationToken ct = default, int batchSize = 1,
        bool includeRootMarket = false)
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
        if (scope is null)
        {
            var units = tree.UnitsFor(batch);
            var synths = tree.Propagate(itemId, units, _ => TradeMaterialRoute.Craft).Synths;
            return Issue(new TradeMarginCost(root.Name, root.IconId, null, synths, null, [], "Pricing scope missing", units));
        }

        var items = order.Where(id => id != itemId).ToArray();
        var leaves = includeRootMarket ? items.Append(itemId).ToArray() : items;
        return new(leaves, async (evidence, pricing, token) =>
        {
            var vendorPrices = new Dictionary<int, decimal>();
            // The sold item's own vendor price sits beside its materials'; routes never buy the root.
            foreach (var id in items.Append(itemId))
            {
                var vendor = (await vendors.GetOrFetchAsync(id, token))?.CheapestGilPrice ?? 0;
                if (vendor > 0)
                {
                    vendorPrices[id] = vendor;
                }
            }
            return await new TradeMarginBasis(tree, evidence, vendorPrices, batch, includeRootMarket).PriceAsync(pricing, token);
        });
    }

    // Each stored entry holds every listing in a data center, so one read of many entries is
    // seconds of deserialization. Small slices keep each main-thread task short.
    private const int StoredReadSlice = 16;

    /// <summary>Every item/data-center pair a row's materials are priced from.</summary>
    public static IReadOnlyList<(int itemId, string dataCenter)> Pairs(PreparedRow row, TradeMarginMarketScope? scope)
    {
        if (row.Issue != null || scope is null)
        {
            return [];
        }

        var centers = scope.DataCenters;
        return row.Leaves.SelectMany(id => centers.Select(dataCenter => (id, dataCenter))).ToArray();
    }

    /// <summary>
    /// True when the row can be priced from <paramref name="stored"/>: it carries an issue, or every
    /// material has stored market data in every data center of <paramref name="scope"/>. Data
    /// covering only part of the scope (e.g. just after the scope widened) would price the row as
    /// if the scope were narrower.
    /// </summary>
    public static bool HasMarketData(PreparedRow row, TradeMarginMarketScope? scope,
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> stored)
    {
        if (row.Issue != null)
        {
            return true;
        }

        return scope is not null && Pairs(row, scope).All(stored.ContainsKey);
    }

    /// <summary>
    /// Market data the browser holds for <paramref name="pairs"/>, whatever its age within retention.
    /// Each entry keeps its fetch time; the row's price age comes from its listings' upload times.
    /// </summary>
    public async Task<Dictionary<(int itemId, string dataCenter), CachedMarketData>> ReadStoredAsync(
        IEnumerable<(int itemId, string dataCenter)> pairs, CancellationToken ct = default)
    {
        var stored = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        foreach (var slice in pairs.Distinct().Chunk(StoredReadSlice))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (pair, entry) in await market.GetManyAsync(slice, MarketEvidencePolicyDefaults.RetainedCacheMaxAge))
            {
                stored[pair] = entry;
            }
            await MainThreadYield.Async(ct);
        }
        return stored;
    }

    /// <summary>Refetches exactly <paramref name="pairs"/>, once each, and returns what was stored.</summary>
    public async Task<MarketPairRefreshResult> FetchAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> pairs, CancellationToken ct = default)
    {
        if (market is IMarketCachePairRefresher refresher)
        {
            return await refresher.RefreshPairsAsync(pairs, ct);
        }

        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await market.RefreshRequestedAsync(pairs.ToList(), progress: null, ct);
        var stored = (await market.GetManyAsync(pairs, MarketEvidencePolicyDefaults.RetainedCacheMaxAge))
            .Where(entry => entry.Value.FetchedAtUnix >= started)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        return new(stored, pairs.Where(pair => !stored.ContainsKey(pair)).ToArray(), new Dictionary<string, string>());
    }

    /// <summary>Prices a prepared row from <paramref name="stored"/> market data, without fetching.</summary>
    public static Task<TradeMarginCost> PriceAsync(PreparedRow row, TradeMarginMarketScope? scope,
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> stored, TradeMarginPricing? pricing,
        CancellationToken ct = default)
    {
        if (row.Issue != null)
        {
            return Task.FromResult(row.Issue);
        }

        ArgumentNullException.ThrowIfNull(scope);
        var pairs = Pairs(row, scope);
        var entries = pairs.Where(stored.ContainsKey).ToDictionary(pair => pair, pair => stored[pair]);
        var evidence = new MarketEvidenceSet(entries, pairs, scope.FetchScope, scope.DataCenters,
            scope.SelectedDataCenter, scope.Region, maxAge: null, fetchedCount: 0, DateTime.UtcNow);
        return row.Price(evidence, pricing ?? TradeMarginPricing.None, ct);
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
    private readonly bool _includeRootMarket;

    internal TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors, int batchSize,
        bool includeRootMarket = false)
        : this(tree, evidence, vendors, batchSize, new(), includeRootMarket)
    {
    }

    private TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors,
        int batchSize, Dictionary<(int ItemId, int Quantity), MarketQuote> quotes, bool includeRootMarket)
    {
        _tree = tree;
        _evidence = evidence;
        _vendors = vendors;
        _quotes = quotes;
        _includeRootMarket = includeRootMarket;
        BatchSize = MarginTree.ClampBatch(batchSize);
        _units = tree.UnitsFor(BatchSize);
    }

    /// <summary>Units sold from one production run; every quantity is evaluated for the whole run.</summary>
    public int BatchSize { get; }

    /// <summary>The same evidence priced for another batch size; quotes already walked are shared.</summary>
    internal TradeMarginBasis ForBatch(int batchSize) =>
        MarginTree.ClampBatch(batchSize) == BatchSize ? this : new(_tree, _evidence, _vendors, batchSize, _quotes, _includeRootMarket);

    private int RootId => _tree.RootId;

    internal async Task<TradeMarginCost> PriceAsync(TradeMarginPricing pricing, CancellationToken ct)
    {
        var solved = await SolveAsync(pricing.Sources, pricing.LaborGilPerSynth, ct);
        DateTime? oldest = null;
        var unknownAge = false;
        foreach (var (id, cost) in solved.Result.BoughtCost)
        {
            var route = solved.Routes[id];
            if (cost == null || route is not (TradeMaterialRoute.Market or TradeMaterialRoute.MarketVendor))
            {
                continue;
            }

            var quote = _quotes[(id, solved.Result.Demand[id])];
            // The vendor part of a Market + vendor buy has no age; only its listings do.
            var (listingsOldest, listingsUnknown) = route == TradeMaterialRoute.Market ? (quote.Oldest, quote.UnknownAge)
                : quote.Blend is { Buy.MarketQuantity: > 0 } blend ? (blend.Oldest, blend.UnknownAge) : (null, false);
            unknownAge |= listingsUnknown;
            if (listingsOldest is { } time)
            {
                oldest = oldest == null || time < oldest ? time : oldest;
            }
        }
        return new TradeMarginCost(_tree.Root.Name, _tree.Root.IconId, solved.Result.Materials, solved.Result.Synths,
            unknownAge ? null : oldest, solved.Result.Missing, Units: _units, UnknownPriceAge: unknownAge)
        {
            Basis = this,
            BatchSize = BatchSize,
            MarketBuy = _includeRootMarket ? await MarketOptionAsync(RootId, _units, ct) : null,
            RootVendorPrice = _vendors.TryGetValue(RootId, out var rootVendor) && rootVendor > 0 ? rootVendor : null
        };
    }

    /// <summary>The market route for <paramref name="qty"/> units of one item, as a line option shows it.</summary>
    private async Task<TradeMarginRouteOption> MarketOptionAsync(int id, int qty, CancellationToken ct)
    {
        var quote = await QuoteAsync(id, qty, ct);
        return new(TradeMaterialRoute.Market, quote.Unit * qty, ListingCount: quote.ListingCount,
            ListedQuantity: quote.Listed, RequiredQuantity: qty, ObservedAtUtc: quote.Oldest, UnitPrice: quote.Unit,
            WorldName: quote.World, DataCenter: quote.DataCenter);
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
            if (sources.TryGetValue(id, out var choice) && IsUsable(id, choice, craftable))
            {
                route = choice.Route;
                explicitChoices.Add(id);
            }
            else
            {
                var quote = await QuoteAsync(id, qty, ct);
                // Market only when listings alone cover the run; Market + vendor only when the
                // cheapest one-world buy really uses both sources.
                route = AcquisitionDefaultRule.Cheapest(
                [
                    (TradeMaterialRoute.Craft, AcquisitionDefaultRule.TieBreak(AcquisitionSource.Craft), craft),
                    (TradeMaterialRoute.Market, AcquisitionDefaultRule.TieBreak(AcquisitionSource.MarketBuyNq),
                        quote.Listed >= qty ? quote.Unit * qty : null),
                    (TradeMaterialRoute.MarketVendor, AcquisitionDefaultRule.MarketWithVendorTopUpTieBreak,
                        quote.Blend is { IsMixed: true } blend ? blend.Buy.Total : null),
                    (TradeMaterialRoute.Vendor, AcquisitionDefaultRule.TieBreak(AcquisitionSource.VendorBuy),
                        _vendors.TryGetValue(id, out var vendor) ? vendor * qty : null)
                ]) ?? (craftable ? TradeMaterialRoute.Craft : TradeMaterialRoute.Market);
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

    private bool IsUsable(int id, TradeMaterialSource choice, bool craftable) => choice.Route switch
    {
        TradeMaterialRoute.Craft => craftable,
        TradeMaterialRoute.SetPrice => choice.UnitPrice is >= 0,
        TradeMaterialRoute.Market or TradeMaterialRoute.Vendor => true,
        TradeMaterialRoute.MarketVendor => _vendors.ContainsKey(id),
        _ => false
    };

    private async Task<decimal?> BuyAsync(int id, TradeMaterialRoute route, int qty,
        IReadOnlyDictionary<int, TradeMaterialSource> sources, CancellationToken ct) => route switch
        {
            TradeMaterialRoute.Market => (await QuoteAsync(id, qty, ct)).Unit * qty,
            TradeMaterialRoute.Vendor => _vendors.TryGetValue(id, out var vendor) ? vendor * qty : null,
            TradeMaterialRoute.MarketVendor => (await QuoteAsync(id, qty, ct)).Blend?.Buy.Total,
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

        options.Add(await MarketOptionAsync(id, qty, ct));
        // Offered when the cheapest one-world buy needs both sources: when listings below the
        // vendor price cover the run it is a market-only buy, which Market already prices. A
        // company that chose it keeps it on every row, priced as its one-source degenerate.
        if ((await QuoteAsync(id, qty, ct)).Blend is { } blend &&
            (blend.IsMixed || routes.GetValueOrDefault(id) == TradeMaterialRoute.MarketVendor))
        {
            options.Add(new(TradeMaterialRoute.MarketVendor, blend.Buy.Total, RequiredQuantity: qty,
                ObservedAtUtc: blend.Buy.MarketQuantity > 0 ? blend.Oldest : null, UnitPrice: blend.Buy.UnitPrice,
                WorldName: blend.World, DataCenter: blend.DataCenter, VendorQuantity: blend.Buy.VendorQuantity));
        }

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

    /// <param name="Blend">The Market + vendor buy; null when no NPC vendor sells the material.</param>
    private sealed record MarketQuote(decimal? Unit, int Listed, int ListingCount, DateTime? Oldest, bool UnknownAge,
        string? World = null, string? DataCenter = null, VendorBlend? Blend = null);

    /// <summary>
    /// Listings below the vendor price on one world, cheapest first, and the rest from the vendor
    /// (<see cref="VendorCappedLadder"/>). With no listing below the vendor price anywhere it is
    /// the vendor alone. The age is the buy world's listings'.
    /// </summary>
    private sealed record VendorBlend(VendorCappedLadder.Buy Buy, string? World, string? DataCenter, DateTime? Oldest, bool UnknownAge)
    {
        /// <summary>Both sources are used, so it differs from a market-only and a vendor-only buy.</summary>
        public bool IsMixed => Buy.MarketQuantity > 0 && Buy.VendorQuantity > 0;
    }

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
        string? buyWorld = null, buyDataCenter = null;
        // Materials only: the sold item's own buy is priced elsewhere.
        var vendor = id != RootId && _vendors.TryGetValue(id, out var vendorPrice) && vendorPrice > 0 ? vendorPrice : (decimal?)null;
        var blend = vendor is { } vendorOnly
            ? new VendorBlend(new(0, quantity, 0, vendorOnly), null, null, null, false) : null;
        if (listings.Length > 0)
        {
            var analysis = (await new MarketPriceLadderAnalysisService().AnalyzeAsync(new MarketAnalysisRequest
            {
                Evidence = _evidence,
                Items = [new MaterialAggregate { ItemId = id, Name = _tree.Data[id].Name, TotalQuantity = quantity }]
            }, ct: ct)).Single(entry => entry.ItemId == id);
            // One analysis is tens of milliseconds of listing work; let the page render between them.
            await MainThreadYield.Async(ct);
            if (vendor is { } cap && VendorCappedLadder.BestWorld(analysis, quantity, cap) is { } capped)
            {
                var (blendOldest, blendUnknown) = Age(entries, capped.World.DataCenter, capped.World.WorldName);
                blend = new(capped.Buy, capped.World.WorldName, capped.World.DataCenter, blendOldest, blendUnknown);
            }

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
                    (buyWorld, buyDataCenter) = (selected.WorldName, selected.DataCenter);
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

        var quote = new MarketQuote(unit, listed, listings.Length, oldest, unknownAge, buyWorld, buyDataCenter, blend);
        _quotes[(id, quantity)] = quote;
        return quote;
    }

    /// <summary>The oldest upload among one world's listings, aged the way the market route ages its buy world.</summary>
    private static (DateTime? Oldest, bool UnknownAge) Age(IEnumerable<CachedMarketData> entries, string dataCenter, string worldName)
    {
        var stamps = entries.Where(entry => entry.DataCenter == dataCenter)
            .SelectMany(entry => entry.Worlds.Where(world => world.WorldName == worldName && world.Listings.Count > 0)
                .Select(world => world.LastUploadTimeUnixMilliseconds ?? entry.LastUploadTimeUnixMilliseconds))
            .ToArray();
        DateTime? oldest = null;
        var unknown = stamps.Length == 0;
        foreach (var stamp in stamps)
        {
            if (stamp is not > 0) { unknown = true; continue; }
            var time = DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime;
            oldest = oldest == null || time < oldest ? time : oldest;
        }
        return (oldest, unknown);
    }
}
