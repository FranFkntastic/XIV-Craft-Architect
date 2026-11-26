using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// The market Margins prices from: the company commission pricing scope, or one to four whole
/// regions. Only Margins reads this; commission pricing always uses the commission scope.
/// </summary>
public sealed class TradeMarginMarketScope : IEquatable<TradeMarginMarketScope>
{
    public const string ErrorCode = "invalid_margins_market_regions";

    private TradeMarginMarketScope(TradeCompanyPricingScope? commission, IReadOnlyList<string> regions,
        IReadOnlyList<string> dataCenters, MarketFetchScope fetchScope, string selectedDataCenter, string region)
    {
        Commission = commission;
        Regions = regions;
        DataCenters = dataCenters;
        FetchScope = fetchScope;
        SelectedDataCenter = selectedDataCenter;
        Region = region;
        Key = commission != null
            ? $"commission|{commission.Region}|{commission.DataCenter}|{(int)commission.MarketFetchScope}"
            : "regions|" + string.Join('+', regions);
    }

    /// <summary>The commission scope Margins follows; null when it prices from whole regions.</summary>
    public TradeCompanyPricingScope? Commission { get; }

    /// <summary>Whole regions in canonical order; empty when following the commission scope.</summary>
    public IReadOnlyList<string> Regions { get; }

    public IReadOnlyList<string> DataCenters { get; }
    internal MarketFetchScope FetchScope { get; }
    internal string SelectedDataCenter { get; }
    internal string Region { get; }

    /// <summary>Stable identity of the scope, for change detection and saved figures.</summary>
    public string Key { get; }

    /// <summary>
    /// The effective Margins scope. No regions follows <paramref name="commission"/> exactly;
    /// null when neither gives a usable scope.
    /// </summary>
    public static TradeMarginMarketScope? Resolve(TradeCompanyPricingScope? commission, IReadOnlyList<string>? regions)
    {
        var normalized = NormalizeRegions(regions);
        if (normalized.Count == 0)
        {
            return commission is { IsValid: true }
                ? new(commission, [],
                    MarketFetchScopeResolver.GetDataCenters(commission.MarketFetchScope, commission.EffectiveDataCenter, commission.Region),
                    commission.MarketFetchScope, commission.EffectiveDataCenter, commission.Region)
                : null;
        }

        var centers = normalized.SelectMany(MarketFetchScopeResolver.GetDataCentersForRegion).ToArray();
        return new(null, normalized, centers, MarketFetchScope.EntireRegion, centers[0], normalized[0]);
    }

    /// <summary>Known regions, distinct, in canonical order; unknown names are dropped.</summary>
    public static IReadOnlyList<string> NormalizeRegions(IEnumerable<string>? regions)
    {
        var wanted = (regions ?? []).Where(region => !string.IsNullOrWhiteSpace(region))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MarketFetchScopeResolver.GetRegions().Where(wanted.Contains).ToArray();
    }

    public static bool IsValid(IReadOnlyList<string>? regions, out string? error)
    {
        error = null;
        if (regions == null || regions.Count == 0)
        {
            return true;
        }

        var known = MarketFetchScopeResolver.GetRegions();
        if (regions.Count > known.Count ||
            regions.Any(region => !known.Contains(region, StringComparer.OrdinalIgnoreCase)) ||
            regions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count)
        {
            error = $"Margins can price from at most {known.Count} distinct known regions.";
            return false;
        }

        return true;
    }

    public bool Equals(TradeMarginMarketScope? other) => other is not null && Key == other.Key;
    public override bool Equals(object? obj) => Equals(obj as TradeMarginMarketScope);
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    public static bool operator ==(TradeMarginMarketScope? left, TradeMarginMarketScope? right) => Equals(left, right);
    public static bool operator !=(TradeMarginMarketScope? left, TradeMarginMarketScope? right) => !Equals(left, right);
}
