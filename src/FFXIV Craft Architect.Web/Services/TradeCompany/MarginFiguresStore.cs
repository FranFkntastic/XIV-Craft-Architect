using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

/// <summary>
/// The last figures Margins computed for each row, kept in this browser so the page can show
/// them the moment it opens. A display cache only: a record is shown only while the inputs it
/// was computed for still match, and nothing is ever priced from it. One record per company,
/// mode and item; writes are batched and never block or fail the page.
/// </summary>
public sealed class MarginFiguresStore(IndexedDbService indexedDb, ILogger<MarginFiguresStore> log)
{
    private const string Prefix = "margins.figures.";
    // v2: keyed by mode and carrying the root market buy; v1 records are dropped on read.
    // v3: carries the root item's NPC vendor price; v2 records are dropped on read.
    // v4: market buys are multi-world plans (worlds, vendor or commissioned rest) and the inputs
    // carry the material pricing policy; v3 records are dropped on read.
    private const int RecordVersion = 4;
    private readonly Dictionary<string, string> _pending = [];
    private readonly Dictionary<string, string> _written = [];
    private bool _flushing;
    private IReadOnlyList<TradeMaterialSource>? _hashedSources;
    private string _sourcesHash = "";

    /// <summary>Figures one row showed, with the inputs they were computed for.</summary>
    public sealed record SavedFigures(
        int Version, string Inputs, string Name, int IconId, decimal? Materials, int? Synths, int Units, int BatchSize,
        string[] Missing, string? Issue, long? OldestPriceUnixMs, bool UnknownPriceAge,
        SavedMarketBuy? MarketBuy = null, decimal? RootVendorPrice = null, SavedBuyPlan? LiveBuy = null)
    {
        public TradeMarginCost ToCost() => new(Name, IconId, Materials, Synths, FromUnixMs(OldestPriceUnixMs),
            Missing, Issue, Units, UnknownPriceAge)
        {
            BatchSize = BatchSize,
            MarketBuy = MarketBuy is { } buy
                ? new(TradeMaterialRoute.Market, buy.Total, ListedQuantity: buy.ListedQuantity,
                    RequiredQuantity: buy.RequiredQuantity, ObservedAtUtc: FromUnixMs(buy.ObservedAtUnixMs),
                    UnitPrice: buy.Total / Math.Max(1, buy.RequiredQuantity), Plan: buy.Plan?.ToPlan())
                : null,
            RootVendorPrice = RootVendorPrice is > 0 ? RootVendorPrice : null,
            LiveBuy = LiveBuy?.ToPlan()
        };
    }

    /// <summary>The root market route a row showed.</summary>
    public sealed record SavedMarketBuy(
        decimal? Total, int ListedQuantity, int RequiredQuantity, long? ObservedAtUnixMs, SavedBuyPlan? Plan)
    {
        public static SavedMarketBuy? From(TradeMarginRouteOption? option) => option == null ? null
            : new(option.Total, option.ListedQuantity, option.RequiredQuantity, ToUnixMs(option.ObservedAtUtc),
                SavedBuyPlan.From(option.Plan));
    }

    public sealed record SavedBuyWorld(string WorldName, string DataCenter, int Quantity, long? ObservedAtUnixMs);

    public sealed record SavedBuyPlan(
        int Quantity, int MarketUnits, decimal MarketCost, SavedBuyWorld[] Worlds,
        TradeMarginBuyRemainder Remainder, decimal? RemainderUnitPrice, bool UnknownAge)
    {
        public TradeMarginBuyPlan ToPlan() => new(Quantity, MarketUnits, MarketCost,
            Worlds.Select(world => new TradeMarginBuyWorld(world.WorldName, world.DataCenter, world.Quantity,
                FromUnixMs(world.ObservedAtUnixMs))).ToArray(),
            Enum.IsDefined(Remainder) ? Remainder : TradeMarginBuyRemainder.None, RemainderUnitPrice, UnknownAge);

        public static SavedBuyPlan? From(TradeMarginBuyPlan? plan) => plan == null ? null
            : new(plan.Quantity, plan.MarketUnits, plan.MarketCost,
                plan.Worlds.Select(world => new SavedBuyWorld(world.WorldName, world.DataCenter, world.Quantity,
                    ToUnixMs(world.ObservedAtUtc))).ToArray(),
                plan.Remainder, plan.RemainderUnitPrice, plan.UnknownAge);
    }

    private static DateTime? FromUnixMs(long? ms) =>
        ms is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime : null;

