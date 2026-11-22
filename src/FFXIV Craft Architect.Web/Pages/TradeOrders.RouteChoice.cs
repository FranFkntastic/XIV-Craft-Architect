using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Shared;

namespace FFXIV_Craft_Architect.Web.Pages;

// Route picker, override badge, precraft labor evidence and output buy costs for the
// commission supply plan. Values come from the live plan projection; nothing here prices.
public partial class TradeOrders
{
    private const string UnavailableAmount = "—";

    private WorkerAcquisitionRowProjection? GetLiveAcquisitionRow(TradeOrderProcurementRow row)
    {
        return row.IsLiveAcquisitionRow
            ? GetVisibleLiveProcurementSnapshot()?.AcquisitionRows.FirstOrDefault(candidate =>
                candidate.ItemId == row.ItemId)
            : null;
    }

    private IReadOnlyList<ListboxPickerItem> GetProcurementRouteItems(TradeOrderProcurementRow row)
    {
        var live = GetLiveAcquisitionRow(row);
        return GetProcurementSourceOptions(row)
            .Select(source => CreateProcurementRouteItem(
                row,
                source,
                live?.Options.FirstOrDefault(option => option.Source == source)))
            .ToArray();
    }

    private ListboxPickerItem CreateProcurementRouteItem(
        TradeOrderProcurementRow row,
        AcquisitionSource source,
        WorkerAcquisitionOptionProjection? option)
    {
        var value = source.ToString();
        var name = RecipePlanDisplayHelpers.GetSourceDisplayName(source);
        var amount = option?.TotalCost is { } total ? FormatGilShort(total) : UnavailableAmount;
        if (source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq)
        {
            if (option is { IsShortOfListings: true })
            {
                return new ListboxPickerItem(
                    value,
                    name,
                    $"⚠ Only {option.ListedQuantity:N0} of {option.RequiredQuantity:N0} listed",
                    UnavailableAmount,
                    Disabled: true,
                    IsWarning: true);
            }

            if (option is { IsAvailable: false })
            {
                return new ListboxPickerItem(
                    value,
                    name,
                    option.ListingCount == 0 ? "⚠ None listed" : "⚠ Not fillable in pricing scope",
                    UnavailableAmount,
                    Disabled: true,
                    IsWarning: true);
            }

            return new ListboxPickerItem(
                value,
                name,
                option is { ListingCount: > 0 } ? FormatListingBasis(option.ListingCount, option.ObservedAtUtc) : null,
                amount);
        }

        if (source == AcquisitionSource.Craft)
        {
            var detail = option is { MaterialCost: { } materials, LaborCost: { } labor }
                ? $"{FormatGilShort(materials)} materials + {FormatGilShort(labor)} labor ({option.SynthCount:N0} synths)"
                : null;
            return new ListboxPickerItem(
                value,
                name,
                detail,
                amount,
                Disabled: row.Responsibility == CommissionMaterialResponsibility.Provided);
        }

        return new ListboxPickerItem(
            value,
            name,
            Amount: amount,
            Disabled: option is { IsAvailable: false });
    }

    /// <summary>
    /// Payment change of the operator's route against the route automatic defaults would pick,
    /// or null when the row is on its default or either route has no total.
    /// </summary>
    private decimal? GetProcurementRouteOverrideDelta(TradeOrderProcurementRow row)
    {
        var live = GetLiveAcquisitionRow(row);
        if (live is not { DefaultSource: { } defaultSource, SourceReason: AcquisitionSourceReason.UserSelected } ||
            live.Source == defaultSource ||
            live.Source != row.Source)
        {
            return null;
        }

        var chosen = live.Options.FirstOrDefault(option => option.Source == live.Source)?.TotalCost;
        var baseline = live.Options.FirstOrDefault(option => option.Source == defaultSource)?.TotalCost;
        if (chosen is null || baseline is null)
        {
            return null;
        }

        var delta = Math.Round(chosen.Value - baseline.Value, MidpointRounding.AwayFromZero);
        return delta == 0 ? null : delta;
    }

    private string? FormatPrecraftLaborEvidence(TradeOrderProcurementRow row)
    {
        if (!IsSupplyPrecraftRow(row) ||
            IsRequestedOutputRow(row) ||
            row.Source != AcquisitionSource.Craft ||
            !string.IsNullOrWhiteSpace(row.WarningSummary) ||
            (row.HasSuppressedOccurrences && !row.IsFullySuppressed))
        {
            return null;
        }

        var live = GetLiveAcquisitionRow(row);
        return live is { LaborGilPerSynth: > 0, CraftSynthCount: > 0 }
            ? $"{live.CraftSynthCount:N0} synths | {FormatGilShort(live.CraftSynthCount * live.LaborGilPerSynth)} labor"
            : null;
    }

    private IReadOnlyList<WorkerOutputPurchaseQuote> GetOutputPurchaseQuotes()
    {
        return GetVisibleLiveProcurementSnapshot()?.OutputPurchaseQuotes ?? [];
    }

    private string FormatOutputPurchaseContext(IReadOnlyList<WorkerOutputPurchaseQuote> quotes)
    {
        var observedAtUtc = quotes
            .Select(quote => quote.ObservedAtUtc)
            .Where(observed => observed.HasValue)
            .Min();
        return JoinContext(
            _selectedOrder is null ? null : FormatProcurementScopeLabel(_selectedOrder),
            FormatObservationAge(observedAtUtc));
    }

    private string FormatListingBasis(int listingCount, DateTime? observedAtUtc)
    {
        return JoinContext(
            $"{listingCount:N0} listings",
            _selectedOrder is null ? null : FormatProcurementScopeLabel(_selectedOrder),
            FormatObservationAge(observedAtUtc));
    }

    private static string? FormatObservationAge(DateTime? observedAtUtc)
    {
        return observedAtUtc is { } observed
            ? CacheTimeHelper.FormatAgeShort(DateTime.UtcNow - DateTime.SpecifyKind(observed, DateTimeKind.Utc))
            : null;
    }

    private static string JoinContext(params string?[] parts)
    {
        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatGilShort(decimal value)
    {
        return $"{Math.Ceiling(value):N0} gil";
    }

    private static string FormatSignedGilShort(decimal value)
    {
        return value > 0
            ? $"+{FormatGilShort(value)}"
            : $"−{FormatGilShort(Math.Abs(value))}";
    }
}
