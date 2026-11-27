using System.Globalization;

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
    /// The market route for the whole run of the item itself, as a material line prices it;
    /// short when the listings cannot cover <see cref="Units"/> within the world-stop limit.
    /// Only for rows prepared with the root market included.
    /// </summary>
    public TradeMarginRouteOption? MarketBuy { get; init; }

    /// <summary>The sold item's own cheapest NPC vendor gil price per unit; null when no vendor sells it.</summary>
    public decimal? RootVendorPrice { get; init; }

    /// <summary>
    /// Buying the whole run of the item itself live: the cheapest of the vendor, the market and
    /// listings below the vendor price topped up from the vendor; with no vendor and too few
    /// listings, the listings below the commission cost with the rest commissioned. Null when
    /// nothing can be bought; only for rows prepared with the root market included.
    /// </summary>
    public TradeMarginBuyPlan? LiveBuy { get; init; }

    public bool Equals(TradeMarginCost? other) =>
        other is not null && Name == other.Name && IconId == other.IconId && Materials == other.Materials &&
        Synths == other.Synths && OldestPriceUtc == other.OldestPriceUtc && Missing.SequenceEqual(other.Missing) &&
        Issue == other.Issue && Units == other.Units && UnknownPriceAge == other.UnknownPriceAge &&
        Equals(MarketBuy, other.MarketBuy) && RootVendorPrice == other.RootVendorPrice && Equals(LiveBuy, other.LiveBuy);

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(Name, IconId, Materials, Synths, OldestPriceUtc, Issue, Units, UnknownPriceAge),
            MarketBuy, RootVendorPrice, LiveBuy);
}

/// <summary>
/// Company inputs to a Margins price: labor rate, explicit material sources, and the material
/// pricing policy whose world-stop limit and split purchases shape every market route.
/// </summary>
public sealed class TradeMarginPricing
{
    public static TradeMarginPricing None { get; } = new(0, []);

    public TradeMarginPricing(decimal laborGilPerSynth, IEnumerable<TradeMaterialSource> sources,
        TradeMaterialPricingPolicy? policy = null)
    {
        LaborGilPerSynth = Math.Max(0, laborGilPerSynth);
        Sources = sources.Where(source => source != null).GroupBy(source => source.ItemId)
            .ToDictionary(group => group.Key, group => group.Last());
        Policy = TradeMaterialPricingPolicyNormalizer.Normalize(policy);
        RouteKey = RouteKeyOf(Policy);
    }

    public decimal LaborGilPerSynth { get; }
    public IReadOnlyDictionary<int, TradeMaterialSource> Sources { get; }
    public TradeMaterialPricingPolicy Policy { get; }

    /// <summary>The policy settings a market route depends on, for change detection and saved figures.</summary>
    public string RouteKey { get; }

    public static string RouteKeyOf(TradeMaterialPricingPolicy? policy)
    {
        var value = TradeMaterialPricingPolicyNormalizer.Normalize(policy);
        return string.Create(CultureInfo.InvariantCulture,
            $"{value.MaximumWorldStops}|{value.AllowSplitPurchases}|{value.MaximumConsolidationPremiumPercent}");
    }
}

/// <summary>What is left after the listings a buy plan takes.</summary>
public enum TradeMarginBuyRemainder
{
    None = 0,
    Vendor = 1,
    Commission = 2
}

/// <summary>Units a buy plan takes from one world, with that world's listing upload time.</summary>
public sealed record TradeMarginBuyWorld(string WorldName, string DataCenter, int Quantity, DateTime? ObservedAtUtc)
{
    public string Label => string.IsNullOrWhiteSpace(DataCenter) ? WorldName : $"{WorldName} ({DataCenter})";
}