    private static long? ToUnixMs(DateTime? time) => time is { } value
        ? new DateTimeOffset(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
        : null;

    private static string CompanyPrefix(Guid companyId) => $"{Prefix}{companyId:D}.";

    private static string Key(Guid companyId, TradeMarginMode mode, int itemId) =>
        CompanyPrefix(companyId) + ((int)mode).ToString(CultureInfo.InvariantCulture) + "." +
        itemId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads <c>{mode}.{itemId}</c>; a v1 key (item id only) does not parse.</summary>
    private static bool TryParseKey(string suffix, out (int ItemId, TradeMarginMode Mode) row)
    {
        row = default;
        var dot = suffix.IndexOf('.');
        if (dot <= 0 ||
            !int.TryParse(suffix.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var mode) ||
            !Enum.IsDefined((TradeMarginMode)mode) ||
            !int.TryParse(suffix.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
        {
            return false;
        }

        row = (itemId, (TradeMarginMode)mode);
        return true;
    }

    /// <summary>
    /// Everything a row's figures depend on besides the market data: company, Margins market
    /// scope, item, batch size, labor rate, the company's material sources and the material
    /// pricing policy's route settings. A scope that follows the commission scope keeps the
    /// inputs it had before Margins had its own scope.
    /// </summary>
    public string Inputs(Guid companyId, TradeMarginMarketScope scope, int itemId, int batchSize, decimal laborRate,
        IReadOnlyList<TradeMaterialSource> sources, string routeKey) =>
        string.Join('|', companyId.ToString("D"), ScopeInputs(scope),
            itemId.ToString(CultureInfo.InvariantCulture), batchSize.ToString(CultureInfo.InvariantCulture),
            laborRate.ToString("0.##########", CultureInfo.InvariantCulture), SourcesHash(sources), routeKey);

    private static string ScopeInputs(TradeMarginMarketScope scope) => scope.Commission is { } commission
        ? string.Join('|', commission.Region, commission.DataCenter ?? "", ((int)commission.MarketFetchScope).ToString(CultureInfo.InvariantCulture))
        : "regions:" + string.Join('+', scope.Regions);

    private string SourcesHash(IReadOnlyList<TradeMaterialSource> sources)
    {
        if (ReferenceEquals(sources, _hashedSources))
        {
            return _sourcesHash;
        }
        // Same normalization as pricing: the last source per item wins.
        var text = string.Join(';', sources.Where(source => source != null).GroupBy(source => source.ItemId)
            .Select(group => group.Last()).OrderBy(source => source.ItemId)
            .Select(source => FormattableString.Invariant($"{source.ItemId}:{(int)source.Route}:{source.UnitPrice}")));
        _sourcesHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 12);
        _hashedSources = sources;
        return _sourcesHash;
    }

    /// <summary>
    /// Reads the company's saved figures in one read, by item and mode. Records for rows no
    /// longer on the page, and unreadable or old-shape records, are dropped. Never throws.
    /// </summary>
    public async Task<Dictionary<(int ItemId, TradeMarginMode Mode), SavedFigures>> LoadAsync(Guid companyId,
        IReadOnlyCollection<(int ItemId, TradeMarginMode Mode)> rows)
    {
        var figures = new Dictionary<(int ItemId, TradeMarginMode Mode), SavedFigures>();
        Dictionary<string, string> stored;
        try { stored = await indexedDb.LoadSettingsWithPrefixRequiredAsync(CompanyPrefix(companyId)); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Margins saved figures: could not read them for company {CompanyId}; rows price from stored market data instead", companyId);
            return figures;
        }

        var wanted = rows.ToHashSet();
        var discard = new List<string>();
        foreach (var (key, value) in stored)
        {
            if (!TryParseKey(key[CompanyPrefix(companyId).Length..], out var row) || !wanted.Contains(row))
            {
                discard.Add(key);
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<SavedFigures>(value) is { Version: RecordVersion, Inputs: not null, Name: not null, Missing: not null } record &&
                    record.Units >= 1 && record.BatchSize >= 1)
                {
                    figures[row] = record;
                    _written[key] = value;
                    continue;
                }
                log.LogWarning("Margins saved figures: ignored the record for item {ItemId} ({Mode}) of company {CompanyId} (old or incomplete shape)", row.ItemId, row.Mode, companyId);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                log.LogWarning("Margins saved figures: ignored the record for item {ItemId} ({Mode}) of company {CompanyId} ({Error})", row.ItemId, row.Mode, companyId, ex.Message);
            }
            discard.Add(key);
        }
        if (discard.Count > 0)
        {
            _ = DeleteKeysAsync(discard, $"{discard.Count} unused records of company {companyId:D}");
        }

        return figures;
    }

    /// <summary>Queues a row's displayed figures; written in one batch after the page next yields.</summary>
    public void Save(Guid companyId, TradeMarginMode mode, int itemId, string inputs, TradeMarginCost cost)
    {
        var key = Key(companyId, mode, itemId);
        var value = JsonSerializer.Serialize(new SavedFigures(RecordVersion, inputs, cost.Name, cost.IconId, cost.Materials,
            cost.Synths, cost.Units, cost.BatchSize, cost.Missing.ToArray(), cost.Issue, ToUnixMs(cost.OldestPriceUtc),
            cost.UnknownPriceAge, SavedMarketBuy.From(cost.MarketBuy), cost.RootVendorPrice, SavedBuyPlan.From(cost.LiveBuy)));
        if (_written.TryGetValue(key, out var written) && written == value)
        {
            _pending.Remove(key);
            return;
        }
        _pending[key] = value;
        if (_flushing)
        {
            return;
        }

        _flushing = true;
        _ = FlushAsync();
    }

    /// <summary>Forgets a removed row's figures.</summary>
    public void Delete(Guid companyId, TradeMarginMode mode, int itemId)
    {
        var key = Key(companyId, mode, itemId);
        _pending.Remove(key);
        _ = DeleteKeysAsync([key], $"item {itemId} ({mode}) of company {companyId:D}");
    }

    private async Task FlushAsync()
    {
        try
        {
            while (_pending.Count > 0)
            {
                await MainThreadYield.Async();
                var batch = new Dictionary<string, string>(_pending);
                _pending.Clear();
                try
                {
                    if (!await indexedDb.SaveSettingsBatchRequiredAsync(batch))
                    {
                        throw new InvalidOperationException("browser storage reported the write as not saved");
                    }

                    foreach (var (key, value) in batch)
                    {
                        _written[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Margins saved figures: could not save {Count} rows; the next open prices them from stored market data", batch.Count);
                }
            }
        }
        finally { _flushing = false; }
    }

    private async Task DeleteKeysAsync(IReadOnlyCollection<string> keys, string what)
    {
        foreach (var key in keys)
        {
            _written.Remove(key);
        }

        try { await indexedDb.DeleteSettingsRequiredAsync(keys); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Margins saved figures: could not delete saved figures for {What}; they reappear only if the item is added back with the same inputs", what);
        }
    }
}
