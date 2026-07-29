using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.BrowserPersistence;

public static class PortableOperatorSettingKeys
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            "market.default_datacenter",
            "market.region",
            "market.comparison_region",
            "market.home_world",
            "market.default_search_scope",
            "market.include_cross_world",
            "market.exclude_congested_worlds",
            "market.search_entire_region",
            "market.analysis_evidence_overlay",
            "procurement.search_entire_region",
            "procurement.region",
            "procurement.enable_split_world_purchases",
            "procurement.travel_tolerance",
            "procurement.world_exclusion_duration_minutes",
            "procurement.start_from_home_data_center",
            "procurement.travel_priority",
            "ui.accent_color",
            "ui.use_split_pane_market_view",
            "planning.default_recommendation_mode"
        ],
        StringComparer.Ordinal);

    public static bool IsPortable(string key) => All.Contains(key);
}

public sealed class PortableOperatorSettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public CompanyId CompanyId { get; set; }
    public Guid GrantId { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists non-secret operator preferences in a company/grant scope and stages the
/// corresponding canonical mutation. Secrets, connection configuration, debug state,
/// and in-flight workflow state remain in browser-local settings.
/// </summary>
public sealed class PortableOperatorSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _jsRuntime;
    private readonly TradeCompanyBrowserPersistence _companyPersistence;
    private readonly DurableTradeCompanyClient? _durableClient;

    public PortableOperatorSettingsStore(
        IJSRuntime jsRuntime,
        TradeCompanyBrowserPersistence companyPersistence)
        : this(jsRuntime, companyPersistence, durableClient: null)
    {
    }

    public PortableOperatorSettingsStore(
        IJSRuntime jsRuntime,
        TradeCompanyBrowserPersistence companyPersistence,
        DurableTradeCompanyClient? durableClient)
    {
        _jsRuntime = jsRuntime;
        _companyPersistence = companyPersistence;
        _durableClient = durableClient;
    }

    public async Task<PortableOperatorSettingsDocument> MigrateLegacyAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var document = await _jsRuntime.InvokeAsync<PortableOperatorSettingsDocument>(
            "IndexedDB.migratePortableOperatorSettings",
            cancellationToken,
            access.CompanyId.ToString(),
            access.GrantId.ToString("D"),
            PortableOperatorSettingKeys.All.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        await SaveDocumentAsync(
            access,
            document,
            $"portable-settings-migration-v1:{access.GrantId:D}",
            cancellationToken);
        return document;
    }

    public async Task<PortableOperatorSettingsDocument> LoadAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        return await _jsRuntime.InvokeAsync<PortableOperatorSettingsDocument>(
            "IndexedDB.loadPortableOperatorSettings",
            cancellationToken,
            access.CompanyId.ToString(),
            access.GrantId.ToString("D"));
    }

    public async Task<PortableOperatorSettingsDocument?> HydrateCanonicalAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessScope(access);
        var record = await _companyPersistence.LoadRecordAsync(
            access.CompanyId,
            TradeCompanyRecordKinds.OperatorSettings,
            $"operator:{access.GrantId:D}",
            cancellationToken);
        if (record == null || record.Deleted)
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<PortableOperatorSettingsDocument>(
            record.PayloadJson,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "The canonical portable settings document is empty.");
        ValidateDocumentScope(access, document);
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.hydratePortableOperatorSettings",
            cancellationToken,
            document,
            PortableOperatorSettingKeys.All.OrderBy(
                key => key,
                StringComparer.Ordinal).ToArray());
        return document;
    }

    public async Task<T?> GetAsync<T>(
        TradeCompanyAccessContext access,
        string key,
        T? defaultValue = default,
        CancellationToken cancellationToken = default)
    {
        EnsurePortableKey(key);
        var document = await LoadAsync(access, cancellationToken);
        if (!document.Settings.TryGetValue(key, out var serialized))
        {
            return defaultValue;
        }
        return JsonSerializer.Deserialize<T>(serialized, JsonOptions);
    }

    public async Task SetAsync<T>(
        TradeCompanyAccessContext access,
        string key,
        T value,
        CancellationToken cancellationToken = default)
    {
        EnsurePortableKey(key);
        var document = await LoadAsync(access, cancellationToken);
        document.Settings[key] = JsonSerializer.Serialize(value, JsonOptions);
        await SaveDocumentAsync(
            access,
            document,
            $"portable-settings:{Guid.NewGuid():D}",
            cancellationToken);
    }

    private async Task SaveDocumentAsync(
        TradeCompanyAccessContext access,
        PortableOperatorSettingsDocument document,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateDocumentScope(access, document);
        var identity = await _companyPersistence.LoadIdentityAsync(
            access.CompanyId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Company {access.CompanyId} must be cached before portable settings can be written.");
        var recordId = $"operator:{access.GrantId:D}";
        var current = await _companyPersistence.LoadRecordAsync(
            access.CompanyId,
            TradeCompanyRecordKinds.OperatorSettings,
            recordId,
            cancellationToken);
        var payload = JsonSerializer.Serialize(document, JsonOptions);
        var mutation = new TradeCompanyMutationRequest(
            access.CompanyId,
            TradeCompanyRecordKinds.OperatorSettings,
            recordId,
            payload,
            current?.RecordRevision ?? CompanyRecordRevision.None,
            identity.Revision,
            idempotencyKey);
        if (!await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.savePortableOperatorSettings",
            cancellationToken,
            document,
            mutation))
        {
            throw new InvalidOperationException(
                "The browser could not stage portable settings for the Trade Company.");
        }

        if (_durableClient == null)
        {
            return;
        }

        var completed = await _durableClient.ReplayPendingAsync(
            access.CompanyId,
            idempotencyKey,
            cancellationToken);
        if (completed is not { Success: true })
        {
            throw new InvalidOperationException(
                completed?.ErrorMessage ??
                "Portable settings remain queued for company synchronization.");
        }
    }

    private static void ValidateAccess(TradeCompanyAccessContext access)
    {
        ValidateAccessScope(access);
        if (access.Role is TradeCompanyRole.ReadOnly)
        {
            throw new InvalidOperationException(
                "Portable operator settings require an operator or owner grant.");
        }
    }

    private static void ValidateAccessScope(TradeCompanyAccessContext access)
    {
        if (access.GrantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Portable operator settings require a non-empty grant ID.",
                nameof(access));
        }
    }

    private static void ValidateDocumentScope(
        TradeCompanyAccessContext access,
        PortableOperatorSettingsDocument document)
    {
        if (document.SchemaVersion != PortableOperatorSettingsDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Portable settings schema v{document.SchemaVersion} is incompatible with " +
                $"v{PortableOperatorSettingsDocument.CurrentSchemaVersion}.");
        }
        if (document.CompanyId != access.CompanyId || document.GrantId != access.GrantId)
        {
            throw new InvalidOperationException(
                "Portable settings document scope does not match the active company operator.");
        }
        var browserLocalKey = document.Settings.Keys.FirstOrDefault(
            key => !PortableOperatorSettingKeys.IsPortable(key));
        if (browserLocalKey is not null)
        {
            throw new InvalidOperationException(
                $"Browser-local setting '{browserLocalKey}' cannot enter portable company storage.");
        }
    }

    private static void EnsurePortableKey(string key)
    {
        if (!PortableOperatorSettingKeys.IsPortable(key))
        {
            throw new InvalidOperationException(
                $"Setting '{key}' is browser-local and cannot enter portable company storage.");
        }
    }
}