/// <summary>
/// How <see cref="Quantity"/> units are bought: whole listings on one or more worlds, the most
/// units first, each costing its bought listings' average unit price per unit used, and any rest
/// from the NPC vendor or commissioned at <see cref="RemainderUnitPrice"/>.
/// </summary>
public sealed record TradeMarginBuyPlan(
    int Quantity, int MarketUnits, decimal MarketCost, IReadOnlyList<TradeMarginBuyWorld> Worlds,
    TradeMarginBuyRemainder Remainder = TradeMarginBuyRemainder.None, decimal? RemainderUnitPrice = null,
    bool UnknownAge = false)
{
    public int RemainderUnits => Remainder == TradeMarginBuyRemainder.None ? 0 : Math.Max(0, Quantity - MarketUnits);

    public decimal Total => MarketCost + RemainderUnits * (RemainderUnitPrice ?? 0);

    /// <summary>The oldest contributing world's upload time; the vendor or commissioned rest has no age.</summary>
    public DateTime? ObservedAtUtc => UnknownAge ? null : Worlds.Min(world => world.ObservedAtUtc);

    internal static TradeMarginBuyPlan FromRemainder(int quantity, TradeMarginBuyRemainder remainder, decimal unitPrice) =>
        new(quantity, 0, 0, [], remainder, unitPrice);

    public bool Equals(TradeMarginBuyPlan? other) =>
        other is not null && Quantity == other.Quantity && MarketUnits == other.MarketUnits &&
        MarketCost == other.MarketCost && Worlds.SequenceEqual(other.Worlds) && Remainder == other.Remainder &&
        RemainderUnitPrice == other.RemainderUnitPrice && UnknownAge == other.UnknownAge;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Quantity);
        hash.Add(MarketUnits);
        hash.Add(MarketCost);
        foreach (var world in Worlds)
        {
            hash.Add(world);
        }
        hash.Add(Remainder);
        hash.Add(RemainderUnitPrice);
        hash.Add(UnknownAge);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One route for a material line, priced for the line's whole quantity. A market route without
