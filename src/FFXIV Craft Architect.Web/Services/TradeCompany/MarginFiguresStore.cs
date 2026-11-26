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
/// was computed for still match, and nothing is ever priced from it. One record per company
/// and item; writes are batched and never block or fail the page.
/// </summary>
public sealed class MarginFiguresStore(IndexedDbService indexedDb, ILogger<MarginFiguresStore> log)
{
    private const string Prefix = "margins.figures.";
    private const int RecordVersion = 1;
    private readonly Dictionary<string, string> _pending = [];
    private readonly Dictionary<string, string> _written = [];
    private bool _flushing;
    private IReadOnlyList<TradeMaterialSource>? _hashedSources;
    private string _sourcesHash = "";

    /// <summary>Figures one row showed, with the inputs they were computed for.</summary>
    public sealed record SavedFigures(
        int Version, string Inputs, string Name, int IconId, decimal? Materials, int? Synths, int Units, int BatchSize,
        string[] Missing, string? Issue, long? OldestPriceUnixMs, bool UnknownPriceAge)
    {
        public TradeMarginCost ToCost() => new(Name, IconId, Materials, Synths,
            OldestPriceUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
            Missing, Issue, Units, UnknownPriceAge)
        { BatchSize = BatchSize };
    }

    private static string CompanyPrefix(Guid companyId) => $"{Prefix}{companyId:D}.";
    private static string Key(Guid companyId, int itemId) => CompanyPrefix(companyId) + itemId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Everything a row's figures depend on besides the market data: company, Margins market
    /// scope, item, batch size, labor rate and the company's material sources. A scope that
    /// follows the commission scope keeps the inputs it had before Margins had its own scope.
    /// </summary>
    public string Inputs(Guid companyId, TradeMarginMarketScope scope, int itemId, int batchSize, decimal laborRate,
        IReadOnlyList<TradeMaterialSource> sources) =>
        string.Join('|', companyId.ToString("D"), ScopeInputs(scope),
            itemId.ToString(CultureInfo.InvariantCulture), batchSize.ToString(CultureInfo.InvariantCulture),
            laborRate.ToString("0.##########", CultureInfo.InvariantCulture), SourcesHash(sources));

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
    /// Reads the company's saved figures in one read, by item. Records for items no longer on
    /// the page, and unreadable or old-shape records, are dropped. Never throws.
    /// </summary>
    public async Task<Dictionary<int, SavedFigures>> LoadAsync(Guid companyId, IReadOnlyCollection<int> itemIds)
    {
        var figures = new Dictionary<int, SavedFigures>();
        Dictionary<string, string> stored;
        try { stored = await indexedDb.LoadSettingsWithPrefixRequiredAsync(CompanyPrefix(companyId)); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Margins saved figures: could not read them for company {CompanyId}; rows price from stored market data instead", companyId);
            return figures;
        }

        var wanted = itemIds.ToHashSet();
        var discard = new List<string>();
        foreach (var (key, value) in stored)
        {
            if (!int.TryParse(key.AsSpan(CompanyPrefix(companyId).Length), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) ||
                !wanted.Contains(itemId))
            {
                discard.Add(key);
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<SavedFigures>(value) is { Version: RecordVersion, Inputs: not null, Name: not null, Missing: not null } record &&
                    record.Units >= 1 && record.BatchSize >= 1)
                {
                    figures[itemId] = record;
                    _written[key] = value;
                    continue;
                }
                log.LogWarning("Margins saved figures: ignored the record for item {ItemId} of company {CompanyId} (old or incomplete shape)", itemId, companyId);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                log.LogWarning("Margins saved figures: ignored the record for item {ItemId} of company {CompanyId} ({Error})", itemId, companyId, ex.Message);
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
    public void Save(Guid companyId, int itemId, string inputs, TradeMarginCost cost)
    {
        var key = Key(companyId, itemId);
        var value = JsonSerializer.Serialize(new SavedFigures(RecordVersion, inputs, cost.Name, cost.IconId, cost.Materials,
            cost.Synths, cost.Units, cost.BatchSize, cost.Missing.ToArray(), cost.Issue,
            cost.OldestPriceUtc is { } oldest ? new DateTimeOffset(oldest.Kind == DateTimeKind.Local ? oldest.ToUniversalTime() : DateTime.SpecifyKind(oldest, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null,
            cost.UnknownPriceAge));
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
    public void Delete(Guid companyId, int itemId)
    {
        var key = Key(companyId, itemId);
        _pending.Remove(key);
        _ = DeleteKeysAsync([key], $"item {itemId} of company {companyId:D}");
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