/// a <see cref="Plan"/> could not be bought within the world-stop limit; its total is directional.
/// </summary>
public sealed record TradeMarginRouteOption(
    TradeMaterialRoute Route, decimal? Total, decimal? Materials = null, int Synths = 0,
    int ListedQuantity = 0, int RequiredQuantity = 0, DateTime? ObservedAtUtc = null,
    decimal? UnitPrice = null, IReadOnlyList<string>? Missing = null, TradeMarginBuyPlan? Plan = null)
{
    public bool IsShortOfListings => Route == TradeMaterialRoute.Market && Plan == null;
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
            return await new TradeMarginBasis(tree, evidence, vendorPrices, new MarketShoppingService(market), batch, includeRootMarket).PriceAsync(pricing, token);
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
    private readonly MarketShoppingService _shopping;
    private readonly Walked _walked;
    private readonly int _units;
    private readonly bool _includeRootMarket;

    /// <summary>Routes already walked, shared by every batch size priced from this evidence.</summary>
    private sealed class Walked
    {
        public Dictionary<(int ItemId, int Quantity, string Policy), MarketRoute> Markets { get; } = new();
        public Dictionary<(int Quantity, string Policy, decimal Ceiling), TradeMarginBuyPlan?> CommissionFills { get; } = new();
    }

    internal TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors,
        MarketShoppingService shopping, int batchSize, bool includeRootMarket = false)
        : this(tree, evidence, vendors, shopping, batchSize, new(), includeRootMarket)
    {
    }

    private TradeMarginBasis(MarginTree tree, MarketEvidenceSet evidence, IReadOnlyDictionary<int, decimal> vendors,
        MarketShoppingService shopping, int batchSize, Walked walked, bool includeRootMarket)
    {
        _tree = tree;
        _evidence = evidence;
        _vendors = vendors;
        _shopping = shopping;
        _walked = walked;
        _includeRootMarket = includeRootMarket;
        BatchSize = MarginTree.ClampBatch(batchSize);
        _units = tree.UnitsFor(BatchSize);
    }

    /// <summary>Units sold from one production run; every quantity is evaluated for the whole run.</summary>
    public int BatchSize { get; }

    /// <summary>The same evidence priced for another batch size; routes already walked are shared.</summary>
    internal TradeMarginBasis ForBatch(int batchSize) =>
        MarginTree.ClampBatch(batchSize) == BatchSize ? this
            : new(_tree, _evidence, _vendors, _shopping, batchSize, _walked, _includeRootMarket);

    private int RootId => _tree.RootId;

    internal async Task<TradeMarginCost> PriceAsync(TradeMarginPricing pricing, CancellationToken ct)
    {
        var solved = await SolveAsync(pricing.Sources, pricing, ct);
        DateTime? oldest = null;
        var unknownAge = false;
        foreach (var (id, cost) in solved.Result.BoughtCost)
        {
            var route = solved.Routes[id];
            if (cost == null || route is not (TradeMaterialRoute.Market or TradeMaterialRoute.MarketVendor))
            {
                continue;
            }

            var market = await MarketAsync(id, solved.Result.Demand[id], pricing, ct);
            var (time, unknown) = route == TradeMaterialRoute.Market
                ? (market.Oldest, market.UnknownAge)
                : market.VendorBlend is { } blend ? (blend.ObservedAtUtc, blend.UnknownAge) : (null, false);
            unknownAge |= unknown;
            if (time is { } observed)
            {
                oldest = oldest == null || observed < oldest ? observed : oldest;
            }
        }

        var rootVendor = _vendors.TryGetValue(RootId, out var vendor) && vendor > 0 ? vendor : (decimal?)null;
        TradeMarginRouteOption? marketBuy = null;
        TradeMarginBuyPlan? liveBuy = null;
        if (_includeRootMarket)
        {
            var market = await MarketAsync(RootId, _units, pricing, ct);
            marketBuy = MarketOption(market, _units);
            liveBuy = RootBuy(market, rootVendor, Total(solved.Result, pricing.LaborGilPerSynth), pricing);
        }

        return new TradeMarginCost(_tree.Root.Name, _tree.Root.IconId, solved.Result.Materials, solved.Result.Synths,
            unknownAge ? null : oldest, solved.Result.Missing, Units: _units, UnknownPriceAge: unknownAge)
        {
            Basis = this,
            BatchSize = BatchSize,
            MarketBuy = marketBuy,
            LiveBuy = liveBuy,
            RootVendorPrice = rootVendor
        };
    }

    /// <summary>
    /// Buying the whole run of the sold item live: the cheapest of the vendor, the market route,
    /// and listings below the vendor price with the rest from the vendor. Without a vendor, a
    /// market that cannot cover the run within the world limit buys its listings below the
    /// commission cost and commissions the rest, so those units save nothing. Null when there is
    /// no vendor and nothing is listed.
    /// </summary>
    private TradeMarginBuyPlan? RootBuy(MarketRoute market, decimal? vendor, decimal? commission, TradeMarginPricing pricing)
    {
        if (vendor is { } price)
        {
            var vendorOnly = TradeMarginBuyPlan.FromRemainder(_units, TradeMarginBuyRemainder.Vendor, price);
            var blend = Offered(market);
            return AcquisitionDefaultRule.Cheapest<TradeMaterialRoute>(
            [
                (TradeMaterialRoute.Vendor, AcquisitionDefaultRule.TieBreak(AcquisitionSource.VendorBuy), vendorOnly.Total),
                (TradeMaterialRoute.Market, AcquisitionDefaultRule.TieBreak(AcquisitionSource.MarketBuyNq), market.Plan?.Total),
                (TradeMaterialRoute.MarketVendor, AcquisitionDefaultRule.MarketWithVendorTopUpTieBreak, blend?.Total)
            ]) switch
            {
                TradeMaterialRoute.Market => market.Plan,
                TradeMaterialRoute.MarketVendor => blend,
                _ => vendorOnly
            };
        }

        if (market.Plan != null)
        {
            return market.Plan;
        }

        // The commission cost is the ceiling, not min(vendor, commission): with no vendor the
        // units the listings cannot cover are commissioned, and a listing above the commission
        // cost would only buy what commissioning makes for less.
        if (market.Listed == 0 || market.Projected is not { } plan || commission is not > 0)
        {
            return null;
        }

        var ceiling = commission.Value / _units;
        var key = (_units, pricing.RouteKey, ceiling);
        if (!_walked.CommissionFills.TryGetValue(key, out var fill))
        {
            fill = Fill(plan, pricing.Policy, ceiling, TradeMarginBuyRemainder.Commission, market.Ages);
            _walked.CommissionFills[key] = fill;
        }
        return fill ?? TradeMarginBuyPlan.FromRemainder(_units, TradeMarginBuyRemainder.Commission, ceiling);
    }

    /// <summary>The market route for <paramref name="qty"/> units of one item, as a line option shows it.</summary>
    private static TradeMarginRouteOption MarketOption(MarketRoute market, int qty)
    {
        var total = market.Plan?.Total ?? market.DirectionalUnit * qty;
        return new(TradeMaterialRoute.Market, total, ListedQuantity: market.Listed, RequiredQuantity: qty,
            ObservedAtUtc: market.Oldest, UnitPrice: total / qty, Plan: market.Plan);
    }

    /// <summary>Listings below the vendor price with the rest from the vendor, when that uses both sources.</summary>
    private static TradeMarginBuyPlan? Offered(MarketRoute market) =>
        market.VendorBlend is { MarketUnits: > 0, RemainderUnits: > 0 } blend ? blend : null;

    internal async Task<TradeMarginBreakdown> DescribeAsync(TradeMarginPricing pricing, CancellationToken ct)
    {
        var rate = pricing.LaborGilPerSynth;
        var solved = await SolveAsync(pricing.Sources, pricing, ct);
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

            var options = await OptionsAsync(id, qty, solved.Routes, pricing, ct);
            var chosen = options.FirstOrDefault(option => option.Route == route);
            var isExplicit = solved.Explicit.Contains(id);
            decimal? delta = null;
            if (isExplicit && total is { } withChoice)
            {
                var without = pricing.Sources.Where(pair => pair.Key != id).ToDictionary();
                if (Total((await SolveAsync(without, pricing, ct)).Result, rate) is { } baseline && baseline != withChoice)
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
    /// Each bought line is its own commission route: up to the company's world-stop limit for
    /// that item alone, so line prices stay independent of one another.
    /// </summary>
    private async Task<Solved> SolveAsync(IReadOnlyDictionary<int, TradeMaterialSource> sources, TradeMarginPricing pricing,
        CancellationToken ct)
    {
        var rate = pricing.LaborGilPerSynth;
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
            if (sources.TryGetValue(id, out var choice) && IsUsable(choice, craftable, _vendors.ContainsKey(id)))
            {
                route = choice.Route;
                explicitChoices.Add(id);
            }
            else
            {
                var market = await MarketAsync(id, qty, pricing, ct);
                route = AcquisitionDefaultRule.Cheapest<TradeMaterialRoute>(
                [
                    (TradeMaterialRoute.Craft, AcquisitionDefaultRule.TieBreak(AcquisitionSource.Craft), craft),
                    (TradeMaterialRoute.Market, AcquisitionDefaultRule.TieBreak(AcquisitionSource.MarketBuyNq), market.Plan?.Total),
                    (TradeMaterialRoute.MarketVendor, AcquisitionDefaultRule.MarketWithVendorTopUpTieBreak, Offered(market)?.Total),
                    (TradeMaterialRoute.Vendor, AcquisitionDefaultRule.TieBreak(AcquisitionSource.VendorBuy),
                        _vendors.TryGetValue(id, out var vendor) ? vendor * qty : null)
                ]) ?? (craftable ? TradeMaterialRoute.Craft : TradeMaterialRoute.Market);
            }

            routes[id] = route;
            unit[id] = route == TradeMaterialRoute.Craft ? craft / qty : await BuyAsync(id, route, qty, sources, pricing, ct) / qty;
        }

        // Every bought line is priced before propagation so Propagate stays synchronous.
        var actual = _tree.Propagate(RootId, _units, id => routes.GetValueOrDefault(id, TradeMaterialRoute.Market));
        var prices = new Dictionary<int, decimal?>();
        foreach (var (id, qty) in actual.Demand)
        {
            if (id != RootId && qty > 0 && !(routes[id] == TradeMaterialRoute.Craft && _tree.Edges.ContainsKey(id)))
            {
                prices[id] = await BuyAsync(id, routes[id], qty, sources, pricing, ct);
            }
        }
        var result = _tree.Propagate(RootId, _units, id => routes.GetValueOrDefault(id, TradeMaterialRoute.Market),
            (id, _, _) => prices.GetValueOrDefault(id));
        return new(result, routes, explicitChoices);
    }

    private static bool IsUsable(TradeMaterialSource choice, bool craftable, bool hasVendor) => choice.Route switch
    {
        TradeMaterialRoute.Craft => craftable,
        TradeMaterialRoute.SetPrice => choice.UnitPrice is >= 0,
        TradeMaterialRoute.MarketVendor => hasVendor,
        TradeMaterialRoute.Market or TradeMaterialRoute.Vendor => true,
        _ => false
    };

    private async Task<decimal?> BuyAsync(int id, TradeMaterialRoute route, int qty,
        IReadOnlyDictionary<int, TradeMaterialSource> sources, TradeMarginPricing pricing, CancellationToken ct) => route switch
        {
            TradeMaterialRoute.Market => MarketOption(await MarketAsync(id, qty, pricing, ct), qty).Total,
            // Kept when the listings no longer beat the vendor: then it is simply the vendor.
            TradeMaterialRoute.MarketVendor => _vendors.TryGetValue(id, out var vendor)
                ? (await MarketAsync(id, qty, pricing, ct)).VendorBlend?.Total ?? vendor * qty : null,
            TradeMaterialRoute.Vendor => _vendors.TryGetValue(id, out var vendor) ? vendor * qty : null,
            TradeMaterialRoute.SetPrice => sources.GetValueOrDefault(id)?.UnitPrice * (decimal)qty,
            _ => null
        };

    private async Task<IReadOnlyList<TradeMarginRouteOption>> OptionsAsync(int id, int qty,
        IReadOnlyDictionary<int, TradeMaterialRoute> routes, TradeMarginPricing pricing, CancellationToken ct)
    {
        var sources = pricing.Sources;
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
                    prices[child] = await BuyAsync(child, routes[child], childQty, sources, pricing, ct);
                }
            }
            var craft = _tree.Propagate(id, qty, child => routes.GetValueOrDefault(child, TradeMaterialRoute.Market),
                (child, _, _) => prices.GetValueOrDefault(child));
            options.Add(new(TradeMaterialRoute.Craft, Total(craft, pricing.LaborGilPerSynth), craft.Materials, craft.Synths,
                Missing: craft.Missing));
        }

        var market = await MarketAsync(id, qty, pricing, ct);
        options.Add(MarketOption(market, qty));
        if (_vendors.TryGetValue(id, out var vendor))
        {
            if (Offered(market) is { } blend)
            {
                options.Add(new(TradeMaterialRoute.MarketVendor, blend.Total, UnitPrice: blend.Total / qty,
                    ObservedAtUtc: blend.ObservedAtUtc, Plan: blend));
            }
            else if (routes.GetValueOrDefault(id) == TradeMaterialRoute.MarketVendor)
            {
                var kept = market.VendorBlend ?? TradeMarginBuyPlan.FromRemainder(qty, TradeMarginBuyRemainder.Vendor, vendor);
                options.Add(new(TradeMaterialRoute.MarketVendor, kept.Total, UnitPrice: kept.Total / qty,
                    ObservedAtUtc: kept.ObservedAtUtc, Plan: kept));
            }

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

    /// <summary>
    /// One item's market at one quantity. <see cref="Plan"/> is the commission route when it
    /// covers the quantity within the world-stop limit; otherwise <see cref="DirectionalUnit"/>
    /// keeps a figure for a material with no other route, which is never chosen as a default
    /// over another priced route. <see cref="VendorBlend"/> buys the listings below the vendor
    /// price and the rest from the vendor. <see cref="Projected"/> is kept for the sold item
    /// only, whose commission-capped fill moves with the labor rate.
    /// </summary>
    private sealed record MarketRoute(
        TradeMarginBuyPlan? Plan, decimal? DirectionalUnit, int Listed, DateTime? Oldest, bool UnknownAge,
        TradeMarginBuyPlan? VendorBlend, DetailedShoppingPlan? Projected, IReadOnlyDictionary<string, DateTime?> Ages);

    private static string WorldKey(string dataCenter, string world) => $"{dataCenter}|{world}";

    /// <summary>
    /// The market route for <paramref name="quantity"/> units, priced exactly as a commission
    /// quote prices one material: the same eligible listings and the same procurement route,
    /// up to the company's world-stop limit for this item, split purchases as the company allows.
    /// </summary>
    private async Task<MarketRoute> MarketAsync(int id, int quantity, TradeMarginPricing pricing, CancellationToken ct)
    {
        var key = (id, quantity, pricing.RouteKey);
        if (_walked.Markets.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var policy = pricing.Policy;
        var entries = _evidence.GetEntriesForItem(id);
        var ages = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        var perWorld = new List<long>();
        foreach (var entry in entries)
        {
            foreach (var world in entry.Worlds)
            {
                var stocked = world.Listings.Where(listing => listing.Quantity > 0).Sum(listing => (long)listing.Quantity);
                if (stocked <= 0)
                {
                    continue;
                }

                perWorld.Add(stocked);
                var stamp = world.LastUploadTimeUnixMilliseconds ?? entry.LastUploadTimeUnixMilliseconds;
                ages[WorldKey(entry.DataCenter, world.WorldName)] =
                    stamp is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(stamp.Value).UtcDateTime : null;
            }
        }

        // What one route could buy: every world when purchases may split, else the best one.
        var listed = (int)Math.Min(int.MaxValue, policy.AllowSplitPurchases ? perWorld.Sum() : perWorld.DefaultIfEmpty().Max());
        MarketRoute route;
        if (perWorld.Count == 0)
        {
            route = new(null, null, 0, null, false, null, null, ages);
        }
        else
        {
            var name = _tree.Data[id].Name;
            var ladder = new MarketPriceLadderAnalysisService();
            var analysis = (await ladder.AnalyzeAsync(new MarketAnalysisRequest
            {
                Evidence = _evidence,
                Items = [new MaterialAggregate { ItemId = id, Name = name, TotalQuantity = quantity }]
            }, ct: ct)).Single(entry => entry.ItemId == id);
            var plan = await ladder.ProjectToShoppingPlanAsync(analysis, MarketAcquisitionLens.MinimumUpfrontCost, ct: ct);
            // One analysis and one route search are tens of milliseconds; let the page render between them.
            await MainThreadYield.Async(ct);
            var quote = await new TradeMaterialQuoteService().PriceAsync(_shopping, [plan],
                [new MaterialAggregate { ItemId = id, Name = name, TotalQuantity = quantity }],
                new Dictionary<(int ItemId, bool RequiresHq), AcquisitionSource> { [(id, false)] = AcquisitionSource.MarketBuyNq },
                policy, DateTime.UtcNow, [], ct: ct);
            await MainThreadYield.Async(ct);

            TradeMarginBuyPlan? covering = null;
            // The commission quote treats the stop limit as advisory and may return a wider
            // route with a warning; Margins buys within the limit or reports the market short.
            if (quote is { IsComplete: true, Quote.WorldStops: var stops } && stops <= policy.MaximumWorldStops &&
                quote.RoutePlans.FirstOrDefault(candidate => candidate.ItemId == id) is { } routePlan)
            {
                var purchases = TradeMaterialQuoteService.ReadRoutePurchase(routePlan, quantity);
                if (purchases.Sum(purchase => purchase.QuantityUsed) >= quantity)
                {
                    covering = BuildPlan(quantity,
                        purchases.Select(purchase => (purchase.DataCenter, purchase.WorldName, purchase.QuantityUsed,
                            purchase.QuantityPurchased, purchase.Cash)),
                        ages, TradeMarginBuyRemainder.None, null);
                }
            }

            DateTime? oldest;
            bool unknownAge;
            decimal? directional = null;
            if (covering != null)
            {
                (oldest, unknownAge) = (covering.ObservedAtUtc, covering.UnknownAge);
            }
            else
            {
                directional = CommissionCostBasisResolver.GetReferenceUnitCost(analysis) is > 0 and var unit ? unit : null;
                unknownAge = ages.Values.Any(age => age == null);
                oldest = unknownAge ? null : ages.Values.Min();
            }

            var vendorBlend = _vendors.TryGetValue(id, out var vendor)
                ? Fill(plan, policy, vendor, TradeMarginBuyRemainder.Vendor, ages)
                : null;
            route = new(covering, directional, listed, oldest, unknownAge, vendorBlend, id == RootId ? plan : null, ages);
        }

        _walked.Markets[key] = route;
        return route;
    }

    /// <summary>
    /// Listings priced below <paramref name="ceiling"/> within the world-stop limit, the rest
    /// from <paramref name="remainder"/> at the ceiling; null when no listing is below it.
    /// </summary>
    private static TradeMarginBuyPlan? Fill(DetailedShoppingPlan plan, TradeMaterialPricingPolicy policy, decimal ceiling,
        TradeMarginBuyRemainder remainder, IReadOnlyDictionary<string, DateTime?> ages)
    {
        var fill = MarketCoverageBuilder.BuildCappedFill(plan,
            policy.AllowSplitPurchases ? policy.MaximumWorldStops : 1, ceiling);
        return fill == null ? null : BuildPlan(plan.QuantityNeeded,
            fill.Worlds.Select(world => (world.DataCenter, world.WorldName, world.QuantityCovered,
                world.QuantityToPurchase, world.CashOutCost)),
            ages, remainder, ceiling);
    }

    private static TradeMarginBuyPlan BuildPlan(int quantity,
        IEnumerable<(string DataCenter, string WorldName, int Used, int Purchased, decimal Cash)> purchases,
        IReadOnlyDictionary<string, DateTime?> ages, TradeMarginBuyRemainder remainder, decimal? remainderUnitPrice)
    {
        var worlds = new List<TradeMarginBuyWorld>();
        decimal cost = 0;
        var units = 0;
        var unknownAge = false;
        foreach (var world in purchases.Where(purchase => purchase.Used > 0).GroupBy(purchase => (purchase.DataCenter, purchase.WorldName)))
        {
            // Margins is per unit sold across repeated production, so a purchase costs what each
            // unit in the bought listings costs. Charging the whole listing to the units used
            // makes a 999-stack of shards the price of five.
            cost += world.Sum(purchase => purchase.Purchased > 0 ? purchase.Used * purchase.Cash / purchase.Purchased : purchase.Cash);
            var used = world.Sum(purchase => purchase.Used);
            units += used;
            var age = ages.GetValueOrDefault(WorldKey(world.Key.DataCenter, world.Key.WorldName));
            unknownAge |= age == null;
            worlds.Add(new(world.Key.WorldName, world.Key.DataCenter, used, age));
        }

        return new(quantity, Math.Min(units, quantity), cost,
            worlds.OrderByDescending(world => world.Quantity)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase).ToArray(),
            remainder, remainder == TradeMarginBuyRemainder.None ? null : remainderUnitPrice, unknownAge);
    }
}
